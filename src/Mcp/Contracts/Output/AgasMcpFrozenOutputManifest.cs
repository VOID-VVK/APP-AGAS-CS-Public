using System;
using System.Collections.Generic;

namespace AppAgasCs.Mcp.Contracts.Output;

/// <summary>
/// Hand-written Wave 1 oracle. This manifest must never be produced from CLR DTO reflection
/// or from the output schema builder that it is intended to audit.
/// </summary>
public static class AgasMcpFrozenOutputManifest
{
    private const string CanonicalCapabilityConstraint =
        "subset:textToImage|referenceEdit|regionEdit|nativeAlphaMask|guidedRegionEdit;" +
        "canonical-order:textToImage>referenceEdit>regionEdit>nativeAlphaMask>guidedRegionEdit";

    public static AgasMcpFrozenOutputManifestVersion V1 { get; } = new(
        "v1",
        BuildPrimitives(),
        BuildSharedShapes(),
        BuildTools());

    private static IReadOnlyList<AgasMcpFrozenOutputShape> BuildPrimitives() =>
    [
        Shape("safeId", [F("value", "string", constraint: "1..120;^[A-Za-z0-9_-]+$")]),
        Shape("providerId", [F("value", "string", constraint: "1..80;^[a-z0-9][a-z0-9._-]{0,79}$")]),
        Shape("jobId", [F("value", "string", constraint: "^aijob-[a-f0-9]{32}$")]),
        Shape("sourceRef", [F("value", "string", constraint: "^src1-[a-f0-9]{32}-[a-f0-9]{64}$;process-local;signed;15m expiry;no encoded path")]),
        Shape("relPath", [F("value", "string", constraint: "project-relative;/ separator;no rooted,dot-segment,NUL,backslash")]),
        Shape("utc", [F("value", "string", constraint: "RFC3339 date-time;Z or +00:00")]),
        Shape("i32", [F("value", "integer", constraint: "Int32;counts/order non-negative")]),
        Shape("i64", [F("value", "integer", constraint: "0..9007199254740991")]),
        Shape("f64", [F("value", "number", constraint: "finite")]),
    ];

    private static IReadOnlyList<AgasMcpFrozenOutputShape> BuildSharedShapes() =>
    [
        Shape("Error", [F("code", "string"), F("message", "string"), F("suggestion", "string")], "closed;stable AGAS_* code;no data"),
        Shape("Grid", [F("sizePx", "i32", constraint: "1..4096"), F("snapEnabled", "bool"), F("visible", "bool")], "closed"),
        Shape("ViewState", [F("cameraX", "f64"), F("cameraY", "f64"), F("zoom", "f64", constraint: ">0")], "closed"),
        Shape("SceneSummary", [F("sceneId", "safeId"), F("name", "string"), F("order", "i32"), F("loaded", "bool")], "closed;order,id ordinal"),
        Shape("LayerSummary", [F("sceneId", "safeId"), F("layerId", "safeId"), F("name", "string"), F("order", "i32"), F("visible", "bool"), F("locked", "bool"), F("shared", "bool"), F("imageCount", "i32"), F("promptCount", "i32")], "closed;order,id ordinal"),
        Shape("ImageSummary", [F("sceneId", "safeId"), F("layerId", "safeId"), F("imageId", "safeId"), F("imagePath", "relPath"), F("order", "i32"), F("x", "f64"), F("y", "f64"), F("scaleX", "f64"), F("scaleY", "f64"), F("rotationDegrees", "f64"), F("opacity", "f64", constraint: "0..1")], "closed;order,id ordinal"),
        Shape("PromptSummary", [F("sceneId", "safeId"), F("layerId", "safeId"), F("fragmentId", "safeId"), F("text", "string"), F("role", "string", constraint: "general|environment|negativeConstraint"), F("enabled", "bool"), F("order", "i32")], "closed;order,id ordinal"),
        Union("SceneItem", ["SceneItemLayer", "SceneItemImage", "SceneItemPrompt"], "closed discriminated union;type,order,id ordinal"),
        Shape("SceneItemLayer", [F("kind", "string", constraint: "const layer"), F("sceneId", "safeId"), F("layerId", "safeId"), F("name", "string"), F("order", "i32"), F("visible", "bool"), F("locked", "bool"), F("shared", "bool"), F("imageCount", "i32"), F("promptCount", "i32")], "closed"),
        Shape("SceneItemImage", [F("kind", "string", constraint: "const image"), F("sceneId", "safeId"), F("layerId", "safeId"), F("imageId", "safeId"), F("imagePath", "relPath"), F("order", "i32"), F("x", "f64"), F("y", "f64"), F("scaleX", "f64"), F("scaleY", "f64"), F("rotationDegrees", "f64"), F("opacity", "f64", constraint: "0..1")], "closed"),
        Shape("SceneItemPrompt", [F("kind", "string", constraint: "const prompt"), F("sceneId", "safeId"), F("layerId", "safeId"), F("fragmentId", "safeId"), F("text", "string"), F("role", "string", constraint: "general|environment|negativeConstraint"), F("enabled", "bool"), F("order", "i32")], "closed"),
        Shape("AssetItem", [F("relativePath", "relPath"), F("extension", "string"), F("sizeBytes", "i64"), F("modifiedUnixTime", "i64")], "closed;path ordinal"),
        Shape("ImportItem", [F("kind", "string", constraint: "asset|scenePackage"), F("name", "string"), F("sizeBytes", "i64"), F("mediaType", "string"), F("validationState", "string", constraint: "unverified|invalid"), F("sourceRef", "sourceRef")], "closed;lightweight;no path/base64/manifest/metadata"),
        Shape("Region", [F("column", "i32"), F("row", "i32"), F("width", "i32", constraint: ">=1"), F("height", "i32", constraint: ">=1")], "closed"),
        Shape("ExecutionIdentity", [F("profileId", "safeId"), F("providerId", "providerId"), F("adapterKind", "string"), F("modelName", "string")], "closed;submission-time safe projection"),
        Shape("Diagnostic", [F("diagnosticId", "string"), F("errorCode", "string"), F("phase", "string"), F("category", "string"), F("httpStatus", "i32", false), F("upstreamCode", "string", false)], "closed;safe allow-list"),
        Shape("SaveFailure", [F("path", "relPath"), F("code", "string"), F("message", "string")], "closed;path ordinal"),
        Shape("AiIssue", [F("code", "string"), F("message", "string"), F("suggestion", "string")], "closed;code ordinal"),
        Shape("RecentProject", [F("projectRef", "string", constraint: "^projectref-[a-f0-9]{32}$"), F("name", "string"), F("lastOpenedUnixTime", "i64"), F("available", "bool"), F("isOpen", "bool")], "closed"),
        Shape("EffectiveProfile", [F("profileId", "safeId"), F("providerId", "providerId"), F("adapterKind", "string"), F("modelName", "string"), F("source", "string", constraint: "liveUi|persisted")], "closed;safe projected"),
        Shape("AiProfileRow", [F("profileId", "safeId"), F("name", "string"), F("providerId", "providerId"), F("providerDisplayName", "string"), F("modelName", "string"), F("credentialConfigured", "bool"), F("providerAvailable", "bool"), F("capabilities", "string[]", constraint: CanonicalCapabilityConstraint)], "closed;profileId ordinal"),
        Shape("AiModelRow", [F("modelName", "string"), F("label", "string"), F("recommended", "bool")], "closed;modelName ordinal;safe projected"),
        Shape("AiProviderRow", [F("providerId", "providerId"), F("displayName", "string"), F("adapterKind", "string"), F("defaultModelName", "string"), F("capabilities", "string[]", constraint: CanonicalCapabilityConstraint), F("supportsCustomModel", "bool"), F("models", "AiModelRow[]")], "closed;providerId ordinal"),
        Shape("AiJobQueued", AiJobCommon([F("state", "string", constraint: "const Queued"), F("alreadyApplied", "bool", constraint: "const false")]), "closed"),
        Shape("AiJobRunning", AiJobCommon([F("state", "string", constraint: "const Running"), F("alreadyApplied", "bool", constraint: "const false"), F("startedAtUtc", "utc")]), "closed"),
        Shape("AiJobSucceeded", AiJobCommon([F("state", "string", constraint: "const Succeeded"), F("alreadyApplied", "bool"), F("startedAtUtc", "utc"), F("completedAtUtc", "utc"), F("resultRelativePath", "relPath"), F("message", "string"), F("previewRelativePath", "relPath", false), F("appliedRelativePath", "relPath", false), F("appliedAtUtc", "utc", false)]), "closed;applied fields only when alreadyApplied=true"),
        Shape("AiJobFailed", AiJobCommon([F("state", "string", constraint: "const Failed"), F("alreadyApplied", "bool", constraint: "const false"), F("completedAtUtc", "utc"), F("errorCode", "string"), F("message", "string"), F("diagnostic", "Diagnostic"), F("startedAtUtc", "utc", false), F("hint", "string", false)]), "closed"),
        Shape("AiJobCanceled", AiJobCommon([F("state", "string", constraint: "const Canceled"), F("alreadyApplied", "bool", constraint: "const false"), F("completedAtUtc", "utc"), F("startedAtUtc", "utc", false)]), "closed"),
    ];

    private static IReadOnlyList<AgasMcpFrozenOutputTool> BuildTools()
    {
        AgasMcpFrozenOutputShape selection = Shape("AgasMcpSelectionDataDto", [
            F("sceneId", "safeId"), F("activeRegionTool", "string", constraint: "Edit|Selection|Replacement"), F("gridSizePx", "i32"), F("canEditCurrentLayer", "bool"),
            F("selection", "Region", false), F("replacement", "Region", false), F("effectiveGenerationRegion", "Region", false), F("selectedImageId", "safeId", false), F("selectedLayerId", "safeId", false)], "closed");
        AgasMcpFrozenOutputShape close = Shape("AgasMcpProjectCloseDataDto", [F("projectStillOpen", "bool"), F("saved", "bool"), F("aiCancellationRequested", "bool"), F("saveState", "string", constraint: "none|partial|complete")], "closed;immutable receipt");

        return
        [
            Tool("agas.projects.recent", Shape("AgasMcpRecentProjectsDataDto", [F("projects", "RecentProject[]")], "lastOpened desc,name/id ordinal;one ref per root")),
            Tool("agas.project.open", Shape("AgasMcpProjectOpenDataDto", [F("projectRef", "string", constraint: "^projectref-[a-f0-9]{32}$"), F("name", "string"), F("isOpen", "bool")], "no path")),
            Tool("agas.project.create", Shape("AgasMcpProjectCreateDataDto", [F("projectRef", "string", constraint: "^projectref-[a-f0-9]{32}$"), F("name", "string"), F("isOpen", "bool")], "no root/path")),
            new AgasMcpFrozenOutputTool("agas.project.close", [close], [close], true, "plain infrastructure failure or typed handler-started failure"),
            Tool("agas.project.snapshot", Shape("AgasMcpProjectSnapshotDataDto", [F("projectName", "string"), F("grid", "Grid"), F("scenes", "SceneSummary[]"), F("layers", "LayerSummary[]"), F("images", "ImageSummary[]"), F("prompts", "PromptSummary[]"), F("dirtyProject", "bool"), F("dirtyLayout", "bool"), F("dirtySceneIds", "safeId[]"), F("pendingDeletedSceneIds", "safeId[]"), F("activeSceneId", "safeId", false)], "stable summary/id order")),
            Tool("agas.assets.list", Shape("AgasMcpAssetsListDataDto", [F("items", "AssetItem[]"), F("count", "i32"), F("totalCount", "i32"), F("hasMore", "bool"), F("nextOffset", "i32", false)], "path ordinal;nextOffset only when hasMore")),
            Tool("agas.imports.list", Shape("AgasMcpImportsListDataDto", [F("items", "ImportItem[]"), F("count", "i32"), F("totalCount", "i32"), F("hasMore", "bool"), F("nextOffset", "i32", false)], "lightweight;stable kind/name order;nextOffset only when hasMore")),
            Tool("agas.assets.import", Shape("AgasMcpAssetImportDataDto", [F("relativePath", "relPath", constraint: "assets/imported/ only"), F("extension", "string"), F("sizeBytes", "i64"), F("pixelWidth", "i32"), F("pixelHeight", "i32")])),
            Tool("agas.scene.get", Shape("AgasMcpSceneGetDataDto", [F("sceneId", "safeId"), F("name", "string"), F("viewState", "ViewState"), F("items", "SceneItem[]"), F("activeLayerId", "safeId", false), F("nextCursor", "string", false, "^scncur-[a-f0-9]{32}$"), F("readme", "string", false)], "type/order/id;cursor scope;readme is the scene playbook")),
            Tool("agas.scene.mutate", Shape("AgasMcpSceneMutationDataDto", [F("scenes", "SceneSummary[]"), F("activeSceneId", "safeId"), F("changedSceneId", "safeId"), F("dirtyProject", "bool"), F("dirtyLayout", "bool"), F("dirtySceneIds", "safeId[]"), F("pendingDeletedSceneIds", "safeId[]")], "stable orders")),
            Tool("agas.scene.package.import", Shape("AgasMcpScenePackageImportDataDto", [F("sceneId", "safeId"), F("name", "string"), F("copiedAssetRelativePaths", "relPath[]"), F("dirtySceneIds", "safeId[]")], "ordinal;journal-owned paths")),
            Tool("agas.scene.package.export", Shape("AgasMcpScenePackageExportDataDto", [F("packageRef", "string"), F("sourceRef", "sourceRef"), F("referenceImageCount", "i32")], "global exchange publish;no path")),
            Tool("agas.grid.mutate", Shape("AgasMcpGridMutationDataDto", [F("grid", "Grid"), F("dirtyProject", "bool"), F("dirtyLayout", "bool"), F("dirtySceneIds", "safeId[]")], "dirty ids ordinal")),
            Tool("agas.layer.mutate", Shape("AgasMcpLayerMutationDataDto", [F("sceneId", "safeId"), F("layers", "LayerSummary[]"), F("currentLayerId", "safeId"), F("changedLayerId", "safeId"), F("dirtyProject", "bool"), F("dirtyLayout", "bool"), F("dirtySceneIds", "safeId[]")], "layer/order;dirty ids ordinal")),
            Tool("agas.layer.mergeVisible", Shape("AgasMcpLayerMergeVisibleDataDto", [F("sceneId", "safeId"), F("layerId", "safeId"), F("image", "ImageSummary"), F("sourceLayerIds", "safeId[]"), F("dirtySceneIds", "safeId[]")], "other visible layers only; current layer excluded")),
            Tool("agas.image.mutate", Shape("AgasMcpImageMutationDataDto", [F("sceneId", "safeId"), F("layerId", "safeId"), F("layer", "LayerSummary"), F("dirtySceneIds", "safeId[]"), F("image", "ImageSummary", false)], "image only non-delete")),
            Tool("agas.prompt.mutate", Shape("AgasMcpPromptMutationDataDto", [F("sceneId", "safeId"), F("layerId", "safeId"), F("fragments", "PromptSummary[]"), F("mergedPreview", "string"), F("changedFragmentId", "safeId"), F("dirtySceneIds", "safeId[]")], "prompt role lower camel;stable order")),
            Tool("agas.view.set", Shape("AgasMcpViewSetDataDto", [F("sceneId", "safeId"), F("cameraX", "f64"), F("cameraY", "f64"), F("zoom", "f64"), F("dirtyProject", "bool"), F("dirtyLayout", "bool"), F("dirtySceneIds", "safeId[]")])),
            Tool("agas.project.save", Shape("AgasMcpProjectSaveDataDto", [F("saveState", "string", constraint: "none|partial|complete"), F("savedFiles", "relPath[]"), F("failedFiles", "SaveFailure[]"), F("pendingDirtyFiles", "relPath[]")], "immutable staged receipt;path ordinal")),
            Tool("agas.selection.set", selection),
            Tool("agas.selection.get", selection with { Name = "AgasMcpSelectionGetDataDto" }),
            Tool("agas.ai.profiles.list", Shape("AgasMcpAiProfilesDataDto", [F("profiles", "AiProfileRow[]"), F("providerConfigurationErrorCount", "i32"), F("activeProfileId", "safeId", false), F("effectiveCurrent", "EffectiveProfile", false)], "profileId ordinal")),
            Tool("agas.ai.models.list", Shape("AgasMcpAiModelsDataDto", [F("providers", "AiProviderRow[]"), F("providerConfigurationErrorCount", "i32")], "providerId/modelName ordinal")),
            Tool("agas.ai.profile.activate", Shape("AgasMcpAiProfileActivatedDataDto", [F("profileId", "safeId"), F("providerId", "providerId"), F("modelName", "string"), F("credentialConfigured", "bool"), F("persisted", "bool"), F("uiSynchronized", "bool")], "safe projected;no BaseUrl")),
            Tool("agas.ai.model.set", Shape("AgasMcpAiModelSetDataDto", [F("profileId", "safeId"), F("providerId", "providerId"), F("modelName", "string"), F("knownPreset", "bool"), F("credentialConfigured", "bool"), F("persisted", "bool"), F("uiSynchronized", "bool")], "safe projected;no BaseUrl")),
            Tool("agas.ai.diagnostics", Shape("AgasMcpAiDiagnosticsDataDto", [F("profileId", "safeId"), F("providerId", "providerId"), F("modelName", "string"), F("credentialConfigured", "bool"), F("providerAvailable", "bool"), F("ready", "bool"), F("networkChecked", "bool", constraint: "const false"), F("capabilities", "string[]", constraint: CanonicalCapabilityConstraint), F("issues", "AiIssue[]")], "issues code ordinal")),
            Tool("agas.ai.generate", Shape("AgasMcpAiGenerateDataDto", [F("jobId", "jobId"), F("state", "string", constraint: "const Queued"), F("effectiveOperation", "string", constraint: "TextToImage|ReferenceEdit|MaskedEdit|GuidedRegionEdit"), F("execution", "ExecutionIdentity"), F("createdAtUtc", "utc")], "submission-time identity")),
            new AgasMcpFrozenOutputTool("agas.ai.status", [
                Union("AgasMcpAiJobDto", ["AiJobQueued", "AiJobRunning", "AiJobSucceeded", "AiJobFailed", "AiJobCanceled"], "direct root;no job wrapper"),
                Shape("AgasMcpAiStatusListDataDto", [F("jobs", "AgasMcpAiJobDto[]")], "createdAt desc,jobId ordinal")]),
            Tool("agas.ai.cancel", Shape("AgasMcpAiCancelDataDto", [F("jobId", "jobId"), F("state", "string", constraint: "Queued|Running|Succeeded|Failed|Canceled"), F("canceled", "bool")])),
            Tool("agas.ai.apply", Shape("AgasMcpAiApplyDataDto", [F("jobId", "jobId"), F("state", "string", constraint: "const Succeeded"), F("alreadyApplied", "bool"), F("appliedRelativePath", "relPath"), F("message", "string")])),
        ];
    }

    private static IReadOnlyList<AgasMcpFrozenOutputField> AiJobCommon(
        IReadOnlyList<AgasMcpFrozenOutputField> stateFields)
    {
        var fields = new List<AgasMcpFrozenOutputField>
        {
            F("jobId", "jobId"),
        };
        fields.Add(stateFields[0]);
        fields.Add(F("effectiveOperation", "string", constraint: "TextToImage|ReferenceEdit|MaskedEdit|GuidedRegionEdit"));
        fields.Add(F("execution", "ExecutionIdentity"));
        fields.Add(F("createdAtUtc", "utc"));
        for (int index = 1; index < stateFields.Count; index++)
        {
            fields.Add(stateFields[index]);
        }
        return fields.AsReadOnly();
    }

    private static AgasMcpFrozenOutputField F(
        string name,
        string type,
        bool required = true,
        string? constraint = null) =>
        new(name, type, required, constraint);

    private static AgasMcpFrozenOutputShape Shape(
        string name,
        IReadOnlyList<AgasMcpFrozenOutputField> fields,
        string? constraint = "closed") =>
        new(name, fields, constraint);

    private static AgasMcpFrozenOutputShape Union(
        string name,
        IReadOnlyList<string> variants,
        string? constraint = "closed union") =>
        new(name, Array.Empty<AgasMcpFrozenOutputField>(), constraint, variants);

    private static AgasMcpFrozenOutputTool Tool(string name, AgasMcpFrozenOutputShape shape) =>
        new(name, [shape]);
}
