#nullable enable

using System;

namespace AppAgasCs.Core.Persistence;

public sealed record ProjectAccessPathIdentity(
    string FullPath,
    string IdentityToken,
    bool Exists,
    bool IsDirectory,
    bool IsLinkOrReparse);

public interface IProjectAccessIdentityProvider
{
    ProjectAccessPathIdentity Capture(string path);
}

public interface IProjectAccessDocumentsRootResolver
{
    string ResolveDocumentsRoot();
}

public sealed class EnvironmentProjectAccessDocumentsRootResolver : IProjectAccessDocumentsRootResolver
{
    public string ResolveDocumentsRoot() =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
}

public sealed record ProjectAccessCreateTarget(
    string DisplayName,
    string DirectoryName,
    string ManagedRoot,
    string TargetPath,
    ProjectAccessPathIdentity ManagedRootIdentity);

public sealed record ProjectAccessOwnerMarker(
    string OperationId,
    string OwnerToken,
    string ExpectedTargetPath,
    string OwnedDirectoryIdentity);

public sealed record ProjectAccessValidationResult(
    bool Success,
    string Code,
    string Message,
    ProjectAccessCreateTarget? Target = null)
{
    public static ProjectAccessValidationResult Ok(ProjectAccessCreateTarget target) =>
        new(true, string.Empty, string.Empty, target);

    public static ProjectAccessValidationResult Fail(string code, string message) =>
        new(false, code, message);
}
