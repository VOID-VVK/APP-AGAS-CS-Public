using Godot;

namespace AppAgasCs.Core.Resources;

[GlobalClass]
public partial class SceneViewStateResource : Resource
{
    [Export] public int ResourceVersion { get; set; } = 1;
    [Export] public double CameraX { get; set; } = 0.0;
    [Export] public double CameraY { get; set; } = 0.0;
    [Export] public double Zoom { get; set; } = 1.0;
}
