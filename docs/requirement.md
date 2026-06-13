# omenctl 当前阶段需求

## 产品定位

`omenctl` 是一个面向 **HP Omen 16 / 8BAB** 的本地硬件控制 Agent。当前阶段只聚焦风扇控制、可信传感器读取与稳定 JSON 协议，不投入旧 WinForms GUI，也不提前投入 Tauri/Web UI。

## 当前必须满足

- 保留已验证可用的 HP BIOS / EC 风扇控制路径。
- `snapshot` 必须稳定返回：
  - `product`
  - `deviceProfile`
  - `temps`
  - `loads`
  - `fans`
  - `biosFan`
  - `warnings`
  - `raw`
- 所有写入类命令的返回快照必须与 `snapshot` 保持同一融合口径，不能单独退回到底层 BIOS/EC 原始温度视图。
- 写入类命令必须继续串行化：
  - `setManual`
  - `setMax`
  - `setProgram`
  - `applyAndReadback`
  - 风扇曲线后台写入

## 传感器与可信度要求

- CPU 温度优先使用 LibreHardwareMonitor。
- 当 LibreHardwareMonitor 没有可用 CPU 温度时，允许退回 BIOS fallback，并显式返回 `cpu_temp_bios_fallback`。
- GPU 温度优先使用 NVML，其次可退到 LibreHardwareMonitor。
- 不允许把 `GPTM<=5C` 当作可信 GPU 温度驱动风扇曲线。
- OmenMon 的 RPM 继续视为不可信，不作为主要控制反馈。

## 风扇曲线要求

- 默认依据 `max(trustedCpuTemp, trustedGpuTemp)` 选择曲线点。
- 在 NVML 尚未可用时，只允许使用可信 CPU 温度驱动曲线。
- 曲线状态必须返回最近一次使用的温度值、来源和应用点。

## 验收条件

- `.\make.cmd build` 成功，且不引入新的 warning/error。
- `snapshot` 在管理员与非管理员场景都能返回合法 JSON。
- `setManual 35/35`、`45/45`、`50/50`、`setMax` 仍可在 8BAB 上写入并读回。
- `snapshot` 不再把 `GPTM=1C` 表现为正常 GPU 温度。
- 所有对外名称、命令说明、构建产物与文档均使用 `omenctl`。
