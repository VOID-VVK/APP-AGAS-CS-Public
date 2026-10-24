# AGAS MCP v2 Agent Ops 执行计划

> 文件：`docs/exec-plans/active/agas-mcp-v2-agent-ops-execution-plan.md`  
> 配套方案：`docs/design-docs/agas-mcp-v2-agent-ops-technical-design.md`  
> 前置：MCP v1 Gate 2 已收尾（见 `agas-mcp-execution-plan.md` / `agas-mcp-gate2-audit.md`）  
> 状态：**Wave 0–3 与 annotation 契约收尾已完成，v2 已实现并通过验收**（2026-07-25）；收尾证据为 `f416371`、`83e2358`、`814e23b`、`21bc853`、`68a6666`、`fbe4c5f`、`c221f38`、`615c214`、`1f09027`、`29093ce`、`8892e17`、`a67c4ad` 及 §7 所列测试结果。
> 技术固定：Godot `4.6.1` Mono / `.NET 8` / 全 C#。

---

## 1. 范围

### 1.1 做

- Wave 1：选区 / 选中图片 MCP 工具（`agas.selection.set` / `agas.selection.get`）
- Wave 2：AI 异步任务工具（`agas.ai.generate` / `status` / `cancel` / `apply`）
- Wave 3：错误码、生命周期、Godot acceptance、v1 回归
- 抽出 AI 准备/应用 bridge 与共享 coordinator，供 UI 与 MCP 共用  

### 1.2 不做（本计划）

- Headless 无窗口全功能  
- UI 键鼠自动化  
- Blueprint MCP  
- selection 并入 snapshot  
- generate 内联 selection / replacement / selectedImageId  
- 通过 MCP 新建/打开项目  
- Bearer token  
- 真实 Provider 作为 CI 硬门禁  

---

## 2. 波次与任务

### Wave 0 — 评审与契约冻结（文档）

| ID | 任务 | 验收 |
|----|------|------|
| T0.1 | 评审 v2 技术方案 §5 工具 JSON 契约 | **完成**：14 工具；无内联选区；`selection.set → ai.generate` |
| T0.2 | 冻结并发/排队模型 | **完成**：UI/MCP 共享 active permits 5；UI 满时即时拒绝；仅 MCP 使用 FIFO 有界队列 20 |
| T0.3 | 冻结 selection 与 snapshot 关系 | **完成**：独立 `selection.get`，snapshot 不加入 selection |
| T0.4 | 统一边界与错误语义 | **完成**：project open 移出 v2；锁/隐藏拒绝选中；operation 自动派生；默认当前 UI profile；apply 使用冻结 W/目标且单次幂等 |

**出口（已通过）**：契约冻结后已进入并完成 Wave 1–3。

---

### Wave 1 — Selection（串行依赖：先有 Canvas API）

| ID | 任务 | 主要文件 | 验收 |
|----|------|----------|------|
| T1.1 | CanvasWorkspace 最小 API：按网格设/清选区与替换区；按 imageId 选中 | `CanvasWorkspace.cs` + 单测/现有 canvas 测 | **完成**：API 不依赖鼠标事件即可设置状态 |
| T1.2 | Facade + Tools：`selection.set` / `selection.get` | `AgasMcpProjectFacade` / `AgasSelectionTools` / Contracts | **完成**：非法网格、锁定/隐藏图层、跨 scene 已覆盖；snapshot 无 selection |
| T1.3 | UI 刷新：MCP 设选区后主界面框选可见 | `AgasMcpUiRefreshScope` / MainWorkspace 注册 | **完成**：Godot `McpSelectionAcceptance` PASS |

---

### Wave 2 — AI Bridge（串行，依赖 Wave 1 可选但建议先完成 Region 路径）

| ID | 任务 | 主要文件 | 验收 |
|----|------|----------|------|
| T2.1 | 从 MainWorkspace 抽出 `IAgasAiGenerationWorkspaceBridge`（Prepare + Apply）；禁止复制逻辑 | `MainWorkspace.cs`、bridge contracts | **完成**：UI/MCP 共用 Prepare/Apply；AI acceptance 回归通过 |
| T2.2 | 公共 `AgasAiGenerationCoordinator` + `AgasMcpAiJobStore` / Scheduler | `src/Features/Ai/*`、`src/Mcp/*` | **完成**：共享 active permits 5；UI 即时拒绝；MCP 队列 20；状态机、清理、cancel 竞态与 apply lease 已覆盖 |
| T2.3 | Tools：`ai.generate` / `status` / `cancel` / `apply` | Tools + Facade + Contracts | **完成**：无内联选区/operation；profile 默认值、冻结目标、幂等/stale、scope 与严格 runtime schema 已覆盖 |
| T2.4 | 项目关闭 / MCP stop 取消 jobs | Lifecycle + context unregister | **完成**：真实 Running job 被取消；quiesce、permit 与端口均释放 |

---

### Wave 3 — 集成验收与文档收尾

| ID | 任务 | 验收 |
|----|------|------|
| T3.1 | Godot `McpAiGenerationAcceptance`（fake provider） | **完成 / PASS**：selection.set→submit→status→apply→save；stale、重复 apply、no-poll UI 终态刷新已覆盖 |
| T3.2 | 真实 HTTP runtime 的 tools/list + tools/call E2E | **完成 / PASS**：`McpHttpRuntimeAcceptance` 经 Streamable HTTP 覆盖 14 工具、selection→generate→status→apply→save→重载、安全边界与严格 runtime schema |
| T3.3 | 更新本执行计划状态 + 方案「已实现」证据段 | **完成**：见 §6–§7 |
| T3.4 | 手工 smoke（可选）：真实 profile 生成一次 | 非硬门禁；本次最终验收未作真实 Provider 稳定性声明 |
| T3.5 | 统一 annotation schema / runtime contract | **完成**：BCL DataAnnotations + AGAS attributes 经 ContractRegistry 同时生成 inputSchema、Validate + Bind；14/14 frozen parity；无新 NuGet |

---

## 3. 实际完成波次

```text
Wave 0   契约冻结
Wave 1   Selection MCP + Acceptance
Wave 2   AI bridge / jobs / tools / 生命周期
Wave 3   集成门禁、提交级缺口修复与文档收尾
```

---

## 4. 最终门禁命令

```bash
# 构建
dotnet build APP-AGAS-CS.sln -c Debug
dotnet test tests/Core/APP-AGAS-CS.Core.Tests.csproj -c Debug --no-build

# Godot C# acceptance（必须使用 Mono 可执行文件）
GODOT=/Applications/Godot_mono.app/Contents/MacOS/Godot
"$GODOT" --headless --path . res://tests/Godot/McpSelectionAcceptance.tscn
"$GODOT" --headless --path . res://tests/Godot/McpAiGenerationAcceptance.tscn
"$GODOT" --headless --path . res://tests/Godot/McpResourceRuntimeAcceptance.tscn
"$GODOT" --headless --path . res://tests/Godot/McpHttpRuntimeAcceptance.tscn
"$GODOT" --headless --path . res://tests/Godot/McpSettingsUiAcceptance.tscn
"$GODOT" --headless --path . res://tests/Godot/AiGenerationPanelAcceptance.tscn
"$GODOT" --headless --path . res://tests/Godot/AiGenerationWorkspaceIntegrationAcceptance.tscn
"$GODOT" --headless --path . res://tests/Godot/AiSettingsUiAcceptance.tscn
"$GODOT" --headless --path . res://tests/Godot/CanvasAiContextMenuAcceptance.tscn
"$GODOT" --headless --path . res://tests/Godot/CanvasPatchSecurityAcceptance.tscn
"$GODOT" --headless --path . res://tests/Godot/CanvasRegionAndReplaceAcceptance.tscn

# 端口最终应无残留（测试后）
lsof -nP -iTCP:54119 -sTCP:LISTEN
```

回归必跑：既有 `McpResourceRuntimeAcceptance` / `McpHttpRuntimeAcceptance` / `McpSettingsUiAcceptance` 以及 AI UI 相关 acceptance。

最终门禁结果统一登记到本计划 §7，并同步技术方案 §12.1；不要只在临时日志中记录。

---

## 5. Agent 端到端验收剧本（已实现真实 HTTP tools/call）

前置：启动 AGAS → 打开项目 → 设置启用 MCP。

```text
snapshot
→ scene.add + layer.add
→ prompt.add
→ selection.setRegions
→ ai.generate（effectiveOperation 由 Q/W 与目标自动派生）
→ poll ai.status 直到终态
→ ai.apply
→ project.save
→ 再次 snapshot 确认图片/路径存在
```

成功标准见方案 §12。

`McpHttpRuntimeAcceptance` 已按真实 Streamable HTTP 会话执行 `tools/list` 和
`tools/call`：从 snapshot 读取 scene/layer，调用 selection.set/get，提交
ai.generate，轮询 ai.status 到 Running/Succeeded，执行 ai.apply（含幂等重复
apply）、ai.cancel、project.save，并从 `.tres` 重载确认结果。测试使用可控 fake
AI runner，因此这里证明的是 AGAS HTTP/MCP 全链，不把第三方 Provider 稳定性写成已验收事实。

参考图编辑时还必须先设置 Q，不能只 selectImage：

```text
agas.selection.set op=setRegions      # 必需 Q；局部替换可同时设置 W
→ agas.selection.set op=selectImage
→ agas.ai.generate target=ReplaceSelected
→ agas.ai.status → agas.ai.apply → agas.project.save
```

---

## 6. 当前进度

| 项 | 状态 |
|----|------|
| v2 技术方案文档 | **已完成并写入最终证据** `docs/design-docs/agas-mcp-v2-agent-ops-technical-design.md` |
| v2 执行计划文档 | **已完成收尾**（本文件） |
| 代码实现 | **已完成** |
| Wave 0 评审 / 契约冻结 | **已完成（2026-07-25）** |
| Wave 1 Selection | **已完成** |
| Wave 2 AI / 生命周期 | **已完成** |
| Wave 3 集成验收 / 缺口修复 | **已完成；具体提交和测试证据见 §6.2–§7** |

### 6.1 最终实现结果

- 正式 tools/list 固定为 **14**：v1 8 个 + Selection 2 个 + AI 4 个。
- Selection 保持独立 set/get；snapshot 不加入 selection；generate 不接受内联 selection/replacement/selectedImageId 或 operation。
- UI/MCP 共用 AI Prepare/Apply bridge 与 5 个 active permits；MCP 使用容量 20 的 FIFO 队列。
- AI job 按规范化 project root + projectId 隔离；apply lease 防止并发重复应用与 TTL/容量清理竞态。
- Runtime 严格执行 AI schema 字段和值约束；安全输出不含 Base64、绝对路径、密钥或大 metadata/manifest。
- 14 工具输入已统一为 annotation 单一契约源：BCL DataAnnotations + AGAS attributes 经 `AgasMcpContractRegistry` 同时生成 tools/list `inputSchema`、执行 `Validate + Bind`，并验证 public typed Facade handlers。
- MCP SDK 只承载/发布项目提供的 schema，不自动执行本项目 runtime validation；AGAS 因此在 Facade 边界集中补层。实现不做通用 JSON Schema 引擎，且未新增 NuGet。
- 项目关闭/切换和 MCP stop 会取消相应 Running/Queued job；stop 等待 quiesce 并释放共享 permit。

### 6.2 提交链

```text
84669b5 feat: freeze AGAS MCP v2 contracts [T0]
80267ee feat: implement MCP selection operations [T1]
3558feb fix: reject read-only MCP selection mutations [T1]
d6a705d feat: extract shared AI workspace bridge [T2.1]
bef601e feat: add MCP AI job scheduler [T2.2]
01cc292 fix: harden MCP AI job cleanup [T2.2]
6f9c18f feat: expose MCP AI generation tools [T2.3-T2.4]
3f152e8 fix: pin scoped MCP AI apply jobs [Wave3]
74665ee fix: enforce scoped MCP AI contracts [Wave3]
dbed5a9 fix: validate MCP AI schema values at runtime [Wave3]
1356b3a fix: enforce exact MCP AI identifier patterns [Wave3]
f416371 test: verify MCP v2 workflow over HTTP [Wave3 real HTTP E2E]
83e2358 fix: close MCP AI lifecycle contract gaps [FIFO/TTL/result/apply/scope lifecycle]
814e23b test: align canvas patch security acceptance [security gate]
21bc853 fix: synchronize MCP AI jobs with editor UI [Main no-poll/scope/shutdown cleanup]
68a6666 fix: enforce strict MCP tool arguments [14-tool schema/runtime parity]
fbe4c5f feat: add annotation-driven MCP contracts [annotation source]
c221f38 fix: harden annotation-driven MCP contracts [conditions/defaults/nesting]
615c214 fix: reject unbindable MCP contract properties [descriptor safety]
1f09027 feat: generate MCP schemas from annotated inputs [14/14 frozen parity]
29093ce refactor: bind MCP tool inputs from contracts [Execute Validate+Bind]
8892e17 fix: validate typed MCP inputs from contracts [typed Facade validation]
a67c4ad fix: preserve MCP selection range errors [Selection error compatibility]
```

## 7. 最终验收证据（2026-07-25）

- Build：0 warning / 0 error。
- Core：563/563 PASS（Selection 错误码兼容收口后）。
- Godot：之前已验证 11 个定向门禁全部 PASS——`McpSelectionAcceptance`、`McpAiGenerationAcceptance`、`McpHttpRuntimeAcceptance`、`McpResourceRuntimeAcceptance`、`McpSettingsUiAcceptance`、`AiGenerationPanelAcceptance`、`AiGenerationWorkspaceIntegrationAcceptance`、`AiSettingsUiAcceptance`、`CanvasAiContextMenuAcceptance`、`CanvasPatchSecurityAcceptance`、`CanvasRegionAndReplaceAcceptance`；`a67c4ad` 后本次只重跑并通过 Selection / HTTP，不虚构为新一轮 11 项全量。
- `f416371` 对应的 `McpHttpRuntimeAcceptance` 输出 `[AGAS-H2][MCP-HTTP-RUNTIME][PASS] Godot headless real MCP HTTP E2E acceptance completed.`，并通过真实 HTTP `tools/list` / `tools/call` 跑通 v2 全链与安全断言。
- `83e2358` 覆盖结果路径/应用路径分离、terminal TTL、严格 FIFO，以及项目切换/关闭取消 Running + Queued job。
- `814e23b` 对齐 Canvas patch 安全验收：使用真实 Q 选区覆盖 traversal/绝对路径、图片与父目录 symlink、scoped generated directory symlink 拒绝。
- `21bc853` 的生产 `Main` 验收确认：后台终态无需 `ai.status` 轮询即可刷新 AI 面板；跨项目 root + projectId scope 事件隔离；WM-close 的真实 shutdown 完成后才进入用户文件恢复。
- `68a6666` 对 14 个工具统一执行 schema 精确参数门禁：顶层 unknown、大小写 alias、空对象工具非空未知参数，以及 Selection Q/W region 嵌套 unknown/case alias 均稳定返回 `AGAS_INVALID_OPERATION`；Core Execute 与真实 HTTP 各有完整反向矩阵。
- `fbe4c5f` / `c221f38` / `615c214` 建立并加固 annotation descriptor/registry；`1f09027` 生成 14 个工具 schema，14/14 与 frozen schema 语义一致；`29093ce` / `8892e17` 将 Execute 与 direct typed Facade handlers 统一接入同一 `Validate + Bind`，不再维护平行手写 wire validator。
- `a67c4ad` 在不改通用 descriptor 和其他工具的前提下恢复 Selection 错误码兼容：region 数值范围错误返回 `AGAS_SELECTION_INVALID`；结构、类型、null、unknown/case 错误保持 `AGAS_INVALID_OPERATION`；Core 563/563、Godot Selection / HTTP 定向通过。
- settings/recent-projects 隔离：恢复后逐字节验证；测试前后 SHA-256 分别保持 `3639638874f5c93f680e1e7952d64463f0e34d1fc11e5bf0e4dd920c1835e340` 与 `c79e50b7e24dbfc173184ef310adcecbd5f63b24b2460d7d45b22cadcd35ff52`。
- `lsof -nP -iTCP:54119 -sTCP:LISTEN`：无监听。
- 后续全量最终门禁继续使用 §4 命令，并在本节追加日期、计数与失败修复证据。

---

## 8. 回滚与兼容

- 新工具以增量注册；关闭代码路径或 feature 时不影响 v1 8 工具。  
- AI bridge 与共享 coordinator 是 Wave 2 强制门禁；不允许 MCP 临时复制 Prepare/Apply，也不允许 UI/MCP 各自维护并发计数。  
- 不修改 v1 工具名与既有必填字段语义；仅允许只读响应新增字段（向后兼容）。
