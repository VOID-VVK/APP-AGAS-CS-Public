using System.Text.Json;
using AppAgasCs.Mcp.Contracts.Runtime;

namespace AppAgasCs.Mcp.Contracts;

public static class AgasMcpToolSchemas
{
    public static JsonElement ProjectsRecent => Schema<AgasProjectsRecentInput>();
    public static JsonElement ProjectOpen => Schema<AgasProjectOpenInput>();
    public static JsonElement ProjectCreate => Schema<AgasProjectCreateInput>();
    public static JsonElement ProjectClose => Schema<AgasProjectCloseInput>();
    public static JsonElement ProjectSnapshot => Schema<AgasProjectSnapshotInput>();
    public static JsonElement AssetsList => Schema<AgasAssetsListInput>();
    public static JsonElement ImportsList => Schema<AgasImportsListInput>();
    public static JsonElement AssetsImport => Schema<AgasAssetsImportInput>();
    public static JsonElement SceneGet => Schema<AgasSceneGetInput>();
    public static JsonElement SceneMutate => Schema<AgasSceneMutateInput>();
    public static JsonElement ScenePackageImport => Schema<AgasScenePackageImportInput>();
    public static JsonElement ScenePackageExport => Schema<AgasScenePackageExportInput>();
    public static JsonElement GridMutate => Schema<AgasGridMutateInput>();
    public static JsonElement LayerMutate => Schema<AgasLayerMutateInput>();
    public static JsonElement LayerMergeVisible => Schema<AgasLayerMergeVisibleInput>();
    public static JsonElement ImageMutate => Schema<AgasImageMutateInput>();
    public static JsonElement PromptMutate => Schema<AgasPromptMutateInput>();
    public static JsonElement ViewSet => Schema<AgasViewSetInput>();
    public static JsonElement ProjectSave => Schema<AgasProjectSaveInput>();
    public static JsonElement SelectionSet => Schema<AgasSelectionSetInput>();
    public static JsonElement SelectionGet => Schema<AgasSelectionGetInput>();
    public static JsonElement AiProfilesList => Schema<AgasAiProfilesListInput>();
    public static JsonElement AiModelsList => Schema<AgasAiModelsListInput>();
    public static JsonElement AiProfileActivate => Schema<AgasAiProfileActivateInput>();
    public static JsonElement AiModelSet => Schema<AgasAiModelSetInput>();
    public static JsonElement AiDiagnostics => Schema<AgasAiDiagnosticsInput>();
    public static JsonElement AiGenerate => Schema<AgasAiGenerateInput>();
    public static JsonElement AiStatus => Schema<AgasAiStatusInput>();
    public static JsonElement AiCancel => Schema<AgasAiCancelInput>();
    public static JsonElement AiApply => Schema<AgasAiApplyInput>();

    private static JsonElement Schema<T>() where T : class =>
        AgasMcpContractRegistry.Get<T>().InputSchema;
}
