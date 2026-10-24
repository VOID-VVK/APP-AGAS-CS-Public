#nullable enable

using System;
using System.IO;
using System.Text;

namespace AppAgasCs.Core.Persistence;

public sealed class ProjectAccessOwnerMarkerStore
{
    public const string MarkerFileName = ".agas-create-owner";
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public ProjectAccessOwnerMarker Write(
        string ownedDirectory,
        string expectedTargetPath,
        ProjectAccessPathIdentity ownedIdentity)
    {
        ArgumentNullException.ThrowIfNull(ownedIdentity);
        if (!ownedIdentity.Exists || !ownedIdentity.IsDirectory || ownedIdentity.IsLinkOrReparse)
            throw new InvalidOperationException("An owner marker requires a safe existing directory identity.");

        string fullOwnedDirectory = Path.GetFullPath(ownedDirectory);
        if (!string.Equals(fullOwnedDirectory, ownedIdentity.FullPath, PathComparison))
            throw new InvalidOperationException("The owner marker directory does not match its captured identity.");

        string operationId = Guid.NewGuid().ToString("N");
        string ownerToken = Guid.NewGuid().ToString("N");
        string fullTarget = Path.GetFullPath(expectedTargetPath);
        string markerPath = Path.Combine(fullOwnedDirectory, MarkerFileName);
        string temporaryPath = Path.Combine(fullOwnedDirectory, $"{MarkerFileName}.{operationId}.tmp");
        string[] lines =
        {
            "version=1",
            $"operationId={operationId}",
            $"ownerToken={ownerToken}",
            $"expectedTarget={Encode(fullTarget)}",
            $"ownedIdentity={ownedIdentity.IdentityToken}",
        };
        File.WriteAllLines(temporaryPath, lines, new UTF8Encoding(false));
        try
        {
            File.Move(temporaryPath, markerPath, overwrite: false);
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
        return new(operationId, ownerToken, fullTarget, ownedIdentity.IdentityToken);
    }

    public bool TryRead(string ownedDirectory, out ProjectAccessOwnerMarker? marker)
    {
        marker = null;
        string markerPath = Path.Combine(Path.GetFullPath(ownedDirectory), MarkerFileName);
        if (!File.Exists(markerPath))
            return false;

        try
        {
            string[] lines = File.ReadAllLines(markerPath, Encoding.UTF8);
            if (lines.Length != 5 || lines[0] != "version=1")
                return false;
            string operationId = Value(lines[1], "operationId=");
            string ownerToken = Value(lines[2], "ownerToken=");
            string expectedTarget = Decode(Value(lines[3], "expectedTarget="));
            string identity = Value(lines[4], "ownedIdentity=");
            if (operationId.Length != 32 || ownerToken.Length != 32 || identity.Length == 0)
                return false;

            marker = new(operationId, ownerToken, Path.GetFullPath(expectedTarget), identity);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsOwnedBy(
        string ownedDirectory,
        ProjectAccessOwnerMarker expected,
        ProjectAccessPathIdentity currentIdentity)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(currentIdentity);
        return TryRead(ownedDirectory, out ProjectAccessOwnerMarker? marker)
            && marker is not null
            && string.Equals(marker.OperationId, expected.OperationId, StringComparison.Ordinal)
            && string.Equals(marker.OwnerToken, expected.OwnerToken, StringComparison.Ordinal)
            && string.Equals(marker.ExpectedTargetPath, expected.ExpectedTargetPath, PathComparison)
            && string.Equals(marker.OwnedDirectoryIdentity, expected.OwnedDirectoryIdentity, StringComparison.Ordinal)
            && string.Equals(marker.OwnedDirectoryIdentity, currentIdentity.IdentityToken, StringComparison.Ordinal)
            && currentIdentity.Exists
            && currentIdentity.IsDirectory
            && !currentIdentity.IsLinkOrReparse;
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Decode(string value) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(value));

    private static string Value(string line, string prefix)
    {
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
            throw new FormatException("Invalid project owner marker.");
        return line[prefix.Length..];
    }
}
