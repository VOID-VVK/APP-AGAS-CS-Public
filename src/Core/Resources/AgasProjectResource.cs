using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Godot.Collections;

namespace AppAgasCs.Core.Resources;

[GlobalClass]
public partial class AgasProjectResource : Resource
{
    public const int CurrentResourceVersion = 2;
    public const int OldestSupportedResourceVersion = 1;
    public const string FutureProjectReadOnlyCode = "PROJECT_VERSION_UNSUPPORTED";

    [Export] public int ResourceVersion { get; set; } = CurrentResourceVersion;
    [Export] public string ProjectId { get; set; } = string.Empty;
    [Export] public string Name { get; set; } = string.Empty;
    [Export] public string ActiveSceneId { get; set; } = string.Empty;
    [Export] public GridSettingsResource Grid { get; set; } = new();
    [Export] internal Array<SceneIndexResource> Scenes { get; set; } = new();

    public IReadOnlyList<SceneIndexResource> ScenesSnapshot =>
        System.Array.AsReadOnly((Scenes ?? new Array<SceneIndexResource>()).ToArray());

    public void AddScene(SceneIndexResource scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        (Scenes ??= new Array<SceneIndexResource>()).Add(scene);
    }

    public bool RemoveScene(SceneIndexResource scene) => Scenes?.Remove(scene) == true;

    public bool IsFutureVersion => IsFutureResourceVersion(ResourceVersion);
    public bool RequiresReadOnly => IsFutureVersion;
    public string ReadOnlyReasonCode => IsFutureVersion ? FutureProjectReadOnlyCode : string.Empty;
    public bool NeedsMigration => CanMigrateToCurrentVersion;
    public bool CanMigrateToCurrentVersion =>
        CanMigrateResourceVersion(ResourceVersion) && !RequiresReadOnly;

    public static bool IsFutureResourceVersion(int resourceVersion) =>
        resourceVersion > CurrentResourceVersion;

    public static bool IsOlderThanSupportedResourceVersion(int resourceVersion) =>
        resourceVersion < OldestSupportedResourceVersion;

    public static bool CanMigrateResourceVersion(int resourceVersion) =>
        resourceVersion >= OldestSupportedResourceVersion && resourceVersion < CurrentResourceVersion;

}
