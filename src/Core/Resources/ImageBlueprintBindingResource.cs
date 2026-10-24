using Godot;
using Godot.Collections;

namespace AppAgasCs.Core.Resources;

public enum BlueprintTargetImageMode
{
    Unspecified = 0,
    ReplaceImageContent = 1,
}

[GlobalClass]
public partial class ImageBlueprintBindingResource : Resource
{
    [Export] public int ResourceVersion { get; set; } = 1;
    [Export] public string Id { get; set; } = string.Empty;
    [Export] public string BlueprintId { get; set; } = string.Empty;
    [Export] public long BlueprintRevision { get; set; }
    [Export] public string BlueprintDigest { get; set; } = string.Empty;
    [Export] public string TargetImageId { get; set; } = string.Empty;
    [Export] public BlueprintTargetImageMode TargetMode { get; set; } = BlueprintTargetImageMode.Unspecified;
    [Export] public string OutputNodeId { get; set; } = string.Empty;
    [Export] public string OutputPortName { get; set; } = string.Empty;
    [Export] internal Array<BlueprintInputImageBindingResource> InputBindings { get; set; } = new();
    [Export] internal Array<BlueprintModuleDependencyResource> ModuleDependencies { get; set; } = new();
}
