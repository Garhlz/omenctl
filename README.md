# omenctl

面向 **HP Omen 16 / 8BAB** 的本地硬件控制与监控 Agent + Tauri/Svelte 桌面 GUI。

项目从 [OmenMon](https://omenmon.github.io/) 演化而来，保留其已验证的 BIOS / EC 风扇控制路径，传感器改用 LibreHardwareMonitor + NVML，GUI 通过 JSON over stdio 协议与 Agent 通信。

## 项目结构

```
omenctl.sln
src/
  omenctl.Core/        协议、设备画像、快照模型、控制接口
  omenctl.Sensors/     LHM / NVML 传感器融合
  omenctl.Agent/       JSON over stdio 本地 Agent
  omenctl.Gui/         Tauri v2 + Svelte + Tailwind 桌面 GUI
docs/
  protocol.md          完整协议规范与 JSON schema
  requirement.md       需求约束
  TODO.md              后续开发路径
  STATUS.md            已完成工作与诊断结论
diagnostics/
  omenctl.Diagnostics/ 自动化验证 CLI（smoke / snapshot-log / apply-readback-batch / curve-watch）
  8BAB/                历史诊断基线
```

## 8BAB 设备画像

```
fanLevelReliable        = true
fanControlReliable      = true
fanRpmReliable          = false
omenGpuTempReliable     = false
manualFanLevelMax       = 64
recommendedCurve:
  45°C → 35/35
  55°C → 44/44
  65°C → 52/52
  75°C → 58/58
  85°C → 64/64
```

已验证：`setManual` 35/35 ~ 64/64、`setMax`、`startCurve` / `stopCurve`、PID 线性插值曲线。

## 构建

需要 `.NET 10 SDK`（Scoop: `scoop install dotnet-sdk`）、Rust 工具链、Node.js。

```powershell
.\make.cmd build              # 构建 Agent + Diagnostics
.\make.cmd gui-run            # 构建 Agent 并启动 Tauri GUI（开发模式）
.\make.cmd gui-publish-agent  # 发布 self-contained agent exe

# Diagnostics
.\make.cmd diag-run smoke     # 一键回归
.\make.cmd diag-run snapshot-log --count 5 --interval-seconds 2
.\make.cmd diag-run apply-readback-batch
.\make.cmd diag-run curve-watch --duration-seconds 60 --poll-seconds 5
```

写入类命令需管理员权限，`snapshot` 非管理员可降级运行。

## Agent 协议

完整规范见 [`docs/protocol.md`](docs/protocol.md)。Agent 从 `stdin` 读一行 JSON，向 `stdout` 写一行 JSON。

```json
{"cmd":"snapshot"}
{"cmd":"setManual","cpuLevel":45,"gpuLevel":45}
{"cmd":"setMax"}
{"cmd":"setProgram","name":"Power"}
{"cmd":"startCurve","intervalSeconds":5,"hysteresisC":2,"points":[...]}
{"cmd":"curveStatus"}
{"cmd":"stopCurve"}
```

- `snapshot` 返回融合快照：product、deviceProfile、temps（LHM/NVML/BIOS）、loads、fans、warnings、raw。
- 写入命令统一返回融合快照，非管理员返回 `hardware_access_denied`。
- 曲线 PID 线性插值，CPU/GPU 独立分扇，`writeGate` 串行化防止并发写 EC。
- 协议版本 `1.0`，向后兼容。

## GUI

Tauri v2 + Svelte + Tailwind，通过 JSON over stdio 与 Agent 通信。

- 实时传感器仪表（温度颜色插值、负载、功耗、频率、显存、系统内存）
- CPU/GPU 风扇 level 与 RPM（不可信时显示说明文字）
- 手动控制按钮（Manual 35/35 / 45/45 / 50/50、Max、Power、Silent），曲线运行时弹确认
- 风扇曲线控制面板（启动/停止/状态，PID 插值，温度→目标 level）
- Chart.js 实时折线图（CPU/GPU 温度 + fan level，双图并排）
- Warnings 横幅、Raw JSON 调试面板、快照复制

## License

GPL-3.0。原始项目版权归 Piotr Szczepanski 及相关贡献者所有。
