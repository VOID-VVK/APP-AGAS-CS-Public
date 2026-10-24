using System.ComponentModel.DataAnnotations;
using AppAgasCs.Mcp.Contracts.Metadata;
using AppAgasCs.Mcp.Contracts.Output;

namespace AppAgasCs.Mcp.Contracts;

[AgasMcpOutputObject]
public sealed record AgasMcpGridDto(
    [property: AgasMcpOutputNumber(Minimum = 1, Maximum = 4096)] int SizePx,
    bool SnapEnabled,
    bool Visible);

[AgasMcpOutputObject]
public sealed record AgasMcpSceneSummaryDto(
    [property: AgasMcpOutputString(MinimumLength = 1, MaximumLength = 120, Pattern = "^[A-Za-z0-9_-]+$")] string SceneId,
    string Name,
    [property: AgasMcpOutputNumber(Minimum = 0)] int Order,
    bool Loaded);

[AgasMcpOutputObject]
public sealed record AgasMcpLayerSummaryDto(
    [property: AgasMcpOutputString(MinimumLength = 1, MaximumLength = 120, Pattern = "^[A-Za-z0-9_-]+$")] string SceneId,
    [property: AgasMcpOutputString(MinimumLength = 1, MaximumLength = 120, Pattern = "^[A-Za-z0-9_-]+$")] string LayerId,
    string Name,
    [property: AgasMcpOutputNumber(Minimum = 0)] int Order,
    bool Visible,
    bool Locked,
    bool Shared,
    [property: AgasMcpOutputNumber(Minimum = 0)] int ImageCount,
    [property: AgasMcpOutputNumber(Minimum = 0)] int PromptCount);

[AgasMcpOutputObject]
public sealed record AgasMcpImageSummaryDto(
    [property: AgasMcpOutputString(MinimumLength = 1, MaximumLength = 120, Pattern = "^[A-Za-z0-9_-]+$")] string SceneId,
    [property: AgasMcpOutputString(MinimumLength = 1, MaximumLength = 120, Pattern = "^[A-Za-z0-9_-]+$")] string LayerId,
    [property: AgasMcpOutputString(MinimumLength = 1, MaximumLength = 120, Pattern = "^[A-Za-z0-9_-]+$")] string ImageId,
    string ImagePath,
    [property: AgasMcpOutputNumber(Minimum = 0)] int Order,
    double X,
    double Y,
    double ScaleX,
    double ScaleY,
    double RotationDegrees,
    [property: AgasMcpOutputNumber(Minimum = 0, Maximum = 1)] double Opacity);

[AgasMcpOutputObject]
public sealed record AgasMcpPromptSummaryDto(
    [property: AgasMcpOutputString(MinimumLength = 1, MaximumLength = 120, Pattern = "^[A-Za-z0-9_-]+$")] string SceneId,
    [property: AgasMcpOutputString(MinimumLength = 1, MaximumLength = 120, Pattern = "^[A-Za-z0-9_-]+$")] string LayerId,
    [property: AgasMcpOutputString(MinimumLength = 1, MaximumLength = 120, Pattern = "^[A-Za-z0-9_-]+$")] string FragmentId,
    string Text,
    [property: AgasMcpOutputAllowedValues("general", "environment", "negativeConstraint")] string Role,
    bool Enabled,
    [property: AgasMcpOutputNumber(Minimum = 0)] int Order);

[AgasMcpOutputObject]
public sealed record AgasMcpAssetItemDto(string RelativePath, string Extension, long SizeBytes, long ModifiedUnixTime);

[AgasMcpOutputObject]
public sealed record AgasMcpSaveFailureDto(string Path, string Code, string Message);

[McpClosedObject]
[AgasMcpOutputObject]
public sealed record AgasMcpGridRegionDto(
    [property: Required]
    [property: Range(int.MinValue, int.MaxValue)]
    int Column,

    [property: Required]
    [property: Range(int.MinValue, int.MaxValue)]
    int Row,

    [property: Required]
    [property: Range(1, int.MaxValue)]
    [property: AgasMcpOutputNumber(Minimum = 1)]
    int Width,

    [property: Required]
    [property: Range(1, int.MaxValue)]
    [property: AgasMcpOutputNumber(Minimum = 1)]
    int Height);
