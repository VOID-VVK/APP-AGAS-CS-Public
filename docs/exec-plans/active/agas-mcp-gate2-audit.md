# AGAS MCP Gate 2 收尾验证审计证据

> 项目：仓库根目录 `APP-AGAS-CS`
> 状态：Gate 2 收尾验证证据已记录；Core 101/101 passed；Debug / Release / ExportRelease build 均 0 warning / 0 error；三项 Godot acceptance 均 PASS；最终 `127.0.0.1:54119` 无监听。  
> 本轮范围：仅更新指定设计/执行/审计文档；不修改生产代码/测试；按既有日志固化当前验证结果。  
> 固定边界：全 C#；正式 MCP；默认关闭；固定 `127.0.0.1:54119/mcp`；Godot Resource `.tres` 持久化；禁止 JS/TS/GDScript；禁止公网/shell/任意文件系统能力。  

---

## 1. 已验证事实

### 1.1 项目/环境

- 当前工作目录：仓库根目录 `APP-AGAS-CS`。
- 当前目录非 git 工作树：`find . -maxdepth 1 -name .git -type d -print` 无输出。
- 缺 `export_presets.cfg`：导出验证阻塞，不能宣称导出通过。
- 历史 Round3 正式 .NET 8 build/test 结果已记录：
  - `dotnet build APP-AGAS-CS.sln -c Debug --no-restore`：0 warning / 0 error。
  - 历史 Round3 `dotnet test tests/Core/APP-AGAS-CS.Core.Tests.csproj -c Debug --no-restore`：83/83 tests passed。
  - `83/83` 仅标作历史 Round3，不作为当前结论。
- 当前收尾验证结果已按用户给定日志记录：
  - `/tmp/agas_gate2_restore_build_core.log`：Debug / Release / ExportRelease build 均 0 warning / 0 error；Core 101/101 passed。
  - `/tmp/agas_gate2_godot_resource.log`：`tests/Godot/McpResourceRuntimeAcceptance.tscn` PASS。
  - `/tmp/agas_gate2_godot_http.log`：`tests/Godot/McpHttpRuntimeAcceptance.tscn` PASS。
  - `/tmp/agas_gate2_godot_settings_ui.log`：`tests/Godot/McpSettingsUiAcceptance.tscn` PASS。
  - 最终 `lsof -nP -iTCP:54119 -sTCP:LISTEN` 无输出：`127.0.0.1:54119` 无监听。

### 1.2 实现已存在

- `APP-AGAS-CS.csproj` 已引用 `ModelContextProtocol.AspNetCore` `1.4.1`。
- MCP 实现文件已存在：
  - `src/Mcp/AgasMcpHostFactory.cs`
  - `src/Mcp/AgasMcpLifecycleService.cs`
  - `src/Mcp/AgasMcpMainThreadDispatcher.cs`
  - `src/Mcp/AgasMcpSerialToolExecutor.cs`
  - `src/Mcp/AgasMcpProjectContextProvider.cs`
  - `src/Mcp/AgasMcpProjectFacade.cs`
  - `src/Mcp/AgasMcpPathValidator.cs`
  - `src/Mcp/AgasMcpRepositoryErrorMapper.cs`
  - `src/Mcp/Contracts/*.cs`
  - `src/Mcp/Tools/*.cs`
- 8 个正式工具注册实现已存在于 `src/UI/Main.cs`：
  - `agas.project.snapshot`
  - `agas.assets.list`
  - `agas.scene.mutate`
  - `agas.layer.mutate`
  - `agas.image.mutate`
  - `agas.prompt.mutate`
  - `agas.view.set`
  - `agas.project.save`
- `src/Mcp/AgasMcpHostFactory.cs` 已存在正式 MCP Host 工厂：
  - Kestrel `IPAddress.Loopback` + 固定端口 `54119`。
  - `MaxConcurrentConnections = 8`。
  - `MaxRequestBodySize = 1024 * 1024`。
  - `AddMcpServer().WithHttpTransport(options => options.Stateless = false).WithTools(tools)`。
  - `app.MapMcp("/mcp")`。
- `src/Mcp/AgasMcpSettingsResource.cs` / `AgasMcpSettingsStore.cs` / `src/UI/McpSettingsDialog.cs` 已存在，MCP 设置为 `.tres` Resource。
- 测试文件已存在：
  - `tests/Core/Mcp/AgasMcpHostTests.cs`
  - `tests/Core/Mcp/AgasMcpPathValidatorTests.cs`
  - `tests/Core/Mcp/AgasMcpSerialToolExecutorTests.cs`
  - `tests/Core/Mcp/AgasMcpSettingsAndLifecycleTests.cs`
  - `tests/Core/Mcp/AgasMcpToolContractTests.cs`

### 1.3 已执行命令证据

```bash
dotnet build APP-AGAS-CS.sln -c Debug --no-restore
dotnet build APP-AGAS-CS.csproj -c Release --no-restore
dotnet build APP-AGAS-CS.csproj -c ExportRelease --no-restore
dotnet test tests/Core/APP-AGAS-CS.Core.Tests.csproj -c Debug --no-restore
```

结果：`/tmp/agas_gate2_restore_build_core.log` 记录 Debug / Release / ExportRelease build 均 `0 个警告` / `0 个错误`；Core `101/101` passed。历史 Round3 的 `83/83` 只保留为历史证据。

```bash
godot --headless --path . res://tests/Godot/McpResourceRuntimeAcceptance.tscn
godot --headless --path . res://tests/Godot/McpHttpRuntimeAcceptance.tscn
godot --headless --path . res://tests/Godot/McpSettingsUiAcceptance.tscn
lsof -nP -iTCP:54119 -sTCP:LISTEN
```

结果：`McpResourceRuntimeAcceptance.tscn`、`McpHttpRuntimeAcceptance.tscn`、`McpSettingsUiAcceptance.tscn` 均 PASS；最终 `lsof` 无输出，`127.0.0.1:54119` 无监听。

---

## 2. Gate 2 旧 P0/P1 修复状态

说明：以下旧 P0/P1 保留首轮审计证据；历史 Round3 的 F2 83/83 仅作历史证据。当前收尾验证已完成 Core 101/101 与三项 Godot acceptance PASS，不再保留旧失败态当前结论。

### P0-1：跨项目 dirty/delete 状态串扰

**已验证证据**

- `src/UI/Main.cs` 的 `InitializeMcp()` 中创建 `_mcpDirtyState = new AgasMcpDirtyState();`，生命周期是 app/root 级。
- `src/UI/Main.cs` 的 `OpenWorkspace()` 在打开项目时执行 `_mcpContextProvider?.Clear();`，但未发现同步清空 `_mcpDirtyState` 的实现。
- `src/UI/MainWorkspace.cs` 的 `CloseProject()` 触发 `ProjectClosed`，但未发现清空 MCP dirty/pending delete 的实现。
- `src/Mcp/AgasMcpDirtyState.cs` 维护 `DirtyProject`、`DirtyLayout`、`DirtySceneIds`、`PendingDeletedSceneIds`，未携带 project root/project id 维度。

**风险**

- 项目 A 的 dirty scene / pending delete 可能在切到项目 B 后继续参与 `agas.project.save`。
- `pendingDeletedSceneIds` 若跨项目残留，可能删除另一个项目同名 `.agas/scenes/<sceneId>.tres`。

**状态**

- 已有代码修复记录：dirty state 按当前项目隔离/切换清理方向已修。
- 当前验证：Core 101/101 与 Godot HTTP/UI acceptance PASS 已作为收尾证据记录；跨项目 dirty/delete 不串扰不再标为 G1/G2 待修。

### P0-2：保存阶段缺 sceneId/path 安全复核

**已验证证据**

- `src/Mcp/AgasMcpProjectFacade.cs` 的 `ProjectSave()` 读取 `before.DirtySceneIds` / `before.PendingDeletedSceneIds`。
- 保存 dirty scene 时直接调用 `AgasPaths.SceneFile(context.ProjectRoot, sceneId)`。
- pending delete 时直接调用 `File.Delete(AgasPaths.SceneFile(context.ProjectRoot, sceneId))`。
- `RequiredId()` 当前只做空白检查和 `Trim()`；未在该方法中验证 `sceneId` pattern / path traversal / 分隔符。
- 当前测试目录未发现 `AgasMcpProjectSaveTests.cs`，也未发现保存阶段针对 symlink/sceneId 的专项测试。

**风险**

- 即使工具输入 schema 约束存在，内部 dirty state / sceneId 仍需要保存前二次防线。
- 保存/删除路径是落盘边界，不能只依赖 UI 或 schema 层。

**状态**

- 已有代码修复记录：保存前 sceneId/path 安全复核方向已修。
- 当前验证：Core 101/101 与 Godot HTTP acceptance PASS 已作为收尾证据记录；sceneId/path/symlink 边界不再标为 G1/G2 待修。

### P1-1：executor 错误未映射为 tool `isError=true`

**已验证证据**

- `src/Mcp/AgasMcpSerialToolExecutor.cs` 对 Busy/RateLimited/Stopped 抛 `AgasMcpToolExecutionRejectedException`。
- timeout 当前在 queued call 中转成 `TimeoutException("AGAS MCP tool execution timed out.")`。
- `src/Mcp/AgasMcpToolRuntime.cs` 的 `ExecuteToolAsync()` 直接返回 `_serialExecutor.ExecuteAsync(...)`。
- 当前未发现 runtime 层 catch `AgasMcpToolExecutionRejectedException` / `TimeoutException` 并转换为 `AgasMcpToolResponse` 的实现。

**风险**

- `AGAS_BUSY` / `AGAS_RATE_LIMITED` / `AGAS_TIMEOUT` / stopped 可能冒泡到 MCP SDK 或表现为协议/服务器异常，而不是业务 tool 错误。
- Gate 2 要求 rate/queue/timeout 返回 `isError=true` + `AGAS_*`，当前证据不满足。

**状态**

- 已有代码修复记录：executor rejection/timeout/cancel 到 tool `isError=true` 的映射方向已修。
- 当前验证：Core 101/101 已覆盖 executor Busy/RateLimited/Timeout/Stopped 映射；不再标为 G1/G2 待修。

### P1-2：未传真实 `sessionId`，每 session 限流语义不成立

**已验证证据**

- `src/Mcp/Tools/AgasMcpServerToolBase.cs` 的 `InvokeAsync()` 调用：

```csharp
AgasMcpToolResponse response = await _runtime.ExecuteToolAsync(null, ProtocolTool.Name, arguments, cancellationToken).ConfigureAwait(false);
```

- `src/Mcp/AgasMcpSerialToolExecutor.cs` 的 `NormalizeSessionId(null)` 返回 `_missing_session`。
- 当前未验证从 SDK `RequestContext<CallToolRequestParams>` 提取真实 `MCP-Session-Id`。

**风险**

- 所有客户端共享 `_missing_session` 限流窗口。
- Gate 2 要求“每 session 固定窗口 120 次/分钟”，当前证据不满足。

**状态**

- 已有代码修复记录：真实 sessionId / per-session rate limit 方向已修。
- 当前验证：Core 101/101 与 Godot HTTP acceptance PASS 已作为收尾证据记录；每 session rate limit 不再标为 G1/G2 待修。

### P1-3：disable/stop 未清空或取消 executor 队列

**已验证证据**

- `src/Mcp/AgasMcpLifecycleService.StopAsync()` 停止/释放 `_host`，未持有或调用 `AgasMcpSerialToolExecutor.Stop()`。
- `src/UI/Main.cs` 的 `ApplyMcpSettingsAsync()` 关闭 MCP 设置时调用 `_mcpLifecycle.ApplySettingsAsync(_mcpSettings, ...)`。
- `src/UI/Main.cs` 的 `DisposeMcpAsync()` 在 AGAS 退出时才 `executor.DisposeAsync()`。
- `src/Mcp/AgasMcpSerialToolExecutor.cs` 的 `Stop()` 具备清队列/拒绝新 work 的能力，但关闭设置路径未调用。

**风险**

- 用户关闭 MCP 后，已排队/阻塞的 tool 可能继续执行或提交结果。
- Gate 2 要求 stop/exit/session DELETE 清空并取消队列，当前关闭设置路径证据不满足。

**状态**

- 已有代码修复记录：disable/stop/exit 队列取消方向已修。
- 当前验证：Core 101/101、Godot HTTP acceptance、Godot Settings UI acceptance PASS 已作为收尾证据记录；disable/stop/exit/session DELETE 清队列不再标为 G1/G2 待修。

### P1-4：测试 runtime/崩溃/覆盖门禁未满足

**已验证证据**

- Round3 正式 .NET 8 build 已通过：0 warning / 0 error。
- 历史 Round3 F2 正式 .NET 8 测试：83/83 tests passed，仅作历史证据。
- 当前 Core 测试：101/101 tests passed。
- 当前 Godot acceptance：Resource / HTTP / Settings UI 三项 PASS。

**风险**

- 历史 F2 83/83 不再作为当前结论依据；当前结论以 Core 101/101、三项 Godot acceptance PASS 和最终端口无监听为准。
- 导出包与 Windows 实机仍为独立待验证项，不得宣称通过。

**状态**

- 历史 F2 已完成：83/83。
- 当前收尾验证已完成：Core 101/101、三项 Godot acceptance PASS、最终 `127.0.0.1:54119` 无监听。

---

## 3. Gate 2 Round3/G3 修复状态

说明：以下为 Round3/G3 记录的修复状态；当前收尾验证已通过 Core 101/101 与三项 Godot acceptance，不再作为当前 G1/G2 待修结论。

### G3-1：accepting gate

**状态**

- 已有代码修复记录：新增 accepting gate。
- 待验证：host/lifecycle 在停止、quiesce、session 删除、禁用设置期间不得接受新的 tool work；对应请求应返回受控错误或 SDK 预期状态。

### G3-2：quiesce

**状态**

- 已有代码修复记录：新增 quiesce。
- 设计决定：30 秒语义在排队/主线程开始前是硬超时；一旦同步 Godot 原子操作已开始，为防止“客户端报超时但副作用继续”，必须等待原子操作完成并返回实际结果。stop/delete 等待 quiesce；这是安全一致性约束，不是无限强杀线程。
- 待验证：进入 quiesce 后不再接收新 work，未开始 work 被取消，已开始的同步原子操作完成后返回真实结果，符合 Gate 2 安全矩阵。

### G3-3：session DELETE cleanup

**状态**

- 已有代码修复记录：新增 session DELETE cleanup。
- 设计决定：unknown session `DELETE` 按 SDK `1.4.1` 返回 2xx 时，对 executor 做幂等 cleanup 是防遗留状态，不是越权；仍不得自定义访问 SDK 内部 session。
- 待验证：合法 session `DELETE` 后清理该 session 相关队列/限流/状态；旧 session 再次 `POST` 与 `GET` 仍按计划验证为 `404`；缺失/未知 session 状态按 SDK `1.4.1` 实际行为记录。

### G3-4：UI dirty 拆分

**状态**

- 已有代码修复记录：新增 UI dirty 拆分。
- 当前验证：Godot Settings UI acceptance PASS 已作为 UI 设置/端口/关闭路径收尾证据记录；不再标为 G1/G2 待修。

### G3-5：保存失败不关闭

**状态**

- 已有代码修复记录：新增保存失败不关闭。
- 待验证：保存失败时 UI/项目不被误关闭，dirty/pendingDirtyFiles/failedFiles 保留，用户可继续处理失败项。

### G3-6：schema/test 修复

**状态**

- 已有代码修复记录：新增 schema/test 修复。
- 当前验证：Core 101/101 与 Godot HTTP acceptance PASS 已作为 8 tools contract、mutate schema、host/session/wire/Host/Origin/body 413、rate limit、queue、timeout、delete cleanup 收尾证据记录。

---

## 4. 待验证项

- 已完成：历史 Round3 正式 .NET 8 build 0 warning / 0 error；历史 F2 正式 .NET 8 测试 83/83 passed。
- 已完成：当前 Debug / Release / ExportRelease build 均 0 warning / 0 error。
- 已完成：当前 Core 101/101 passed。
- 已完成：当前 Godot acceptance 三项 PASS：
  - `tests/Godot/McpResourceRuntimeAcceptance.tscn`。
  - `tests/Godot/McpHttpRuntimeAcceptance.tscn`。
  - `tests/Godot/McpSettingsUiAcceptance.tscn`。
- 已完成：最终 `127.0.0.1:54119` 无监听。
- 仍待验证且不得宣称通过：
  - 缺 `export_presets.cfg`；导出预设验证阻塞。
  - 实际 Godot 导出包未验证。
  - Windows junction/reparse 与 Windows 实机未验证。

---

## 5. P2 / 后续

- Bearer token / 一次性本地 token。
- 首次连接用户确认。
- 只读模式开关。
- MCP 审计日志。
- 更细粒度工具权限。
- 多项目/多窗口支持。
- 并发只读优化。

---

## 6. 当前结论

- 实现已存在；历史 Round3 正式 .NET 8 build 0 warning / 0 error、F2 83/83 已完成，且仅作为历史证据。
- 当前收尾验证已完成：Debug / Release / ExportRelease build 均 0 warning / 0 error；Core 101/101 passed；三项 Godot acceptance PASS；最终 `127.0.0.1:54119` 无监听。
- 当前不再保留旧失败态结论。
- 仍待验证且不得宣称通过：缺 `export_presets.cfg`；实际 Godot 导出包未验证；Windows junction/reparse 与 Windows 实机未验证。
