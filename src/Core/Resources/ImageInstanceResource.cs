using Godot;

namespace AppAgasCs.Core.Resources;

[GlobalClass]
public partial class ImageInstanceResource : Resource
{
    [Export] public int ResourceVersion { get; set; } = 1;
    [Export] public string Id { get; set; } = string.Empty;
    [Export] public string ImagePath { get; set; } = string.Empty;
    [Export] public int Order { get; set; } = 0;
    [Export] public double X { get; set; } = 0.0;
    [Export] public double Y { get; set; } = 0.0;
    [Export] public double ScaleX { get; set; } = 1.0;
    [Export] public double ScaleY { get; set; } = 1.0;
    [Export] public double RotationDegrees { get; set; } = 0.0;
    [Export] public double Opacity { get; set; } = 1.0;
}
