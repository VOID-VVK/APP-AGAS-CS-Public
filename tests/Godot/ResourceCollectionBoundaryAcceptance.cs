#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class ResourceCollectionBoundaryAcceptance : Node
{
    private const string Prefix = "[AGAS][RESOURCE-COLLECTION-BOUNDARY]";
    private string? _root;

    public override void _Ready()
    {
        int exitCode = 0;
        try
        {
            VerifyAllExportedArraysRoundTripWithCompatibleNames();
            GD.Print($"{Prefix}[PASS]");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {exception}");
        }
        finally
        {
            try { if (_root != null && Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
            GetTree().Quit(exitCode);
        }
    }

    private void VerifyAllExportedArraysRoundTripWithCompatibleNames()
    {
        _root = Path.Combine(Path.GetTempPath(), "agas-resource-array-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var repository = new AgasResourceRepository();

        var project = new AgasProjectResource { ProjectId = "project-1", Name = "Boundary" };
        project.AddScene(new SceneIndexResource { Id = "scene-1", Name = "Scene", RelativePath = ".agas/scenes/scene-1.tres" });

        var layer = new AgasLayerResource { Id = "layer-1", Name = "Layer" };
        layer.AddImage(new ImageInstanceResource { Id = "image-1", ImagePath = "assets/image.png" });
        layer.AddPromptFragment(new PromptFragmentResource { Id = "prompt-1", Text = "prompt" });
        var scene = new AgasSceneResource { Id = "scene-1", Name = "Scene", ActiveLayerId = "layer-1" };
        scene.AddLayer(layer);

        var recent = new RecentProjectsResource();
        recent.AddEntry(new RecentProjectEntryResource { ProjectName = "Boundary", ProjectPath = _root });
        var layout = new WorkspaceLayoutResource();

        string projectPath = Save(repository, project, "project.tres");
        string scenePath = Save(repository, scene, "scene.tres");
        string recentPath = Save(repository, recent, "recent.tres");
        string layoutPath = Save(repository, layout, "layout.tres");

        RequireSerializedNames(projectPath, "Scenes");
        RequireSerializedNames(scenePath, "Layers", "Images", "PromptFragments");
        RequireSerializedNames(recentPath, "Entries");
        Require(!File.ReadAllText(projectPath).Contains("Blueprints =", StringComparison.Ordinal), "Project serialized a legacy Blueprint index.");
        Require(!File.ReadAllText(layoutPath).Contains("OpenDocumentIds =", StringComparison.Ordinal), "Layout serialized legacy document state.");

        AgasProjectResource loadedProject = LoadFresh<AgasProjectResource>(projectPath);
        AgasSceneResource loadedScene = LoadFresh<AgasSceneResource>(scenePath);
        RecentProjectsResource loadedRecent = LoadFresh<RecentProjectsResource>(recentPath);
        WorkspaceLayoutResource loadedLayout = LoadFresh<WorkspaceLayoutResource>(layoutPath);

        Require(loadedProject.ScenesSnapshot.Count == 1 && loadedProject.ScenesSnapshot[0].Id == "scene-1", "Scenes roundtrip failed.");
        Require(loadedScene.LayersSnapshot.Count == 1 && loadedScene.LayersSnapshot[0].Id == "layer-1", "Layers roundtrip failed.");
        Require(loadedScene.LayersSnapshot[0].ImagesSnapshot.Count == 1 && loadedScene.LayersSnapshot[0].ImagesSnapshot[0].Id == "image-1", "Images roundtrip failed.");
        Require(loadedScene.LayersSnapshot[0].PromptFragmentsSnapshot.Count == 1 && loadedScene.LayersSnapshot[0].PromptFragmentsSnapshot[0].Id == "prompt-1", "PromptFragments roundtrip failed.");
        Require(loadedRecent.EntriesSnapshot.Count == 1 && loadedRecent.EntriesSnapshot[0].ProjectName == "Boundary", "Entries roundtrip failed.");

        RequireMutationRejected(loadedProject.ScenesSnapshot, new SceneIndexResource(), "ScenesSnapshot");
        RequireMutationRejected(loadedScene.LayersSnapshot, new AgasLayerResource(), "LayersSnapshot");
    }

    private string Save(AgasResourceRepository repository, Resource resource, string fileName)
    {
        string path = Path.Combine(_root!, fileName);
        AgasPersistenceResult result = repository.Save(resource, path);
        Require(result.Success, $"Save failed for {fileName}: {result.ErrorMessage}");
        return path;
    }

    private static T LoadFresh<T>(string path) where T : Resource
    {
        Resource? loaded = ResourceLoader.Load(path, string.Empty, ResourceLoader.CacheMode.Ignore);
        return loaded as T ?? throw new InvalidOperationException($"Expected {typeof(T).Name} at {path}, got {loaded?.GetType().Name ?? "null"}.");
    }

    private static void RequireSerializedNames(string path, params string[] names)
    {
        string text = File.ReadAllText(path);
        foreach (string name in names)
        {
            Require(text.Contains(name + " =", StringComparison.Ordinal), $"Serialized property name changed or disappeared: {name} in {Path.GetFileName(path)}.");
        }
    }

    private static void RequireMutationRejected<T>(IReadOnlyList<T> snapshot, T item, string name)
    {
        try
        {
            ((IList<T>)snapshot).Add(item);
        }
        catch (NotSupportedException)
        {
            return;
        }

        throw new InvalidOperationException(name + " accepted public collection mutation.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
