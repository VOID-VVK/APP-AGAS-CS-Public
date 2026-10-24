using Godot;

namespace AppAgasCs.Core.Resources;

[GlobalClass]
public partial class BlueprintModuleDependencyResource : Resource
{
    [Export] public int ResourceVersion { get; set; } = 1;
    [Export] public string ModuleId { get; set; } = string.Empty;
    [Export] public string Version { get; set; } = string.Empty;
    [Export] public string Digest { get; set; } = string.Empty;
}
