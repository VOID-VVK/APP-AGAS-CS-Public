using AppAgasCs.Core.Persistence;
using Xunit;

namespace AppAgasCs.Tests.Core.Persistence;

public sealed class ProjectAccessOwnerMarkerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "agas-owner-marker-tests",
        Guid.NewGuid().ToString("N"));

    public ProjectAccessOwnerMarkerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void MarkerRequiresOperationTargetAndIdentityToMatch()
    {
        var identityProvider = new FileSystemProjectAccessIdentityProvider();
        ProjectAccessPathIdentity identity = identityProvider.Capture(_root);
        string target = Path.Combine(Path.GetDirectoryName(_root)!, "published");
        var store = new ProjectAccessOwnerMarkerStore();

        ProjectAccessOwnerMarker marker = store.Write(_root, target, identity);

        Assert.True(store.IsOwnedBy(_root, marker, identity));
        Assert.False(store.IsOwnedBy(_root, marker with { OperationId = new string('a', 32) }, identity));
        Assert.False(store.IsOwnedBy(_root, marker with { OwnerToken = new string('b', 32) }, identity));
        Assert.False(store.IsOwnedBy(_root, marker with { ExpectedTargetPath = target + "-other" }, identity));
        Assert.False(store.IsOwnedBy(
            _root,
            marker,
            identity with { IdentityToken = "changed" }));
    }

    [Fact]
    public void CorruptMarkerFailsClosed()
    {
        File.WriteAllText(Path.Combine(_root, ProjectAccessOwnerMarkerStore.MarkerFileName), "invalid");
        var store = new ProjectAccessOwnerMarkerStore();

        Assert.False(store.TryRead(_root, out ProjectAccessOwnerMarker? marker));
        Assert.Null(marker);
    }

    [Fact]
    public void ExistingOwnerMarkerIsNeverOverwritten()
    {
        var identityProvider = new FileSystemProjectAccessIdentityProvider();
        ProjectAccessPathIdentity identity = identityProvider.Capture(_root);
        var store = new ProjectAccessOwnerMarkerStore();
        ProjectAccessOwnerMarker first = store.Write(_root, Path.Combine(_root, "target-a"), identity);

        Assert.Throws<IOException>(() =>
            store.Write(_root, Path.Combine(_root, "target-b"), identity));

        Assert.True(store.TryRead(_root, out ProjectAccessOwnerMarker? persisted));
        Assert.Equal(first, persisted);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
