# AGAS MCP 能力扩展技术方案

> 历史设计：本文的 Blueprint MCP 接口及 `AGAS_BLUEPRINT_BUSY` 已移除，不是当前契约。现行 MCP 工具清单见 [`AgasMcpToolNames.cs`](../../src/Mcp/Contracts/AgasMcpToolNames.cs)；图片工具箱由 [`src/Features/Tools`](../../src/Features/Tools) 实现，目前不作为 Blueprint MCP 接口暴露。

> 状态：历史方案，仅供追溯，不代表现行实现
> 日期：2026-07-31
> 基线：Godot 4.6.1 Mono / .NET 8 / C# / MCP Streamable HTTP
> 关联文档：[Active execution plan](../exec-plans/active/agas-mcp-capability-expansion-execution-plan.md) · [MCP v2 gap 设计](./agas-mcp-v2-agent-ops-technical-design.md) · [MCP 基线设计](./agas-mcp-technical-design.md)

## 1. 背景

当前 MCP 已覆盖项目内场景、图层、图片、提示词、视图、保存、选区、AI 生成任务和 Blueprint 执行，但 Agent 仍不能完成以下高频闭环：

1. 发现、切换并持久化生图 Profile/Model；
2. 自主创建、打开和关闭受控项目；
3. 读取未打开场景的完整轻量详情；
4. 从受控入口导入图片，交换场景包；
5. 修改网格和 Prompt role；
6. 通过 `outputSchema` 确定所有工具的结构化响应；
7. 在 AI 失败时获得不泄密、可操作的诊断信息。

本方案只扩展领域 API，不引入鼠标/键盘模拟，也不放宽 loopback、文件路径和凭据边界。

## 2. 已验证现状

### 2.1 已验证事实

- 当前正式工具为 16 个，单一名称清单位于 `src/Mcp/Contracts/AgasMcpToolNames.cs`。
- 输入契约由 DataAnnotations + AGAS contract attributes 生成，`AgasMcpContractRegistry` 同时负责 schema、绑定和实例验证。
- 当前工具的 `OutputSchema` 均为空，成功数据多数由匿名对象构造。
- `AgasAiProfile` 已持久化 `ProviderId`、`ModelName`、`BaseUrl`、`ApiKey`；全部保存在 `$AGAS_HOME/image-generation-profiles.json`。
- AI 面板已支持 Profile 选择、Provider/Model 切换和持久化；显式 `agas.ai.generate.profileId` 只是单次覆盖，不改变当前项。
- Provider/Model preset 来自 `$AGAS_HOME/providers/*.json`；模型名允许中转站自定义 alias，preset 不是封闭枚举。
- `AiGenerationException` 已携带经过 allow-list 脱敏的 `AiGenerationSafeDiagnostic`，但 `AgasMcpAiFacade.MapRunnerFailure` 会将其降级成通用 job 错误。
- AI job 当前没有保存安全的执行 Profile/Provider/Model 身份，`ai.status` 无法证明实际使用的模型。
- `RecentProjectsStore` 已维护最多 20 条最近项目，但资源内含绝对路径，不能原样返回 MCP。
- `Main` 已集中处理 picker/workspace 切换、打开初始化、最近项目记录和旧项目 AI job 取消。
- `ProjectMutationService` 已实现 `MutateGrid(SetSize/SetSnapEnabled/SetVisible)`；UI 网格尺寸范围为 1–4096。
- `PromptFragmentResource.Role` 已存在，枚举为 `General`、`Environment`、`NegativeConstraint`；Application 的 Prompt update 已能写 role，但 MCP 输入和输出尚未暴露。
- `ScenePackageStore` 已支持场景包导入导出；当前包只含图层、Prompt 和图层参考图，不含普通画布图片或相机状态。
- 当前 `ScenePackageStore` 接受任意本机路径、导出会先删目标、导入会边校验边复制，因此不能直接暴露给 MCP。

### 2.2 待验证项

- 开发时需验证所有目标平台对 symlink/reparse point 的检测实现，尤其是 macOS 目录链和 Windows junction。
- 开发时需用当前 MCP SDK 1.4.1 验证 `Tool.OutputSchema` 发布后的客户端兼容性。
- 本方案不宣称第三方 AI Provider 可用性；网络诊断只验证安全分类，不验证外部服务稳定性。

### 2.3 基于证据的推断

- Profile 是 Provider、Model、BaseUrl 和 credential 的执行边界，因此“切模型”应修改当前 Profile 的 `ModelName`，跨 Provider 切换应激活另一 Profile，而不是让 MCP 猜测并重写 BaseUrl。
- 生命周期必须由 `Main` 的窄 bridge 统一协调；绕过 `Main` 直接替换 `ProjectSession` 会漏掉 UI、dirty、AI job 和 Blueprint gate。
- 导入必须经过受控 inbox/exchange 的 list-issued、签名且过期的 opaque `sourceRef`；接受任意绝对路径或让调用方猜文件名会破坏 MCP 路径边界。

## 3. 目标与非目标

### 3.1 目标

1. Agent 能完成 Profile/Model 发现、切换、持久化、生成和诊断闭环。
2. Agent 能通过 opaque `projectRef` 安全操作最近项目，并在固定托管目录创建项目。
3. Agent 能读取任意已索引场景，不改变 active scene、缓存、dirty 或 UI 临时状态。
4. Agent 能分页发现固定 inbox/exchange 中的轻量导入项，通过 opaque `sourceRef` 导入图片和场景包，并把场景包原子发布到全局 exchange 供另一已打开项目直接导入。
5. 网格与 Prompt role 复用 Application mutation service。
6. 最终 31 个工具全部使用 typed input/output DTO，并发布可验证的 `outputSchema`。
7. 所有新写操作都有明确 dirty、保存、原子性、并发和回滚语义。

### 3.2 非目标

- 不提供图片预览 URL、Base64、`data:` URI 或图片列表原图回退。
- 不提供 MCP undo/redo。
- 不提供任意文件系统路径打开、创建、导入或导出。
- 不通过 MCP 创建、读取、修改或删除 API Key。
- 不把 `agas.ai.diagnostics` 变成 Provider 网络探针或任意 HTTP 代理。
- 不扩展场景包格式以包含普通画布图片、相机状态或 Blueprint。
- 不实现通用事务批处理、通用 revision 或 Blueprint 图编辑。

## 4. 最终工具清单

本期新增 15 个工具，扩展 1 个既有工具；总数从 16 增至 31。

| 类别 | 工具 | 行为 |
|---|---|---|
| 既有 | 原 16 个工具 | 名称和既有必填字段保持兼容 |
| AI | `agas.ai.profiles.list` | 全局 Profile 发现，可无项目调用 |
| AI | `agas.ai.models.list` | 全局 Provider/Model preset 发现，可无项目调用 |
| AI | `agas.ai.profile.activate` | 激活并持久化 Profile |
| AI | `agas.ai.model.set` | 修改当前 Profile 模型并持久化 |
| AI | `agas.ai.diagnostics` | 配置预检；运行期诊断仍随 `ai.status` 返回 |
| Lifecycle | `agas.projects.recent` | 返回 opaque projectRef，不返回路径 |
| Lifecycle | `agas.project.open` | 仅打开 recent 返回的 projectRef |
| Lifecycle | `agas.project.create` | 仅按 name 在固定托管根创建并打开 |
| Lifecycle | `agas.project.close` | 按显式 dirty/AI job 策略关闭 |
| Read | `agas.scene.get` | 分页读取任意已索引场景详情 |
| Import | `agas.imports.list` | 分页列出受控 inbox/exchange 的轻量导入项并签发 expiring `sourceRef` |
| Import | `agas.assets.import` | 从固定图片 inbox 导入项目 |
| Package | `agas.scene.package.import` | 从固定场景包 inbox 原子导入 |
| Package | `agas.scene.package.export` | 原子发布到 `$AGAS_HOME/import-exchange/scene-packages` 并返回 opaque ref |
| Grid | `agas.grid.mutate` | 修改 size/snap/visible |
| Prompt | `agas.prompt.mutate` | 既有工具增加 `role` |

## 5. 输入契约

所有 schema 使用 JSON Schema 2020-12、严格大小写、`additionalProperties:false`。下列公共 pattern 固定为：

```text
safeId      = ^[A-Za-z0-9_-]+$          // 1-120
projectRef  = ^projectref-[a-f0-9]{32}$
sceneCursor = ^scncur-[a-f0-9]{32}$
packageRef  = ^[A-Za-z0-9][A-Za-z0-9_-]{0,119}$
```

显式传入 `null` 不等价于省略，必须拒绝。

### 5.1 AI Profile/Model

#### `agas.ai.profiles.list`

```json
{"type":"object","properties":{},"additionalProperties":false}
```

成功数据：

```json
{
  "activeProfileId": "profile-1",
  "effectiveCurrent": {
    "profileId": "profile-1",
    "providerId": "gemini-compatible",
    "adapterKind": "gemini-compatible",
    "modelName": "gemini-3-pro-image-preview",
    "source": "liveUi"
  },
  "profiles": [
    {
      "profileId": "profile-1",
      "name": "Relay",
      "providerId": "gemini-compatible",
      "providerDisplayName": "Nano Banana / Gemini Compatible",
      "modelName": "gemini-3-pro-image-preview",
      "credentialConfigured": true,
      "providerAvailable": true,
      "capabilities": ["textToImage", "referenceEdit", "regionEdit"]
    }
  ],
  "providerConfigurationErrorCount": 0
}
```

`effectiveCurrent.source` 仅允许 `liveUi|persisted`。有工作区时必须反映下一次省略 `profileId` 的真实 UI 选择；无项目时回退持久化状态。

#### `agas.ai.models.list`

```json
{"type":"object","properties":{},"additionalProperties":false}
```

返回全部有效 Provider：`providerId`、`displayName`、`adapterKind`、`defaultModelName`、`capabilities`、`supportsCustomModel:true` 和 `models[{modelName,label,recommended}]`。无效/重复 Provider 只返回计数，不返回配置文件路径或原始异常。

#### `agas.ai.profile.activate`

```json
{
  "type":"object",
  "required":["profileId"],
  "properties":{"profileId":{"type":"string","minLength":1,"maxLength":120,"pattern":"^[A-Za-z0-9_-]+$"}},
  "additionalProperties":false
}
```

写入前 fresh-load profiles，只修改 `ActiveProfileId`。成功返回安全的 `profileId/providerId/modelName/credentialConfigured/persisted/uiSynchronized`。运行中的 job 不受影响。

#### `agas.ai.model.set`

```json
{
  "type":"object",
  "required":["modelName"],
  "properties":{"modelName":{"type":"string","minLength":1,"maxLength":200}},
  "additionalProperties":false
}
```

运行时额外要求 `modelName == modelName.Trim()` 且不含 U+0000–U+001F/U+007F。它只修改当前 active Profile 的 `ModelName`，保留 ProfileId、Name、ProviderId、BaseUrl 和 credential。模型不在 preset 时仍成功，返回 `knownPreset:false`。

#### `agas.ai.diagnostics`

```json
{
  "type":"object",
  "properties":{"profileId":{"type":"string","minLength":1,"maxLength":120,"pattern":"^[A-Za-z0-9_-]+$"}},
  "additionalProperties":false
}
```

省略 `profileId` 时检查 effective current。它只做本地配置预检：

```json
{
  "profileId":"profile-1",
  "providerId":"gemini-compatible",
  "modelName":"custom-alias",
  "credentialConfigured":false,
  "providerAvailable":true,
  "ready":false,
  "networkChecked":false,
  "capabilities":["textToImage"],
  "issues":[{"code":"AGAS_AI_CREDENTIAL_MISSING","message":"...","suggestion":"..."}]
}
```

### 5.2 项目生命周期

#### `agas.projects.recent`

```json
{
  "type":"object",
  "properties":{"limit":{"type":"integer","minimum":1,"maximum":20,"default":20}},
  "additionalProperties":false
}
```

返回 `projects[{projectRef,name,lastOpenedUnixTime,available,isOpen}]`。不返回绝对路径、相对路径或可逆路径 hash。

#### `agas.project.open`

```json
{
  "type":"object",
  "required":["projectRef"],
  "properties":{"projectRef":{"type":"string","pattern":"^projectref-[a-f0-9]{32}$"}},
  "additionalProperties":false
}
```

`projectRef` 必须由本进程最近一次 `agas.projects.recent` 签发且仍对应当前 recent entry。此工具不接受 `path`、`root`、URL 或文件名。`project.open` 只允许在“无项目打开”状态调用；无论 `projectRef` 是否指向当前项目，只要已有项目打开都返回 `AGAS_PROJECT_ALREADY_OPEN`，要求先调用 `project.close`。

#### `agas.project.create`

```json
{
  "type":"object",
  "required":["name"],
  "properties":{"name":{"type":"string","minLength":1,"maxLength":80}},
  "additionalProperties":false
}
```

本期只接受 `name`。不接受任何目录、父目录、模板路径或覆盖开关。它与 `project.open` 一样只允许在“无项目打开”状态调用；已有任意项目均返回 `AGAS_PROJECT_ALREADY_OPEN`。

#### `agas.project.close`

```json
{
  "type":"object",
  "properties":{
    "saveIfDirty":{"type":"boolean","default":false},
    "cancelAiJobs":{"type":"boolean","default":false}
  },
  "additionalProperties":false
}
```

- dirty 且 `saveIfDirty=false`：返回 `AGAS_PROJECT_DIRTY`；
- dirty 且 `saveIfDirty=true`：调用现有 controlled save；只在保存成功且主线程复核为 clean 后继续关闭；保存失败返回 `AGAS_SAVE_FAILED` 并保持项目、UI 和上下文不变；
- 本期不提供 discard 路径，不允许通过 close 丢弃内存变更；
- 有 Queued/Running AI job 且 `cancelAiJobs=false`：返回 `AGAS_AI_JOBS_ACTIVE`；
- `cancelAiJobs=true`：按当前完整 project scope 取消后关闭；
- 任意 Blueprint session 正在执行或保存输出：始终返回 `AGAS_BLUEPRINT_BUSY`，本期不提供强制中断。

`cancelAiJobs=true` 不是“发出取消后立即切项目”。Lifecycle facade 必须异步等待新增的 project-scoped quiescence；在限定时间内未完全释放 runner/permit 时返回 `AGAS_TIMEOUT` 并保持原项目打开。不得在 Godot 主线程同步阻塞等待。

所有 close success/domain failure/internal fail-closed data 都含 immutable outcome receipt 的 `projectStillOpen/saved/aiCancellationRequested/saveState`；`saveState` 仅为 `none|partial|complete`。

### 5.3 Scene read

#### `agas.scene.get`

```json
{
  "type":"object",
  "required":["sceneId"],
  "properties":{
    "sceneId":{"type":"string","minLength":1,"maxLength":120,"pattern":"^[A-Za-z0-9_-]+$"},
    "cursor":{"type":"string","pattern":"^scncur-[a-f0-9]{32}$"},
    "limit":{"type":"integer","minimum":1,"maximum":200,"default":100}
  },
  "additionalProperties":false
}
```

输出含场景头 `sceneId/name/activeLayerId/viewState` 和按稳定顺序分页的 discriminated `items`：

```text
layer(order,id) -> image(order,id) -> prompt(order,id)
```

item.kind 为 `layer|image|prompt`；Prompt DTO 必含 `role`。有后续页时返回 opaque `nextCursor`，无后续页时省略该字段，不输出 JSON `null`。

读取来源固定如下：

- 已加载场景：在主线程从 live Resource 捕获 immutable scalar snapshot，必须包含尚未保存的内存修改；
- 未加载场景：在主线程调用 `ResourceLoader`/repository 的 `CacheMode.Ignore` fresh read，并在同一主线程阶段把所有 Godot Resource 投影成 immutable scalar snapshot；禁止读取或写入 session cache；
- 两种来源都禁止调用 `CacheScene`、`ChangeCurrentScene`，禁止改变 active、dirty、selection 或 UI。

Godot `ResourceLoader`、Resource 属性遍历和 Resource→DTO 投影不得放到 worker。`AsyncOrchestrated` 的后台阶段只允许处理已经完全脱离 Godot Object/Resource 的 scalar snapshot，例如 UTF-8 budget 估算、稳定排序、分页切片和 cursor broker 预算预留。

分页由 `AgasMcpSceneCursorBroker` 保存 immutable scalar snapshot，而不是后续页重新读取 live/disk。每个 snapshot 最多 10,000 items、UTF-8 估算总量最多 8 MiB、单 item 最多 256 KiB；全 broker snapshot payload 最多 64 MiB，且 TTL 固定 5 分钟、全进程最多 128 个快照、单项目最多 32 个。先校验单 item/单 snapshot，再在 broker gate 内原子预留全局预算；超限返回稳定 `AGAS_SCENE_SNAPSHOT_TOO_LARGE`（单 item/snapshot）或 `AGAS_CURSOR_CAPACITY`（broker 全局预算），不得建立 cursor。broker 以创建时的 project scope、sceneId 和稳定排序键绑定 token；正在处理 page 的 snapshot 必须 pin。容量回收顺序固定为：①清理全部已过期且未 pin 项；②仍超限时按 `createdAt`、再 token ordinal 淘汰最旧未 pin 项；③仍无法满足 snapshot 数量/单项目数量/64 MiB payload 时返回 `AGAS_CURSOR_CAPACITY`。任何阶段都不得淘汰 pinned item。项目关闭或切换在 scope 无 pin 后清除并释放预算；有 pin 时标记 revoked，最后一个 pin 释放时删除。过期、被淘汰、revoked、跨场景或跨项目 cursor 返回 `AGAS_CURSOR_INVALID`。稳定排序固定为类型序 `layer -> image -> prompt`，再按 `order` 升序、`id` ordinal 升序打破并列。

### 5.4 受控图片导入

固定 inbox：

```text
$AGAS_HOME/import-inbox/assets/
```

#### `agas.imports.list`

输入支持 `kind=asset|scenePackage`、`limit=1..200`、`offset>=0`。输出仅含 `items/count/totalCount/hasMore/nextOffset?`；每项固定为 `kind/name/sizeBytes/mediaType/validationState/sourceRef`。`sourceRef` 是 process-local 签名、不可猜、默认 15 分钟过期的 capability，不编码或回显路径。broker 全进程最多保留 4096 个 active ref：先清过期，再按签发序淘汰最旧项；被淘汰/到期 token 必须拒绝。列表禁止返回绝对路径、Base64、manifest、graph 或大 metadata；validation state 仅为轻量 `unverified|invalid`，完整解码/manifest 校验仍在 import 时执行。

#### `agas.assets.import`

```json
{
  "type":"object",
  "required":["sourceRef"],
  "properties":{"sourceRef":{"type":"string","pattern":"^src1-[a-f0-9]{32}-[a-f0-9]{64}$"}},
  "additionalProperties":false
}
```

`sourceRef` 必须由当前 broker 的 `agas.imports.list` 签发、未过期且 kind 为 asset。目标固定为 `assets/imported/<uuid>-<sanitized-name>`。成功返回 `relativePath/extension/sizeBytes/pixelWidth/pixelHeight`，不自动向图层添加实例。

限制：单文件不超过 32 MiB；宽高各不超过 8192；解码后像素不超过 64M；签发时与 import 开始/复制结束均验证 native identity；source、inbox、目标祖先中任一 symlink/reparse point 均拒绝。禁止绝对路径、URL、data URI、Base64。operation-owned marker/payload slot 必须在首次写入前登记；故障、取消和 commit gate 拒绝都在 `finally` 仅清理 native identity 仍匹配的本 operation 临时文件。

### 5.5 场景包导入/导出

受控来源根：

```text
$AGAS_HOME/import-inbox/scene-packages/<packageRef>/scene.json
$AGAS_HOME/import-exchange/scene-packages/<packageRef>/scene.json
```

#### `agas.scene.package.import`

```json
{
  "type":"object",
  "required":["sourceRef"],
  "properties":{"sourceRef":{"type":"string","pattern":"^src1-[a-f0-9]{32}-[a-f0-9]{64}$"}},
  "additionalProperties":false
}
```

`sourceRef` 必须由 list 或 exchange export 签发、未过期且 kind 为 scenePackage；import 开始与各 source 复制结束都复核 package/manifest/file native identity。成功返回 `sceneId/name/copiedAssetRelativePaths/dirtySceneIds`。导入创建新 sceneId/layerId/imageId，不能覆盖包内 ID 到现有项目对象。

#### `agas.scene.package.export`

```json
{
  "type":"object",
  "required":["sceneId"],
  "properties":{"sceneId":{"type":"string","minLength":1,"maxLength":120,"pattern":"^[A-Za-z0-9_-]+$"}},
  "additionalProperties":false
}
```

成功返回 `packageRef/sourceRef/referenceImageCount`。staging 与 final 都位于同一受控全局根 `$AGAS_HOME/import-exchange/scene-packages/`，以 directory rename 原子发布；每次生成新 packageRef，不覆盖旧包，不返回项目相对 manifest path或绝对路径。返回的 `sourceRef` 可由同一应用中随后打开的另一项目直接导入，无需人工移动目录。

包限制固定为：manifest 1 MiB、JSON 深度 64、256 layers、4096 prompts、512 reference images、单图不超过 32 MiB、manifest 与全部引用文件的总 payload 合计不超过 128 MiB；所有文件必须处于固定 references 层级且无 symlink/reparse。普通画布图片、相机、Blueprint 不进入包。

`assets.import`、`scene.package.import`、`scene.package.export` 保持一次 `tools/call` 等待最终成功/失败的同步协议体验，但 descriptor timeout 固定为 120 秒；其余 28 个工具为 30 秒。三个重 IO 工具使用全链 `CancellationToken`，超时等价于取消，不允许后台继续提交。所有 copy destination 使用 `CreateNew` 受控临时文件，先登记 operation-owned slot 再写；写中 fault/cancel 走局部 finally 撤销 slot，外层 finally/reconciler 清理，禁止留下未登记 partial file。

### 5.6 Grid mutate

#### `agas.grid.mutate`

```json
{
  "type":"object",
  "required":["op"],
  "properties":{
    "op":{"type":"string","enum":["setSize","setSnapEnabled","setVisible"]},
    "sizePx":{"type":"integer","minimum":1,"maximum":4096},
    "enabled":{"type":"boolean"}
  },
  "allOf":[
    {"if":{"properties":{"op":{"const":"setSize"}}},"then":{"required":["sizePx"]}},
    {"if":{"properties":{"op":{"enum":["setSnapEnabled","setVisible"]}}},"then":{"required":["enabled"]}}
  ],
  "additionalProperties":false
}
```

运行时拒绝与 op 无关的字段。复用 `ProjectMutationService.MutateGrid`；修改 project dirty，刷新 grid/canvas/toolbar，不自动保存。

### 5.7 Prompt role

既有 `agas.prompt.mutate` 增加：

```json
"role":{"type":"string","enum":["general","environment","negativeConstraint"]}
```

规则：

- `add`：`text` 仍必填，`role` 可选，默认 `general`；
- `update`：`fragmentId` 必填，`text|role` 至少一个；不再强制必须有 `text`；
- 其他 op 出现 `role` 时拒绝；
- snapshot、scene.get 和 mutate 响应的 Prompt DTO 均增加 `role`。

## 6. AI 执行身份与安全诊断

### 6.1 Job execution identity

新增纯安全 DTO：

```csharp
public sealed record AgasMcpAiExecutionIdentity(
    string ProfileId,
    string ProviderId,
    string AdapterKind,
    string ModelName);
```

它在 `agas.ai.generate` 主线程解析 effective profile 后立即冻结，随 `AgasMcpAiJobSubmission` 进入 `CreateQueued`，并存入 job entry/snapshot。禁止存储 `ApiKey`、BaseUri 或 credential-bearing `AiGenerationProviderProfile`。

`ai.generate` 初始响应以及 `ai.status` 的 Queued/Running/Succeeded/Failed/Canceled 五态都返回相同的 `execution`；已 apply 的 job 仍是 Succeeded，并以 `alreadyApplied:true` 和 applied metadata 表达。切换模型后，旧 job 的 identity 不变，新 job 使用新值。status 禁止从当前 settings 反查。

现有 `AiGenerationProviderProfile` 需要区分 `ProviderId` 和 `AdapterKind`；第三方 Provider 的二者可能不同，不能用 adapterKind 冒充 providerId。实现必须完成以下全链改造，不允许只改 MCP DTO：

```text
AgasAiProfile.ProviderId
 -> AiGenerationProfileController.ResolveProviderProfile
 -> AiGenerationProviderProfile.ProviderId + AdapterKind
 -> runner request/submission
 -> AgasMcpAiJobSubmission.Execution
 -> job entry/snapshot
 -> generate/status typed output
```

Provider factory 仍按 `AdapterKind` 选择实现，配置发现、用户身份和审计输出使用 `ProviderId`。合同测试必须使用 `providerId != adapterKind` 的第三方 Provider，证明两者未被覆盖或串写。

### 6.2 运行期诊断

`AgasMcpAiJobFailure` 增加 MCP-safe diagnostic：

```json
{
  "diagnosticId":"...",
  "errorCode":"ProviderRequestFailed",
  "phase":"ProviderResponse",
  "category":"ProviderRejected",
  "httpStatus":401,
  "upstreamCode":"UNAUTHENTICATED"
}
```

只允许上述字段。MCP 不返回 exception type、inner exception、stack、完整 URL、Endpoint path/query、header、body、prompt、图片内容或日志文件。`MapRunnerFailure(AiGenerationException)` 直接映射现有 `exception.Diagnostic`；未知异常继续使用保守通用失败。

所有 AI 对外字符串必须经过 credential-aware safe projector，但投影时机不能延后到 `ai.status`：

- Profile/Provider/model list 与本地 diagnostics 在构造响应时使用本次 fresh-load 的 credential 集合投影；
- `ai.generate` 在提交时使用本次实际执行 credential 投影 execution identity，job store 只保存 safe identity；若 model alias/Profile/Provider label 包含完整 credential，generate 必须拒绝或做类型感知整字段替换：safeId 字段用 `redacted`，自由文本用 `[redacted]`，不得保存原值；
- runner failure 映射时使用该次 provider request 的 credential 投影 allow-list diagnostic，job store 只保存 safe diagnostic；
- credential 后续 rotate/clear 时，`ai.status` 直接返回 job 内已冻结的 safe identity/diagnostic，不重新读取当前 credential，也不保存旧 credential；
- `model.set` 若新 alias 包含任一当前已存 credential，必须返回 `AGAS_AI_MODEL_INVALID`，不得把 secret 写入 profile 文档。

projector 只在当前调用栈内做 ordinal containment 比较，不把 credential 加入 DTO、exception、job、diagnostic 或日志；Profile name/model、Provider/model label、execution identity、diagnostic 的任一用户/Provider 来源字符串命中 secret 时不得原样输出。projector 之后再运行现有 allow-list 脱敏。测试必须覆盖提交后 rotate/clear credential 再查询 status，保证零原文泄漏。

### 6.3 Profile 设置冲突

`AgasAiSettingsStore._gate` 当前是实例级锁；Panel、SettingsDialog 和 MCP 都可能持有整份 profiles 快照。不能用“调用前 fresh-load”代替原子 RMW，冻结实现如下：

1. 按 canonical `AGAS_HOME` settings root 使用**同进程共享** keyed RMW gate；同一根下所有 Store 实例必须进入同一 gate；它只保证 AGAS 进程内无 lost update，不声称对非协作外部进程提供 CAS；
2. 在 gate 内 fresh-load profiles/credentials，计算包含文件 identity、length、mtime 与内容 hash 的 persisted fingerprint，做字段级 merge，再写同目录临时文件并原子 replace；replace 前 fingerprint 复核只用于 best-effort 发现外部变化，命中时返回 `AGAS_AI_SETTINGS_CONFLICT`，不得声称消除了 compare/replace 间的跨进程 TOCTOU；
3. MCP `profile.activate` 只合并 `ActiveProfileId`；`model.set` 只合并目标 Profile 的 `ModelName`；两者不得用 stale document 整份覆盖；
4. persisted fingerprint 单独覆盖文档 resourceVersion、ActiveProfileId、profile 顺序以及每项 `Id/Name/ProviderId/ModelName/BaseUrl`；它不是 draft dirty 的替代品；
5. SettingsDialog 对 `name`、`provider`、`model`、`baseUrl`、`credential` 五类 draft 分别维护 dirty bit；任一 draft dirty 都阻止 MCP mutation，不能只依赖现有 `_credentialEdited`；
6. MCP mutation 在主线程通过 UI bridge 获取 workspace/settings mutation lease 与 generation。lease 只在确认五类 draft 全 clean 后签发；lease 存续期间禁用 Profile 表单编辑、切换、新增、删除和保存。后台在 keyed gate 内 fresh-load/merge/写 temp；**atomic replace 前**回主线程复核 lease、generation、active profile 与 persisted fingerprint，任一漂移都删除 temp、返回 `AGAS_AI_SETTINGS_CONFLICT` 且磁盘零写；
7. atomic replace 是 settings 的 commit point。replace 后再回主线程复核只决定 UI apply：若 generation/workspace 漂移，不得回滚已持久设置，也不得覆盖新 workspace/draft，返回 success `persisted:true,uiSynchronized:false`，下次 clean UI 打开时 fresh-load；只有匹配时同步并返回 `uiSynchronized:true`；最后释放 lease并恢复 UI；
8. RMW commit 前 IO 失败、timeout/cancel 时删除 temp并释放 lease，不刷新、不覆盖 UI；同进程 keyed gate 保证 AGAS Store 间无 lost update，非协作外部进程只做 fingerprint best-effort 检测，不承诺跨进程 CAS；
9. 无 draft 时保存成功后才在主线程同步 Panel、toolbar 和 SettingsDialog；同步不得重新写盘；
10. `profile.activate` 缺 credential 可以成功并返回 `credentialConfigured:false`，但目标 Provider unavailable 必须返回 `AGAS_AI_PROVIDER_UNAVAILABLE`；`model.set` 永久允许不在 preset 中的 custom alias，但仍受 secret containment 拒绝规则约束；
11. profiles 按 `profileId` ordinal、models 按 `providerId` 后 `modelName` ordinal 确定性排序；
12. 运行中 job 继续使用已冻结 profile。

SettingsDialog 当前保存整份 `_document` 的路径必须改为调用相同字段级 merge service；UI 与 MCP 不允许各自保留另一套写盘实现。

## 7. 项目生命周期与安全边界

### 7.1 Opaque recent reference

新增进程内 `AgasMcpRecentProjectRefBroker`：

- 每次 recent list 为每个有效 root 轮换签发随机 `projectref-<32 hex>`，同 root 的旧 ref 立即失效；
- broker 保存 ref 到规范化 root 的单向内存映射，每个 root 最多一个 active ref；
- ref TTL 固定 10 分钟、全进程最多 20 个；超限按到期时间、再按签发时间淘汰；
- open 时重新确认 root 仍在 RecentProjectsStore、目录 identity 未变、项目文件有效；
- 应用退出、recent entry 消失或目录 identity 改变后 ref 失效；
- ref 不由路径 hash 生成，客户端无法枚举或逆推出路径。

### 7.2 Create 的固定托管根

项目创建根固定为：

```csharp
Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
    "AGAS Projects")
```

`Environment.GetFolderPath(MyDocuments)` 必须返回非空且 `Path.IsPathRooted` 的绝对路径；不满足时返回安全错误并停止，禁止 fallback 到 cwd、应用目录或用户 home。可实施流程：

1. 校验 name 1–80 rune；去首尾空白；将 OS 非法文件名字符、分隔符和连续空白规范为 `-`；拒绝空结果、`.`、`..`、保留设备名和尾随点/空格；
2. 组合 `<managedRoot>/<sanitized-name>` 后用 `Path.GetFullPath` 证明 parent 与 managedRoot 完全相同，并捕获 managedRoot identity；
3. 检查 managedRoot、目标和所有已有祖先无 symlink/reparse；目标只要存在就拒绝，空目录也不得接管；
4. 在 managedRoot 下创建仅本次拥有的 `.agas-create-<guid>` staging，写入包含 operationId、staging identity、expected target 的随机 owner marker；
5. 使用 initializer 建立完整项目，写入用户可见 project name，使用 fresh load 验证 project/active scene；
6. 发布前再次验证 managedRoot identity、parent equality、target 仍不存在、staging identity 与 owner marker 完全匹配；任何变化都停止且只清理匹配 marker 的 staging；
7. `Directory.Move(staging,target)` 原子发布，随后再次验证 managedRoot/target identity 与 owner marker；
8. 通过同一 Main open bridge 打开；只有 open 完整成功后才写 recent、签发 projectRef 并删除 owner marker，因此失败路径不得污染 recent；
9. publish 后 open 失败时，只有 target identity 与 owner marker 的 operationId/expected target 全部匹配，才允许递归删除这个新 target；不匹配时保留现场并返回安全错误，绝不猜测所有权；
10. publish 前失败只删除 owner marker 匹配的 staging；绝不递归删除既有目标或用户目录。

因此本期 create 没有任何路径参数，也不会在 Documents 以外写入项目。

### 7.3 Lifecycle gate 顺序

生命周期使用 workspace mutation lease + 三段式 orchestration，禁止在 Godot 主线程等待 job quiescence 或 cancellation；现有同步 `ResourceSaver` 保存仍在主线程执行，不得扔到 worker：

```text
主线程只读 gate + workspace lease/capture
 -> 后台 project-scoped cancel/quiescence
 -> 主线程 lease recheck + controlled save + finalize
```

close 的固定顺序为：

1. 主线程确认 project exists，冻结 project scope/generation 并申请唯一 workspace mutation lease；lease 期间 UI open/close/save、MCP lifecycle/save/apply 和 Blueprint output finalize 必须排队或稳定拒绝；
2. 在**任何副作用前**完成全部只读 gate：Blueprint running/output-saving 拒绝；dirty 且 `saveIfDirty=false` 拒绝；AI Queued/Running 且 `cancelAiJobs=false` 拒绝；
3. 若 `cancelAiJobs=true`，只取消冻结的 project scope；后台等待新增的 `WaitForProjectQuiescenceAsync(projectScope, cancellationToken)`，覆盖 Queued、Running、runner task 与 permit 释放，不能退化为全局 wait；
4. timeout/caller cancel 若在 controlled save 开始前赢得 `Pending -> Canceled`，返回 `AGAS_TIMEOUT`/`AGAS_STOPPED`，保持项目打开，不清 context、不切 picker；AI cancellation 已发出时不会伪装为零副作用；
5. 回主线程按 workspace lease、project scope/generation、dirty policy、Blueprint 和 AI quiescence 重新检查；
6. 若需要 controlled save，在第一个同步 `ResourceSaver` 前以原子 CAS 进入 `StartedMainThreadCompletion`；随后即使 descriptor timeout/caller cancel 到达，也完成主线程 save 并返回真实 partial/complete receipt。保存失败保持项目打开；保存后再次复核 clean、scope/generation 与 lease；
7. 若无需保存，则在 finalize 前进入同一 `StartedMainThreadCompletion`。随后清 dirty binding/context、关闭 workspace并显示 picker；不得把已完成 finalize 映射成 timeout；
8. 所有返回路径释放 workspace lease。executor 持有 immutable `AgasMcpProjectCloseOutcomeReceipt`，close success/failure data 都必须从 receipt 返回 `projectStillOpen:boolean`、`saved:boolean`、`aiCancellationRequested:boolean`、`saveState:"none|partial|complete"`。`saved` 表示返回时项目 dirty 数据是否全部 durable；`saveState` 表示本次 close save 实际写入程度。调用方可区分“无副作用 gate reject”“已请求 AI cancel 但仍打开”“部分保存”“已完整保存但 finalize 失败”等状态，不承诺所有失败都零变化；
9. output runtime 若发现 close handler DTO/schema 违例，必须从 execution context 的最后 immutable receipt 构造 `AGAS_INTERNAL_ERROR` failure data；禁止猜测或使用固定默认三/四字段。

open/create 使用相同 workspace lease：主线程确认无项目并 capture generation，后台验证/IO，主线程 lease/generation recheck 后进入短 finalize。这样不会出现“新项目已部分打开，旧项目才发现无法关闭”的中间态。

## 8. 导入导出事务与路径安全

### 8.1 共同路径规则

- 所有输入先 lexical normalize，再逐段验证实际目录 identity；
- 拒绝 symlink、junction、reparse point、hard-link identity 变化；
- source 读取前后比较 length、mtime 和平台可用的 file identity；
- 目标只允许固定项目子目录；
- 错误响应只使用稳定码与安全文案，不透传 `Exception.Message`；
- 列表和响应不含 Base64、raw manifest、图片 bytes 或绝对路径。

### 8.2 Assets import 原子性

先复制到 `assets/imported/.staging/<operationId>.tmp`，flush、校验 magic/尺寸/解码和 identity 后再原子 move 到最终唯一文件。全链传递 descriptor 的 `CancellationToken`；最终 rename 是 commit point，commit 前取消必须删除本次 staging，commit 后不再反向删除已发布 artifact，而是完成并返回确定响应。失败只删除本次临时文件。导入文件属于已落盘 artifact，但不改变 Resource，故不标 project dirty；普通 `assets.import` 没有 pending journal，后续 save、close 或 startup recovery 永远不得自动删除其最终文件。

### 8.3 Scene package import 原子性

1. 完整读取并验证 manifest、计数、文件名、引用存在性和总大小；
2. 在项目 `.agas/import-staging/<operationId>` 准备全部图片；
3. 在内存构造全新 scene snapshot，不碰 ProjectSession；
4. 将 staging 文件原子 move 到最终唯一路径；
5. 通过新的可逆 Application operation 一次性加入 scene index/cache；
6. 标记 project + new scene dirty；
7. 步骤 4–6 任一步失败，回滚内存并删除本次移动的文件；既有项目内容零变化。

步骤 4 前原子写入仅由 AGAS 创建的 pending-import journal 与 owner marker，记录 operationId、project identity、new sceneId、目标 scene 路径和本操作最终 UUID 图片路径。`CancellationToken` 必须贯穿 preflight、copy、decode、journal、主线程 commit；步骤 5 是内存 commit point，commit 前取消要清 staging/journal 及 owner marker 匹配的未发布文件，commit 后完成一致性处理，不得留下“响应取消但内存已提交”的歧义。

新增 `AgasScenePackageImportJournalReconciler`，由唯一 integration owner 接入所有 UI save、MCP `project.save`、close 的 controlled save 与 startup recovery：

1. 每次保存流程返回后，不依赖 UI layout/preferences 的保存结果，fresh-read 磁盘 project index 与目标 scene file；
2. 若磁盘 index 已引用 new sceneId，scene file 存在且可 fresh-load，并且 journal 的 project/scene identity 匹配，则认定业务数据已持久，立即 commit（删除 journal/owner marker）；即使 layout save 随后或此前失败也不能回滚持久数据；
3. startup recovery 使用完全相同的 disk-first reconcile，禁止另写一套“看到 journal 就删除”的逻辑；
4. 只有磁盘尚未持久，且 journal 格式、project identity、operationId、owner marker 和每个 UUID target identity 全部匹配时，才允许删除该 package import 的自有 artifacts；任一项不匹配就保留并报告安全诊断；
5. reconciler 绝不扫描或删除 journal 未列出的文件，也绝不删除普通 `assets.import` artifact。

因为 close 不提供 discard，未持久的 package import 只能由 `saveIfDirty=true` 成功保存后关闭，或由用户保持项目打开继续处理；不能借 close 触发数据删除。

### 8.4 Scene package export 原子性

已加载场景必须从主线程捕获 live immutable snapshot，保证导出包含未保存内存修改；未加载场景使用 `CacheMode.Ignore` fresh read。export 只读 snapshot，禁止为了导出调用 project/scene save，也不得改变 dirty/cache/active。

在 `$AGAS_HOME/import-exchange/scene-packages/.staging/<operationId>` 写入全部引用图和 manifest，所有 marker/manifest/image 临时 slot 在首次写入前登记，完成后在同一文件系统内原子 rename 为 `<packageRef>`。全链传递 `CancellationToken`；rename 是 commit point。commit 前完成完整 staging tree/size/native identity 校验并 prepare 一个绑定 final paths、尚未进入 active-cap/不会淘汰旧 ref 的 opaque token；取消、fault 或 CAS 拒绝会 revoke 该 prepared token并由 finally 删除 identity 匹配的 staging。rename 后立即无失败 activate（此时才按 cap 淘汰旧项），随后只做 best-effort marker cleanup；任何 postcommit fault 都必须返回已准备的真实 success/ref，禁止出现 failure + 已发布目录。不删除/覆盖旧导出，不改变项目 dirty；另一项目可以直接消费返回 ref。

### 8.5 Commit-aware executor

executor commit policy 固定为三种：

| Policy | 原子仲裁与取消语义 | 允许的完成段 |
|---|---|---|
| `None` | 不申请 commit lease；按既有同步 handler 或可取消 read pipeline 执行 | 无新增持久化 commit 协议 |
| `ShortAtomicCommit` | timeout/caller cancel 以 CAS 将 `Pending -> Canceled`；handler 以 CAS 将 `Pending -> ShortCommitting`。只有一个转换能成功 | 仅 atomic rename/replace、短内存 swap、journal flag；禁止 copy/hash/decode/ResourceLoader/ResourceSaver/网络/遍历/无界等待 |
| `StartedMainThreadCompletion` | timeout/caller cancel 与 handler start 通过同一状态机 CAS 仲裁：`Pending -> Canceled` 或 `Pending -> MainThreadCompleting`；main-thread completion 赢后暂停 timeout/caller-cancel 的**错误映射**，服务器内部等待完成并返回 outcome receipt | 允许现有同步 ResourceSaver、workspace install/finalize、受控多阶段 main-thread commit；仍禁止网络和不受控后台等待 |

状态机固定为 `Pending -> Canceled | ShortCommitting | MainThreadCompleting -> Completed`。token 预检查只作 fast-path，正确性必须来自同一 execution state 上的 `Interlocked.CompareExchange`/锁内原子转换；禁止“先检查 token，再无条件进入 commit”的 TOCTOU。进入任一 completion 状态后，后到 timeout/caller cancel 只记录 `cancellationObservedAfterStart=true`，不能把真实结果改写为 timeout/canceled。runtime stop 可请求应用安全退出，但也必须先完成/恢复已开始的本地 commit。

逐工具 policy：

- `StartedMainThreadCompletion`：`agas.project.open`、`agas.project.create`、`agas.project.close`、`agas.project.save`、`agas.scene.package.import`；
- `ShortAtomicCommit`：`agas.ai.profile.activate`、`agas.ai.model.set`、`agas.assets.import`、`agas.scene.package.export`；
- `None`：其余 21 个工具。所有 read-only 工具必须为 `None`；现有 MainThread mutation/AI submit-cancel-apply/Blueprint execute 保留自身同步或既有 async 语义，不被伪装成新 commit policy。

特殊 receipt/边界：

1. `project.save` 在第一个主线程 ResourceSaver 前进入 `MainThreadCompleting`。它保留既有 partial-save 语义，并在每个 scene/project/layout/delete stage 后更新 executor 内 immutable `AgasMcpProjectSaveOutcomeReceipt(saveState,savedFiles,failedFiles,pendingDirtyFiles)`；`saveState=none` 表示本次无 durable 写入，`partial` 表示至少一项已 durable 但仍有失败/dirty，`complete` 表示本次目标全部 durable/已处理。最终响应必须来自最后 receipt；
2. `project.create` 在开始 `Directory.Move(staging,target)` 前进入 `MainThreadCompleting`；一旦开始，必须完成 open/recent finalize，或仅在 owner marker/identity 匹配时完成 target rollback，再返回真实结果；
3. `project.close` 在开始 controlled save/ResourceSaver 前进入 `MainThreadCompleting`；此后 timeout 不得映射成失败。close outcome receipt 固定含 `projectStillOpen/saved/aiCancellationRequested/saveState`，每个 save/finalize stage 原子替换 receipt；
4. `scene.package.import` 在主线程 session/index/journal publish 开始前进入 `MainThreadCompleting`，必须完成 publish 或按 journal/owner marker reconcile；
5. Short policy 的 settings atomic replace、asset final rename、package export final rename 都必须把重 IO 放在 CAS 前；CAS 后只做对应短原子操作。

commit completion 只保证**服务器内部状态与最终 outcome 确定**。当前 MCP transport 将 request cancellation 传入 runtime；客户端已断线时无法承诺最终 `CallToolResult` 可达。只要连接仍可写，executor返回真实结果；断线客户端只能通过重新读取项目/资产/job状态判断，不得把“内部完成”表述为“客户端一定收到”。

## 9. Typed output 与 outputSchema

### 9.1 单一输出契约源

当前匿名 `data` 全部替换为命名 immutable DTO。每个 descriptor 增加：

```csharp
IReadOnlyList<Type> SuccessDataTypes;
IReadOnlyList<Type> FailureDataTypes; // 默认空；project.close 登记 side-effect DTO
JsonElement OutputSchema;
TimeSpan Timeout;
AgasMcpCommitPolicy CommitPolicy;
```

新增 `AgasMcpOutputContractRegistry`，由成功 data DTO 和统一 error DTO 生成工具 envelope schema。运行时同时校验：

- 成功：`ok=true`、`data` 类型与 descriptor 的 success variant 一致、无 `error`；
- 失败：`ok=false`、`error` 为 `AgasMcpToolErrorDto`；默认无 `data`。只有 descriptor 显式登记 `FailureDataTypes` 时才允许 typed failure data；本期仅 `project.close` 登记与 success 同型的 side-effect DTO；error DTO 自身不包含任意扩展 `data`；
- `saved/dirty/warnings` 始终存在；
- 所有对象 `additionalProperties:false`；
- collection 对外冻结，不返回可变内部集合。

CLR contract reflection 必须与实际 `System.Text.Json` wire 一致。完整 contract graph 的对象、enum、collection effective type 与 collection item type都禁止 type-level `JsonConverterAttribute`/`JsonNumberHandlingAttribute`，属性也禁止 `JsonConverter`/`JsonNumberHandling`/rename/ignore/extension-data 等会绕开 frozen contract 的 serialization override；contract build 直接拒绝，而不是先接受 CLR 字段再让 converter 或 `WriteAsString` 改写实际 JSON。回归测试必须覆盖真实会写出 secret 的 object、enum、自定义 `IReadOnlyList<T>` converter，以及把 integer 写成 string 的 property/type-level number handling，证明这些类型都无法注册为输出 contract。

wire primitives 冻结如下：

- 属性名一律 lower camel。为兼容既有 wire，枚举 casing 按领域冻结：Prompt role 为 `general|environment|negativeConstraint`，`effectiveCurrent.source` 为 `liveUi|persisted`，capability 完整五值为 `textToImage|referenceEdit|regionEdit|nativeAlphaMask|guidedRegionEdit`；AI job state 保持现有 PascalCase 五态 `Queued|Running|Succeeded|Failed|Canceled`，apply 后仍为 `Succeeded`，另由 `alreadyApplied`/applied metadata 表达；effective operation 固定 `TextToImage|ReferenceEdit|MaskedEdit|GuidedRegionEdit`。Blueprint `nodeStatuses[].state` **保留现 wire JSON integer**，enum 固定 `0=Idle,1=Running,2=Succeeded,3=Failed,4=Skipped`，schema 为 `{"type":"integer","enum":[0,1,2,3,4]}`；禁止生成器自行套 string enum converter；
- 非 nullable DTO 成员全部进入 `required`；optional 字段仅在有值时出现，序列化使用 `WhenWritingNull`，协议响应禁止输出 JSON `null`；数组存在时必须为数组，空集合输出 `[]`；
- `DateTimeOffset` 对外保持现有 System.Text.Json UTC RFC 3339 wire（通常为 `yyyy-MM-dd'T'HH:mm:ss.fffffff+00:00`），schema 仅使用 `type:string,format:date-time`，兼容等价 `Z`/`+00:00` 表示，不新增强制 `Z` regex 或自定义 converter；
- `long`/文件大小/Unix time 输出 JSON integer，schema 范围固定 `0..9007199254740991`，保证 JavaScript 无损；不使用数字字符串；
- 浮点数必须 finite，禁止 NaN/Infinity；opaque id 使用本方案固定 pattern；
- `providerId` 是独立 primitive，固定为 1..80 字符并匹配 `^[a-z0-9][a-z0-9._-]{0,79}$`；它不是 `safeId`，必须保留合法的 dot provider id；所有 Profile/Provider/execution/diagnostics 输出统一使用该 primitive；
- 列表按各领域指定的 ordinal 稳定键排序，不能依赖 Dictionary、目录或 Resource 枚举顺序。

协议 schema 使用 `oneOf` 表达成功/失败：

```json
{
  "oneOf":[
    {
      "type":"object",
      "required":["ok","saved","dirty","data","warnings"],
      "properties":{"ok":{"const":true},"data":{"$ref":"#/$defs/successData"}},
      "additionalProperties":false
    },
    {
      "type":"object",
      "required":["ok","saved","dirty","warnings","error"],
      "properties":{"ok":{"const":false},"error":{"$ref":"#/$defs/error"}},
      "additionalProperties":false
    }
  ]
}
```

实际生成器需补齐两分支共有 properties 和 `$defs`；两分支互斥，success 的 `error` 被拒绝。普通工具 failure 的 `data` 被拒绝；`project.close` 的 failure 分支由 manifest 生成第二个 closed variant，额外 required typed `data`，用于报告已发生的 cancel/save 副作用。`AgasMcpServerToolBase` 把 descriptor schema 赋给 `ProtocolTool.OutputSchema`。

runtime assertion 必须在响应离开 handler 后、序列化前执行。任何 success variant、required、enum casing、date/int64、null omission 或 failure envelope 不匹配都 fail closed，替换为固定的 `AGAS_INTERNAL_ERROR` failure；descriptor 可提供 typed failure-data factory，`project.close` factory 只从 execution context 的 immutable outcome receipt 读取四字段。日志只记录 tool name、contract id 与 correlation id，不记录原 response、error、data 或序列化 payload。

`AgasMcpAiJobDto` 本身是按 `state` 判别的 `oneOf`：五个命名 variant 共享 `jobId/state/effectiveOperation/execution/createdAtUtc/alreadyApplied`；`alreadyApplied:boolean` 为兼容现 wire在五态都 required。Running 增加 `startedAtUtc`，Succeeded 增加安全 result并在已 apply 时附加 applied metadata，Failed 增加 safe failure/diagnostic，Canceled 增加 `completedAtUtc`；不适用于该状态的字段必须省略。`agas.ai.status` 指定 `jobId` 时 success `data` **直接是 `AgasMcpAiJobDto`**，不得新增 `{job:{...}}` 包装；未指定时才是 `AgasMcpAiStatusListDataDto { jobs:[...] }`。descriptor 的 `SuccessDataTypes` 显式登记这两个根 variant，output generator/runtime assertion 以列表生成并接受 success `oneOf`。

### 9.2 全工具覆盖

不是只给新工具补 schema。以下有序表是 tool names、descriptor、runtime routing 和 `tools/list` 的唯一顺序；A 列依次为 `readOnly/destructive/idempotent/openWorld`，`1/0` 是冻结布尔值。除三个 120 秒重 IO 工具外 timeout 均为 30 秒。

| # | 工具 | family | dispatch | A (R/D/I/O) | timeout | success DTO variant |
|---:|---|---|---|---|---:|---|
| 1 | `agas.projects.recent` | Project | MainThread | 1/0/1/0 | 30s | `AgasMcpRecentProjectsDataDto` |
| 2 | `agas.project.open` | Project | AsyncOrchestrated | 0/0/0/0 | 30s | `AgasMcpProjectOpenDataDto` |
| 3 | `agas.project.create` | Project | AsyncOrchestrated | 0/0/0/0 | 30s | `AgasMcpProjectCreateDataDto` |
| 4 | `agas.project.close` | Project | AsyncOrchestrated | 0/0/0/0 | 30s | `AgasMcpProjectCloseDataDto` |
| 5 | `agas.project.snapshot` | Project | MainThread | 1/0/1/0 | 30s | `AgasMcpProjectSnapshotDataDto` |
| 6 | `agas.assets.list` | Project | MainThread | 1/0/1/0 | 30s | `AgasMcpAssetsListDataDto` |
| 7 | `agas.imports.list` | Project | AsyncOrchestrated | 1/0/1/0 | 30s | `AgasMcpImportsListDataDto` |
| 8 | `agas.assets.import` | Project | AsyncOrchestrated | 0/0/0/0 | 120s | `AgasMcpAssetImportDataDto` |
| 9 | `agas.scene.get` | Project | AsyncOrchestrated | 1/0/1/0 | 30s | `AgasMcpSceneGetDataDto` |
| 10 | `agas.scene.mutate` | Project | MainThread | 0/1/0/0 | 30s | `AgasMcpSceneMutationDataDto` |
| 11 | `agas.scene.package.import` | Project | AsyncOrchestrated | 0/0/0/0 | 120s | `AgasMcpScenePackageImportDataDto` |
| 12 | `agas.scene.package.export` | Project | AsyncOrchestrated | 0/0/0/0 | 120s | `AgasMcpScenePackageExportDataDto` |
| 13 | `agas.grid.mutate` | Project | MainThread | 0/0/1/0 | 30s | `AgasMcpGridMutationDataDto` |
| 14 | `agas.layer.mutate` | Project | MainThread | 0/1/0/0 | 30s | `AgasMcpLayerMutationDataDto` |
| 15 | `agas.image.mutate` | Project | MainThread | 0/1/0/0 | 30s | `AgasMcpImageMutationDataDto` |
| 16 | `agas.prompt.mutate` | Project | MainThread | 0/1/0/0 | 30s | `AgasMcpPromptMutationDataDto` |
| 17 | `agas.view.set` | Project | MainThread | 0/0/1/0 | 30s | `AgasMcpViewSetDataDto` |
| 18 | `agas.project.save` | Project | AsyncOrchestrated | 0/0/1/0 | 30s | `AgasMcpProjectSaveDataDto` |
| 19 | `agas.selection.set` | Project | MainThread | 0/0/1/0 | 30s | `AgasMcpSelectionSetDataDto` |
| 20 | `agas.selection.get` | Project | MainThread | 1/0/1/0 | 30s | `AgasMcpSelectionGetDataDto` |
| 21 | `agas.ai.profiles.list` | Ai | AsyncOrchestrated | 1/0/1/0 | 30s | `AgasMcpAiProfilesDataDto` |
| 22 | `agas.ai.models.list` | Ai | AsyncOrchestrated | 1/0/1/0 | 30s | `AgasMcpAiModelsDataDto` |
| 23 | `agas.ai.profile.activate` | Ai | AsyncOrchestrated | 0/0/1/0 | 30s | `AgasMcpAiProfileActivatedDataDto` |
| 24 | `agas.ai.model.set` | Ai | AsyncOrchestrated | 0/0/1/0 | 30s | `AgasMcpAiModelSetDataDto` |
| 25 | `agas.ai.diagnostics` | Ai | AsyncOrchestrated | 1/0/1/0 | 30s | `AgasMcpAiDiagnosticsDataDto` |
| 26 | `agas.ai.generate` | Ai | MainThread | 0/0/0/1 | 30s | `AgasMcpAiGenerateDataDto` |
| 27 | `agas.ai.status` | Ai | MainThread | 1/0/1/0 | 30s | `AgasMcpAiJobDto | AgasMcpAiStatusListDataDto` |
| 28 | `agas.ai.cancel` | Ai | MainThread | 0/0/1/0 | 30s | `AgasMcpAiCancelDataDto` |
| 29 | `agas.ai.apply` | Ai | MainThread | 0/0/1/0 | 30s | `AgasMcpAiApplyDataDto` |
| 30 | `agas.blueprint.capabilities` | Blueprint | MainThread | 1/0/1/0 | 30s | `AgasMcpBlueprintCapabilitiesDataDto` |
| 31 | `agas.blueprint.execute` | Blueprint | AsyncMainThread | 0/0/0/1 | 30s | `AgasMcpBlueprintExecuteDataDto` |

`AsyncOrchestrated` 是本期新增 dispatch，不等于把整个 handler 放到 worker：主线程 capture/preflight → 后台可取消 IO/wait → 主线程 scope/generation recheck 与 commit。`BlueprintExecute` 保留现有 `AsyncMainThread`。AI status list 与 direct single 共用同一 `AgasMcpAiJobDto` union；Prompt DTO 增加 role 后，snapshot、scene.get、prompt.mutate 共用同一 DTO。

family、dispatch、timeout、commit policy 和 success/failure CLR type list 都是 AGAS runtime 内部 metadata；MCP `tools/list` 只发布标准的 `name/description/inputSchema/outputSchema/annotations`，不得添加非标准 family/dispatch/timeout/commitPolicy 字段。HTTP 合同只断言 31 个 name、inputSchema、outputSchema、annotations 及标准 description，不把内部 metadata 当 wire 字段。

### 9.3 手写输出合同 manifest（独立 oracle）

本节是独立于 DTO reflection/generator 的手写 oracle。实现新增版本化常量 `AgasMcpFrozenOutputManifest.V1`；Wave 1 只完成31工具手写 manifest 的完整性，以及用独立 test-fixture DTO/variant/envelope 验证 schema builder 与 fail-closed 基础，不提前新增生产 typed data DTO/handler或发布 `outputSchema`。生产 DTO/handler 接入后，Wave 3 T3.8 Core 测试必须逐字段比较31/31实际 manifest、CLR DTO、generated schema 与真实 handler serialization/output；不能用“DTO 能序列化”替代本表。以下字段按列出顺序序列化；JSON consumer 不应依赖对象属性顺序，但 golden contract 依此发现漂移。`R`=required，`O`=optional 且无值时省略，禁止 `null`。union 的成员列表只保存在 manifest 的 `UnionVariants` 元数据中；`oneOf`/`variant` 绝不是普通 wire `Fields`，golden 必须拒绝这两个伪字段。

公共 primitive/path：

```text
safeId      string, 1..120, ^[A-Za-z0-9_-]+$
providerId  string, 1..80, ^[a-z0-9][a-z0-9._-]{0,79}$
jobId       string, ^aijob-[a-f0-9]{32}$
sourceRef   string, ^src1-[a-f0-9]{32}-[a-f0-9]{64}$；opaque、签名、list/export-issued、默认15分钟过期；不编码 path/name
relPath     string, project-relative, '/' separator, 非 rooted, 无 '.', '..', NUL 或反斜杠
projectUri  string, opaque `project:///...` 兼容 URI；不宣称已注册 MCP resource、不可由 `resources/list`/`resources/read` 发现或解引用，也不是文件 URL/绝对路径
utc         string, RFC 3339 date-time；现有 serializer 输出保留 +00:00，schema 同时接受等价 Z
i32         JSON integer, Int32 范围；计数/order 非负
i64         JSON integer, 0..9007199254740991
f64         finite JSON number
```

公共 closed object（字段顺序即 manifest 顺序）：

| 类型 | R 字段 | O 字段 | enum / path / order |
|---|---|---|---|
| `Error` | `code:string,message:string,suggestion:string` | 无 | code 为稳定 `AGAS_*`；不得有 data |
| `Grid` | `sizePx:i32,snapEnabled:bool,visible:bool` | 无 | sizePx 1..4096 |
| `ViewState` | `cameraX:f64,cameraY:f64,zoom:f64` | 无 | zoom > 0 |
| `SceneSummary` | `sceneId:safeId,name:string,order:i32,loaded:bool` | 无 | scenes 按 order,id ordinal |
| `LayerSummary` | `sceneId:safeId,layerId:safeId,name:string,order:i32,visible:bool,locked:bool,imageCount:i32,promptCount:i32` | 无 | layers 按 order,id ordinal |
| `ImageSummary` | `sceneId:safeId,layerId:safeId,imageId:safeId,imagePath:relPath,order:i32,x:f64,y:f64,scaleX:f64,scaleY:f64,rotationDegrees:f64,opacity:f64` | 无 | images 按 order,id；opacity 0..1 |
| `PromptSummary` | `sceneId:safeId,layerId:safeId,fragmentId:safeId,text:string,role:string,enabled:bool,order:i32` | 无 | role=`general|environment|negativeConstraint`；按 order,id |
| `SceneItem` | `kind:string` + 对应 `LayerSummary|ImageSummary|PromptSummary` 全字段 | 无 | oneOf closed；kind=`layer|image|prompt`；类型序后 order,id |
| `AssetItem` | `relativePath:relPath,extension:string,sizeBytes:i64,modifiedUnixTime:i64` | 无 | path ordinal |
| `ImportItem` | `kind:string,name:string,sizeBytes:i64,mediaType:string,validationState:string,sourceRef:sourceRef` | 无 | kind=`asset|scenePackage`；validationState=`unverified|invalid`；无 path/Base64/manifest/metadata |
| `Region` | `column:i32,row:i32,width:i32,height:i32` | 无 | width/height >=1 |
| `ExecutionIdentity` | `profileId:safeId,providerId:providerId,adapterKind:string,modelName:string` | 无 | 只允许 submission 时 safe projection 后的值 |
| `Diagnostic` | `diagnosticId:string,errorCode:string,phase:string,category:string` | `httpStatus:i32,upstreamCode:string` | safe allow-list；不含 URL/body/header/secret |
| `SaveFailure` | `path:relPath,code:string,message:string` | 无 | path ordinal |
| `AiIssue` | `code:string,message:string,suggestion:string` | 无 | code ordinal |
| `BlueprintNodeStatus` | `nodeId:safeId,state:i32,errorCode:string,message:string` | 无 | state integer enum 0..4；graph node order |

`AgasMcpAiJobDto` 是以下 explicit union；common R 固定为 `jobId:jobId,state:string,effectiveOperation:string,execution:ExecutionIdentity,createdAtUtc:utc,alreadyApplied:bool`，其中 effectiveOperation enum=`TextToImage|ReferenceEdit|MaskedEdit|GuidedRegionEdit`：

| state | R 字段 | O 字段 |
|---|---|---|
| `Queued` | common，`state:"Queued",alreadyApplied:false` | 无 |
| `Running` | common + `startedAtUtc:utc`，`state:"Running",alreadyApplied:false` | 无 |
| `Succeeded` | common + `startedAtUtc:utc,completedAtUtc:utc,resultRelativePath:relPath,message:string`，`state:"Succeeded"` | `previewRelativePath:relPath,appliedRelativePath:relPath,appliedAtUtc:utc`；三 applied 字段仅 alreadyApplied=true 时出现 |
| `Failed` | common + `completedAtUtc:utc,errorCode:string,message:string,diagnostic:Diagnostic`，`state:"Failed",alreadyApplied:false` | `startedAtUtc:utc,hint:string` |
| `Canceled` | common + `completedAtUtc:utc`，`state:"Canceled",alreadyApplied:false` | `startedAtUtc:utc` |

逐工具 success `data` manifest：

| # / 工具 | R 字段（按序，含类型） | O 字段 | array order / path 约束 |
|---|---|---|---|
| 1 `agas.projects.recent` | `projects:RecentProject[]`；item=`projectRef:string,name:string,lastOpenedUnixTime:i64,available:bool,isOpen:bool` | 无 | lastOpened desc、name/id ordinal；每 root 一个 ref |
| 2 `agas.project.open` | `projectRef:string,name:string,isOpen:bool` | 无 | projectRef pattern；不返回 path |
| 3 `agas.project.create` | `projectRef:string,name:string,isOpen:bool` | 无 | 不返回 managed root/path |
| 4 `agas.project.close` | `projectStillOpen:bool,saved:bool,aiCancellationRequested:bool,saveState:string` | 无 | saveState=`none|partial|complete`；failure/fail-closed envelope也从同型 immutable receipt 返回；无 path |
| 5 `agas.project.snapshot` | `projectName:string,grid:Grid,scenes:SceneSummary[],layers:LayerSummary[],images:ImageSummary[],prompts:PromptSummary[],dirtyProject:bool,dirtyLayout:bool,dirtySceneIds:safeId[],pendingDeletedSceneIds:safeId[]` | `activeSceneId:safeId` | 各 summary 用公共排序；id arrays ordinal |
| 6 `agas.assets.list` | `items:AssetItem[],count:i32,totalCount:i32,hasMore:bool` | `nextOffset:i32` | path ordinal；nextOffset 仅 hasMore=true |
| 7 `agas.imports.list` | `items:ImportItem[],count:i32,totalCount:i32,hasMore:bool` | `nextOffset:i32` | item 仅 `kind,name,sizeBytes,mediaType,validationState,sourceRef`；禁止 path/base64/manifest/metadata |
| 8 `agas.assets.import` | `relativePath:relPath,extension:string,sizeBytes:i64,pixelWidth:i32,pixelHeight:i32` | 无 | 输入仅 list-issued sourceRef；relativePath 仅 `assets/imported/` |
| 9 `agas.scene.get` | `sceneId:safeId,name:string,viewState:ViewState,items:SceneItem[]` | `activeLayerId:safeId,nextCursor:string` | items 稳定类型/order/id；cursor pattern/scope |
| 10 `agas.scene.mutate` | `scenes:SceneSummary[],activeSceneId:safeId,changedSceneId:safeId,dirtyProject:bool,dirtyLayout:bool,dirtySceneIds:safeId[],pendingDeletedSceneIds:safeId[]` | 无 | scenes/order；dirty ids ordinal |
| 11 `agas.scene.package.import` | `sceneId:safeId,name:string,copiedAssetRelativePaths:relPath[],dirtySceneIds:safeId[]` | 无 | 输入仅 list/export-issued sourceRef；paths/id ordinal |
| 12 `agas.scene.package.export` | `packageRef:string,sourceRef:sourceRef,referenceImageCount:i32` | 无 | 全局 exchange 原子发布；不返回 manifest path |
| 13 `agas.grid.mutate` | `grid:Grid,dirtyProject:bool,dirtyLayout:bool,dirtySceneIds:safeId[]` | 无 | dirty ids ordinal |
| 14 `agas.layer.mutate` | `sceneId:safeId,layers:LayerSummary[],currentLayerId:safeId,changedLayerId:safeId,dirtyProject:bool,dirtyLayout:bool,dirtySceneIds:safeId[]` | 无 | layer/order；dirty ids ordinal |
| 15 `agas.image.mutate` | `sceneId:safeId,layerId:safeId,layer:LayerSummary,dirtySceneIds:safeId[]` | `image:ImageSummary` | image 仅非 delete；ids ordinal |
| 16 `agas.prompt.mutate` | `sceneId:safeId,layerId:safeId,fragments:PromptSummary[],mergedPreview:string,changedFragmentId:safeId,dirtySceneIds:safeId[]` | 无 | fragments/order；role lower camel |
| 17 `agas.view.set` | `sceneId:safeId,cameraX:f64,cameraY:f64,zoom:f64,dirtyProject:bool,dirtyLayout:bool,dirtySceneIds:safeId[]` | 无 | dirty ids ordinal |
| 18 `agas.project.save` | `saveState:string,savedFiles:relPath[],failedFiles:SaveFailure[],pendingDirtyFiles:relPath[]` | 无 | saveState=`none|partial|complete`；三数组 path ordinal；来自多阶段 receipt |
| 19 `agas.selection.set` | `sceneId:safeId,activeRegionTool:string,gridSizePx:i32,canEditCurrentLayer:bool` | `selection:Region,replacement:Region,effectiveGenerationRegion:Region,selectedImageId:safeId,selectedLayerId:safeId` | activeRegionTool=`Edit|Selection|Replacement`；无 path |
| 20 `agas.selection.get` | 与 #19 完全相同 | 与 #19 相同 | 与 #19 相同 |
| 21 `agas.ai.profiles.list` | `profiles:AiProfileRow[],providerConfigurationErrorCount:i32` | `activeProfileId:safeId,effectiveCurrent:EffectiveProfile` | profiles 按 profileId ordinal；EffectiveProfile=`profileId,providerId,adapterKind,modelName,source` |
| 22 `agas.ai.models.list` | `providers:AiProviderRow[],providerConfigurationErrorCount:i32` | 无 | providerId ordinal；models 按 modelName ordinal；row=`providerId,displayName,adapterKind,defaultModelName,capabilities[],supportsCustomModel,models[]` |
| 23 `agas.ai.profile.activate` | `profileId:safeId,providerId:providerId,modelName:string,credentialConfigured:bool,persisted:bool,uiSynchronized:bool` | 无 | safe projected；无 BaseUrl |
| 24 `agas.ai.model.set` | `profileId:safeId,providerId:providerId,modelName:string,knownPreset:bool,credentialConfigured:bool,persisted:bool,uiSynchronized:bool` | 无 | safe projected；无 BaseUrl |
| 25 `agas.ai.diagnostics` | `profileId:safeId,providerId:providerId,modelName:string,credentialConfigured:bool,providerAvailable:bool,ready:bool,networkChecked:bool,capabilities:string[],issues:AiIssue[]` | 无 | capabilities 固定序；issues code ordinal；networkChecked=false |
| 26 `agas.ai.generate` | `jobId:jobId,state:"Queued",effectiveOperation:string,execution:ExecutionIdentity,createdAtUtc:utc` | 无 | execution 已 submission-time projection |
| 27 `agas.ai.status` | direct `AgasMcpAiJobDto` **或** `jobs:AgasMcpAiJobDto[]` | 无 | list createdAt desc、jobId ordinal；不得 `{job}` 包装 |
| 28 `agas.ai.cancel` | `jobId:jobId,state:string,canceled:bool` | 无 | state=`Queued|Running|Succeeded|Failed|Canceled` |
| 29 `agas.ai.apply` | `jobId:jobId,state:"Succeeded",alreadyApplied:bool,appliedRelativePath:relPath,message:string` | 无 | 保留现 wire；project-relative output path |
| 30 `agas.blueprint.capabilities` | `nodeFunctions:BlueprintNodeFunction[],workflows:BlueprintWorkflow[],inputRequirements:BlueprintWorkflowInputs[]` | 无 | registry/workflow order稳定；所有 image path 为 relPath |
| 31 `agas.blueprint.execute` | `blueprintId:safeId,blueprintName:string,source:string,nodeStatuses:BlueprintNodeStatus[],output:BlueprintOutput` | 无 | source=`live|stored`；output R=`width:i32,height:i32`, O=`relativePath:relPath,resourceUrl:projectUri` |

AI/Blueprint nested rows：

| 类型 | R 字段（按序） | O 字段 | 约束 |
|---|---|---|---|
| `EffectiveProfile` | `profileId:safeId,providerId:providerId,adapterKind:string,modelName:string,source:string` | 无 | source=`liveUi|persisted`；safe projected |
| `AiProfileRow` | `profileId:safeId,name:string,providerId:providerId,providerDisplayName:string,modelName:string,credentialConfigured:bool,providerAvailable:bool,capabilities:string[]` | 无 | safe projected；capability 固定序 |
| `AiModelRow` | `modelName:string,label:string,recommended:bool` | 无 | safe projected；modelName ordinal |
| `AiProviderRow` | `providerId:providerId,displayName:string,adapterKind:string,defaultModelName:string,capabilities:string[],supportsCustomModel:bool,models:AiModelRow[]` | 无 | safe projected；providerId ordinal |
| `BlueprintInputPort` | `key:string,type:string,required:bool,capacity:i32` | 无 | type lower camel；capacity >=1 |
| `BlueprintOutputPort` | `key:string,type:string` | 无 | type lower camel |
| `BlueprintNodeFunction` | `kind:string,name:string,inputs:BlueprintInputPort[],outputs:BlueprintOutputPort[]` | 无 | registry order |
| `BlueprintInputRequirement` | `nodeId:safeId,required:bool,currentImagePath:string,needsBinding:bool` | 无 | currentImagePath 仅空字符串或 relPath |
| `BlueprintWorkflow` | `blueprintId:safeId,name:string,canExecute:bool,readOnly:bool,errorCode:string,errorMessage:string,inputRequirements:BlueprintInputRequirement[]` | 无 | workflow order；requirements graph order |
| `BlueprintWorkflowInputs` | `blueprintId:safeId,inputs:BlueprintInputRequirement[]` | 无 | 与 workflow 同序 |
| `BlueprintOutput` | `width:i32,height:i32` | `relativePath:relPath,resourceUrl:projectUri` | width/height >=1；`resourceUrl` 仅允许 opaque `project:///...` 兼容 URI |

所有对象均 closed；表中未列字段一律拒绝。

## 10. 组合与 wiring

```text
MCP HTTP
 -> serial executor / session rate limit
 -> descriptor(input + output contracts)
 -> per-tool handler registry
 -> main-thread dispatcher / async orchestrator / commit lease
 -> target facade/service
    -> AI profile service + AI UI sync port
    -> project lifecycle bridge owned by Main
    -> project read/mutation services
    -> controlled import/package services
 -> typed response + runtime contract assertion
```

runtime routing 不再按 family 批量把所有 `Project` 工具交给单一 facade。新增以 ordinal toolName 为唯一键的 `AgasMcpToolHandlerRegistry`，每个 entry 统一暴露：

```csharp
ValueTask<AgasMcpToolResponse> InvokeAsync(
    IReadOnlyDictionary<string, JsonElement>? arguments,
    AgasMcpToolExecutionContext context,
    CancellationToken cancellationToken);
```

sync/main-thread handler 由 adapter 返回 completed `ValueTask`；`AsyncMainThread` 和 `AsyncOrchestrated` 使用同一接口，但由内部 dispatch metadata 决定 capture/await/recheck 路径。composition 必须逐工具显式绑定到 Project、AI、Blueprint、Lifecycle、Asset 或 Package handler。应用启动时断言 descriptor names、official names 与 handler keys 三者各 31 个、ordinal 顺序一致、无重复、无遗漏、无未知 key；失败则 MCP runtime 不启动。family 只用于内部分类/指标，不参与 handler 选择。

新增/调整职责：

| 组件 | 职责 |
|---|---|
| `AgasMcpAiProfileService` | fresh-load、safe projection、activate/model set、配置 diagnostics |
| `IAgasMcpAiProfileUiBridge` | 获取 live effective state、同步 Panel/toolbar/SettingsDialog draft gate |
| `AgasMcpProjectLifecycleFacade` | recent refs、create/open/close gate 与响应 |
| `IAgasMcpProjectLifecycleBridge` | Main 拥有的 open/close/UI page 切换窄端口 |
| `AgasMcpRecentProjectRefBroker` | opaque ref 生命周期与重新验证 |
| `AgasMcpSceneDetailQuery` + `AgasMcpSceneCursorBroker` | live/fresh immutable snapshot、稳定分页、TTL/容量/scope |
| `AgasMcpToolHandlerRegistry` | toolName 唯一键的逐工具 handler；启动验证 31/31 无重漏 |
| `AgasMcpAsyncToolOrchestrator` | 主线程 capture、后台可取消 IO/quiescence、主线程 generation recheck/commit lease；实施通用 `AsyncOrchestrated`，不是 lifecycle 专用分支 |
| `AgasMcpControlledAssetImportService` | inbox 校验、staging、原子发布 |
| `AgasMcpScenePackageService` | MCP-safe package 校验、事务和安全错误映射 |
| `AgasScenePackageImportJournalReconciler` | disk-first package journal reconcile；由所有 save 与 startup 共用 |
| `AgasAiSettingsRmwCoordinator` | 按 canonical AGAS_HOME 共享 keyed gate、fingerprint、字段级 merge、原子 replace |
| `AgasMcpAiSafeProjector` | credential-aware AI 输出投影，禁止身份/label/diagnostic 泄密 |
| `AgasMcpOutputContractRegistry` | typed envelope outputSchema 与运行时类型检查 |

生命周期 gate/finalize、`ResourceLoader`/repository Resource load、Resource→scalar projection 和 Resource mutation 必须在主线程。AI Provider IO 继续后台。纯文件复制、已投影 scalar snapshot 的排序/预算/分页、settings RMW 和 project-scoped wait 可以在后台，但最终 scene/project/UI commit 必须回主线程；不得从 worker 线程触碰 Godot Object/Resource/Node。

package journal 的唯一持久化 owner 固定在 Core Persistence；Application 只 orchestration，MCP facade 不拥有 journal。文件边界冻结为：

- `src/Core/Persistence/ScenePackage*`：沿用现有 `ScenePackageStore` 所在边界，放置 journal DTO/store/reconciler、owner marker、disk-state probe 与原子持久化规则，不依赖 MCP/UI；
- `src/Application/*ScenePackage*`：只编排 preflight、snapshot、commit lease 和 Core Persistence port，不复制删除/归属/reconcile 算法；
- UI save coordinator、MCP project save facade 和 startup project loader：只调用同一个 Core reconciler port，不复制 reconcile 规则；
- `src/Mcp/**`：只负责 package input/output、timeout/cancellation、安全错误映射与调用 Application service，不直接删 journal/artifact；
- `Main.cs`：只做 lifecycle/startup wiring 和主线程 bridge，不承载 journal 判定算法。

## 11. Dirty、保存和幂等语义

| 操作 | project dirty | 自动 project save | 幂等 |
|---|---:|---:|---:|
| profiles/models/diagnostics list | 否 | 不适用 | 是 |
| profile.activate/model.set | 否（全局设置独立持久化） | 设置立即持久化 | 相同值是 |
| projects.recent | 否 | 否 | 是 |
| project.open | 否 | 否 | 否；已有任意项目都返回 alreadyOpen |
| project.create | 新项目完整落盘 | 是 | 否；同名目标拒绝 |
| project.close | clean 时关闭；dirty 时先 controlled save | 仅 `saveIfDirty=true` 显式保存 | 关闭后再次调用返回 no project |
| scene.get | 否 | 否 | 同快照/cursor 是 |
| assets.import | 否 | artifact 立即落盘 | 否 |
| package.import | project + new scene dirty | 否 | 否 |
| package.export | 否 | export artifact 立即落盘 | 否 |
| grid.mutate/prompt role | project 或 scene dirty | 否 | set 相同值是 |

## 12. 错误码

新增稳定码：

```text
AGAS_AI_PROFILE_NOT_FOUND
AGAS_AI_PROVIDER_UNAVAILABLE
AGAS_AI_CREDENTIAL_MISSING
AGAS_AI_MODEL_INVALID
AGAS_AI_SETTINGS_CONFLICT
AGAS_PROJECT_REF_INVALID
AGAS_PROJECT_ALREADY_OPEN
AGAS_PROJECT_DIRTY
AGAS_AI_JOBS_ACTIVE
AGAS_BLUEPRINT_BUSY
AGAS_CURSOR_INVALID
AGAS_CURSOR_CAPACITY
AGAS_SCENE_SNAPSHOT_TOO_LARGE
AGAS_IMPORT_SOURCE_NOT_FOUND
AGAS_IMPORT_INVALID
AGAS_IMPORT_TOO_LARGE
AGAS_PACKAGE_INVALID
AGAS_PACKAGE_TOO_LARGE
AGAS_TIMEOUT
AGAS_INTERNAL_ERROR
```

路径越界继续使用 `AGAS_PATH_REJECTED`；无项目继续使用 `AGAS_NO_OPEN_PROJECT`；只读项目继续使用 `AGAS_PROJECT_READ_ONLY`；保存失败继续使用 `AGAS_SAVE_FAILED`。`AGAS_INTERNAL_ERROR` 只用于 runtime output assertion 等内部合同违例，文案与 suggestion 固定且不携带原始 data。禁止通过解析中文 message 决定错误码；Application/bridge 必须返回结构化 reason。

## 13. 兼容与迁移

- 原 16 个工具名不变；现有必填字段和语义不变。
- `agas.ai.generate.profileId` 继续作为单次覆盖，不激活 Profile。
- `agas.prompt.mutate.role` 是可选增量字段；旧调用不传时保持 `general`/原 role。
- Prompt 输出新增 `role` 为 additive wire change。
- Grid 和 Prompt role 已存在于 Resource，不提升项目 resourceVersion。
- AI execution identity 只存在于进程内 job，无持久化迁移。
- recent projectRef 只存在内存，不修改 recent-projects.tres 格式。
- 新目录 `import-inbox`、`import-exchange/scene-packages`、`assets/imported`、`.agas/import-staging` 按需创建。
- outputSchema 是协议发现增强，结构化响应保留既有 envelope 字段名和 JSON casing。

## 14. 风险与回滚

| 风险 | 缓解 |
|---|---|
| UI 草稿被 MCP 覆盖 | draft dirty + fingerprint gate，稳定 conflict |
| 模型 preset 过时 | custom alias 永久允许，preset 仅指导 |
| job status 显示当前而非实际模型 | submission 时冻结 execution identity |
| recent ref 被伪造/复用 | 随机进程内 ref + open 时重新验证 recent/root identity |
| create 越权写目录 | 只收 name，固定 Documents/AGAS Projects，staging + parent equality |
| import zip/path traversal | 本期不接收 zip；固定目录层级、basename、安全 identity |
| package 半导入 | 全量 preflight + staging + 可逆 commit + rollback |
| 大图片/manifest DoS | byte/count/pixel/decode budget |
| output schema 与运行时漂移 | 同一 typed DTO 生成 + 每次响应 runtime assertion |
| 生命周期破坏运行任务 | dirty/AI/Blueprint gates，先 gate 后切 UI |

回滚策略：本期不改持久化 Resource schema；可以整体撤回新 descriptor/facade/wiring。已 commit 的新项目、普通 `assets.import` 最终文件、已 durable 的 package import 和已发布 export 都是用户 artifact，回滚不得自动删除。只有 commit 前 staging，或“磁盘尚未 durable + journal 明列 + owner marker/identity 完整匹配”的 pending package files 可由对应 operation 清理。事务 staging 的 startup 清理也必须验证 owner marker/identity/TTL，不能只凭目录名前缀，更不能清理未知目录。

## 15. 测试策略

测试基础设施新增可注入的 root provider；生产实现读取并验证 `AGAS_HOME`/MyDocuments。每个 Core/Godot/HTTP runner 必须在启动被测进程前把 `HOME`、`AGAS_HOME`、`XDG_CONFIG_HOME`、`XDG_DATA_HOME`、`XDG_CACHE_HOME`、`GODOT_USER_HOME`、Godot `user://` 数据根、managed root、asset inbox、scene-package inbox 全部指向当前 case 的独立临时目录，并禁止继承真实 recent/credential/provider 路径。

runner 在覆盖环境变量前解析并记录 known real paths：真实 `~/.agas`、真实 `Documents/AGAS Projects`、当前平台 Godot `user://` 对应的 APP-AGAS-CS app_userdata 目录，以及 XDG config/data/cache 下的 APP-AGAS-CS 路径。fingerprint 只针对这些 known paths，记录目录存在性、文件/目录类型、symlink/reparse identity，以及已存在文件的相对名/长度/mtime/content hash；不递归扫描或 hash 整个 HOME。正常退出、timeout kill 和模拟异常退出后逐项比较 fingerprint，任一真实目录新建、删除或内容变化都失败。固定生产来源根为 `$AGAS_HOME/import-inbox/assets`、`$AGAS_HOME/import-inbox/scene-packages` 与 `$AGAS_HOME/import-exchange/scene-packages`。

### 15.1 Core contract 与安全测试

1. 31 个 tool name/order/descriptor/handler 与 §8.5/§9.2 逐项相等且唯一；内部 family、dispatch、四个 annotations、timeout、commit policy、success/failure variants 全部做 Core parity assertion，handler registry 启动断言无重漏。
2. 31/31 input/output schema 可解析；§9.3 手写 manifest 与 CLR DTO、generated schema、真实 JSON 四方逐字段 parity；unknown、case alias、显式 null、range、条件字段完整反向矩阵。
3. 每个工具 success/failure 真实响应均通过自己的 outputSchema；覆盖冻结的逐领域 enum casing、Blueprint integer state、RFC3339 UTC、int64 边界、optional omission 和 AI status direct-single/list union。
4. 故意返回错误 DTO/variant、null、错误 casing 或超界 long 时必须 fail closed 为 `AGAS_INTERNAL_ERROR`；断言日志不含原 response/error/data。
5. typed DTO collection immutability；禁止匿名 success data 的架构测试。
6. 全响应扫描禁止 credential、Authorization、Base64/data URI、绝对路径、大 manifest/metadata。

### 15.2 AI 测试

- 无项目 profiles/models/diagnostics 可用；empty、active、liveUi/persisted source 正确。
- 使用 `providerId != adapterKind` 的第三方 Provider 跑 resolve → request → submission → job snapshot → status 全链；隔离坏配置、credentialConfigured bool、自定义 model alias。
- profiles/models 确定性排序；内置 Provider 的五个 capability 值均通过 manifest/schema；activate 缺 credential 可成功；Provider unavailable 拒绝；custom alias 可 set。
- activate/model set 在同进程共享 keyed gate 内字段级持久化；两个 Store 实例无丢更新，外部变化只验证 best-effort conflict、不宣称跨进程 CAS；credential 文件 bytes 不变；重启/新 Panel 恢复。
- SettingsDialog 的 name/provider/model/baseUrl/credential 五类草稿分别触发稳定 conflict；mutation lease 期间 UI 编辑/切换/增删/保存禁用；用 atomic-replace barrier 分别验证 precommit generation漂移=conflict/零写、postcommit漂移=`persisted:true,uiSynchronized:false`且不覆盖新UI；无草稿时同步不重复写盘。
- 切换时旧 job execution identity 不变，新 job 使用新模型；五态均保留 required `alreadyApplied`，apply 前后 `state` 均为 `Succeeded`，仅 alreadyApplied/applied metadata 变化。
- DNS、connection、TLS、proxy、timeout、HTTP 401、invalid response 的安全 diagnostic 分类。
- 将本次执行 credential 注入 Profile name/model、Provider/model label、job identity、upstream diagnostic 后，所有输出及日志都不含 credential；提交后 rotate/clear 再 status 仍安全；diagnostic 也不含原始异常、prompt、body、URL、header、图片或绝对路径。

### 15.3 Lifecycle 测试

- recent 不返回路径；伪造、过期、删除后 ref 拒绝。
- open 仅接受 ref；path/root/URL unknown field 被 schema 拒绝；已有同一或不同项目都返回 `AGAS_PROJECT_ALREADY_OPEN`。
- create 只写注入的固定托管根；MyDocuments 空/非 rooted 不 fallback；separator、`..`、保留名、symlink/reparse、空目标、非空目标全部拒绝。
- initializer/publish/open 各阶段故障注入；发布前后 root identity 变化被拒绝；只有 owner marker 完整匹配才能清 staging/new target；open 失败 recent 零污染。
- 全部只读 gate 在 save/cancel 前完成；dirty + `saveIfDirty=false` 拒绝；true 时 quiescence 后在主线程 controlled save，成功才关闭；验证 `projectStillOpen/saved/aiCancellationRequested/saveState` 与 immutable receipt 可表达 timeout 到达时已发生的 cancel/partial save/finalize 副作用。
- `cancelAiJobs=true` 覆盖 Queued/Running/runner/permit 的 project-scoped wait；timeout 保持项目打开；不得错误等待或取消其他 project scope。
- close/open 后 context、dirty binding、AI scope、picker/workspace 和 recent 同步。

### 15.4 Scene/import/package/grid/prompt 测试

- scene.get 对已加载场景读取 live 未保存 snapshot，对未加载场景走 `CacheMode.Ignore`；用 thread-id/assertion 证明 ResourceLoader、Resource遍历、scalar projection 全在主线程，worker 只接收无 Godot 引用的 scalar snapshot；active/cache/dirty/selection/UI 不变；覆盖 10,000 items、8 MiB/snapshot、256 KiB/item、64 MiB global、pin/TTL/淘汰顺序、数量、排序和 scope 隔离及稳定错误码。
- 三种 commit policy 分别做状态机测试；cancel 与 enter 使用同一 CAS barrier。`project.save` 覆盖 scene/project/layout/delete 各阶段 timeout 与 `none|partial|complete` receipt；create 覆盖 move 后成功/owner rollback；close 覆盖 ResourceSaver 开始后 timeout 仍返回真实 receipt；package import覆盖publish/reconcile。
- asset import 支持四种格式与 32 MiB 边界；拒绝 traversal、绝对、URL、data URI、symlink、超限、扩展/signature 不匹配和读取中变化；ShortAtomicCommit 覆盖 CAS 前/刚进入/commit 后 timeout-cancel；普通 import commit 后绝不被 recovery 删除。
- package preflight 覆盖 128 MiB 总量、32 MiB 单图、计数/JSON 深度、文件缺失、重复 ID、非法 role、symlink、原子 rollback与 commit 前取消。
- UI save、MCP save、close controlled save、startup recovery 使用同一 reconciler；index+scene 已持久但 layout save 失败仍 commit journal；未持久且 owner 不匹配时不删除。
- export 从 live snapshot 包含未保存修改但不触发 save；不覆盖旧包，失败/取消无半成品；导出再导入保持包定义范围内语义。
- grid 1/4096 边界、UI 刷新、dirty/save/reload；Prompt role add/update/read/save/reload。

### 15.5 Godot 与 HTTP 验收

只扩展仓库已存在的以下场景，不引用不存在的聚合 acceptance：

```text
AiSettingsUiAcceptance.tscn
McpAiGenerationAcceptance.tscn
AiAdapterPluginAcceptance.tscn
ProjectPickerAcceptance.tscn
McpResourceRuntimeAcceptance.tscn
McpSettingsUiAcceptance.tscn
McpHttpRuntimeAcceptance.tscn
CanvasPatchSecurityAcceptance.tscn
```

真实 HTTP 主路径：

```text
tools/list(31 + outputSchema)
 -> profiles.list/models.list
 -> profile.activate/model.set
 -> project.create 或 projects.recent/open
 -> assets.import
 -> scene.get/grid.mutate/prompt role
 -> ai.generate/status(execution + diagnostic)
 -> package.export/import
 -> project.save/close(saveIfDirty/cancelAiJobs)
```

真实命令：

```bash
dotnet build APP-AGAS-CS.sln -c Debug
tests/run-core-isolated.sh --no-build
dotnet build APP-AGAS-CS.sln -c Release
dotnet build APP-AGAS-CS.csproj -c ExportRelease --no-restore
tests/Godot/run-acceptance.sh AiSettingsUiAcceptance.tscn
tests/Godot/run-acceptance.sh McpAiGenerationAcceptance.tscn
tests/Godot/run-acceptance.sh AiAdapterPluginAcceptance.tscn
tests/Godot/run-acceptance.sh ProjectPickerAcceptance.tscn
tests/Godot/run-acceptance.sh McpResourceRuntimeAcceptance.tscn
tests/Godot/run-acceptance.sh McpSettingsUiAcceptance.tscn
tests/Godot/run-acceptance.sh McpHttpRuntimeAcceptance.tscn
tests/Godot/run-acceptance.sh CanvasPatchSecurityAcceptance.tscn
```

## 16. 验收标准

1. tools/list 按 §9.2 精确返回 31 个标准工具，31/31 具有 name/description/inputSchema/outputSchema/annotations，不发布内部 family/dispatch/timeout；Core 单独验证内部 metadata 与 handler 31/31 无重漏。runtime assertion 违例 fail closed 为 `AGAS_INTERNAL_ERROR` 且不记录 data。
2. 用户只说“切模型”时，Agent 可 list → activate/model.set → 省略 `profileId` generate；UI 与重启状态一致。
3. `providerId != adapterKind` 全链不串值；`ai.status` 五态及已 apply 的 Succeeded 状态都返回提交时冻结的 Profile/Provider/Adapter/Model，保留 required `alreadyApplied`；失败返回安全分类和 diagnosticId。
4. SettingsDialog 五类未保存草稿都使 MCP Profile mutation 明确冲突；同进程共享 keyed RMW 零丢更新；precommit generation漂移零写，postcommit漂移准确返回 `persisted:true,uiSynchronized:false`，后台写盘不覆盖新草稿。
5. Agent 不提交任意路径即可 create/open；create 只落到 validated MyDocuments managed root，空目标也拒绝，open/create 仅允许无项目状态，失败不污染 recent。
6. close 只支持 controlled save、不支持 discard；全部只读 gate 先于 cancel/save；project-scoped AI quiescence、主线程 ResourceSaver、StartedMainThreadCompletion、immutable outcome receipt 和四个 typed side-effect fields 均有真实测试；save 开始后的 timeout 不改写真实结果。
7. scene.get 的 ResourceLoader/Resource投影只在主线程，对 unloaded 使用 `CacheMode.Ignore`且不写 cache；cursor 过期→最旧未pin→capacity错误的淘汰顺序、TTL/预算/scope/排序通过。
8. 图片与场景包通过 `agas.imports.list` 获得 opaque expiring `sourceRef`，来源固定为 `$AGAS_HOME/import-inbox/assets`、`$AGAS_HOME/import-inbox/scene-packages`、`$AGAS_HOME/import-exchange/scene-packages`；32/128 MiB、120 秒、CancellationToken、symlink、identity revalidation、原子 rollback 和轻量响应要求全部通过。
9. grid 与 Prompt role 可修改、读取、保存、重载，非法值被拒绝。
10. package journal 在 UI/MCP save 与 startup 都 disk-first reconcile；业务数据已持久时即使 layout save 失败也 commit，只有未持久且 owner marker 匹配才删除；普通 asset import 永不被自动删除。
11. Core、上述八个定向 Godot、真实 MCP HTTP 验收全部通过；Debug、Release、ExportRelease build 为 0 error，warning 如有必须如实登记；ExportRelease 固定构建 `APP-AGAS-CS.csproj`。
12. 所有 credential-aware secret/Base64/绝对路径反向扫描通过；未连接真实第三方 Provider 必须作为待验证项报告。
13. §9.3 的 31 工具手写 manifest 与 DTO/schema/真实 JSON 四方一致；正常退出、timeout kill、异常退出后三种 runner 对覆盖环境前解析的 known real `~/.agas`、managed root、XDG app paths、Godot app_userdata fingerprints 均验证未变化。
