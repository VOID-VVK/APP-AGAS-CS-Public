using Godot;

namespace AppAgasCs.Core.Resources;

public enum PromptFragmentRole
{
    General = 0,
    Environment = 1,
    NegativeConstraint = 2,
}

[GlobalClass]
public partial class PromptFragmentResource : Resource
{
    [Export] public int ResourceVersion { get; set; } = 1;
    [Export] public string Id { get; set; } = string.Empty;
    [Export] public string Text { get; set; } = string.Empty;
    [Export] public bool Enabled { get; set; } = true;
    [Export] public int Order { get; set; } = 0;
    [Export] public PromptFragmentRole Role { get; set; } = PromptFragmentRole.General;
}
