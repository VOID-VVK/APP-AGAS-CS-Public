using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Contracts.Output;
using AppAgasCs.Mcp.Contracts.Runtime;
using AppAgasCs.Mcp.Runtime;

namespace AppAgasCs.Mcp;

public enum AgasMcpToolFamily { Project, Ai }
public enum AgasMcpToolDispatchMode { MainThread, AsyncMainThread, AsyncOrchestrated }

public sealed record AgasMcpToolDescriptor(
    string Name,
    string Description,
    JsonElement InputSchema,
    bool ReadOnlyHint,
    bool DestructiveHint,
    bool IdempotentHint,
    bool OpenWorldHint,
    AgasMcpToolFamily Family,
    AgasMcpToolDispatchMode DispatchMode,
    TimeSpan Timeout,
    AgasMcpCommitPolicy CommitPolicy,
    AgasMcpOutputContract? OutputContract);

/// <summary>
/// Single source of truth for protocol discovery and runtime routing metadata.
/// Adding an MCP tool no longer requires editing both the catalog and runtime name switches.
/// </summary>
public static class AgasMcpToolDescriptorRegistry
{
    private static readonly TimeSpan HeavyTimeout = TimeSpan.FromSeconds(120);

    public static IReadOnlyList<AgasMcpToolDescriptor> All { get; } = Array.AsReadOnly(new[]
    {
        D(AgasMcpToolNames.ProjectsRecent, "List recent AGAS projects using process-local opaque references without exposing paths.", AgasMcpToolSchemas.ProjectsRecent, true, false, true, O<AgasMcpRecentProjectsDataDto>()),
        D(AgasMcpToolNames.ProjectOpen, "Open a recent AGAS project by an issued opaque project reference.", AgasMcpToolSchemas.ProjectOpen, false, false, false, O<AgasMcpProjectOpenDataDto>(), dispatch: AgasMcpToolDispatchMode.AsyncOrchestrated, commitPolicy: AgasMcpCommitPolicy.StartedMainThreadCompletion),
        D(AgasMcpToolNames.ProjectCreate, "Create and open an AGAS project under the fixed managed project root.", AgasMcpToolSchemas.ProjectCreate, false, false, false, O<AgasMcpProjectCreateDataDto>(), dispatch: AgasMcpToolDispatchMode.AsyncOrchestrated, commitPolicy: AgasMcpCommitPolicy.StartedMainThreadCompletion),
        D(AgasMcpToolNames.ProjectClose, "Close the current AGAS project using explicit save and AI cancellation policies.", AgasMcpToolSchemas.ProjectClose, false, false, false, O<AgasMcpProjectCloseDataDto>([typeof(AgasMcpProjectCloseDataDto)]), dispatch: AgasMcpToolDispatchMode.AsyncOrchestrated, commitPolicy: AgasMcpCommitPolicy.StartedMainThreadCompletion),
        D(AgasMcpToolNames.ProjectSnapshot, "Return a lightweight snapshot of the currently open AGAS project.", AgasMcpToolSchemas.ProjectSnapshot, true, false, true, O<AgasMcpProjectSnapshotDataDto>()),
        D(AgasMcpToolNames.AssetsList, "List safe project-relative image assets without returning base64, URLs, or absolute paths.", AgasMcpToolSchemas.AssetsList, true, false, true, O<AgasMcpAssetsListDataDto>()),
        D(AgasMcpToolNames.ImportsList, "List lightweight import inbox entries and issue expiring opaque source references.", AgasMcpToolSchemas.ImportsList, true, false, true, O<AgasMcpImportsListDataDto>(), dispatch: AgasMcpToolDispatchMode.AsyncOrchestrated),
        D(AgasMcpToolNames.AssetsImport, "Import an image from the controlled inbox using an issued opaque source reference.", AgasMcpToolSchemas.AssetsImport, false, false, false, O<AgasMcpAssetImportDataDto>(), dispatch: AgasMcpToolDispatchMode.AsyncOrchestrated, commitPolicy: AgasMcpCommitPolicy.ShortAtomicCommit, timeout: HeavyTimeout),
        D(AgasMcpToolNames.SceneGet, "Read a stable paginated scene detail snapshot without changing active project state. Includes optional scene readme/playbook; follow it before inventing a generation pipeline.", AgasMcpToolSchemas.SceneGet, true, false, true, O<AgasMcpSceneGetDataDto>(), dispatch: AgasMcpToolDispatchMode.AsyncOrchestrated),
        D(AgasMcpToolNames.SceneMutate, "Add, rename, delete, activate, or set the scene readme/playbook in memory without saving files.", AgasMcpToolSchemas.SceneMutate, false, true, false, O<AgasMcpSceneMutationDataDto>()),
        D(AgasMcpToolNames.ScenePackageImport, "Import a controlled scene package into the current project using an opaque source reference.", AgasMcpToolSchemas.ScenePackageImport, false, false, false, O<AgasMcpScenePackageImportDataDto>(), dispatch: AgasMcpToolDispatchMode.AsyncOrchestrated, commitPolicy: AgasMcpCommitPolicy.StartedMainThreadCompletion, timeout: HeavyTimeout),
        D(AgasMcpToolNames.ScenePackageExport, "Export a scene package atomically to the global controlled exchange.", AgasMcpToolSchemas.ScenePackageExport, false, false, false, O<AgasMcpScenePackageExportDataDto>(), dispatch: AgasMcpToolDispatchMode.AsyncOrchestrated, commitPolicy: AgasMcpCommitPolicy.ShortAtomicCommit, timeout: HeavyTimeout),
        D(AgasMcpToolNames.GridMutate, "Set grid size, snapping, or visibility without automatically saving the project.", AgasMcpToolSchemas.GridMutate, false, false, true, O<AgasMcpGridMutationDataDto>()),
        D(AgasMcpToolNames.LayerMutate, "Mutate in-memory AGAS layers for a loaded scene and mark the scene dirty.", AgasMcpToolSchemas.LayerMutate, false, true, false, O<AgasMcpLayerMutationDataDto>()),
        D(AgasMcpToolNames.LayerMergeVisible, "Merge every other visible layer into the current layer as one PNG at the current Q region or source bounds.", AgasMcpToolSchemas.LayerMergeVisible, false, true, false, O<AgasMcpLayerMergeVisibleDataDto>()),
        D(AgasMcpToolNames.ImageMutate, "Mutate image instances on visible unlocked AGAS layers using safe project-relative paths.", AgasMcpToolSchemas.ImageMutate, false, true, false, O<AgasMcpImageMutationDataDto>()),
        D(AgasMcpToolNames.PromptMutate, "Mutate prompt fragments and lower-camel prompt roles on visible unlocked AGAS layers.", AgasMcpToolSchemas.PromptMutate, false, true, false, O<AgasMcpPromptMutationDataDto>()),
        D(AgasMcpToolNames.ViewSet, "Set in-memory AGAS scene view state and mark the scene dirty.", AgasMcpToolSchemas.ViewSet, false, false, true, O<AgasMcpViewSetDataDto>()),
        D(AgasMcpToolNames.ProjectSave, "Persist dirty AGAS project resources using explicit partial save semantics.", AgasMcpToolSchemas.ProjectSave, false, false, true, O<AgasMcpProjectSaveDataDto>(), dispatch: AgasMcpToolDispatchMode.AsyncOrchestrated, commitPolicy: AgasMcpCommitPolicy.StartedMainThreadCompletion),
        D(AgasMcpToolNames.SelectionSet, "Set or clear the live AGAS canvas regions or selected image without simulating pointer input.", AgasMcpToolSchemas.SelectionSet, false, false, true, O<AgasMcpSelectionDataDto>()),
        D(AgasMcpToolNames.SelectionGet, "Return the live AGAS canvas regions and selected-image state.", AgasMcpToolSchemas.SelectionGet, true, false, true, O<AgasMcpSelectionGetDataDto>()),
        D(AgasMcpToolNames.AiProfilesList, "List safe AI connections and the effective active connection/model execution selection.", AgasMcpToolSchemas.AiProfilesList, true, false, true, O<AgasMcpAiProfilesDataDto>(), family: AgasMcpToolFamily.Ai, dispatch: AgasMcpToolDispatchMode.AsyncOrchestrated),
        D(AgasMcpToolNames.AiModelsList, "Refresh and list image models permitted by the active connection's URL and API key.", AgasMcpToolSchemas.AiModelsList, true, false, true, O<AgasMcpAiModelsDataDto>(), family: AgasMcpToolFamily.Ai, dispatch: AgasMcpToolDispatchMode.AsyncOrchestrated),
        D(AgasMcpToolNames.AiProfileActivate, "Activate and persist an AI connection without changing the global model or running jobs.", AgasMcpToolSchemas.AiProfileActivate, false, false, true, O<AgasMcpAiProfileActivatedDataDto>(), family: AgasMcpToolFamily.Ai, dispatch: AgasMcpToolDispatchMode.AsyncOrchestrated, commitPolicy: AgasMcpCommitPolicy.ShortAtomicCommit),
        D(AgasMcpToolNames.AiModelSet, "Set and persist the global image model without changing the active AI connection.", AgasMcpToolSchemas.AiModelSet, false, false, true, O<AgasMcpAiModelSetDataDto>(), family: AgasMcpToolFamily.Ai, dispatch: AgasMcpToolDispatchMode.AsyncOrchestrated, commitPolicy: AgasMcpCommitPolicy.ShortAtomicCommit),
        D(AgasMcpToolNames.AiDiagnostics, "Run a local safe AI configuration preflight without contacting a provider.", AgasMcpToolSchemas.AiDiagnostics, true, false, true, O<AgasMcpAiDiagnosticsDataDto>(), family: AgasMcpToolFamily.Ai, dispatch: AgasMcpToolDispatchMode.AsyncOrchestrated),
        D(AgasMcpToolNames.AiGenerate, "Submit an AI generation job using the current Q selection. layerScope selects reference image layers (default CurrentLayer); quality low/medium applies only to grok-imagine-image-2.0; prompts and results always use the current layer.", AgasMcpToolSchemas.AiGenerate, false, false, false, O<AgasMcpAiGenerateDataDto>(), family: AgasMcpToolFamily.Ai, openWorld: true),
        D(AgasMcpToolNames.AiStatus, "Read AGAS AI generation job status and safe result metadata.", AgasMcpToolSchemas.AiStatus, true, false, true, AgasMcpOutputSchemaBuilder.Build([typeof(AgasMcpAiJobDto), typeof(AgasMcpAiStatusListDataDto)]), family: AgasMcpToolFamily.Ai),
        D(AgasMcpToolNames.AiCancel, "Cancel an AGAS AI generation job idempotently.", AgasMcpToolSchemas.AiCancel, false, false, true, O<AgasMcpAiCancelDataDto>(), family: AgasMcpToolFamily.Ai),
        D(AgasMcpToolNames.AiApply, "Apply a succeeded AGAS AI job to its frozen canvas target without saving the project.", AgasMcpToolSchemas.AiApply, false, false, true, O<AgasMcpAiApplyDataDto>(), family: AgasMcpToolFamily.Ai),
    });

    private static readonly FrozenDictionary<string, AgasMcpToolDescriptor> ByName =
        All.ToFrozenDictionary(item => item.Name, StringComparer.Ordinal);

    static AgasMcpToolDescriptorRegistry()
    {
        if (!AgasMcpToolNames.All.SequenceEqual(All.Select(item => item.Name), StringComparer.Ordinal))
            throw new InvalidOperationException("MCP descriptors must match the official contract order exactly once.");
        if (!AgasMcpFrozenOutputManifest.V1.Tools.Select(tool => tool.ToolName)
                .SequenceEqual(All.Select(item => item.Name), StringComparer.Ordinal))
            throw new InvalidOperationException("MCP descriptors must match frozen output manifest V1 order.");

        foreach ((AgasMcpToolDescriptor descriptor, AgasMcpFrozenOutputTool frozen) in
                 All.Zip(AgasMcpFrozenOutputManifest.V1.Tools))
        {
            string[] actualSuccess = descriptor.OutputContract!.SuccessDataTypes.Select(type => type.Name).ToArray();
            string[] expectedSuccess = frozen.SuccessVariants.Select(shape => shape.Name).ToArray();
            if (!actualSuccess.SequenceEqual(expectedSuccess, StringComparer.Ordinal))
                throw new InvalidOperationException($"MCP output variants drifted for '{descriptor.Name}'.");
            string[] actualFailure = descriptor.OutputContract.FailureDataTypes.Select(type => type.Name).ToArray();
            string[] expectedFailure = frozen.FailureDataVariants.Select(shape => shape.Name).ToArray();
            if (!actualFailure.SequenceEqual(expectedFailure, StringComparer.Ordinal)
                || descriptor.OutputContract.AllowsFailureWithoutData != frozen.AllowsFailureWithoutData)
                throw new InvalidOperationException($"MCP failure output variants drifted for '{descriptor.Name}'.");
        }
    }

    public static AgasMcpToolDescriptor Get(string name) =>
        ByName.TryGetValue(name, out AgasMcpToolDescriptor? descriptor)
            ? descriptor
            : throw new KeyNotFoundException($"Unknown AGAS MCP tool '{name}'.");

    private static AgasMcpToolDescriptor D(
        string name,
        string description,
        JsonElement schema,
        bool readOnly,
        bool destructive,
        bool idempotent,
        AgasMcpOutputContract outputContract,
        AgasMcpToolFamily family = AgasMcpToolFamily.Project,
        AgasMcpToolDispatchMode dispatch = AgasMcpToolDispatchMode.MainThread,
        AgasMcpCommitPolicy commitPolicy = AgasMcpCommitPolicy.None,
        TimeSpan? timeout = null,
        bool openWorld = false) =>
        new(name, description, schema, readOnly, destructive, idempotent, openWorld, family, dispatch,
            timeout ?? TimeSpan.FromSeconds(30), commitPolicy, outputContract);

    private static AgasMcpOutputContract O<T>(IEnumerable<Type>? failureDataTypes = null) =>
        AgasMcpOutputSchemaBuilder.Build([typeof(T)], failureDataTypes);

}
