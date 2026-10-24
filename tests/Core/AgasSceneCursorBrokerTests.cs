using System;
using System.Collections.Generic;
using System.Linq;
using AppAgasCs.Application;
using Xunit;

namespace AppAgasCs.Tests.Core;

public sealed class AgasSceneCursorBrokerTests
{
    [Fact]
    public void DefaultsFreezeFiveMinuteCountAndUtf8Budgets()
    {
        AgasSceneDetailCursorOptions options = AgasSceneDetailCursorOptions.Default;

        Assert.Equal(TimeSpan.FromMinutes(5), options.TimeToLive);
        Assert.Equal(128, options.MaximumSnapshots);
        Assert.Equal(32, options.MaximumSnapshotsPerProject);
        Assert.Equal(10_000, options.MaximumItemsPerSnapshot);
        Assert.Equal(256 * 1024, options.MaximumItemUtf8Bytes);
        Assert.Equal(8 * 1024 * 1024, options.MaximumSnapshotUtf8Bytes);
        Assert.Equal(64L * 1024 * 1024, options.MaximumRetainedUtf8Bytes);
        Assert.Equal(1_024, options.MaximumCursors);
        Assert.Equal(4 * 1024 * 1024, options.MaximumSnapshotMetadataUtf8Bytes);
    }

    [Fact]
    public void CursorTokensHaveAnIndependentGlobalCountBudget()
    {
        var broker = new AgasSceneDetailCursorBroker();
        AgasSceneDetailProjectScope scope = Scope("project-a");
        AgasSceneDetailPage first = broker.CreateFirstPage(
            scope,
            SnapshotWithPrompts("scene-1", 2_000),
            1).Value!;
        var frontier = new Queue<string>();
        frontier.Enqueue(first.NextCursor!);
        bool capacityRejected = false;

        while (frontier.Count > 0 && !capacityRejected)
        {
            string cursor = frontier.Dequeue();
            for (int limit = 1; limit <= 200; limit++)
            {
                AgasSceneDetailResult<AgasSceneDetailPageLease> result =
                    broker.AcquirePage(scope, "scene-1", cursor, limit);
                if (!result.Success)
                {
                    Assert.Equal(AgasSceneDetailErrorCodes.CursorCapacity, result.ErrorCode);
                    capacityRejected = true;
                    break;
                }
                using AgasSceneDetailPageLease lease = result.Value!;
                if (lease.Page.NextCursor != null) frontier.Enqueue(lease.Page.NextCursor);
            }
        }

        Assert.True(capacityRejected);
        Assert.InRange(broker.ActiveCursorCount, 0, 1_024);
    }

    [Fact]
    public void SnapshotMetadataHasIndependentFourMiBUtf8Budget()
    {
        var broker = new AgasSceneDetailCursorBroker();
        AgasSceneDetailSnapshot snapshot = new(
            "scene-1",
            new string('m', (4 * 1024 * 1024) + 1),
            null,
            new AgasSceneDetailViewState(0, 0, 1),
            Array.Empty<AgasSceneDetailItem>());

        AgasSceneDetailResult<AgasSceneDetailPage> result =
            broker.CreateFirstPage(Scope("project-a"), snapshot, 1);

        Assert.False(result.Success);
        Assert.Equal(AgasSceneDetailErrorCodes.SnapshotTooLarge, result.ErrorCode);
        Assert.Equal(0, broker.RetainedSnapshotCount);
    }

    [Fact]
    public void CreateAndAcquire_UsesStableKindOrderAndImmutableCursorPages()
    {
        var broker = new AgasSceneDetailCursorBroker();
        AgasSceneDetailProjectScope scope = Scope("project-a");
        AgasSceneDetailSnapshot snapshot = Snapshot("scene-1", new AgasSceneDetailItem[]
        {
            Prompt("scene-1", "layer-1", "prompt-b", 1, "b"),
            Image("scene-1", "layer-1", "image-a", 0),
            Layer("scene-1", "layer-1", 0, images: 1, prompts: 2),
            Prompt("scene-1", "layer-1", "prompt-a", 1, "a"),
        });

        AgasSceneDetailResult<AgasSceneDetailPage> first = broker.CreateFirstPage(scope, snapshot, 2);

        Assert.True(first.Success);
        Assert.Equal(new[] { AgasSceneDetailItemKind.Layer, AgasSceneDetailItemKind.Image }, first.Value!.Items.Select(item => item.Kind));
        Assert.Matches("^scncur-[a-f0-9]{32}$", first.Value.NextCursor!);
        Assert.Equal(2, first.Value.Items.Count);
        Assert.Equal("scene-1", first.Value.SceneId);

        AgasSceneDetailResult<AgasSceneDetailPageLease> second = broker.AcquirePage(scope, "scene-1", first.Value.NextCursor!, 1);
        Assert.True(second.Success);
        using AgasSceneDetailPageLease lease = second.Value!;
        Assert.Equal("prompt-a", lease.Page.Items.Single().Id);
        Assert.NotNull(lease.Page.NextCursor);

        AgasSceneDetailResult<AgasSceneDetailPageLease> repeated = broker.AcquirePage(scope, "scene-1", first.Value.NextCursor!, 1);
        Assert.True(repeated.Success);
        using AgasSceneDetailPageLease repeatedLease = repeated.Value!;
        Assert.Equal(lease.Page.NextCursor, repeatedLease.Page.NextCursor);
    }

    [Fact]
    public void CursorRejectsCrossScopeCrossSceneExpiryAndRevocation()
    {
        DateTimeOffset now = new(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
        var broker = new AgasSceneDetailCursorBroker(AgasSceneDetailCursorOptions.Default, () => now);
        AgasSceneDetailProjectScope scope = Scope("project-a");
        AgasSceneDetailResult<AgasSceneDetailPage> first = broker.CreateFirstPage(
            scope,
            SnapshotWithPrompts("scene-1", 2),
            1);
        string cursor = first.Value!.NextCursor!;

        Assert.Equal(AgasSceneDetailErrorCodes.CursorInvalid, broker.AcquirePage(Scope("project-b"), "scene-1", cursor, 1).ErrorCode);
        Assert.Equal(AgasSceneDetailErrorCodes.CursorInvalid, broker.AcquirePage(scope, "scene-2", cursor, 1).ErrorCode);

        now += TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1);
        Assert.Equal(AgasSceneDetailErrorCodes.CursorInvalid, broker.AcquirePage(scope, "scene-1", cursor, 1).ErrorCode);
        Assert.Equal(0, broker.RetainedSnapshotCount);

        now = new DateTimeOffset(2026, 7, 31, 1, 0, 0, TimeSpan.Zero);
        first = broker.CreateFirstPage(scope, SnapshotWithPrompts("scene-1", 2), 1);
        broker.RevokeScope(scope);
        Assert.Equal(AgasSceneDetailErrorCodes.CursorInvalid, broker.AcquirePage(scope, "scene-1", first.Value!.NextCursor!, 1).ErrorCode);
        Assert.Equal(0, broker.RetainedSnapshotCount);
    }

    [Fact]
    public void PinnedSnapshotCannotBeEvictedAndRevokedPayloadReleasesAfterLease()
    {
        AgasSceneDetailCursorOptions options = Options(maxSnapshots: 1, maxPerProject: 1);
        var broker = new AgasSceneDetailCursorBroker(options);
        AgasSceneDetailProjectScope scopeA = Scope("project-a");
        AgasSceneDetailProjectScope scopeB = Scope("project-b");
        AgasSceneDetailPage first = broker.CreateFirstPage(scopeA, SnapshotWithPrompts("scene-a", 3), 1).Value!;
        AgasSceneDetailPageLease pinned = broker.AcquirePage(scopeA, "scene-a", first.NextCursor!, 1).Value!;

        AgasSceneDetailResult<AgasSceneDetailPage> blocked = broker.CreateFirstPage(scopeB, SnapshotWithPrompts("scene-b", 2), 1);
        Assert.False(blocked.Success);
        Assert.Equal(AgasSceneDetailErrorCodes.CursorCapacity, blocked.ErrorCode);

        broker.RevokeScope(scopeA);
        Assert.Equal(1, broker.RetainedSnapshotCount);
        Assert.Equal(AgasSceneDetailErrorCodes.CursorInvalid, broker.AcquirePage(scopeA, "scene-a", first.NextCursor!, 1).ErrorCode);
        pinned.Dispose();
        Assert.Equal(0, broker.RetainedSnapshotCount);
        Assert.Equal(0, broker.RetainedUtf8Bytes);

        Assert.True(broker.CreateFirstPage(scopeB, SnapshotWithPrompts("scene-b", 2), 1).Success);
    }

    [Fact]
    public void CapacityEvictsOldestUnpinnedSnapshotAndEnforcesPerProjectLimit()
    {
        DateTimeOffset now = new(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
        var broker = new AgasSceneDetailCursorBroker(Options(maxSnapshots: 2, maxPerProject: 1), () => now);
        AgasSceneDetailProjectScope scopeA = Scope("project-a");
        AgasSceneDetailProjectScope scopeB = Scope("project-b");
        AgasSceneDetailPage a1 = broker.CreateFirstPage(scopeA, SnapshotWithPrompts("scene-a1", 2), 1).Value!;
        now += TimeSpan.FromSeconds(1);
        AgasSceneDetailPage b1 = broker.CreateFirstPage(scopeB, SnapshotWithPrompts("scene-b1", 2), 1).Value!;
        now += TimeSpan.FromSeconds(1);

        AgasSceneDetailPage a2 = broker.CreateFirstPage(scopeA, SnapshotWithPrompts("scene-a2", 2), 1).Value!;

        Assert.Equal(2, broker.RetainedSnapshotCount);
        Assert.Equal(AgasSceneDetailErrorCodes.CursorInvalid, broker.AcquirePage(scopeA, "scene-a1", a1.NextCursor!, 1).ErrorCode);
        Assert.True(broker.AcquirePage(scopeA, "scene-a2", a2.NextCursor!, 1).Success);
        Assert.True(broker.AcquirePage(scopeB, "scene-b1", b1.NextCursor!, 1).Success);
    }

    [Fact]
    public void GlobalRetainedPayloadCapacityIsAtomicWhenExistingSnapshotIsPinned()
    {
        var broker = new AgasSceneDetailCursorBroker(Options(
            maxSnapshots: 4,
            maxPerProject: 4,
            maxItemBytes: 512,
            maxSnapshotBytes: 1200,
            maxRetainedBytes: 1200));
        AgasSceneDetailProjectScope scopeA = Scope("project-a");
        AgasSceneDetailPage first = broker.CreateFirstPage(
            scopeA,
            SnapshotWithPromptText("scene-a", 2, 150),
            1).Value!;
        using AgasSceneDetailPageLease pin = broker.AcquirePage(scopeA, "scene-a", first.NextCursor!, 1).Value!;
        long retainedBefore = broker.RetainedUtf8Bytes;

        AgasSceneDetailResult<AgasSceneDetailPage> second = broker.CreateFirstPage(
            Scope("project-b"),
            SnapshotWithPromptText("scene-b", 2, 150),
            1);

        Assert.False(second.Success);
        Assert.Equal(AgasSceneDetailErrorCodes.CursorCapacity, second.ErrorCode);
        Assert.Equal(1, broker.RetainedSnapshotCount);
        Assert.Equal(retainedBefore, broker.RetainedUtf8Bytes);
    }

    [Fact]
    public void ItemCountItemUtf8AndSnapshotUtf8LimitsFailWithoutCursorReservation()
    {
        var countBroker = new AgasSceneDetailCursorBroker(Options(maxItems: 2));
        AgasSceneDetailResult<AgasSceneDetailPage> count = countBroker.CreateFirstPage(
            Scope("project-a"), SnapshotWithPrompts("scene-1", 3), 1);
        Assert.Equal(AgasSceneDetailErrorCodes.SnapshotTooLarge, count.ErrorCode);
        Assert.Equal(0, countBroker.RetainedSnapshotCount);

        var itemBroker = new AgasSceneDetailCursorBroker(Options(maxItemBytes: 256, maxSnapshotBytes: 4096, maxRetainedBytes: 8192));
        AgasSceneDetailResult<AgasSceneDetailPage> item = itemBroker.CreateFirstPage(
            Scope("project-a"),
            Snapshot("scene-1", new AgasSceneDetailItem[]
            {
                Layer("scene-1", "layer-1", 0, 0, 1),
                Prompt("scene-1", "layer-1", "prompt-1", 0, new string('x', 500)),
            }),
            1);
        Assert.Equal(AgasSceneDetailErrorCodes.SnapshotTooLarge, item.ErrorCode);

        var snapshotBroker = new AgasSceneDetailCursorBroker(Options(maxItemBytes: 512, maxSnapshotBytes: 700, maxRetainedBytes: 1400));
        AgasSceneDetailResult<AgasSceneDetailPage> total = snapshotBroker.CreateFirstPage(
            Scope("project-a"),
            Snapshot("scene-1", new AgasSceneDetailItem[]
            {
                Layer("scene-1", "layer-1", 0, 0, 3),
                Prompt("scene-1", "layer-1", "prompt-1", 0, new string('a', 180)),
                Prompt("scene-1", "layer-1", "prompt-2", 1, new string('b', 180)),
                Prompt("scene-1", "layer-1", "prompt-3", 2, new string('c', 180)),
            }),
            1);
        Assert.Equal(AgasSceneDetailErrorCodes.SnapshotTooLarge, total.ErrorCode);
        Assert.Equal(0, snapshotBroker.RetainedUtf8Bytes);
    }

    private static AgasSceneDetailCursorOptions Options(
        int maxSnapshots = 8,
        int maxPerProject = 4,
        int maxItems = 20,
        int maxItemBytes = 2048,
        int maxSnapshotBytes = 16 * 1024,
        long maxRetainedBytes = 64 * 1024) =>
        new(TimeSpan.FromMinutes(5), maxSnapshots, maxPerProject, maxItems, maxItemBytes, maxSnapshotBytes, maxRetainedBytes)
        {
            MaximumSnapshotMetadataUtf8Bytes = Math.Min(4 * 1024 * 1024, maxSnapshotBytes),
        };

    private static AgasSceneDetailProjectScope Scope(string projectId) =>
        AgasSceneDetailProjectScope.Create("root-" + projectId, projectId);

    private static AgasSceneDetailSnapshot SnapshotWithPrompts(string sceneId, int promptCount)
    {
        var items = new List<AgasSceneDetailItem>
        {
            Layer(sceneId, "layer-1", 0, 0, promptCount),
        };
        items.AddRange(Enumerable.Range(0, promptCount)
            .Select(index => Prompt(sceneId, "layer-1", $"prompt-{index}", index, index.ToString())));
        return Snapshot(sceneId, items);
    }

    private static AgasSceneDetailSnapshot SnapshotWithPromptText(string sceneId, int promptCount, int textLength)
    {
        var items = new List<AgasSceneDetailItem>
        {
            Layer(sceneId, "layer-1", 0, 0, promptCount),
        };
        items.AddRange(Enumerable.Range(0, promptCount)
            .Select(index => Prompt(sceneId, "layer-1", $"prompt-{index}", index, new string((char)('a' + index), textLength))));
        return Snapshot(sceneId, items);
    }

    private static AgasSceneDetailSnapshot Snapshot(string sceneId, IEnumerable<AgasSceneDetailItem> items) => new(
        sceneId,
        sceneId,
        "layer-1",
        new AgasSceneDetailViewState(0, 0, 1),
        items);

    private static AgasSceneDetailLayerItem Layer(
        string sceneId,
        string layerId,
        int order,
        int images,
        int prompts) =>
        new(sceneId, layerId, "Layer", order, true, false, false, images, prompts);

    private static AgasSceneDetailImageItem Image(string sceneId, string layerId, string imageId, int order) =>
        new(sceneId, layerId, imageId, "assets/a.png", order, 0, 0, 1, 1, 0, 1);

    private static AgasSceneDetailPromptItem Prompt(
        string sceneId,
        string layerId,
        string promptId,
        int order,
        string text) =>
        new(sceneId, layerId, promptId, text, "general", true, order);
}
