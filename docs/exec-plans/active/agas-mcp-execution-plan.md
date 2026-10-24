# AGAS MCP 执行计划

> 文件：`docs/exec-plans/active/agas-mcp-execution-plan.md`  
> 配套方案：`docs/design-docs/agas-mcp-technical-design.md`  
> 项目：仓库根目录 `APP-AGAS-CS`
> 状态：状态型执行计划；MCP 实现已存在；Gate 2 收尾验证证据已记录，Core 101/101 与三项 Godot acceptance 均 PASS；导出包与 Windows 实机仍保留待验证。  
> 技术固定：Godot `4.6.1` Mono / `.NET 8` / 全 C#；禁止 JS/TS/GDScript。  
> 当前门禁：Debug / Release / ExportRelease build 均 0 warning / 0 error；Core 101/101 passed；`McpResourceRuntimeAcceptance.tscn`、`McpHttpRuntimeAcceptance.tscn`、`McpSettingsUiAcceptance.tscn` 均 PASS；最终 `127.0.0.1:54119` 无监听。未通过项不得外推：缺 `export_presets.cfg`、实际 Godot 导出包未验证、Windows junction/reparse 与 Windows 实机未验证。

---

## 1. 当前状态

### 1.1 已完成/已存在

已通过本轮只读/命令验证：

- 工程：`APP-AGAS-CS.csproj` 使用 `Godot.NET.Sdk/4.6.1` + `net8.0`。
- 已添加 `ModelContextProtocol.AspNetCore` `1.4.1` 包引用。
- MCP 生产实现已存在，主要位于 `src/Mcp/**`、`src/Mcp/Contracts/**`、`src/Mcp/Tools/**`。
- 8 个正式工具注册代码已存在：`src/UI/Main.cs` 创建 `agas.project.snapshot`、`agas.assets.list`、`agas.scene.mutate`、`agas.layer.mutate`、`agas.image.mutate`、`agas.prompt.mutate`、`agas.view.set`、`agas.project.save`。
- MCP 设置/生命周期/UI 入口实现已存在：`AgasMcpSettingsResource`、`AgasMcpSettingsStore`、`AgasMcpLifecycleService`、`src/UI/McpSettingsDialog.cs`。
- MCP Host 实现已存在：`AgasMcpHostFactory` 绑定固定 `127.0.0.1:54119`，使用 `AddMcpServer().WithHttpTransport(options => options.Stateless = false)` 并 `app.MapMcp("/mcp")`。
- 主线程 dispatcher、串行 executor、项目 context/facade、dirty state、路径 validator、错误 mapper 已存在。
- MCP 当前实际测试文件已存在于 `tests/Core/Mcp/*.cs`：host、path validator、serial executor、settings/lifecycle、tool contract、tool runtime、mutate schema precision、dirty state。
- 历史 Round3：`dotnet build APP-AGAS-CS.sln -c Debug --no-restore` 0 warning / 0 error；F2 `dotnet test tests/Core/APP-AGAS-CS.Core.Tests.csproj -c Debug --no-restore` 83/83 tests passed。`83/83` 仅标作历史 Round3。
- 当前收尾验证：`/tmp/agas_gate2_restore_build_core.log` 记录 Debug / Release / ExportRelease build 均 0 warning / 0 error，Core 101/101 passed。
- 当前 Godot acceptance 已补齐并通过：
  - `tests/Godot/McpResourceRuntimeAcceptance.tscn`：在仓库根目录运行 `godot --headless --path . res://tests/Godot/McpResourceRuntimeAcceptance.tscn`，日志 `/tmp/agas_gate2_godot_resource.log`，PASS。
  - `tests/Godot/McpHttpRuntimeAcceptance.tscn`：在仓库根目录运行 `godot --headless --path . res://tests/Godot/McpHttpRuntimeAcceptance.tscn`，日志 `/tmp/agas_gate2_godot_http.log`，PASS。
  - `tests/Godot/McpSettingsUiAcceptance.tscn`：在仓库根目录运行 `godot --headless --path . res://tests/Godot/McpSettingsUiAcceptance.tscn`，日志 `/tmp/agas_gate2_godot_settings_ui.log`，PASS。
- 最终端口状态：`lsof -nP -iTCP:54119 -sTCP:LISTEN` 无输出，`127.0.0.1:54119` 无监听。
- Godot 配置：`project.godot` 主场景为 `res://src/UI/Main.tscn`，features 包含 `C#`。
- 产品数据模型已存在于 `src/Core/Resources/*.cs`，使用 Godot custom `Resource`。
- `.agas` 路径与保存逻辑已存在：`src/Core/Persistence/AgasPaths.cs`、`AgasResourceRepository.cs`、`AgasProjectInitializer.cs`。
- 当前 UI 工作区集中持有打开项目状态：`src/UI/MainWorkspace.cs`。
- 当前画布/图层/图片/提示词操作集中在 `src/Features/Canvas/CanvasWorkspace.cs` 与 `src/Features/Commands/*.cs`。
- 当前图片安全边界已有基础：`ExternalImageLoader` 和 `ProjectFilesTree` 拒绝/过滤非项目图片路径、URL/data、非支持扩展名、跳过内部目录。
- 测试项目存在：`tests/Core/APP-AGAS-CS.Core.Tests.csproj`。
- 当前工作区根目录未发现 `.git` 目录；执行计划不得使用 `git diff` 作为必过门禁。
- 当前根目录未发现 `export_presets.cfg`；导出验证阻塞，不能宣称导出通过。
- 本轮 Wave G3 仅允许更新指定文档：
  - `docs/design-docs/agas-mcp-technical-design.md`
  - `docs/exec-plans/active/agas-mcp-execution-plan.md`
  - `docs/exec-plans/active/agas-mcp-gate2-audit.md`

### 1.2 已定稿约束

- 官方 `ModelContextProtocol.AspNetCore 1.4.1`。
- SDK `1.4.1` 精确事实：`HttpServerTransportOptions.Stateless` 默认 `false`；实现必须使用 `builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless=false);`，再 `app.MapMcp("/mcp");`，禁止通过 `MapMcp` 的 options lambda 配置 Stateless。
- stateful Streamable HTTP；AGAS 主成功链/完整验收协议使用 `2025-11-25`；`MapMcp` 映射 `/mcp` 的 `POST`、`GET`、`DELETE`；合法版本协商由官方 SDK `1.4.1` 处理。
- 固定 endpoint：`http://127.0.0.1:54119/mcp`。
- Host header 最终只允许 `127.0.0.1:54119`；不允许 `localhost`/IPv6。
- Origin 存在时只允许 `http://127.0.0.1:54119`；缺失 Origin 允许以兼容非浏览器 MCP 客户端；不暴露 `AllowMissingOrigin` 设置。
- Bearer token 仅 P2 后续，不是 v1 硬需求。
- 正式安全限流门禁 v1 固定：Kestrel 仅 `127.0.0.1:54119`，最大并发连接 8；请求 body 最大 1 MiB，超限 HTTP 413；每 session 固定窗口 tool 调用 120 次/分钟；全局待执行 tool 队列容量 32；30 秒语义为排队/主线程开始前硬超时；一旦同步 Godot 原子操作已开始，必须等待原子操作完成并返回实际结果；rate/队列满返回 tool 错误 `isError=true` + `AGAS_RATE_LIMITED`/`AGAS_BUSY`；stop/exit/session DELETE 进入 quiesce 并等待一致性收敛。
- 最终可交付必须严格 8 个工具；Wave 中间态工具数量不可交付。
- 生命周期 owner 是 app-root `src/UI/Main.cs`；`MainWorkspace` 只注册/注销项目上下文。
- 新增 UI 文件：`src/UI/McpSettingsDialog.cs`；`ProjectPicker` + `MainWorkspace` 提供入口，由 `Main.cs` 协调。
- UI 状态：`Disabled/Starting/Running/Stopping/Stopped/Error`；默认 `Enabled=false`。
- scene mutate 不复用现有会触盘的 UI scene handlers；独立 Facade 内存操作，维护 `dirtyProject/dirtyLayout/dirtySceneIds/pendingDeletedSceneIds`。
- save 采用“安全有序的显式部分提交”，不宣称全事务。
- 路径安全覆盖 symlink/reparse-point；目录链接不跟随，文件链接拒绝。
- `agas.assets.list` 采用后台纯 IO；主线程只取 root 快照。
- `AgasMcpProtocolHeaderMiddleware` 在 `MapMcp` 前执行，只做 Streamable HTTP 后续 `MCP-Protocol-Version` header 存在性门禁：GET/DELETE 必须有；POST 缺失时仅 `method=initialize` 放行；具体版本值和 lifecycle 由官方 SDK `1.4.1` 协商/验证。AGAS 主成功链使用 `2025-11-25`，但不把其他合法 initialize 版本字符串当安全错误。
- v1 工具不声明可选 `outputSchema`；仍返回 `structuredContent` 与同一 JSON text fallback。

### 1.3 当前仍待验证 / 不得外推项

- Gate 2 收尾状态详见 `docs/exec-plans/active/agas-mcp-gate2-audit.md`。当前不再保留旧失败态结论。
- 已完成并可作为当前结论的证据：Core 101/101 passed；Debug / Release / ExportRelease build 均 0 warning / 0 error；三项 Godot acceptance 均 PASS；最终 `127.0.0.1:54119` 无监听。
- 仍待验证且不得宣称通过：
  - 缺 `export_presets.cfg`；导出预设验证阻塞。
  - 实际 Godot 导出包未验证。
  - Windows junction/reparse 与 Windows 实机未验证。

---

## 2. 固定边界

### 2.1 必须做

- 全 C# 实现，禁止 JS/TS/GDScript。
- 使用官方 `ModelContextProtocol.AspNetCore 1.4.1`。
- stateful Streamable HTTP，显式 `options.Stateless=false`。
- 固定 `http://127.0.0.1:54119/mcp`。
- 默认关闭；设置开启才启动，关闭立即停止。
- 端口冲突报错，不自动切换。
- loopback only；Host/Origin 严格校验。
- 产品数据继续 Godot Resource `.tres`；MCP JSON 只作为 JSON-RPC 协议层。
- 设置保存到 `user://agas-mcp-settings.tres`。
- 只访问当前 UI 打开的项目。
- 拒绝绝对路径、`../`、URL、`data/base64`、`file://`、symlink/reparse 文件。
- 支持 `png/jpg/jpeg/webp`。
- 跳过 `.agas/.godot/.git/bin/obj`，不跟随目录链接。
- 不返回绝对路径/base64。
- 不提供 shell、公网、任意文件系统。
- Godot Resource/UI/Canvas 操作经主线程队列。
- HTTP 线程只做协议/安全/排队。
- `agas.assets.list` 后台纯 IO，主线程只取 project root 快照。
- tools v1 串行。
- 写操作改内存、dirty、saved=false；只有显式 `agas.project.save` 写 `.agas/*.tres`。
- 工具名最终严格 8 个：
  - `agas.project.snapshot`
  - `agas.assets.list`
  - `agas.scene.mutate`
  - `agas.layer.mutate`
  - `agas.image.mutate`
  - `agas.prompt.mutate`
  - `agas.view.set`
  - `agas.project.save`

### 2.2 明确不做

- 不实现 Bearer token 作为 v1 硬需求；仅记录为 P2 后续安全增强。
- 不做公网访问、远程监听、自动端口切换。
- 不做 stdio MCP。
- 不做任意项目路径参数。
- 不做 shell/进程/网络抓取工具。
- 不改产品持久化为 JSON。
- 不返回图片 base64、data URI、绝对路径。
- 不绕过审计门禁直接开发。

---

## 3. 计划门禁

### Gate 0：文档落地门禁（历史门禁）

目标：只写方案与计划，不改生产代码。该门禁为开发前历史门禁，当前真实状态以 `1.1` / `1.3` / Gate 2 审计文档为准。

验收命令：

```bash
test -f docs/design-docs/agas-mcp-technical-design.md
test -f docs/exec-plans/active/agas-mcp-execution-plan.md
find . -maxdepth 1 -name .git -type d -print
# 辅助人工范围检查；mtime 受编辑器/索引器影响，不作为稳定硬门禁
find docs src tests -type f -mmin -120 -print | sort
grep -F "WithHttpTransport(options => options.Stateless=false)" docs/design-docs/agas-mcp-technical-design.md docs/exec-plans/active/agas-mcp-execution-plan.md
grep -F "app.MapMcp(\"/mcp\")" docs/design-docs/agas-mcp-technical-design.md docs/exec-plans/active/agas-mcp-execution-plan.md
grep -F "AgasMcpProtocolHeaderMiddleware" docs/design-docs/agas-mcp-technical-design.md docs/exec-plans/active/agas-mcp-execution-plan.md
grep -F "MCP-Session-Id" docs/design-docs/agas-mcp-technical-design.md
grep -F "src/UI/McpSettingsDialog.cs" docs/design-docs/agas-mcp-technical-design.md docs/exec-plans/active/agas-mcp-execution-plan.md
grep -F "dirtyProject" docs/design-docs/agas-mcp-technical-design.md docs/exec-plans/active/agas-mcp-execution-plan.md
grep -F "安全有序的显式部分提交" docs/design-docs/agas-mcp-technical-design.md docs/exec-plans/active/agas-mcp-execution-plan.md
grep -F "Host header 最终只允许" docs/exec-plans/active/agas-mcp-execution-plan.md
grep -F "v1 不声明" docs/design-docs/agas-mcp-technical-design.md docs/exec-plans/active/agas-mcp-execution-plan.md
shasum -a 256 docs/design-docs/agas-mcp-technical-design.md docs/exec-plans/active/agas-mcp-execution-plan.md
```

通过标准：

- 非 git 环境只能以文件列表与 hash 作为本次文档写入证据；不得宣称 `git diff` 结果。
- `find . -maxdepth 1 -name .git -type d -print` 若无输出，只能证明当前目录无 `.git` 目录，门禁不依赖 git。
- `find docs src tests -type f -mmin -120 -print | sort` 仅作为辅助人工范围检查；mtime 不是稳定硬门禁。本轮固定证据以两份目标文档最终 `shasum -a 256` 为准。
- 所有 grep 命中。
- `shasum -a 256` 记录两份文档最终 hash，作为非 git 环境下的可复核证据。

### Gate 1：方案/计划审计门禁（开发前历史门禁）

必须至少分 3 个只读审计方向：

1. MCP/后端协议与生命周期 CR。
2. Godot 主线程/Resource/UI 集成 CR。
3. 安全/路径/测试/文档一致性 CR。

每个 CR 输出：

```md
## 已验证事实
## P0-P1
## P2
## 建议
```

当前状态：已进入开发后 Gate 2 阶段；不得把 Gate 1 历史门禁当作当前通过证据。

通过标准：

- 所有方向 P0/P1=0。
- 若存在 P0/P1，必须先修 `agas-mcp-technical-design.md` / 本计划，再复审。
- 未达到 P0/P1=0 禁止进入 Wave 1 生产代码开发。

### Gate 2：开发后 CR 门禁

实现完成后再次至少 4 个方向 CR：

1. MCP/HTTP/生命周期实现 CR。
2. Godot 主线程/Resource/UI 状态一致性 CR。
3. 安全/路径/能力边界 CR。
4. 测试覆盖/文档一致性 CR。

当前状态：Gate 2 收尾验证证据已记录；Debug / Release / ExportRelease build 均 0 warning / 0 error，Core 101/101 passed，三项 Godot acceptance 均 PASS，最终 `127.0.0.1:54119` 无监听。当前不再保留旧失败态结论；导出包与 Windows 实机仍按待验证项记录，不宣称通过。

通过标准：P0/P1=0 后进入最终统一测试。

### Gate 3：最终统一测试门禁

开发期间可按 wave 跑 `dotnet build/test`；最终必须在功能实现完后统一启动 Godot 做端到端测试。失败不得交付。

---

## 4. 分波次执行

## Wave 0：开发前准备与审计

状态：历史阶段；当前以 Gate 2 收尾验证为准，历史 Round3 的 F2 83/83 仅作历史证据。  
依赖：本技术方案和本执行计划已修订。  
生产代码：不修改。

任务：

- 组织 Gate 1 只读审计。
- 汇总 P0/P1/P2。
- 修复方案/计划中的 P0/P1。
- 复审直到 P0/P1=0。

文件范围：

- 可读：全项目必要文件。
- 可写：仅文档：
  - `docs/design-docs/agas-mcp-technical-design.md`
  - `docs/exec-plans/active/agas-mcp-execution-plan.md`

验证命令：

```bash
test -f docs/design-docs/agas-mcp-technical-design.md
test -f docs/exec-plans/active/agas-mcp-execution-plan.md
grep -F "正式工具名最终严格 8 个" docs/design-docs/agas-mcp-technical-design.md docs/exec-plans/active/agas-mcp-execution-plan.md
shasum -a 256 docs/design-docs/agas-mcp-technical-design.md docs/exec-plans/active/agas-mcp-execution-plan.md
```

验收证据：

- 三个审计方向均明确 P0/P1=0。
- 若有 P2，记录是否进入本期。

---

## Wave 1：依赖、设置 UI、生命周期骨架

状态：实现已存在；当前收尾验证已通过，历史 Round3 的 F2 83/83 仅作历史证据；导出包与 Windows 实机仍按待验证项记录。  
依赖：Gate 1 P0/P1=0。  
目标：验证官方 MCP SDK 与 Godot 进程内 Host 生命周期可行；不实现业务工具。

文件范围：

- `APP-AGAS-CS.csproj`
- `src/Mcp/AgasMcpSettingsResource.cs`
- `src/Mcp/AgasMcpSettingsStore.cs`
- `src/Mcp/AgasMcpRuntimeStatus.cs`
- `src/Mcp/AgasMcpLifecycleService.cs`
- `src/Mcp/AgasMcpHostFactory.cs`
- `src/Mcp/AgasMcpEndpointSecurity.cs`
- `src/UI/Main.cs`
- `src/UI/McpSettingsDialog.cs`
- `src/UI/ProjectPicker.cs`
- `src/UI/MainWorkspace.cs`
- 当前实际测试覆盖：`tests/Core/Mcp/AgasMcpSettingsAndLifecycleTests.cs`、`tests/Core/Mcp/AgasMcpHostTests.cs`

任务：

1. 添加 `ModelContextProtocol.AspNetCore` `1.4.1` 包引用。
2. 新增 SDK fact 测试：验证 `HttpServerTransportOptions.Stateless` 默认 `false`；host factory 必须显式使用 `builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless=false);`。
3. 新增 `AgasMcpSettingsResource`，默认 `Enabled=false`，固定 `127.0.0.1:54119/mcp`，不含 `AllowMissingOrigin`。
4. 新增 settings store，读写 `user://agas-mcp-settings.tres`。
5. 新增 runtime status：`Disabled/Starting/Running/Stopping/Stopped/Error`。
6. 新增 lifecycle service：Start/Stop/Dispose，端口冲突报错，不自动切换。
7. 新增 host factory：只绑定 `127.0.0.1:54119`，Kestrel 最大并发连接 8、请求 body 最大 1 MiB，先注册 `AddMcpServer().WithHttpTransport(options => options.Stateless=false)`，再 `app.MapMcp("/mcp")` 映射同一 `/mcp` 的 `POST/GET/DELETE`；禁止通过 `MapMcp` 的 options lambda 配置 Stateless。
8. 新增 Host/Origin guard middleware 骨架；新增 `AgasMcpProtocolHeaderMiddleware` 骨架并保证在 `MapMcp` 前注册。
9. `src/UI/Main.cs` 成为生命周期 owner；Godot 退出 Dispose host。
10. 新增 `src/UI/McpSettingsDialog.cs`；`ProjectPicker` 与 `MainWorkspace` 只提供入口，由 `Main.cs` 协调。

真实可执行验证：

```bash
dotnet restore APP-AGAS-CS.sln
dotnet build APP-AGAS-CS.sln -c Debug
dotnet test tests/Core/APP-AGAS-CS.Core.Tests.csproj -c Debug --no-build
```

端口冲突测试建议：

- 优先在 C# 测试内用 `TcpListener` 绑定 `IPAddress.Parse("127.0.0.1"), 54119`，再启动 MCP，断言 `AGAS_PORT_IN_USE` 与 status `Error`。
- 不引入 Python/Node/Bash 脚本语言模拟端口占用。

验收证据：

- build/test 通过。
- 默认关闭、开启、关闭、端口冲突都有测试或日志输出。
- endpoint 与 Host 不出现 `localhost`/IPv6。

---

## Wave 2：主线程队列、串行执行器、当前项目 Facade

状态：实现已存在；当前收尾验证已通过，历史 Round3 的 F2 83/83 仅作历史证据；导出包与 Windows 实机仍按待验证项记录。  
依赖：MCP Host 生命周期可启动/停止。  
目标：建立 HTTP 到 Godot 主线程的唯一访问通道。

文件范围：

- `src/Mcp/AgasMcpMainThreadDispatcher.cs`
- `src/Mcp/AgasMcpSerialToolExecutor.cs`
- `src/Mcp/AgasMcpProjectContextProvider.cs`
- `src/Mcp/AgasMcpProjectFacade.cs`
- `src/Mcp/Contracts/AgasMcpDtos.cs`
- `src/UI/MainWorkspace.cs`（只注册/注销当前项目上下文）
- 当前实际测试覆盖：`tests/Core/Mcp/AgasMcpSerialToolExecutorTests.cs`、`tests/Core/Mcp/AgasMcpToolRuntimeTests.cs`

任务：

1. 实现主线程队列接口：HTTP 线程提交，Godot 主线程 drain。
2. 实现请求超时/取消/异常转换；30 秒仅对排队/主线程开始前生效为硬超时并返回 `AGAS_TIMEOUT`；一旦同步 Godot 原子操作已开始，必须等待完成并返回实际结果，避免客户端报超时但副作用继续。
3. 实现 tools v1 串行执行器；全局待执行 tool 队列容量固定 32，队列满返回 `isError=true` / `AGAS_BUSY`。
4. 实现 `AgasMcpProjectContextProvider`，`MainWorkspace` 只注册/注销当前项目上下文。
5. 实现 `AgasMcpProjectFacade`，只暴露当前 UI 打开的项目状态与受控操作入口。
6. 未打开项目时返回 `AGAS_NO_OPEN_PROJECT`。
7. 禁止 Facade 接收 project root 参数。
8. 项目关闭时 server 按 Enabled 继续运行，tools 返回 `AGAS_NO_OPEN_PROJECT`。
9. stop/exit/session DELETE 取消待执行 tool；用户关闭 MCP 或 AGAS 退出必须清空全局队列。

真实可执行验证：

```bash
dotnet build APP-AGAS-CS.sln -c Debug
dotnet test tests/Core/APP-AGAS-CS.Core.Tests.csproj -c Debug --no-build
```

验收证据：

- 串行测试通过。
- HTTP 线程不能直接触碰 Godot `Resource`/UI 的调用链经 CR 确认。

---

## Wave 3：安全路径、错误码、wire contract

状态：实现已存在；当前收尾验证已通过，历史 Round3 的 F2 83/83 仅作历史证据；导出包与 Windows 实机仍按待验证项记录。  
依赖：Facade 可在主线程读取当前项目。  
目标：先完成安全边界与 schema，再实现工具。

文件范围：

- `src/Mcp/AgasMcpPathValidator.cs`
- `src/Mcp/AgasMcpRepositoryErrorMapper.cs`
- `src/Mcp/Contracts/AgasMcpToolInputs.cs`
- `src/Mcp/Contracts/AgasMcpToolOutputs.cs`
- `src/Mcp/Contracts/AgasMcpErrorCodes.cs`
- 当前实际测试覆盖：`tests/Core/Mcp/AgasMcpPathValidatorTests.cs`、`tests/Core/Mcp/AgasMcpToolContractTests.cs`、`tests/Core/Mcp/AgasMcpToolRuntimeTests.cs`、`tests/Core/Mcp/AgasMcpMutateSchemaPrecisionTests.cs`

任务：

1. 实现统一路径 validator。
2. 拒绝绝对路径、`../`、URL、`data:`、base64、`file://`。
3. 图片扩展名限制：png/jpg/jpeg/webp。
4. 跳过 `.agas/.godot/.git/bin/obj`。
5. 不跟随目录 symlink/reparse point；文件 symlink/reparse point 拒绝；project root 本身若为 symlink/reparse-point，MCP 拒绝项目操作并返回 `AGAS_PROJECT_ROOT_UNSAFE`，UI 现有行为不在此改。
6. 输出路径统一项目相对 `/` 分隔。
7. 定义统一 success/error envelope。
8. 定义最小错误码表：`AGAS_NO_OPEN_PROJECT`、`AGAS_PATH_REJECTED`、`AGAS_ASSET_NOT_FOUND`、`AGAS_SCENE_NOT_FOUND`、`AGAS_LAYER_NOT_FOUND`、`AGAS_IMAGE_NOT_FOUND`、`AGAS_PROMPT_NOT_FOUND`、`AGAS_LAYER_LOCKED`、`AGAS_LAYER_HIDDEN`、`AGAS_INVALID_OPERATION`、`AGAS_SAVE_FAILED`、`AGAS_PROJECT_ROOT_UNSAFE`、`AGAS_RATE_LIMITED`、`AGAS_BUSY`、`AGAS_PORT_IN_USE`、`AGAS_TIMEOUT`、`AGAS_INTERNAL_ERROR`。
9. 定义分页字段：`limit/offset/count/totalCount/hasMore/nextOffset`。
10. 定义所有 8 工具 wire-level `inputSchema`、structuredContent DTO 字段表 + text fallback；v1 不声明可选 `outputSchema`。
11. 4 个 mutate 工具的 `inputSchema` 必须实际编码 JSON Schema 2020-12 条件约束，不能只写 prose；保持 top-level `additionalProperties:false`，内部二次校验保留：
    - scene：`add=>name`；`rename=>sceneId+name`；`delete/setActive=>sceneId`。
    - layer：`add=>name`；`rename=>layerId+name`；`delete/setCurrent=>layerId`；`move=>layerId+direction`；`setVisible=>layerId+visible`；`setLocked=>layerId+locked`。
    - image：`add=>imagePath`；`delete=>imageId`；`move=>imageId` 且 `x/y` 至少一个；`update=>imageId` 且更新字段至少一个。
    - prompt：`add=>text`；`delete=>fragmentId`；`update=>fragmentId+text`；`move=>fragmentId+direction`；`setEnabled=>fragmentId+enabled`。
12. 定义业务错误 `isError=true` 与协议错误 JSON-RPC error 的分界。
13. Repository 错误包装不泄露绝对路径、项目根、临时文件名、栈。

真实可执行验证：

```bash
dotnet build APP-AGAS-CS.sln -c Debug
dotnet test tests/Core/APP-AGAS-CS.Core.Tests.csproj -c Debug --no-build
```

路径验证矩阵：

- 必须拒绝：`/etc/passwd`、`C:\Windows\win.ini`、`../secret.png`、`sub/../../secret.png`、`http://example.com/a.png`、`https://example.com/a.png`、`file:///tmp/a.png`、`data:image/png;base64,xxx`、`a.png;base64`、`.agas/project.tres`、`.godot/imported/x.png`、`.git/objects/x`、`bin/x.png`、`obj/x.png`、`a.gif`、`a.svg`。
- 必须允许：`a.webp`、`images/a.png`，前提是文件真实存在、父目录不是 symlink/reparse-point、文件不是 symlink/reparse-point。
- 平台条件：macOS/Linux 覆盖 `link-dir/outside.png` 目录 symlink 跳过/拒绝、`link-file.png` 文件 symlink 拒绝；Windows 覆盖 `junction-dir/outside.png` 目录 junction/reparse-point 跳过/拒绝、`reparse-file.png` 文件 reparse-point 拒绝。无对应平台时记录跳过原因。

验收证据：

- 所有拒绝项返回结构化错误；`a.webp` 与 `images/a.png` 合法样例通过。
- 合法图片相对路径通过。
- macOS symlink 按当前平台证据记录；Windows reparse/junction 必须有 Windows 实机证据，当前未验证时不得宣称通过。
- project root 本身 symlink/reparse-point 返回 `AGAS_PROJECT_ROOT_UNSAFE`。
- 任何输出不含绝对项目根字符串、不含 `data:image`、不含 `base64`。

---

## Wave 4：协议 wire smoke 与只读工具

状态：实现已存在；当前收尾验证已通过，历史 Round3 的 F2 83/83 仅作历史证据；导出包与 Windows 实机仍按待验证项记录。  
依赖：安全 validator 与 DTO 完成。  
目标：实现只读 MCP 工具，打通协议层。

文件范围：

- `src/Mcp/Tools/AgasProjectTools.cs`
- `src/Mcp/Tools/AgasAssetsTools.cs`
- `src/Mcp/AgasMcpHostFactory.cs`（注册工具）
- 当前实际测试覆盖：`tests/Core/Mcp/AgasMcpHostTests.cs`、`tests/Core/Mcp/AgasMcpToolContractTests.cs`

工具：

- `agas.project.snapshot`
- `agas.assets.list`

历史计划说明：Wave 4 原范围只覆盖 2 个只读工具；当前 8 工具实现已存在，收尾验证已覆盖当前 MCP runtime acceptance；导出包与 Windows 实机仍按待验证项记录。

任务：

1. 注册工具名、描述、annotations、完整 `inputSchema`；v1 不声明可选 `outputSchema`。
2. `snapshot` 返回当前项目、scene/layer/grid/dirty/saved 轻量摘要，包含 dirty 集合。
3. `assets.list` 实现分页、过滤、跳过内部目录、symlink/reparse 规则。
4. `assets.list` 主线程只取 project root 快照，后台纯 IO 枚举。
5. 未打开项目返回 `AGAS_NO_OPEN_PROJECT`。
6. 不返回绝对路径/base64。
7. text fallback 与 structuredContent 一致。

真实可执行验证：

```bash
dotnet build APP-AGAS-CS.sln -c Debug
dotnet test tests/Core/APP-AGAS-CS.Core.Tests.csproj -c Debug --no-build
```

协议验证：

- 主链 `POST /mcp initialize`：`Accept: application/json,text/event-stream`（允许 header 逗号后有空格）、`Content-Type: application/json`、返回 `protocolVersion=2025-11-25` 与 `MCP-Session-Id`。
- 主链 `notifications/initialized`：后续 POST 携带 `MCP-Session-Id` 与 `MCP-Protocol-Version: 2025-11-25`，返回 `202 Accepted`。
- `tools/list`：Wave 4 历史中间态只列出当时已实现工具；当前实现已进入 8 工具状态，但仍须以 Gate 2 P0/P1=0 后的最终 contract 验证为准。
- `tools/call agas.project.snapshot` 成功。
- `tools/call agas.assets.list` 成功且分页字段正确。

验收证据：

- 返回不含绝对路径/base64。
- 未打开项目时错误可操作。
- POST Accept、Content-Type、session/version header 测试通过。

---

## Wave 5：写内存工具与 dirty 集合

状态：实现已存在；当前收尾验证已通过，历史 Round3 的 F2 83/83 仅作历史证据；导出包与 Windows 实机仍按待验证项记录。  
依赖：只读工具和主线程队列稳定。  
目标：实现所有 mutate 工具，但不自动落盘。

文件范围：

- `src/Mcp/Tools/AgasSceneTools.cs`
- `src/Mcp/Tools/AgasLayerTools.cs`
- `src/Mcp/Tools/AgasImageTools.cs`
- `src/Mcp/Tools/AgasPromptTools.cs`
- `src/Mcp/Tools/AgasViewTools.cs`
- `src/Mcp/AgasMcpProjectFacade.cs`
- `src/Features/Canvas/CanvasWorkspace.cs`（只允许补受控内存方法，不做无关重构）
- 当前实际测试覆盖：`tests/Core/Mcp/AgasMcpDirtyStateTests.cs`、`tests/Core/Mcp/AgasMcpToolContractTests.cs`、`tests/Core/Mcp/AgasMcpMutateSchemaPrecisionTests.cs`

工具：

- `agas.scene.mutate`
- `agas.layer.mutate`
- `agas.image.mutate`
- `agas.prompt.mutate`
- `agas.view.set`

历史计划说明：Wave 5 原范围累计 7 个工具、缺 `agas.project.save`；当前 `agas.project.save` 实现已存在，收尾验证已覆盖当前 MCP runtime acceptance；导出包与 Windows 实机仍按待验证项记录。

任务：

1. Scene mutate：add/rename/delete/setActive。
2. Scene mutate 不复用现有会触盘 UI scene handlers；独立 Facade 纯内存操作。
3. Facade 维护 `dirtyProject/dirtyLayout/dirtySceneIds/pendingDeletedSceneIds`。
4. Layer mutate：add/rename/delete/move/setVisible/setLocked/setCurrent。
5. Image mutate：add/delete/move/update。
6. Prompt mutate：add/delete/update/move/setEnabled。
7. View set：设置 `SceneViewStateResource`；因 ViewState 属于 `AgasSceneResource`，必须明确标记对应 `dirtySceneIds`。
8. 所有写工具必须经主线程队列和串行执行器。
9. 所有写工具只改内存，设置 dirty/saved=false。
10. `imagePath` 必须经过 Wave 3 validator。
11. hidden/locked layer 编辑规则与 UI 一致。
12. 写工具不调用 `AgasResourceRepository.Save` / `SaveProject()`。

真实可执行验证：

```bash
dotnet build APP-AGAS-CS.sln -c Debug
dotnet test tests/Core/APP-AGAS-CS.Core.Tests.csproj -c Debug --no-build
```

语义验证：

- 调用 mutate 后 `dirty=true`、`saved=false`。
- 调用 mutate 后 `.agas/*.tres` 文件内容/mtime 不变化。
- hidden/locked layer 写操作被拒绝。
- 非法 imagePath 被拒绝。
- 并发 mutate 串行执行。
- 删除 scene 只加入 `pendingDeletedSceneIds`，save 前旧文件仍存在。
- `agas.view.set` 后 `dirtySceneIds` 包含对应 sceneId。

验收证据：

- 所有 mutate contract 测试通过。
- 代码搜索/CR 证明 mutate 工具未直接保存磁盘，且未复用触盘 UI handler。

---

## Wave 6：显式保存工具与完整 8 工具

状态：实现已存在；当前收尾验证已通过，历史 Round3 的 F2 83/83 仅作历史证据；导出包与 Windows 实机仍按待验证项记录。  
依赖：写内存工具语义正确。  
目标：实现 `agas.project.save`，完成最终严格 8 工具。

文件范围：

- `src/Mcp/Tools/AgasProjectTools.cs`
- `src/Mcp/AgasMcpProjectFacade.cs`
- `src/Mcp/AgasMcpRepositoryErrorMapper.cs`
- `src/UI/MainWorkspace.cs`（只允许必要上下文/保存入口适配）
- 当前实际测试覆盖：`tests/Core/Mcp/AgasMcpDirtyStateTests.cs`、`tests/Core/Mcp/AgasMcpToolContractTests.cs`、`tests/Core/Mcp/AgasMcpMutateSchemaPrecisionTests.cs`

任务：

1. 实现 `agas.project.save`。
2. 保存语义固定为“安全有序的显式部分提交”，不宣称全事务。
3. 顺序：dirty scenes -> project -> layout -> project 成功后应用 pending deletes。
4. 成功项移除对应 dirty；失败项保留 dirty。
5. 返回 `savedFiles/failedFiles/pendingDirtyFiles`，只包含相对逻辑路径。
6. 失败时返回可操作错误，不泄露绝对路径。
7. `tools/list` 正式只暴露 8 个工具，不多不少。
8. 每个工具描述、`inputSchema`、annotations 准确匹配实际功能；v1 不声明可选 `outputSchema`。
9. 从 raw `tools/list` 响应提取 4 个 mutate 工具 `inputSchema`，用 JSON Schema 2020-12 validator 执行正反输入验证，证明 `allOf/if/then/anyOf` 条件由 schema 本身生效。

真实可执行验证：

```bash
dotnet build APP-AGAS-CS.sln -c Debug
dotnet test tests/Core/APP-AGAS-CS.Core.Tests.csproj -c Debug --no-build
```

协议/语义验证：

- `tools/list` 精确包含 8 个正式工具，不多不少。
- 每个工具有完整 `inputSchema`、structuredContent + text fallback；v1 不声明 `outputSchema`。
- 4 个 mutate schema 正反输入覆盖：scene/layer/image/prompt 的缺条件字段、move 缺 direction、image move 缺 x/y、image update 无更新字段等必须在 schema 层失败。
- mutate 后 save 前磁盘不变。
- save 后 `.agas/project.tres`、`.agas/workspace-layout.tres`、dirty scenes 正确写入。
- project 保存失败时不应用 pending deletes。
- pending delete 删除旧 scene 文件失败时保留 `pendingDeletedSceneIds`，响应 `saved=false`，逻辑路径进入 `failedFiles` 与 `pendingDirtyFiles`。
- 部分失败返回 `savedFiles/failedFiles/pendingDirtyFiles` 且 dirty 不错清。

验收证据：

- 8 工具 contract 测试通过。
- 保存语义测试通过。

---

## Wave 7：Host/Origin、安全矩阵、完整协议

状态：实现已存在；当前收尾验证已通过，历史 Round3 的 F2 83/83 仅作历史证据；导出包与 Windows 实机仍按待验证项记录。  
依赖：8 工具完整。  
目标：用 C# integration tests 在进程内启动 host/stub context，完成协议与安全自动验收；本波不启动 Godot、不跑 curl、不依赖手工 UI。

文件范围：

- `src/Mcp/AgasMcpEndpointSecurity.cs`
- `src/Mcp/AgasMcpProtocolHeaderMiddleware.cs`
- `src/Mcp/AgasMcpHostFactory.cs`
- `src/Mcp/AgasMcpLifecycleService.cs`
- `src/Mcp/AgasMcpSerialToolExecutor.cs`
- 当前实际测试覆盖：`tests/Core/Mcp/AgasMcpHostTests.cs`、`tests/Core/Mcp/AgasMcpSerialToolExecutorTests.cs`、`tests/Core/Mcp/AgasMcpSettingsAndLifecycleTests.cs`

任务：

1. 在 C# integration tests 内启动真实 ASP.NET Core host + stub project context，验证 `initialize` / `notifications/initialized` / `tools/list` / `tools/call`，不启动 Godot Editor。
2. 验证 `AgasMcpProtocolHeaderMiddleware` 在 `MapMcp` 前执行，只做 Streamable HTTP 后续 `MCP-Protocol-Version` header 存在性门禁，不校验具体版本值、不处理 MCP lifecycle、不访问 SDK 内部 session：`GET`/`DELETE` 缺失 header 返回 `400`；`POST` 缺失 header 时仅 `method=initialize` 放行，其余返回 `400`；为判定 initialize 可安全 buffer/read/rewind 并恢复 request body。
3. 验证 initialize 版本协商完全交给官方 SDK `1.4.1`：主链 `2025-11-25` 返回 `2025-11-25`；SDK 支持的 `2025-06-18` 返回同版本；合法但 SDK 不支持的版本记录 SDK 返回的服务端支持版本；缺失、类型非法、malformed 由 SDK 协议错误处理。
4. 验证 `MCP-Session-Id` 生成；`POST` 缺 session 为 `400`，POST 未知 session 为 `404`；`GET` 缺 session为 `400`，GET 未知 session 为 `404`；合法 session `DELETE` 后再用旧 session 发 `POST` 与 `GET` 均必须 `404`。
5. 验证 `DELETE` 遵循官方 SDK 1.4.1 + MCP 规范最小语义：合法 session 终止；`DELETE` 缺失/未知 session 响应状态只记录 SDK 1.4.1 实际行为，不强行断言 `400/404`，不自定义访问 SDK 内部 session；若 SDK 1.4.1 对 unknown session `DELETE` 返回 2xx，则 executor cleanup 按幂等 cleanup 处理，用于防遗留状态，不是越权。
6. 验证 POST Accept：`application/json,text/event-stream`（允许 header 逗号后有空格）。
7. 验证 GET Accept：`text/event-stream`。
8. 验证 `Content-Type: application/json` 请求与响应 `application/json`/`text/event-stream`。
9. Host/Origin 合法/非法矩阵测试：只允许 Host `127.0.0.1:54119`；Origin 存在时只允许 `http://127.0.0.1:54119`；缺失 Origin 允许。
10. Host/Origin 反向测试必须使用除此之外完全合法的 initialize POST 请求来隔离判因：合法 `Accept`、合法 `Content-Type`、合法 JSON-RPC body、合法 `params.protocolVersion`，只改变 evil `Host` 或 evil `Origin`。
11. 验证请求 body 超过 1 MiB 在进入 JSON-RPC/MCP 前返回 HTTP `413 Payload Too Large`。
12. 验证 Kestrel 只监听/绑定 `127.0.0.1:54119`，最大并发连接配置为 8；不允许 `localhost`、IPv6、`0.0.0.0` 或自动换端口。
13. 验证每 session 固定窗口最多 120 次/分钟：突发 121 次 `tools/call` 时第 121 次返回 `isError=true` / `AGAS_RATE_LIMITED`。
14. 验证全局待执行 tool 队列容量 32：构造阻塞 stub tool 占满队列后，新 tool call 返回 `isError=true` / `AGAS_BUSY`。
15. 验证 30 秒语义：排队/主线程开始前为硬超时，阻塞 stub tool 未开始时返回 `isError=true` / `AGAS_TIMEOUT`；同步 Godot 原子操作开始后必须等待完成并返回实际结果，不允许客户端先超时而副作用继续。
16. 验证关闭设置后端口释放，stop/exit/session DELETE 进入 quiesce，清空/取消未开始队列，并等待已开始的同步原子操作完成后返回一致结果；这是安全一致性约束，不是无限强杀线程。
17. 重复开关 10 次无残留端口/异常。
18. C# `TcpListener` 端口冲突测试。
19. P2 安全验证：构造 8 个 `GET` SSE 长连接占用 Kestrel 最大并发连接，再发一个合法 `POST`；记录实际行为并保证安全边界不被绕过。若 SDK 对 GET SSE 返回 `405 Method Not Allowed`，记录该 SDK 行为；该项用 C# integration test 实现，不写 shell 脚本。

真实可执行验证：

```bash
dotnet build APP-AGAS-CS.sln -c Debug
dotnet test tests/Core/APP-AGAS-CS.Core.Tests.csproj -c Debug --no-build
```

Wave 7 明确不执行：

- 不启动 Godot Editor/Mono。
- 不跑 `curl`。
- 不把 MCP Inspector 作为硬门禁。
- 不用 shell 脚本替代 C# integration tests。

验收证据：

- C# integration tests 输出覆盖协议、Host/Origin、body 413、rate limit、队列满、超时、关闭清队列。
- 合法本地请求可进入 MCP 流程；非法 Host/Origin 被拒绝且判因隔离。
- 缺失 Origin 兼容非浏览器客户端。
- 关闭后端口不监听。

---

## Wave 8：Godot 进程内/导出验证与文档收尾

状态：当前 MCP runtime acceptance 已通过；Core 101/101、三项 Godot acceptance PASS、最终端口无监听均已记录。导出验证因缺 `export_presets.cfg` 阻塞；实际 Godot 导出包、Windows junction/reparse 与 Windows 实机不得宣称通过。  
依赖：安全矩阵通过。  
目标：功能实现完后统一启动 Godot 做最终端到端测试；标准 `curl` 是目标客户端自动验收硬门禁，MCP Inspector 仅可选人工兼容性检查，不作为硬门禁。

文件范围：

- 文档：
  - `docs/design-docs/agas-mcp-technical-design.md`
  - `docs/exec-plans/active/agas-mcp-execution-plan.md`
  - 如项目已有用户文档入口，再补 MCP 使用说明（开发前确认具体文件）
- 可能的导出配置文件（仅在验证需要时改，改前单独确认范围）
- 测试：按前波次已有测试补缺。

任务：

1. 先跑 build/test。
2. 启动 Godot Editor/Mono，但默认 `Enabled=false` 时必须先确认 `54119` 无监听。
3. 用户在 AGAS 设置 UI 开启 MCP；再确认只监听 `127.0.0.1:54119`。
4. 使用标准 `curl` 完整验证 initialize、session、tools/list、8 个 tools/call、Host/Origin、关闭/重开恢复。
5. 关闭 MCP，端口释放；退出 Godot，端口释放。
6. 保存后关闭/重开项目，验证 scene/layer/image/prompt/view 与 dirty 状态恢复。
7. 导出验证前先核对 `export_presets.cfg`；当前若缺失已验证 export preset，只记录阻塞证据，不写伪导出命令，不宣称导出通过。
8. 更新待验证项状态。
9. 补充使用说明：默认关闭、端口、工具、限制、安全边界。

真实顺序与命令矩阵：

```bash
# 0) 自动测试先通过
dotnet build APP-AGAS-CS.sln -c Debug --no-restore
dotnet build APP-AGAS-CS.csproj -c Release --no-restore
dotnet build APP-AGAS-CS.csproj -c ExportRelease --no-restore
dotnet test tests/Core/APP-AGAS-CS.Core.Tests.csproj -c Debug --no-restore

# 0.5) 当前已通过的 Godot acceptance 场景
godot --headless --path . res://tests/Godot/McpResourceRuntimeAcceptance.tscn
godot --headless --path . res://tests/Godot/McpHttpRuntimeAcceptance.tscn
godot --headless --path . res://tests/Godot/McpSettingsUiAcceptance.tscn

# 1) 启动 Godot；此时 MCP 默认关闭
open -a "Godot_mono" --args --path .

# 2) 默认关闭检查：预期无输出/无监听
if lsof -nP -iTCP:54119 -sTCP:LISTEN; then
  echo "FAIL: MCP should not listen before user enables it"
  exit 1
else
  echo "OK: default MCP disabled, no 54119 listener"
fi

# 3) 用户在 AGAS 设置 UI 开启 MCP 后：预期只监听 127.0.0.1:54119
lsof -nP -iTCP:54119 -sTCP:LISTEN
```

专用测试项目要求：以下 `curl` 在专用测试项目上执行，避免污染真实作品。`SCENE_ID/LAYER_ID/IMAGE_PATH` 可从专用测试项目预置数据、UI、或下方前两条 `agas.project.snapshot` / `agas.assets.list` 响应中复制后再执行 mutate/save 命令：

```bash
SCENE_ID='scene-001'          # 从 snapshot 复制
LAYER_ID='layer-001'          # 从 snapshot 复制
IMAGE_PATH='images/a.png'     # 从 assets.list 复制；a.webp 与 images/a.png 均为合法路径样例
SCENE_NAME_AFTER='MCP Curl Scene'
UNKNOWN_SESSION='00000000-0000-0000-0000-000000000000'
```

#### Wave 8 curl：initialize 正反向

```bash
# initialize 正向：复制响应 header 中的 MCP-Session-Id
curl -i 'http://127.0.0.1:54119/mcp' \
  -H 'Host: 127.0.0.1:54119' \
  -H 'Accept: application/json,text/event-stream' \
  -H 'Content-Type: application/json' \
  --data '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "initialize",
    "params": {
      "protocolVersion": "2025-11-25",
      "capabilities": {},
      "clientInfo": { "name": "agas-curl-e2e", "version": "0.1.0" }
    }
  }'

SESSION='<copy MCP-Session-Id from initialize response header>'

# initialize 正向 + 合法 Origin：预期通过；复制响应 header 中的 MCP-Session-Id 可作为 ORIGIN_SESSION
curl -i 'http://127.0.0.1:54119/mcp' \
  -H 'Host: 127.0.0.1:54119' \
  -H 'Origin: http://127.0.0.1:54119' \
  -H 'Accept: application/json,text/event-stream' \
  -H 'Content-Type: application/json' \
  --data '{"jsonrpc":"2.0","id":89,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"agas-curl-e2e","version":"0.1.0"}}}'

# initialize 旧版本：预期进入 SDK lifecycle；SDK 支持 2025-06-18 时 result.protocolVersion 返回 2025-06-18
curl -i 'http://127.0.0.1:54119/mcp' \
  -H 'Host: 127.0.0.1:54119' \
  -H 'Accept: application/json,text/event-stream' \
  -H 'Content-Type: application/json' \
  --data '{"jsonrpc":"2.0","id":90,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"agas-curl-e2e","version":"0.1.0"}}}'

OLD_SESSION='<copy MCP-Session-Id from old-version initialize response header if continuing this session; subsequent headers must use MCP-Protocol-Version: 2025-06-18>'

# initialize 缺失 params.protocolVersion：预期由官方 SDK 协议错误处理，不由 AGAS header middleware 拒绝
curl -i 'http://127.0.0.1:54119/mcp' \
  -H 'Host: 127.0.0.1:54119' \
  -H 'Accept: application/json,text/event-stream' \
  -H 'Content-Type: application/json' \
  --data '{"jsonrpc":"2.0","id":91,"method":"initialize","params":{"capabilities":{},"clientInfo":{"name":"agas-curl-e2e","version":"0.1.0"}}}'

# initialize params.protocolVersion 类型非法：预期由官方 SDK 协议错误处理，不由 AGAS header middleware 拒绝
curl -i 'http://127.0.0.1:54119/mcp' \
  -H 'Host: 127.0.0.1:54119' \
  -H 'Accept: application/json,text/event-stream' \
  -H 'Content-Type: application/json' \
  --data '{"jsonrpc":"2.0","id":92,"method":"initialize","params":{"protocolVersion":123,"capabilities":{},"clientInfo":{"name":"agas-curl-e2e","version":"0.1.0"}}}'
```

#### Wave 8 curl：session / POST / GET / DELETE

```bash
# notifications/initialized
curl -i 'http://127.0.0.1:54119/mcp' \
  -H 'Host: 127.0.0.1:54119' \
  -H 'Accept: application/json,text/event-stream' \
  -H 'Content-Type: application/json' \
  -H "MCP-Session-Id: $SESSION" \
  -H 'MCP-Protocol-Version: 2025-11-25' \
  --data '{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}'

# tools/list：断言 8 个工具均有完整 inputSchema，4 个 mutate 使用 allOf/if/then/anyOf，且 v1 不声明 outputSchema
curl -i 'http://127.0.0.1:54119/mcp' \
  -H 'Host: 127.0.0.1:54119' \
  -H 'Accept: application/json,text/event-stream' \
  -H 'Content-Type: application/json' \
  -H "MCP-Session-Id: $SESSION" \
  -H 'MCP-Protocol-Version: 2025-11-25' \
  --data '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'

# POST 缺 session：预期 400
curl -i 'http://127.0.0.1:54119/mcp' \
  -H 'Host: 127.0.0.1:54119' \
  -H 'Accept: application/json,text/event-stream' \
  -H 'Content-Type: application/json' \
  -H 'MCP-Protocol-Version: 2025-11-25' \
  --data '{"jsonrpc":"2.0","id":10,"method":"tools/list","params":{}}'

# POST 未知 session：预期 404
curl -i 'http://127.0.0.1:54119/mcp' \
  -H 'Host: 127.0.0.1:54119' \
  -H 'Accept: application/json,text/event-stream' \
  -H 'Content-Type: application/json' \
  -H "MCP-Session-Id: $UNKNOWN_SESSION" \
  -H 'MCP-Protocol-Version: 2025-11-25' \
  --data '{"jsonrpc":"2.0","id":11,"method":"tools/list","params":{}}'

# GET 缺失 session：预期 400
curl -i --max-time 2 'http://127.0.0.1:54119/mcp' \
  -H 'Host: 127.0.0.1:54119' \
  -H 'Accept: text/event-stream' \
  -H 'MCP-Protocol-Version: 2025-11-25'

# GET 未知 session：预期 404
curl -i --max-time 2 'http://127.0.0.1:54119/mcp' \
  -H 'Host: 127.0.0.1:54119' \
  -H 'Accept: text/event-stream' \
  -H "MCP-Session-Id: $UNKNOWN_SESSION" \
  -H 'MCP-Protocol-Version: 2025-11-25'
```

#### Wave 8 curl：8 个正式工具各一次

```bash
# 1) agas.project.snapshot
curl -i 'http://127.0.0.1:54119/mcp' \
  -H 'Host: 127.0.0.1:54119' -H 'Accept: application/json,text/event-stream' -H 'Content-Type: application/json' \
  -H "MCP-Session-Id: $SESSION" -H 'MCP-Protocol-Version: 2025-11-25' \
  --data '{"jsonrpc":"2.0","id":20,"method":"tools/call","params":{"name":"agas.project.snapshot","arguments":{"includeScenes":true,"includeLayers":true,"includeImages":true,"includePrompts":true}}}'

# 2) agas.assets.list
curl -i 'http://127.0.0.1:54119/mcp' \
  -H 'Host: 127.0.0.1:54119' -H 'Accept: application/json,text/event-stream' -H 'Content-Type: application/json' \
  -H "MCP-Session-Id: $SESSION" -H 'MCP-Protocol-Version: 2025-11-25' \
  --data '{"jsonrpc":"2.0","id":21,"method":"tools/call","params":{"name":"agas.assets.list","arguments":{"query":"","extensions":["png","jpg","jpeg","webp"],"limit":50,"offset":0}}}'

# 3) agas.scene.mutate
curl -i 'http://127.0.0.1:54119/mcp' \
  -H 'Host: 127.0.0.1:54119' -H 'Accept: application/json,text/event-stream' -H 'Content-Type: application/json' \
  -H "MCP-Session-Id: $SESSION" -H 'MCP-Protocol-Version: 2025-11-25' \
  --data "{\"jsonrpc\":\"2.0\",\"id\":22,\"method\":\"tools/call\",\"params\":{\"name\":\"agas.scene.mutate\",\"arguments\":{\"op\":\"rename\",\"sceneId\":\"$SCENE_ID\",\"name\":\"$SCENE_NAME_AFTER\"}}}"

# 4) agas.layer.mutate
curl -i 'http://127.0.0.1:54119/mcp' \
  -H 'Host: 127.0.0.1:54119' -H 'Accept: application/json,text/event-stream' -H 'Content-Type: application/json' \
  -H "MCP-Session-Id: $SESSION" -H 'MCP-Protocol-Version: 2025-11-25' \
  --data "{\"jsonrpc\":\"2.0\",\"id\":23,\"method\":\"tools/call\",\"params\":{\"name\":\"agas.layer.mutate\",\"arguments\":{\"op\":\"setVisible\",\"sceneId\":\"$SCENE_ID\",\"layerId\":\"$LAYER_ID\",\"visible\":true}}}"

# 5) agas.image.mutate
curl -i 'http://127.0.0.1:54119/mcp' \
  -H 'Host: 127.0.0.1:54119' -H 'Accept: application/json,text/event-stream' -H 'Content-Type: application/json' \
  -H "MCP-Session-Id: $SESSION" -H 'MCP-Protocol-Version: 2025-11-25' \
  --data "{\"jsonrpc\":\"2.0\",\"id\":24,\"method\":\"tools/call\",\"params\":{\"name\":\"agas.image.mutate\",\"arguments\":{\"op\":\"add\",\"sceneId\":\"$SCENE_ID\",\"layerId\":\"$LAYER_ID\",\"imagePath\":\"$IMAGE_PATH\",\"x\":0,\"y\":0}}}"

# 6) agas.prompt.mutate
curl -i 'http://127.0.0.1:54119/mcp' \
  -H 'Host: 127.0.0.1:54119' -H 'Accept: application/json,text/event-stream' -H 'Content-Type: application/json' \
  -H "MCP-Session-Id: $SESSION" -H 'MCP-Protocol-Version: 2025-11-25' \
  --data "{\"jsonrpc\":\"2.0\",\"id\":25,\"method\":\"tools/call\",\"params\":{\"name\":\"agas.prompt.mutate\",\"arguments\":{\"op\":\"add\",\"sceneId\":\"$SCENE_ID\",\"layerId\":\"$LAYER_ID\",\"text\":\"curl prompt fragment\"}}}"

# 7) agas.view.set
curl -i 'http://127.0.0.1:54119/mcp' \
  -H 'Host: 127.0.0.1:54119' -H 'Accept: application/json,text/event-stream' -H 'Content-Type: application/json' \
  -H "MCP-Session-Id: $SESSION" -H 'MCP-Protocol-Version: 2025-11-25' \
  --data "{\"jsonrpc\":\"2.0\",\"id\":26,\"method\":\"tools/call\",\"params\":{\"name\":\"agas.view.set\",\"arguments\":{\"sceneId\":\"$SCENE_ID\",\"cameraX\":10,\"cameraY\":20,\"zoom\":1.25}}}"

# 8) agas.project.save
curl -i 'http://127.0.0.1:54119/mcp' \
  -H 'Host: 127.0.0.1:54119' -H 'Accept: application/json,text/event-stream' -H 'Content-Type: application/json' \
  -H "MCP-Session-Id: $SESSION" -H 'MCP-Protocol-Version: 2025-11-25' \
  --data '{"jsonrpc":"2.0","id":27,"method":"tools/call","params":{"name":"agas.project.save","arguments":{}}}'
```

#### Wave 8 curl：Host/Origin 反向、DELETE 后旧 session

Host/Origin 反向必须使用除此之外完全合法的 initialize POST 请求，隔离判因：

```bash
# evil Host：预期拒绝；Accept/Content-Type/body 全合法
curl -i 'http://127.0.0.1:54119/mcp' \
  -H 'Host: evil.test:54119' \
  -H 'Accept: application/json,text/event-stream' \
  -H 'Content-Type: application/json' \
  --data '{"jsonrpc":"2.0","id":93,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"agas-curl-e2e","version":"0.1.0"}}}'

# evil Origin：预期拒绝；Host/Accept/Content-Type/body 全合法
curl -i 'http://127.0.0.1:54119/mcp' \
  -H 'Host: 127.0.0.1:54119' \
  -H 'Origin: http://evil.test' \
  -H 'Accept: application/json,text/event-stream' \
  -H 'Content-Type: application/json' \
  --data '{"jsonrpc":"2.0","id":94,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"agas-curl-e2e","version":"0.1.0"}}}'

# DELETE 合法 session：预期终止当前 session
curl -i -X DELETE 'http://127.0.0.1:54119/mcp' \
  -H 'Host: 127.0.0.1:54119' \
  -H "MCP-Session-Id: $SESSION" \
  -H 'MCP-Protocol-Version: 2025-11-25'

# DELETE 后旧 session 再次 POST：预期 404
curl -i 'http://127.0.0.1:54119/mcp' \
  -H 'Host: 127.0.0.1:54119' \
  -H 'Accept: application/json,text/event-stream' \
  -H 'Content-Type: application/json' \
  -H "MCP-Session-Id: $SESSION" \
  -H 'MCP-Protocol-Version: 2025-11-25' \
  --data '{"jsonrpc":"2.0","id":30,"method":"tools/list","params":{}}'

# DELETE 后旧 session 再次 GET：预期 404
curl -i --max-time 2 'http://127.0.0.1:54119/mcp' \
  -H 'Host: 127.0.0.1:54119' \
  -H 'Accept: text/event-stream' \
  -H "MCP-Session-Id: $SESSION" \
  -H 'MCP-Protocol-Version: 2025-11-25'
```

关闭/重开恢复验收：

1. 执行 `agas.project.save` 后，关闭专用测试项目并重新打开。
2. 重新开启 MCP，重新 initialize。
3. 调用 `agas.project.snapshot`，确认 scene/layer/image/prompt/view 与 save 前预期一致，dirty=false/saved=true。
4. 关闭 MCP 后执行 `lsof -nP -iTCP:54119 -sTCP:LISTEN`，预期无监听；退出 Godot 后再次确认无监听。

导出验证：

```bash
if test -f export_presets.cfg; then
  echo "export_presets.cfg exists; review preset names/platforms before any export validation"
  grep -n '^\[preset\|name=\|platform=' export_presets.cfg || true
else
  echo "BLOCKED: export_presets.cfg missing; export validation not claimed"
fi
```

- 当前没有已验证 export preset 时，不写 `godot --export...` 伪命令。
- 若 `export_presets.cfg` 缺失或 preset 未验证，记录阻塞证据，不宣称导出通过。

运行期验收：

- Godot 内开关 MCP 可用。
- `src/UI/Main.cs` 在退出时 Dispose host。
- 标准 `curl` 作为目标客户端自动验收通过；MCP Inspector 仅可选人工兼容性检查。
- 导出包结果有明确证据：通过或记录阻塞原因。
- macOS symlink、Windows reparse/junction 与导出包验证按实际平台记录；无平台时写明跳过原因。

验收证据：

- 日志/截图/命令输出记录。
- 待验证项逐项更新。

---

## 5. 文件范围汇总

### 5.1 本次实际写入范围

- `docs/exec-plans/active/agas-mcp-execution-plan.md`
- `docs/exec-plans/active/agas-mcp-gate2-audit.md`

### 5.2 后续开发允许候选范围

生产代码候选：

```text
APP-AGAS-CS.csproj
src/Mcp/**
src/UI/Main.cs
src/UI/McpSettingsDialog.cs
src/UI/ProjectPicker.cs
src/UI/MainWorkspace.cs
src/Features/Canvas/CanvasWorkspace.cs
```

测试候选：

```text
tests/Core/Mcp/**
tests/Core/APP-AGAS-CS.Core.Tests.csproj
```

文档候选：

```text
docs/design-docs/agas-mcp-technical-design.md
docs/exec-plans/active/agas-mcp-execution-plan.md
```

禁止无关修改：

- 不改现有 Resource 字段含义，除非方案审计后明确需要。
- 不改现有 `.agas` 持久化格式为 JSON。
- 不新增 JS/TS/GDScript。
- 不改图片列表接口/工具为 base64 返回。
- 不修改与 MCP 无关生产代码。

---

## 6. 最终测试矩阵

开发完成后统一执行：

```bash
dotnet restore APP-AGAS-CS.sln
dotnet build APP-AGAS-CS.sln -c Debug
dotnet test tests/Core/APP-AGAS-CS.Core.Tests.csproj -c Debug --no-build
open -a "Godot_mono" --args --path .
# 默认关闭时应无监听；用户在设置 UI 开启 MCP 后再确认监听
lsof -nP -iTCP:54119 -sTCP:LISTEN || true
```

Godot 启动后，用户在 AGAS 设置中开启 MCP，再执行 Wave 8 文档中的完整 `curl initialize` → `notifications/initialized` → `tools/list` → `tools/call` 序列做 E2E；不得写不存在的测试脚本替代。Wave 8 实际 curl 必须覆盖：initialize 旧版本/缺失/type 非法；POST 缺 session/未知 session；GET 缺 session/未知 session；DELETE 后旧 session POST 与 GET；Host/Origin 隔离；8 个正式 tools 各一次。

必须覆盖：

### 6.1 生命周期/UI

- 默认关闭：`54119` 不监听，状态 `Disabled`。
- 开启 MCP：`127.0.0.1:54119/mcp` 可访问，状态 `Running`。
- 关闭 MCP：端口释放，状态 `Disabled`。
- 端口冲突：C# `TcpListener` 占用 54119 后启动失败，状态 `Error`，不自动切换。
- Kestrel 只绑定 `127.0.0.1:54119`，最大并发连接 8；请求 body 超过 1 MiB 返回 HTTP 413。
- `ProjectPicker` 和 `MainWorkspace` 均有 MCP 设置入口，入口由 `Main.cs` 协调。
- 项目关闭：server 按 Enabled 继续运行，tools 返回 `AGAS_NO_OPEN_PROJECT`。
- Godot 退出：Host Dispose，端口释放。

### 6.2 MCP wire

- 主成功链 `initialize` 使用 `2025-11-25`，响应 `result.protocolVersion=2025-11-25`。
- `initialize` 旧版本合法字符串不作为 AGAS 安全错误：SDK 支持 `2025-06-18` 时返回同版本；缺失 `params.protocolVersion`、`params.protocolVersion` 类型非法、malformed 由官方 SDK 协议错误处理。
- 初始化后请求必须携带该 session 协商版本的 `MCP-Protocol-Version`；缺失 header 的 `GET`/`DELETE` 与非 initialize `POST` 返回 `400`；header 具体版本值由官方 SDK 验证。
- `notifications/initialized` 成功。
- 主 `2025-11-25` session 后续请求携带 `MCP-Protocol-Version: 2025-11-25`；旧版本 session 后续请求携带其协商版本。
- `MCP-Session-Id` 生成；`POST/GET` 缺失 session 为 `400`；`POST/GET` 未知 session 为 `404`；合法 session DELETE 后同 session `POST/GET` 为 `404`。
- `DELETE` 缺失/未知 session 状态按 SDK 1.4.1 实际行为记录，不强行断言 `400/404`；若 unknown session `DELETE` 返回 2xx，对 executor 做幂等 cleanup 以防遗留状态，不视为越权。
- POST Accept 为 `application/json,text/event-stream`（允许 header 逗号后有空格）。
- GET Accept 为 `text/event-stream`。
- `Content-Type` 验收。
- DELETE 合法 session 终止；缺失/未知 session 状态只记录 SDK 1.4.1 实际行为；unknown session DELETE 返回 2xx 时执行幂等 executor cleanup。
- 业务错误 `isError=true`；协议错误 JSON-RPC error。
- 每 session 固定窗口 120 次/分钟，突发超限返回 `isError=true` / `AGAS_RATE_LIMITED`。
- 全局待执行 tool 队列容量 32，队列满返回 `isError=true` / `AGAS_BUSY`。
- 30 秒语义：排队/主线程开始前硬超时返回 `isError=true` / `AGAS_TIMEOUT`；同步 Godot 原子操作开始后等待完成并返回实际结果。
- stop/exit/session DELETE 等待 quiesce：未开始 work 清空/取消；已开始同步原子操作等待完成，不做无限强杀线程。

### 6.3 网络安全

- Host 合法：仅 `127.0.0.1:54119`。
- Host 非法：`localhost:54119`、`[::1]:54119`、`evil.test:54119` 均拒绝。
- Origin 合法：存在时仅 `http://127.0.0.1:54119`。
- Origin 缺失：允许，但 Host 仍严格。
- 不暴露 `AllowMissingOrigin` 用户设置。
- Host/Origin 反向测试必须使用除此之外完全合法的 initialize POST 请求，只改变 evil Host 或 evil Origin 来隔离判因。

### 6.4 工具 contract

- `tools/list`：最终严格 8 个工具。
- 8 工具均有 wire-level 完整 `inputSchema`、annotations；v1 不声明可选 `outputSchema`。
- 每个 schema 覆盖 required、enum、bounds、top-level `additionalProperties:false`。
- 4 个 mutate 工具 schema 必须用 JSON Schema 2020-12 `allOf/if/then/anyOf` 编码条件约束；raw `tools/list` schema 用正反输入验证。
- 每个工具返回 `structuredContent` + text fallback。
- `tools/call` 逐工具正反向矩阵引用设计文档 `7.12 逐工具正反向测试矩阵`，执行计划不另行维护第二份冲突矩阵。
- snapshot/list：只读，不改 dirty。
- assets.list：分页，后台纯 IO，跳过内部目录，不返回绝对路径/base64。
- scene/layer/image/prompt/view mutate：只改内存，dirty=true，saved=false。
- save：显式部分提交，返回 `savedFiles/failedFiles/pendingDirtyFiles`。

### 6.5 数据与恢复

- 保存前关闭/重开：未保存 mutate 不应被误写入磁盘。
- 保存后关闭/重开：scene/layer/image/prompt/view 全部恢复。
- 保存成功后 dirty=false/saved=true。
- 部分保存失败后对应 dirty 保留，pendingDirtyFiles 正确。
- pending deletes 仅在 project 成功保存后应用。
- pending delete 删除失败时保留 ID 于 `pendingDeletedSceneIds`，`saved=false`，逻辑路径进 `failedFiles/pendingDirtyFiles`。

### 6.6 路径与能力边界

- 路径必须拒绝矩阵全部拒绝；`a.webp` 与 `images/a.png` 在文件真实存在且不是链接/reparse-point 时必须允许。
- macOS symlink：目录链接不跟随，文件链接拒绝。
- Windows reparse/junction：目录链接不跟随，文件链接拒绝。
- project root 本身 symlink/reparse-point：MCP 项目操作返回 `AGAS_PROJECT_ROOT_UNSAFE`；UI 现有行为不在本期改。
- macOS symlink、Windows reparse/junction 与导出验证无对应平台时记录跳过原因。
- 错误不泄露绝对路径、外部目标、栈。
- hidden/locked layer：编辑拒绝。
- 并发工具调用：串行执行。
- 不提供 shell/进程/公网/任意 FS 能力。
- 导出包：先核对 `export_presets.cfg`；缺失或 preset 未验证时只记录阻塞证据，不写伪导出命令，不宣称导出通过。

---

## 7. P2 / 后续独立任务

- Bearer token / 一次性本地 token。
- 首次连接用户确认。
- 只读模式开关。
- MCP 审计日志。
- 更细粒度工具权限。
- 多项目/多窗口支持。
- 并发只读优化。
