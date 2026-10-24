#nullable enable

using System;
using System.IO;

namespace AppAgasCs.Core.Persistence;

public interface IProjectAccessManagedRootProvider
{
    string ResolveManagedRoot();
}

public sealed class EnvironmentProjectAccessManagedRootProvider : IProjectAccessManagedRootProvider
{
    public const string TestManagedRootEnvironmentVariable = "AGAS_TEST_MANAGED_PROJECT_ROOT";

    private readonly IProjectAccessDocumentsRootResolver _documentsRootResolver;
    private readonly Func<string, string?> _environmentReader;

    public EnvironmentProjectAccessManagedRootProvider(
        IProjectAccessDocumentsRootResolver? documentsRootResolver = null,
        Func<string, string?>? environmentReader = null)
    {
        _documentsRootResolver = documentsRootResolver
            ?? new EnvironmentProjectAccessDocumentsRootResolver();
        _environmentReader = environmentReader ?? Environment.GetEnvironmentVariable;
    }

    public string ResolveManagedRoot()
    {
        string? explicitTestRoot = _environmentReader(
            TestManagedRootEnvironmentVariable);
        if (explicitTestRoot is not null)
        {
            return CanonicalizeAbsolute(explicitTestRoot);
        }

        string documentsRoot = _documentsRootResolver.ResolveDocumentsRoot();
        if (string.IsNullOrWhiteSpace(documentsRoot) || !Path.IsPathRooted(documentsRoot))
        {
            return string.Empty;
        }

        try
        {
            return CanonicalizeAbsolute(Path.Combine(
                documentsRoot,
                ProjectAccessPathGuard.ManagedDirectoryName));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    private static string CanonicalizeAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return string.Empty;
        }
    }
}

internal sealed class DocumentsProjectAccessManagedRootProvider : IProjectAccessManagedRootProvider
{
    private readonly IProjectAccessDocumentsRootResolver _documentsRootResolver;

    public DocumentsProjectAccessManagedRootProvider(
        IProjectAccessDocumentsRootResolver documentsRootResolver)
    {
        _documentsRootResolver = documentsRootResolver
            ?? throw new ArgumentNullException(nameof(documentsRootResolver));
    }

    public string ResolveManagedRoot()
    {
        string documentsRoot = _documentsRootResolver.ResolveDocumentsRoot();
        if (string.IsNullOrWhiteSpace(documentsRoot) || !Path.IsPathRooted(documentsRoot))
        {
            return string.Empty;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(
                documentsRoot,
                ProjectAccessPathGuard.ManagedDirectoryName)));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return string.Empty;
        }
    }
}
