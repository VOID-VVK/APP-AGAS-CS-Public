using Godot;

namespace AppAgasCs.Core.Resources;

[GlobalClass]
public partial class RecentProjectEntryResource : Resource
{
    [Export] public int ResourceVersion { get; set; } = 1;
    [Export] public string ProjectName { get; set; } = string.Empty;
    [Export] public string ProjectPath { get; set; } = string.Empty;
    [Export] public long LastOpenedUnixTime { get; set; }
}
