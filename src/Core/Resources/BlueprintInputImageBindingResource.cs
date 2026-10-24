using Godot;

namespace AppAgasCs.Core.Resources;

[GlobalClass]
public partial class BlueprintInputImageBindingResource : Resource
{
    [Export] public int ResourceVersion { get; set; } = 1;
    [Export] public string InputNodeId { get; set; } = string.Empty;
    [Export] public string InputPortName { get; set; } = string.Empty;
    [Export] public string SourceImageId { get; set; } = string.Empty;
}
