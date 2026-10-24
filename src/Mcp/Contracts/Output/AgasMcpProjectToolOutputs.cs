using System;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AppAgasCs.Mcp.Contracts.Output;

internal static class AgasMcpOutputPatterns
{
    public const string SafeId = "^[A-Za-z0-9_-]+$";
    public const string ProjectRef = "^projectref-[a-f0-9]{32}$";
    public const string SourceRef = "^src1-[a-f0-9]{32}-[a-f0-9]{64}$";
    public const string SceneCursor = "^scncur-[a-f0-9]{32}$";
}

[AgasMcpOutputObject]
public sealed record AgasMcpRecentProjectDto(
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.ProjectRef)] string ProjectRef,
    string Name,
    long LastOpenedUnixTime,
    bool Available,
    bool IsOpen);

[AgasMcpOutputObject]
public sealed record AgasMcpRecentProjectsDataDto(ImmutableArray<AgasMcpRecentProjectDto> Projects);

[AgasMcpOutputObject]
public sealed record AgasMcpProjectOpenDataDto(
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.ProjectRef)] string ProjectRef,
    string Name,
    bool IsOpen);

[AgasMcpOutputObject]
public sealed record AgasMcpProjectCreateDataDto(
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.ProjectRef)] string ProjectRef,
    string Name,
    bool IsOpen);

[AgasMcpOutputObject]
public sealed record AgasMcpProjectCloseDataDto(
    bool ProjectStillOpen,
    bool Saved,
    bool AiCancellationRequested,
    [property: AgasMcpOutputAllowedValues("none", "partial", "complete")] string SaveState);

[AgasMcpOutputObject]
public sealed record AgasMcpProjectSnapshotDataDto(
    string ProjectName,
    AgasMcpGridDto Grid,
    ImmutableArray<AgasMcpSceneSummaryDto> Scenes,
    ImmutableArray<AgasMcpLayerSummaryDto> Layers,
    ImmutableArray<AgasMcpImageSummaryDto> Images,
    ImmutableArray<AgasMcpPromptSummaryDto> Prompts,
    bool DirtyProject,
    bool DirtyLayout,
    ImmutableArray<string> DirtySceneIds,
    ImmutableArray<string> PendingDeletedSceneIds,
    [property: AgasMcpOutputOptional]
    [property: AgasMcpOutputString(MinimumLength = 1, MaximumLength = 120, Pattern = AgasMcpOutputPatterns.SafeId)] string? ActiveSceneId);

[AgasMcpOutputObject]
public sealed record AgasMcpAssetsListDataDto(
    ImmutableArray<AgasMcpAssetItemDto> Items,
    int Count,
    int TotalCount,
    bool HasMore,
    [property: AgasMcpOutputOptional] int? NextOffset);

[AgasMcpOutputObject]
public sealed record AgasMcpImportsListDataDto(
    ImmutableArray<AgasMcpImportItemDto> Items,
    int Count,
    int TotalCount,
    bool HasMore,
    [property: AgasMcpOutputOptional] int? NextOffset);

[AgasMcpOutputObject]
public sealed record AgasMcpImportItemDto(
    [property: AgasMcpOutputAllowedValues("asset", "scenePackage")] string Kind,
    string Name,
    long SizeBytes,
    string MediaType,
    [property: AgasMcpOutputAllowedValues("unverified", "invalid")] string ValidationState,
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SourceRef)] string SourceRef);

[AgasMcpOutputObject]
public sealed record AgasMcpAssetImportDataDto(
    string RelativePath,
    string Extension,
    long SizeBytes,
    [property: AgasMcpOutputNumber(Minimum = 1, Maximum = 8192)] int PixelWidth,
    [property: AgasMcpOutputNumber(Minimum = 1, Maximum = 8192)] int PixelHeight);

[AgasMcpOutputUnion(typeof(AgasMcpSceneLayerItemDto), typeof(AgasMcpSceneImageItemDto), typeof(AgasMcpScenePromptItemDto))]
[JsonPolymorphic]
[JsonDerivedType(typeof(AgasMcpSceneLayerItemDto))]
[JsonDerivedType(typeof(AgasMcpSceneImageItemDto))]
[JsonDerivedType(typeof(AgasMcpScenePromptItemDto))]
public abstract record AgasMcpSceneItemDto;

[AgasMcpOutputObject]
public sealed record AgasMcpSceneLayerItemDto(
    [property: AgasMcpOutputConst("layer")] string Kind,
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string SceneId,
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string LayerId,
    string Name,
    [property: AgasMcpOutputNumber(Minimum = 0)] int Order,
    bool Visible,
    bool Locked,
    bool Shared,
    [property: AgasMcpOutputNumber(Minimum = 0)] int ImageCount,
    [property: AgasMcpOutputNumber(Minimum = 0)] int PromptCount) : AgasMcpSceneItemDto;

[AgasMcpOutputObject]
public sealed record AgasMcpSceneImageItemDto(
    [property: AgasMcpOutputConst("image")] string Kind,
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string SceneId,
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string LayerId,
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string ImageId,
    string ImagePath,
    [property: AgasMcpOutputNumber(Minimum = 0)] int Order,
    double X,
    double Y,
    double ScaleX,
    double ScaleY,
    double RotationDegrees,
    [property: AgasMcpOutputNumber(Minimum = 0, Maximum = 1)] double Opacity) : AgasMcpSceneItemDto;

[AgasMcpOutputObject]
public sealed record AgasMcpScenePromptItemDto(
    [property: AgasMcpOutputConst("prompt")] string Kind,
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string SceneId,
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string LayerId,
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string FragmentId,
    string Text,
    [property: AgasMcpOutputAllowedValues("general", "environment", "negativeConstraint")] string Role,
    bool Enabled,
    [property: AgasMcpOutputNumber(Minimum = 0)] int Order) : AgasMcpSceneItemDto;

[AgasMcpOutputObject]
public sealed record AgasMcpViewStateDto(double CameraX, double CameraY, [property: AgasMcpOutputNumber(Minimum = double.Epsilon)] double Zoom);

[AgasMcpOutputObject]
public sealed record AgasMcpSceneGetDataDto(
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string SceneId,
    string Name,
    AgasMcpViewStateDto ViewState,
    ImmutableArray<AgasMcpSceneItemDto> Items,
    [property: AgasMcpOutputOptional]
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string? ActiveLayerId,
    [property: AgasMcpOutputOptional]
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SceneCursor)] string? NextCursor,
    [property: AgasMcpOutputOptional]
    string? Readme = null);

[AgasMcpOutputObject]
public sealed record AgasMcpSceneMutationDataDto(
    ImmutableArray<AgasMcpSceneSummaryDto> Scenes,
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string ActiveSceneId,
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string ChangedSceneId,
    bool DirtyProject,
    bool DirtyLayout,
    ImmutableArray<string> DirtySceneIds,
    ImmutableArray<string> PendingDeletedSceneIds);

[AgasMcpOutputObject]
public sealed record AgasMcpScenePackageImportDataDto(
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string SceneId,
    string Name,
    ImmutableArray<string> CopiedAssetRelativePaths,
    ImmutableArray<string> DirtySceneIds);

[AgasMcpOutputObject]
public sealed record AgasMcpScenePackageExportDataDto(
    string PackageRef,
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SourceRef)] string SourceRef,
    [property: AgasMcpOutputNumber(Minimum = 0)] int ReferenceImageCount);

[AgasMcpOutputObject]
public sealed record AgasMcpGridMutationDataDto(
    AgasMcpGridDto Grid,
    bool DirtyProject,
    bool DirtyLayout,
    ImmutableArray<string> DirtySceneIds);

[AgasMcpOutputObject]
public sealed record AgasMcpLayerMutationDataDto(
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string SceneId,
    ImmutableArray<AgasMcpLayerSummaryDto> Layers,
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string CurrentLayerId,
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string ChangedLayerId,
    bool DirtyProject,
    bool DirtyLayout,
    ImmutableArray<string> DirtySceneIds);

[AgasMcpOutputObject]
public sealed record AgasMcpLayerMergeVisibleDataDto(
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string SceneId,
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string LayerId,
    AgasMcpImageSummaryDto Image,
    ImmutableArray<string> SourceLayerIds,
    ImmutableArray<string> DirtySceneIds);

[AgasMcpOutputObject]
public sealed record AgasMcpImageMutationDataDto(
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string SceneId,
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string LayerId,
    AgasMcpLayerSummaryDto Layer,
    ImmutableArray<string> DirtySceneIds,
    [property: AgasMcpOutputOptional] AgasMcpImageSummaryDto? Image);

[AgasMcpOutputObject]
public sealed record AgasMcpPromptMutationDataDto(
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string SceneId,
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string LayerId,
    ImmutableArray<AgasMcpPromptSummaryDto> Fragments,
    string MergedPreview,
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string ChangedFragmentId,
    ImmutableArray<string> DirtySceneIds);

[AgasMcpOutputObject]
public sealed record AgasMcpViewSetDataDto(
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string SceneId,
    double CameraX,
    double CameraY,
    [property: AgasMcpOutputNumber(Minimum = double.Epsilon)] double Zoom,
    bool DirtyProject,
    bool DirtyLayout,
    ImmutableArray<string> DirtySceneIds);

[AgasMcpOutputObject]
public sealed record AgasMcpProjectSaveDataDto(
    [property: AgasMcpOutputAllowedValues("none", "partial", "complete")] string SaveState,
    ImmutableArray<string> SavedFiles,
    ImmutableArray<AgasMcpSaveFailureDto> FailedFiles,
    ImmutableArray<string> PendingDirtyFiles);

[AgasMcpOutputObject]
public record AgasMcpSelectionDataDto(
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string SceneId,
    [property: AgasMcpOutputAllowedValues("Edit", "Selection", "Replacement")] string ActiveRegionTool,
    [property: AgasMcpOutputNumber(Minimum = 1, Maximum = 4096)] int GridSizePx,
    bool CanEditCurrentLayer,
    [property: AgasMcpOutputOptional] AgasMcpGridRegionDto? Selection,
    [property: AgasMcpOutputOptional] AgasMcpGridRegionDto? Replacement,
    [property: AgasMcpOutputOptional] AgasMcpGridRegionDto? EffectiveGenerationRegion,
    [property: AgasMcpOutputOptional]
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string? SelectedImageId,
    [property: AgasMcpOutputOptional]
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string? SelectedLayerId);

[AgasMcpOutputObject]
public sealed record AgasMcpSelectionGetDataDto(
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string SceneId,
    [property: AgasMcpOutputAllowedValues("Edit", "Selection", "Replacement")] string ActiveRegionTool,
    [property: AgasMcpOutputNumber(Minimum = 1, Maximum = 4096)] int GridSizePx,
    bool CanEditCurrentLayer,
    [property: AgasMcpOutputOptional] AgasMcpGridRegionDto? Selection,
    [property: AgasMcpOutputOptional] AgasMcpGridRegionDto? Replacement,
    [property: AgasMcpOutputOptional] AgasMcpGridRegionDto? EffectiveGenerationRegion,
    [property: AgasMcpOutputOptional]
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string? SelectedImageId,
    [property: AgasMcpOutputOptional]
    [property: AgasMcpOutputString(Pattern = AgasMcpOutputPatterns.SafeId)] string? SelectedLayerId);
