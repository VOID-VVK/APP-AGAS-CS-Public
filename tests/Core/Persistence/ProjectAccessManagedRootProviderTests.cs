using AppAgasCs.Core.Persistence;
using Xunit;

namespace AppAgasCs.Tests.Core.Persistence;

public sealed class ProjectAccessManagedRootProviderTests : IDisposable
{
    private readonly string? _previousManagedRoot = Environment.GetEnvironmentVariable(
        EnvironmentProjectAccessManagedRootProvider.TestManagedRootEnvironmentVariable);
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".agas-managed-root-provider-tests",
        Guid.NewGuid().ToString("N"));

    public ProjectAccessManagedRootProviderTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ExplicitAcceptanceManagedRootIsConsumedByRealGuard()
    {
        string managedRoot = Path.Combine(_root, "acceptance-managed-root", ".");
        Environment.SetEnvironmentVariable(
            EnvironmentProjectAccessManagedRootProvider.TestManagedRootEnvironmentVariable,
            managedRoot);
        var provider = new EnvironmentProjectAccessManagedRootProvider(
            new FixedDocumentsRootResolver(Path.Combine(_root, "must-not-be-used")));
        string expectedRoot = provider.ResolveManagedRoot();
        var guard = new ProjectAccessPathGuard();

        ProjectAccessValidationResult result = guard.PrepareCreateTarget("Acceptance Project");

        Assert.True(result.Success, result.Message);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(managedRoot)), expectedRoot);
        Assert.Equal(expectedRoot, result.Target!.ManagedRoot);
        Assert.Equal(Path.Combine(expectedRoot, "Acceptance-Project"), result.Target.TargetPath);
    }

    [Fact]
    public void UnsetAcceptanceVariableUsesCanonicalDocumentsManagedRoot()
    {
        Environment.SetEnvironmentVariable(
            EnvironmentProjectAccessManagedRootProvider.TestManagedRootEnvironmentVariable,
            null);
        var provider = new EnvironmentProjectAccessManagedRootProvider(
            new FixedDocumentsRootResolver(_root));

        string managedRoot = provider.ResolveManagedRoot();

        Assert.Equal(
            Path.Combine(Path.GetFullPath(_root), ProjectAccessPathGuard.ManagedDirectoryName),
            managedRoot);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative-managed-root")]
    public void ExplicitInvalidAcceptanceRootFailsClosedWithoutDocumentsFallback(string invalidRoot)
    {
        string fallbackDocuments = Path.Combine(_root, "fallback-documents");
        Directory.CreateDirectory(fallbackDocuments);
        var provider = new EnvironmentProjectAccessManagedRootProvider(
            new FixedDocumentsRootResolver(fallbackDocuments),
            _ => invalidRoot);
        var guard = new ProjectAccessPathGuard(provider);

        ProjectAccessValidationResult result = guard.PrepareCreateTarget("Must Fail");

        Assert.False(result.Success);
        Assert.Equal(ProjectAccessPathGuard.PathRejectedCode, result.Code);
        Assert.False(Directory.Exists(Path.Combine(
            fallbackDocuments,
            ProjectAccessPathGuard.ManagedDirectoryName)));
    }

    [Fact]
    public void ExplicitAcceptanceManagedRootSymlinkIsRejectedByRealGuard()
    {
        string external = Path.Combine(_root, "external");
        string managedLink = Path.Combine(_root, "managed-link");
        Directory.CreateDirectory(external);
        try
        {
            Directory.CreateSymbolicLink(managedLink, external);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        Environment.SetEnvironmentVariable(
            EnvironmentProjectAccessManagedRootProvider.TestManagedRootEnvironmentVariable,
            managedLink);
        var guard = new ProjectAccessPathGuard(new EnvironmentProjectAccessManagedRootProvider());

        ProjectAccessValidationResult result = guard.PrepareCreateTarget("Must Fail");

        Assert.False(result.Success);
        Assert.Contains("link", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(
            EnvironmentProjectAccessManagedRootProvider.TestManagedRootEnvironmentVariable,
            _previousManagedRoot);
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FixedDocumentsRootResolver(string root) : IProjectAccessDocumentsRootResolver
    {
        public string ResolveDocumentsRoot() => root;
    }
}
