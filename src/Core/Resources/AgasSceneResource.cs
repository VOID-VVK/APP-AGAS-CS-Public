using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Godot.Collections;

namespace AppAgasCs.Core.Resources;

[GlobalClass]
public partial class AgasSceneResource : Resource
{
    [Export] public int ResourceVersion { get; set; } = 1;
    [Export] public string Id { get; set; } = string.Empty;
    [Export] public string Name { get; set; } = string.Empty;
    [Export] public string Readme { get; set; } = string.Empty;
    [Export] public string ActiveLayerId { get; set; } = string.Empty;
    [Export] public SceneViewStateResource ViewState { get; set; } = new();
    [Export] internal Array<AgasLayerResource> Layers { get; set; } = new();

    public IReadOnlyList<AgasLayerResource> LayersSnapshot =>
        System.Array.AsReadOnly((Layers ?? new Array<AgasLayerResource>()).ToArray());

    public void AddLayer(AgasLayerResource layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        (Layers ??= new Array<AgasLayerResource>()).Add(layer);
    }

    public bool RemoveLayer(AgasLayerResource layer) => Layers?.Remove(layer) == true;
}
