using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using AppAgasCs.Mcp.Contracts.Metadata;

namespace AppAgasCs.Mcp.Contracts;

internal static class AgasMcpContractPatterns
{
    public const string SafeIdentifier = "^[A-Za-z0-9_-]+$";
    public const string AiJobId = "^aijob-[a-f0-9]{32}$";
    public const string ProjectRef = "^projectref-[a-f0-9]{32}$";
    public const string SceneCursor = "^scncur-[a-f0-9]{32}$";
    public const string SourceRef = "^src1-[a-f0-9]{32}-[a-f0-9]{64}$";
}

[McpClosedObject]
public sealed record AgasProjectsRecentInput(
    [property: DefaultValue(20)]
    [property: Range(1, 20)]
    int Limit = 20);

[McpClosedObject]
public sealed record AgasProjectOpenInput(
    [property: Required]
    [property: RegularExpression(AgasMcpContractPatterns.ProjectRef)]
    string ProjectRef);

[McpClosedObject]
public sealed record AgasProjectCreateInput(
    [property: Required]
    [property: StringLength(80, MinimumLength = 1)]
    string Name);

[McpClosedObject]
public sealed record AgasProjectCloseInput(
    [property: DefaultValue(false)] bool SaveIfDirty = false,
    [property: DefaultValue(false)] bool CancelAiJobs = false);

[McpClosedObject]
public sealed record AgasProjectSnapshotInput(
    [property: DefaultValue(true)] bool IncludeScenes = true,
    [property: DefaultValue(true)] bool IncludeLayers = true,
    [property: DefaultValue(false)] bool IncludeImages = false,
    [property: DefaultValue(false)] bool IncludePrompts = false);

[McpClosedObject]
public sealed record AgasAssetsListInput(
    [property: DefaultValue("")]
    [property: MaxLength(200)]
    string Query = "",

    [property: DefaultValue(new string[] { "png", "jpg", "jpeg", "webp" })]
    [property: MinLength(0)]
    [property: MaxLength(4)]
    [property: McpUniqueItems]
    [property: McpItemsAllowedValues("png", "jpg", "jpeg", "webp")]
    IReadOnlyList<string>? Extensions = null,

    [property: DefaultValue(50)]
    [property: Range(1, 200)]
    int Limit = 50,

    [property: DefaultValue(0)]
    [property: Range(0, 1_000_000)]
    int Offset = 0);

[McpClosedObject(IncludeSchemaKeyword = true)]
[McpWhenEqualsRequired(nameof(AgasSceneMutateInput.Op), "add", nameof(AgasSceneMutateInput.Name))]
[McpWhenEqualsRequired(nameof(AgasSceneMutateInput.Op), "rename", nameof(AgasSceneMutateInput.SceneId), nameof(AgasSceneMutateInput.Name))]
[McpWhenEqualsRequired(nameof(AgasSceneMutateInput.Op), "delete", nameof(AgasSceneMutateInput.SceneId))]
[McpWhenEqualsRequired(nameof(AgasSceneMutateInput.Op), "setActive", nameof(AgasSceneMutateInput.SceneId))]
[McpWhenEqualsRequired(nameof(AgasSceneMutateInput.Op), "setReadme", nameof(AgasSceneMutateInput.SceneId), nameof(AgasSceneMutateInput.Readme))]
public sealed record AgasSceneMutateInput(
    [property: Required]
    [property: AllowedValues("add", "rename", "delete", "setActive", "setReadme")]
    string Op,

    [property: StringLength(120, MinimumLength = 1)]
    [property: RegularExpression(AgasMcpContractPatterns.SafeIdentifier)]
    string? SceneId = null,

    [property: StringLength(120, MinimumLength = 1)]
    string? Name = null,

    [property: StringLength(8000, MinimumLength = 0)]
    string? Readme = null);

[McpClosedObject(IncludeSchemaKeyword = true)]
[McpWhenEqualsRequired(nameof(AgasLayerMutateInput.Op), "add", nameof(AgasLayerMutateInput.Name))]
[McpWhenEqualsRequired(nameof(AgasLayerMutateInput.Op), "rename", nameof(AgasLayerMutateInput.LayerId), nameof(AgasLayerMutateInput.Name))]
[McpWhenEqualsRequired(nameof(AgasLayerMutateInput.Op), "delete", nameof(AgasLayerMutateInput.LayerId))]
[McpWhenEqualsRequired(nameof(AgasLayerMutateInput.Op), "setCurrent", nameof(AgasLayerMutateInput.LayerId))]
[McpWhenEqualsRequired(nameof(AgasLayerMutateInput.Op), "move", nameof(AgasLayerMutateInput.LayerId), nameof(AgasLayerMutateInput.Direction))]
[McpWhenEqualsRequired(nameof(AgasLayerMutateInput.Op), "setVisible", nameof(AgasLayerMutateInput.LayerId), nameof(AgasLayerMutateInput.Visible))]
[McpWhenEqualsRequired(nameof(AgasLayerMutateInput.Op), "setLocked", nameof(AgasLayerMutateInput.LayerId), nameof(AgasLayerMutateInput.Locked))]
[McpWhenEqualsRequired(nameof(AgasLayerMutateInput.Op), "setShared", nameof(AgasLayerMutateInput.LayerId), nameof(AgasLayerMutateInput.Shared))]
public sealed record AgasLayerMutateInput(
    [property: Required]
    [property: AllowedValues("add", "rename", "delete", "move", "setVisible", "setLocked", "setShared", "setCurrent")]
    string Op,

    [property: Required]
    [property: StringLength(120, MinimumLength = 1)]
    [property: RegularExpression(AgasMcpContractPatterns.SafeIdentifier)]
    string SceneId,

    [property: StringLength(120, MinimumLength = 1)]
    [property: RegularExpression(AgasMcpContractPatterns.SafeIdentifier)]
    string? LayerId = null,

    [property: StringLength(120, MinimumLength = 1)]
    string? Name = null,

    [property: AllowedValues(-1, 1)]
    int? Direction = null,

    bool? Visible = null,
    bool? Locked = null,
    bool? Shared = null);

[McpClosedObject]
public sealed record AgasLayerMergeVisibleInput(
    [property: Required]
    [property: StringLength(120, MinimumLength = 1)]
    [property: RegularExpression(AgasMcpContractPatterns.SafeIdentifier)]
    string SceneId,

    [property: Required]
    [property: StringLength(120, MinimumLength = 1)]
    [property: RegularExpression(AgasMcpContractPatterns.SafeIdentifier)]
    string LayerId);

[McpClosedObject(IncludeSchemaKeyword = true)]
[McpWhenEqualsRequired(nameof(AgasImageMutateInput.Op), "add", nameof(AgasImageMutateInput.ImagePath))]
[McpWhenEqualsRequired(nameof(AgasImageMutateInput.Op), "delete", nameof(AgasImageMutateInput.ImageId))]
[McpWhenEqualsRequired(nameof(AgasImageMutateInput.Op), "move", nameof(AgasImageMutateInput.ImageId))]
[McpWhenEqualsRequired(nameof(AgasImageMutateInput.Op), "update", nameof(AgasImageMutateInput.ImageId))]
[McpWhenEqualsAnyRequired(nameof(AgasImageMutateInput.Op), "move", nameof(AgasImageMutateInput.X), nameof(AgasImageMutateInput.Y))]
[McpWhenEqualsAnyRequired(
    nameof(AgasImageMutateInput.Op),
    "update",
    nameof(AgasImageMutateInput.ImagePath),
    nameof(AgasImageMutateInput.X),
    nameof(AgasImageMutateInput.Y),
    nameof(AgasImageMutateInput.ScaleX),
    nameof(AgasImageMutateInput.ScaleY),
    nameof(AgasImageMutateInput.RotationDegrees),
    nameof(AgasImageMutateInput.Opacity))]
public sealed record AgasImageMutateInput(
    [property: Required]
    [property: AllowedValues("add", "delete", "move", "update")]
    string Op,

    [property: Required]
    [property: StringLength(120, MinimumLength = 1)]
    [property: RegularExpression(AgasMcpContractPatterns.SafeIdentifier)]
    string SceneId,

    [property: Required]
    [property: StringLength(120, MinimumLength = 1)]
    [property: RegularExpression(AgasMcpContractPatterns.SafeIdentifier)]
    string LayerId,

    [property: StringLength(120, MinimumLength = 1)]
    [property: RegularExpression(AgasMcpContractPatterns.SafeIdentifier)]
    string? ImageId = null,

    [property: StringLength(500, MinimumLength = 1)]
    string? ImagePath = null,

    [property: Range(-100_000, 100_000)] double? X = null,
    [property: Range(-100_000, 100_000)] double? Y = null,
    [property: Range(0.01, 100)] double? ScaleX = null,
    [property: Range(0.01, 100)] double? ScaleY = null,
    [property: Range(-3600, 3600)] double? RotationDegrees = null,
    [property: Range(0, 1)] double? Opacity = null);

[McpClosedObject(IncludeSchemaKeyword = true)]
[McpWhenEqualsRequired(nameof(AgasPromptMutateInput.Op), "add", nameof(AgasPromptMutateInput.Text))]
[McpWhenEqualsRequired(nameof(AgasPromptMutateInput.Op), "delete", nameof(AgasPromptMutateInput.FragmentId))]
[McpWhenEqualsRequired(nameof(AgasPromptMutateInput.Op), "update", nameof(AgasPromptMutateInput.FragmentId))]
[McpWhenEqualsAnyRequired(nameof(AgasPromptMutateInput.Op), "update", nameof(AgasPromptMutateInput.Text), nameof(AgasPromptMutateInput.Role))]
[McpWhenEqualsRequired(nameof(AgasPromptMutateInput.Op), "move", nameof(AgasPromptMutateInput.FragmentId), nameof(AgasPromptMutateInput.Direction))]
[McpWhenEqualsRequired(nameof(AgasPromptMutateInput.Op), "setEnabled", nameof(AgasPromptMutateInput.FragmentId), nameof(AgasPromptMutateInput.Enabled))]
public sealed record AgasPromptMutateInput(
    [property: Required]
    [property: AllowedValues("add", "delete", "update", "move", "setEnabled")]
    string Op,

    [property: Required]
    [property: StringLength(120, MinimumLength = 1)]
    [property: RegularExpression(AgasMcpContractPatterns.SafeIdentifier)]
    string SceneId,

    [property: Required]
    [property: StringLength(120, MinimumLength = 1)]
    [property: RegularExpression(AgasMcpContractPatterns.SafeIdentifier)]
    string LayerId,

    [property: StringLength(120, MinimumLength = 1)]
    [property: RegularExpression(AgasMcpContractPatterns.SafeIdentifier)]
    string? FragmentId = null,

    [property: StringLength(4000, MinimumLength = 0)]
    string? Text = null,

    [property: AllowedValues("general", "environment", "negativeConstraint")]
    string? Role = null,

    bool? Enabled = null,

    [property: AllowedValues(-1, 1)]
    int? Direction = null);

[McpClosedObject]
public sealed record AgasSceneGetInput(
    [property: Required]
    [property: StringLength(120, MinimumLength = 1)]
    [property: RegularExpression(AgasMcpContractPatterns.SafeIdentifier)]
    string SceneId,

    [property: RegularExpression(AgasMcpContractPatterns.SceneCursor)]
    string? Cursor = null,

    [property: DefaultValue(100)]
    [property: Range(1, 200)]
    int Limit = 100);

[McpClosedObject]
public sealed record AgasImportsListInput(
    [property: AllowedValues("asset", "scenePackage")]
    string? Kind = null,

    [property: DefaultValue(50)]
    [property: Range(1, 200)]
    int Limit = 50,

    [property: DefaultValue(0)]
    [property: Range(0, int.MaxValue)]
    int Offset = 0);

[McpClosedObject]
public sealed record AgasAssetsImportInput(
    [property: Required]
    [property: RegularExpression(AgasMcpContractPatterns.SourceRef)]
    string SourceRef);

[McpClosedObject]
public sealed record AgasScenePackageImportInput(
    [property: Required]
    [property: RegularExpression(AgasMcpContractPatterns.SourceRef)]
    string SourceRef);

[McpClosedObject]
public sealed record AgasScenePackageExportInput(
    [property: Required]
    [property: StringLength(120, MinimumLength = 1)]
    [property: RegularExpression(AgasMcpContractPatterns.SafeIdentifier)]
    string SceneId);

[McpClosedObject(IncludeSchemaKeyword = true)]
[McpWhenEqualsRequired(nameof(AgasGridMutateInput.Op), "setSize", nameof(AgasGridMutateInput.SizePx))]
[McpWhenEqualsRequired(nameof(AgasGridMutateInput.Op), "setSnapEnabled", nameof(AgasGridMutateInput.Enabled))]
[McpWhenEqualsRequired(nameof(AgasGridMutateInput.Op), "setVisible", nameof(AgasGridMutateInput.Enabled))]
public sealed record AgasGridMutateInput(
    [property: Required]
    [property: AllowedValues("setSize", "setSnapEnabled", "setVisible")]
    string Op,

    [property: Range(1, 4096)]
    int? SizePx = null,

    bool? Enabled = null);

[McpClosedObject]
public sealed record AgasViewSetInput(
    [property: Required]
    [property: StringLength(120, MinimumLength = 1)]
    [property: RegularExpression(AgasMcpContractPatterns.SafeIdentifier)]
    string SceneId,

    [property: Required]
    [property: Range(-100_000, 100_000)]
    double CameraX,

    [property: Required]
    [property: Range(-100_000, 100_000)]
    double CameraY,

    [property: Required]
    [property: Range(0.05, 20)]
    double Zoom);

[McpClosedObject]
public sealed record AgasProjectSaveInput;

[McpClosedObject(IncludeSchemaKeyword = true)]
[McpWhenEqualsRequired(nameof(AgasSelectionSetInput.Op), "setRegions", nameof(AgasSelectionSetInput.Selection))]
[McpWhenEqualsRequired(nameof(AgasSelectionSetInput.Op), "clearRegions", nameof(AgasSelectionSetInput.Op))]
[McpWhenEqualsRequired(nameof(AgasSelectionSetInput.Op), "selectImage", nameof(AgasSelectionSetInput.LayerId), nameof(AgasSelectionSetInput.ImageId))]
[McpWhenEqualsRequired(nameof(AgasSelectionSetInput.Op), "clearImageSelection", nameof(AgasSelectionSetInput.Op))]
public sealed record AgasSelectionSetInput(
    [property: Required]
    [property: AllowedValues("setRegions", "clearRegions", "selectImage", "clearImageSelection")]
    string Op,

    [property: Required]
    [property: StringLength(120, MinimumLength = 1)]
    [property: RegularExpression(AgasMcpContractPatterns.SafeIdentifier)]
    string SceneId,

    [property: McpUseDefinition("region")]
    AgasMcpGridRegionDto? Selection = null,

    [property: McpUseDefinition("region")]
    AgasMcpGridRegionDto? Replacement = null,

    [property: StringLength(120, MinimumLength = 1)]
    [property: RegularExpression(AgasMcpContractPatterns.SafeIdentifier)]
    string? ImageId = null,

    [property: StringLength(120, MinimumLength = 1)]
    [property: RegularExpression(AgasMcpContractPatterns.SafeIdentifier)]
    string? LayerId = null);

[McpClosedObject]
public sealed record AgasSelectionGetInput;

[McpClosedObject]
public sealed record AgasAiGenerateInput(
    [property: Required]
    [property: StringLength(120, MinimumLength = 1)]
    [property: RegularExpression(AgasMcpContractPatterns.SafeIdentifier)]
    string SceneId,

    [property: Required]
    [property: StringLength(120, MinimumLength = 1)]
    [property: RegularExpression(AgasMcpContractPatterns.SafeIdentifier)]
    string LayerId,

    [property: StringLength(4000, MinimumLength = 1)]
    string? Prompt = null,

    [property: StringLength(120, MinimumLength = 1)]
    [property: RegularExpression(AgasMcpContractPatterns.SafeIdentifier)]
    string? ProfileId = null,

    [property: DefaultValue("NewImage")]
    [property: AllowedValues("NewImage", "ReplaceSelected")]
    string Target = "NewImage",

    [property: DefaultValue("CurrentLayer")]
    [property: AllowedValues("CurrentLayer", "AllVisibleLayers", "VisibleLayersExceptCurrent")]
    string LayerScope = "CurrentLayer",

    [property: AllowedValues("low", "medium")]
    string? Quality = null);

[McpClosedObject]
public sealed record AgasAiStatusInput(
    [property: StringLength(38, MinimumLength = 38)]
    [property: RegularExpression(AgasMcpContractPatterns.AiJobId)]
    string? JobId = null,

    [property: DefaultValue(10)]
    [property: Range(1, 50)]
    int Limit = 10);

[McpClosedObject]
public sealed record AgasAiCancelInput(
    [property: Required]
    [property: StringLength(38, MinimumLength = 38)]
    [property: RegularExpression(AgasMcpContractPatterns.AiJobId)]
    string JobId);

[McpClosedObject]
public sealed record AgasAiApplyInput(
    [property: Required]
    [property: StringLength(38, MinimumLength = 38)]
    [property: RegularExpression(AgasMcpContractPatterns.AiJobId)]
    string JobId);
