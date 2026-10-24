#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Application;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Contracts.Output;
using AppAgasCs.Mcp.Contracts.Runtime;
using AppAgasCs.Mcp.Runtime;

namespace AppAgasCs.Mcp;

/// <summary>Single explicit production binding table: one handler per official tool name.</summary>
internal static class AgasMcpDescriptorHandlerRegistry
{
    public static IReadOnlyDictionary<string, AgasMcpPerToolHandler> Create(
        AgasMcpProjectFacade project,
        AgasMcpProjectLifecycleFacade lifecycle,
        AgasMcpImportFacade imports,
        AgasMcpAiSettingsFacade aiSettings,
        AgasMcpAiFacade ai,
        Func<bool> stageAiMutation,
        Action finishAiMutation,
        Action refreshEffectiveAiSelection)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(imports);
        ArgumentNullException.ThrowIfNull(aiSettings);
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(stageAiMutation);
        ArgumentNullException.ThrowIfNull(finishAiMutation);
        ArgumentNullException.ThrowIfNull(refreshEffectiveAiSelection);

        var registry = new AgasMcpPerToolHandlerRegistry(AgasMcpToolNames.All);

        RegisterMain<AgasProjectsRecentInput>(registry, AgasMcpToolNames.ProjectsRecent, lifecycle.Recent);
        Register(registry, AgasMcpToolNames.ProjectOpen, async (arguments, context, token) =>
        {
            if (!TryBind(arguments, out AgasProjectOpenInput? input, out AgasMcpToolResponse? error)) return error!;
            Task<AgasMcpToolResponse> operation = await context.CaptureMainThreadAsync(
                _ => lifecycle.OpenAsync(input!, context.CommitExecution, token).AsTask(), token).ConfigureAwait(false);
            return await operation.ConfigureAwait(false);
        });
        Register(registry, AgasMcpToolNames.ProjectCreate, async (arguments, context, token) =>
        {
            if (!TryBind(arguments, out AgasProjectCreateInput? input, out AgasMcpToolResponse? error)) return error!;
            Task<AgasMcpToolResponse> operation = await context.CaptureMainThreadAsync(
                _ => lifecycle.CreateAsync(input!, context.CommitExecution, token).AsTask(), token).ConfigureAwait(false);
            return await operation.ConfigureAwait(false);
        });
        Register(registry, AgasMcpToolNames.ProjectClose, async (arguments, context, token) =>
        {
            var tracker = new ProjectLifecycleCloseReceiptTracker();
            context.SetFailureDataFactory(() => lifecycle.LastCloseReceipt(tracker));
            if (!TryBind(arguments, out AgasProjectCloseInput? input, out AgasMcpToolResponse? error))
            {
                AgasMcpProjectCloseDataDto receipt = lifecycle.LastCloseReceipt(tracker);
                return new AgasMcpToolResponse(
                    ok: false,
                    saved: receipt.Saved,
                    dirty: receipt.ProjectStillOpen && !receipt.Saved,
                    data: receipt,
                    warnings: error!.Warnings,
                    error: error.Error);
            }
            return await lifecycle.CloseAsync(input!, context.CommitExecution, tracker, token).ConfigureAwait(false);
        });

        RegisterMain<AgasProjectSnapshotInput>(registry, AgasMcpToolNames.ProjectSnapshot, project.ProjectSnapshot);
        RegisterMain<AgasAssetsListInput>(registry, AgasMcpToolNames.AssetsList, project.AssetsList);
        Register(registry, AgasMcpToolNames.ImportsList, (arguments, _, _) =>
            BindValueTask<AgasImportsListInput>(arguments, imports.List));
        Register(registry, AgasMcpToolNames.AssetsImport, async (arguments, context, token) =>
        {
            if (!TryBind(arguments, out AgasAssetsImportInput? input, out AgasMcpToolResponse? error)) return error!;
            AgasMcpImportProjectCapture capture = await context.CaptureMainThreadAsync(
                _ => imports.CaptureProject(), token).ConfigureAwait(false);
            return imports.AssetImport(input!, capture, context.CommitExecution, token);
        });
        Register(registry, AgasMcpToolNames.SceneGet, (arguments, context, token) =>
            BindMainThreadAsync<AgasSceneGetInput>(arguments, context, project.SceneGet, token));
        RegisterMain<AgasSceneMutateInput>(registry, AgasMcpToolNames.SceneMutate, project.SceneMutate);
        Register(registry, AgasMcpToolNames.ScenePackageImport, async (arguments, context, token) =>
        {
            if (!TryBind(arguments, out AgasScenePackageImportInput? input, out AgasMcpToolResponse? error)) return error!;
            AgasMcpImportProjectCapture capture = await context.CaptureMainThreadAsync(
                _ => imports.CaptureProject(), token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(capture.ProjectRoot)) return NoOpenProject();
            if (capture.RequiresReadOnly) return ReadOnlyProject();
            AgasScenePackagePreparedImport prepared = imports.PreparePackageImport(input!, capture, token);
            if (!prepared.Success) return ImportFailure(prepared.Code);
            Task<AgasMcpToolResponse> completion = await context.CaptureMainThreadAsync(
                _ => imports.CompletePackageImportAsync(prepared, context.CommitExecution, token).AsTask(),
                token).ConfigureAwait(false);
            return await completion.ConfigureAwait(false);
        });
        Register(registry, AgasMcpToolNames.ScenePackageExport, async (arguments, context, token) =>
        {
            if (!TryBind(arguments, out AgasScenePackageExportInput? input, out AgasMcpToolResponse? error)) return error!;
            AgasMcpScenePackageExportWork work = await context.CaptureMainThreadAsync(
                _ => imports.CapturePackageExport(input!), token).ConfigureAwait(false);
            return imports.PackageExport(work, context.CommitExecution, token);
        });
        RegisterMain<AgasGridMutateInput>(registry, AgasMcpToolNames.GridMutate, project.GridMutate);
        RegisterMain<AgasLayerMutateInput>(registry, AgasMcpToolNames.LayerMutate, project.LayerMutate);
        RegisterMain<AgasLayerMergeVisibleInput>(registry, AgasMcpToolNames.LayerMergeVisible, project.LayerMergeVisible);
        RegisterMain<AgasImageMutateInput>(registry, AgasMcpToolNames.ImageMutate, project.ImageMutate);
        RegisterMain<AgasPromptMutateInput>(registry, AgasMcpToolNames.PromptMutate, project.PromptMutate);
        RegisterMain<AgasViewSetInput>(registry, AgasMcpToolNames.ViewSet, project.ViewSet);
        Register(registry, AgasMcpToolNames.ProjectSave, (arguments, context, token) =>
        {
            var tracker = new AgasMcpProjectSaveReceiptTracker();
            context.SetFailureDataFactory(() => tracker.Current);
            return BindMainThreadAsync<AgasProjectSaveInput>(arguments, context,
                input => project.ProjectSave(input, context.CommitExecution, tracker, token), token);
        });
        RegisterMain<AgasSelectionSetInput>(registry, AgasMcpToolNames.SelectionSet, project.SelectionSet);
        RegisterMain<AgasSelectionGetInput>(registry, AgasMcpToolNames.SelectionGet, project.SelectionGet);

        Register(registry, AgasMcpToolNames.AiProfilesList, async (arguments, context, token) =>
        {
            await context.CaptureMainThreadAsync(_ => { refreshEffectiveAiSelection(); return true; }, token).ConfigureAwait(false);
            return await BindValueTask<AgasAiProfilesListInput>(arguments, aiSettings.Profiles).ConfigureAwait(false);
        });
        Register(registry, AgasMcpToolNames.AiModelsList, (arguments, _, _) =>
            BindValueTask<AgasAiModelsListInput>(arguments, aiSettings.Models));
        Register(registry, AgasMcpToolNames.AiProfileActivate, (arguments, context, token) =>
            ExecuteStagedAiMutation<AgasAiProfileActivateInput>(arguments, context, token,
                input => aiSettings.Activate(input, context.CommitExecution, token),
                aiSettings.FinalizeStagedUiOnMainThread,
                stageAiMutation,
                finishAiMutation,
                refreshEffectiveAiSelection));
        Register(registry, AgasMcpToolNames.AiModelSet, (arguments, context, token) =>
            ExecuteStagedAiMutation<AgasAiModelSetInput>(arguments, context, token,
                input => aiSettings.SetModel(input, context.CommitExecution, token),
                aiSettings.FinalizeStagedUiOnMainThread,
                stageAiMutation,
                finishAiMutation,
                refreshEffectiveAiSelection));
        Register(registry, AgasMcpToolNames.AiDiagnostics, async (arguments, context, token) =>
        {
            await context.CaptureMainThreadAsync(_ => { refreshEffectiveAiSelection(); return true; }, token).ConfigureAwait(false);
            return await BindValueTask<AgasAiDiagnosticsInput>(arguments, aiSettings.Diagnostics).ConfigureAwait(false);
        });
        RegisterMain<AgasAiGenerateInput>(registry, AgasMcpToolNames.AiGenerate, ai.Generate);
        RegisterMain<AgasAiStatusInput>(registry, AgasMcpToolNames.AiStatus, ai.Status);
        RegisterMain<AgasAiCancelInput>(registry, AgasMcpToolNames.AiCancel, ai.Cancel);
        RegisterMain<AgasAiApplyInput>(registry, AgasMcpToolNames.AiApply, ai.Apply);

        return registry.FreezeAndValidateComplete();
    }

    private static void RegisterMain<TInput>(
        AgasMcpPerToolHandlerRegistry registry,
        string toolName,
        Func<TInput, AgasMcpToolResponse> handler)
        where TInput : class =>
        Register(registry, toolName, (arguments, _, _) => BindValueTask(arguments, handler));

    private static void Register(
        AgasMcpPerToolHandlerRegistry registry,
        string toolName,
        AgasMcpPerToolHandler handler) => registry.Register(toolName, handler);

    private static ValueTask<AgasMcpToolResponse> BindValueTask<TInput>(
        IReadOnlyDictionary<string, JsonElement>? arguments,
        Func<TInput, AgasMcpToolResponse> handler)
        where TInput : class =>
        new(TryBind(arguments, out TInput? input, out AgasMcpToolResponse? error)
            ? handler(input!)
            : error!);

    private static async ValueTask<AgasMcpToolResponse> BindAsync<TInput>(
        IReadOnlyDictionary<string, JsonElement>? arguments,
        Func<TInput, ValueTask<AgasMcpToolResponse>> handler)
        where TInput : class
    {
        if (!TryBind(arguments, out TInput? input, out AgasMcpToolResponse? error)) return error!;
        return await handler(input!).ConfigureAwait(false);
    }

    private static async ValueTask<AgasMcpToolResponse> BindMainThreadAsync<TInput>(
        IReadOnlyDictionary<string, JsonElement>? arguments,
        AgasMcpToolExecutionContext context,
        Func<TInput, AgasMcpToolResponse> handler,
        CancellationToken cancellationToken)
        where TInput : class
    {
        if (!TryBind(arguments, out TInput? input, out AgasMcpToolResponse? error)) return error!;
        return await context.InvokeMainThreadAsync(_ => handler(input!), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<AgasMcpToolResponse> ExecuteStagedAiMutation<TInput>(
        IReadOnlyDictionary<string, JsonElement>? arguments,
        AgasMcpToolExecutionContext context,
        CancellationToken cancellationToken,
        Func<TInput, AgasMcpToolResponse> workerMutation,
        Func<AgasMcpToolResponse, AgasMcpToolResponse> finalizeOnMainThread,
        Func<bool> stageOnMainThread,
        Action finishOnMainThread,
        Action refreshEffectiveSelection)
        where TInput : class
    {
        if (!TryBind(arguments, out TInput? input, out AgasMcpToolResponse? error)) return error!;
        bool staged = await context.CaptureMainThreadAsync(_ =>
        {
            refreshEffectiveSelection();
            return stageOnMainThread();
        }, cancellationToken).ConfigureAwait(false);
        if (!staged)
        {
            return AgasMcpToolResponse.Failure(false, false, AgasMcpErrorCodes.AiSettingsConflict,
                "AI settings have unsaved UI edits or another mutation is active.",
                "Finish settings edits and retry.");
        }

        AgasMcpToolResponse response;
        try
        {
            response = workerMutation(input!);
        }
        catch
        {
            await context.CaptureMainThreadAsync(_ =>
            {
                finishOnMainThread();
                return true;
            }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return await context.CaptureMainThreadAsync(_ =>
        {
            AgasMcpToolResponse finalized = finalizeOnMainThread(response);
            refreshEffectiveSelection();
            finishOnMainThread();
            return finalized;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private static bool TryBind<TInput>(
        IReadOnlyDictionary<string, JsonElement>? arguments,
        out TInput? input,
        out AgasMcpToolResponse? error)
        where TInput : class
    {
        AgasMcpContractBindResult<TInput> bound = AgasMcpContractRegistry.Bind<TInput>(arguments);
        input = bound.Value;
        error = bound.Success && input != null
            ? null
            : AgasMcpToolResponse.Failure(
                false,
                false,
                AgasMcpErrorCodes.InvalidOperation,
                "Tool arguments did not match the expected JSON shape.",
                "Use the exact documented camelCase properties and value constraints.");
        return error == null;
    }

    private static AgasMcpToolResponse NoOpenProject() => AgasMcpToolResponse.Failure(
        false, false, AgasMcpErrorCodes.NoOpenProject,
        "No AGAS project is currently open.", "Open a project before importing package content.");

    private static AgasMcpToolResponse ReadOnlyProject() => AgasMcpToolResponse.Failure(
        false, false, AgasMcpErrorCodes.ProjectReadOnly,
        "Project is read-only.", "Open the project in a compatible AGAS version before importing content.");

    private static AgasMcpToolResponse ImportFailure(string code) => AgasMcpToolResponse.Failure(
        false, false, string.IsNullOrWhiteSpace(code) ? AgasMcpErrorCodes.PackageInvalid : code,
        "Scene package validation failed.", "Verify the package contents and retry with a newly issued sourceRef.");
}
