#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Mcp.Contracts;

namespace AppAgasCs.Application;

public sealed record AgasAssetImportResult(
    bool Success,
    string Code,
    string RelativePath,
    string Extension,
    long SizeBytes,
    int PixelWidth,
    int PixelHeight)
{
    public static AgasAssetImportResult Fail(string code) => new(false, code, string.Empty, string.Empty, 0, 0, 0);
}

public interface IAgasDurableCommitGate
{
    bool TryBeginDurableCommit();
}

public sealed class AgasAllowDurableCommitGate : IAgasDurableCommitGate
{
    public bool TryBeginDurableCommit() => true;
}

internal interface IAgasImportCopyFaultInjector
{
    void AfterTemporarySlotRegistered(string path);
    void AfterExchangePublished() { }
}

public sealed class AgasControlledAssetImportService
{
    private sealed record CleanupFileFingerprint(
        string CanonicalPath,
        long Length,
        long CreationTimeUtcTicks,
        long LastWriteTimeUtcTicks,
        string Sha256);

    private sealed record CleanupDirectoryFingerprint(
        string CanonicalPath,
        long CreationTimeUtcTicks);

    private sealed record CleanupRegistration(
        string ProjectRoot,
        string OperationId,
        IReadOnlyList<CleanupDirectoryFingerprint> Ancestors,
        CleanupDirectoryFingerprint OperationDirectory,
        CleanupFileFingerprint Marker,
        CleanupFileFingerprint Payload,
        bool PayloadPresent,
        string? PublishedFinalPath);

    private sealed class EarlyCleanupRegistration
    {
        public required string ProjectRoot { get; init; }
        public required string RelativeDirectory { get; init; }
        public required string OperationDirectory { get; init; }
        public required string OperationDirectoryIdentity { get; init; }
        public required string MarkerPath { get; init; }
        public required string PayloadPath { get; init; }
        public string? MarkerIdentity { get; set; }
        public string? PayloadIdentity { get; set; }
    }

    public const string InvalidCode = AgasMcpErrorCodes.ImportInvalid;
    public const string TooLargeCode = AgasMcpErrorCodes.ImportTooLarge;
    public const string NotFoundCode = AgasMcpErrorCodes.ImportSourceNotFound;
    public const string CanceledCode = AgasMcpErrorCodes.Stopped;

    private readonly ScenePackagePathGuard _guard;
    private readonly TimeSpan _timeout;
    private readonly IScenePackageImageDecoder _imageDecoder;
    private readonly IAgasImportCopyFaultInjector? _copyFaultInjector;

    public AgasControlledAssetImportService(
        ScenePackagePathGuard? guard = null,
        TimeSpan? timeout = null,
        IScenePackageImageDecoder? imageDecoder = null)
        : this(guard, timeout, imageDecoder, null)
    {
    }

    internal AgasControlledAssetImportService(
        ScenePackagePathGuard? guard,
        TimeSpan? timeout,
        IScenePackageImageDecoder? imageDecoder,
        IAgasImportCopyFaultInjector? copyFaultInjector)
    {
        _guard = guard ?? new ScenePackagePathGuard();
        _timeout = timeout ?? ScenePackageSafetyLimits.HeavyOperationTimeout;
        _imageDecoder = imageDecoder ?? new GodotScenePackageImageDecoder();
        _copyFaultInjector = copyFaultInjector;
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public AgasAssetImportResult Import(
        string agasHome,
        string projectRoot,
        string sourceBasename,
        IAgasDurableCommitGate? commitGate = null,
        CancellationToken cancellationToken = default)
    {
        if (!_guard.IsSafeBasename(sourceBasename)) return AgasAssetImportResult.Fail(InvalidCode);
        string agasRoot;
        try { agasRoot = Path.GetFullPath(agasHome); }
        catch { return AgasAssetImportResult.Fail(InvalidCode); }
        if (!_guard.TryResolveDirectory(agasRoot, "import-inbox/assets", mustExist: true, out string inboxRoot))
            return AgasAssetImportResult.Fail(NotFoundCode);
        if (!_guard.TryResolveExistingFile(inboxRoot, sourceBasename, out string source))
            return AgasAssetImportResult.Fail(NotFoundCode);
        return ImportResolved(projectRoot, sourceBasename, source, expectedSourceIdentity: null, commitGate, cancellationToken);
    }

    public AgasAssetImportResult ImportBySourceRef(
        string agasHome,
        string projectRoot,
        string sourceRef,
        AgasImportSourceRefBroker broker,
        IAgasDurableCommitGate? commitGate = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);
        if (!broker.TryResolve(sourceRef, AgasImportKind.Asset, agasHome, out AgasImportSourceGrant? grant)
            || grant == null)
            return AgasAssetImportResult.Fail(NotFoundCode);
        return ImportResolved(
            projectRoot,
            grant.SafeName,
            grant.SourcePath,
            grant.NativeIdentities[grant.SourcePath],
            commitGate,
            cancellationToken);
    }

    private AgasAssetImportResult ImportResolved(
        string projectRoot,
        string sourceBasename,
        string source,
        string? expectedSourceIdentity,
        IAgasDurableCommitGate? commitGate,
        CancellationToken cancellationToken)
    {
        commitGate ??= new AgasAllowDurableCommitGate();
        string extension = Path.GetExtension(sourceBasename).ToLowerInvariant();
        if (!ProjectPathRules.AllowedImageExtensionsWithDot.Contains(extension))
            return AgasAssetImportResult.Fail(InvalidCode);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        CancellationToken token = timeout.Token;
        string operationId = Guid.NewGuid().ToString("N");
        string relativeStaging = $"assets/imported/.staging/{operationId}";
        string stagingDirectory = Path.Combine(projectRoot, "assets", "imported", ".staging", operationId);
        string markerPath = Path.Combine(stagingDirectory, "owner.v1");
        string stagingFile = Path.Combine(stagingDirectory, "payload.tmp");
        CleanupRegistration? cleanup = null;
        EarlyCleanupRegistration? earlyCleanup = null;
        try
        {
            token.ThrowIfCancellationRequested();
            FileInfo before = new(source);
            before.Refresh();
            if (before.Length > ScenePackageSafetyLimits.MaximumImageBytes)
                return AgasAssetImportResult.Fail(TooLargeCode);
            ProjectAccessPathIdentity beforeIdentity = _guard.Capture(source);
            if (expectedSourceIdentity != null
                && !string.Equals(beforeIdentity.IdentityToken, expectedSourceIdentity, StringComparison.Ordinal))
                return AgasAssetImportResult.Fail(NotFoundCode);

            if (!_guard.TryResolveDirectory(projectRoot, "assets/imported", mustExist: false, out _))
                return AgasAssetImportResult.Fail(InvalidCode);
            if (!_guard.TryResolveDirectory(projectRoot, "assets/imported/.staging", mustExist: false, out _))
                return AgasAssetImportResult.Fail(InvalidCode);
            Directory.CreateDirectory(stagingDirectory);
            if (!_guard.TryResolveDirectory(projectRoot, relativeStaging, mustExist: true, out stagingDirectory))
                return AgasAssetImportResult.Fail(InvalidCode);
            ProjectAccessPathIdentity operationDirectoryIdentity = _guard.Capture(stagingDirectory);
            earlyCleanup = new EarlyCleanupRegistration
            {
                ProjectRoot = Path.GetFullPath(projectRoot),
                RelativeDirectory = relativeStaging,
                OperationDirectory = stagingDirectory,
                OperationDirectoryIdentity = operationDirectoryIdentity.IdentityToken,
                MarkerPath = markerPath,
                PayloadPath = stagingFile,
            };
            WriteControlledText(markerPath, operationId, identity => earlyCleanup.MarkerIdentity = identity);

            long copied = CopyBounded(
                source,
                stagingFile,
                ScenePackageSafetyLimits.MaximumImageBytes,
                token,
                identity => earlyCleanup.PayloadIdentity = identity);
            if (!_guard.TryResolveExistingFile(projectRoot, $"{relativeStaging}/payload.tmp", out stagingFile))
                return AgasAssetImportResult.Fail(InvalidCode);
            cleanup = CaptureCleanupRegistration(
                projectRoot, stagingDirectory, markerPath, stagingFile, operationId,
                payloadPresent: true, publishedFinalPath: null);
            earlyCleanup = null;
            (int width, int height) = ScenePackageImageInspector.Inspect(stagingFile, extension, _imageDecoder);
            FileInfo after = new(source);
            after.Refresh();
            ProjectAccessPathIdentity afterIdentity = _guard.Capture(source);
            if (before.Length != after.Length
                || before.LastWriteTimeUtc != after.LastWriteTimeUtc
                || beforeIdentity.IdentityToken != afterIdentity.IdentityToken)
                return AgasAssetImportResult.Fail(InvalidCode);

            token.ThrowIfCancellationRequested();
            if (!commitGate.TryBeginDurableCommit()) return AgasAssetImportResult.Fail(CanceledCode);
            string safeName = ScenePackageFileNamePolicy.SanitizeStem(sourceBasename);
            string relativeFinal = $"assets/imported/{operationId}-{safeName}{extension}";
            if (!_guard.TryResolveOwnedTarget(projectRoot, relativeFinal, out string finalPath)
                || File.Exists(finalPath) || Directory.Exists(finalPath))
                return AgasAssetImportResult.Fail(InvalidCode);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            File.Move(stagingFile, finalPath);
            cleanup = TryRefreshPublishedCleanupRegistration(
                cleanup, projectRoot, stagingDirectory, markerPath, stagingFile, operationId, finalPath);
            return new(true, string.Empty, relativeFinal, extension.TrimStart('.'), copied, width, height);
        }
        catch (OperationCanceledException)
        {
            return AgasAssetImportResult.Fail(CanceledCode);
        }
        catch (ScenePackageImageTooLargeException)
        {
            return AgasAssetImportResult.Fail(TooLargeCode);
        }
        catch (InvalidDataException)
        {
            return AgasAssetImportResult.Fail(InvalidCode);
        }
        catch
        {
            return AgasAssetImportResult.Fail(InvalidCode);
        }
        finally
        {
            if (cleanup != null) TryDeleteOwnedStaging(cleanup);
            else if (earlyCleanup != null) TryDeleteEarlyOwnedStaging(earlyCleanup);
        }
    }

    private long CopyBounded(
        string source,
        string destination,
        long maximum,
        CancellationToken token,
        Action<string> registerCreatedSlot)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        ProjectAccessPathIdentity identity = _guard.Capture(destination);
        if (!identity.Exists || identity.IsDirectory || identity.IsLinkOrReparse) throw new InvalidDataException();
        registerCreatedSlot(identity.IdentityToken);
        _copyFaultInjector?.AfterTemporarySlotRegistered(destination);
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            total += read;
            if (total > maximum) throw new InvalidDataException();
            output.Write(buffer, 0, read);
        }
        output.Flush(flushToDisk: true);
        return total;
    }

    private void WriteControlledText(string destination, string value, Action<string> registerCreatedSlot)
    {
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        ProjectAccessPathIdentity identity = _guard.Capture(destination);
        if (!identity.Exists || identity.IsDirectory || identity.IsLinkOrReparse) throw new InvalidDataException();
        registerCreatedSlot(identity.IdentityToken);
        _copyFaultInjector?.AfterTemporarySlotRegistered(destination);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
        output.Write(bytes, 0, bytes.Length);
        output.Flush(flushToDisk: true);
    }

    private CleanupRegistration CaptureCleanupRegistration(
        string projectRoot,
        string stagingDirectory,
        string markerPath,
        string payloadPath,
        string operationId,
        bool payloadPresent,
        string? publishedFinalPath,
        CleanupFileFingerprint? existingPayloadFingerprint = null)
    {
        string project = Path.GetFullPath(projectRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string operationDirectory = Path.GetFullPath(stagingDirectory);
        string expectedOperationDirectory = Path.Combine(
            project, "assets", "imported", ".staging", operationId);
        string marker = Path.GetFullPath(markerPath);
        string payload = Path.GetFullPath(payloadPath);
        if (!string.Equals(operationDirectory, expectedOperationDirectory, StringComparison.Ordinal)
            || !string.Equals(marker, Path.Combine(operationDirectory, "owner.v1"), StringComparison.Ordinal)
            || !string.Equals(payload, Path.Combine(operationDirectory, "payload.tmp"), StringComparison.Ordinal)
            || !string.Equals(File.ReadAllText(marker), operationId, StringComparison.Ordinal))
            throw new InvalidDataException("Controlled import cleanup identity is invalid.");

        CleanupFileFingerprint payloadFingerprint = payloadPresent
            ? CaptureFileFingerprint(payload)
            : existingPayloadFingerprint
              ?? throw new InvalidDataException("Controlled import payload identity is unavailable.");
        if (!payloadPresent
            && (string.IsNullOrEmpty(publishedFinalPath)
                || !FileFingerprintMatchesMovedFile(payloadFingerprint, publishedFinalPath)))
            throw new InvalidDataException("Published controlled import payload identity changed.");

        string[] ancestorPaths =
        {
            project,
            Path.Combine(project, "assets"),
            Path.Combine(project, "assets", "imported"),
            Path.Combine(project, "assets", "imported", ".staging"),
        };
        CleanupDirectoryFingerprint[] ancestors = ancestorPaths
            .Select(CaptureDirectoryFingerprint)
            .ToArray();
        return new CleanupRegistration(
            project,
            operationId,
            ancestors,
            CaptureDirectoryFingerprint(operationDirectory),
            CaptureFileFingerprint(marker),
            payloadFingerprint,
            payloadPresent,
            publishedFinalPath == null ? null : Path.GetFullPath(publishedFinalPath));
    }

    private CleanupRegistration TryRefreshPublishedCleanupRegistration(
        CleanupRegistration current,
        string projectRoot,
        string stagingDirectory,
        string markerPath,
        string payloadPath,
        string operationId,
        string publishedFinalPath)
    {
        try
        {
            return CaptureCleanupRegistration(
                projectRoot,
                stagingDirectory,
                markerPath,
                payloadPath,
                operationId,
                payloadPresent: false,
                publishedFinalPath,
                current.Payload);
        }
        catch
        {
            return current;
        }
    }

    private void TryDeleteEarlyOwnedStaging(EarlyCleanupRegistration registration)
    {
        try
        {
            if (!_guard.TryResolveDirectory(
                    registration.ProjectRoot,
                    registration.RelativeDirectory,
                    mustExist: true,
                    out string operationDirectory)
                || !string.Equals(operationDirectory, registration.OperationDirectory, StringComparison.Ordinal)) return;
            ProjectAccessPathIdentity directoryIdentity = _guard.Capture(operationDirectory);
            if (!directoryIdentity.Exists || !directoryIdentity.IsDirectory || directoryIdentity.IsLinkOrReparse
                || !string.Equals(
                    directoryIdentity.IdentityToken,
                    registration.OperationDirectoryIdentity,
                    StringComparison.Ordinal)) return;

            var allowed = new Dictionary<string, string>(StringComparer.Ordinal);
            if (registration.MarkerIdentity != null)
                allowed[registration.MarkerPath] = registration.MarkerIdentity;
            if (registration.PayloadIdentity != null)
                allowed[registration.PayloadPath] = registration.PayloadIdentity;
            string[] entries = Directory.EnumerateFileSystemEntries(operationDirectory)
                .Select(Path.GetFullPath)
                .ToArray();
            if (entries.Length != allowed.Count || entries.Any(entry => !allowed.ContainsKey(entry))) return;
            foreach ((string path, string expectedIdentity) in allowed)
            {
                ProjectAccessPathIdentity identity = _guard.Capture(path);
                if (!identity.Exists || identity.IsDirectory || identity.IsLinkOrReparse
                    || !string.Equals(identity.IdentityToken, expectedIdentity, StringComparison.Ordinal)) return;
            }
            foreach (string path in allowed.Keys) File.Delete(path);
            Directory.Delete(operationDirectory, recursive: false);
        }
        catch
        {
        }
    }

    private void TryDeleteOwnedStaging(CleanupRegistration registration)
    {
        try
        {
            string operationDirectory = registration.OperationDirectory.CanonicalPath;
            if (!TryValidateCleanupAt(registration, operationDirectory, validateAncestorChain: true)) return;

            string stagingRoot = registration.Ancestors[^1].CanonicalPath;
            string holding = Path.Combine(
                stagingRoot, $".cleanup-{registration.OperationId}-{Guid.NewGuid():N}");
            if (_guard.Capture(holding).Exists) return;
            Directory.Move(operationDirectory, holding);
            if (_guard.Capture(operationDirectory).Exists
                || !TryValidateCleanupAt(registration, holding, validateAncestorChain: false))
            {
                TryRestoreControlledCleanup(operationDirectory, holding);
                return;
            }

            string heldPayload = Path.Combine(holding, "payload.tmp");
            string heldMarker = Path.Combine(holding, "owner.v1");
            if (registration.PayloadPresent) File.Delete(heldPayload);
            File.Delete(heldMarker);
            Directory.Delete(holding, recursive: false);
        }
        catch
        {
        }
    }

    private bool TryValidateCleanupAt(
        CleanupRegistration registration,
        string directory,
        bool validateAncestorChain)
    {
        string canonicalDirectory = Path.GetFullPath(directory);
        if (validateAncestorChain)
        {
            if (!string.Equals(canonicalDirectory, registration.OperationDirectory.CanonicalPath, StringComparison.Ordinal)
                || !_guard.TryResolveDirectory(
                    registration.ProjectRoot,
                    $"assets/imported/.staging/{registration.OperationId}",
                    mustExist: true,
                    out string resolved)
                || !string.Equals(resolved, canonicalDirectory, StringComparison.Ordinal)
                || !AncestorChainMatches(registration.Ancestors))
                return false;
        }
        else
        {
            string stagingRoot = registration.Ancestors[^1].CanonicalPath;
            if (!string.Equals(Path.GetDirectoryName(canonicalDirectory), stagingRoot, StringComparison.Ordinal)
                || !Path.GetFileName(canonicalDirectory).StartsWith(
                    $".cleanup-{registration.OperationId}-", StringComparison.Ordinal))
                return false;
        }

        if (!DirectoryFingerprintMatches(registration.OperationDirectory, canonicalDirectory)) return false;
        string marker = Path.Combine(canonicalDirectory, "owner.v1");
        string payload = Path.Combine(canonicalDirectory, "payload.tmp");
        var expected = new HashSet<string>(StringComparer.Ordinal) { marker };
        if (registration.PayloadPresent) expected.Add(payload);
        string[] entries = Directory.EnumerateFileSystemEntries(canonicalDirectory)
            .Select(Path.GetFullPath)
            .ToArray();
        if (entries.Length != expected.Count
            || entries.Any(entry => !string.Equals(
                Path.GetDirectoryName(entry), canonicalDirectory, StringComparison.Ordinal)
                || !expected.Contains(entry))
            || !FileFingerprintMatches(registration.Marker, marker)
            || !string.Equals(File.ReadAllText(marker), registration.OperationId, StringComparison.Ordinal))
            return false;
        if (registration.PayloadPresent)
            return FileFingerprintMatches(registration.Payload, payload);
        return registration.PublishedFinalPath != null
            && !_guard.Capture(payload).Exists
            && FileFingerprintMatchesMovedFile(registration.Payload, registration.PublishedFinalPath);
    }

    private bool AncestorChainMatches(IReadOnlyList<CleanupDirectoryFingerprint> ancestors)
    {
        for (int index = 0; index < ancestors.Count; index++)
        {
            CleanupDirectoryFingerprint ancestor = ancestors[index];
            if (!DirectoryFingerprintMatches(ancestor, ancestor.CanonicalPath)) return false;
            if (index > 0 && !string.Equals(
                    Path.GetDirectoryName(ancestor.CanonicalPath),
                    ancestors[index - 1].CanonicalPath,
                    StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private CleanupDirectoryFingerprint CaptureDirectoryFingerprint(string path)
    {
        string canonical = Path.GetFullPath(path);
        ProjectAccessPathIdentity identity = _guard.Capture(canonical);
        if (!identity.Exists || !identity.IsDirectory || identity.IsLinkOrReparse)
            throw new InvalidDataException("Controlled import directory identity is unavailable.");
        var info = new DirectoryInfo(canonical);
        info.Refresh();
        return new CleanupDirectoryFingerprint(
            canonical,
            info.CreationTimeUtc.Ticks);
    }

    private CleanupFileFingerprint CaptureFileFingerprint(string path)
    {
        string canonical = Path.GetFullPath(path);
        ProjectAccessPathIdentity identity = _guard.Capture(canonical);
        if (!identity.Exists || identity.IsDirectory || identity.IsLinkOrReparse)
            throw new InvalidDataException("Controlled import file identity is unavailable.");
        var info = new FileInfo(canonical);
        info.Refresh();
        using var stream = new FileStream(canonical, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new CleanupFileFingerprint(
            canonical,
            info.Length,
            info.CreationTimeUtc.Ticks,
            info.LastWriteTimeUtc.Ticks,
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    private bool DirectoryFingerprintMatches(CleanupDirectoryFingerprint expected, string path)
    {
        try
        {
            CleanupDirectoryFingerprint current = CaptureDirectoryFingerprint(path);
            // Directory membership updates LastWriteTime, and the path-guard identity token
            // includes the canonical path, so neither remains stable when the owned operation
            // directory is moved into its cleanup holding name. The callers validate the
            // expected canonical location; capture validates directory kind and rejects links.
            return current.CreationTimeUtcTicks == expected.CreationTimeUtcTicks;
        }
        catch { return false; }
    }

    private bool FileFingerprintMatches(CleanupFileFingerprint expected, string path)
    {
        try
        {
            CleanupFileFingerprint current = CaptureFileFingerprint(path);
            return current.Length == expected.Length
                && current.CreationTimeUtcTicks == expected.CreationTimeUtcTicks
                && current.LastWriteTimeUtcTicks == expected.LastWriteTimeUtcTicks
                && current.Sha256 == expected.Sha256;
        }
        catch { return false; }
    }

    private bool FileFingerprintMatchesMovedFile(CleanupFileFingerprint expected, string path) =>
        FileFingerprintMatches(expected, path);

    private static void TryRestoreControlledCleanup(string operationDirectory, string holding)
    {
        try
        {
            if (Directory.Exists(holding) && !Directory.Exists(operationDirectory) && !File.Exists(operationDirectory))
                Directory.Move(holding, operationDirectory);
        }
        catch { }
    }
}
