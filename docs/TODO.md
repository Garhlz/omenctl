# TODO：omenctl 后续开发路径

本文档只记录后续要做的工作。已经完成的迁移、诊断和验证结论请见 `STATUS.md`；稳定需求与验收标准请见 `requirement.md`。

排序原则：

- **优先级**：P0 必须做，P1 高价值，P2 可延后，P3 暂缓。
- **ROI**：综合考虑用户可见收益、实现成本、风险降低程度和是否阻塞后续工作。
- **开发顺序**：默认按本文档顺序推进；除非发现新的硬件风险，否则不要先做低 ROI 的大功能。

## P0：合并前基线与文档整理

**ROI：极高**  
**目标：防止继续开发时丢失当前已验证能力。**

任务：

1. 将本文档、`requirement.md`、`STATUS.md` 放入 `docs/`。
2. README 只保留项目简介、构建命令、最小使用示例和文档导航。
3. 确认 `.\make.cmd build` 成功。
4. 执行一次 diagnostics 基线：
   - `snapshot-log`
   - `apply-readback-batch`
   - `curve-watch`
5. 将最新结果保存到 `diagnostics/8BAB/`。

验收：

- 文档职责清晰：需求、TODO、状态归档不互相重复。
- 构建成功。
- 有一份可回看的 8BAB 最新诊断基线。

## P0：Agent 协议稳定化 ✅

**ROI：极高**
**目标：让未来 GUI 可以放心依赖 Agent，而不是边写 GUI 边改协议。**
**状态：已完成（2026-06-13）**

完成内容：

- `AgentResponse` 增加 `protocolVersion`（`"1.0"`）和 `agentVersion`（从程序集版本读取）。
- 新建 `docs/protocol.md`：完整 JSON schema、命令参考、8 个错误码目录、版本策略。
- `FanCurveStatus` 统一显式 `[JsonPropertyName]`。
- `docs/requirement.md` 补充版本字段要求和 `invalid_apply_command` 错误码。
- 向后兼容验证通过：diagnostics runner 三个命令均正常运行。

## P0：最小 GUI 原型 ✅

**ROI：极高**
**状态：已完成（2026-06-13）**

完成内容：

- Tauri v2 + Svelte + Tailwind 桌面 GUI，通过 JSON over stdio 与 omenctl Agent 通信。
- 6 个 Svelte 组件：AgentClient（生命周期+轮询）、SensorCard（温度颜色插值）、FanPanel（RPM 说明文字）、ControlButtons（写入禁用防并发）、WarningBanner（中文映射）、RawJsonPanel（调试）。
- `make.cmd` 增加 `gui-run`、`gui-build`、`gui-publish-agent`。
- LHM 新增系统内存读取（`IsMemoryEnabled` + `AddMemorySamples`）。
- 已验证：snapshot 展示、温度颜色、Manual/Max/Power/Silent 写入与回读、warnings 展示、RPM unavailable 说明文字、并发保护。

## P0：GUI 安全保护与状态反馈 ✅

**ROI：高**
**状态：已完成（2026-06-13）**

已实现：

- 写入命令期间按钮禁用（`isWriting` 控制）。
- Agent 串行 gate 防止并发写入。
- Agent 连接状态指示灯（stopped/running/error）。
- `hardware_access_denied` 错误横幅提示。
- RPM 不可信 → "RPM unavailable" 说明文字。
- 复制当前快照 JSON 按钮。

## P1：风扇曲线 GUI MVP ✅

**ROI：高**
**状态：已完成（2026-06-14）**

完成内容：

- CurvePanel：启动/停止/状态，曲线点预览，CPU/GPU 分显温度→目标 level。
- MonitorCharts：Chart.js 双图（CPU/GPU 温度+fan level），始终可见。
- ControlButtons：曲线运行时 Manual/Max 弹确认→自动 stop 后再写，失败不盲发。
- Agent：PID 线性插值、独立分扇、writeGate 串行化、LHM 瞬断重试、worker crash guard。
- code review 修复：agent_manager 竞态、setInterval→setTimeout、死代码清理、默认点同步。

## P1：diagnostics 结果纳入回归流程 ✅

**ROI：高**
**状态：已完成（2026-06-14）**

- `apply-readback-batch` summary 增加 requestedLevel、finalBiosLevel、warnings、passed 判定。
- `curve-watch` 增加 sourceFlaps（温度源切换次数）、applyChanges（应用变更次数）。
- 新增 `diag-run smoke` 一键回归：snapshot-log 3 次 → apply-readback-batch → curve-watch 30s。
- 曲线点引用 `DeviceProfiles.EightBabCurve`，不再独立维护。

## P1：Agent 生命周期与 GUI 集成细节

**ROI：中高**  
**目标：让 GUI 不只是 demo，而是日常可用。**

任务：

1. GUI 自动寻找或启动 `omenctl.exe`。
2. Agent 退出时 GUI 自动提示并允许重启。
3. GUI 退出时正确停止子进程。
4. 增加超时机制，避免某个命令卡住整个界面。
5. 将 Agent stdout/stderr 日志保存到用户目录或项目日志目录。
6. 增加“打开日志目录”入口。

验收：

- Agent 崩溃后 GUI 能恢复。
- 硬件命令超时时 GUI 不假死。
- 日志能用于复现问题。

## P1：配置持久化 ✅

**ROI：中高**
**状态：已完成（2026-06-14）**

- 接入 `tauri-plugin-store`，配置文件 `omenctl-settings.json` 位于用户目录。
- `config.svelte.js` 封装存储/恢复：手动预设、曲线点、刷新间隔、raw JSON 展开。
- ControlButtons 按钮从 `manualPresets` 读取动态生成。
- CurvePanel 启动曲线时 `saveCurvePoints`。
- AgentClient 轮询间隔读 `refreshInterval`，RawJsonPanel 由 `showRawJson` 控制显隐。

**ROI：中高**  
**目标：保存用户常用设置，减少每次手动重配。**

任务：

1. 保存最近使用的手动 fan level。
2. 保存默认曲线点。
3. 保存 GUI 刷新间隔。
4. 保存是否显示 raw JSON。
5. 保存窗口大小和位置。
6. 配置文件放在用户目录，避免污染仓库。

验收：

- 重启 GUI 后能恢复上次常用设置。
- 配置损坏时能回退默认值，不影响 Agent 启动。

## P2：曲线编辑器与策略调优

**ROI：中等**  
**目标：在 GUI MVP 稳定后，再提高可调性。**

任务：

1. 支持编辑曲线点。
2. 校验温度升序和 level 范围。
3. 支持导入/导出曲线 preset。
4. 支持调整 hysteresisC 和 intervalSeconds。
5. 基于 diagnostics 结果优化默认曲线。
6. 研究是否需要按 AC / battery 分开策略。

验收：

- 用户能安全编辑曲线。
- 无效曲线不会发送给 Agent。
- 默认曲线比当前实验值更适合日常使用。

## P2：托盘与后台常驻 ✅

**ROI：中等**
**状态：已完成（2026-06-14）**

- Tauri v2 `TrayIconBuilder` + `tray-icon` feature，右键菜单 6 项。
- Snapshot / Manual 45/45 / Max / Start Curve / Stop Curve / Exit。
- 关闭窗口 → 隐藏到托盘（`CloseRequested` 拦截），Exit 真正退出并 stop agent。
- 前端 `listen` tray events，曲线用 config defaultPoints 启动。
- AgentClient `$effect` 自动注册/清理事件监听。

## P2：打包与发布

**ROI：中等**  
**目标：降低自己日用和后续分发成本。**

任务：

1. 发布 self-contained x64 build。
2. 生成 release zip。
3. 包含：
   - `omenctl.exe`
   - GUI exe
   - driver resource
   - license
   - README
4. 写清楚管理员权限要求。
5. 考虑是否需要应用清单请求管理员权限。
6. 暂不急于签名；先保证构建可复现。

验收：

- 新机器上解压即可运行。
- README 能说明如何以管理员权限启动。
- Release 包不包含源码临时文件和 diagnostics 历史日志。

## P2：更完整日志系统

**ROI：中等**  
**目标：降低排查硬件问题的成本。**

任务：

1. 为 Agent 增加结构化日志。
2. 为每次写入记录：
   - command
   - input
   - apply result
   - snapshot after apply
   - warnings
3. 为曲线循环记录关键 tick。
4. 日志脱敏并限制大小。
5. GUI 提供导出日志按钮。

验收：

- 控制效果异常时可以从日志看到写入和回读过程。
- 日志不会无限增长。

## P3：GPU power UI

**ROI：低到中**  
**原因：硬件风险与语义复杂度高，且当前目标是风扇控制。**

暂缓内容：

- GPU power preset。
- PPAB / CustomTGP UI。
- 与 BIOS 写入相关的高风险设置。

进入条件：

- 有明确诊断数据证明需要。
- 先在 diagnostics runner 中实现验证命令，再考虑 GUI。

## P3：多设备支持

**ROI：低**  
**原因：当前项目定位是 HP Omen 16 / 8BAB，泛化会显著增加测试成本。**

暂缓内容：

- 自动扫描并适配更多 HP Omen 型号。
- 新 EC register 映射。
- 通用设备规则 UI。

进入条件：

- 至少有第二台设备的 diagnostics 数据。
- 新设备有独立 `DeviceProfile`。
- 不影响 8BAB 默认路径。

## 推荐近期迭代顺序

1. ~~文档整理与 README 导航~~ ✅
2. ~~协议字段冻结与错误码整理~~ ✅
3. ~~GUI 最小原型~~ ✅
4. ~~GUI 状态保护~~ ✅
5. ~~风扇曲线 GUI~~ ✅
6. ~~diagnostics smoke 回归命令~~ ✅
7. ~~配置持久化~~ ✅
8. ~~Agent 生命周期健壮性~~ ✅（已在 code review 中修复：command_lock、timeout 自愈、quit_app）
9. ~~托盘~~ ✅
10. 打包与发布。← 下一步
11. 曲线编辑器与可视化。
