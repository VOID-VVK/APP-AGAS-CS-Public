using System;
using System.Collections.Generic;

namespace AppAgasCs.Mcp.Contracts;

public static class AgasMcpToolNames
{
    public const string ProjectsRecent = "agas.projects.recent";
    public const string ProjectOpen = "agas.project.open";
    public const string ProjectCreate = "agas.project.create";
    public const string ProjectClose = "agas.project.close";
    public const string ProjectSnapshot = "agas.project.snapshot";
    public const string AssetsList = "agas.assets.list";
    public const string ImportsList = "agas.imports.list";
    public const string AssetsImport = "agas.assets.import";
    public const string SceneGet = "agas.scene.get";
    public const string SceneMutate = "agas.scene.mutate";
    public const string ScenePackageImport = "agas.scene.package.import";
    public const string ScenePackageExport = "agas.scene.package.export";
    public const string GridMutate = "agas.grid.mutate";
    public const string LayerMutate = "agas.layer.mutate";
    public const string LayerMergeVisible = "agas.layer.mergeVisible";
    public const string ImageMutate = "agas.image.mutate";
    public const string PromptMutate = "agas.prompt.mutate";
    public const string ViewSet = "agas.view.set";
    public const string ProjectSave = "agas.project.save";
    public const string SelectionSet = "agas.selection.set";
    public const string SelectionGet = "agas.selection.get";
    public const string AiProfilesList = "agas.ai.profiles.list";
    public const string AiModelsList = "agas.ai.models.list";
    public const string AiProfileActivate = "agas.ai.profile.activate";
    public const string AiModelSet = "agas.ai.model.set";
    public const string AiDiagnostics = "agas.ai.diagnostics";
    public const string AiGenerate = "agas.ai.generate";
    public const string AiStatus = "agas.ai.status";
    public const string AiCancel = "agas.ai.cancel";
    public const string AiApply = "agas.ai.apply";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[]
    {
        ProjectsRecent,
        ProjectOpen,
        ProjectCreate,
        ProjectClose,
        ProjectSnapshot,
        AssetsList,
        ImportsList,
        AssetsImport,
        SceneGet,
        SceneMutate,
        ScenePackageImport,
        ScenePackageExport,
        GridMutate,
        LayerMutate,
        LayerMergeVisible,
        ImageMutate,
        PromptMutate,
        ViewSet,
        ProjectSave,
        SelectionSet,
        SelectionGet,
        AiProfilesList,
        AiModelsList,
        AiProfileActivate,
        AiModelSet,
        AiDiagnostics,
        AiGenerate,
        AiStatus,
        AiCancel,
        AiApply,
    });
}
