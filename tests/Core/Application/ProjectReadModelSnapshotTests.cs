using System;
using System.Collections.Generic;
using AppAgasCs.Application.Contracts;
using Xunit;

namespace AppAgasCs.Tests.Core.Application;

public sealed class ProjectReadModelSnapshotTests
{
    [Fact]
    public void Collections_AreImmutableCopiesOfConstructorInputs()
    {
        var scenes = new List<ProjectSceneReadModel>
        {
            new("scene-1", "Scene", 0, true),
        };
        var layers = new List<ProjectLayerReadModel>
        {
            new("scene-1", "layer-1", "Layer", 0, true, false, false, 1, 1),
        };
        var images = new List<ProjectImageReadModel>
        {
            new("scene-1", "layer-1", "image-1", "assets/a.png", 0, 0, 0, 1, 1, 0, 1),
        };
        var prompts = new List<ProjectPromptReadModel>
        {
            new("scene-1", "layer-1", "prompt-1", "Prompt", true, 0),
        };

        var snapshot = new ProjectReadModelSnapshot(
            "project-1",
            "Project",
            "scene-1",
            new ProjectGridReadModel(48, true, true),
            scenes,
            layers,
            images,
            prompts);

        scenes.Clear();
        layers.Clear();
        images.Clear();
        prompts.Clear();

        Assert.Single(snapshot.Scenes);
        Assert.Single(snapshot.Layers);
        Assert.Single(snapshot.Images);
        Assert.Single(snapshot.Prompts);

        var secondScene = new ProjectSceneReadModel("scene-2", "Other", 1, false);
        var expandedScenes = snapshot.Scenes.Add(secondScene);
        Assert.Single(snapshot.Scenes);
        Assert.Equal(2, expandedScenes.Count);

        AssertReadOnly(snapshot.Scenes, secondScene);
        AssertReadOnly(snapshot.Layers,
            new ProjectLayerReadModel("scene-2", "layer-2", "Other", 1, true, false, false, 0, 0));
        AssertReadOnly(snapshot.Images,
            new ProjectImageReadModel("scene-2", "layer-2", "image-2", "assets/b.png", 1, 0, 0, 1, 1, 0, 1));
        AssertReadOnly(snapshot.Prompts,
            new ProjectPromptReadModel("scene-2", "layer-2", "prompt-2", "Other", true, 1));
    }

    [Fact]
    public void Constructor_RejectsNullCollectionsAndItems()
    {
        var grid = new ProjectGridReadModel(48, true, true);
        Assert.Throws<ArgumentNullException>(() => new ProjectReadModelSnapshot(
            "project-1", "Project", "scene-1", grid,
            null!, Array.Empty<ProjectLayerReadModel>(), Array.Empty<ProjectImageReadModel>(), Array.Empty<ProjectPromptReadModel>()));

        Assert.Throws<ArgumentException>(() => new ProjectReadModelSnapshot(
            "project-1", "Project", "scene-1", grid,
            new ProjectSceneReadModel[] { null! },
            Array.Empty<ProjectLayerReadModel>(),
            Array.Empty<ProjectImageReadModel>(),
            Array.Empty<ProjectPromptReadModel>()));
    }

    private static void AssertReadOnly<T>(IEnumerable<T> values, T addedValue)
    {
        ICollection<T> collection = Assert.IsAssignableFrom<ICollection<T>>(values);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => collection.Add(addedValue));
        Assert.Throws<NotSupportedException>(() => collection.Clear());
    }
}
