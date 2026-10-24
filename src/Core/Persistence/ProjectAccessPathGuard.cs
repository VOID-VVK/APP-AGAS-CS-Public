#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AppAgasCs.Core.Persistence;

public sealed class ProjectAccessPathGuard
{
    public const string ManagedDirectoryName = "AGAS Projects";
    public const string PathRejectedCode = "AGAS_PATH_REJECTED";
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };
    // macOS APFS may be either case-sensitive or case-insensitive. Treat only Windows as
    // unconditionally case-insensitive; ordinal comparison on macOS is the fail-closed policy,
    // while physical identity tokens remain authoritative for existing directories.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly IProjectAccessManagedRootProvider _managedRootProvider;
    private readonly IProjectAccessIdentityProvider _identityProvider;

    public ProjectAccessPathGuard(
        IProjectAccessDocumentsRootResolver? rootResolver = null,
        IProjectAccessIdentityProvider? identityProvider = null)
    {
        _managedRootProvider = rootResolver is null
            ? new EnvironmentProjectAccessManagedRootProvider()
            : new DocumentsProjectAccessManagedRootProvider(rootResolver);
        _identityProvider = identityProvider ?? new FileSystemProjectAccessIdentityProvider();
    }

    public ProjectAccessPathGuard(
        IProjectAccessManagedRootProvider managedRootProvider,
        IProjectAccessIdentityProvider? identityProvider = null)
    {
        _managedRootProvider = managedRootProvider
            ?? throw new ArgumentNullException(nameof(managedRootProvider));
        _identityProvider = identityProvider ?? new FileSystemProjectAccessIdentityProvider();
    }

    public ProjectAccessValidationResult PrepareCreateTarget(string name)
    {
        if (!TryNormalizeName(name, out string displayName, out string directoryName, out string nameError))
            return ProjectAccessValidationResult.Fail(PathRejectedCode, nameError);

        string managedRootSource;
        try
        {
            managedRootSource = _managedRootProvider.ResolveManagedRoot();
        }
        catch
        {
            return ProjectAccessValidationResult.Fail(PathRejectedCode, "The managed project root is unavailable.");
        }
        if (string.IsNullOrWhiteSpace(managedRootSource) || !Path.IsPathRooted(managedRootSource))
            return ProjectAccessValidationResult.Fail(PathRejectedCode, "The managed project root is unavailable.");

        string managedRoot;
        string target;
        try
        {
            managedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(managedRootSource));
            target = Path.GetFullPath(Path.Combine(managedRoot, directoryName));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return ProjectAccessValidationResult.Fail(PathRejectedCode, "The managed project path is invalid.");
        }

        string? managedRootParent = Path.GetDirectoryName(managedRoot);
        if (string.IsNullOrEmpty(managedRootParent) || !Directory.Exists(managedRootParent))
            return ProjectAccessValidationResult.Fail(PathRejectedCode, "The managed project root parent is not accessible.");
        if (!string.Equals(Path.GetDirectoryName(target), managedRoot, PathComparison))
            return ProjectAccessValidationResult.Fail(PathRejectedCode, "The project target escaped the managed root.");
        if (!TryCaptureSafeDirectory(managedRootParent, out ProjectAccessPathIdentity parentBefore))
            return ProjectAccessValidationResult.Fail(PathRejectedCode, "The managed project root parent identity is unsafe.");
        if (!ValidateExistingChain(managedRoot, out string chainError))
            return ProjectAccessValidationResult.Fail(PathRejectedCode, chainError);
        if (!TryCaptureIdentity(managedRoot, out ProjectAccessPathIdentity rootBefore))
            return ProjectAccessValidationResult.Fail(PathRejectedCode, "The managed project root identity could not be verified.");
        if (rootBefore.Exists && (!rootBefore.IsDirectory || rootBefore.IsLinkOrReparse))
            return ProjectAccessValidationResult.Fail(PathRejectedCode, "The managed project root identity is unsafe.");
        if (!TryCaptureIdentity(target, out ProjectAccessPathIdentity targetIdentity))
            return ProjectAccessValidationResult.Fail(PathRejectedCode, "The project target identity could not be verified.");
        if (targetIdentity.Exists || targetIdentity.IsLinkOrReparse)
            return ProjectAccessValidationResult.Fail(PathRejectedCode, "The project target already exists.");

        try
        {
            Directory.CreateDirectory(managedRoot);
        }
        catch
        {
            return ProjectAccessValidationResult.Fail(PathRejectedCode, "The managed project root could not be created.");
        }

        if (!ValidateExistingChain(managedRoot, out chainError))
            return ProjectAccessValidationResult.Fail(PathRejectedCode, chainError);
        if (!TryCaptureSafeDirectory(managedRootParent, out ProjectAccessPathIdentity parentAfter)
            || !SameIdentity(parentBefore, parentAfter))
        {
            return ProjectAccessValidationResult.Fail(PathRejectedCode, "The managed project root parent identity changed.");
        }
        if (!TryCaptureSafeDirectory(managedRoot, out ProjectAccessPathIdentity rootIdentity))
            return ProjectAccessValidationResult.Fail(PathRejectedCode, "The managed project root identity could not be verified.");
        if (rootBefore.Exists && !SameIdentity(rootBefore, rootIdentity))
            return ProjectAccessValidationResult.Fail(PathRejectedCode, "The managed project root identity changed.");
        if (!TryCaptureIdentity(target, out ProjectAccessPathIdentity targetAfter)
            || targetAfter.Exists
            || targetAfter.IsLinkOrReparse)
        {
            return ProjectAccessValidationResult.Fail(PathRejectedCode, "The project target appeared during validation.");
        }

        return ProjectAccessValidationResult.Ok(new(
            displayName,
            directoryName,
            managedRoot,
            target,
            rootIdentity));
    }

    public bool IdentityStillMatches(ProjectAccessPathIdentity expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!TryCaptureIdentity(expected.FullPath, out ProjectAccessPathIdentity current))
            return false;
        return current.Exists
            && current.IsDirectory == expected.IsDirectory
            && !current.IsLinkOrReparse
            && string.Equals(current.IdentityToken, expected.IdentityToken, StringComparison.Ordinal);
    }

    private bool ValidateExistingChain(string managedRoot, out string error)
    {
        string current = Path.GetPathRoot(managedRoot) ?? string.Empty;
        foreach (string segment in Path.GetRelativePath(current, managedRoot)
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!TryCaptureIdentity(current, out ProjectAccessPathIdentity identity))
            {
                error = "The managed project path identity could not be verified.";
                return false;
            }
            if (!identity.Exists)
                continue;
            if (!identity.IsDirectory || identity.IsLinkOrReparse)
            {
                error = "The managed project path contains a link, reparse point, or non-directory ancestor.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool TryNormalizeName(
        string name,
        out string displayName,
        out string directoryName,
        out string error)
    {
        displayName = name?.Trim() ?? string.Empty;
        directoryName = string.Empty;
        error = string.Empty;
        if (displayName.Length == 0 || displayName.EnumerateRunes().Count() > 80)
        {
            error = "The project name must contain 1 to 80 characters.";
            return false;
        }

        if (displayName is "." or ".." || displayName.Contains('\0'))
        {
            error = "The project name contains a traversal segment or NUL.";
            return false;
        }

        var normalized = new StringBuilder(displayName.Length);
        bool previousSeparator = false;
        foreach (char character in displayName)
        {
            bool replace = character is '/' or '\\'
                || char.IsWhiteSpace(character)
                || Path.GetInvalidFileNameChars().Contains(character);
            if (replace)
            {
                if (!previousSeparator)
                    normalized.Append('-');
                previousSeparator = true;
            }
            else
            {
                normalized.Append(character);
                previousSeparator = false;
            }
        }

        directoryName = normalized.ToString().Trim('-', ' ', '.');
        string reservedStem = directoryName.Split('.', 2)[0];
        if (directoryName.Length == 0 || WindowsReservedNames.Contains(reservedStem))
        {
            error = "The project name resolves to an empty or reserved directory name.";
            return false;
        }

        // The normalized value is both the managed directory and user-visible project name;
        // no path-like separator or platform-invalid character survives on the wire or disk.
        displayName = directoryName;

        return true;
    }

    private bool TryCaptureIdentity(string path, out ProjectAccessPathIdentity identity)
    {
        try
        {
            identity = _identityProvider.Capture(path);
            return true;
        }
        catch
        {
            identity = null!;
            return false;
        }
    }

    private bool TryCaptureSafeDirectory(string path, out ProjectAccessPathIdentity identity) =>
        TryCaptureIdentity(path, out identity)
        && identity.Exists
        && identity.IsDirectory
        && !identity.IsLinkOrReparse;

    private static bool SameIdentity(
        ProjectAccessPathIdentity left,
        ProjectAccessPathIdentity right) =>
        string.Equals(left.FullPath, right.FullPath, PathComparison)
        && left.Exists == right.Exists
        && left.IsDirectory == right.IsDirectory
        && left.IsLinkOrReparse == right.IsLinkOrReparse
        && string.Equals(left.IdentityToken, right.IdentityToken, StringComparison.Ordinal);
}
