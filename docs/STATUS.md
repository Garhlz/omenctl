# omenctl 完成情况与诊断结论

本文档用于归档已经完成的工作、8BAB 设备诊断结论和当前实现状态。`TODO.md` 只保留后续开发路径，避免被历史完成项淹没。

## 1. 当前项目状态

`omenctl` 已从旧 OmenMon WinForms 项目中拆出，并转向现代本地 Agent 架构。

已完成：

- 新增 `omenctl.sln`。
- 新增 `.NET 10` 项目：
  - `src/omenctl.Core`
  - `src/omenctl.Sensors`
  - `src/omenctl.Agent`
- `make build` 已切到现代 Agent 构建。
- 旧 WinForms GUI 已删除。
- 旧 CLI 已删除。
- 旧 `.NET Framework 4.8` 项目文件已删除。
- 旧 UI 图片、字体、图标资源已删除。
- 保留 `Resources/Driver.sys.gz` 与必要 GPL 来源说明。
- 对外名称、文档和构建产物已迁移到 `omenctl`。

## 2. 架构拆分

当前仓库结构已经形成：

```text
omenctl.sln
src/
  omenctl.Core/      核心协议、设备画像、快照模型、控制接口
  omenctl.Sensors/   传感器 provider 层，接 LHM / NVML
  omenctl.Agent/     JSON over stdio 本地 Agent
docs/
  requirement.md     需求文档
  TODO.md            后续开发路径
  STATUS.md          已完成工作与诊断结论
diagnostics/
  8BAB/              历史诊断结果
  omenctl.Diagnostics/ 自动化验证 CLI / test runner
```

核心原则已经稳定：

- 风扇控制复用 OmenMon 已验证有效的 BIOS / EC 路径。
- 温度、使用率、功耗、频率等监控数据使用 LibreHardwareMonitor / NVML。
- GUI 和 diagnostics 只通过 Agent 协议通信，不直接操作 BIOS / EC。

## 3. 已完成：真实风扇控制

代码路径已接入并在 8BAB 上完成基础验证。

已完成：

- 抽出最小真实 `OmenFanController`。
- BIOS fan level / max / mode 走 HP BIOS WMI。
- EC rate / rpm / raw / manual / countdown 走 WinRing0 I/O port 路径。
- 不再依赖 WinForms、旧 CLI、`App.Exit` 或弹窗错误处理。
- BIOS COM/WMI 能读写 fan level / max / fan mode。
- EC 驱动能读 raw EC 字段。
- 所有 Agent 写入通过串行 gate，避免并发写 EC / BIOS。

已验证有效：

- `setManual 35/35`
- `setManual 45/45`
- `setManual 50/50`
- `setMax`

保留判断：

- `Power/Silent` 当前只是实验性 BIOS fan mode 快捷映射。
- 旧 OmenMon 的完整 Program 语义应由新的 fan curve 替代。

## 4. 已完成：Agent 协议骨架

Agent 已支持 JSON over stdio 的基本调度骨架。

当前已支持命令：

```json
{"cmd":"snapshot"}
{"cmd":"setAuto","biosMode":"Default"}
{"cmd":"setMax"}
{"cmd":"setManual","cpuLevel":45,"gpuLevel":45}
{"cmd":"setProgram","name":"Power"}
{"cmd":"applyAndReadback","command":{"cmd":"setManual","cpuLevel":45,"gpuLevel":45},"delays":[0,1,3,5,15]}
{"cmd":"startCurve","intervalSeconds":5,"hysteresisC":2,"points":[{"temp":45,"cpuLevel":35,"gpuLevel":35},{"temp":55,"cpuLevel":45,"gpuLevel":45},{"temp":65,"cpuLevel":50,"gpuLevel":50}]}
{"cmd":"curveStatus"}
{"cmd":"stopCurve"}
```

已完成行为：

- `snapshot` 可返回合法 JSON。
- 非管理员运行时：
  - `snapshot` 返回降级 JSON。
  - `setManual` / `setMax` / `setProgram` 返回 `hardware_access_denied`。
- 写入类命令返回值已统一为融合后的现代快照，不再混入仅 BIOS/EC 视角的原始温度结果。
- 出错时返回 JSON error，不弹窗、不退出整个进程。

## 5. 已完成：传感器接入与融合

已完成：

1. 在 `omenctl.Sensors` 接入 LibreHardwareMonitor。
2. 已读取：
   - CPU package temperature
   - CPU load
   - GPU temperature
   - GPU load
   - SSD temperature
3. 已接入 NVML。
4. 已优先使用 NVML 读取 NVIDIA GPU：
   - temperature
   - utilization
   - memory usage
   - power
   - clocks
5. 已建立 `SensorFusionService`：
   - CPU 温度优先 LHM，失败再 BIOS fallback。
   - GPU 温度优先 NVML，其次 LHM。
   - OmenMon `GPTM<=5C` 标记为 suspect。
   - `RPM=0 && level>0` 标记为 `rpm_unavailable`。

当前结论：

- OmenMon 负责风扇控制。
- 传感器监控交给 LibreHardwareMonitor / NVML。
- OmenMon raw 字段保留用于诊断，但不作为主展示数据来源。

## 6. 已完成：风扇曲线

实验性风扇曲线已实现并持续迭代优化。

曲线命令：`startCurve` / `curveStatus` / `stopCurve`。

2026-06-14 重大升级——PID 插值 + 独立分扇：

- **线性插值代替阶梯跳变**：风扇 level 是温度的连续函数，在锚点之间平滑过渡。升温时即时响应，降温时通过 `2°C` 迟滞防抖，大幅减少温度波动。
- **CPU/GPU 独立控制**：CPU 风扇跟随 CPU 温度（LHM），GPU 风扇跟随 GPU 温度（NVML），各自独立查曲线。单方传感器失效时自动用另一方 fallback。
- **每 tick 必写入**：EC 有倒计时计数器，长时间不写入会退回自动模式。曲线改为每 tick 无条件写入，确保控制不丢失。
- **LHM 瞬断重试**：传感器读取可能因并发访问失败，首次失败后等 1 秒重试一次，减少误回退 BIOS 温度。
- **顶层 crash guard**：worker 线程最外层 `try/catch` 确保任何未捕获异常都会将曲线标记为停止并记录错误，不会静默死亡。
- **writeGate 串行化**：曲线 worker 写入前获取 `hardwareGate`，与 GUI 手动命令互斥，防止并发写 EC。

`curveStatus` 返回：
- `running`, `points`, `intervalSeconds`, `hysteresisC`
- `lastTemperature`, `lastTemperatureSource`（格式：`"CPU: LHM (89°C → 70), GPU: NVML (59°C → 45)"`）
- `lastApplied`, `lastError`, `tickCount`, `applyCount`, `timestamp`

## 7. 已完成：自动化 diagnostics runner

已完成：

1. 在 `diagnostics/` 下新增现代验证 CLI / test runner，专门面向 `omenctl`。
2. runner 自动拉起 `omenctl` 子进程，通过 JSON over stdio 协议发送命令并记录响应。
3. 输出格式已统一为：
   - 原始 `jsonl` 采样日志
   - 控制台 summary

2026-06-14 升级：

- `apply-readback-batch` summary 增加 `requestedLevel`、`passed` 判定、`warnings`。
- `curve-watch` 增加 `sourceFlaps`（温度源切换次数）、`applyChanges`（应用变更次数）。
- 新增 `smoke` 命令：一键串联 `snapshot-log` → `apply-readback-batch` → `curve-watch`。
- 曲线点统一引用 `DeviceProfiles.EightBabCurve`，不再独立维护。

## 8. 8BAB 设备诊断结论

针对当前 HP Omen 16 / 8BAB，已有诊断结果表明：

### 8.1 可靠项

- fan level 控制有效。
- fan level 可作为主要反馈。
- `setManual 35/35`、`45/45`、`50/50` 能写入并读回。
- `setMax` 能拉高 fan level。
- 风扇曲线后台循环能按可信温度应用 level。

### 8.2 不可靠项

- OmenMon 原始 RPM 读数不可信：
  - CPU/GPU RPM 长期为 `0`
  - 但 fan level/rate 非零
- OmenMon 原始温度寄存器不完全可信：
  - `CPUT=0` 时需要 BIOS fallback 或外部传感器来源
  - `GPTM=1C` 不应作为 GPU 温度

### 8.3 当前设备画像

```text
fanLevelReliable = true
fanControlReliable = true
fanRpmReliable = false
omenGpuTempReliable = false
omenFanRateInterpretation = raw
rawFanMode = 0x44
```

UI 和 Agent 都应遵守：

- fan level 是主要反馈。
- RPM 默认不可用。
- rate 只作为 raw 字段展示，不解释为真实速度。
- GPTM 不作为 GPU 温度。
- CPU 温度可用 BIOS fallback，但更推荐 LHM。

## 9. 已完成：Agent 协议稳定化

2026-06-13 完成协议稳定化，主要包括：

1. `AgentResponse` 增加 `protocolVersion`（当前 `"1.0"`）和 `agentVersion`（如 `"1.0.0"`）字段，所有响应均包含。
2. 程序集版本通过 `omenctl.Agent.csproj` 的 `<Version>1.0.0.0</Version>` 管理。
3. `FanCurveStatus` 全部属性增加显式 `[JsonPropertyName]`，与项目中其他 record 保持一致（输出字段名不变）。
4. 新建 [`docs/protocol.md`](protocol.md)，包含：
   - 传输层描述
   - 请求/响应通用格式
   - `HardwareSnapshot` / `SensorValue` / `FanSnapshot` / `DeviceProfile` 完整 JSON schema
   - `FanCurveStatus` / `FanCurvePoint` schema
   - `applyAndReadback` 数组格式
   - 9 个命令速查表
   - 8 个错误码目录
   - 协议版本策略与向后兼容声明
5. `docs/requirement.md` 补充 `protocolVersion`/`agentVersion` 要求、完整错误码列表、新增 6.3 版本要求节。
6. `README.md` 增加 `docs/protocol.md` 导航链接。
7. 向后兼容验证通过：diagnostics runner（`snapshot-log` / `apply-readback-batch` / `curve-watch`）均正常运行，未知 JSON 字段自动忽略。

结论：

- GUI 现在可依据 `docs/protocol.md` 完成 mock client，不再需要频繁修改命令名和核心字段名。
- 协议已有明确的版本策略：字段可增加不可改名，主版本号标记破坏性变更。

## 10. 已完成：最小 GUI 原型

2026-06-13 完成 Tauri v2 + Svelte + Tailwind 的 GUI 最小原型：

1. Rust 后端：`agent_manager.rs` 管理子进程（代数计数器防竞态、错误状态恢复、Scoop dotnet 发现）；`commands.rs` 暴露 4 个 Tauri IPC 命令。
2. Svelte 前端 9 个组件：AgentClient（递归 setTimeout 防堆积）、MonitorCharts（CPU/GPU 双图）、SensorCard（温度颜色插值）、FanPanel、ControlButtons（曲线互斥确认）、CurvePanel（PID 曲线控制 + 状态展示）、WarningBanner、RawJsonPanel。
3. 3 秒轮询 snapshot + curveStatus，写入后即时刷新，写入期间按钮禁用。
4. `make.cmd` 增加 `gui-run`、`gui-build`、`gui-publish-agent`。
5. LHM 新增系统内存；Chart.js + date-fns 实时温度/fan level 图表。

## 11. 已完成：风扇曲线 GUI + PID 控制

2026-06-14 完成：

- **CurvePanel**：曲线启动/停止/状态展示，柱状预览曲线点，CPU/GPU 分显温度→目标 level。
- **MonitorCharts**：Chart.js 实时折线图，CPU 和 GPU 各自一图（温度 + fan level），始终可见。
- **ControlButtons**：曲线运行时 Manual/Max 弹确认→自动 stopCurve→再写入手动值，stopCurve 失败不再盲发。
- **Agent 层升级**：PID 线性插值、独立分扇、writeGate 串行化、LHM 瞬断重试、worker crash guard。
- **code review 修复**：agent_manager 竞态、setInterval 堆积、死代码清理、默认点同步。

## 12. 已完成：托盘 + 命令管道修复

2026-06-15 完成：

- 系统托盘：动态 tooltip（CPU/GPU 温度 + 风扇模式 + level）、Open Dashboard、多档 Manual、Start/Stop Curve、Max Fan、Open Logs、Copy JSON、Exit（确认保护）。
- 移除 Power/Silent（BIOS fan mode 已被曲线替代）。
- 命令管道：全局 command_lock 事务串行化、timeout 后 kill agent 自愈、quit_app 正确退出。
- 配置校验：版本号、level 0-64 范围、曲线升序检查，损坏自动复位。

## 13. 当前遗留问题

- 打包发布（self-contained zip）。
- 曲线编辑器（拖拽调点、hysteresis/interval 调节、preset 导入导出）。
- 控制日志（记录写入/读回/曲线 tick，用于复盘风扇行为）。
- 键盘灯功能暂不做。
- GPU power UI 暂不做。
- 新 HP 机型适配暂不做。
