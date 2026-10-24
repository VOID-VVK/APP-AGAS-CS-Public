using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Godot.Collections;

namespace AppAgasCs.Core.Resources;

[GlobalClass]
public partial class RecentProjectsResource : Resource
{
    [Export] public int ResourceVersion { get; set; } = 1;
    [Export] internal Array<RecentProjectEntryResource> Entries { get; set; } = new();

    public IReadOnlyList<RecentProjectEntryResource> EntriesSnapshot =>
        System.Array.AsReadOnly((Entries ?? new Array<RecentProjectEntryResource>()).ToArray());

    public void AddEntry(RecentProjectEntryResource entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        (Entries ??= new Array<RecentProjectEntryResource>()).Add(entry);
    }

    public bool RemoveEntry(RecentProjectEntryResource entry) => Entries?.Remove(entry) == true;
}
