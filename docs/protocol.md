# omenctl Agent 协议规范

协议版本：**1.0**

## 1. 传输层

Agent 从 `stdin` 读取一行 JSON 命令，向 `stdout` 输出一行 JSON 响应。每行是一个独立的请求/响应对。

- 不设心跳、不设帧头。
- 空行会被忽略。
- stderr 不参与协议通信，仅供诊断日志使用。

## 2. 请求格式

```json
{
  "cmd": "<command>",
  "biosMode": "...?",
  "name": "...?",
  "cpuLevel": 45,
  "gpuLevel": 45,
  "command": { ... },
  "delays": [0, 1, 3, 5, 15],
  "points": [ ... ],
  "intervalSeconds": 5,
  "hysteresisC": 2
}
```

| 字段 | 类型 | 必需 | 适用命令 |
|---|---|---|---|
| `cmd` | string | 是 | 所有 |
| `biosMode` | string? | 否 | `setAuto` |
| `name` | string? | 否 | `setProgram` |
| `cpuLevel` | int? (0..100) | 否 | `setManual` |
| `gpuLevel` | int? (0..100) | 否 | `setManual` |
| `command` | object? | 否 | `applyAndReadback` |
| `delays` | int[]? | 否 | `applyAndReadback` |
| `points` | FanCurvePoint[]? | 否 | `startCurve` |
| `intervalSeconds` | int? (1..60) | 否 | `startCurve` |
| `hysteresisC` | int? (0..10) | 否 | `startCurve` |

## 3. 响应格式

### 3.1 成功响应

```json
{
  "ok": true,
  "protocolVersion": "1.0",
  "agentVersion": "1.0.0",
  "data": { ... },
  "error": null
}
```

### 3.2 错误响应

```json
{
  "ok": false,
  "protocolVersion": "1.0",
  "agentVersion": "1.0.0",
  "data": null,
  "error": {
    "code": "hardware_access_denied",
    "message": "Hardware access was denied. Run the agent as administrator."
  }
}
```

### 3.3 通用字段

| 字段 | 类型 | 始终存在 | 说明 |
|---|---|---|---|
| `ok` | bool | 是 | 成功为 `true` |
| `protocolVersion` | string | 是 | 当前协议版本，见第 9 节 |
| `agentVersion` | string | 是 | Agent 程序集版本 `MAJOR.MINOR.BUILD` |
| `data` | object? | 是（`ok=true` 时有值） | 命令特定负载 |
| `error` | object? | 是（`ok=false` 时有值） | `{code, message}` |

## 4. 命令参考

### 4.1 命令速查

| 命令 | 分类 | `data` 类型 | 需要管理员 |
|---|---|---|---|
| `snapshot` | 只读 | `HardwareSnapshot` | 否（降级） |
| `setAuto` | 写入 | `HardwareSnapshot` | 是 |
| `setMax` | 写入 | `HardwareSnapshot` | 是 |
| `setManual` | 写入 | `HardwareSnapshot` | 是 |
| `setProgram` | 写入 | `HardwareSnapshot` | 是 |
| `applyAndReadback` | 复合 | `ReadbackRow[]` | 是 |
| `startCurve` | 曲线 | `FanCurveStatus` | 是 |
| `curveStatus` | 曲线 | `FanCurveStatus` | 否 |
| `stopCurve` | 曲线 | `FanCurveStatus` | 否 |

### 4.2 命令说明

#### snapshot

读取当前硬件快照。非管理员场景返回降级快照（含 warnings），不会报错。

#### setAuto

恢复指定 BIOS fan mode 并尝试关闭 manual EC 标记。

```json
{"cmd":"setAuto","biosMode":"Default"}
```

#### setMax

启用最大风扇模式。

```json
{"cmd":"setMax"}
```

#### setManual

手动设置 CPU/GPU 风扇 level。

```json
{"cmd":"setManual","cpuLevel":45,"gpuLevel":45}
```

`cpuLevel` / `gpuLevel` 范围 `0..100`，缺少任一参数返回 `invalid_argument`。

#### setProgram

BIOS fan mode 快捷映射。当前仅支持 `"Power"` 和 `"Silent"`，不等同于旧 OmenMon 的完整温控 Program。

```json
{"cmd":"setProgram","name":"Power"}
```

#### applyAndReadback

执行写入命令并按延迟序列重复读取快照。

```json
{
  "cmd":"applyAndReadback",
  "command":{"cmd":"setManual","cpuLevel":45,"gpuLevel":45},
  "delays":[0,1,3,5,15]
}
```

仅允许包裹写入类命令（`setAuto`/`setMax`/`setManual`/`setProgram`），其他命令返回 `invalid_apply_command`。`delays` 默认 `[0, 1, 3, 5, 15]`。

返回数组，每项格式见第 7 节。

#### startCurve

启动后台风扇曲线循环。

```json
{
  "cmd":"startCurve",
  "intervalSeconds":5,
  "hysteresisC":2,
  "points":[
    {"temp":45,"cpuLevel":35,"gpuLevel":35},
    {"temp":55,"cpuLevel":45,"gpuLevel":45},
    {"temp":65,"cpuLevel":50,"gpuLevel":50}
  ]
}
```

- 默认按 `max(trustedCpuTemp, trustedGpuTemp)` 选择曲线点。
- 升温立即抬档，降温按 `hysteresisC` 迟滞防抖。
- GPU 温度不可用时仅使用可信 CPU 温度。
- 如果已有曲线在运行，先停止再启动。

#### curveStatus

查询当前曲线状态。返回 `FanCurveStatus`（见第 6 节）。

#### stopCurve

停止后台风扇曲线。返回停止后的 `FanCurveStatus`。

## 5. HardwareSnapshot

所有写入命令和 `snapshot` 命令的 `data` 字段均为 `HardwareSnapshot`。

### 5.1 顶层结构

```json
{
  "timestamp": "2026-06-13T12:34:56.789+08:00",
  "product": "8BAB",
  "deviceProfile": { ... },
  "temps": { ... },
  "loads": { ... },
  "fans": { ... },
  "biosFan": { ... },
  "warnings": [ ... ],
  "raw": { ... }
}
```

| 字段 | 类型 | 说明 |
|---|---|---|
| `timestamp` | string (ISO 8601) | 快照创建时间 |
| `product` | string | 产品 ID，如 `"8BAB"`，未知为 `"unknown"` |
| `deviceProfile` | DeviceProfile | 设备画像 |
| `temps` | object | 温度传感器，key 为 `"cpu"` / `"gpu"` |
| `loads` | object | 负载传感器，key 为 `"cpu"` / `"gpu"` / `"gpuMemory"` |
| `fans` | object | 风扇快照，key 为 `"cpu"` / `"gpu"` |
| `biosFan` | object | BIOS 原始风扇字段 |
| `warnings` | string[] | 降级/异常标记 |
| `raw` | object | 诊断原始数据 |

### 5.2 SensorValue

`temps` 和 `loads` 中的每个值均为 `SensorValue`：

```json
{
  "value": 52.3,
  "unit": "C",
  "source": "LHM:CPU Package",
  "trusted": true,
  "suspect": false
}
```

| 字段 | 类型 | 说明 |
|---|---|---|
| `value` | double? | 数值，不可用时为 null |
| `unit` | string | `"C"` / `"%"` / `"W"` / `"MHz"` / `"MiB"` |
| `source` | string | 数据来源标识 |
| `trusted` | bool | 数据来源是否可信 |
| `suspect` | bool | 值是否可疑（如 GPTM<=5C） |

### 5.3 FanSnapshot

`fans` 中的每个值均为 `FanSnapshot`：

```json
{
  "rpm": 0,
  "rate": 128,
  "level": 50,
  "rpmTrusted": false,
  "levelTrusted": true
}
```

| 字段 | 类型 | 说明 |
|---|---|---|
| `rpm` | int? | 风扇 RPM |
| `rate` | int? | EC raw rate 值 |
| `level` | int? | 风扇 level (0..100) |
| `rpmTrusted` | bool | RPM 是否可信 |
| `levelTrusted` | bool | level 是否可信 |

### 5.4 DeviceProfile

```json
{
  "id": "8BAB",
  "fanLevelReliable": true,
  "fanControlReliable": true,
  "fanRpmReliable": false,
  "omenGpuTempReliable": false,
  "omenFanRateInterpretation": "raw",
  "rawFanMode": "0x44"
}
```

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | string | 产品标识符 |
| `fanLevelReliable` | bool | fan level 是否可靠 |
| `fanControlReliable` | bool | 风扇控制是否有效 |
| `fanRpmReliable` | bool | RPM 读数是否可信 |
| `omenGpuTempReliable` | bool | Omen EC GPU 温度是否可信 |
| `omenFanRateInterpretation` | string | `"raw"` / `"unknown"` |
| `rawFanMode` | string? | 原始 EC 风扇模式寄存器值 |

### 5.5 raw 字段

| 子字段 | 类型 | 说明 |
|---|---|---|
| `raw.ec` | object | EC 寄存器原始值（XGS1/2, SRP1/2, RPM1/3, CPUT, GPTM, HPCM 等） |
| `raw.fanMode` | int? | EC 风扇模式寄存器 |
| `raw.manual` | int? | EC manual 标记 |
| `raw.countdown` | int? | EC countdown |
| `raw.sensors` | object | 传感器指标（gpuPowerW, gpuCoreClockMHz, gpuMemoryUsedMiB 等），每个为 `{value, unit, source, trusted, suspect}` |
| `raw.sensorProviders` | object | 各 provider 的元数据 |

## 6. FanCurveStatus

`curveStatus` / `startCurve` / `stopCurve` 的 `data` 字段。

```json
{
  "running": true,
  "points": [
    {"temp":45,"cpuLevel":35,"gpuLevel":35},
    {"temp":55,"cpuLevel":45,"gpuLevel":45}
  ],
  "intervalSeconds": 5,
  "hysteresisC": 2,
  "lastTemperature": 52.3,
  "lastTemperatureSource": "LHM:CPU Package",
  "lastApplied": {"temp":45,"cpuLevel":35,"gpuLevel":35},
  "lastError": null,
  "tickCount": 42,
  "applyCount": 3,
  "timestamp": "2026-06-13T12:34:56.789+08:00"
}
```

| 字段 | 类型 | 说明 |
|---|---|---|
| `running` | bool | 曲线是否正在运行 |
| `points` | FanCurvePoint[] | 当前曲线点 |
| `intervalSeconds` | int | 循环间隔（秒） |
| `hysteresisC` | int | 降温迟滞（摄氏度） |
| `lastTemperature` | double? | 最近一次选择的温度 |
| `lastTemperatureSource` | string? | 温度来源 |
| `lastApplied` | FanCurvePoint? | 最近一次应用的曲线点 |
| `lastError` | string? | 最近错误（无则为 null） |
| `tickCount` | int | 总循环次数 |
| `applyCount` | int | 实际应用次数 |
| `timestamp` | string (ISO 8601) | 状态快照时间 |

### FanCurvePoint

```json
{"temp":45, "cpuLevel":35, "gpuLevel":35}
```

| 字段 | 类型 | 说明 |
|---|---|---|
| `temp` | int | 阈值温度（摄氏度） |
| `cpuLevel` | int | CPU 风扇 level (0..100) |
| `gpuLevel` | int | GPU 风扇 level (0..100) |

## 7. applyAndReadback 数组格式

`applyAndReadback` 返回一个对象数组，每项：

```json
{
  "phase": "readback",
  "delaySeconds": 0,
  "requestedCommand": "setManual",
  "apply": {"ok":true,"protocolVersion":"1.0","agentVersion":"1.0.0","data":{...},"error":null},
  "resultSnapshot": { ... }
}
```

| 字段 | 类型 | 说明 |
|---|---|---|
| `phase` | string | 固定 `"readback"` |
| `delaySeconds` | int | 从 apply 起经过的秒数 |
| `requestedCommand` | string | 内部命令名 |
| `apply` | AgentResponse | 写入命令的完整响应 |
| `resultSnapshot` | HardwareSnapshot | 该延迟点的快照 |

## 8. 错误码

| 错误码 | 含义 | 产生位置 |
|---|---|---|
| `invalid_json` | JSON 解析失败或命令为空 | Agent 基础设施 |
| `unknown_command` | `cmd` 值与任何已知命令不匹配 | Agent 路由 |
| `invalid_argument` | 参数缺失、类型错误或超出范围 | 参数校验 |
| `invalid_apply_command` | `applyAndReadback` 不能包裹此命令 | applyAndReadback 路由 |
| `hardware_access_denied` | 权限不足（需要管理员权限） | 硬件控制器 |
| `hardware_error` | 一般硬件访问失败 | 硬件控制器 |
| `not_implemented` | 操作未实现 | 硬件控制器 |
| `unexpected_error` | 未捕获的异常 | Agent 基础设施 |

## 9. 协议版本策略

### 9.1 版本号

- `protocolVersion`：字符串 `"MAJOR.MINOR"`，硬编码于 Agent。
  - **主版本号**递增：字段重命名、语义变更、字段删除。客户端需检查兼容性。
  - **次版本号**递增：新增字段、新增命令。向后兼容。
- `agentVersion`：字符串 `"MAJOR.MINOR.BUILD"`，从程序集版本读取，与协议版本解耦。

### 9.2 向后兼容策略

1. **字段可以增加**。客户端必须忽略不认识的 JSON 属性。
2. **已有字段不得重命名或改变语义**。需要变更时，增加新字段并保留旧字段至少一个主版本。
3. **删除字段**需在次版本中先标记弃用，再在主版本中移除。
4. **新增命令**不破坏兼容性。
5. **客户端应检查 `protocolVersion`** 确认兼容性，必要时提示用户升级 Agent。

### 9.3 版本历史

| 版本 | 日期 | 变更 |
|---|---|---|
| 1.0 | 2026-06-13 | 初始稳定版。9 个命令全部实现。写入命令统一返回融合快照。8 个错误码。 |
