using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Godot.Collections;

namespace AppAgasCs.Core.Resources;

[GlobalClass]
public partial class AgasLayerResource : Resource
{
    [Export] public int ResourceVersion { get; set; } = 1;
    [Export] public string Id { get; set; } = string.Empty;
    [Export] public string Name { get; set; } = string.Empty;
    [Export] public int Order { get; set; } = 0;
    [Export] public bool Visible { get; set; } = true;
    [Export] public bool Locked { get; set; } = false;
    [Export] public bool Shared { get; set; } = false;
    [Export] internal Array<ImageInstanceResource> Images { get; set; } = new();
    [Export] internal Array<PromptFragmentResource> PromptFragments { get; set; } = new();

    public IReadOnlyList<ImageInstanceResource> ImagesSnapshot =>
        System.Array.AsReadOnly((Images ?? new Array<ImageInstanceResource>()).ToArray());

    public IReadOnlyList<PromptFragmentResource> PromptFragmentsSnapshot =>
        System.Array.AsReadOnly((PromptFragments ?? new Array<PromptFragmentResource>()).ToArray());

    public void AddImage(ImageInstanceResource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        (Images ??= new Array<ImageInstanceResource>()).Add(image);
    }

    public bool RemoveImage(ImageInstanceResource image) => Images?.Remove(image) == true;

    public void AddPromptFragment(PromptFragmentResource fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        (PromptFragments ??= new Array<PromptFragmentResource>()).Add(fragment);
    }

    public bool RemovePromptFragment(PromptFragmentResource fragment) => PromptFragments?.Remove(fragment) == true;
}
