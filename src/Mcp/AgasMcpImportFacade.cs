#nullable enable

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Application;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Contracts.Output;
using AppAgasCs.Mcp.Contracts.Runtime;
using AppAgasCs.Mcp.Runtime;

namespace AppAgasCs.Mcp;

public sealed class AgasMcpImportFacade
{
    private readonly AgasMcpProjectContextProvider _contextProvider;
    private readonly AgasMcpDirtyState _dirtyState;
    private readonly AgasImportSourceRefBroker _sourceRefs;
    private readonly AgasImportInboxService _inbox;
    private readonly AgasControlledAssetImportService _assets;
    private readonly AgasScenePackageStagingService _packages;
    private readonly IAgasMcpScenePackageBridge _packageBridge;
    private readonly Func<string> _agasHome;

    public AgasMcpImportFacade(
        AgasMcpProjectContextProvider contextProvider,
        AgasMcpDirtyState dirtyState,
        IAgasMcpScenePackageBridge packageBridge,
        AgasImportSourceRefBroker? sourceRefs = null,
        AgasImportInboxService? inbox = null,
        AgasControlledAssetImportService? assets = null,
        AgasScenePackageStagingService? packages = null,
        Func<string>? agasHome = null)
    {
        _contextProvider = contextProvider ?? throw new ArgumentNullException(nameof(contextProvider));
        _dirtyState = dirtyState ?? throw new ArgumentNullException(nameof(dirtyState));
        _packageBridge = packageBridge ?? throw new ArgumentNullException(nameof(packageBridge));
        _sourceRefs = sourceRefs ?? new AgasImportSourceRefBroker();
        _inbox = inbox ?? new AgasImportInboxService(_sourceRefs);
        _assets = assets ?? new AgasControlledAssetImportService();
        _packages = packages ?? new AgasScenePackageStagingService();
        _agasHome = agasHome ?? AgasAiSettingsPaths.ResolveHome;
    }

    public AgasMcpToolResponse List(AgasImportsListInput input)
    {
        if (!TryValidate(input, out input, out AgasMcpToolResponse? error)) return error!;
        AgasImportKind? kind = input.Kind switch
        {
            "asset" => AgasImportKind.Asset,
            "scenePackage" => AgasImportKind.ScenePackage,
            null => null,
            _ => throw new InvalidOperationException("Validated import kind was not handled."),
        };
        AgasImportInboxPage page;
        try
        {
            page = _inbox.List(_agasHome(), kind, input.Limit, input.Offset);
        }
        catch
        {
            return Failure(AgasControlledAssetImportService.InvalidCode);
        }
        var data = new AgasMcpImportsListDataDto(
            page.Items.Select(item => new AgasMcpImportItemDto(
                item.Kind,
                item.Name,
                item.SizeBytes,
                item.MediaType,
                item.ValidationState,
                item.SourceRef)).ToImmutableArray(),
            page.Count,
            page.TotalCount,
            page.HasMore,
            page.NextOffset);
        return AgasMcpToolResponse.Success(saved: true, dirty: false, data);
    }

    public AgasMcpToolResponse AssetImport(
        AgasAssetsImportInput input,
        AgasMcpCommitExecution execution,
        CancellationToken cancellationToken) =>
        AssetImport(input, CaptureProject(), execution, cancellationToken);

    /// <summary>Main-thread capture. The returned record contains no Godot object.</summary>
    public AgasMcpImportProjectCapture CaptureProject()
    {
        AgasMcpProjectContext? context = _contextProvider.GetCurrent();
        if (context == null) return new(string.Empty, string.Empty, false);
        return new(context.ProjectRoot, context.ProjectId, context.RequiresReadOnly);
    }

    /// <summary>Worker-safe heavy asset copy/decode; project capture must come from the main thread.</summary>
    public AgasMcpToolResponse AssetImport(
        AgasAssetsImportInput input,
        AgasMcpImportProjectCapture capture,
        AgasMcpCommitExecution execution,
        CancellationToken cancellationToken)
    {
        if (!TryValidate(input, out input, out AgasMcpToolResponse? error)) return error!;
        if (execution.Policy != AgasMcpCommitPolicy.ShortAtomicCommit)
            throw new ArgumentException("Asset import requires ShortAtomicCommit.", nameof(execution));
        if (string.IsNullOrWhiteSpace(capture.ProjectRoot)) return NoOpenProject();
        if (capture.RequiresReadOnly) return ReadOnly();

        using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
            static state => ((AgasMcpCommitExecution)state!).TryCancel(), execution);
        var gate = new ExecutionDurableCommitGate(execution);
        AgasAssetImportResult result = _assets.ImportBySourceRef(
            _agasHome(), capture.ProjectRoot, input.SourceRef, _sourceRefs, gate, cancellationToken);
        CompleteShortCommit(execution);
        if (!result.Success) return Failure(result.Code);
        var data = new AgasMcpAssetImportDataDto(
            result.RelativePath,
            result.Extension.TrimStart('.'),
            result.SizeBytes,
            result.PixelWidth,
            result.PixelHeight);
        return AgasMcpToolResponse.Success(saved: true, dirty: _dirtyState.IsDirty, data);
    }

    public async ValueTask<AgasMcpToolResponse> PackageImportAsync(
        AgasScenePackageImportInput input,
        AgasMcpCommitExecution execution,
        CancellationToken cancellationToken)
    {
        if (!TryValidate(input, out input, out AgasMcpToolResponse? error)) return error!;
        if (execution.Policy != AgasMcpCommitPolicy.StartedMainThreadCompletion)
            throw new ArgumentException("Scene package import requires StartedMainThreadCompletion.", nameof(execution));
        AgasMcpImportProjectCapture capture = CaptureProject();
        if (string.IsNullOrWhiteSpace(capture.ProjectRoot)) return NoOpenProject();
        if (capture.RequiresReadOnly) return ReadOnly();

        using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
            static state => ((AgasMcpCommitExecution)state!).TryCancel(), execution);
        AgasScenePackagePreparedImport prepared = PreparePackageImport(input, capture, cancellationToken);
        if (!prepared.Success) return Failure(prepared.Code);
        return await CompletePackageImportAsync(prepared, execution, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Worker-safe package validation/copy stage. It never installs Resources into the session.</summary>
    public AgasScenePackagePreparedImport PreparePackageImport(
        AgasScenePackageImportInput input,
        AgasMcpImportProjectCapture capture,
        CancellationToken cancellationToken)
    {
        AgasMcpContractBindResult<AgasScenePackageImportInput> validation =
            AgasMcpContractRegistry.ValidateInstance(input);
        if (!validation.Success || validation.Value == null
            || string.IsNullOrWhiteSpace(capture.ProjectRoot) || capture.RequiresReadOnly)
            return AgasScenePackagePreparedImport.Fail(AgasScenePackageStagingService.InvalidCode);
        return _packages.PrepareImportBySourceRef(
            _agasHome(), capture.ProjectRoot, input.SourceRef, $"scene-{Guid.NewGuid():N}", _sourceRefs, cancellationToken);
    }

    /// <summary>Main-thread session/index/journal publish stage.</summary>
    public async ValueTask<AgasMcpToolResponse> CompletePackageImportAsync(
        AgasScenePackagePreparedImport prepared,
        AgasMcpCommitExecution execution,
        CancellationToken cancellationToken)
    {
        if (execution.Policy != AgasMcpCommitPolicy.StartedMainThreadCompletion)
            throw new ArgumentException("Scene package import requires StartedMainThreadCompletion.", nameof(execution));
        AgasMcpScenePackagePublishResult published = await _packageBridge.PublishPreparedImportAsync(
            prepared, execution, cancellationToken).ConfigureAwait(false);
        if (!published.Success) return Failure(published.Code);
        var data = new AgasMcpScenePackageImportDataDto(
            published.SceneId,
            published.Name,
            prepared.CopiedAssetRelativePaths.Order(StringComparer.Ordinal).ToImmutableArray(),
            published.DirtySceneIds.Order(StringComparer.Ordinal).ToImmutableArray());
        return AgasMcpToolResponse.Success(saved: false, dirty: true, data);
    }

    public AgasMcpToolResponse PackageExport(
        AgasScenePackageExportInput input,
        AgasMcpCommitExecution execution,
        CancellationToken cancellationToken)
    {
        if (!TryValidate(input, out input, out AgasMcpToolResponse? error)) return error!;
        if (execution.Policy != AgasMcpCommitPolicy.ShortAtomicCommit)
            throw new ArgumentException("Scene package export requires ShortAtomicCommit.", nameof(execution));
        AgasMcpScenePackageExportWork capture = CapturePackageExport(input);
        if (string.IsNullOrWhiteSpace(capture.Project.ProjectRoot)) return NoOpenProject();
        if (!capture.Package.Success) return Failure(capture.Package.Code);
        return PackageExport(capture, execution, cancellationToken);
    }

    /// <summary>Main-thread live Resource to immutable package snapshot projection.</summary>
    public AgasMcpScenePackageExportWork CapturePackageExport(AgasScenePackageExportInput input)
    {
        AgasMcpImportProjectCapture project = CaptureProject();
        if (string.IsNullOrWhiteSpace(project.ProjectRoot))
            return new(project, AgasMcpScenePackageExportCapture.Fail(AgasMcpErrorCodes.NoOpenProject));
        return new(project, _packageBridge.CaptureExport(input.SceneId));
    }

    /// <summary>Worker-safe heavy export copy/hash with commit CAS immediately before final rename.</summary>
    public AgasMcpToolResponse PackageExport(
        AgasMcpScenePackageExportWork capture,
        AgasMcpCommitExecution execution,
        CancellationToken cancellationToken)
    {
        if (execution.Policy != AgasMcpCommitPolicy.ShortAtomicCommit)
            throw new ArgumentException("Scene package export requires ShortAtomicCommit.", nameof(execution));
        if (string.IsNullOrWhiteSpace(capture.Project.ProjectRoot)) return NoOpenProject();
        if (!capture.Package.Success) return Failure(capture.Package.Code);
        using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
            static state => ((AgasMcpCommitExecution)state!).TryCancel(), execution);
        var gate = new ExecutionDurableCommitGate(execution);
        AgasScenePackageExportResult result = _packages.ExportToExchange(
            _agasHome(),
            capture.Project.ProjectRoot,
            capture.Package.PackageRef,
            capture.Package.ManifestUtf8,
            capture.Package.Files,
            _sourceRefs,
            gate,
            cancellationToken);
        CompleteShortCommit(execution);
        if (!result.Success) return Failure(result.Code);
        var data = new AgasMcpScenePackageExportDataDto(
            result.PackageRef,
            result.SourceRef,
            result.ReferenceImageCount);
        return AgasMcpToolResponse.Success(saved: !_dirtyState.IsDirty, dirty: _dirtyState.IsDirty, data);
    }

    private static void CompleteShortCommit(AgasMcpCommitExecution execution)
    {
        if (execution.State == AgasMcpCommitExecutionState.ShortCommitting)
            execution.CompleteCommit();
    }

    private static bool TryValidate<T>(T input, out T validated, out AgasMcpToolResponse? error) where T : class
    {
        AgasMcpContractBindResult<T> result = AgasMcpContractRegistry.ValidateInstance(input);
        validated = result.Value!;
        error = result.Success && result.Value != null ? null : InvalidArguments();
        return error == null;
    }

    private static AgasMcpToolResponse InvalidArguments() => AgasMcpToolResponse.Failure(
        false, false, AgasMcpErrorCodes.InvalidOperation,
        "Tool arguments did not match the expected JSON shape.",
        "Check the documented import tool input schema.");

    private static AgasMcpToolResponse NoOpenProject() => AgasMcpToolResponse.Failure(
        false, false, AgasMcpErrorCodes.NoOpenProject,
        "No AGAS project is currently open.",
        "Open a project before importing or exporting project content.");

    private static AgasMcpToolResponse ReadOnly() => AgasMcpToolResponse.Failure(
        false, false, AgasMcpErrorCodes.ProjectReadOnly,
        "Project is read-only.",
        "Open the project in a compatible AGAS version before importing content.");

    private static AgasMcpToolResponse Failure(string code)
    {
        (string message, string suggestion) = code switch
        {
            AgasControlledAssetImportService.NotFoundCode => ("Import source reference is invalid or expired.", "Call agas.imports.list again and use the newly issued sourceRef."),
            AgasControlledAssetImportService.TooLargeCode or AgasScenePackageStagingService.TooLargeCode => ("Import or package payload exceeds the safe size limit.", "Reduce image dimensions, file size, or package contents and retry."),
            AgasControlledAssetImportService.CanceledCode or AgasScenePackageStagingService.CanceledCode => ("Import or export was stopped before commit.", "Retry the operation after the workspace becomes idle."),
            AgasSceneDetailErrorCodes.NotFound => ("Scene was not found.", "Use agas.project.snapshot and pass a known sceneId."),
            _ => ("Import or package validation failed.", "Verify inbox/package contents and retry with a newly issued sourceRef."),
        };
        string stableCode = string.IsNullOrWhiteSpace(code) ? AgasControlledAssetImportService.InvalidCode : code;
        return AgasMcpToolResponse.Failure(false, false, stableCode, message, suggestion);
    }

    private sealed class ExecutionDurableCommitGate : IAgasDurableCommitGate
    {
        private readonly AgasMcpCommitExecution _execution;
        public ExecutionDurableCommitGate(AgasMcpCommitExecution execution) => _execution = execution;
        public bool TryBeginDurableCommit() => _execution.TryBeginShortAtomicCommit();
    }
}
