# TODO：Omen Agent 现代化路线

## 目标

把当前项目整理成面向 HP Omen 16 / 8BAB 的现代本地 Agent。

核心原则：

- 风扇控制复用 OmenMon 已验证有效的 BIOS / EC 路径。
- 温度、使用率、功耗、频率等监控数据使用 LibreHardwareMonitor / NVML。
- UI 暂时不重写，先把 Agent 和数据可信度做好。
- 旧 WinForms / CLI / .NET Framework 代码已清理，不再作为维护目标。

## 当前状态

已完成：

- 新增 `OmenMon.Modern.sln`。
- 新增 `.NET 10` 项目：
  - `src/OmenMon.Core`
  - `src/OmenMon.Sensors`
  - `src/OmenMon.Agent`
- `make build` 已切到现代 Agent 构建。
- Agent 已支持 JSON over stdio 的基本调度骨架。
- `snapshot` 可返回合法 JSON。
- 已抽出最小真实 `OmenFanController`：
  - BIOS fan level / max / mode 走 HP BIOS WMI。
  - EC rate / rpm / raw / manual / countdown 走 WinRing0 I/O port 路径。
  - 不依赖 WinForms、旧 CLI、`App.Exit` 或弹窗错误处理。
- 已新增实验性风扇曲线命令：
  - `startCurve`
  - `curveStatus`
  - `stopCurve`
  - 当前只使用 snapshot 中可信的摄氏温度源，后续应切到 LHM / NVML。
- 非管理员运行时：
  - `snapshot` 返回降级 JSON。
  - `setManual` / `setMax` / `setProgram` 返回 `hardware_access_denied`。
- 8BAB 诊断确认：
  - fan level 控制有效
  - `setManual 45/45` 和 `35/35` 能读回
  - `setMax` 能拉高 fan level
  - 风扇曲线后台循环能按可信温度应用 level
  - RPM 不可信
  - GPTM GPU 温度不可信
  - CPU 温度需要 BIOS fallback 或外部传感器来源
- 已清理旧 OmenMon WinForms / CLI / .NET Framework 项目和旧 UI 资源。

## 已完成阶段：真实风扇控制

代码路径已接入并在 8BAB 上完成基础验证。

已完成：

- BIOS COM/WMI 能读写 fan level / max / fan mode。
- EC 驱动能读 raw EC 字段。
- `setManual 45/45`、`setManual 35/35` 和 `setMax` 已通过 `applyAndReadback` 验证。
- 所有 Agent 写入通过串行 gate，避免并发写 EC / BIOS。
- `Power/Silent` 暂时保留为实验性 BIOS fan mode 快捷映射；旧 OmenMon 的完整 Program 语义应由新的 fan curve 替代。

验收标准：

- `{"cmd":"snapshot"}` 能读到 product、fan level、BIOS fan level。
- `{"cmd":"setManual","cpuLevel":45,"gpuLevel":45}` 能实际写入并读回。
- `applyAndReadback` 能记录 `0s/1s/3s/5s/15s` 的读回结果。
- 出错时只返回 JSON error，不弹窗、不退出整个进程。

## 下一阶段：接入传感器数据

目标是让 OmenMon 不再负责不可信的温度/RPM 读取。

要做：

1. 在 `OmenMon.Sensors` 接入 LibreHardwareMonitor。
2. 读取：
   - CPU package temperature
   - CPU load
   - GPU temperature
   - GPU load
   - SSD temperature
3. 接入 NVML。
4. 优先使用 NVML 读取 NVIDIA GPU：
   - temperature
   - utilization
   - memory usage
   - power
   - clocks
5. 建立 `SensorFusionService`：
   - CPU 温度优先 LHM，失败再 BIOS fallback。
   - GPU 温度优先 NVML，其次 LHM。
   - OmenMon `GPTM<=5C` 标记为 suspect。
   - `RPM=0 && level>0` 标记为 `rpm_unavailable`。
6. 将风扇曲线温度源从 BIOS fallback 切到 LHM / NVML。

验收标准：

- `snapshot.temps.cpu` 有可信来源。
- `snapshot.loads.cpu` 有可信来源。
- `snapshot.temps.gpu` 优先来自 NVML 或 LHM。
- 不再把 `GPTM=1C` 展示为正常 GPU 温度。

## Agent 协议

输入：每行一个 JSON 命令。

```json
{"cmd":"snapshot"}
{"cmd":"setAuto","biosMode":"Default"}
{"cmd":"setMax"}
{"cmd":"setManual","cpuLevel":45,"gpuLevel":45}
{"cmd":"setProgram","name":"Power"}
{"cmd":"applyAndReadback","command":{"cmd":"setManual","cpuLevel":45,"gpuLevel":45},"delays":[0,1,3,5,15]}
{"cmd":"startCurve","intervalSeconds":5,"points":[{"temp":45,"cpuLevel":35,"gpuLevel":35},{"temp":55,"cpuLevel":45,"gpuLevel":45},{"temp":65,"cpuLevel":50,"gpuLevel":50}]}
{"cmd":"curveStatus"}
{"cmd":"stopCurve"}
```

输出：每行一个 JSON 响应。

```json
{"ok":true,"data":{}}
{"ok":false,"error":{"code":"not_implemented","message":"..."}}
```

统一 snapshot 至少包含：

- `product`
- `deviceProfile`
- `temps`
- `loads`
- `fans`
- `biosFan`
- `warnings`
- `raw`

## 8BAB 设备规则

当前固定规则：

- `fanLevelReliable = true`
- `fanControlReliable = true`
- `fanRpmReliable = false`
- `omenGpuTempReliable = false`
- `omenFanRateInterpretation = raw`
- `rawFanMode = 0x44`

UI 和 Agent 都应遵守：

- fan level 是主要反馈。
- RPM 默认不可用。
- rate 只作为 raw 字段展示，不解释为真实速度。
- GPTM 不作为 GPU 温度。
- CPU 温度可用 BIOS fallback，但更推荐 LHM。

## 构建与运行

现代主线：

```powershell
.\make.cmd build
.\make.cmd agent-run
```

注意：

- 当前 `make.cmd` 优先使用 Scoop 安装的 `.NET 10 SDK`。
- 如果 `dotnet` 路径异常，先检查：

```powershell
dotnet --list-sdks
```

## 暂不做

- 暂不重写 Tauri/Web UI。
- 暂不扫描新的 HP EC 温度寄存器。
- 暂不实现键盘灯功能。
- 暂不处理 GPU power UI。

## 已完成清理

- 旧 WinForms GUI 已删除。
- 旧 CLI 已删除。
- 旧 .NET Framework 项目文件已删除。
- 旧 UI 图片、字体、图标资源已删除。
- 保留 `Resources/Driver.sys.gz` 和必要的来源说明。
