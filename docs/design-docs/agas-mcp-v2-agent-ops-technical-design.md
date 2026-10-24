# AGAS MCP v2 Agent Ops 技术方案

> 文件：`docs/design-docs/agas-mcp-v2-agent-ops-technical-design.md`  
> 项目：仓库根目录 `APP-AGAS-CS`
> 配套执行计划：`docs/exec-plans/active/agas-mcp-v2-agent-ops-execution-plan.md`  
> 前置方案：`docs/design-docs/agas-mcp-technical-design.md`（MCP v1，Gate 2 已收尾）  
> 状态：**v2 已实现并完成 Wave 3 与 annotation 契约收尾**（2026-07-25）；收尾证据为 `f416371`、`83e2358`、`814e23b`、`21bc853`、`68a6666`、`fbe4c5f`、`c221f38`、`615c214`、`1f09027`、`29093ce`、`8892e17`、`a67c4ad` 及 §12.1 所列测试结果。
> 技术固定：Godot `4.6.1` Mono / `.NET 8` / 全 C#；禁止 JS/TS/GDScript。  
> 协议固定：MCP Streamable HTTP；endpoint `http://127.0.0.1:54119/mcp`；官方 `ModelContextProtocol.AspNetCore` `1.4.1`。

---

## 1. 背景与目标

### 1.1 用户目标（已确认）

1. **可以打开 AGAS 软件，并在设置中启动 MCP**。  
2. 外部 Agent（Grok / Claude / Cursor 等）连接 MCP 后，能像真人一样完成软件内核心操作。  
3. 典型剧本：新建场景 → 新建图层 → 放置/框选图片或区域 → 写提示词 → **发送 AI 生成请求** → 应用结果 → 保存。  
4. **不要求**「完全不打开软件」；无 UI headless 宿主是远期可选，不在本方案 v2 范围。

### 1.2 产品定位

| 维度 | v1（已实现） | v2（本方案） |
|------|-------------|-------------|
| 使用前提 | 打开 AGAS + 打开项目 + 开启 MCP | 同左 |
| 能力重心 | 项目/场景/图层/图片/提示词/视图 CRUD | 在 v1 上补齐 **选区 + 选中图片 + AI 生成链路** |
| 操作模型 | 领域 API（改 Resource / 内存状态） | 同左；**不做鼠标/键盘 UI 自动化** |
| 工具数量 | 严格 8 个 | **在 8 个基础上增量扩展**（见 §5）；不推翻 v1 契约 |

### 1.3 目标

1. Agent 用 MCP 完成「框选区域 / 选中图片 / 发起 AI 生成 / 查询任务状态 / 应用结果」全链路。  
2. 所有新工具仍走：HTTP 线程排队 → 主线程 Facade → 安全校验 → 结构化 JSON；写盘仍需显式 save（AI 生成落盘路径遵循现有 `AgasGeneratedImagePathPolicy`）。  
3. 复用现有 `CanvasWorkspace` 选区状态、`MainWorkspace` AI 准备/应用逻辑、`AiGenerationService`，避免平行实现第二套业务。  
4. 保持 v1 安全边界：loopback only、相对路径、无 shell/公网任意 FS、无 base64 返回（AI 结果只返回项目相对路径与元数据）。

### 1.4 非目标（v2 明确不做）

- 不实现「软件进程未启动」的远程全功能（headless daemon）。  
- 不模拟点击按钮、拖拽手势、屏幕坐标点击（非 UI automation）。  
- 不在 v2 一次性覆盖 Blueprint 图编辑/执行（列入 v3 候选）。  
- 不暴露 API Key / 明文 Provider 密钥给 MCP 客户端。  
- 不返回图片 base64、`data:` URI、绝对路径。  
- 不放宽 loopback / Host / Origin / 路径安全策略。  
- 不把 MCP 变成任意 HTTP 代理去访问公网。

---

## 2. 已验证事实与交付结果

### 2.1 已验证事实（代码与 v1 文档）

- MCP 进程内 host：`src/Mcp/**`，生命周期 owner `src/UI/Main.cs`。  
- v1 正式 8 工具：  
  - `agas.project.snapshot`  
  - `agas.assets.list`  
  - `agas.scene.mutate`（`add` / `rename` / `delete` / `setActive`）  
  - `agas.layer.mutate`（`add` / `rename` / `delete` / `move` / `setVisible` / `setLocked` / `setCurrent`）  
  - `agas.image.mutate`（`add` / `update`·`move` / `delete`）  
  - `agas.prompt.mutate`（`add` / `update` / `delete` / `move` / `setEnabled`）  
  - `agas.view.set`  
  - `agas.project.save`  
- 未打开项目时返回 `AGAS_NO_OPEN_PROJECT`。  
- 写操作默认内存 + dirty；`agas.project.save` 才写 `.agas/*.tres`。  
- 画布已有选区模型：  
  - `CanvasWorkspace.SelectionRegion` / `ReplacementRegion`（`CanvasGridRegion`）  
  - `CanvasWorkspace.GetSelectedImage()` / 选择相关 API  
  - `TryCaptureCurrentPatch` / `CanvasCompositeRenderer` 支持区域生成与替换  
- AI 链路已有：  
  - `AiGenerationService.GenerateAsync`  
  - `AiGenerationPanel` + `MainWorkspace.PrepareAiGenerationRequest` / `ApplyAiGeneratedImage`  
  - 操作类型：`TextToImage` / `ReferenceEdit` / `RegionEdit`  
  - 生成图路径策略：`AgasGeneratedImagePathPolicy` / `AiGeneratedImageStore`  

### 2.2 v1 基线差距与 v2 交付结果

| 人手操作 | v1 MCP | v2 交付结果 |
|---------|--------|-------------|
| 新建场景/图层/放图/写提示词/保存 | 已有 | 保持 v1 契约与行为 |
| 框选网格区域（黄框选区 / 红框替换区） | 无 | 已实现 `agas.selection.set` |
| 选中某张图片实例 | 无（只能 mutate 路径/位姿） | 已实现 `agas.selection.set op=selectImage` |
| 读取当前选区与选中状态 | snapshot 未含选区/选中图 | 已实现独立 `agas.selection.get`；snapshot 未扩展 |
| 发送 AI 生成请求 | 无 | 已实现异步 `agas.ai.generate` |
| 查看生成进度 / 取消 | 无 | 已实现 `agas.ai.status` / `agas.ai.cancel` |
| 应用生成结果到图层/替换图 | 无 | 已实现幂等 `agas.ai.apply` |
| Blueprint 编辑执行 | 无 | 保持为 v3 范围 |
| 新建/打开项目 | 无（依赖 UI） | 不进 v2；仍由用户在 UI 打开 |

---

## 3. 架构原则

### 3.1 领域 API，不是 UI 录制

```text
错误方向：MCP → 模拟鼠标点击「新建图层」按钮
正确方向：MCP → Facade.LayerMutate(op=add) → 改 Resource → UI 刷新
```

v2 继续此方向：选区、选中、AI 都通过 Facade 调已有业务入口，再通知 UI 同步。

### 3.2 依赖方向（与 v1 一致）

```text
MCP HTTP handler
  -> Serial executor / rate limit / security
  -> MainThreadDispatcher
  -> AgasMcpProjectFacade（及 v2 拆分的 Ai/Selection Facade 协作）
  -> CanvasWorkspace / MainWorkspace AI hooks / AiGenerationService
  -> 轻量 DTO（相对路径 + id + 状态）
```

约束：

- HTTP 线程禁止直接碰 Godot `Node` / `Resource`。  
- AI 网络 IO 在后台任务执行；**开始/提交/应用结果的 Resource 变更必须回主线程**。  
- v1 工具串行语义保留；AI 长任务采用「提交任务 + 轮询」模型，避免阻塞 30s 语义硬超时（见 §6）。

### 3.3 与 v1 工具的关系

- **不删除、不改名** v1 的 8 个工具名。  
- `agas.project.snapshot` 保持 v1 结构，不加入 selection；读取选区统一调用独立 `agas.selection.get`。  
- 新增工具（v2 正式清单见 §5）。  
- 客户端应继续能用旧 8 工具完成基础编辑。

### 3.4 UI 同步

新操作成功后必须：

1. 更新内存 Resource / Canvas 状态；  
2. 只有内容变更才按 v1 模型标记 dirty；选区/选中属于临时 UI 状态，不标记 dirty；  
3. 通过既有 `AgasMcpUiRefreshScope` 通知工作区刷新选区、选中、图层与 AI 面板状态。

### 3.5 Annotation 驱动的单一契约源（已落地）

当前运行时 16 个工具（v1 8 个 + v2 6 个 + 后续 Blueprint 2 个）的输入契约不再分别维护「手写 inputSchema」和「手写运行时 wire 校验」。当前链路为：

```text
BCL DataAnnotations + AGAS MCP attributes
  -> AgasMcpContractRegistry / descriptor
     -> 生成 tools/list inputSchema
     -> Validate + Bind Execute arguments
     -> ValidateInstance public typed Facade inputs
```

- BCL `Required` / `Range` / `StringLength` / `RegularExpression` / `AllowedValues` 与 AGAS 的 closed object、条件必填、唯一数组、items enum、definition 等 attributes 共同描述契约。
- `AgasMcpContractRegistry` 是 schema、默认值物化、严格大小写绑定和运行时参数验证的集中入口；当前 16 个 Facade Execute 入口与 public typed handlers 均经过同一 descriptor。
- 当前使用的 MCP SDK 负责承载/发布项目提供的 `inputSchema`，但不会替本项目自动执行这套 runtime 参数校验；因此 AGAS 在 Facade 边界集中补 `Validate + Bind` 层，失败统一映射为 `AGAS_INVALID_OPERATION`。
- 本实现只覆盖 AGAS 冻结契约需要的关键字，**不实现通用 JSON Schema 引擎**；未新增 NuGet，继续使用现有 BCL 与项目内 attributes/descriptor。

---

## 4. 能力分层与波次

```text
Wave 0  契约冻结
Wave 1  选区与选中（同步，纯内存）
Wave 2  AI 生成任务（异步：submit / status / cancel / apply）
Wave 3  集成验收（错误码、生命周期、Godot acceptance、v1 回归）
v3      Blueprint MCP（不进本版）
```

本方案 **v2 交付范围 = Wave 0–3**；Blueprint 仅记录边界，不进入 v2 验收。

---

## 5. 工具契约（v2）

### 5.1 工具总表

| 工具名 | 读写 | 同步/异步 | 说明 |
|--------|------|-----------|------|
| （v1 保留）8 工具 | 同 v1 | 同步 | 场景/图层/图片/提示词/视图/保存 |
| `agas.selection.set` | 写 | 同步 | 设置/清除选区、替换区、当前选中图片 |
| `agas.selection.get` | 读 | 同步 | 读取当前选区、替换区、选中图片、网格尺寸 |
| `agas.ai.generate` | 写 | **异步提交** | 基于提交时捕获的当前画布上下文提交生成任务 |
| `agas.ai.status` | 读 | 同步 | 查询任务状态与结果元数据 |
| `agas.ai.cancel` | 写 | 同步 | 取消进行中的任务 |
| `agas.ai.apply` | 写 | 同步 | 将成功结果应用到画布（新图/替换选中图） |
| `agas.blueprint.capabilities` | 读 | 同步 | 后续增量：读取 Blueprint 节点能力与可执行工作流 |
| `agas.blueprint.execute` | 写 | 同步 | 后续增量：执行已有 Blueprint 并保存输出 |

**v2 新增正式工具数：6**。  
v2 里程碑交付时为 v1 8 个 + v2 6 个，共 14 个；当前运行时又加入 Blueprint 2 个，`tools/list` 共 **16** 个（以 `src/Mcp/Contracts/AgasMcpToolNames.cs` 为准）。`agas.selection.get` 已冻结为独立工具，`agas.project.snapshot` 不加入 selection。

### 5.2 `agas.selection.set`

**目的**：等价于人手切换区域工具并框选 / 点选图片。

```json
{
  "op": "setRegions | clearRegions | selectImage | clearImageSelection",
  "sceneId": "scene-...",
  "selection": { "column": 0, "row": 0, "width": 4, "height": 4 },
  "replacement": { "column": 1, "row": 1, "width": 2, "height": 2 },
  "imageId": "image-...",
  "layerId": "layer-..."
}
```

规则：

- 网格坐标使用现有 `CanvasGridRegion` 语义（列/行/宽/高，单位为格）。  
- `width/height >= 1`；越界策略：拒绝并返回 `AGAS_SELECTION_INVALID`（不静默 clamp）。参数缺失、类型错误等非选区语义错误仍使用 v1 通用参数错误。  
- `selectImage` 要求图片属于指定 scene 的**当前可见且未锁定图层**；隐藏或锁定图层返回 `AGAS_LAYER_NOT_EDITABLE`。这与现有手工画布仅命中当前可编辑图层的行为一致，不增加 MCP 专属的“只读选中”。  
- 只改内存；不写盘；触发 UI 刷新。  
- 若当前 active scene ≠ `sceneId`，返回 `AGAS_INVALID_OPERATION`；Agent 必须先调用 `agas.scene.mutate op=setActive`。selection 工具不隐式切场景，也不因选区变化标记项目 dirty。

### 5.3 `agas.selection.get`

```json
{
  "sceneId": "scene-...",
  "activeRegionTool": "Edit | Selection | Replacement | ...",
  "selection": null,
  "replacement": null,
  "effectiveGenerationRegion": null,
  "selectedImageId": null,
  "selectedLayerId": null,
  "gridSizePx": 48,
  "canEditCurrentLayer": true
}
```

### 5.4 `agas.ai.generate`（异步提交）

**目的**：等价于人手在 AI 面板点「生成」。

```json
{
  "sceneId": "scene-...",
  "layerId": "layer-...",
  "prompt": "可选；默认合并当前图层已启用的 prompt fragments",
  "profileId": "可选；省略时使用 AI 面板当前选中的 profile",
  "target": "NewImage | ReplaceSelected",
  "layerScope": "CurrentLayer | AllVisibleLayers | VisibleLayersExceptCurrent",
  "quality": "可选；low | medium，仅 grok-imagine-image-2.0"
}
```

冻结约束：

- v2 **不允许内联 selection / replacement / selectedImageId**；调用顺序固定为 `agas.selection.set` → `agas.ai.generate`，等价于用户先在画布框选/点选，再点击生成。  
- `agas.ai.generate` 始终使用当前画布选区，不提供 `useCurrentSelection` 开关。  
- `layerScope` 可选，默认 `CurrentLayer` 保持原有行为；`AllVisibleLayers` 使用所有可见层作为参考，`VisibleLayersExceptCurrent` 只使用当前层以外的可见层。该参数只控制参考像素，提示词仍由当前层片段与显式 prompt 合并；结果始终写入 `layerId` 指定的当前活动图层，参考层不被修改。
- `quality` 仅适用于 `grok-imagine-image-2.0`；省略时最终请求使用 `medium`，显式 `low`/`medium` 以小写写入 Images JSON。其他模型显式携带该字段会在服务商请求发送前拒绝。
- 跨层参考仍需显式设置 Q，不会自动按背景大小扩框；不可见层和透明度为 0 的图片不会成为参考像素。已有生图结果可留在当前层，使用 `VisibleLayersExceptCurrent` 时不会再次进入参考输入。
- 不接受调用方指定 `operation`；实际 `effectiveOperation` 由公共 Prepare bridge 按提交时冻结的 Q/W、目标与 patch capture 派生，避免与手工路径冲突。  
- 显式 `profileId` 等价于用户选择指定 profile；省略时必须通过 bridge 读取 AI 面板**当前选中项**，不能只读磁盘 `ActiveProfileId`。  
- 提交阶段在主线程原子捕获当前选区、选中图片与项目/场景/图层 fingerprint；提交后的 UI 操作不改变该 job 的输入快照。

行为：

1. 主线程校验：有打开项目、图层可编辑、Q/W 与选中图片满足当前 target 要求。  
2. 复用 `PrepareAiGenerationRequest` 同类逻辑生成 `AiGenerationRequest` + `CanvasPatchCapture`（Region 路径）。  
3. 分配 `jobId`，状态置 `Queued`，入队后台执行 `AiGenerationService.GenerateAsync`。  
4. **立即**返回：

```json
{
  "jobId": "aijob-...",
  "state": "Queued",
  "effectiveOperation": "RegionEdit",
  "createdAtUtc": "..."
}
```

不在同一次 tool call 内等待完整生成（避免与 v1 30s 排队/开始前超时冲突；生成本身可长达数分钟）。

### 5.5 `agas.ai.status`

```json
{
  "jobId": "aijob-..."   // 可选；省略则返回最近 N 条（默认 10，上限 50）
}
```

任务状态机：

```text
Queued -> Running -> Succeeded | Failed | Canceled
```

成功时返回：

- `resultRelativePath`（项目相对路径，生成图）  
- `previewRelativePath`（若有）  
- `effectiveOperation`  
- `message`（安全文案，不含 API Key）  
- **不**返回 base64  

失败时返回：

- `errorCode`（映射 `AiGenerationErrorCode` → `AGAS_AI_*`）  
- `message` / `hint`  

### 5.6 `agas.ai.cancel`

取消 `Queued`/`Running` 任务；幂等：已结束任务或同一任务重复取消均成功返回当前终态 + `canceled=false`，不返回 `AGAS_AI_JOB_NOT_RUNNING`。不存在的 jobId 才返回 `AGAS_AI_JOB_NOT_FOUND`。取消与完成竞态以 JobStore 中第一个成功写入的终态为准，终态不可反转。

### 5.7 `agas.ai.apply`

将 `Succeeded` 任务结果应用到画布：

```json
{
  "jobId": "aijob-..."
}
```

规则：

- 复用 `ApplyAiGeneratedImage` / `ReplaceImagePath` / `TryAddImageFromProjectRelativePath` 一类入口。  
- 应用后标记 scene dirty；**不自动 `project.save`**（与 v1 一致，由 Agent 显式 save）。  
- 若画布内容相对提交时 fingerprint 已变，返回 `AGAS_AI_CONTEXT_STALE`，要求 Agent 重新 generate 或确认强制应用（v2 **默认拒绝强制**，避免静默破坏）。
- apply 不接受 `mode`、`imageId`、`x/y`；应用位置固定为 job 提交时捕获的 W（未设置 W 时使用 Q），目标固定为提交时的 `target` 与选中图片 id，禁止调用时改目标或坐标，也禁止替换失败后静默降级为新增。  
- 每个 job 只产生一次画布变更。首次 apply 成功后保存结果；同一 job 重复调用幂等返回首次结果并标注 `alreadyApplied=true`。

---

## 6. 异步 AI 运行时

### 6.1 组件

```text
AgasMcpAiJobStore          // jobId -> 状态/结果元数据（进程内）
AgasAiGenerationCoordinator // UI/MCP 共用 active permits；MCP 有限队列；取消
AgasMcpAiJobScheduler      // MCP job 与共享 coordinator 协作
AgasMcpAiFacade            // 主线程通过公共 bridge 准备/应用；后台调用 AiGenerationService
```

冻结的并发与排队策略：

- UI 与 MCP 必须通过同一个 `AgasAiGenerationCoordinator` 共享全局 active permits 上限 **5**；不允许回退为各自独立计数。  
- UI 保持现有手工行为：`TryAcquire` 发现 5 个 permit 已占用时立即拒绝第 6 个请求，不进入队列。  
- 仅 MCP 使用 FIFO 有界队列，最多保留 **20** 个 `Queued` job；无空闲 permit 时排队，队列也满时返回 `AGAS_AI_BUSY`。释放 permit 后按 FIFO 启动 MCP job。
- UI 与 MCP 复用同一个 `IAgasAiGenerationWorkspaceBridge` 的 Prepare/Apply 逻辑；禁止复制第二套 Prepare/Apply。

### 6.2 与串行 executor 的关系

| 调用 | 是否占 serial tool 槽 |
|------|----------------------|
| `agas.ai.generate` 提交阶段 | 是（短：校验+入队） |
| 后台 GenerateAsync | 否（独立 job 槽） |
| `agas.ai.status` / `cancel` | 是（短） |
| `agas.ai.apply` | 是（短：主线程改 Resource） |

### 6.3 生命周期

- 项目关闭：取消该项目相关 Running/Queued jobs；清理 store 或标记 `Canceled`。  
- MCP stop / 应用退出：取消全部 jobs，等待 quiesce（与 v1 stop 收敛一致）。  
- jobs **不持久化**到磁盘；进程退出即丢失（可接受；结果文件若已写入项目目录仍保留）。
- 终态 job 在进程内最多保留最近 **100** 条或 **30 分钟**（任一条件先达到即清理）；Running/Queued 不因 TTL 清理。清理后查询返回 `AGAS_AI_JOB_NOT_FOUND`。

### 6.4 超时

- Provider 超时沿用 `AiGenerationService.DefaultTimeout`（10 分钟）。  
- tool call 自身仍受 v1 rate/queue 约束；status 轮询由 Agent 负责（建议客户端 1–2s 间隔）。

---

## 7. 安全与错误码

### 7.1 继续遵守 v1

- 仅 `127.0.0.1:54119`  
- 相对路径；拒绝 `../`、绝对路径、URL、`data:`、`file://`  
- 不返回绝对路径 / base64  
- 无 shell / 任意 FS  

### 7.2 新增错误码（契约冻结）

| Code | 含义 |
|------|------|
| `AGAS_SELECTION_INVALID` | 网格选区非法 |
| `AGAS_IMAGE_NOT_SELECTED` | 需要选中图片但没有 |
| `AGAS_LAYER_NOT_EDITABLE` | 图层锁定或不可见 |
| `AGAS_AI_UNSUPPORTED_OP` | Provider/配置不支持派生出的 effectiveOperation |
| `AGAS_AI_NO_PROFILE` | 无可用 AI profile / 未配置 |
| `AGAS_AI_JOB_NOT_FOUND` | jobId 不存在 |
| `AGAS_AI_JOB_NOT_SUCCEEDED` | apply 时任务未成功 |
| `AGAS_AI_CONTEXT_STALE` | 画布相对提交时已变化 |
| `AGAS_AI_BUSY` | 并发任务已满 |
| `AGAS_AI_PROVIDER_FAILED` | 上游生成失败（安全文案） |
| `AGAS_AI_CANCELED` | 已取消 |

### 7.3 密钥

- MCP **不得**读写或回显 API Key。  
- 仅使用用户已在 AGAS AI 设置中配置的 profile。

---

## 8. Agent 推荐剧本

### 8.1 区域生成（最接近「框选 + 发请求」）

```text
1. agas.project.snapshot
2. agas.scene.mutate        op=add / setActive   （按需）
3. agas.layer.mutate        op=add / setCurrent
4. agas.prompt.mutate       op=add
5. agas.selection.set       op=setRegions        （框选）
6. agas.ai.generate         （操作类型由 Q/W 与目标自动派生）
7. loop: agas.ai.status     until Succeeded/Failed
8. agas.ai.apply
9. agas.project.save
```

### 8.2 参考图编辑

```text
1. agas.image.mutate / assets.list 准备图
2. agas.selection.set op=setRegions      （先设置必需的 Q 生成区域；需要局部替换时同时设置 W）
3. agas.selection.set op=selectImage     （选中参考图；ReplaceSelected 时传对应 imageId/layerId）
4. agas.ai.generate                      （target=ReplaceSelected 或按目标使用 NewImage）
5. agas.ai.status → agas.ai.apply → agas.project.save
```

Q 不是可省略的内联参数：`agas.ai.generate` 不接收 selection，必须先用
`agas.selection.set op=setRegions` 写入当前画布 Q，再选参考图并提交生成。

---

## 9. 实现落点（已落地）

```text
src/Mcp/
  AgasMcpProjectFacade.cs          // 继续承载 v1；selection 可内聚或委托
  AgasMcpAiJobStore.cs             // job 状态、scope、TTL/容量清理、apply lease
  AgasMcpAiJobScheduler.cs         // 共享 permit、MCP 有界队列、取消与 quiesce
  AgasMcpAiJobUiSynchronizer.cs    // scheduler 事件转主线程；按项目 scope 同步 AI 面板
  AgasMcpAiFacade.cs               // 生成/状态/取消/应用与严格运行时契约校验
  Contracts/
    AgasMcpToolNames.cs            // 当前 v1 8 个 + v2 6 个 + Blueprint 2 个工具名
    AgasMcpToolInputs.cs
    AgasMcpToolSchemas.cs
    AgasMcpErrorCodes.cs
    AgasMcpDtos.cs                 // selection / ai job DTO
    Metadata/                      // AGAS 条件、closed object、unique/items 等 attributes
    Runtime/AgasMcpContractRegistry.cs // annotation -> schema / Validate / Bind 单一入口
  Tools/
    AgasSelectionTools.cs          // selection set/get
    AgasAiTools.cs                 // AI generate/status/cancel/apply
    AgasBlueprintTools.cs          // Blueprint capabilities/execute

  AgasMcpBlueprintFacade.cs        // Blueprint 能力发现与同步执行
  IAgasMcpBlueprintWorkspaceBridge.cs

src/Features/Ai/Generation/AgasAiGenerationCoordinator.cs // UI/MCP 共用 active permits
src/UI/MainWorkspace.cs           // 可复用的 Prepare/Apply bridge 与 stale 检查
src/Features/Canvas/CanvasWorkspace.cs  // 按 id 选中 / 按网格设选区 API

tests/Core/Mcp/
  AgasMcpSelectionToolTests.cs
  AgasMcpAiJobTests.cs
tests/Godot/
  McpSelectionAcceptance.tscn
  McpAiGenerationAcceptance.tscn   // 可用 fake runner，避免真实打 Provider
```

### 9.1 MainWorkspace 解耦结果

v2 保留 `MainWorkspace` 内既有业务逻辑，并通过公共 bridge 向 UI/MCP 提供同一入口，已完成：

- `IAgasAiGenerationWorkspaceBridge` 由 `MainWorkspace` 实现并注册到 `AgasMcpProjectContext`。
- UI 与 MCP 共用 Prepare/Apply、当前画布 context 和 profile 解析逻辑，没有复制第二套实现。
- MCP 省略 `profileId` 时通过 bridge 读取 AI 面板当前选中项，不绕过 UI 状态读取磁盘默认值。

最终实现未让 MCP 直接操作 `AiGenerationPanel` 控件；公共 bridge 与共享 coordinator 门禁已通过。

---

## 10. 测试与验收

### 10.1 单元 / Core

- selection set/get 正反向（非法网格、锁定/隐藏图层、跨 scene，且 snapshot 不含 selection）。  
- ai job 状态机、cancel 幂等、取消/完成竞态、UI/MCP 共享 active permits、UI 第 6 个即时拒绝、MCP 20 条有界队列及 busy 分支。  
- generate 的 effectiveOperation 派生、当前 UI profile 默认值，以及 apply 在 Succeeded / 非 Succeeded / stale fingerprint / 重复 job 幂等 / 冻结 W 与目标分支。  
- schema / tools/list 当前固定含 16 工具（含 Blueprint 2 个）。
- 16/16 生成 `inputSchema` 与 frozen schema 语义一致；Execute 与 public typed Facade handler 共用 annotation descriptor，覆盖 required/type/null/range/enum/pattern/unknown/case/conditions/default/unique。
- 路径与错误码不含绝对路径/密钥；status/list JSON 不含 base64、`data:image` 或大块原始图片数据。

### 10.2 Godot Acceptance

- `McpHttpRuntimeAcceptance` 启动真实 Streamable HTTP runtime，经 `tools/list` 验证当前 16 工具，并通过真实 `tools/call` 跑通 selection.set/get → ai.generate → ai.status → ai.apply → project.save → 重载确认；AI runner 使用可控 fake，测试不依赖第三方 Provider。
- selection 后 UI 黄/红框与 MCP get 一致。  
- 使用 **fake `IAiGenerationRunner`** 跑通 generate → status → apply → save。  
- 关闭/切换项目、stop MCP 时 job 被取消；重复 apply 不重复修改画布；任务上下文过期时拒绝应用。  
- 生产 `Main` 的 scheduler 终态事件无需调用 `agas.ai.status` 即可刷新 AI 面板；切换项目后旧 root + projectId scope 的事件不会进入新工作区。
- 测试结束后 `127.0.0.1:54119` 无残留监听。

### 10.3 非验收（本阶段不宣称）

- 真实第三方 Provider 稳定性（可作为手工 smoke，不作门禁）。  
- Blueprint 图编辑与异步 job（当前 capabilities / 同步 execute 已有 HTTP 验收）。
- 导出包 / Windows 实机（仍继承 v1 待验证项）。

---

## 11. 风险与决策

| 风险 | 缓解 |
|------|------|
| AI 耗时长 vs tool 超时 | 异步 job + status 轮询 |
| MCP 与 UI 同时生成打爆配额 | 共享并发预算 |
| 直接依赖 MainWorkspace 私有方法 | 先抽 bridge 接口 |
| snapshot 过大 | selection 固定使用独立 get；snapshot 不加入 selection |
| Agent 误 apply 过期结果 | fingerprint + `AGAS_AI_CONTEXT_STALE` |
| 工具数量膨胀 | v2 固定 6 新工具；Blueprint 另开 v3 |

### 11.1 已拍板决策（方案层）

1. **使用模型**：打开软件 + 开 MCP（非 headless）。  
2. **操作模型**：领域 API，非 UI 自动化。  
3. **AI 模型**：异步 job，不在单次 tools/call 内同步等完成。  
4. **保存语义**：apply 不自动 save；仍显式 `agas.project.save`。  
5. **密钥**：不经 MCP 暴露。  
6. **Blueprint**：不进 v2。
7. **选区读取**：`agas.selection.get` 独立，snapshot 不加入 selection。  
8. **AI 并发**：UI/MCP 共用 5 个 active permits；UI 满时即时拒绝，只有 MCP 使用有界队列 20。  
9. **生成输入**：不支持内联选区；固定 `selection.set` → `ai.generate`。  
10. **项目打开**：不进 v2；用户先在 UI 打开项目。  
11. **选中语义**：隐藏/锁定图层不能通过 MCP 选中，与当前手工画布一致。  
12. **复用要求**：UI/MCP 共用 AI Prepare/Apply bridge，禁止复制实现。

---

## 12. 成功标准（v2 Done）

1. **已完成**：文档、执行计划与代码收尾已由 `f416371`、`83e2358`、`814e23b`、`21bc853`、`68a6666` 及 annotation 契约提交链落库，并通过本节所列门禁。
2. **已完成**：Agent 仅通过 MCP（外加用户打开项目并启用 MCP）可完成：
   建场景/图层 → 设选区 → 提交 AI → 轮询 → 应用 → 保存。  
3. **已完成**：Selection 错误码兼容收口后 Core 563/563；本次定向 Godot Selection / HTTP 门禁 PASS；响应安全断言覆盖密钥、绝对路径、Base64 与大字段。
4. **已完成**：v2 里程碑交付时 tools/list 为 v1 8 个 + v2 6 个，共 14 个；当前运行时另含 Blueprint 2 个，共 16 个，v1 工具回归保持通过。

### 12.1 最终实现与验收证据（2026-07-25）

关键实现结果：

- Selection 使用独立 set/get 工具，snapshot 保持 v1 结构；AI generate 不接受内联选区或调用方指定 operation。
- AI 使用进程内异步 job、共享 UI/MCP active permits、MCP FIFO 有界队列；status/cancel/apply 按规范化项目 root + projectId 隔离。
- apply 使用冻结上下文、stale fingerprint、单次 apply lease 与幂等记录；活跃 lease 不被 TTL/容量清理，也不能旁路读取 application state。
- AI 工具运行时严格拒绝 schema 外字段，并校验 sceneId/layerId/profileId、jobId、limit、target 与长度/模式边界。
- 当前 16 个工具均使用 annotation 单一契约源：同一 registry 同时生成 frozen-compatible `inputSchema`、绑定 Execute arguments，并验证 direct typed Facade inputs；MCP SDK 不自动替项目执行该验证。
- 关闭/切换项目按完整 project scope 取消任务；MCP stop 会取消 Running/Queued jobs、等待 quiesce 并释放共享 permit。
- 继续保持 loopback、Host/Origin、1 MiB body、相对路径和安全输出边界；AI status/list 不返回 Base64、绝对路径或密钥。

提交链：

```text
84669b5  80267ee  3558feb  d6a705d  bef601e  01cc292
6f9c18f  3f152e8  74665ee  dbed5a9  1356b3a  f416371
83e2358  814e23b  21bc853  68a6666
fbe4c5f  c221f38  615c214  1f09027  29093ce  8892e17
a67c4ad
```

收尾提交与可核查证据：

- `f416371 test: verify MCP v2 workflow over HTTP`：`McpHttpRuntimeAcceptance` 使用真实 HTTP `tools/list` / `tools/call`，覆盖 14 工具、完整 v2 生成/轮询/应用/保存/重载链路，以及 Host/Origin、body limit、严格 schema 和安全响应。
- `83e2358 fix: close MCP AI lifecycle contract gaps`：拆分 provider 结果路径与 applied 路径，保持 terminal TTL，冻结严格 FIFO，并覆盖项目切换/关闭取消 Running + Queued job。
- `814e23b test: align canvas patch security acceptance`：使用真实 Q 选区执行 Canvas patch 安全验收，并覆盖 traversal/绝对路径、图片 symlink、父目录 symlink 和 scoped generated directory symlink 拒绝。
- `21bc853 fix: synchronize MCP AI jobs with editor UI`：生产 `Main` 装配 job UI synchronizer；后台终态与 active count 无需 `ai.status` 轮询即可更新；root + projectId scope 过滤阻止旧项目事件污染新工作区；WM-close 验收等待真实 Main shutdown 完成后再恢复用户文件。
- `68a6666 fix: enforce strict MCP tool arguments`：14 个工具运行时按 schema 精确拒绝未知字段和大小写别名；`project.save` / `selection.get` 不再忽略非空未知 arguments；Selection 的 Q/W region 同样执行嵌套 `additionalProperties:false`；Core Execute 与真实 HTTP 均覆盖 14 unknown + 12 case-alias + 4 nested-region 反向矩阵。
- `fbe4c5f feat: add annotation-driven MCP contracts`：引入 BCL DataAnnotations + AGAS attributes 与 contract descriptor/registry。
- `c221f38 fix: harden annotation-driven MCP contracts`：补强条件、默认值、嵌套对象与严格验证边界。
- `615c214 fix: reject unbindable MCP contract properties`：构建 descriptor 时拒绝无法可靠绑定的契约属性。
- `1f09027 feat: generate MCP schemas from annotated inputs`：14 个 tools/list `inputSchema` 改由同一 annotation descriptor 生成；14/14 与 frozen schema 语义一致。
- `29093ce refactor: bind MCP tool inputs from contracts`：14 个 Execute 入口统一通过 registry `Validate + Bind`，删除旧 shape validator 与 AI 手写 wire validator。
- `8892e17 fix: validate typed MCP inputs from contracts`：所有 public typed Facade handlers 同样走 annotation 验证；保留路径、blank、选区 endpoint、项目状态与 AI job 等业务语义。
- `a67c4ad fix: preserve MCP selection range errors`：仅在 Selection Facade 边界把 selection/replacement 数值范围错误兼容映射为 `AGAS_SELECTION_INVALID`；缺字段、type/null、unknown/case 仍返回 `AGAS_INVALID_OPERATION`，Execute 与 public typed `SelectionSet` 一致。

最终门禁证据：

- `dotnet build APP-AGAS-CS.sln -c Debug`：0 warning / 0 error。
- Core：563/563 PASS（Selection 错误码兼容收口后）。
- 之前已验证的 11 个 Godot 定向门禁全部 PASS：`McpSelectionAcceptance`、`McpAiGenerationAcceptance`、`McpHttpRuntimeAcceptance`、`McpResourceRuntimeAcceptance`、`McpSettingsUiAcceptance`、`AiGenerationPanelAcceptance`、`AiGenerationWorkspaceIntegrationAcceptance`、`AiSettingsUiAcceptance`、`CanvasAiContextMenuAcceptance`、`CanvasPatchSecurityAcceptance`、`CanvasRegionAndReplaceAcceptance`；`a67c4ad` 后本次只重跑并通过 `McpSelectionAcceptance` 与 `McpHttpRuntimeAcceptance`，不将其表述为新一轮 11 项全量结果。
- `McpHttpRuntimeAcceptance` 日志：`[AGAS-H2][MCP-HTTP-RUNTIME][PASS] Godot headless real MCP HTTP E2E acceptance completed.`；这是真实 HTTP runtime/tool call 证据，不宣称真实第三方 Provider 稳定性。
- `McpSettingsUiAcceptance` 覆盖生产 `Main` no-poll 终态刷新、跨项目 dispatcher scope 隔离、真实 WM-close shutdown、端口释放，以及 settings/recent-projects 备份恢复后逐字节验证。
- 用户文件隔离：测试前后 settings SHA-256 均为 `3639638874f5c93f680e1e7952d64463f0e34d1fc11e5bf0e4dd920c1835e340`；recent-projects SHA-256 均为 `c79e50b7e24dbfc173184ef310adcecbd5f63b24b2460d7d45b22cadcd35ff52`。
- 门禁结束后 `lsof -nP -iTCP:54119 -sTCP:LISTEN` 无监听。
- 后续全量最终门禁命令与结果登记位置固定为配套执行计划 §4、§7；本方案的汇总证据继续维护在本节。

---

## 13. 后续 MCP 能力扩展

本方案只维护 v2 Selection / AI Agent Ops 与当前 16 工具的已交付历史基线，并作为后续能力缺口的索引入口。扩展开发采用以下单一来源分工：

- [`agas-mcp-capability-expansion-technical-design.md`](agas-mcp-capability-expansion-technical-design.md) — 契约、架构与安全边界的 SSOT。
- [`agas-mcp-capability-expansion-execution-plan.md`](../exec-plans/active/agas-mcp-capability-expansion-execution-plan.md) — 开发波次、文件 owner、依赖、测试命令与验收记录的 SSOT。

当前优先级摘要：

- **P0**：补齐 AI Profile / Model 的发现、读取当前项、切换与持久化闭环；`agas.ai.generate.profileId` 继续仅表示单次生成覆盖。
- **P1**：项目 recent/open/create/close 生命周期、未缓存场景 `scene.get`、图片与场景包 Import / Export、AI 安全诊断、Grid mutate、Prompt role、全工具 typed DTO + `outputSchema`。

当前扩展范围明确不包含：

- 图片 preview URL / Base64 返回；当前继续使用受控项目相对路径与轻量元数据。
- MCP Undo / Redo；继续沿用既定 UI 历史策略，不新增 `history` 工具。

若本摘要与扩展文档不一致：工具契约、架构和安全语义以扩展技术方案为准；开发顺序、文件 owner、测试命令和验收状态以 active execution plan 为准。本节不再复制详细接口、字段表或测试矩阵，避免双处维护造成漂移。

---

## 14. 参考

- [`agas-mcp-capability-expansion-technical-design.md`](agas-mcp-capability-expansion-technical-design.md) — 后续 MCP 能力扩展的契约、架构与安全边界 SSOT。
- [`agas-mcp-capability-expansion-execution-plan.md`](../exec-plans/active/agas-mcp-capability-expansion-execution-plan.md) — 后续能力扩展的波次、owner、测试与验收 SSOT。
- `docs/design-docs/agas-mcp-technical-design.md` — MCP v1 协议、安全、8 工具、脏状态。  
- `docs/exec-plans/active/agas-mcp-execution-plan.md` — v1 执行与 Gate 2。  
- `src/Mcp/AgasMcpProjectFacade.cs` — 现有 mutate 实现。  
- `src/Features/Canvas/CanvasWorkspace.cs` — 选区与选中。  
- `src/Features/Ai/Generation/*`、`src/UI/MainWorkspace.cs` — AI 准备与应用。  
- `src/Features/Ai/UI/AiGenerationPanel.cs` — UI 生成并发与准备 DTO。
