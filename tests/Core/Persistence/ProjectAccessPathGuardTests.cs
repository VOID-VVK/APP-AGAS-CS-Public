using AppAgasCs.Core.Persistence;
using Xunit;

namespace AppAgasCs.Tests.Core.Persistence;

public sealed class ProjectAccessPathGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "agas-project-access-tests",
        Guid.NewGuid().ToString("N"));

    public ProjectAccessPathGuardTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void InvalidDocumentsRootFailsClosedWithoutCwdFallback()
    {
        string before = Environment.CurrentDirectory;
        var guard = new ProjectAccessPathGuard(new FixedRootResolver(string.Empty));

        ProjectAccessValidationResult result = guard.PrepareCreateTarget("Project");

        Assert.False(result.Success);
        Assert.Equal(ProjectAccessPathGuard.PathRejectedCode, result.Code);
        Assert.Null(result.Target);
        Assert.Equal(before, Environment.CurrentDirectory);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("CON")]
    [InlineData("Lpt1.txt")]
    public void UnsafeNamesAreRejected(string name)
    {
        var guard = new ProjectAccessPathGuard(new FixedRootResolver(_root), new StableIdentityProvider());

        ProjectAccessValidationResult result = guard.PrepareCreateTarget(name);

        Assert.False(result.Success);
        Assert.Equal(ProjectAccessPathGuard.PathRejectedCode, result.Code);
    }

    [Theory]
    [InlineData("../escape", "escape")]
    [InlineData("folder/name", "folder-name")]
    [InlineData("folder\\name", "folder-name")]
    [InlineData("many   spaces/name", "many-spaces-name")]
    public void SeparatorsInvalidCharactersAndWhitespaceAreNormalizedInsideManagedRoot(
        string name,
        string expectedDirectoryName)
    {
        var guard = new ProjectAccessPathGuard(new FixedRootResolver(_root), new StableIdentityProvider());

        ProjectAccessValidationResult result = guard.PrepareCreateTarget(name);

        Assert.True(result.Success);
        Assert.Equal(expectedDirectoryName, result.Target!.DirectoryName);
        Assert.Equal(
            Path.Combine(_root, ProjectAccessPathGuard.ManagedDirectoryName, expectedDirectoryName),
            result.Target.TargetPath);
    }

    [Fact]
    public void SafeNameProducesOnlyManagedRootTarget()
    {
        var guard = new ProjectAccessPathGuard(new FixedRootResolver(_root), new StableIdentityProvider());

        ProjectAccessValidationResult result = guard.PrepareCreateTarget("  My   Project  ");

        Assert.True(result.Success);
        Assert.NotNull(result.Target);
        Assert.Equal("My-Project", result.Target.DisplayName);
        Assert.Equal("My-Project", result.Target.DirectoryName);
        Assert.Equal(Path.Combine(_root, "AGAS Projects"), result.Target.ManagedRoot);
        Assert.Equal(Path.Combine(_root, "AGAS Projects", "My-Project"), result.Target.TargetPath);
        Assert.True(result.Target.ManagedRootIdentity.Exists);
    }

    [Fact]
    public void ExistingEmptyTargetIsRejected()
    {
        string target = Path.Combine(_root, "AGAS Projects", "Existing");
        Directory.CreateDirectory(target);
        var guard = new ProjectAccessPathGuard(new FixedRootResolver(_root), new StableIdentityProvider());

        ProjectAccessValidationResult result = guard.PrepareCreateTarget("Existing");

        Assert.False(result.Success);
        Assert.Contains("already exists", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SymlinkManagedRootIsRejected()
    {
        string external = Path.Combine(_root, "external");
        Directory.CreateDirectory(external);
        string managed = Path.Combine(_root, "AGAS Projects");
        try
        {
            Directory.CreateSymbolicLink(managed, external);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var guard = new ProjectAccessPathGuard(new FixedRootResolver(_root));

        ProjectAccessValidationResult result = guard.PrepareCreateTarget("Project");

        Assert.False(result.Success);
        Assert.Contains("link", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InjectedIdentityDriftIsDetected()
    {
        var identities = new ChangingIdentityProvider();
        var guard = new ProjectAccessPathGuard(new FixedRootResolver(_root), identities);
        ProjectAccessValidationResult result = guard.PrepareCreateTarget("Project");
        Assert.True(result.Success);

        identities.ChangeIdentity = true;

        Assert.False(guard.IdentityStillMatches(result.Target!.ManagedRootIdentity));
    }

    [Fact]
    public void PhysicalIdentityIsStableAcrossAnAncestorSymlinkAlias()
    {
        string physicalParent = Path.Combine(_root, "physical");
        string physicalDirectory = Path.Combine(physicalParent, "project");
        string aliasParent = Path.Combine(_root, "alias");
        Directory.CreateDirectory(physicalDirectory);
        try
        {
            Directory.CreateSymbolicLink(aliasParent, physicalParent);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var provider = new FileSystemProjectAccessIdentityProvider();
        ProjectAccessPathIdentity physical = provider.Capture(physicalDirectory);
        ProjectAccessPathIdentity alias = provider.Capture(Path.Combine(aliasParent, "project"));

        Assert.True(physical.Exists && physical.IsDirectory && !physical.IsLinkOrReparse);
        Assert.True(alias.Exists && alias.IsDirectory && !alias.IsLinkOrReparse);
        Assert.NotEqual(physical.FullPath, alias.FullPath);
        Assert.Equal(physical.IdentityToken, alias.IdentityToken);
    }

    [Fact]
    public void ReplacingAPathWithANewDirectoryChangesPhysicalIdentity()
    {
        string path = Path.Combine(_root, "identity-target");
        string moved = Path.Combine(_root, "identity-original");
        Directory.CreateDirectory(path);
        var provider = new FileSystemProjectAccessIdentityProvider();
        ProjectAccessPathIdentity original = provider.Capture(path);

        Directory.Move(path, moved);
        Directory.CreateDirectory(path);
        ProjectAccessPathIdentity replacement = provider.Capture(path);

        Assert.NotEqual(original.IdentityToken, replacement.IdentityToken);
        Assert.Equal(original.IdentityToken, provider.Capture(moved).IdentityToken);
    }

    [Fact]
    public void FinalSymlinkIsReportedAsUnsafeIdentity()
    {
        string target = Path.Combine(_root, "link-target");
        string link = Path.Combine(_root, "link");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        ProjectAccessPathIdentity identity = new FileSystemProjectAccessIdentityProvider().Capture(link);

        Assert.True(identity.Exists);
        Assert.True(identity.IsLinkOrReparse);
    }

    [Fact]
    public void IdentityInspectionFailureRejectsCreateWithoutThrowing()
    {
        var guard = new ProjectAccessPathGuard(
            new FixedRootResolver(_root),
            new ThrowingIdentityProvider());

        ProjectAccessValidationResult result = guard.PrepareCreateTarget("Project");

        Assert.False(result.Success);
        Assert.Equal(ProjectAccessPathGuard.PathRejectedCode, result.Code);
        Assert.Contains("identity", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManagedRootParentIdentityDriftDuringPreparationFailsClosed()
    {
        var identities = new DriftOnSecondCaptureIdentityProvider(_root);
        var guard = new ProjectAccessPathGuard(new FixedRootResolver(_root), identities);

        ProjectAccessValidationResult result = guard.PrepareCreateTarget("Project");

        Assert.False(result.Success);
        Assert.Equal(ProjectAccessPathGuard.PathRejectedCode, result.Code);
    }

    [Fact]
    public void TargetAppearingAfterInitialCheckFailsClosed()
    {
        string target = Path.Combine(_root, "AGAS Projects", "Project");
        var identities = new TargetAppearsOnSecondCaptureIdentityProvider(target);
        var guard = new ProjectAccessPathGuard(new FixedRootResolver(_root), identities);

        ProjectAccessValidationResult result = guard.PrepareCreateTarget("Project");

        Assert.False(result.Success);
        Assert.Equal(ProjectAccessPathGuard.PathRejectedCode, result.Code);
    }

    [Fact]
    public void FileSystemIdentityChangesWhenDirectoryIsReplacedWithSameCreationTime()
    {
        string path = Path.Combine(_root, "identity-root");
        Directory.CreateDirectory(path);
        DateTime creationTime = Directory.GetCreationTimeUtc(path);
        var provider = new FileSystemProjectAccessIdentityProvider();
        ProjectAccessPathIdentity before = provider.Capture(path);

        Directory.Delete(path);
        Directory.CreateDirectory(path);
        Directory.SetCreationTimeUtc(path, creationTime);
        ProjectAccessPathIdentity after = provider.Capture(path);

        Assert.NotEqual(before.IdentityToken, after.IdentityToken);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FixedRootResolver(string root) : IProjectAccessDocumentsRootResolver
    {
        public string ResolveDocumentsRoot() => root;
    }

    private sealed class ChangingIdentityProvider : IProjectAccessIdentityProvider
    {
        public bool ChangeIdentity { get; set; }

        public ProjectAccessPathIdentity Capture(string path)
        {
            string fullPath = Path.GetFullPath(path);
            bool exists = Directory.Exists(fullPath);
            return new(fullPath, ChangeIdentity ? "changed" : "original", exists, exists, false);
        }
    }

    private sealed class StableIdentityProvider : IProjectAccessIdentityProvider
    {
        public ProjectAccessPathIdentity Capture(string path)
        {
            string fullPath = Path.GetFullPath(path);
            bool exists = Directory.Exists(fullPath);
            return new(fullPath, $"identity:{fullPath}", exists, exists, false);
        }
    }

    private sealed class DriftOnSecondCaptureIdentityProvider(string driftPath) : IProjectAccessIdentityProvider
    {
        private readonly string _driftPath = Path.GetFullPath(driftPath);
        private int _captures;

        public ProjectAccessPathIdentity Capture(string path)
        {
            string fullPath = Path.GetFullPath(path);
            bool exists = Directory.Exists(fullPath);
            string identity = string.Equals(fullPath, _driftPath, StringComparison.Ordinal)
                && ++_captures > 1
                    ? "changed"
                    : $"identity:{fullPath}";
            return new(fullPath, identity, exists, exists, false);
        }
    }

    private sealed class TargetAppearsOnSecondCaptureIdentityProvider(string targetPath) : IProjectAccessIdentityProvider
    {
        private readonly string _targetPath = Path.GetFullPath(targetPath);
        private int _targetCaptures;

        public ProjectAccessPathIdentity Capture(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (string.Equals(fullPath, _targetPath, StringComparison.Ordinal))
            {
                bool appears = ++_targetCaptures > 1;
                return new(fullPath, appears ? "appeared" : "missing", appears, appears, false);
            }

            bool exists = Directory.Exists(fullPath);
            return new(fullPath, $"identity:{fullPath}", exists, exists, false);
        }
    }

    private sealed class ThrowingIdentityProvider : IProjectAccessIdentityProvider
    {
        public ProjectAccessPathIdentity Capture(string path) =>
            throw new IOException("identity unavailable");
    }
}
