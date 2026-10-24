using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AppAgasCs.Application;
using AppAgasCs.Core.Persistence;
using Godot;
using Xunit;
using Environment = System.Environment;

namespace AppAgasCs.Tests.Core.Application;

public sealed class AgasSceneDetailQueryTests
{
    [Fact]
    public void Capture_LoadedSceneUsesLiveSnapshotWithoutFreshRead()
    {
        AgasSceneDetailSnapshot live = Snapshot("scene-1", "dirty live");
        var source = new RecordingSource(live, Snapshot("scene-1", "stale disk"));
        var query = new AgasSceneDetailQuery(
            source,
            new AgasSceneDetailMainThreadGuard(Environment.CurrentManagedThreadId));

        AgasSceneDetailResult<AgasSceneDetailCaptureResult> result = query.Capture("scene-1");

        Assert.True(result.Success);
        Assert.Equal(AgasSceneDetailSourceKind.LoadedLive, result.Value!.Source);
        Assert.Equal("dirty live", result.Value.Snapshot.Name);
        Assert.Equal(1, source.LiveCalls);
        Assert.Equal(0, source.FreshCalls);
        Assert.All(source.ThreadIds, id => Assert.Equal(Environment.CurrentManagedThreadId, id));
    }

    [Fact]
    public void Capture_UncachedSceneUsesFreshProjectedSnapshot()
    {
        var source = new RecordingSource(live: null, fresh: Snapshot("scene-1", "fresh disk"));
        var query = new AgasSceneDetailQuery(
            source,
            new AgasSceneDetailMainThreadGuard(Environment.CurrentManagedThreadId));

        AgasSceneDetailResult<AgasSceneDetailCaptureResult> result = query.Capture("scene-1");

        Assert.True(result.Success);
        Assert.Equal(AgasSceneDetailSourceKind.FreshDisk, result.Value!.Source);
        Assert.Equal("fresh disk", result.Value.Snapshot.Name);
        Assert.Equal(1, source.LiveCalls);
        Assert.Equal(1, source.FreshCalls);
    }

    [Fact]
    public async Task Capture_WorkerThreadIsRejectedBeforeSourceTouchesResourceBoundary()
    {
        var source = new RecordingSource(live: null, fresh: Snapshot("scene-1", "fresh"));
        var query = new AgasSceneDetailQuery(
            source,
            new AgasSceneDetailMainThreadGuard(Environment.CurrentManagedThreadId));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => query.Capture("scene-1")));

        Assert.Equal(0, source.LiveCalls);
        Assert.Equal(0, source.FreshCalls);
    }

    [Fact]
    public void Capture_RejectsMalformedOrMismatchedProjection()
    {
        AgasSceneDetailSnapshot malformed = new(
            "scene-other",
            "bad",
            "layer-1",
            new AgasSceneDetailViewState(0, 0, 1),
            new[] { new AgasSceneDetailLayerItem("scene-other", "layer-1", "Layer", 0, true, false, false, 1, 0) });
        var source = new RecordingSource(null, malformed);
        var query = new AgasSceneDetailQuery(
            source,
            new AgasSceneDetailMainThreadGuard(Environment.CurrentManagedThreadId));

        AgasSceneDetailResult<AgasSceneDetailCaptureResult> result = query.Capture("scene-1");

        Assert.False(result.Success);
        Assert.Equal(AgasSceneDetailErrorCodes.Invalid, result.ErrorCode);
    }

    [Theory]
    [InlineData("../scene")]
    [InlineData("scene.with.dot")]
    [InlineData(" scene-1")]
    [InlineData("scene-1 ")]
    public void Capture_RejectsUnsafeSceneIdBeforeTouchingSource(string sceneId)
    {
        var source = new RecordingSource(live: null, fresh: Snapshot("scene-1", "fresh"));
        var query = new AgasSceneDetailQuery(
            source,
            new AgasSceneDetailMainThreadGuard(Environment.CurrentManagedThreadId));

        AgasSceneDetailResult<AgasSceneDetailCaptureResult> result = query.Capture(sceneId);

        Assert.False(result.Success);
        Assert.Equal(AgasSceneDetailErrorCodes.Invalid, result.ErrorCode);
        Assert.Equal(0, source.LiveCalls);
        Assert.Equal(0, source.FreshCalls);
    }

    [Fact]
    public void ContractsExposeOnlyScalarTypesAndRepositoryHasIgnoreCachePort()
    {
        Type[] contractTypes =
        {
            typeof(AgasSceneDetailSnapshot),
            typeof(AgasSceneDetailViewState),
            typeof(AgasSceneDetailItem),
            typeof(AgasSceneDetailLayerItem),
            typeof(AgasSceneDetailImageItem),
            typeof(AgasSceneDetailPromptItem),
        };
        Assert.All(contractTypes, type =>
        {
            Assert.False(typeof(GodotObject).IsAssignableFrom(type));
            Assert.DoesNotContain(type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
                property => typeof(GodotObject).IsAssignableFrom(property.PropertyType));
        });

        MethodInfo fresh = Assert.Single(typeof(AgasResourceRepository).GetMethods(), method =>
            method.Name == nameof(AgasResourceRepository.LoadFresh) && method.IsGenericMethodDefinition);
        Assert.NotNull(fresh);
        string source = File.ReadAllText(Path.Combine(
            AppAgasCs.Tests.Core.ProjectRoot.Path,
            "src/Core/Persistence/AgasResourceRepository.cs"));
        Assert.Contains("ResourceLoader.CacheMode.Ignore", source, StringComparison.Ordinal);
        Assert.Contains("ResourceLoader.Load(fullPath, cacheMode: cacheMode)", source, StringComparison.Ordinal);
    }

    private static AgasSceneDetailSnapshot Snapshot(string sceneId, string name) => new(
        sceneId,
        name,
        "layer-1",
        new AgasSceneDetailViewState(1, 2, 1.5),
        new AgasSceneDetailItem[]
        {
            new AgasSceneDetailLayerItem(sceneId, "layer-1", "Layer", 0, true, false, false, 1, 1),
            new AgasSceneDetailImageItem(sceneId, "layer-1", "image-1", "assets/a.png", 0, 1, 2, 1, 1, 0, 1),
            new AgasSceneDetailPromptItem(sceneId, "layer-1", "prompt-1", "text", "general", true, 0),
        });

    private sealed class RecordingSource : IAgasSceneDetailSnapshotSource
    {
        private readonly AgasSceneDetailSnapshot? _live;
        private readonly AgasSceneDetailSnapshot? _fresh;

        public RecordingSource(AgasSceneDetailSnapshot? live, AgasSceneDetailSnapshot? fresh)
        {
            _live = live;
            _fresh = fresh;
        }

        public int LiveCalls { get; private set; }
        public int FreshCalls { get; private set; }
        public List<int> ThreadIds { get; } = new();

        public bool TryCaptureLoadedLive(string sceneId, out AgasSceneDetailSnapshot? snapshot)
        {
            LiveCalls++;
            ThreadIds.Add(Environment.CurrentManagedThreadId);
            snapshot = _live;
            return snapshot != null;
        }

        public AgasSceneDetailSnapshot? LoadFreshAndProject(string sceneId)
        {
            FreshCalls++;
            ThreadIds.Add(Environment.CurrentManagedThreadId);
            return _fresh;
        }
    }
}
