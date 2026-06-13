# omenctl 需求文档

## 1. 项目概述

`omenctl` 是一个面向 **HP Omen 16 / 8BAB** 的本地硬件控制与监控项目。项目从 OmenMon 演化而来，但当前目标不是继续维护旧 WinForms 控制面板，而是构建一个边界清晰、可验证、可被现代 GUI 复用的本地硬件控制 Agent。

当前阶段的核心产品形态为：

```text
现代 GUI / diagnostics runner
        │
        │ JSON over stdio
        ▼
omenctl.Agent
        │
        ├─ omenctl.Core      HP BIOS / EC 风扇控制、设备画像、快照模型
        └─ omenctl.Sensors   LibreHardwareMonitor / NVML 传感器融合
```

## 2. 产品目标

### 2.1 当前目标

1. 为 HP Omen 16 / 8BAB 提供稳定、轻量、可验证的风扇控制能力。
2. 通过可信传感器来源展示 CPU/GPU 温度、负载、GPU 功耗、频率等状态。
3. 通过稳定的 JSON over stdio 协议向 GUI 和 diagnostics runner 暴露能力。
4. 将旧 OmenMon 中已验证有效的 BIOS / EC 控制路径保留下来，但避免 GUI 直接耦合底层硬件细节。
5. 保留自动化诊断工具，用于回归测试、曲线调参和长期记录设备行为。

### 2.2 非目标

当前阶段暂不投入以下内容：

1. 不继续维护旧 WinForms GUI。
2. 不把 OmenMon 原始 RPM / `GPTM` 作为可信展示数据。
3. 不扫描或猜测新的 HP EC 温度寄存器。
4. 不优先实现键盘灯、GPU power UI、跨设备通用适配。
5. 不在 GUI 中直接操作 BIOS / EC。
6. 不把本项目设计为跨平台硬件控制工具；底层能力明确依赖 Windows、HP BIOS WMI、WinRing0 风格驱动和 NVIDIA/NVML。

## 3. 目标用户与使用场景

### 3.1 目标用户

当前目标用户是使用 HP Omen 16 / 8BAB 的开发者用户，具备基本 Windows 管理员权限、命令行和日志分析能力。

### 3.2 核心使用场景

1. 查看设备型号、设备画像、温度、负载、风扇 level、warning 和 raw 诊断字段。
2. 手动设置 CPU/GPU 风扇 level，例如 `35/35`、`45/45`、`50/50`。
3. 启用最大风扇模式。
4. 启动、查看和停止实验性风扇曲线。
5. 使用 diagnostics runner 批量验证写入效果和回读结果。
6. 未来通过现代 GUI 完成上述操作，而不直接接触底层命令。

## 4. 运行环境要求

### 4.1 操作系统

- Windows 10 / Windows 11 x64。
- 写入 BIOS / EC 时需要管理员权限。
- 非管理员场景允许降级运行只读 `snapshot`，但必须返回明确 warning。

### 4.2 开发环境

- .NET 10 SDK。
- x64 构建目标。
- 可通过 Scoop 安装 SDK：

```powershell
scoop install dotnet-sdk
```

### 4.3 硬件与驱动依赖

- 主要目标设备：HP Omen 16 / 8BAB。
- 风扇控制依赖 HP BIOS WMI / COM 调用与 EC I/O port 读写。
- 传感器读取依赖 LibreHardwareMonitor 与 NVML。
- NVIDIA GPU 相关状态优先使用 NVML。

## 5. 系统边界与架构约束

### 5.1 Agent 边界

`omenctl.Agent` 是唯一对 GUI 暴露的硬件控制入口。GUI、diagnostics runner 和其他上层客户端不得直接引用或调用 BIOS / EC 实现。

### 5.2 控制与监控分离

- 风扇控制：使用已验证可用的 OmenMon BIOS / EC 路径。
- 传感器监控：优先使用 LibreHardwareMonitor / NVML。
- OmenMon EC raw 字段可以保留在 `raw` 中用于诊断，但不得在 UI 中伪装成可信数据。

### 5.3 串行化约束

所有硬件写入必须串行化，至少包括：

- `setAuto`
- `setManual`
- `setMax`
- `setProgram`
- `applyAndReadback`
- 风扇曲线后台写入

同一时刻不得出现多个并发 BIOS / EC 写入任务。

### 5.4 错误处理约束

Agent 不得弹窗，不得因单次硬件访问失败直接退出整个进程。所有对外错误必须统一返回 JSON error。

## 6. Agent 协议要求

### 6.1 传输形式

Agent 从 `stdin` 读取一行 JSON 命令，并向 `stdout` 输出一行 JSON 响应。

输入示例：

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

输出格式：

```json
{"ok":true,"data":{}}
{"ok":false,"error":{"code":"hardware_access_denied","message":"Hardware access was denied. Run the agent as administrator."}}
```

### 6.2 通用响应要求

1. 所有命令都必须返回合法 JSON。
2. 所有响应必须包含 `protocolVersion`（当前 `"1.0"`）和 `agentVersion`（如 `"1.0.0"`）。
3. `ok=false` 时必须包含 `error.code` 与 `error.message`。
4. 未知命令必须返回 `unknown_command`。
5. JSON 解析失败必须返回 `invalid_json`。
6. 参数错误必须返回 `invalid_argument`。
7. 权限不足必须返回 `hardware_access_denied`。
8. `applyAndReadback` 包裹不支持的命令时返回 `invalid_apply_command`。
9. 硬件访问失败返回 `hardware_error`。
10. 未实现的操作返回 `not_implemented`。
11. 未捕获异常返回 `unexpected_error`。

完整的 JSON schema、命令参考和版本策略见 `docs/protocol.md`。

### 6.3 协议版本要求

1. 每个响应必须返回 `protocolVersion` 字段，当前值为 `"1.0"`。
2. 每个响应必须返回 `agentVersion` 字段，格式为 `"MAJOR.MINOR.BUILD"`，从程序集版本读取。
3. 协议版本采用 `MAJOR.MINOR` 格式：
   - 主版本号递增表示破坏性变更（字段重命名、语义变更、字段删除）。
   - 次版本号递增表示向后兼容的变更（新增字段、新增命令）。
4. 已有字段不得随意改名或改变语义。需要变更时增加新字段并保留旧字段至少一个主版本。
5. 客户端可以安全忽略不认识的 JSON 字段。

## 7. 功能需求

### FR-001：设备快照

`{"cmd":"snapshot"}` 必须返回融合后的现代快照，至少包含：

- `product`
- `deviceProfile`
- `temps`
- `loads`
- `fans`
- `biosFan`
- `warnings`
- `raw`

验收条件：

1. 管理员场景下可以读取完整快照。
2. 非管理员场景下仍返回合法 JSON，并通过 warning 标记降级原因。
3. 快照中的 CPU/GPU 温度不得把明显不可信的 EC raw 字段当作可信值。

### FR-002：手动风扇 level 控制

`{"cmd":"setManual","cpuLevel":N,"gpuLevel":M}` 用于设置 CPU/GPU 风扇 level。

要求：

1. `cpuLevel` 与 `gpuLevel` 必须在 `0..100` 范围内。
2. 当前 8BAB 验证目标至少覆盖 `35/35`、`45/45`、`50/50`。
3. 命令返回值必须是融合后的现代快照，而不是纯 BIOS / EC 视角的局部结果。
4. 权限不足时返回 `hardware_access_denied`。

### FR-003：最大风扇模式

`{"cmd":"setMax"}` 用于启用最大风扇模式。

验收条件：

1. 在 8BAB 上能够实际拉高 fan level。
2. 返回融合快照。
3. diagnostics runner 可以记录命令后 `0s/1s/3s/5s/15s` 的读回结果。

### FR-004：BIOS fan mode 快捷映射

`{"cmd":"setProgram","name":"Power"}` 和 `{"cmd":"setProgram","name":"Silent"}` 当前只作为 BIOS fan mode 快捷映射。

要求：

1. 文档和 UI 必须明确这不等同于旧 OmenMon 的完整温控 Program。
2. 旧 Program 语义应由新的 fan curve 能力逐步替代。
3. 未知名称必须返回参数错误。

### FR-005：自动模式

`{"cmd":"setAuto","biosMode":"Default"}` 用于关闭最大风扇并恢复指定 BIOS fan mode。

要求：

1. 命令应尝试关闭 manual EC 标记。
2. EC 写入失败不应掩盖 BIOS 模式恢复结果，但必须在快照或 warning 中保留可诊断信息。

### FR-006：风扇曲线

Agent 必须支持：

- `startCurve`
- `curveStatus`
- `stopCurve`

要求：

1. 默认依据 `max(trustedCpuTemp, trustedGpuTemp)` 选择曲线点。
2. 默认使用 `2C` 迟滞抑制降温跳档。
3. 当 GPU 温度不可用时，只允许使用可信 CPU 温度驱动曲线。
4. 不允许使用 `GPTM<=5C` 作为可信 GPU 温度。
5. `curveStatus` 至少返回：
   - 是否运行
   - 最近一次温度
   - 最近一次温度来源
   - 最近一次应用的曲线点
   - `tickCount`
   - `applyCount`
   - 最近错误

### FR-007：applyAndReadback

`applyAndReadback` 用于执行写入命令并按延迟序列重复读取快照。

要求：

1. 默认延迟序列为 `[0, 1, 3, 5, 15]` 秒。
2. 支持用户自定义 `delays`。
3. 输出应包含原始 apply 响应与每次回读快照。
4. 仅允许包裹写入类命令，不允许递归包裹 `applyAndReadback`。

### FR-008：diagnostics runner

diagnostics runner 必须自动拉起 `omenctl` 子进程，并通过 JSON over stdio 协议交互。

首批命令：

- `snapshot-log`
- `apply-readback-batch`
- `curve-watch`

输出要求：

1. 原始 `jsonl`。
2. 控制台 summary。
3. 默认输出目录为 `diagnostics/<product>/`。
4. 写入类诊断应建议在管理员权限下运行。

### FR-009：现代 GUI

GUI 第一阶段只作为 Agent 客户端，不直接操作 BIOS / EC。

首批功能：

1. 展示 `snapshot` 设备信息。
2. 展示 CPU/GPU 温度、负载、风扇 level。
3. 展示 warnings。
4. 展示 raw JSON / 调试信息。
5. 支持 `setManual`。
6. 支持 `setMax`。
7. 支持 `setProgram`。
8. 支持启动、查看、停止风扇曲线。

GUI 验收条件：

1. GUI 可以读取并展示一次完整 `snapshot`。
2. GUI 可以触发 `setManual`、`setMax` 并显示回读结果。
3. GUI 可以启动曲线并展示 `curveStatus` 关键字段。
4. GUI 不引用 `OmenFanController`、`OmenBiosClient`、`OmenEmbeddedController` 等底层类型。

## 8. 数据可信度要求

### 8.1 8BAB 设备画像

当前 8BAB 默认规则：

```text
fanLevelReliable = true
fanControlReliable = true
fanRpmReliable = false
omenGpuTempReliable = false
omenFanRateInterpretation = raw
rawFanMode = 0x44
```

### 8.2 展示规则

1. fan level 是主要风扇反馈。
2. RPM 默认不可用。
3. rate 只作为 raw 字段展示，不解释为真实速度。
4. `GPTM` 不作为 GPU 温度。
5. CPU 温度优先使用 LibreHardwareMonitor，必要时允许 BIOS fallback。
6. GPU 温度优先使用 NVML，其次 LibreHardwareMonitor。

## 9. 非功能需求

### 9.1 稳定性

1. Agent 不得因为单次硬件读取失败崩溃。
2. 后台风扇曲线异常时必须记录 `lastError`，并允许客户端停止曲线。
3. diagnostics runner 不应影响正常 Agent 协议兼容性。

### 9.2 可观测性

1. `warnings` 必须可读、稳定、可被 GUI 直接展示。
2. `raw` 中应保留足够字段用于诊断，但不得污染可信展示字段。
3. 诊断结果应长期保存在 `diagnostics/<product>/` 下，便于比较不同版本行为。

### 9.3 安全性

1. 写入命令必须校验参数范围。
2. 权限不足必须显式返回 `hardware_access_denied`。
3. 不提供危险的任意 EC 写入对外命令，除非未来单独进入 debug / developer mode 并明确隔离。

### 9.4 可维护性

1. Core、Sensors、Agent、Diagnostics、GUI 应保持职责分离。
2. GUI 与 diagnostics 不得复制硬件控制逻辑。
3. 新增设备支持时必须先增加设备画像和 diagnostics 记录，再开放 UI 策略。

## 10. 验收与回归标准

每个阶段合入前至少满足：

1. `.\make.cmd build` 成功。
2. `snapshot` 在管理员与非管理员场景下均返回合法 JSON。
3. 8BAB 上 `setManual 35/35`、`45/45`、`50/50`、`setMax` 能写入并读回。
4. `snapshot` 不把 `GPTM=1C` 表现为正常 GPU 温度。
5. 风扇曲线可以启动、查询状态、停止。
6. 所有对外名称、命令说明、构建产物与文档均使用 `omenctl`。
