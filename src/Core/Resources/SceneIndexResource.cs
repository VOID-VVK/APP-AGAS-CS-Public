using Godot;

namespace AppAgasCs.Core.Resources;

[GlobalClass]
public partial class SceneIndexResource : Resource
{
    [Export] public int ResourceVersion { get; set; } = 1;
    [Export] public string Id { get; set; } = string.Empty;
    [Export] public string Name { get; set; } = string.Empty;
    [Export] public string RelativePath { get; set; } = string.Empty;
    [Export] public int Order { get; set; } = 0;
}
