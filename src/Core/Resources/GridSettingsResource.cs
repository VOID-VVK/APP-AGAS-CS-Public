using Godot;

namespace AppAgasCs.Core.Resources;

[GlobalClass]
public partial class GridSettingsResource : Resource
{
    [Export] public int ResourceVersion { get; set; } = 1;
    [Export] public int SizePx { get; set; } = 48;
    [Export] public bool SnapEnabled { get; set; } = true;
    [Export] public bool Visible { get; set; } = true;
}
