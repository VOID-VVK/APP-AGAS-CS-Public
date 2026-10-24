# AGAS MCP 能力扩展执行计划

> 文件：`docs/exec-plans/active/agas-mcp-capability-expansion-execution-plan.md`
> 配套方案：`docs/design-docs/agas-mcp-capability-expansion-technical-design.md`
> 基线方案：`docs/design-docs/agas-mcp-v2-agent-ops-technical-design.md` §13
> 基线：Godot `4.6.1` Mono / `.NET 8` / C# / MCP Streamable HTTP
> 状态：**Wave 1 收尾：隔离 Core 基线 966/966 与 Debug build 已验证全绿；Wave 1 基础服务已实现，尚未进入 Wave 2/3 的生产 DTO/handler/中央 tools/list 发布**
> 更新日期：2026-07-31

---

## 1. 目标与当前状态

### 1.1 本计划目标

在不破坏现有 16 个 MCP 工具的前提下，补齐以下闭环：

1. AI Profile / Provider Model 的发现、激活、修改、持久化与安全诊断。
2. AI job 冻结并返回实际 `profileId/providerId/adapterKind/modelName`，失败返回脱敏诊断身份。
3. 最近项目、创建、打开、切换、关闭的安全生命周期。
4. 未缓存场景详情读取、Grid 修改、Prompt role 读写。
5. 受控图片导入、场景包导入与导出。
6. 所有现有和新增工具使用 typed output，并在 `tools/list` 发布可验证的 `outputSchema`。
7. 用 Core、Godot、真实 Streamable HTTP `tools/list/tools/call` 完成回归与验收。

AI capability 元素值域冻结为 `textToImage|referenceEdit|regionEdit|nativeAlphaMask|guidedRegionEdit`；各 capability 数组可只返回当前 Profile/Provider 实际支持能力的子集，出现项按上述 canonical order 输出。AI job 状态保持 `Queued|Running|Succeeded|Failed|Canceled` 五态，五态都要求 `alreadyApplied`，apply 前后 job 仍为 `Succeeded`。

若 Wave 0 冻结的 15 个新增工具全部落地，正式工具数将从 **16 增至 31**；最终数量必须以 `AgasMcpToolNames.All` 和真实 `tools/list` 为准，不以文档手工计数替代验证。

### 1.2 已验证并可复用的现状

| 能力 | 当前状态 | 证据位置 |
|---|---|---|
| MCP 输入契约 | 已有 annotation descriptor、strict bind、frozen schema parity | `src/Mcp/Contracts/**`、`tests/Core/Mcp/AgasMcpGeneratedSchemaParityTests.cs` |
| MCP 输出契约 | `AgasMcpToolResponse.Data` 仍大量使用匿名对象；`Tool.OutputSchema` 为空 | `AgasMcpToolResponse`、`AgasMcpServerToolBase`、`AgasMcpToolContractTests` |
| AI Profile 设置 | 已有原子保存、凭据分文件保存、ActiveProfile、Provider catalog、可编辑 custom model | `src/Features/Ai/Settings/**` |
| AI UI 模型切换 | `AiGenerationProfileController` 可选择 Profile 并持久化当前选择/模型字段 | `src/Features/Ai/UI/AiGenerationProfileController.cs` |
| AI capability | 当前产品 contract 已固定五个允许元素值 `textToImage/referenceEdit/regionEdit/nativeAlphaMask/guidedRegionEdit`；数组是所支持能力的子集，出现项按 canonical order；本计划要求 typed output/schema/manifest 迁移时保持该语义 | `AiGenerationContracts.cs`、`AgasAiProviderCatalog.cs` |
| AI 安全诊断 | 已有 `AiGenerationSafeDiagnostic`、诊断 ID、phase/category/http status 等 allow-list 字段 | `src/Features/Ai/Generation/AiGenerationDiagnosticContracts.cs` |
| AI job | 已有 scoped store/scheduler、TTL、cancel、apply lease；job 尚未冻结 execution identity，failure 映射会丢失安全诊断详情 | `src/Mcp/AgasMcpAiJobStore.cs`、`AgasMcpAiFacade.cs` |
| AI apply | 当前 Facade 只接受并保持 `Succeeded`，重复 apply 已以 `alreadyApplied` 表达；迁移不得新增 Applied 第六态 | `AgasMcpAiFacade.cs`、现有 MCP AI acceptance |
| 项目生命周期 | UI 已有 recent、初始化、打开、关闭与 controlled save；尚无 MCP app-level bridge | `RecentProjectsStore`、`AgasProjectInitializer`、`Main.cs`、`MainWorkspace.cs` |
| 场景详情 | `ProjectSession.LoadScene` 可安全加载并缓存非当前场景；snapshot 默认不加载未缓存详情 | `ProjectSession`、`AgasMcpProjectReadModelQuery` |
| Grid / Prompt role | application/domain/UI 已有完整变更能力，MCP 输入和输出未暴露 | `ProjectMutationService`、`PromptFragmentResource`、Canvas/UI controller |
| 图片导入 | Wave 1 已有受控 inbox list/sourceRef broker、native identity复核、32 MiB/像素预算、写前临时 slot 登记、fault/cancel finally cleanup；生产 MCP handler 留到 Wave 2/3 | `AgasImportInboxService`、`AgasControlledAssetImportService`、对应 Core tests |
| 场景包 | Wave 1 已有受控 inbox/exchange list、全部引用文件 identity绑定、128 MiB预算、journal/reconciler、copy fault cleanup、全局 exchange 原子发布与跨项目 ref import；生产 Facade/UI wiring 留到 Wave 2/3 | `AgasScenePackageStagingService`、`ScenePackageImportJournal*`、对应 Core tests |

### 1.3 尚未实现（Wave 2/3 边界）

- 本计划新增的 15 个目标工具尚未进入生产 `AgasMcpToolNames.All`；Wave 1 只冻结31工具 manifest/基础服务，不提前发布 handler。
- 现有工具尚未全面迁移 typed output，任何工具尚未发布 `outputSchema`。
- 项目切换若直接复用当前 `Main.OpenWorkspace` 会绕过旧 workspace 的 dirty / Blueprint active-operation 门禁；开发前必须先建立生命周期 bridge。
- 既有 UI `ScenePackageStore.Import` 仍是 legacy 路径；未来 MCP 只能接 `AgasScenePackageStagingService` 的 opaque sourceRef 安全入口，禁止直接暴露 legacy 任意路径方法。
- 真实第三方 Provider、Windows/Linux 实机和远程无共享文件系统客户端不属于本轮已验证事实。
- `tests/run-core-isolated.sh`、`tests/run-core-isolated-self-test.sh` 与 Godot acceptance isolation self-test 已存在；后续完整 Core 验证必须继续经 runner 隔离真实 HOME/AGAS/Godot/XDG 数据根。
- 用户确认的合并基线为隔离 Core **966/966**、Debug build green；此前两个已知失败已修复，不再列为当前阻塞。

---

## 2. Wave 0：契约冻结、方案 CR 与基线修复

### 2.1 已确认工具名

```text
AI（5）
agas.ai.profiles.list
agas.ai.models.list
agas.ai.profile.activate
agas.ai.model.set
agas.ai.diagnostics

项目生命周期（4）
agas.projects.recent
agas.project.open
agas.project.create
agas.project.close

项目编辑与资源（5）
agas.scene.get
agas.grid.mutate
agas.assets.import
agas.scene.package.import
agas.scene.package.export
```

`agas.prompt.mutate` 只扩展 `role`，不增加新工具。

### 2.2 已确认产品与安全决策

1. `profiles.list/models.list/diagnostics` 在没有打开项目时也可调用。
2. `profiles.list` 仅返回安全 Profile 描述、active/effective current 和 `credentialConfigured`；不得返回 credential、BaseUrl、认证头。
3. `models.list` 返回 Provider catalog 的模型预设，并声明 `supportsCustomModel=true`。
4. `profile.activate` 持久化 ActiveProfile 并同步 UI；`model.set` 只修改当前 active profile，允许 relay custom alias，并持久化、同步 UI。
5. SettingsDialog 存在未保存草稿时，`profile.activate/model.set` 必须返回稳定冲突错误，不覆盖草稿。
6. `ai.diagnostics {profileId?}` 只做配置预检；固定返回 `networkChecked=false`，不得伪装成真实网络连通性检查。
7. AI job 在提交时、仍持有 settings mutation lease/generation 的同一临界区内，先经过 credential-aware safe projector，再冻结纯安全 execution identity：`profileId/providerId/adapterKind/modelName`；旧 job 不因后续切模型改变。不得在 status 时反查当前设置，也不得把 key、BaseUrl、原始未投影 label 或完整 Profile 放进 job store。`ai.status` 的 `Queued|Running|Succeeded|Failed|Canceled` 五态都必须含 required `alreadyApplied:boolean`；apply 前后 job state 都保持 `Succeeded`，只通过 `alreadyApplied` 与 applied metadata 表达 apply 结果。`agas.ai.apply` success 的 `state` 也固定为 `Succeeded`。
8. AI failure/status 仅允许返回 `diagnosticId/errorCode/phase/category/httpStatus/upstreamCode` 等 allow-list 字段。
9. `project.open {projectRef}` 只接受 `agas.projects.recent` 返回的 opaque ref；`open/create` 均只允许当前无项目时执行，有项目时稳定返回 `AGAS_PROJECT_ALREADY_OPEN`。最近项目输出不得包含绝对路径或可逆路径 hash。
10. `project.create {name}` 仅在生产 `Environment.SpecialFolder.MyDocuments/AGAS Projects/<sanitized-name>` 下创建，目标必须**不存在**，空目录也拒绝。MyDocuments 为空、非 rooted、不可访问或路径身份不安全时 fail closed，禁止回退 cwd。根、父链、目标链全部拒绝 symlink/reparse，并在 publish 前后复核 root identity。发布后若 Main open 失败，仅当 owner marker 与目录 identity 仍匹配本 operation 时回滚新目标并恢复 picker；响应只返回 opaque `projectRef/name`。
11. 其他目录必须先由用户在 UI 授权打开一次，进入 recent 后才能由 MCP 使用 `projectRef` 打开。每次 recent list 都 rotate ref：同一 root 的旧 ref 立即吊销，每个 root 最多一个 active ref；TTL 固定 10 分钟、全进程最多 20 个 active refs。过期、淘汰、recent 删除、root identity 变化或应用退出都失效，轮询不得无界增长。
12. `project.close {saveIfDirty:false,cancelAiJobs:false}` 不支持 discard。固定顺序为：主线程只读 gates（project scope/generation、Blueprint busy、dirty/AI 授权）→ 如获授权则取消当前 project scope 并在后台等待 quiescence → 回主线程复核 lease/generation → 在主线程同步执行现有 controlled save/`ResourceSaver` → finalize close。禁止把 Godot Resource/Node 或整段现有 save coordinator 放到 worker。取消已完成但后续 save/finalize 失败时不得宣称零变化；close success 和 handler 已开始后的 failure typed side-effect data 固定含 `projectStillOpen:boolean`、`saved:boolean`、`aiCancellationRequested:boolean`、`saveState:"none|partial|complete"` 四个 required 字段，并且只能取自最后 immutable outcome receipt。handler 尚未开始的普通 transport/executor/infrastructure failure 可以省略 side-effect data；一旦 handler 已开始，failure（包括 runtime fail-closed）必须返回准确 receipt。项目仍打开时不得切 picker/context。
13. `agas.imports.list` 分页列出 `$AGAS_HOME/import-inbox/assets/`、`$AGAS_HOME/import-inbox/scene-packages/` 与 `$AGAS_HOME/import-exchange/scene-packages/`，仅返回 kind/name/size/type/validation state 与签名、不可猜、默认15分钟过期的 opaque `sourceRef`；broker 全进程 cap=4096，先清过期再按签发序淘汰最旧。禁止 path/Base64/manifest/大 metadata。import 只接受 list/export-issued ref并复核 package/manifest/全部引用文件 native identity。资产单文件上限 32 MiB；场景包单图 32 MiB、manifest 1 MiB、总 payload 128 MiB。三个 heavy 工具均使用 120 秒 timeout，其余 28 个工具为 30 秒。
14. Import 全链传播 cancellation token：超时/调用方取消时，若尚未原子发布只清本 operation staging；不得留下半成品、journal 或内存变更。最终 Resource/session commit 仍必须在 Godot 主线程完成。
15. Package export 由主线程捕获 live immutable scene snapshot；已加载场景优先用 live 状态，未加载场景才用 `ResourceLoader.CacheMode.Ignore` fresh read。不得 silent save、切场景、填 session cache；只返回项目相对 manifest 路径，构建失败不得破坏旧导出。
16. `scene.get` 对已加载场景捕获 live immutable snapshot；未加载场景使用 `CacheMode.Ignore` fresh read且不调用 `CacheScene/ChangeCurrentScene/UI callback`。`ResourceLoader`/repository load、Godot Resource 属性遍历和 Resource→immutable scalar projection **全部在主线程**；worker 只能接收已经完全脱离 Godot Object/Resource 的 scalar snapshot，做排序、预算、分页与 cursor 工作。cursor broker 保存 immutable scalar items，按 project scope 隔离；TTL 5 分钟、全进程最多 128 个 snapshots、单项目最多 32 个，同时硬限制每 snapshot 最多 10,000 items、单 item UTF-8 256 KiB、单 snapshot UTF-8 8 MiB、全局 retained payload 64 MiB。单 item/snapshot 超限返回 `AGAS_SCENE_SNAPSHOT_TOO_LARGE`，全局预算无法预留返回 `AGAS_CURSOR_CAPACITY`，不得建立 cursor；数量淘汰按冻结顺序并原子释放预算。不得改变 active/current、session cache、dirty、selection 或 Blueprint 状态。
17. Prompt role wire 值冻结为 lower camel：`general/environment/negativeConstraint`；新增、查询、修改、保存、重载均可观测。
18. Profile RMW 使用按规范化 AGAS_HOME/settings path 在**同一进程所有 UI/MCP Store 实例间共享**的 keyed gate；每次 gate 内 fresh-load、获取 mutation lease/generation、字段级 merge并写同目录 temp。SettingsDialog 对 name/provider/model/baseUrl/credential 五类 draft 分别维护 dirty bit；persisted fingerprint 覆盖 resourceVersion、ActiveProfile、Profile顺序及每项 `Id/Name/ProviderId/ModelName/BaseUrl`，credential draft 由独立 dirty bit gate。atomic replace 前回主线程复核 lease、workspace、generation、active profile 与 persisted fingerprint：任一 **precommit generation** 漂移都删除 temp、返回 `AGAS_AI_SETTINGS_CONFLICT` 且磁盘零写。atomic replace 是 settings commit point；replace 后的 **postcommit generation** 漂移不得回滚已持久设置、不得覆盖新 UI/workspace/draft，仍返回 success `persisted:true,uiSynchronized:false`，下次 clean UI fresh-load；只有仍匹配时才主线程同步并返回 `uiSynchronized:true`。
19. `ProviderId` 必须从 stored profile → resolved generation profile → job submission/snapshot 全链独立传播，不得由 `AdapterKind` 反推。所有 Profile/Provider/diagnostic 输出只通过统一 safe projector，禁止任何 Facade 自行挑字段。所有输出 `providerId` 使用独立 primitive `^[a-z0-9][a-z0-9._-]{0,79}$`，不得误套不允许 dot 的 `safeId`。
20. Envelope 默认保持现有 wire：成功省略 null `error`，普通失败省略 null `data`，协议不输出显式 null。`project.close` success 与 handler 已开始后的 failure 要求 typed side-effect data variant，以四个 required 字段表达准确 receipt；handler 尚未开始的普通 infrastructure failure 允许走无 data 的普通 failure variant。其 outputSchema 必须同时覆盖这两个 failure 分支。每工具 output schema 使用 typed success data + 统一 failure DTO；`agas.ai.status` 指定jobId时success data直接是job DTO根对象、未指定时才是jobs列表根对象，两个 variants 都做direct handler测试；五态 `alreadyApplied` 均 required，`agas.ai.apply.state` 固定 `Succeeded`；Blueprint `nodeStatuses[].state` 保持 `0=Idle,1=Running,2=Succeeded,3=Failed,4=Skipped` numeric wire。运行时类型/schema不匹配fail closed为安全`AGAS_INTERNAL_ERROR`，若 close handler 已开始则 fail-closed data 只能来自最后 receipt。Output contract build 必须拒绝 type-level/property-level `JsonConverter` 等 serialization override，避免 CLR 校验通过后实际 serializer 改写 wire 或泄漏 secret。
21. 31 个工具必须有独立于生成器的逐字段 contract manifest/golden oracle，并与技术设计 §9.3 的**实际 wire contract**逐行一致；Blueprint `resourceUrl` 必须采用第 25 条与 §9.3 一致冻结的独立 `projectUri`：冻结每个 success variant及共享嵌套 DTO 的字段顺序、required/optional、类型、enum、casing、排序和数值边界；union 成员只能记录在 manifest metadata，禁止把 `oneOf`/`variant` 伪装成普通 wire Fields；以 manifest↔CLR DTO↔generated schema↔真实 serialization 四方 parity 验证，禁止用同一运行时 schema 生成器同时生成 expected 与 actual 自证正确。capability 数组元素值域仅允许 `textToImage|referenceEdit|regionEdit|nativeAlphaMask|guidedRegionEdit`；数组可为实际支持能力的任意子集，出现项按该 canonical order 输出。
22. Package import journal 记录 operationId、owner marker、new sceneId、预期 scene `.tres` 与全部最终 UUID assets。`src/Core/Persistence/ScenePackage*` 是 ScenePackage persistence/journal/reconciler 原子规则的唯一 owner；Application 只做 orchestration，UI save、MCP save、startup 只能调用其窄 port，`src/Mcp/**` 不直接判定或删除 journal/artifact。durable commit 不依赖整个 save 返回成功：只要磁盘 project index 已引用 new sceneId 且 scene 文件完整落盘，就提交 journal；未 durable 时只删除该 journal 明确拥有的 scene/assets，禁止扫描或删除其他文件。
23. executor 冻结三类 `CommitPolicy`：`None`、`ShortAtomicCommit`、`StartedMainThreadCompletion`。取消/timeout 与 handler 必须在同一 execution state 上做 CAS，状态为 `Pending -> Canceled | ShortCommitting | MainThreadCompleting -> Completed`；token 预检查仅是 fast-path，禁止 check-then-commit。进入任一 committing 状态后，后到取消只记录 `cancellationObservedAfterStart=true`，不得改写最终真实结果。逐工具 policy/commit point 冻结如下：
    - `agas.project.open`：`StartedMainThreadCompletion`，主线程 lease/generation 复核后、workspace install/finalize 开始前；
    - `agas.project.create`：`StartedMainThreadCompletion`，`Directory.Move(staging,target)` 前；必须更新 move/open/recent/owner rollback 多阶段 receipt；
    - `agas.project.close`：`StartedMainThreadCompletion`，需要保存时在第一个 `ResourceSaver` 前，否则在 finalize 前；必须更新 save/finalize 多阶段 receipt；
    - `agas.project.save`：`StartedMainThreadCompletion`，第一个主线程 `ResourceSaver` 前；scene/project/layout/delete 每阶段更新 `saveState/savedFiles/failedFiles/pendingDirtyFiles` receipt；
    - `agas.scene.package.import`：`StartedMainThreadCompletion`，主线程 session/index/journal publish 前；必须完成 publish 或 owner/journal reconcile，并更新多阶段 receipt；
    - `agas.ai.profile.activate`、`agas.ai.model.set`：`ShortAtomicCommit`，settings atomic replace；CAS 前完成 IO 与 precommit generation 复核；
    - `agas.assets.import`：`ShortAtomicCommit`，最终唯一 artifact rename；copy/hash/decode 均在 CAS 前；
    - `agas.scene.package.export`：`ShortAtomicCommit`，最终 export directory rename；全部写 staging 在 CAS 前；
    - `agas.scene.get`、`agas.ai.profiles.list`、`agas.ai.models.list`、`agas.ai.diagnostics`：`None`；只读 pipeline 不申请 commit lease。
    其余非 `AsyncOrchestrated` 工具按技术设计 §8.5/§9.2 保持 `None` 或既有同步语义。覆盖 CAS 前、刚进入 committing、commit 后 timeout/cancel 三类确定竞态。
24. `tools/list` 只验 MCP wire 可发现字段：31 个唯一 name、description、inputSchema、outputSchema、annotations；不要求或通过 `_meta` 暴露内部 family/dispatch/timeout。family、dispatch、timeout、success variant 与 per-tool handler 唯一性由独立 descriptor/handler registry parity 测试验证。
25. Blueprint `output.resourceUrl` 保留现有 opaque `project:///...` project URI wire，只作为兼容性 project URI。技术设计 §9.3 与 manifest 均独立定义 `projectUri` primitive（opaque string，`project:///` 前缀），`resourceUrl` 使用 `projectUri`；不把它声称为已注册 MCP resource、可由 `resources/list` 发现或可由 `resources/read` 解引用，也不是文件 URL/绝对路径。

### 2.3 31 工具输出 manifest 与实际 contract

下表是执行期必须逐项落到 `AgasMcpFrozenOutputManifest.V1` 的 31/31 清单；字段级 required/optional、closed object、primitive/path、排序和嵌套 DTO 以技术设计 §9.3 为冻结来源，Blueprint `resourceUrl` 按第 25 条及 §9.3 一致使用独立 `projectUri`/opaque `project:///...`。不能以 production generator 反向生成此表。最终 Core 必须比较 manifest ↔ CLR DTO ↔ generated schema ↔ 真实 handler serialization/output 四方 parity。

| # | 工具 | 实际 success data contract / 特殊约束 |
|---:|---|---|
| 1 | `agas.projects.recent` | `AgasMcpRecentProjectsDataDto`；opaque refs，不返回 path |
| 2 | `agas.project.open` | `AgasMcpProjectOpenDataDto` |
| 3 | `agas.project.create` | `AgasMcpProjectCreateDataDto` |
| 4 | `agas.project.close` | `AgasMcpProjectCloseDataDto(projectStillOpen,saved,aiCancellationRequested,saveState)`；handler 已开始 failure 同型 receipt，未开始 ordinary infrastructure failure 可无 data |
| 5 | `agas.project.snapshot` | `AgasMcpProjectSnapshotDataDto` |
| 6 | `agas.assets.list` | `AgasMcpAssetsListDataDto` |
| 7 | `agas.imports.list` | `AgasMcpImportsListDataDto`；轻量分页，签发 opaque expiring sourceRef，无 path/Base64/manifest/metadata |
| 8 | `agas.assets.import` | `AgasMcpAssetImportDataDto`；输入 list-issued sourceRef |
| 9 | `agas.scene.get` | `AgasMcpSceneGetDataDto` |
| 10 | `agas.scene.mutate` | `AgasMcpSceneMutationDataDto` |
| 11 | `agas.scene.package.import` | `AgasMcpScenePackageImportDataDto`；输入 list/export-issued sourceRef |
| 12 | `agas.scene.package.export` | `AgasMcpScenePackageExportDataDto(packageRef,sourceRef,referenceImageCount)`；全局 exchange |
| 13 | `agas.grid.mutate` | `AgasMcpGridMutationDataDto` |
| 14 | `agas.layer.mutate` | `AgasMcpLayerMutationDataDto` |
| 15 | `agas.image.mutate` | `AgasMcpImageMutationDataDto` |
| 16 | `agas.prompt.mutate` | `AgasMcpPromptMutationDataDto`；role lower camel |
| 17 | `agas.view.set` | `AgasMcpViewSetDataDto` |
| 18 | `agas.project.save` | `AgasMcpProjectSaveDataDto(saveState,savedFiles,failedFiles,pendingDirtyFiles)`；最后多阶段 receipt |
| 19 | `agas.selection.set` | `AgasMcpSelectionSetDataDto` |
| 20 | `agas.selection.get` | `AgasMcpSelectionGetDataDto` |
| 21 | `agas.ai.profiles.list` | `AgasMcpAiProfilesDataDto`；capability 为所支持能力子集，元素仅限五值，出现项按 canonical order |
| 22 | `agas.ai.models.list` | `AgasMcpAiModelsDataDto`；capability 为所支持能力子集，元素仅限五值，出现项按 canonical order |
| 23 | `agas.ai.profile.activate` | `AgasMcpAiProfileActivatedDataDto`；`persisted/uiSynchronized` 分别表达 postcommit 持久化/UI 同步 |
| 24 | `agas.ai.model.set` | `AgasMcpAiModelSetDataDto`；同上 |
| 25 | `agas.ai.diagnostics` | `AgasMcpAiDiagnosticsDataDto`；`networkChecked=false`，capability 为所支持能力子集，元素仅限五值，出现项按 canonical order |
| 26 | `agas.ai.generate` | `AgasMcpAiGenerateDataDto`；初态 `Queued` + submission-time execution |
| 27 | `agas.ai.status` | direct `AgasMcpAiJobDto` 或 `AgasMcpAiStatusListDataDto`；五态 union 均 required `alreadyApplied`，不得 `{job}` 包装 |
| 28 | `agas.ai.cancel` | `AgasMcpAiCancelDataDto`；state 五态 |
| 29 | `agas.ai.apply` | `AgasMcpAiApplyDataDto`；`state:"Succeeded"` + required `alreadyApplied` |
| 30 | `agas.blueprint.capabilities` | `AgasMcpBlueprintCapabilitiesDataDto`；registry/workflow/input requirement 顺序稳定 |
| 31 | `agas.blueprint.execute` | `AgasMcpBlueprintExecuteDataDto`；numeric node status 保持既有 wire；`resourceUrl:projectUri` 仅现有 opaque `project:///...` URI，不宣称 MCP resource |

所有 AI capability 数组的元素值域仅允许 `textToImage|referenceEdit|regionEdit|nativeAlphaMask|guidedRegionEdit`；数组可为实际支持能力的子集，出现项按该 canonical order 输出。所有 object closed、无值 optional 字段省略且禁止 JSON `null`；Blueprint `nodeStatuses[].state` 继续为 `0..4` integer enum。逐字段合同不得用本计划摘要替代技术设计 §9.3 manifest。

### 2.4 Wave 0 任务

| ID | 任务 | 文件范围 | 依赖 | 验收证据 |
|---|---|---|---|---|
| T0.1 | 将上述输入、输出、错误码、annotation、幂等性与 destructive/read-only hints 冻结到技术方案 | 技术方案文档；不改代码 | 无 | 三方向 CR 记录均无 P0/P1 |
| T0.2 | 冻结 output envelope（null 省略）、close handler-started failure receipt/未开始 infrastructure failure 可无 data、31工具逐字段manifest/golden、AI五态required `alreadyApplied`、`ai.apply=Succeeded`、capability子集/五值元素域/canonical order、`ai.status` single/list、Blueprint numeric enum与独立 `projectUri` contract、runtime fail-closed与序列化口径 | 技术方案文档 | T0.1 | expected oracle独立于生成器；close四字段receipt准确；manifest与实际contract一致；其余wire兼容 |
| T0.3 | 冻结 lifecycle root/rotate ref、无项目 open/create，以及“只读 gates→cancel/quiesce→主线程同步 controlled save→finalize”的 close 顺序、四字段副作用 DTO 与多阶段 receipt | 技术方案文档 | T0.1 | 不存在 discard/任意路径/worker ResourceSaver；handler开始后的失败准确报告最后 receipt |
| T0.4 | 冻结 `$AGAS_HOME` inbox、32/128 MiB、三个 heavy 工具 120 秒 timeout、三类 CommitPolicy/同状态CAS/逐写工具commit point、ScenePackage 唯一 persistence owner、journal durable reconcile 与 startup recovery | 技术方案文档 | T0.1 | traversal/symlink/CAS前后cancel/partial save/restart 均有明确预期 |
| T0.5 | 方案与本计划进行后端、UI、协议/测试三路只读复审 | 文档 | T0.1–T0.4 | 所有 P0/P1 清零后才进入 Wave 1 |
| T0.6 Core isolated runner（`core-isolation-owner`） | 新增 `tests/run-core-isolated.sh`：固定运行 `tests/Core/APP-AGAS-CS.Core.Tests.csproj -c Debug`，接受透传的其余 `dotnet test` 参数；每次创建独立 HOME、AGAS_HOME、XDG config/data/cache、GODOT_USER_HOME/user:// 与 managed roots，退出/timeout/异常后校验真实已知目录 fingerprint 零变化 | `tests/run-core-isolated.sh`、test-only root/fingerprint helper；不改产品语义 | T0.1 | runner 自测覆盖正常/timeout/异常清理；后续所有 Core 命令只能经此脚本 |
| T0.7 Baseline external image loader | 修复 `ExternalImageLoaderTests.ProductSources_DoNotUseGdscriptJavascriptOrJsonPersistence` 当前失败，先验证失败证据再做最小修复 | 对应 product source 与该 Core test；owner 先定位后冻结文件 | T0.6 | `tests/run-core-isolated.sh --filter "FullyQualifiedName~ExternalImageLoaderTests.ProductSources_DoNotUseGdscriptJavascriptOrJsonPersistence"` 通过，且无放宽测试 |
| T0.8 Baseline AI cancellation | 修复 `AiGenerationServiceTests.CallerCancellation_AlsoStopsGeneratedImageDownload` 当前失败，保证 caller cancellation 真实停止 generated image download | 对应 AI generation/download service 与该 Core test；与 T0.7 文件 owner 隔离 | T0.6 | `tests/run-core-isolated.sh --filter "FullyQualifiedName~AiGenerationServiceTests.CallerCancellation_AlsoStopsGeneratedImageDownload"` 通过，且不吞取消/不留下载任务 |
| T0.9 Baseline full green | 在 T0.7/T0.8 合并后做 Debug build 与完整隔离 Core 基线，记录总数/失败数与真实目录 fingerprint | 验证任务，不新增产品改动 | T0.5–T0.8 | `dotnet build APP-AGAS-CS.sln -c Debug` 与 `tests/run-core-isolated.sh --no-build` 全绿；否则继续停留 Wave 0 |

**Wave 0 出口**：方案和计划 CR 明确“无 P0/P1”，上述两个已知 Core 失败均有针对性回归证据，完整 `tests/run-core-isolated.sh --no-build` 基线全绿且隔离 fingerprint 未变化。任一条件不满足都停留 Wave 0，不得开始 Wave 1。

---

## 3. 开发波次与文件所有权

### 3.1 并行修改规则

- 同一波次不得让两个 worker 修改同一文件。
- `AgasMcpToolNames.cs`、`AgasMcpToolInputs.cs`、`AgasMcpToolSchemas.cs`、`AgasMcpToolDescriptorRegistry.cs`、`AgasMcpDescriptorHandlerRegistry.cs`、`AgasMcpToolRuntime.cs`、`MainMcpRuntimeComposition.cs` 是中央集成文件，只交给当波唯一 central integration owner。
- 家族 worker 新增独立 Facade/DTO/Service 文件；需要中央注册时先完成本地 direct API 测试，随后由 integration owner 统一接线。
- `Main.cs` 同时涉及 AI UI 同步和项目生命周期，只允许 UI integration owner 顺序修改。
- `src/Core/Persistence/ScenePackage*`、`src/Application/*ScenePackage*`、`ProjectSaveCoordinator.cs`、`MainWorkspaceSaveController.cs`、`AgasMcpProjectFacade.ProjectSave` 与 startup recovery 同时影响 journal durable barrier；固定由同一 `package-persistence-owner` 贯穿实现与集成，其他 worker 只能调用其 port，禁止复制 reconcile/删除规则。
- `tests/Godot/run-acceptance.sh` 及 acceptance 环境隔离 helper 只交给 `acceptance-isolation-owner`；各功能 worker 不自行读写真实 HOME/user:// 来造 fixture。
- `tests/run-core-isolated.sh` 及 Core 环境隔离/fingerprint helper 只交给 `core-isolation-owner`；任何 lane 的 Core 命令必须调用该 runner，不得直接 `dotnet test tests/Core/...`。
- 每个任务完成后先跑最小隔离 Core/build；发现其他 agent 已有改动时基于现状兼容，不回退他人修改。

### Wave 1 — 基础设施与纯服务（可并行）

| ID / lane | 任务 | 独占文件范围 | 依赖 | 最小验收 |
|---|---|---|---|---|
| T1.A Output/manifest | **Wave 1 已实现** typed output registry/schema builder 基础：支持 closed object、null字段省略、array、enum、日期、discriminated DTO variants 及 success/failure envelope；拒绝 type/property serializer override；providerId 使用独立 dot-safe primitive；frozen union 只用 metadata，不生成 `oneOf/variant` wire field。新增手写版本化 `AgasMcpFrozenOutputManifest.V1`，逐项覆盖 §2.3/技术设计 §9.3 的31个实际contract、AI五态required `alreadyApplied`、`ai.apply=Succeeded`、capability子集/五值元素域/canonical order、Blueprint numeric enum与独立 `projectUri`；用独立 test-fixture DTO/variant/envelope 验证 builder 与类型/schema漂移安全 fail closed。本 lane 不新增 Wave 2 的生产 typed data DTO/handler，不发布 Wave 3 的 `outputSchema` | `src/Mcp/Contracts/Output/**`、`src/Mcp/Contracts/Runtime/*Output*`、`tests/Core/Mcp/Contracts/*golden*`；对应 Core tests | Wave 0 | manifest expected 不由 production generator 生成；真实 secret-writing converter 无法注册；dot provider id schema/runtime 通过且非法 casing 拒绝；union golden 禁止伪 wire field；实际 manifest↔CLR DTO↔schema↔真实 handler serialization/output 四方逐字段 parity 保留到 T3.8 完成 |
| T1.B AI settings | 抽取 Profile 查询/激活/model set/配置诊断 service；引入同进程 shared keyed gate、五类 draft、mutation lease/generation、统一 safe projector；atomic replace 前 precommit generation 漂移 conflict/零写，replace 后 postcommit 漂移成功但 `uiSynchronized:false`；resolved generation profile 增独立 ProviderId，并为 submission-time lease 内安全投影/冻结 identity 建立纯合同与 fixture 基础，同时保持 capability 数组为所支持能力子集、元素仅限五值且按 canonical order，以及 ai.apply Succeeded contract，并保留第三方 adapter SDK 既有 constructor 二进制/源码兼容入口。本 lane 不修改 `AgasMcpAiJobStore`/scheduler/status handler，job store 全链接入留到 T2.A | `src/Features/Ai/Settings/**`、`src/Features/Ai/Generation/AiGenerationContracts.cs`、adapter fixture tests；不改 MCP central files或 `AgasMcpAiJobStore`/scheduler | Wave 0 | 多Store/UI/MCP无lost update；五类draft/precommit漂移零写；postcommit漂移返回`persisted:true,uiSynchronized:false`且不覆盖新草稿；safe projector/ProviderId/identity fixture 不含 secret；job store 无 secret 的真实全链验收保留到 T2.A |
| T1.C Lifecycle | 建立 app-level lifecycle service/port：rotate/bounded opaque ref、固定 Documents 根、test root resolver、root identity/owner marker、只读 gates、project quiescence contract、主线程同步 save port与四字段 immutable outcome receipt；消费 T1.F 唯一定义的 `StartedMainThreadCompletion` CAS primitive，不在 lifecycle lane 复制状态机 | 新增 `src/Application/*ProjectLifecycle*`、`src/Core/Persistence/*ProjectAccess*`、scheduler quiescence contracts/tests；不改 `Main.cs` 或另建 CAS 状态机 | Wave 0、T1.F | ref rotate、10分钟TTL/20 cap；MyDocuments无效fail closed；通过 T1.F primitive 验证 cancel后save失败返回四个required字段且项目仍打开；未开始 infrastructure failure 可无data |
| T1.D Import/package（`package-persistence-owner`） | **Wave 1 已实现**：受控 inbox/exchange 轻量分页 list、签名/过期/cap=4096 opaque sourceRef、asset/package 全 native identity复核；copy destination 先登记 operation slot 再写并 finally cleanup；Core Persistence 唯一定义 journal/reconciler；export commit前prepare inactive ref（不触发cap淘汰）、同根原子发布后activate，postcommit fault保持真实success/ref。本 lane 不提前注册生产 MCP handler | `src/Application/AgasImportInboxService.cs`、`*AssetImport*`、`*ScenePackage*`、`src/Core/Persistence/ScenePackage*`；专属 Core tests | Wave 0 | 隔离定向；单图32MiB、manifest1MiB、package128MiB；引用替换、unpaged深树、symlink、copy fault、cap/TTL、gate拒绝不淘汰旧ref、postcommit receipt、跨项目 exchange 回归通过 |
| T1.E Scene query | 建立 live/fresh scene source selector、`CacheMode.Ignore` fresh read、scene invariant validator、bounded cursor snapshot broker；ResourceLoader、Resource遍历、scalar projection都冻结在主线程，worker只接收纯scalar snapshot | 新增 `src/Application/*SceneDetail*`、repository fresh-load窄入口、专属 Core tests | Wave 0 | thread-id证明load/projection主线程；loaded dirty取live；uncached取fresh；TTL5m/128/32及10,000items、256KiB/item、8MiB/snapshot、64MiB/global预算正确 |
| T1.F Runtime orchestration | 新建通用 per-tool handler registry、`AsyncOrchestrated` pipeline 与三类 CommitPolicy/CAS primitive；所有 descriptor 均可注册 direct handler，所有标为 `AsyncOrchestrated` 的工具走同一 capture/background/recheck/commit 框架，并逐工具冻结 §2.2 的 policy/commit point，不做 family 特判 | 新增 `src/Mcp/Runtime/**` 与 `tests/Core/Mcp/*Orchestration*`；不改中央 registry/runtime | Wave 0 | 31 个 fake descriptor 可逐工具绑定；unknown/duplicate拒绝；`Pending/Canceled/ShortCommitting/MainThreadCompleting/Completed`原子仲裁；CAS前/刚进入/commit后结果确定 |
| T1.G Acceptance isolation（`acceptance-isolation-owner`） | 加固 runner：每 case 建独立 HOME、AGAS_HOME、XDG_CONFIG_HOME、XDG_DATA_HOME、XDG_CACHE_HOME、GODOT_USER_HOME、Godot `user://` data root 与 injected managed root；trap清理并做真实目录fingerprint零变化检查 | `tests/Godot/run-acceptance.sh`、新增 test-only root provider/helper、边界 Core tests | Wave 0 | 正常/timeout kill/模拟异常退出后真实home/Documents/AGAS_HOME/Godot user-data零变化；并行case互不污染 |

Wave 1 每个 lane 的真实验证：

```bash
dotnet build APP-AGAS-CS.sln -c Debug
tests/run-core-isolated.sh --no-build
```

### Wave 2 — 工具家族实现与 typed output 迁移（可并行）

Wave 2 先由 contract owner 只新增各家族 input/output DTO 类型，不修改中央工具清单；随后各 lane 并行实现 direct Facade handler 和单元测试。

| ID / lane | 任务 | 独占文件范围 | 依赖 | 最小验收 |
|---|---|---|---|---|
| T2.0 Contract seed | 新增 15 工具输入 DTO、所有 31 工具 typed data DTO variants并逐项映射独立 golden manifest；close 固定输入及 `projectStillOpen/saved/aiCancellationRequested/saveState` 四字段 receipt DTO，handler-started failure可用、未开始普通infrastructure failure可无data；AI五态/`alreadyApplied`、`ai.apply=Succeeded`、capability子集/五值元素域/canonical order、Prompt role lower camel与Blueprint独立 `projectUri`；此时不进 `ToolNames.All` | `AgasMcpToolInputs.cs`；新增按家族拆分的 `*ToolOutputs.cs` / DTO 文件；golden manifest | T1.A | 31/31 manifest↔生产 CLR DTO↔generated schema逐字段parity；close两类failure均通过schema；unknown/case/range/null/条件门禁；其余failure省略data；真实handler serialization/output parity保留到T3.8 |
| T2.A AI | 实现 AI 5 工具；ProviderId 全链进入 submission-time safe identity，保留 allow-list diagnostic；所有 Profile/Provider 输出走 safe projector；保持 capability 数组为所支持能力子集、元素仅限五值且按 canonical order，以及 `ai.apply` Succeeded 现 contract；`ai.status`五态均required `alreadyApplied`，`{jobId}` success data直接是`AgasMcpAiJobDto`根对象（无`job`包装），未指定jobId才是`{jobs:[...]}`列表variant；既有 AI 输出迁 typed variants | `AgasMcpAiFacade.cs`、新增 AI settings Facade、`AgasMcpAiJobStore.cs`、`AgasMcpAiJobScheduler.cs`、AI MCP tests | T1.B、T1.F、T2.0 | direct-root/list direct调用均匹配自己的variant；apply前后state仍Succeeded；旧job不漂移；第三方ProviderId正确；job store/diagnostic无credential原文 |
| T2.B Lifecycle | 实现 lifecycle Facade/bridge；open/create仅无项目；close严格执行只读 gates→cancel/quiesce→主线程同步 controlled save→StartedMainThreadCompletion finalize，create owner rollback；create/close/save维护多阶段 immutable receipt。close success和handler-started failure返回四字段 typed receipt，未开始普通 infrastructure failure 可无data | 新增 `IAgasMcpApplicationLifecycleBridge`、Lifecycle Facade；`Main.cs` 暂不接线 | T1.C、T1.F、T2.0 | cancel后save失败为`projectStillOpen=true,saved=false,aiCancellationRequested=true,saveState=none|partial`且准确；timeout不保存；finalize start后不误报timeout；无worker save |
| T2.C Project edit | 实现 `scene.get/grid.mutate`、Prompt role；现有 Project Facade 全输出迁 typed variants；扩 role/view/detail read model；scene.get 的 ResourceLoader、Resource遍历与scalar projection都在主线程 | `AgasMcpProjectFacade.cs`、`AgasMcpProjectReadModelQuery.cs`、`ProjectReadModelContracts.cs`、Project MCP tests | T1.E、T2.0 | thread-id证明Resource load/projection主线程；live/fresh读取且状态不变；cursor 5m/128/32与10,000items、256KiB/8MiB/64MiB预算；role save/reload |
| T2.D Assets/package（`package-persistence-owner`） | 实现 imports list 与 assets/package Facade；三个 heavy 工具使用120秒timeout/cancellation；assets.import/package.export为ShortAtomicCommit final rename，package.import为StartedMainThreadCompletion session/index/journal publish并维护多阶段receipt；package导入通过唯一 persistence port 一次性登记cache/index/dirty+journal；export用live immutable snapshot且无silent save；typed output | 新增独立 Import/Asset/Package MCP Facade、专用 path adapter、tests；不复制package persistence规则，不改 Project Facade/SaveController | T1.D、T1.E、T1.F、T2.0 | list分页轻量且无路径/大字段；import只收opaque ref并复核全部native identity；package总payload128MiB；export全局exchange ref可跨项目导入 |
| T2.E Blueprint output | 将现有 Blueprint capabilities/execute 改为 typed data，不改变行为或同步执行语义；`nodeStatuses[].state` 保持既有 numeric enum wire并冻结数值集合；`resourceUrl`保持现有opaque `project:///` URI且不注册/声称MCP resource | `AgasMcpBlueprintFacade.cs`、Blueprint output DTO/tests | T1.A、T2.0 | numeric enum与project URI golden/运行时一致；既有 Blueprint tests 通过；响应无图片字节/Base64 |

Wave 2 验证：

```bash
dotnet build APP-AGAS-CS.sln -c Debug
tests/run-core-isolated.sh --no-build
```

### Wave 3 — 中央协议注册与 UI/runtime 接线（串行 integration owner）

| ID | 任务 | 文件范围 | 依赖 | 验收 |
|---|---|---|---|---|
| T3.1 Central contracts | 由唯一 owner 注册最终 31 工具名、input schema、family、dispatch、annotation、success/failure DTO type/output schema、CommitPolicy与通用per-tool handler；为 assets import、package import、package export 三个 descriptor 配120秒，其余28个30秒；逐个AsyncOrchestrated工具绑定§2.2冻结policy/commit point | `AgasMcpToolNames.cs`、`AgasMcpToolSchemas.cs`、`AgasMcpToolDescriptorRegistry.cs`、`AgasMcpDescriptorHandlerRegistry.cs` | Wave 2 全部 | 31个name/descriptor/handler/policy一一对应；schema可解析；恰好三个heavy为120秒；无policy默认漂移 |
| T3.2 Output runtime | 发布 `ProtocolTool.OutputSchema`；structured content统一typed envelope；普通failure无data；close handler未开始ordinary infrastructure failure可无data，handler开始后的failure要求专用四字段receipt；所有variants做runtime assertion，错类型fail closed且已开始close只能从最后receipt构造data | `AgasMcpServerToolBase.cs`、`AgasMcpToolCatalog.cs`、output runtime registry | T3.1 | 31/31 outputSchema；null省略；close success/handler-started failure四字段required、未开始failure无data；其他错误样本无data且通过schema |
| T3.3 Runtime | 将全部31个工具接入通用per-tool handler registry；所有 `AsyncOrchestrated` descriptor 走同一pipeline；executor按descriptor timeout执行None/ShortAtomicCommit/StartedMainThreadCompletion与同execution-state CAS，不按family特判 | `AgasMcpToolRuntime.cs`、`AgasMcpSerialToolExecutor.cs`、`MainMcpRuntimeComposition.cs` | T3.1–T3.2、T1.F | 31/31 handler唯一；所有AsyncOrchestrated direct/HTTP一致；CAS前取消、刚进入commit、commit后真实receipt竞态通过 |
| T3.4 AI UI | 接入同进程 shared keyed gate、五类draft dirty/fingerprint、mutation lease/generation、submission-time safe projector、ProviderId 与 Panel/Settings/toolbar 同步；settings以atomic replace为ShortAtomicCommit，区分precommit/postcommit generation漂移 | `Main.cs`、`SettingsDialog.cs`、`AiGenerationPanel.cs`/profile controller、窄 bridge | T2.A、T3.3 | lease期间禁用编辑/切换/增删/保存；precommit漂移conflict零写；postcommit漂移不覆盖新UI且`persisted:true,uiSynchronized:false`；credential/job store无泄漏 |
| T3.5 Lifecycle UI | 接入 close：主线程只读gate/capture → 后台cancel+project quiescence → dispatcher回主线程复核lease/generation并同步controlled save → StartedMainThreadCompletion finalize；接入open/create/save/package.import各自commit point、多阶段receipt，以及create root test resolver和owner rollback | `Main.cs`、`MainWorkspace.cs`、lifecycle bridge adapter | T2.B、T3.4 | timeout发生在save前；ResourceSaver仅主线程；handler-started失败报告准确四字段receipt；completion进入后返回真实结果且不错误切context |
| T3.6 Journal integration（同一`package-persistence-owner`） | 仅通过 Core Persistence 唯一 reconciler port 把 durable reconcile 接到 UI controlled save、MCP project.save 与 project open startup recovery；以磁盘project index引用+完整scene文件为commit barrier，与layout/delete后续失败解耦 | `src/Core/Persistence/ScenePackage*`、`src/Application/*ScenePackage*`、`ProjectSaveCoordinator.cs`、`MainWorkspaceSaveController.cs`、`AgasMcpProjectFacade.ProjectSave`、startup wiring | T2.D、T3.5 | scene+project成功/layout失败也提交；未引用只清journal owner文件；三入口规则与故障注入结果一致 |
| T3.7 Resource UI | 接入 scene/grid/prompt/assets/package 精确 UI refresh；scene.get不触发refresh/cache；export无silent save | `MainWorkspaceMcpSurfaceAdapter.cs`、相关 controller 窄 callback | T2.C–T2.D、T3.3、T3.6 | grid/prompt/files tree一致；read/export无非预期状态变化 |
| T3.8 Contract tests | 在 T1.A builder fixture 基础上，用独立逐字段golden完成31/31实际 input/output schema、生产 DTO variants、真实handler serialization/output、strict runtime、annotation、collection immutability和“禁止匿名data”校验；HTTP `tools/list` 不断言内部metadata，另做descriptor/handler/CommitPolicy registry parity | `tests/Core/Mcp/**`（单一 test owner） | T3.1–T3.7 | 31/31实际 manifest↔CLR DTO↔schema↔真实handler output 四方一致；close两类failure；AI五态/required alreadyApplied/apply Succeeded/capability子集+五值元素域+canonical order；status direct/list；Blueprint numeric enum+独立projectUri；错DTO failclosed |

Wave 3 验证：

```bash
dotnet build APP-AGAS-CS.sln -c Debug
tests/run-core-isolated.sh --no-build
```

#### Wave 2/3 执行验收证据（2026-08-01）

以下均在当前脏工作区完成；未执行 reset/clean/revert/checkout/commit/push。

| 验证 | 实际命令 | 结果 |
|---|---|---|
| Debug 构建 | `dotnet build APP-AGAS-CS.sln -c Debug --no-restore -m:1` | exit `0`；`0` errors、`3` 个 `CS0618` warnings。 |
| Core isolation runner 自测 | `tests/run-core-isolated-self-test.sh` | exit `0`；`PASS`。日志中的负向 Core isolation failure 输出为自测预期分支。 |
| Godot acceptance isolation 自测 | `tests/Godot/AcceptanceIsolationSelfTest.sh` | exit `0`；`PASS`。两条 failure 输出为预期负向分支。 |
| 全量 Core（防卡死诊断） | `dotnet test tests/Core/APP-AGAS-CS.Core.Tests.csproj -c Debug --no-build --blame-hang --blame-hang-timeout 2m --logger "console;verbosity=normal"` | exit `0`；total `1025` / passed `1025` / failed `0`。 |
| MCP Host 实际发布 | `dotnet test tests/Core/APP-AGAS-CS.Core.Tests.csproj -c Debug --no-restore --filter 'FullyQualifiedName~AppAgasCs.Tests.Core.Mcp.AgasMcpHostTests' --logger 'console;verbosity=normal'` | exit `0`；total `29` / passed `29` / failed `0`。官方 SDK client 经真实 Streamable HTTP `tools/list` 断言 31 个唯一名称与 `AgasMcpToolNames.All` 双向一致，且 31/31 `OutputSchema` 为 object。 |
| Godot acceptance | `tests/Godot/run-acceptance.sh` | exit `0`；Acceptance build `0` warnings / `0` errors；`[AGAS][PROJECT-PICKER][PASS]`，无 `FAIL`、无其他 scene 标签。 |
| diff whitespace | `git diff --check` | exit `0`；无输出。 |

这些结果提供 Wave 2/3 的 typed contract、中央 31 工具发布与 runtime 接线回归证据；真实第三方 Provider 与 Windows/Linux 实机仍按 §6 保持待验证。

### Wave 4 — Godot 与真实 HTTP 验收

只扩展下列已经存在的 acceptance scene；本轮不创建或引用不存在的 `McpCapabilityExpansionAcceptance.tscn`。

| ID | 验收方向 | 主要测试文件 | 必须覆盖 |
|---|---|---|---|
| T4.0 | Acceptance环境隔离 | `run-acceptance.sh` + 所有下列场景 | 每case独立HOME/AGAS_HOME/XDG/GODOT_USER_HOME/user://与managed root；正常/timeout kill/异常后真实settings/recent/credential/Documents fingerprint不变；清临时目录/端口 |
| T4.1 | AI Profile/UI/job | `AiSettingsUiAcceptance`、`McpAiGenerationAcceptance`、`AiAdapterPluginAcceptance` | 无项目list/models/diagnostics；capability数组为支持能力子集、元素仅限五值且按canonical order；同进程keyed RMW；五类draft+mutation lease；precommit generation漂移零写/postcommit漂移persisted但不覆盖UI；submission-time安全投影；ProviderId链；status五态required alreadyApplied/direct/list，apply保持Succeeded；第三方constructor/fixture兼容 |
| T4.2 | Lifecycle | `McpSettingsUiAcceptance`、`ProjectPickerAcceptance` | 注入managed root；ref rotate+10分钟TTL/20cap；只读gates→cancel/quiesce→主线程同步save→finalize；open/create/close/save的StartedMainThreadCompletion边界；close handler-started四字段receipt与未开始infra无data；Blueprint busy；owner rollback |
| T4.3 | Project/resource | `McpResourceRuntimeAcceptance` | scene live/fresh且状态不变；ResourceLoader/Resource遍历/scalar projection主线程；cursor 5m/128/32及10,000items/256KiB/8MiB/64MiB预算；grid/role；三个heavy120秒；Short/Started CAS边界；package总payload128MiB；export无silent save |
| T4.4 | Journal/partial save | `McpResourceRuntimeAcceptance`、`McpSettingsUiAcceptance` | MCP save与UI save均reconcile；scene+project成功/layout失败不误删；未提交启动恢复只删owner files；已提交重启保留 |
| T4.5 | 真实 HTTP 全链 | `McpHttpRuntimeAcceptance` | `tools/list` 只验31个name/description/input/output/annotations；manifest/CLR DTO/schema/真实handler output 31/31一致；Blueprint resourceUrl只为opaque `project:///` URI；完整agent链；内部family/dispatch/timeout/CommitPolicy另由Core registry parity验证 |
| T4.6 | 安全/回归 | 上述现有场景 + `CanvasPatchSecurityAcceptance` | 无credential/BaseUrl/absolute/Base64；Host/Origin/body；symlink/identity race；commit前cancel/commit后确定响应；错DTO failclosed；端口释放 |

真实命令：

```bash
tests/Godot/run-acceptance.sh AiSettingsUiAcceptance.tscn
tests/Godot/run-acceptance.sh McpAiGenerationAcceptance.tscn
tests/Godot/run-acceptance.sh AiAdapterPluginAcceptance.tscn
tests/Godot/run-acceptance.sh ProjectPickerAcceptance.tscn
tests/Godot/run-acceptance.sh McpResourceRuntimeAcceptance.tscn
tests/Godot/run-acceptance.sh McpSettingsUiAcceptance.tscn
tests/Godot/run-acceptance.sh McpHttpRuntimeAcceptance.tscn
tests/Godot/run-acceptance.sh CanvasPatchSecurityAcceptance.tscn
lsof -nP -iTCP:54119 -sTCP:LISTEN
```

`lsof` 最终应无输出。任何 acceptance 失败都必须修复并重跑受影响场景；不得用 Core 单测代替真实 Godot/HTTP 证据。

### Wave 5 — 开发后多 Agent CR 循环

至少并行派发以下只读复审：

1. AI 设置、凭据隔离、job identity/diagnostics CR。
2. 生命周期、dirty、Blueprint busy、job cleanup、路径安全 CR。
3. Scene/Grid/Prompt/Assets/Package 与原子回滚 CR。
4. typed output、outputSchema、runtime/schema parity、HTTP 测试覆盖 CR。

每轮统一输出：`已验证事实 / P0-P1 / P2 / 建议`。

```text
有 P0/P1 -> 修复 -> 重跑最小测试 -> 重新只读复审
所有方向无 P0/P1 -> 进入 Wave 6
```

P2 中属于凭据泄漏、路径越界、部分写入、schema 漂移、防回归缺失的项必须在本轮收掉，不得仅因标为 P2 延后。

### Wave 6 — 最终验收与交付

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

lsof -nP -iTCP:54119 -sTCP:LISTEN
git status --short
```

最终记录必须包含：Debug/Release/ExportRelease build warning/error 数（Debug/Release 使用 `.sln`；ExportRelease 仅使用主 `APP-AGAS-CS.csproj`，不得使用未定义该 solution configuration 的 `.sln`）、`tests/run-core-isolated.sh` Core 通过数、两个 Wave0 基线回归、八个 Godot scene 的 PASS 日志、真实 HTTP `tools/list` 工具数与 wire字段、31/31 manifest/CLR DTO/schema/真实handler output验证、内部registry/CommitPolicy parity、端口无残留、隔离前后真实用户 settings/recent/credential hash一致、多 Agent CR 无 P0/P1，以及未连接真实 Provider/非 macOS 实机等待验证项。

---

## 4. 最终测试矩阵

| 功能 | 正向 | 反向/竞态 | 持久化/UI | 安全响应 |
|---|---|---|---|---|
| Wave0 Core baseline | 两个指定回归与完整suite经`tests/run-core-isolated.sh`全绿 | timeout/异常退出、无意直接访问真实root | case根独立、真实known-path fingerprint不变 | 不读取/修改真实credential/recent/managed root |
| profiles.list | 无项目列安全 rows、active/effective；capability为支持能力子集、元素仅限五值且按canonical order | 空/损坏配置、缺 Provider | UI 当前项一致 | 无 key/BaseUrl/credential 内容 |
| models.list | Provider 预设、custom 标志；capability为支持能力子集、元素仅限五值且按canonical order | 无效 Provider 配置被隔离 | 与 Provider catalog 一致 | 轻量、无远程探测 |
| profile.activate | 合法 profile 持久化 | 同进程keyed并发RMW、unknown id、五类draft、mutation lease、ShortAtomicCommit CAS、precommit generation漂移 | postcommit漂移仍`persisted:true,uiSynchronized:false`且不覆盖新UI；正常时Panel/Settings/重启一致 | safe projector；credential逐字节不变 |
| model.set | preset 与 custom alias | blank/超长、无active、并发RMW、五类draft/lease、ShortAtomicCommit、precommit漂移/写失败 | postcommit漂移语义同上；当前profile/重启一致 | 不回显BaseUrl/key |
| ai.diagnostics | profile preflight | missing credential/provider/model | `networkChecked=false` | 只返回 allow-list hint |
| AI job identity/status | submit时安全投影并冻结ProviderId/AdapterKind/model；五态required `alreadyApplied`；status direct/list；apply保持Succeeded | 切模型后旧job不漂移、第三方ProviderId、credential注入、cancel/失败竞态、重复apply | 新job使用新模型；apply仅改变alreadyApplied/applied metadata | job store/failure仅安全字段 |
| recent/open/create/close | rotate ref（10分钟/20cap）；无项目create/open；只读gates→cancel/quiesce→main-thread save→close | fake/expired/rotated ref、已有项目、目标存在、root race、save失败、Blueprint busy、quiescence timeout、lease漂移、Started CAS边界 | create/close多阶段receipt；close handler-started四字段data/未开始infra无data；owner rollback、picker/workspace/context正确 | 不返回路径；测试root隔离；symlink拒绝 |
| scene.get | loaded live/uncached fresh 全详情分页；ResourceLoader/遍历/scalar projection主线程 | thread-id、missing/malformed/future/symlink、cursor过期/跨scope、5m/128/32、10,000items/256KiB/8MiB/64MiB预算 | worker仅纯scalar；session cache/current/active/dirty/selection不变 | 相对image path；无bytes/manifest |
| grid.mutate | size/snap/visible | 0、>4096、read-only、unknown op | UI control、save/reload | typed grid only |
| prompt role | add/read/update lower-camel三种role | 非法/大小写错误、role-only条件、锁/隐藏/read-only | PromptComposer、save/reload | prompt长度受限 |
| assets.import | `$AGAS_HOME` inbox导入、重名策略 | 假扩展、解码失败、>32MiB、traversal/symlink、120秒timeout、ShortAtomicCommit CAS竞态 | final rename前清staging、进入commit后确定响应；files tree刷新 | 仅relative path/size/type，无Base64 |
| imports list / package export/import | 轻量分页list→opaque ref；live snapshot export到全局exchange→另一项目ref import→save/reload | 未分页深/海量包、symlink、引用文件替换、ref伪造/过期、32/128MiB、Short export/Started import竞态 | operation slot写前登记+finally cleanup；唯一persistence owner；export无silent save | list无path/Base64/manifest/metadata；export仅packageRef/sourceRef/count |
| typed output/schema | 独立逐字段manifest覆盖31工具实际success/failure、AI五态/apply/capability、Blueprint numeric enum与opaque project URI；union 仅 metadata；providerId dot primitive | manifest/CLR DTO/schema/真实handler output、data/error、null、错DTO漂移、type-level converter | tools/list wire与内部registry/CommitPolicy分开验；failclosed | secret-writing converter 无法注册；schema/response无秘密/图片内容；不声称Blueprint URI为MCP resource |
| HTTP runtime | 完整 agent 工作流 | Host/Origin/body/session/rate/timeout | stop 后 quiesce/端口释放 | text fallback 与 structured content 一致 |

---

## 5. 回滚点

| 回滚点 | 触发条件 | 回滚动作 |
|---|---|---|
| R0 文档/基线 | 方案 CR 有 P0/P1，或两个指定 Core 回归/完整隔离基线任一不绿 | 只修文档或对应既有基线问题；保留失败证据并停留 Wave 0，不进入能力扩展 Wave 1 |
| R1 基础服务 | 新 service 无法保持现有 UI/存储语义 | 回滚对应独立 service 文件；不修改现有 16 工具注册 |
| R2 family implementation | direct Facade 测试失败或产生凭据/路径泄漏 | 回滚单一 family，不注册中央 tool descriptor |
| R3 outputSchema | SDK/客户端兼容问题或 runtime 不符合 schema | 先撤协议 `OutputSchema` 注册，保留 typed DTO；修复后重新发布，禁止回到匿名输出作为最终状态；错误路径仍必须failclosed |
| R4 lifecycle | dirty/busy/save/quiescence/open失败出现错误页面切换或上下文串线 | 禁用新增 lifecycle descriptors；保留 UI 原路径；仅按owner marker回滚本次create，绝不删除既有/身份变化目录 |
| R5 import/package | timeout/cancel/partial write、越界、journal误判或旧导出损坏 | 禁用descriptors；commit前仅清owner匹配的pending staging/journal文件；先做disk durable reconcile，已commit或已被project index引用的用户artifact一律保留；不把“导入资源不删除”误套到未commit pending文件 |
| R6 AI 设置 | keyed RMW、draft gate、ProviderId或UI同步失败 | 禁用profile写工具注册；保留原设置快照和UI手工路径；不得回退成跨实例无锁整文档覆盖 |
| R7 最终集成 | HTTP/Godot 任一 P0/P1 未清零 | 不提交“完成”状态、不 push 发布；回到对应波次修复和复审 |

每个开发波次应形成独立、可构建、测试通过的提交点；不得用一次大提交混合 output infrastructure、生命周期和导入安全逻辑。

---

## 6. P2 与非目标

### 6.1 本轮 P2，不阻塞当前能力扩展

- 统一 `agas.capabilities` / `agas.status`。
- Blueprint 图读取/编辑和异步 submit/status/cancel/apply。
- 通用 revision / `expectedRevision` / atomic batch。
- AI recent jobs cursor 和时间/state filter。
- 远程客户端专用的上传/download resource endpoint；本轮只支持 `$AGAS_HOME` 受控 inbox。
- Windows/Linux 实机专项验证；本轮保留跨平台代码与 Core 覆盖，但不能宣称已实机验收。

### 6.2 明确非目标

- 图片预览 URL、Base64 图片返回或列表内联原图。
- MCP Undo / Redo。
- 任意绝对路径项目 open/create、任意本机文件 import。
- Headless daemon、键鼠 UI 自动化。
- 通过 MCP 读取、写入或回显 API key/credential。
- 把 `ai.diagnostics` 变成真实 Provider 请求、网络扫描或任意 HTTP 代理。
- 真实第三方 Provider 稳定性作为自动化硬门禁；可在最终阶段做人工 smoke，但必须单独标注。

---

## 7. 交付记录模板

开发完成前本节保持模板，不提前填写 PASS：

```md
## 已验证事实
- 提交与文件：
- Build：
- Core：
- Godot acceptance：
- HTTP tools/list / outputSchema：
- 多 Agent CR：无 P0/P1 / 尚有问题

## 待验证项
- 真实 Provider：
- Windows/Linux：
- 其他外部环境：

## 基于证据的推断
- 仅记录由代码、测试和日志支持的结论。

## 剩余项
- P2：
```

---

## 8. 文档关系

- 本计划的冻结契约来源：`docs/design-docs/agas-mcp-capability-expansion-technical-design.md`。
- 现有 16 工具、AI job 与安全基线：`docs/design-docs/agas-mcp-v2-agent-ops-technical-design.md`。
- 能力缺口审计入口：上述 v2 方案 §13；开发状态与命令只在本 active plan 更新，禁止把“待实现”提前改成“已完成”。
