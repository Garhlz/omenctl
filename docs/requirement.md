# omenctl 当前阶段需求

## 产品定位

`omenctl` 是一个面向 **HP Omen 16 / 8BAB** 的本地硬件控制 Agent。当前阶段只聚焦风扇控制、可信传感器读取与稳定 JSON 协议，不投入旧 WinForms GUI，也不提前投入 Tauri/Web UI。

当前 backend 能力已经足够支撑 GUI 开发；后续 GUI 应建立在现有 Agent 协议之上，而不是重新耦合 BIOS / EC 细节。

## 当前必须满足

- 保留已验证可用的 HP BIOS / EC 风扇控制路径。
- 保留 `diagnostics/` 下的现代自动化验证工具，用于重复采样而不是依赖手工日志。
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

## diagnostics runner 要求

- runner 必须能够自动拉起 `omenctl` 子进程并通过 stdio JSON 协议交互。
- runner 首批至少提供：
  - `snapshot-log`
  - `apply-readback-batch`
  - `curve-watch`
- runner 输出至少包含：
  - 原始 `jsonl`
  - 一份 summary
- 默认输出目录应落在 `diagnostics/<product>/` 下，便于长期积累 8BAB 等设备的验证结果。
- `snapshot-log` 允许非管理员降级运行；涉及真实写入验证的 `apply-readback-batch` 与 `curve-watch` 应默认在管理员场景下使用。

## 传感器与可信度要求

- CPU 温度优先使用 LibreHardwareMonitor。
- 当 LibreHardwareMonitor 没有可用 CPU 温度时，允许退回 BIOS fallback，并显式返回 `cpu_temp_bios_fallback`。
- GPU 温度优先使用 NVML，其次可退到 LibreHardwareMonitor。
- 不允许把 `GPTM<=5C` 当作可信 GPU 温度驱动风扇曲线。
- OmenMon 的 RPM 继续视为不可信，不作为主要控制反馈。

## 风扇曲线要求

- 默认依据 `max(trustedCpuTemp, trustedGpuTemp)` 选择曲线点。
- 默认使用 `2C` 迟滞抑制降温时的来回跳档。
- 在 NVML 尚未可用时，只允许使用可信 CPU 温度驱动曲线。
- 曲线状态必须返回最近一次使用的温度值、来源和应用点。
- 曲线状态应额外暴露基础运行计数，便于判断循环是否正常推进、是否频繁改档。

## 验收条件

- `.\make.cmd build` 成功，且不引入新的 warning/error。
- `snapshot` 在管理员与非管理员场景都能返回合法 JSON。
- `setManual 35/35`、`45/45`、`50/50`、`setMax` 仍可在 8BAB 上写入并读回。
- `snapshot` 不再把 `GPTM=1C` 表现为正常 GPU 温度。
- 所有对外名称、命令说明、构建产物与文档均使用 `omenctl`。
