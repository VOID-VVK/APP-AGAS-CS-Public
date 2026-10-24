# AGAS MCP 技术方案

> 文件：`docs/design-docs/agas-mcp-technical-design.md`  
> 项目：仓库根目录 `APP-AGAS-CS`
> 范围：AGAS 进程内 MCP Server 方案与开发门禁。  
> 状态：Gate 2 收尾证据更新版；MCP Core/运行时验收已按当前日志通过；导出包与 Windows 实机仍保留待验证；本次只修改文档，不修改生产代码。  
> 技术固定：Godot `4.6.1` Mono / `.NET 8` / 全 C#；禁止 JS/TS/GDScript。  
> 协议固定：MCP Streamable HTTP；AGAS 首选/完整验收协议为 `2025-11-25`；版本协商交由官方 `ModelContextProtocol.AspNetCore` `1.4.1`；固定 endpoint `http://127.0.0.1:54119/mcp`。

---

## 1. 已验证事实、待验证项、基于证据的推断

### 1.1 已验证事实

来自本项目只读调研的证据：

- `APP-AGAS-CS.csproj` 使用 `<Project Sdk="Godot.NET.Sdk/4.6.1">`、`<TargetFramework>net8.0</TargetFramework>`、`<EnableDynamicLoading>true</EnableDynamicLoading>`、`<Nullable>enable</Nullable>`。
- `project.godot` 声明 `config/features=PackedStringArray("4.6", "C#")`，主场景为 `res://src/UI/Main.tscn`。
- 现有项目源码位于 `src/**/*.cs`；本次只读调研未发现生产 JS/TS/GDScript 入口。
- 当前单元测试项目为 `tests/Core/APP-AGAS-CS.Core.Tests.csproj`，使用 xUnit 与 `Microsoft.NET.Test.Sdk`。
- `tests/Core/ExternalImageLoaderTests.cs` 已包含禁止 `.gd`、`.js`、`.ts` 和 Core JSON 持久化的源码级测试。
- 产品业务数据当前使用 Godot custom `Resource`：
  - `src/Core/Resources/AgasProjectResource.cs`
  - `src/Core/Resources/AgasSceneResource.cs`
  - `src/Core/Resources/AgasLayerResource.cs`
  - `src/Core/Resources/ImageInstanceResource.cs`
  - `src/Core/Resources/PromptFragmentResource.cs`
  - `src/Core/Resources/SceneViewStateResource.cs`
  - `src/Core/Resources/WorkspaceLayoutResource.cs`
- 当前 `.agas` 路径与保存逻辑在：
  - `src/Core/Persistence/AgasPaths.cs`
  - `src/Core/Persistence/AgasResourceRepository.cs`
  - `src/Core/Persistence/AgasProjectInitializer.cs`
- `AgasResourceRepository.Save` 采用 `ResourceSaver.Save(resource, tempPath)` 后 `File.Move(tempPath, targetPath, overwrite: true)` 的临时文件替换模式。
- `ExternalImageLoader.LoadProjectImage` 已拒绝：空路径、绝对路径、`://` URL、`data:` URI；仅允许 `.png/.jpg/.jpeg/.webp`。
- `ProjectFilesTree` 扫描项目文件时跳过 `.agas`、`.godot`、`.git`、`bin`、`obj`，只显示 `.png/.jpg/.jpeg/.webp`。
- `MainWorkspace` 当前持有打开中的项目状态：`_projectRoot`、`_project`、`_layout`、`_currentScene`、`_sceneCache`、`_dirty`。
- `MainWorkspace.SaveProject()` 当前保存 `.agas/project.tres`、`.agas/workspace-layout.tres` 和当前 scene；`SetDirty`/`MarkDirty` 管理保存状态。
- `CanvasWorkspace` 当前提供画布、图层、图片、提示词、视图状态相关操作，并在修改后发出 `CanvasChanged`。
- 当前工作区根目录未发现 `.git` 目录；因此本方案和执行计划不得把 `git diff` 作为必过门禁，必须使用 `test`、`find`、`grep`、`shasum`、`dotnet build/test`、Godot 运行日志等真实可执行证据。

来自用户给定并要求固化的已验证背景：

- 正式 MCP 采用官方 `ModelContextProtocol.AspNetCore` `1.4.1`（Apache-2.0）。
- SDK `1.4.1` 精确事实：`HttpServerTransportOptions.Stateless` 默认值为 `false`，该事实已由 v1.4.1 源码与 NuGet 二进制验证；实现仍必须通过 `builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless=false)` 显式固定，避免后续 SDK 默认值变化导致方案漂移。
- stateful Streamable HTTP 通过 `MapMcp` 映射同一 `/mcp` endpoint 的 `POST`、`GET`、`DELETE`。
- MCP 默认关闭；设置开启才启动，关闭立即停止。
- 固定端点只允许 `http://127.0.0.1:54119/mcp`；不得使用 `localhost`、IPv6、`0.0.0.0` 或自动端口切换。
- 只绑定 loopback；严格校验 `Host`/`Origin`。
- 产品业务数据继续 Godot Resource `.tres`；MCP JSON 仅用于 JSON-RPC 协议层。
- MCP 设置存放 `user://agas-mcp-settings.tres`。
- 当前项目只访问当前 UI 打开的项目。
- 拒绝绝对路径、`../`、URL、`data/base64`、`file://`。
- 支持图片扩展名：`png`、`jpg`、`jpeg`、`webp`。
- 跳过 `.agas/.godot/.git/bin/obj`。
- 不返回绝对路径，不返回 base64。
- 不提供 shell、公网、任意文件系统能力。
- 所有 Godot `Resource`、UI、Canvas 操作必须经主线程队列；HTTP 线程只做协议、安全、排队。
- tools v1 串行。
- 写操作只改内存、标记 dirty、`saved=false`；只有显式 `agas.project.save` 写 `.agas/*.tres`。
- 正式工具名最终严格 8 个：
  - `agas.project.snapshot`
  - `agas.assets.list`
  - `agas.scene.mutate`
  - `agas.layer.mutate`
  - `agas.image.mutate`
  - `agas.prompt.mutate`
  - `agas.view.set`
  - `agas.project.save`
- Wave 中间态如只实现 2 个只读工具或 7 个非保存工具，仅用于开发验证，不可交付；最终可交付状态必须严格 8 个工具，不多不少。
- Bearer token 用户未确认，不得设为 v1 硬需求；仅列为 P2 后续安全增强。
- SDK TFM 兼容已验证。
- 历史 Round3 结果：正式 .NET 8 build 0 warning / 0 error；F2 正式 .NET 8 测试 83/83 passed。该 `83/83` 仅作为历史 Round3 证据，不再作为当前结论。
- 当前收尾验证证据按用户给定日志固化：
  - `/tmp/agas_gate2_restore_build_core.log`：`dotnet build APP-AGAS-CS.sln -c Debug --no-restore`、`dotnet build APP-AGAS-CS.csproj -c Release --no-restore`、`dotnet build APP-AGAS-CS.csproj -c ExportRelease --no-restore` 均为 0 warning / 0 error；`dotnet test tests/Core/APP-AGAS-CS.Core.Tests.csproj -c Debug --no-restore` 为 101/101 passed。
  - `/tmp/agas_gate2_godot_resource.log`：在仓库根目录运行 `godot --headless --path . res://tests/Godot/McpResourceRuntimeAcceptance.tscn`，`tests/Godot/McpResourceRuntimeAcceptance.tscn` PASS。
  - `/tmp/agas_gate2_godot_http.log`：在仓库根目录运行 `godot --headless --path . res://tests/Godot/McpHttpRuntimeAcceptance.tscn`，`tests/Godot/McpHttpRuntimeAcceptance.tscn` PASS；覆盖真实 MCP HTTP E2E、Host/Origin、body 413、initialize、tools/list、8 tools/call、DELETE 后旧 session 404。
  - `/tmp/agas_gate2_godot_settings_ui.log`：在仓库根目录运行 `godot --headless --path . res://tests/Godot/McpSettingsUiAcceptance.tscn`，`tests/Godot/McpSettingsUiAcceptance.tscn` PASS；覆盖设置 UI、端口启动/停止、持久化 Enabled 状态和关闭路径。
  - 最终 `lsof -nP -iTCP:54119 -sTCP:LISTEN` 无输出：`127.0.0.1:54119` 无监听。

来自 MCP Streamable HTTP 与 Lifecycle 官方规范的已验证协议事实：

- 初始化阶段必须以 `initialize` 请求开始，成功后客户端发送 `notifications/initialized`。
- `initialize.params.protocolVersion` 用于协商协议版本；AGAS 首选/完整验收协议是 `2025-11-25`，但其他合法版本字符串的 initialize 不作为 AGAS 安全错误处理。
- initialize 的合法版本字符串交给官方 SDK `1.4.1` 协商：SDK 支持客户端请求版本时返回同版本，否则返回服务端支持版本，由客户端决定是否断开；缺失、类型非法、malformed JSON-RPC 仍由官方 SDK 协议错误处理。
- Streamable HTTP 后续请求必须带 `MCP-Protocol-Version`；请求必须使用该 session 协商得到的版本；header 具体版本值与协商一致性由官方 SDK 验证。
- Streamable HTTP `POST` 请求必须包含 `Accept: application/json, text/event-stream`；含 JSON-RPC request 时响应 `Content-Type` 为 `application/json` 或 `text/event-stream`。
- Streamable HTTP `GET` 请求必须包含 `Accept: text/event-stream`；若提供 SSE stream 则返回 `Content-Type: text/event-stream`，否则 `405 Method Not Allowed`。
- stateful server 可在 initialize 响应中返回 `MCP-Session-Id`；后续 `POST`/`GET` 请求必须携带该 header；缺失返回 `400 Bad Request`；未知、已过期或 DELETE 后的 session 返回 `404 Not Found`。`DELETE` 缺失/未知 session 的状态码按 SDK 1.4.1 实际行为记录；unknown session `DELETE` 若按 SDK 1.4.1 返回 2xx，executor cleanup 按幂等防遗留状态处理，不视为越权。
- 客户端不再需要 session 时应以 `DELETE /mcp` + `MCP-Session-Id` 终止 session；本项目 v1 支持 DELETE 终止。
- MCP 工具必须声明 `inputSchema`；`outputSchema` 在 MCP `2025-11-25` 中是可选项，本项目 MVP/v1 不声明 `outputSchema`，但仍必须返回 `structuredContent`，并在 `content` text 中返回同一 JSON fallback；业务执行错误使用 tool result `isError=true`，协议/ malformed/未知工具等使用 JSON-RPC error。

### 1.2 待验证项

- 当前根目录缺 `export_presets.cfg`；导出预设验证阻塞，不能宣称导出预设已通过。
- 实际 Godot 导出包未验证；不能宣称导出包内 `ModelContextProtocol.AspNetCore`、ASP.NET Core runtime 依赖和 AGAS MCP runtime 已通过。
- Windows junction/reparse 与 Windows 实机未验证；不能宣称 Windows 平台通过。

### 1.3 基于证据的推断

- 现有 `MainWorkspace` 已集中持有当前打开项目、当前 scene、layout、dirty/save 状态；MCP v1 应复用这一上下文，但生命周期 owner 必须上移到 app-root `src/UI/Main.cs`，避免 workspace 关闭/切换与 host 生命周期互相污染。
- 当前 UI scene 操作可能含立即落盘、副作用或 UI 事件链；`agas.scene.mutate` 不能复用现有会触盘的 UI scene handlers，应通过独立 Facade 执行纯内存 Resource 操作，并显式维护 dirty 集合。
- 当前 `AgasResourceRepository.Save` 可作为 `agas.project.save` 的底层保存路径；但 v1 需要保证所有 mutate 工具默认不调用它，只有显式 save 调用。

---

## 2. 目标与非目标

### 2.1 目标

1. 在 AGAS Godot 进程内提供一个可开关的 MCP Server。
2. 使用官方 `ModelContextProtocol.AspNetCore 1.4.1`，全 C# 实现。
3. 监听固定 `http://127.0.0.1:54119/mcp`，stateful Streamable HTTP；显式 `options.Stateless = false`。
4. 默认关闭；用户在设置中开启后启动，关闭后立即停止。
5. 只操作当前 UI 打开的 AGAS 项目；未打开项目时工具返回 `AGAS_NO_OPEN_PROJECT`。
6. 所有工具只返回项目相对逻辑路径、轻量 JSON、结构化错误；不返回绝对路径或 base64。
7. 写工具只修改内存状态并标记 dirty/saved=false；显式 `agas.project.save` 才写 `.agas/*.tres`。
8. 所有 Godot `Resource`、UI、Canvas 操作都经主线程队列执行。
9. v1 工具串行执行，避免并发改内存状态。
10. 提供可测试的安全边界：loopback、Host/Origin、路径、扩展名、symlink/reparse-point、目录跳过、无 shell/公网/任意 FS。
11. 提供 wire-level contract：8 工具逐工具 `inputSchema`、`structuredContent`、text fallback、结构化 DTO 字段表、正反向测试矩阵；v1 不声明可选 `outputSchema`。

### 2.2 非目标

v1 明确不做：

- 不做 JS/TS/GDScript/Node/Python MCP Server。
- 不做 stdio transport。
- 不做公网监听、远程访问、反向代理、自动端口切换。
- 不做任意文件系统浏览，只访问当前 UI 打开的项目。
- 不提供 shell、命令执行、进程启动、网络抓取、公网 HTTP 访问工具。
- 不返回图片 base64、`data:image`、绝对路径、`file://`。
- 不把 MCP JSON 作为产品持久化格式；产品数据仍是 Godot `.tres` Resource。
- 不引入复杂多项目并发；同一时刻只绑定当前 UI 打开的一个项目。
- 不把 Bearer token 作为 v1 硬需求；仅作为 P2 后续安全增强项。
- 不在未完成方案审计 P0/P1 清零前进入生产代码开发。

---

## 3. 架构与文件范围

### 3.1 组件划分

```text
AGAS Godot Main Thread
├─ src/UI/Main.cs
│  ├─ app-root 生命周期 owner：创建/持有/Dispose AgasMcpLifecycleService
│  ├─ 协调 ProjectPicker / MainWorkspace 的 MCP 设置入口
│  └─ Godot 退出时释放 MCP host
├─ src/UI/McpSettingsDialog.cs
│  ├─ 展示 Enabled、Endpoint、Status、LastError
│  └─ 用户切换 Enabled；不暴露 AllowMissingOrigin
├─ src/UI/ProjectPicker.cs
│  └─ 提供 MCP 设置入口，由 Main.cs 协调打开 dialog
├─ src/UI/MainWorkspace.cs
│  ├─ 只注册/注销当前项目上下文给 MCP context provider
│  ├─ 提供 MCP 设置入口，由 Main.cs 协调打开 dialog
│  └─ 不拥有 MCP host 生命周期
├─ src/Mcp/AgasMcpSettingsResource.cs
├─ src/Mcp/AgasMcpSettingsStore.cs
├─ src/Mcp/AgasMcpLifecycleService.cs
├─ src/Mcp/AgasMcpHostFactory.cs
├─ src/Mcp/AgasMcpEndpointSecurity.cs
├─ src/Mcp/AgasMcpMainThreadDispatcher.cs
├─ src/Mcp/AgasMcpSerialToolExecutor.cs
├─ src/Mcp/AgasMcpProjectContextProvider.cs
└─ src/Mcp/AgasMcpProjectFacade.cs

HTTP / ASP.NET Core Thread Pool
├─ Kestrel loopback listener 127.0.0.1:54119 only
├─ Host/Origin guard middleware
├─ MCP Streamable HTTP endpoint /mcp
├─ Tool registry: final 8 agas.* tools only
└─ Serial tool executor：v1 单工具串行 + 超时 + 取消
```

### 3.2 依赖方向

```text
MCP HTTP handler
  -> Host/Origin guard / MCP SDK / JSON schema 校验 / 串行排队
  -> AgasMcpMainThreadDispatcher.InvokeAsync(...)
  -> AgasMcpProjectFacade
  -> AgasMcpProjectContextProvider 当前项目快照
  -> MainWorkspace / CanvasWorkspace / Resources 的受控入口
  -> 返回轻量 DTO
```

约束：

- HTTP 线程不得直接读写 Godot `Resource`、`Node`、`CanvasItem`、UI 控件。
- 主线程队列是 Godot 对象访问的唯一入口。
- `src/UI/Main.cs` 是 MCP host 生命周期唯一 owner。
- `MainWorkspace` 只注册/注销项目上下文；项目关闭时 server 按 `Enabled` 继续运行，但所有需要项目的 tools 返回 `AGAS_NO_OPEN_PROJECT`。
- AGAS 退出时 `Main.cs` 必须 Stop/Dispose host 并释放端口。
- 业务 DTO 与工具 contract 放在独立 MCP 命名空间下，不反向污染 Core Resource。
- MCP Server 生命周期由设置驱动，不写入 `project.godot` Autoload。

### 3.3 后续开发建议文件范围

```text
src/Mcp/
  AgasMcpSettingsResource.cs
  AgasMcpSettingsStore.cs
  AgasMcpRuntimeStatus.cs
  AgasMcpLifecycleService.cs
  AgasMcpHostFactory.cs
  AgasMcpEndpointSecurity.cs
  AgasMcpMainThreadDispatcher.cs
  AgasMcpSerialToolExecutor.cs
  AgasMcpProjectContextProvider.cs
  AgasMcpProjectFacade.cs
  AgasMcpPathValidator.cs
  AgasMcpRepositoryErrorMapper.cs
  Contracts/
    AgasMcpDtos.cs
    AgasMcpToolInputs.cs
    AgasMcpToolOutputs.cs
    AgasMcpErrorCodes.cs
  Tools/
    AgasProjectTools.cs
    AgasAssetsTools.cs
    AgasSceneTools.cs
    AgasLayerTools.cs
    AgasImageTools.cs
    AgasPromptTools.cs
    AgasViewTools.cs

src/UI/
  Main.cs                  // 生命周期 owner 与入口协调
  McpSettingsDialog.cs      // 新增 UI
  ProjectPicker.cs          // 只加入口按钮/菜单与事件
  MainWorkspace.cs          // 只注册/注销项目上下文与入口事件
```

当前实际测试清单：

```text
tests/Core/Mcp/
  AgasMcpDirtyStateTests.cs
  AgasMcpHostTests.cs
  AgasMcpMutateSchemaPrecisionTests.cs
  AgasMcpPathValidatorTests.cs
  AgasMcpSerialToolExecutorTests.cs
  AgasMcpSettingsAndLifecycleTests.cs
  AgasMcpToolContractTests.cs
  AgasMcpToolRuntimeTests.cs
```

### 3.4 SRP 硬约束

- 每个 MCP 类只承担一个职责；超过约 200 行必须在 CR 中解释，否则拆分。
- `AgasMcpHostFactory` 只创建/配置 host，不读写项目。
- `AgasMcpEndpointSecurity` 只做 Host/Origin/header 安全校验。
- `AgasMcpLifecycleService` 只处理 settings -> start/stop/status，不实现工具业务。
- `AgasMcpProjectFacade` 只做主线程上的 AGAS 业务编排，不处理 HTTP header。
- Tool 类只声明 schema、annotations、调用 facade，不直接保存文件、不直接访问 Godot Node。
- Path validator 只做路径规范化/拒绝/逻辑路径输出，不打开或解析业务 Resource。

---

## 4. 设置、状态与生命周期

### 4.1 设置 Resource

`user://agas-mcp-settings.tres` 仅保存最小本地设置：

```text
AgasMcpSettingsResource
- ResourceVersion: int = 1
- Enabled: bool = false
- BindAddress: string = "127.0.0.1"     // v1 固定，不给用户任意改公网地址
- Port: int = 54119                     // v1 固定
- EndpointPath: string = "/mcp"         // v1 固定
```

禁止字段：

- 不提供 `AllowMissingOrigin` 用户设置；缺失 `Origin` 的允许策略是内置兼容策略，不让用户降低安全边界。
- 不提供 bind address/port/path 的任意 UI 编辑能力。
- Bearer token 不进入 v1 设置。

### 4.2 UI 状态机

`AgasMcpRuntimeStatus` 必须覆盖：

```text
Disabled   // Enabled=false，host 未运行
Starting   // Enabled=true，正在启动 host
Running    // Enabled=true，host 已监听 127.0.0.1:54119/mcp
Stopping   // Enabled=false 或退出，正在停止 host
Stopped    // Enabled=true 但 host 已被显式停止；仅用于异常恢复/测试态
Error      // Enabled=true 但 host 启动失败或运行异常
```

状态一致性要求：

- 默认 `Enabled=false`，UI 显示 `Disabled`。
- 用户开启后先持久化 settings，再进入 `Starting`，成功后 `Running`。
- 端口冲突：`Enabled=true` 且实际 host 未运行，状态必须为 `Error`，`LastError` 显示“端口 54119 已被占用”，不得自动改端口；再次关闭应写回 `Enabled=false` 并进入 `Disabled`。
- 用户关闭：先写 settings `Enabled=false`，进入 `Stopping`，释放 host 后 `Disabled`。
- 项目关闭不改变 `Enabled`；server 若处于 `Running` 继续运行，工具返回 `AGAS_NO_OPEN_PROJECT`。
- AGAS 退出必须不依赖 UI 状态，直接 Stop/Dispose host。

### 4.3 MCP Host 启动配置

实现必须显式表达 SDK 事实和意图：

```csharp
builder.Services
    .AddMcpServer()
    .WithHttpTransport(options => options.Stateless=false);

app.UseMiddleware<AgasMcpProtocolHeaderMiddleware>();
app.MapMcp("/mcp");
```

验收点：

- 禁止通过 `MapMcp` 的 options lambda 配置 Stateless；SDK 1.4.1 的 stateful 配置必须放在 `WithHttpTransport`。
- `AgasMcpProtocolHeaderMiddleware` 必须在 `MapMcp` 前注册，只做 Streamable HTTP 后续请求的 `MCP-Protocol-Version` header 存在性门禁；不校验具体版本值，不处理 MCP lifecycle，不访问 SDK 内部 session。
- `MapMcp` 同一 endpoint 映射 `POST`、`GET`、`DELETE`。
- Kestrel 只监听 `127.0.0.1:54119`。
- Kestrel `MaxConcurrentConnections=8`；超过连接并发上限时按 Kestrel 行为拒绝/排队，不新增公网监听或备用端口。
- Kestrel 请求 body 最大 `1 MiB`；超限在进入 MCP/JSON-RPC 协议前返回 HTTP `413 Payload Too Large`，不包装为 tool result。
- 生成的对外配置 URL 只显示 `http://127.0.0.1:54119/mcp`。
- 不出现 `localhost`、`[::1]`、`0.0.0.0`。

---

## 5. MCP wire protocol 验收

### 5.1 初始化与版本协商

- AGAS 首选/完整验收协议是 `2025-11-25`；主成功链、8 工具验收和最终 E2E 使用 `2025-11-25`。
- AGAS 不把其他合法 `initialize.params.protocolVersion` 字符串作为安全错误；合法版本字符串必须进入官方 SDK `1.4.1` lifecycle，由 SDK 协商。
- 官方 SDK 协商规则作为验收口径：SDK 支持客户端请求版本时返回同版本；否则返回服务端支持版本，由客户端决定是否断开。
- `initialize.params.protocolVersion` 缺失、类型非法或 JSON-RPC malformed 不由 AGAS 自定义安全错误处理，必须交给官方 SDK 协议错误处理。
- `initialize` request：
  - JSON-RPC method 必须为 `initialize`。
  - `params.protocolVersion="2025-11-25"` 时，主验收链响应 `result.protocolVersion` 必须为 `2025-11-25`。
  - `params.protocolVersion="2025-06-18"` 等 SDK 支持的旧版本时，预期 SDK 按支持集合返回同版本；若 SDK 不支持某合法版本，记录 SDK 返回的服务端支持版本，并由客户端断开策略处理。
- initialize response header：stateful 模式必须生成 `MCP-Session-Id`，值为 cryptographically strong、visible ASCII、不可预测字符串。
- initialize response capabilities：必须声明 `tools` capability；不声明未实现的 resources/prompts。
- initialize 成功后，客户端发送 `notifications/initialized`；服务端接受后进入正常操作。
- 在 initialize 完成前，除 `ping` 外不得执行 tools。

### 5.2 后续 HTTP header 与 session

- 新增 `AgasMcpProtocolHeaderMiddleware` 并在 `MapMcp` 前执行，只实现 Streamable HTTP 后续 `MCP-Protocol-Version` header 存在性门禁：
  - `GET`/`DELETE` 必须携带 `MCP-Protocol-Version`；缺失返回 HTTP `400 Bad Request`。
  - `POST` 缺失 `MCP-Protocol-Version` 时，仅 JSON-RPC `method=initialize` 允许进入 SDK；其余 POST 返回 HTTP `400 Bad Request`。为判定 initialize，可安全 buffer/read/rewind，读取后必须恢复 request body 供 SDK 继续处理。
  - header 具体版本值不由 AGAS middleware 校验；版本值与 session 协商版本是否一致由官方 SDK 验证。
  - middleware 不处理 initialize/initialized/session lifecycle，不访问 SDK 内部 session。
- 后续所有 `POST`/`GET`/`DELETE` 请求必须携带：
  - `MCP-Protocol-Version: <该 session initialize 协商得到的版本>`；主 `2025-11-25` session 使用 `2025-11-25`，旧版本 session 使用其协商版本。
  - `MCP-Session-Id: <initialize returned session id>`
- `POST`/`GET` 缺失 `MCP-Session-Id`：按 SDK 1.4.1 真实行为验收为 HTTP `400 Bad Request`。
- `POST`/`GET` 未知 session、过期 session、已 DELETE session：按 SDK 1.4.1 真实行为验收为 HTTP `404 Not Found`。
- `DELETE` 合法 session：终止 session；DELETE 响应状态按 SDK 1.4.1 实际行为记录。
- `DELETE` 缺失/未知 session：不强行断言 `400/404`，只记录 SDK 1.4.1 实际响应状态；不得通过自定义访问 SDK 内部 session 来实现断言。若 unknown session `DELETE` 按 SDK 1.4.1 返回 2xx，executor cleanup 必须按幂等 cleanup 执行，用于防遗留状态，不是越权。
- DELETE 真正强制验收：合法 session DELETE 后，继续用旧 `MCP-Session-Id` 发 `POST` 或 `GET`，必须返回 HTTP `404 Not Found`。
- HTTP header 名大小写不敏感；测试名使用规范写法 `MCP-Session-Id`。若 SDK API 类型名使用 `McpSessionId` 或文档中出现 `Mcp-Session-Id`，实现与测试仍按 HTTP header 大小写不敏感处理。

### 5.3 POST / GET / DELETE 语义

| Method | 必要 header | 成功 Content-Type / Status | 失败验收 |
|---|---|---|---|
| POST `/mcp` initialize | `Accept: application/json,text/event-stream`（允许 header 逗号后有空格），`Content-Type: application/json` | `200`，`application/json` 或 `text/event-stream`，带 `MCP-Session-Id` | Accept/Content-Type 错误返回 `4xx` |
| POST `/mcp` notification/response | `Accept: application/json,text/event-stream`（允许 header 逗号后有空格），`Content-Type: application/json`，后续请求带 session/version | 接受后 `202 Accepted` 且无 body | 缺 session `400`，未知 session `404` |
| POST `/mcp` request | 同上 | `Content-Type: application/json` 或 `text/event-stream`，返回 JSON-RPC response | malformed/未知 method 走 JSON-RPC error 或 HTTP `4xx`，按 SDK 行为固定测试 |
| GET `/mcp` | `Accept: text/event-stream`，session/version | `Content-Type: text/event-stream` 或 `405 Method Not Allowed` | 缺 session `400`，未知 session `404`，Accept 错误 `4xx` |
| DELETE `/mcp` | session/version | 合法 session 终止；DELETE 自身状态码按 SDK 1.4.1 实际行为记录；之后同 session `POST`/`GET` 必须 `404`；unknown session 若 SDK 返回 2xx，则 executor 幂等 cleanup 防遗留状态 | 缺失/未知 session 不强行断言 `400/404`，记录 SDK 实际行为；禁止访问 SDK 内部 session 做自定义判断；幂等 cleanup 不是越权 |

### 5.4 Host / Origin 安全

- `Host` 必须严格等于 `127.0.0.1:54119`；不允许 `localhost:54119`、`[::1]:54119`、`0.0.0.0:54119`、任意外部 host。
- endpoint、UI 文案、客户端配置示例只使用 `http://127.0.0.1:54119/mcp`。
- `Origin` 存在时必须严格等于 `http://127.0.0.1:54119`。
- `Origin` 缺失时允许通过，以兼容非浏览器 MCP 客户端；但 Host 仍必须严格校验。
- 不把 `AllowMissingOrigin` 暴露成用户设置。
- 不引入 Bearer token 或一次性 token 作为 v1 硬需求；Bearer 属于 P2 安全增强。

### 5.5 正式安全限流门禁（v1 定稿）

固定约束：

| 项 | v1 固定值 | 失败行为 |
|---|---:|---|
| Kestrel 监听地址 | `127.0.0.1:54119` only | 不允许 `localhost`、IPv6、`0.0.0.0` 或自动换端口 |
| Kestrel 最大并发连接 | `8` | 按 Kestrel 连接层行为拒绝/排队；不得绕过 Host/Origin guard |
| 请求 body 最大值 | `1 MiB` | 协议前 HTTP `413 Payload Too Large` |
| MCP tool rate limit | 每个 `MCP-Session-Id` 固定窗口最多 `120` 次/分钟 | tool 执行错误：`isError=true`、`AGAS_RATE_LIMITED` |
| 全局待执行 tool 队列容量 | `32` | tool 执行错误：`isError=true`、`AGAS_BUSY` |
| 30 秒超时语义 | 排队/主线程开始前硬超时；同步 Godot 原子操作开始后等待完成 | 未开始时 tool 执行错误：`isError=true`、`AGAS_TIMEOUT`；已开始时返回原子操作实际结果 |

执行语义：

- rate limit 只统计 `tools/call`，不统计 `initialize`、`notifications/initialized`、`tools/list`、GET SSE、DELETE。
- rate limit key 为 stateful session id；窗口为固定窗口，不要求滑动窗口。
- 队列容量是全局待执行 tool 队列，不按 session 分桶；达到容量后新 tool call 不入队。
- 30 秒语义修正：排队/主线程开始前为硬超时；一旦同步 Godot 原子操作已开始，为防止“客户端报超时但副作用继续”，必须等待原子操作完成并返回实际结果。
- tool 已入队但尚未执行时，若 session DELETE、用户关闭 MCP、AGAS 退出或 host stop，必须取消等待并从队列清除。
- stop/exit/session DELETE 必须进入 quiesce：清空/取消未开始队列项；已开始的同步 Godot 原子操作等待完成并返回真实结果。这是安全一致性约束，不是无限强杀线程。
- 突发、队列满、超时、关闭清队列必须有 C# integration tests 覆盖。

---

## 6. 数据模型、dirty/saved 与保存语义

### 6.1 产品持久化数据

不新增 MCP 专用业务持久化 JSON。继续使用：

```text
<project-root>/.agas/project.tres
<project-root>/.agas/workspace-layout.tres
<project-root>/.agas/scenes/<scene-id>.tres
user://agas-mcp-settings.tres
```

### 6.2 saved / dirty 精确定义

- `dirty=false`：当前内存中的 project/layout/scenes/view state 与最近一次成功保存到 `.agas` 的状态一致；或当前无打开项目。
- `dirty=true`：至少存在一个 dirty 集合非空：`dirtyProject`、`dirtyLayout`、`dirtySceneIds`、`pendingDeletedSceneIds`。
- `saved=true`：本次工具调用结束后不存在待保存变更；通常只读工具在原本干净时为 true，`agas.project.save` 全部成功后为 true。
- `saved=false`：本次工具调用后仍存在待保存变更，或保存部分失败。
- 只读工具不得改变 dirty/saved。
- mutate 工具必须设置对应 dirty 集合，返回 `dirty=true`、`saved=false`。
- 删除 scene 不立即删磁盘文件；只加入 `pendingDeletedSceneIds`，由 `agas.project.save` 在 project 保存成功后应用。

### 6.3 dirty 集合

Facade 必须维护：

```text
dirtyProject: bool
dirtyLayout: bool
dirtySceneIds: set<string>
pendingDeletedSceneIds: set<string>
```

变更映射：

- scene add/rename/delete/setActive：`dirtyProject=true`；涉及具体 scene 内容时加入 `dirtySceneIds`；delete 加入 `pendingDeletedSceneIds`。
- layer/image/prompt mutate：对应 `sceneId` 加入 `dirtySceneIds`。
- view.set：`ViewState` 属于 `AgasSceneResource`，对应 `sceneId` view state 改变时必须加入 `dirtySceneIds`；不得把 view.set 的 dirty 归入 layout。
- 当前项目关闭：清空 context；server 不停；工具返回 `AGAS_NO_OPEN_PROJECT`。

### 6.4 scene mutate 独立 Facade

`agas.scene.mutate` 绝不能复用现有会触盘的 UI scene handlers。要求：

- 通过 `AgasMcpProjectFacade` 在主线程直接操作内存 Resource。
- 不调用 `SaveProject()`、`AgasResourceRepository.Save`、任何 UI 菜单 handler、任何会写文件的 command。
- 删除 scene 只更新内存 project index、active scene 与 `pendingDeletedSceneIds`。
- 删除 active scene 后按 UI 规则选择保留 scene，并标记 `dirtyProject=true`、必要时 `dirtyLayout=true`。
- 至少保留一个 scene。

### 6.5 多文件 save：安全有序的显式部分提交

`agas.project.save` 采用最小语义，不宣称全事务：

1. 保存 `dirtySceneIds` 中仍存在的 scene 到 `.agas/scenes/<scene-id>.tres`。
2. 保存 `.agas/project.tres`。
3. 保存 `.agas/workspace-layout.tres`。
4. 仅当 `.agas/project.tres` 成功保存后，才删除 `pendingDeletedSceneIds` 对应的旧 scene 文件。
5. 每个成功文件从对应 dirty 集合移除；失败文件保留 dirty。
6. 返回：
   - `savedFiles`: 本次成功保存/删除的相对逻辑路径。
   - `failedFiles`: 本次失败的相对逻辑路径与错误码。
   - `pendingDirtyFiles`: 调用结束后仍 dirty 的相对逻辑路径。
7. 所有路径必须是逻辑相对路径，例如 `.agas/project.tres`、`.agas/workspace-layout.tres`、`.agas/scenes/scene-001.tres`；不得返回绝对路径。
8. Repository 异常必须通过 `AgasMcpRepositoryErrorMapper` 包装为安全错误，不返回真实外部路径、项目根、栈、临时文件名。

失败语义：

- dirty scene 保存失败：保留该 scene dirty，继续尝试其他 dirty scene；是否继续保存 project/layout 由实现固定并测试，但不得清掉失败 dirty。
- project 保存失败：保留 `dirtyProject=true`；不得应用 `pendingDeletedSceneIds` 的磁盘删除。
- pending delete 删除旧 scene 文件失败：必须保留该 sceneId 于 `pendingDeletedSceneIds`，响应 `saved=false`，逻辑路径进入 `failedFiles` 与 `pendingDirtyFiles`，不得误报保存完成。
- layout 保存失败：保留 `dirtyLayout=true`。
- 部分成功不回滚；响应必须清楚列出成功、失败、仍待保存项。

---

## 7. 工具 contract（最终严格 8 个）

### 7.1 通用 tool result envelope

MCP `2025-11-25` 中 `outputSchema` 为可选项。为避免 MVP 阶段出现虚假或不完整声明，AGAS v1 工具不声明 `outputSchema`；所有工具仍必须返回：

```json
{
  "content": [
    { "type": "text", "text": "{...serialized structuredContent...}" }
  ],
  "structuredContent": {
    "ok": true,
    "saved": false,
    "dirty": true,
    "data": {},
    "warnings": []
  },
  "isError": false
}
```

业务错误示例：

```json
{
  "content": [
    { "type": "text", "text": "{...serialized structuredContent...}" }
  ],
  "structuredContent": {
    "ok": false,
    "saved": false,
    "dirty": true,
    "error": {
      "code": "AGAS_PATH_REJECTED",
      "message": "ImagePath must be a project-relative png/jpg/jpeg/webp path.",
      "suggestion": "Use agas.assets.list first and pass the returned relativePath."
    },
    "warnings": []
  },
  "isError": true
}
```

规则：

- 工具业务错误：`isError=true` + `structuredContent.ok=false`。
- 协议错误：JSON-RPC error，用于 unknown tool、malformed JSON-RPC、CallToolRequest schema 不合法、缺 header/session 等协议层问题。
- 每个 `structuredContent` 必须符合本节定义的结构化 DTO 合同与逐工具字段表。
- 每个 `content[0].text` 是 `structuredContent` 的 JSON 字符串 fallback。
- 不返回 MCP image content，不返回 base64。

### 7.2 JSON Schema 公共片段

除特别说明外，所有 `inputSchema` 均：

```json
{ "type": "object", "additionalProperties": false }
```

公共输出字段：

```json
{
  "type": "object",
  "additionalProperties": false,
  "properties": {
    "ok": { "type": "boolean" },
    "saved": { "type": "boolean" },
    "dirty": { "type": "boolean" },
    "data": { "type": "object" },
    "warnings": { "type": "array", "items": { "type": "string", "maxLength": 300 } },
    "error": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "code": { "type": "string" },
        "message": { "type": "string", "maxLength": 500 },
        "suggestion": { "type": "string", "maxLength": 500 }
      },
      "required": ["code", "message", "suggestion"]
    }
  },
  "required": ["ok", "saved", "dirty", "warnings"]
}
```

### 7.3 `agas.project.snapshot`

- annotations：`readOnlyHint=true`，`destructiveHint=false`，`idempotentHint=true`，`openWorldHint=false`。
- `inputSchema`：

```json
{
  "type": "object",
  "additionalProperties": false,
  "properties": {
    "includeScenes": { "type": "boolean", "default": true },
    "includeLayers": { "type": "boolean", "default": true },
    "includeImages": { "type": "boolean", "default": false },
    "includePrompts": { "type": "boolean", "default": false }
  },
  "required": []
}
```

- `structuredContent.data`：project name、activeSceneId、grid、scene index；按 include flags 返回 layers/images/prompts 摘要；包含 `dirtyProject`、`dirtyLayout`、`dirtySceneIds`、`pendingDeletedSceneIds`。
- 正向测试：打开项目后返回轻量快照；图片字段仅项目相对逻辑路径，不含 URL/绝对路径/base64；不改变 dirty。
- 反向测试：未打开项目返回 `AGAS_NO_OPEN_PROJECT`，`isError=true`。

### 7.4 `agas.assets.list`

- annotations：`readOnlyHint=true`，`destructiveHint=false`，`idempotentHint=true`，`openWorldHint=false`。
- `inputSchema`：

```json
{
  "type": "object",
  "additionalProperties": false,
  "properties": {
    "query": { "type": "string", "maxLength": 200, "default": "" },
    "extensions": {
      "type": "array",
      "items": { "type": "string", "enum": ["png", "jpg", "jpeg", "webp"] },
      "uniqueItems": true,
      "minItems": 0,
      "maxItems": 4,
      "default": ["png", "jpg", "jpeg", "webp"]
    },
    "limit": { "type": "integer", "minimum": 1, "maximum": 200, "default": 50 },
    "offset": { "type": "integer", "minimum": 0, "maximum": 1000000, "default": 0 }
  },
  "required": []
}
```

- `structuredContent.data`：`items[] { relativePath, extension, sizeBytes, modifiedUnixTime }`、`count`、`totalCount`、`hasMore`、`nextOffset`。
- 后台纯 IO 策略：主线程只读取当前 project root 快照和允许目录配置；目录枚举、文件 stat、分页过滤在后台纯 .NET IO 执行，不能触碰 Godot Resource/UI。
- 正向测试：分页正确，跳过内部目录，返回相对路径。
- 反向测试：非法 extension、limit 越界、offset 越界、未打开项目、输出包含绝对路径/base64 均失败。

### 7.5 `agas.scene.mutate`

- annotations：`readOnlyHint=false`，`destructiveHint=true`，`idempotentHint=false`，`openWorldHint=false`。
- `inputSchema`：

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "additionalProperties": false,
  "properties": {
    "op": { "type": "string", "enum": ["add", "rename", "delete", "setActive"] },
    "sceneId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_.-]+$" },
    "name": { "type": "string", "minLength": 1, "maxLength": 120 }
  },
  "required": ["op"],
  "allOf": [
    {
      "if": { "properties": { "op": { "const": "add" } }, "required": ["op"] },
      "then": { "required": ["name"] }
    },
    {
      "if": { "properties": { "op": { "const": "rename" } }, "required": ["op"] },
      "then": { "required": ["sceneId", "name"] }
    },
    {
      "if": { "properties": { "op": { "const": "delete" } }, "required": ["op"] },
      "then": { "required": ["sceneId"] }
    },
    {
      "if": { "properties": { "op": { "const": "setActive" } }, "required": ["op"] },
      "then": { "required": ["sceneId"] }
    }
  ]
}
```

- 条件要求已编码进 `inputSchema`：`add=>name`；`rename=>sceneId+name`；`delete/setActive=>sceneId`。内部二次校验仍保留，用于业务存在性、最后 scene 等语义错误。
- `structuredContent.data`：scene index、activeSceneId、dirty sets。
- 正向测试：add/rename/delete/setActive 只改内存；删除 active scene 后切到保留 scene；dirtyProject 更新。
- 反向测试：删除最后一个 scene、未知 sceneId、缺必需字段、复用触盘 UI handler、save 前磁盘变化均失败。

### 7.6 `agas.layer.mutate`

- annotations：`readOnlyHint=false`，`destructiveHint=true`，`idempotentHint=false`，`openWorldHint=false`。
- `inputSchema`：

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "additionalProperties": false,
  "properties": {
    "op": { "type": "string", "enum": ["add", "rename", "delete", "move", "setVisible", "setLocked", "setCurrent"] },
    "sceneId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_.-]+$" },
    "layerId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_.-]+$" },
    "name": { "type": "string", "minLength": 1, "maxLength": 120 },
    "direction": { "type": "integer", "enum": [-1, 1] },
    "visible": { "type": "boolean" },
    "locked": { "type": "boolean" }
  },
  "required": ["op", "sceneId"],
  "allOf": [
    {
      "if": { "properties": { "op": { "const": "add" } }, "required": ["op"] },
      "then": { "required": ["name"] }
    },
    {
      "if": { "properties": { "op": { "const": "rename" } }, "required": ["op"] },
      "then": { "required": ["layerId", "name"] }
    },
    {
      "if": { "properties": { "op": { "const": "delete" } }, "required": ["op"] },
      "then": { "required": ["layerId"] }
    },
    {
      "if": { "properties": { "op": { "const": "setCurrent" } }, "required": ["op"] },
      "then": { "required": ["layerId"] }
    },
    {
      "if": { "properties": { "op": { "const": "move" } }, "required": ["op"] },
      "then": { "required": ["layerId", "direction"] }
    },
    {
      "if": { "properties": { "op": { "const": "setVisible" } }, "required": ["op"] },
      "then": { "required": ["layerId", "visible"] }
    },
    {
      "if": { "properties": { "op": { "const": "setLocked" } }, "required": ["op"] },
      "then": { "required": ["layerId", "locked"] }
    }
  ]
}
```

- 条件要求已编码进 `inputSchema`：`add=>name`；`rename=>layerId+name`；`delete/setCurrent=>layerId`；`move=>layerId+direction`；`setVisible=>layerId+visible`；`setLocked=>layerId+locked`。内部二次校验仍保留，用于 scene/layer 存在性、最后 layer、hidden/locked 等语义错误。
- `structuredContent.data`：sceneId、layers、currentLayerId、dirty sets。
- 正向测试：增删改移动图层、设置 visible/locked/current，dirtySceneIds 包含 scene。
- 反向测试：删除最后一个 layer、未知 scene/layer、locked 规则不一致、save 前磁盘变化均失败。

### 7.7 `agas.image.mutate`

- annotations：`readOnlyHint=false`，`destructiveHint=true`，`idempotentHint=false`，`openWorldHint=false`。
- `inputSchema`：

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "additionalProperties": false,
  "properties": {
    "op": { "type": "string", "enum": ["add", "delete", "move", "update"] },
    "sceneId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_.-]+$" },
    "layerId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_.-]+$" },
    "imageId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_.-]+$" },
    "imagePath": { "type": "string", "minLength": 1, "maxLength": 500 },
    "x": { "type": "number", "minimum": -100000, "maximum": 100000 },
    "y": { "type": "number", "minimum": -100000, "maximum": 100000 },
    "scaleX": { "type": "number", "minimum": 0.01, "maximum": 100 },
    "scaleY": { "type": "number", "minimum": 0.01, "maximum": 100 },
    "rotationDegrees": { "type": "number", "minimum": -3600, "maximum": 3600 },
    "opacity": { "type": "number", "minimum": 0, "maximum": 1 }
  },
  "required": ["op", "sceneId", "layerId"],
  "allOf": [
    {
      "if": { "properties": { "op": { "const": "add" } }, "required": ["op"] },
      "then": { "required": ["imagePath"] }
    },
    {
      "if": { "properties": { "op": { "const": "delete" } }, "required": ["op"] },
      "then": { "required": ["imageId"] }
    },
    {
      "if": { "properties": { "op": { "const": "move" } }, "required": ["op"] },
      "then": {
        "required": ["imageId"],
        "anyOf": [
          { "required": ["x"] },
          { "required": ["y"] }
        ]
      }
    },
    {
      "if": { "properties": { "op": { "const": "update" } }, "required": ["op"] },
      "then": {
        "required": ["imageId"],
        "anyOf": [
          { "required": ["imagePath"] },
          { "required": ["x"] },
          { "required": ["y"] },
          { "required": ["scaleX"] },
          { "required": ["scaleY"] },
          { "required": ["rotationDegrees"] },
          { "required": ["opacity"] }
        ]
      }
    }
  ]
}
```

- 条件要求已编码进 `inputSchema`：`add=>imagePath`；`delete=>imageId`；`move=>imageId` 且 `x/y` 至少一个；`update=>imageId` 且更新字段 `imagePath/x/y/scaleX/scaleY/rotationDegrees/opacity` 至少一个。内部二次校验仍保留，用于路径、图片存在性、hidden/locked 等语义错误。
- `structuredContent.data`：image instance、layer 摘要、dirty sets。
- 路径约束：`imagePath` 通过统一 validator，且文件链接拒绝、目录链接不跟随。
- 正向测试：合法相对图片可 add/update/move/delete，hidden=false 且 locked=false 时成功。
- 反向测试：非法路径、URL、base64、绝对路径、symlink 文件、reparse-point 文件、hidden/locked layer、save 前磁盘变化均失败。

### 7.8 `agas.prompt.mutate`

- annotations：`readOnlyHint=false`，`destructiveHint=true`，`idempotentHint=false`，`openWorldHint=false`。
- `inputSchema`：

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "additionalProperties": false,
  "properties": {
    "op": { "type": "string", "enum": ["add", "delete", "update", "move", "setEnabled"] },
    "sceneId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_.-]+$" },
    "layerId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_.-]+$" },
    "fragmentId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_.-]+$" },
    "text": { "type": "string", "minLength": 0, "maxLength": 4000 },
    "enabled": { "type": "boolean" },
    "direction": { "type": "integer", "enum": [-1, 1] }
  },
  "required": ["op", "sceneId", "layerId"],
  "allOf": [
    {
      "if": { "properties": { "op": { "const": "add" } }, "required": ["op"] },
      "then": { "required": ["text"] }
    },
    {
      "if": { "properties": { "op": { "const": "delete" } }, "required": ["op"] },
      "then": { "required": ["fragmentId"] }
    },
    {
      "if": { "properties": { "op": { "const": "update" } }, "required": ["op"] },
      "then": { "required": ["fragmentId", "text"] }
    },
    {
      "if": { "properties": { "op": { "const": "move" } }, "required": ["op"] },
      "then": { "required": ["fragmentId", "direction"] }
    },
    {
      "if": { "properties": { "op": { "const": "setEnabled" } }, "required": ["op"] },
      "then": { "required": ["fragmentId", "enabled"] }
    }
  ]
}
```

- 条件要求已编码进 `inputSchema`：`add=>text`；`delete=>fragmentId`；`update=>fragmentId+text`；`move=>fragmentId+direction`；`setEnabled=>fragmentId+enabled`。内部二次校验仍保留，用于 fragment 存在性、hidden/locked 等语义错误。
- `structuredContent.data`：fragments、mergedPreview、dirty sets。
- 正向测试：增删改排序启停后 dirtySceneIds 包含 scene。
- 反向测试：未知 fragment、文本超长、hidden/locked layer、save 前磁盘变化均失败。

### 7.9 `agas.view.set`

- annotations：`readOnlyHint=false`，`destructiveHint=false`，`idempotentHint=true`，`openWorldHint=false`。
- `inputSchema`：

```json
{
  "type": "object",
  "additionalProperties": false,
  "properties": {
    "sceneId": { "type": "string", "minLength": 1, "maxLength": 120, "pattern": "^[A-Za-z0-9_.-]+$" },
    "cameraX": { "type": "number", "minimum": -100000, "maximum": 100000 },
    "cameraY": { "type": "number", "minimum": -100000, "maximum": 100000 },
    "zoom": { "type": "number", "minimum": 0.05, "maximum": 20 }
  },
  "required": ["sceneId", "cameraX", "cameraY", "zoom"]
}
```

- `structuredContent.data`：sceneId、cameraX、cameraY、zoom、dirty sets。
- 正向测试：设置视图状态，按 UI min/max clamp 或 schema bounds 生效，`dirtySceneIds` 包含 scene。
- 反向测试：未知 scene、zoom 越界、NaN/Infinity、save 前磁盘变化均失败。

### 7.10 `agas.project.save`

- annotations：`readOnlyHint=false`，`destructiveHint=false`，`idempotentHint=true`，`openWorldHint=false`。
- `inputSchema`：

```json
{
  "type": "object",
  "additionalProperties": false,
  "properties": {},
  "required": []
}
```

- `structuredContent.data`：

```json
{
  "savedFiles": [".agas/scenes/scene-001.tres", ".agas/project.tres", ".agas/workspace-layout.tres"],
  "failedFiles": [
    { "path": ".agas/workspace-layout.tres", "code": "AGAS_SAVE_FAILED", "message": "Workspace layout could not be saved." }
  ],
  "pendingDirtyFiles": [".agas/workspace-layout.tres"]
}
```

- 正向测试：保存 dirty scenes、project、layout，project 成功后应用 pending deletes；成功后 `dirty=false`、`saved=true`。
- 反向测试：repository 失败不泄露绝对路径；project 失败不删除 pending scene 文件；部分成功返回三类列表；未打开项目返回 `AGAS_NO_OPEN_PROJECT`。

### 7.11 `structuredContent.data` 精确字段表（不声明 `outputSchema`）

本节只定义运行时 `structuredContent.data` DTO 合同；AGAS v1 仍不声明 MCP `outputSchema`。字段表用于实现、测试和文档一致性校验。

#### `agas.project.snapshot` data

| 字段名 | JSON 类型 | 必选/可空 | 限制/安全 |
|---|---|---|---|
| `projectName` | `string` | 必选，可空字符串 | 不包含路径；最大 120 字符 |
| `activeSceneId` | `string`/`null` | 必选，可空 | 只返回逻辑 ID，不返回文件路径 |
| `grid` | `object` | 必选，不可空 | 只含轻量网格配置；不含 Godot 对象引用 |
| `scenes` | `array` | 必选，不可空 | `includeScenes=false` 时为空数组；元素只含 scene 摘要 |
| `layers` | `array` | 必选，不可空 | `includeLayers=false` 时为空数组；元素只含 layer 摘要 |
| `images` | `array` | 必选，不可空 | `includeImages=false` 时为空数组；图片路径仅项目相对逻辑路径；禁止 URL，不含 base64 |
| `prompts` | `array` | 必选，不可空 | `includePrompts=false` 时为空数组；文本按 maxLength 裁剪策略返回 |
| `dirtyProject` | `boolean` | 必选，不可空 | 当前内存 project dirty |
| `dirtyLayout` | `boolean` | 必选，不可空 | 当前 workspace layout dirty |
| `dirtySceneIds` | `array` | 必选，不可空 | string ID 数组 |
| `pendingDeletedSceneIds` | `array` | 必选，不可空 | string ID 数组 |

#### `agas.assets.list` data

| 字段名 | JSON 类型 | 必选/可空 | 限制/安全 |
|---|---|---|---|
| `items` | `array` | 必选，不可空 | 每项只含 `relativePath/extension/sizeBytes/modifiedUnixTime` |
| `items[].relativePath` | `string` | 必选，不可空 | 项目相对路径；`/` 分隔；不含绝对路径、URL、`data:`、base64 |
| `items[].extension` | `string` | 必选，不可空 | 仅 `png/jpg/jpeg/webp` |
| `items[].sizeBytes` | `integer` | 必选，不可空 | `>=0` |
| `items[].modifiedUnixTime` | `integer` | 必选，不可空 | Unix seconds；不含本机绝对路径 |
| `count` | `integer` | 必选，不可空 | 当前页数量，`0..limit` |
| `totalCount` | `integer` | 必选，不可空 | 过滤后总数，`>=count` |
| `hasMore` | `boolean` | 必选，不可空 | 是否存在下一页 |
| `nextOffset` | `integer`/`null` | 必选，可空 | `hasMore=true` 时为下一页 offset，否则 `null` |

#### `agas.scene.mutate` data

| 字段名 | JSON 类型 | 必选/可空 | 限制/安全 |
|---|---|---|---|
| `scenes` | `array` | 必选，不可空 | scene index 摘要；不含绝对路径 |
| `activeSceneId` | `string` | 必选，不可空 | 删除 active scene 后返回新的 active scene |
| `changedSceneId` | `string`/`null` | 必选，可空 | add/rename/delete/setActive 影响的 scene ID；删除未知失败不返回内部对象 |
| `dirtyProject` | `boolean` | 必选，不可空 | mutate 成功后通常为 `true` |
| `dirtyLayout` | `boolean` | 必选，不可空 | 若本操作未影响 layout 则为当前值 |
| `dirtySceneIds` | `array` | 必选，不可空 | string ID 数组 |
| `pendingDeletedSceneIds` | `array` | 必选，不可空 | delete 成功后包含待删除 scene ID，直到 save 应用 |

#### `agas.layer.mutate` data

| 字段名 | JSON 类型 | 必选/可空 | 限制/安全 |
|---|---|---|---|
| `sceneId` | `string` | 必选，不可空 | 逻辑 ID |
| `layers` | `array` | 必选，不可空 | layer 摘要数组；不含 Godot 对象引用 |
| `currentLayerId` | `string` | 必选，不可空 | 当前 layer ID |
| `changedLayerId` | `string`/`null` | 必选，可空 | 本次影响的 layer ID |
| `dirtyProject` | `boolean` | 必选，不可空 | 当前 dirty 状态 |
| `dirtyLayout` | `boolean` | 必选，不可空 | 当前 dirty 状态 |
| `dirtySceneIds` | `array` | 必选，不可空 | 成功 mutate 后包含 `sceneId` |

#### `agas.image.mutate` data

| 字段名 | JSON 类型 | 必选/可空 | 限制/安全 |
|---|---|---|---|
| `sceneId` | `string` | 必选，不可空 | 逻辑 ID |
| `layerId` | `string` | 必选，不可空 | 逻辑 ID |
| `image` | `object`/`null` | 必选，可空 | delete 成功后可为 `null`；非 delete 为图片实例摘要 |
| `image.imageId` | `string` | 条件必选 | 图片实例 ID |
| `image.imagePath` | `string` | 条件必选 | 项目相对图片路径；不含绝对路径、URL、`data:`、base64 |
| `image.x`/`image.y` | `number` | 条件必选 | `-100000..100000` |
| `image.scaleX`/`image.scaleY` | `number` | 条件必选 | `0.01..100` |
| `image.rotationDegrees` | `number` | 条件必选 | `-3600..3600` |
| `image.opacity` | `number` | 条件必选 | `0..1` |
| `layer` | `object` | 必选，不可空 | 目标 layer 摘要；不含 Godot 对象引用 |
| `dirtySceneIds` | `array` | 必选，不可空 | 成功 mutate 后包含 `sceneId` |

#### `agas.prompt.mutate` data

| 字段名 | JSON 类型 | 必选/可空 | 限制/安全 |
|---|---|---|---|
| `sceneId` | `string` | 必选，不可空 | 逻辑 ID |
| `layerId` | `string` | 必选，不可空 | 逻辑 ID |
| `fragments` | `array` | 必选，不可空 | fragment 摘要数组 |
| `fragments[].fragmentId` | `string` | 条件必选 | fragment ID |
| `fragments[].text` | `string` | 条件必选 | 最大 4000 字符；不记录到安全日志 |
| `fragments[].enabled` | `boolean` | 条件必选 | 是否启用 |
| `mergedPreview` | `string` | 必选，不可空 | 可截断预览；不得泄露外部文件内容 |
| `changedFragmentId` | `string`/`null` | 必选，可空 | 本次影响的 fragment ID |
| `dirtySceneIds` | `array` | 必选，不可空 | 成功 mutate 后包含 `sceneId` |

#### `agas.view.set` data

| 字段名 | JSON 类型 | 必选/可空 | 限制/安全 |
|---|---|---|---|
| `sceneId` | `string` | 必选，不可空 | 逻辑 ID |
| `cameraX` | `number` | 必选，不可空 | `-100000..100000` |
| `cameraY` | `number` | 必选，不可空 | `-100000..100000` |
| `zoom` | `number` | 必选，不可空 | `0.05..20` |
| `dirtyProject` | `boolean` | 必选，不可空 | 当前 dirty 状态 |
| `dirtyLayout` | `boolean` | 必选，不可空 | 只反映本次 `view.set` 前已存在的 layout dirty；`view.set` 本身不置为 `true` |
| `dirtySceneIds` | `array` | 必选，不可空 | `view.set` 成功后必须包含 `sceneId` |

#### `agas.project.save` data

| 字段名 | JSON 类型 | 必选/可空 | 限制/安全 |
|---|---|---|---|
| `savedFiles` | `array` | 必选，不可空 | 成功写入/删除的逻辑相对路径；不含绝对路径 |
| `failedFiles` | `array` | 必选，不可空 | 保存失败条目；允许部分成功 |
| `failedFiles[].path` | `string` | 条件必选 | 逻辑相对路径 |
| `failedFiles[].code` | `string` | 条件必选 | 最小错误码表中的安全 code |
| `failedFiles[].message` | `string` | 条件必选 | 最大 500 字符；不含栈/绝对路径/临时文件名 |
| `pendingDirtyFiles` | `array` | 必选，不可空 | 保存后仍 dirty 的逻辑相对路径 |

### 7.12 逐工具正反向测试矩阵

| 工具 | 正向 | 反向 |
|---|---|---|
| `agas.project.snapshot` | 打开项目返回 scene/layer/image/prompt/view 摘要；图片字段仅项目相对逻辑路径；不改 dirty | 未打开项目；include flags 类型错误；输出含 URL/绝对路径/base64 |
| `agas.assets.list` | 分页、query、extension 过滤、后台纯 IO | limit/offset 越界；非法 extension；symlink/reparse-point；内部目录；未打开项目 |
| `agas.scene.mutate` | add/rename/delete/setActive 内存变更 | 删除最后 scene；未知 scene；触盘 handler；save 前 mtime 变化 |
| `agas.layer.mutate` | add/rename/delete/move/setVisible/setLocked/setCurrent | 删除最后 layer；未知 layer；条件字段缺失；hidden/locked 规则错误 |
| `agas.image.mutate` | add/delete/move/update 合法图片实例 | 绝对路径/URL/data/base64/file/symlink/reparse；locked/hidden layer；越界数值 |
| `agas.prompt.mutate` | add/delete/update/move/setEnabled | 文本超长；未知 fragment；locked/hidden layer；缺条件字段 |
| `agas.view.set` | 设置 camera/zoom，保存后重开恢复 | zoom 越界；未知 scene；NaN/Infinity；dirty 未标记 |
| `agas.project.save` | 多 dirty 文件显式部分提交，返回 savedFiles | repository 失败；project 失败仍 delete pending；绝对路径泄露；dirty 错清 |

`tools/list` 原始响应必须作为 schema 合同测试输入：从 raw `tools/list` JSON 中提取 4 个 mutate 工具 `inputSchema`，分别用本节列出的正向/反向参数做 JSON Schema 2020-12 校验，确认条件约束由 schema 本身拒绝，而不是只依赖 prose 或内部二次校验。

---

## 8. 路径安全

所有工具输入路径必须通过统一 validator：

- 当前 project root 本身若为 symlink/reparse-point，MCP 必须拒绝该项目的工具操作并返回 `AGAS_PROJECT_ROOT_UNSAFE`；UI 现有打开项目行为不在本期修改范围。
- 不能为空。
- 必须是项目相对路径。
- 拒绝绝对路径。
- 拒绝 `..` 和归一化后逃逸项目根。
- 拒绝 `://`、`file://`、URL、`data:`、base64。
- 图片只允许 `.png/.jpg/.jpeg/.webp`。
- 扫描跳过 `.agas/.godot/.git/bin/obj`。
- 输出路径统一 `/` 分隔的项目相对逻辑路径。
- 错误信息不返回绝对路径。
- 不跟随目录 symlink/reparse point；目录枚举遇到链接目录直接跳过。
- 文件 symlink/reparse point 一律拒绝，即使目标仍在项目内。
- Windows 测试覆盖 junction/reparse point；macOS 测试覆盖 symlink；失败消息不得泄露真实外部目标路径。

路径测试矩阵：

必须拒绝：

| 输入 | 原因 |
|---|---|
| `/etc/passwd` | POSIX 绝对路径 |
| `C:\Windows\win.ini` | Windows 绝对路径 |
| `../secret.png` | 父目录逃逸 |
| `sub/../../secret.png` | 归一化后逃逸 |
| `http://example.com/a.png` | URL |
| `https://example.com/a.png` | URL |
| `file:///tmp/a.png` | file URL |
| `data:image/png;base64,xxx` | data/base64 |
| `a.png;base64` | base64-like 输入 |
| `.agas/project.tres` | 内部目录 |
| `.godot/imported/x.png` | 内部目录 |
| `.git/objects/x` | 内部目录 |
| `bin/x.png` | 构建目录 |
| `obj/x.png` | 构建目录 |
| `a.gif` | 非允许扩展名 |
| `a.svg` | 非允许扩展名 |

必须允许：

| 输入 | 条件 |
|---|---|
| `a.webp` | 文件真实存在、不是 symlink/reparse-point、扩展名大小写归一化后为 `webp` |
| `images/a.png` | 文件真实存在、父目录不是 symlink/reparse-point、文件不是 symlink/reparse-point |

平台条件：

| 输入 | 平台/条件 | 预期 |
|---|---|---|
| `link-dir/outside.png` | macOS/Linux，`link-dir` 为目录 symlink | 跳过/拒绝，不能跟随目录链接 |
| `link-file.png` | macOS/Linux，文件 symlink | 拒绝，即使目标在项目内 |
| `junction-dir/outside.png` | Windows，目录 junction/reparse-point | 跳过/拒绝，不能跟随 reparse 目录 |
| `reparse-file.png` | Windows，文件 reparse-point | 拒绝，即使目标在项目内 |

无对应平台时必须记录跳过原因；不得把未执行的平台条件测试写成已通过。

---

## 9. 最小错误码表

| code | 层级 | 含义 | 泄露控制 |
|---|---|---|---|
| `AGAS_NO_OPEN_PROJECT` | tool | 当前无打开项目 | 不返回最近项目路径 |
| `AGAS_PATH_REJECTED` | tool | 路径格式、扩展名、逃逸、URL/base64 被拒绝 | 不返回绝对路径或外部目标 |
| `AGAS_ASSET_NOT_FOUND` | tool | 相对图片不存在或不允许访问 | 只返回输入相对路径 |
| `AGAS_SCENE_NOT_FOUND` | tool | sceneId 不存在 | 只返回 sceneId |
| `AGAS_LAYER_NOT_FOUND` | tool | layerId 不存在 | 只返回 layerId |
| `AGAS_IMAGE_NOT_FOUND` | tool | imageId 不存在 | 只返回 imageId |
| `AGAS_PROMPT_NOT_FOUND` | tool | fragmentId 不存在 | 只返回 fragmentId |
| `AGAS_LAYER_LOCKED` | tool | 目标 layer locked | 不返回内部对象地址 |
| `AGAS_LAYER_HIDDEN` | tool | 目标 layer hidden 且操作不允许 | 不返回内部对象地址 |
| `AGAS_INVALID_OPERATION` | tool | 条件字段缺失、删除最后 scene/layer 等业务错误 | 不返回栈 |
| `AGAS_SAVE_FAILED` | tool | 保存部分或全部失败 | 只返回逻辑路径 |
| `AGAS_PROJECT_ROOT_UNSAFE` | tool | 当前 project root 本身为 symlink/reparse-point，MCP 拒绝项目操作 | 不返回真实目标路径 |
| `AGAS_RATE_LIMITED` | tool | 当前 session 的 tool 调用超过固定窗口 120 次/分钟 | 不返回全局 session 信息 |
| `AGAS_BUSY` | tool | 全局待执行 tool 队列已满（容量 32） | 不返回队列内部状态或其他 session 信息 |
| `AGAS_MCP_NOT_RUNNING` | UI/status | Enabled 与 host 状态不一致 | 不返回内部异常栈 |
| `AGAS_PORT_IN_USE` | UI/status | 54119 被占用 | 不返回占用进程详情，日志可内部记录 |
| `AGAS_TIMEOUT` | tool/protocol boundary | 主线程队列或工具超时 | 不返回线程栈 |
| `AGAS_INTERNAL_ERROR` | tool | 未分类异常 | 泛化消息，内部日志记录详情 |

协议层错误不占用上述 code：例如缺 `MCP-Session-Id`、未知 session、invalid JSON-RPC、unknown tool、unsupported protocol version，按 HTTP status 或 JSON-RPC error 返回。

---

## 10. 线程模型

### 10.1 强约束

- HTTP/ASP.NET Core 线程只做：MCP 协议处理、安全校验、输入 schema 校验、提交到串行工具执行器、等待主线程结果。
- Godot `Resource`、`Node`、UI、Canvas 的读写只在 Godot 主线程执行。
- `agas.assets.list` 例外：主线程只取 project root 快照；文件枚举为后台纯 IO，不读写 Godot 对象。
- v1 所有 tools 串行执行，避免同时修改 `_project`、`_currentScene`、dirty 集合。

### 10.2 主线程队列

建议抽象：

```csharp
public interface IAgasMainThreadDispatcher
{
    Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken);
    Task InvokeAsync(Action action, CancellationToken cancellationToken);
}
```

实现要求：

- 队列由 Godot 主线程 `_Process` 或等价主线程 tick drain。
- 每个请求有超时；超时后 HTTP 返回可操作错误。
- Godot 退出/关闭 MCP 时取消等待中的请求。
- 主线程 action 捕获异常并转换为工具错误；不把内部栈返回客户端。

### 10.3 串行工具执行器

```text
HTTP request -> MCP SDK tool delegate -> AgasMcpSerialToolExecutor.Enqueue -> Dispatcher.InvokeAsync -> Facade -> Result
```

规则：

- 同一进程只允许一个 tool 在执行。
- 全局待执行 tool 队列容量固定为 `32`；满时新 tool call 不入队，返回 `isError=true` / `AGAS_BUSY`。
- 每个 session 固定窗口最多 `120` 次 `tools/call`/分钟；超限返回 `isError=true` / `AGAS_RATE_LIMITED`。
- 30 秒语义：排队/主线程开始前为硬超时；未开始时返回 `isError=true` / `AGAS_TIMEOUT`。一旦同步 Godot 原子操作已开始，为防止“客户端报超时但副作用继续”，必须等待原子操作完成并返回实际结果。
- 用户关闭 MCP、合法 session DELETE、AGAS 退出或 host stop 必须进入 quiesce：取消并清空未开始的相关/全部队列项；已开始的同步 Godot 原子操作等待完成。这是安全一致性约束，不是无限强杀线程。
- `tools/list`、`initialize` 等协议请求不算业务写工具；涉及 Godot 状态读取的 snapshot 仍经主线程。
- 后续如要并发只读，必须先证明 Resource/UI 读取线程安全；v1 不做。

---

## 11. 验收标准

### 11.1 开发前门禁

- 本技术方案与执行计划完成方案审计。
- 审计方向至少包含：
  - MCP/后端协议与生命周期。
  - Godot 主线程/Resource/UI 集成。
  - 安全/路径/测试/文档一致性。
- P0/P1 必须为 0 后才能进入生产代码开发。
- 若发现 P0/P1，必须修本文档/计划并复审。

### 11.2 开发期间验证原则

- 按波次可运行 `dotnet restore/build/test` 与 C# 单元/协议测试。
- 开发期间先实现完功能，再统一启动 Godot 做最终端到端测试；避免半成品频繁启动 Godot 得出误导结论。
- 端口占用测试优先在 C# 测试内用 `TcpListener` 占用 `127.0.0.1:54119`，不引入 Python/Node/Bash 脚本语言。

### 11.3 开发后最小命令

```bash
dotnet restore APP-AGAS-CS.sln
dotnet build APP-AGAS-CS.sln -c Debug
dotnet test tests/Core/APP-AGAS-CS.Core.Tests.csproj -c Debug --no-build
```

### 11.4 最终统一测试矩阵

必须覆盖：

- 默认关闭：`54119` 不监听。
- 开启 MCP：只监听 `127.0.0.1:54119/mcp`。
- 关闭 MCP：端口释放。
- Kestrel 最大并发连接 8；请求 body 超过 1 MiB 在协议前返回 HTTP 413。
- MCP tool 每 session 固定窗口 120 次/分钟；超限返回 tool 错误 `isError=true` / `AGAS_RATE_LIMITED`。
- 全局待执行 tool 队列容量 32；队列满返回 tool 错误 `isError=true` / `AGAS_BUSY`。
- 30 秒语义：排队/主线程开始前硬超时；同步 Godot 原子操作已开始后等待完成并返回实际结果；关闭 MCP/退出 AGAS/session DELETE 等待 quiesce。
- 端口冲突：C# `TcpListener` 占用 `127.0.0.1:54119` 后启动报错，不自动切换，UI 状态 `Error` 与 Enabled/实际 host 一致。
- Host/Origin：Host 仅 `127.0.0.1:54119`；Origin 存在时仅 `http://127.0.0.1:54119`；Origin 缺失允许。
- MCP wire：`initialize`、`notifications/initialized`、`MCP-Protocol-Version`、`MCP-Session-Id` 生成、`POST/GET` 缺失 session=400、`POST/GET` 未知或 DELETE 后 session=404、`DELETE` 自身缺失/未知 session 状态按 SDK 1.4.1 实际行为记录；unknown session DELETE 返回 2xx 时 executor 幂等 cleanup 防遗留状态；POST/GET/DELETE、Accept、Content-Type。
- `tools/list`：最终严格 8 个工具，不多不少；每个工具 `inputSchema` 完整；v1 不声明 `outputSchema`。
- `tools/call`：8 工具正反向矩阵全部通过。
- snapshot/list：只读，不改 dirty。
- assets.list：后台纯 IO，分页，跳过内部目录，不返回绝对路径/base64，不跟随目录链接，拒绝文件链接。
- scene/layer/image/prompt/view mutate：只改内存，dirty=true，saved=false，save 前磁盘不变。
- save：安全有序显式部分提交，返回 `savedFiles/failedFiles/pendingDirtyFiles`，不宣称全事务。
- 保存后关闭/重开项目：scene/layer/image/prompt/view 与 dirty 状态恢复正确；pending deletes 已按规则应用。
- 路径恶意输入：全部拒绝且不泄露真实路径。
- project root 本身为 symlink/reparse-point 时 MCP 项目操作返回 `AGAS_PROJECT_ROOT_UNSAFE`。
- macOS symlink、Windows reparse/junction 与导出包验证必须有平台证据；无对应平台时记录跳过原因。
- hidden/locked layer：编辑拒绝。
- 并发工具调用：串行执行。
- Godot 退出：Host Dispose，端口释放。
- 导出包：可运行或有明确阻塞证据。

---

## 12. 风险与回滚

| 风险 | 若发生时的风险等级 | 验证/缓解 |
|---|---:|---|
| ASP.NET Core/MCP Host 在 Godot 进程内生命周期不稳定 | P0 | 先做最小 host spike；开关 10 次；退出 Dispose 验证 |
| 导出包缺 ASP.NET Core/MCP runtime 依赖 | P1 | 独立导出验证；失败时文档标记桌面开发模式限制或补打包配置 |
| HTTP 线程误触 Godot Resource/UI | P0 | 架构强制 dispatcher；测试/CR 检查所有工具调用链 |
| 路径绕过读取项目外文件 | P0 | 统一 validator；恶意路径 + symlink/reparse 测试矩阵 |
| tools 并发导致状态错乱 | P0 | v1 串行执行器；并发请求测试 |
| 关闭 MCP 后请求未取消或端口未释放 | P1 | 生命周期测试与端口检测 |
| Host/Origin 规则与客户端不兼容 | P1 | 允许缺失 Origin 兼容非浏览器客户端；Host 仍严格；目标客户端实测 |
| 保存语义被误实现为每次写工具自动落盘 | P1 | mutation semantics 测试：写后不保存，save 后保存 |
| 多文件保存被误宣称全事务 | P1 | 文档和测试固定“显式部分提交”语义 |

回滚：

- 所有 MCP 代码集中在 `src/Mcp/**` 与少量 `src/UI/Main.cs`、`McpSettingsDialog.cs`、`ProjectPicker.cs`、`MainWorkspace.cs` 接入点，便于单独禁用。
- 设置默认关闭；若出现问题，可保持 `Enabled=false` 不启动 server。
- 生产数据仍为现有 `.tres`，MCP 不引入新业务持久化格式；回滚不需要数据迁移。
- 包依赖回滚：移除 `ModelContextProtocol.AspNetCore` PackageReference 与 `src/Mcp/**`，还原少量 UI/生命周期接线。

---

## 13. 后续安全增强（P2，非 v1 硬需求）

- Bearer token 或本地一次性 token。
- 用户首次连接确认弹窗。
- 更细粒度工具权限开关。
- 审计日志：只记录工具名、时间、结果、相对路径，不记录 prompt 全文/图片内容。
- 只读模式：只启用 snapshot/assets/list，禁用 mutate/save。
