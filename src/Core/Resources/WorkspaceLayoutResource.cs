using Godot;

namespace AppAgasCs.Core.Resources;

[GlobalClass]
public partial class WorkspaceLayoutResource : Resource
{
    [Export] public int ResourceVersion { get; set; } = 2;
    [Export] public double LeftPanelWidth { get; set; } = 280.0;
    [Export] public bool LeftPanelCollapsed { get; set; } = false;
    [Export] public double PreviewHeight { get; set; } = 180.0;
    [Export] public bool PreviewCollapsed { get; set; } = false;
    [Export] public double RightPanelWidth { get; set; } = 280.0;
    [Export] public bool RightPanelCollapsed { get; set; } = false;
    [Export] public double BottomDockHeight { get; set; } = 240.0;
    [Export] public bool BottomDockCollapsed { get; set; } = true;
    [Export] public string ActiveLeftTab { get; set; } = "Files";
    [Export] public string ActiveBottomTab { get; set; } = "Prompt";
}
