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

已完成实验性风扇曲线命令：

- `startCurve`
- `curveStatus`
- `stopCurve`

当前策略：

- 按 `max(trustedCpuTemp, trustedGpuTemp)` 选择温度源。
- 默认使用 `2C` 迟滞，降低降温抖动。
- 曲线后台循环能按可信温度应用 level。
- `curveStatus` 已返回：
  - `hysteresisC`
  - `tickCount`
  - `applyCount`
  - `lastTemperature`
  - `lastTemperatureSource`
  - `lastApplied`
  - `lastError`

## 7. 已完成：自动化 diagnostics runner

已完成：

1. 在 `diagnostics/` 下新增现代验证 CLI / test runner，专门面向 `omenctl`。
2. runner 自动拉起 `omenctl` 子进程，通过 JSON over stdio 协议发送命令并记录响应。
3. 输出格式已统一为：
   - 原始 `jsonl` 采样日志
   - 控制台 summary

已实现命令：

- `snapshot-log`
- `apply-readback-batch`
- `curve-watch`

`apply-readback-batch` 已覆盖：

- `setManual 35/35`
- `setManual 45/45`
- `setManual 50/50`
- `setMax`

`curve-watch` 已记录：

- `lastTemperature`
- `lastTemperatureSource`
- `lastApplied`
- `tickCount`
- `applyCount`
- 是否出现 `lastError`

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

## 10. 当前遗留问题

这些内容尚未进入高优先级实现：

- 现代 GUI 尚未完成。
- 风扇曲线点位还需要根据长期 diagnostics 数据调优。
- `Power/Silent` 的最终语义尚未确定。
- 键盘灯功能暂不做。
- GPU power UI 暂不做。
- 新 HP 机型适配暂不做。
- 更完整的打包、签名、开机自启、托盘常驻仍待后续阶段规划。
