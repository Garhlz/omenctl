# omenctl

这是一个面向 **HP Omen 16 / 8BAB** 的硬件控制与监控实验项目。

项目从开源 [OmenMon](https://omenmon.github.io/) 代码演化而来，但目标已经不再是维护原版 WinForms GUI，而是一个更清晰的现代本地控制 Agent：`omenctl`。

- 用 OmenMon 中已验证可用的 HP BIOS / EC 风扇控制能力。
- 用 LibreHardwareMonitor / NVML 等通用来源读取温度、使用率、功耗、频率等传感器数据。
- 用 `.NET 10` 编写一个本地 Agent，对未来的 Tauri/Web UI 暴露稳定 JSON 协议。

## 当前结论

针对这台 `8BAB` 设备，已有诊断结果表明：

- 风扇 level 控制有效：
  - `Manual 35/35`
  - `Manual 45/45`
  - `Manual 50/50`
  - `Max`
- 实验性风扇曲线可运行：
  - `startCurve`
  - `curveStatus`
  - `stopCurve`
- `Program Power/Silent` 当前只是 BIOS fan mode 快捷映射，不等同于旧 OmenMon 的完整温控程序。
- OmenMon 原始 RPM 读数不可信：
  - CPU/GPU RPM 长期为 `0`
  - 但 fan level/rate 非零
- OmenMon 原始温度寄存器不完全可信：
  - `CPUT=0` 时需要 BIOS fallback
  - `GPTM=1C` 不应作为 GPU 温度
- 因此，新架构中：
  - OmenMon 负责风扇控制
  - 传感器监控交给 LibreHardwareMonitor / NVML

## 项目结构

```text
omenctl.sln
src/
  omenctl.Core/      核心协议、设备画像、快照模型、控制接口
  omenctl.Sensors/   传感器 provider 层，接 LHM / NVML
  omenctl.Agent/     JSON over stdio 本地 Agent
docs/
  TODO.md            当前开发计划
  requirement.md     当前阶段需求约束
diagnostics/
  8BAB/              历史诊断结果
  omenctl.Diagnostics/ 自动化验证 CLI / test runner
```

旧版 `.NET Framework 4.8` / WinForms / CLI 代码已经清理。当前仓库只保留现代 Agent 主线、必要的 GPL 来源说明、诊断记录和 WinRing0 驱动资源。

## 构建

需要 `.NET 10 SDK`。当前本机使用 Scoop 安装：

```powershell
scoop install dotnet-sdk
```

构建现代 Agent：

```powershell
.\make.cmd build
```

运行 Agent：

```powershell
.\make.cmd agent-run
```

运行自动化 diagnostics runner：

```powershell
.\make.cmd diag-run snapshot-log --count 5 --interval-seconds 2
.\make.cmd diag-run apply-readback-batch
.\make.cmd diag-run curve-watch --duration-seconds 60 --poll-seconds 5
```

说明：

- `snapshot-log` 非管理员也可运行，但可能看到 `bios_unavailable` 等降级 warning。
- `apply-readback-batch` 和 `curve-watch` 若要验证真实写入效果，建议以管理员权限运行。

## Agent 协议

完整的协议规范、JSON schema、命令参考、错误码目录和版本策略见 [`docs/protocol.md`](docs/protocol.md)。

Agent 从 `stdin` 读取一行 JSON 命令，向 `stdout` 输出一行 JSON 响应。

示例：

```json
{"cmd":"snapshot"}
{"cmd":"setManual","cpuLevel":45,"gpuLevel":45}
{"cmd":"setMax"}
{"cmd":"setProgram","name":"Power"}
{"cmd":"startCurve","intervalSeconds":5,"hysteresisC":2,"points":[{"temp":45,"cpuLevel":35,"gpuLevel":35},{"temp":55,"cpuLevel":45,"gpuLevel":45},{"temp":65,"cpuLevel":50,"gpuLevel":50}]}
{"cmd":"curveStatus"}
{"cmd":"stopCurve"}
```

当前 Agent 状态：

- `snapshot` 可返回 product、deviceProfile、BIOS fan level、fan level、warnings 和 raw EC 字段。
- `omenctl.Core` 已接入最小 BIOS / EC / fan control 路径。
- Agent 已支持实验性风扇曲线后台循环：按 `max(trustedCpuTemp, trustedGpuTemp)` 选择温度，升温立即抬档，降温按迟滞阈值防抖。
- 非管理员运行时，`snapshot` 会降级返回 product / deviceProfile / warnings。
- 写入类命令需要管理员权限；权限不足时返回 `hardware_access_denied`。
- 写入类命令返回值已统一为融合后的现代快照，不再混入仅 BIOS/EC 视角的原始温度结果。
- `setManual`、`setMax`、`applyAndReadback` 已在 8BAB 上验证有效。
- `snapshot` 当前已优先使用 LibreHardwareMonitor 提供 CPU 温度/负载，使用 NVML 提供 GPU 温度/负载/显存占用，并在 `raw.sensors` 中附带 GPU 功耗与频率。
- `curveStatus` 当前会返回 `hysteresisC`、`tickCount` 和 `applyCount`，便于观察曲线是否频繁跳档。
- `diagnostics/omenctl.Diagnostics` 可自动拉起 `omenctl` 并输出 `jsonl + summary`，用于曲线观察和批量回读验证。

## 8BAB 设备画像

当前默认策略：

- `fanLevelReliable = true`
- `fanControlReliable = true`
- `fanRpmReliable = false`
- `omenGpuTempReliable = false`
- `omenFanRateInterpretation = raw`
- `rawFanMode = 0x44`

展示层不应把 OmenMon 原始 RPM / GPTM 当作可信数据。

## 后续方向

短期目标：

1. 开始构建面向 `omenctl` 的现代 GUI。
2. GUI 第一阶段优先接入：
   - `snapshot`
   - `setManual`
   - `setMax`
   - `setProgram`
   - `startCurve / curveStatus / stopCurve`
3. 将自动化 diagnostics 结果作为后续曲线调参与稳定性优化依据。
4. 风扇策略细化、迟滞阈值微调和 `Power/Silent` 去留先后置，不阻塞 GUI 开发。

长期目标是把这个项目变成一个轻量本地 Agent + 现代 UI，而不是继续维护旧 WinForms 控制面板。

## License

本项目基于 GPL-3.0 许可的 OmenMon 代码演化而来，继续遵循 GPL-3.0。原始项目版权归 Piotr Szczepanski 及相关贡献者所有。
