using AppAgasCs.Application;
using Xunit;

namespace AppAgasCs.Tests.Core.Application;

public sealed class ProjectLifecycleRecentRefBrokerTests
{
    [Fact]
    public void EachListRotatesRefAndRevokesPreviousValue()
    {
        var clock = new FakeClock();
        var broker = new ProjectLifecycleRecentRefBroker(clock, new SequentialRefFactory());
        ProjectLifecycleRecentEntry entry = Entry("/projects/a", "identity-a", "A", 10);

        string first = Assert.Single(broker.Rotate(new[] { entry }, null)).ProjectRef;
        string second = Assert.Single(broker.Rotate(new[] { entry }, null)).ProjectRef;

        Assert.NotEqual(first, second);
        Assert.False(broker.TryResolve(first, new[] { entry }, out _));
        Assert.True(broker.TryResolve(second, new[] { entry }, out ProjectLifecycleResolvedRecentProject? resolved));
        Assert.Equal("/projects/a", resolved?.ProjectRoot);
    }

    [Fact]
    public void ExpiredDeletedAndIdentityChangedRefsAreRejected()
    {
        var clock = new FakeClock();
        var broker = new ProjectLifecycleRecentRefBroker(clock, new SequentialRefFactory());
        ProjectLifecycleRecentEntry entry = Entry("/projects/a", "identity-a", "A", 10);

        string expired = Assert.Single(broker.Rotate(new[] { entry }, null)).ProjectRef;
        clock.UtcNow += TimeSpan.FromMinutes(10);
        Assert.False(broker.TryResolve(expired, new[] { entry }, out _));

        string deleted = Assert.Single(broker.Rotate(new[] { entry }, null)).ProjectRef;
        Assert.False(broker.TryResolve(deleted, Array.Empty<ProjectLifecycleRecentEntry>(), out _));

        string changed = Assert.Single(broker.Rotate(new[] { entry }, null)).ProjectRef;
        Assert.False(broker.TryResolve(
            changed,
            new[] { entry with { ProjectIdentity = "identity-b" } },
            out _));
    }

    [Fact]
    public void OutputIsBoundedSortedAndContainsNoRoot()
    {
        var broker = new ProjectLifecycleRecentRefBroker(new FakeClock(), new SequentialRefFactory());
        ProjectLifecycleRecentEntry[] entries = Enumerable.Range(0, 25)
            .Select(index => Entry($"/secret/root-{index}", $"identity-{index}", $"Project {index:D2}", index))
            .ToArray();

        IReadOnlyList<ProjectLifecycleRecentProject> result = broker.Rotate(entries, "/secret/root-24");

        Assert.Equal(20, result.Count);
        Assert.Equal("Project 24", result[0].Name);
        Assert.True(result[0].IsOpen);
        Assert.All(result, item =>
        {
            Assert.Matches("^projectref-[a-f0-9]{32}$", item.ProjectRef);
            Assert.DoesNotContain("/secret", item.ToString(), StringComparison.Ordinal);
        });
        Assert.Null(typeof(ProjectLifecycleRecentProject).GetProperty("ProjectRoot"));
    }

    private static ProjectLifecycleRecentEntry Entry(
        string root,
        string identity,
        string name,
        long opened) => new(root, identity, name, opened, true);

    private sealed class FakeClock : IProjectLifecycleClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class SequentialRefFactory : IProjectLifecycleRefFactory
    {
        private int _next;

        public string CreateProjectRef() => $"projectref-{Interlocked.Increment(ref _next):x32}";
    }
}
