#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Mcp.Contracts;

namespace AppAgasCs.Application;

public sealed record AgasScenePackagePreparedImport
{
    public AgasScenePackagePreparedImport(
        bool success,
        string code,
        string operationId,
        string sceneId,
        string manifestText,
        IEnumerable<AgasScenePackageAssetMapping> assetMappings,
        IEnumerable<AgasScenePackagePreparedArtifact>? packageArtifacts = null)
    {
        Success = success;
        Code = code;
        OperationId = operationId;
        SceneId = sceneId;
        ManifestText = manifestText;
        AssetMappings = new ReadOnlyCollection<AgasScenePackageAssetMapping>(assetMappings.ToArray());
        CopiedAssetRelativePaths = new ReadOnlyCollection<string>(AssetMappings.Select(item => item.ProjectRelativePath).ToArray());
        PackageArtifacts = new ReadOnlyCollection<AgasScenePackagePreparedArtifact>(
            (packageArtifacts ?? Array.Empty<AgasScenePackagePreparedArtifact>()).ToArray());
    }

    public bool Success { get; }
    public string Code { get; }
    public string OperationId { get; }
    public string SceneId { get; }
    public string ManifestText { get; }
    public IReadOnlyList<AgasScenePackageAssetMapping> AssetMappings { get; }
    public IReadOnlyList<string> CopiedAssetRelativePaths { get; }
    public IReadOnlyList<AgasScenePackagePreparedArtifact> PackageArtifacts { get; }

    public static AgasScenePackagePreparedImport Fail(string code) =>
        new(false, code, string.Empty, string.Empty, string.Empty, Array.Empty<AgasScenePackageAssetMapping>());
}

public sealed record AgasScenePackageAssetMapping(string PackageRelativePath, string ProjectRelativePath);

public sealed record AgasScenePackagePreparedArtifact(
    string PackageRelativePath,
    string PreparedRelativePath,
    ScenePackageFileType Type,
    ScenePackageFileIntegrity Integrity,
    bool IsPublishedProjectAsset);

public enum AgasScenePackageExportSourceScope
{
    Project = 0,
    AgasHome = 1,
}

public sealed record AgasScenePackageExportFile(
    string SourceProjectRelativePath,
    string PackageRelativePath,
    ScenePackageFileType Type = ScenePackageFileType.Unspecified,
    AgasScenePackageExportSourceScope SourceScope = AgasScenePackageExportSourceScope.Project,
    ScenePackageFileIntegrity? ExpectedIntegrity = null);

public sealed record AgasScenePackagePreparedSceneResult(
    bool Success,
    string Code,
    AgasSceneResource? Scene)
{
    public static AgasScenePackagePreparedSceneResult Fail(string code) => new(false, code, null);
}

public sealed record AgasScenePackageExportResult(
    bool Success,
    string Code,
    string PackageRef,
    string SourceRef,
    int ReferenceImageCount)
{
    public static AgasScenePackageExportResult Fail(string code) => new(false, code, string.Empty, string.Empty, 0);
}

public sealed class AgasScenePackageStagingService
{
    public const string InvalidCode = AgasMcpErrorCodes.PackageInvalid;
    public const string TooLargeCode = AgasMcpErrorCodes.PackageTooLarge;
    public const string NotFoundCode = AgasMcpErrorCodes.ImportSourceNotFound;
    public const string CanceledCode = AgasMcpErrorCodes.Stopped;

    private readonly ScenePackagePathGuard _guard;
    private readonly ScenePackageImportJournalStore _journals;
    private readonly ScenePackageImportJournalReconciler _reconciler;
    private readonly TimeSpan _timeout;
    private readonly IScenePackageImageDecoder _imageDecoder;
    private readonly IAgasImportCopyFaultInjector? _copyFaultInjector;

    public AgasScenePackageStagingService(
        ScenePackagePathGuard? guard = null,
        ScenePackageImportJournalStore? journals = null,
        ScenePackageImportJournalReconciler? reconciler = null,
        TimeSpan? timeout = null,
        IScenePackageImageDecoder? imageDecoder = null)
        : this(guard, journals, reconciler, timeout, imageDecoder, null)
    {
    }

    internal AgasScenePackageStagingService(
        ScenePackagePathGuard? guard,
        ScenePackageImportJournalStore? journals,
        ScenePackageImportJournalReconciler? reconciler,
        TimeSpan? timeout,
        IScenePackageImageDecoder? imageDecoder,
        IAgasImportCopyFaultInjector? copyFaultInjector)
    {
        _guard = guard ?? new ScenePackagePathGuard();
        _journals = journals ?? new ScenePackageImportJournalStore(_guard);
        _reconciler = reconciler ?? new ScenePackageImportJournalReconciler(_journals, _guard);
        _timeout = timeout ?? ScenePackageSafetyLimits.HeavyOperationTimeout;
        _imageDecoder = imageDecoder ?? new GodotScenePackageImageDecoder();
        _copyFaultInjector = copyFaultInjector;
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public AgasMcpScenePackageExportCapture CaptureExport(
        AgasSceneResource scene,
        string packageRef)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!_guard.IsSafeReference(scene.Id) || !_guard.IsSafeReference(packageRef))
            return AgasMcpScenePackageExportCapture.Fail(InvalidCode);

        try
        {
            ScenePackageDocument source = ScenePackageCodec.FromScene(scene);
            var files = new List<AgasScenePackageExportFile>();
            var layers = new List<ScenePackageLayer>();
            foreach (ScenePackageLayer packageLayer in source.Layers
                         .OrderBy(item => item.Order)
                         .ThenBy(item => item.Id, StringComparer.Ordinal))
            {
                AgasLayerResource? liveLayer = scene.LayersSnapshot.FirstOrDefault(item =>
                    string.Equals(item.Id, packageLayer.Id, StringComparison.Ordinal));
                if (liveLayer == null)
                    return AgasMcpScenePackageExportCapture.Fail(InvalidCode);
                var references = new List<ScenePackageReferenceImage>();
                var images = new List<ScenePackageImage>();
                if (liveLayer.Shared)
                {
                    foreach (ImageInstanceResource image in liveLayer.ImagesSnapshot
                                 .OrderBy(item => item.Order)
                                 .ThenBy(item => item.Id, StringComparer.Ordinal))
                    {
                        ScenePackageImage? packageImage = (packageLayer.Images ?? Array.Empty<ScenePackageImage>())
                            .SingleOrDefault(item => string.Equals(item.Id, image.Id, StringComparison.Ordinal));
                        if (packageImage == null)
                            return AgasMcpScenePackageExportCapture.Fail(InvalidCode);
                        files.Add(new AgasScenePackageExportFile(
                            image.ImagePath.Replace('\\', '/'),
                            packageImage.FilePath,
                            ScenePackageFileType.CanvasImage));
                        images.Add(packageImage);
                    }
                }
                layers.Add(packageLayer with
                {
                    Shared = liveLayer.Shared,
                    ReferenceImages = Array.AsReadOnly(references.ToArray()),
                    Images = Array.AsReadOnly(images.ToArray()),
                });
            }

            ScenePackageDocument document = source with
            {
                Layers = Array.AsReadOnly(layers.ToArray()),
                Integrity = source.SchemaVersion == ScenePackageCodec.CurrentSchemaVersion
                    ? Array.Empty<ScenePackageFileIntegrity>()
                    : source.Integrity,
            };
            _ = BuildReferences(document);
            IReadOnlyList<ScenePackageTransportArtifact> expected =
                ScenePackageTransportArtifactCatalog.ForExport(document);
            if (!expected.Select(item => item.PackagePath).Order(StringComparer.Ordinal).SequenceEqual(
                    files.Select(item => item.PackageRelativePath).Order(StringComparer.Ordinal),
                    StringComparer.Ordinal))
                return AgasMcpScenePackageExportCapture.Fail(InvalidCode);
            return new AgasMcpScenePackageExportCapture(
                true,
                string.Empty,
                packageRef,
                Encoding.UTF8.GetBytes(ScenePackageCodec.Serialize(document)),
                Array.AsReadOnly(files.ToArray()));
        }
        catch
        {
            return AgasMcpScenePackageExportCapture.Fail(InvalidCode);
        }
    }

    public AgasScenePackagePreparedSceneResult BuildSceneFromPreparedImport(
        AgasScenePackagePreparedImport prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!prepared.Success || !_guard.IsSafeReference(prepared.SceneId))
            return AgasScenePackagePreparedSceneResult.Fail(InvalidCode);

        try
        {
            ScenePackageDocument document = ScenePackageCodec.Deserialize(prepared.ManifestText);
            var scene = new AgasSceneResource
            {
                Id = prepared.SceneId,
                Name = document.Name.Trim(),
                ViewState = new SceneViewStateResource { CameraX = 0, CameraY = 0, Zoom = 1 },
            };
            var mapping = prepared.AssetMappings.ToDictionary(
                item => item.PackageRelativePath.Replace('\\', '/'),
                item => item.ProjectRelativePath.Replace('\\', '/'),
                StringComparer.Ordinal);
            string? firstLayerId = null;
            foreach (ScenePackageLayer packageLayer in document.Layers
                         .OrderBy(item => item.Order)
                         .ThenBy(item => item.Id, StringComparer.Ordinal))
            {
                string layerId = packageLayer.Id;
                firstLayerId ??= layerId;
                var layer = new AgasLayerResource
                {
                    Id = layerId,
                    Name = packageLayer.Name,
                    Order = packageLayer.Order,
                    Visible = packageLayer.Visible,
                    Locked = packageLayer.Locked,
                    Shared = packageLayer.Shared,
                };
                foreach (ScenePackagePromptFragment prompt in packageLayer.PromptFragments
                             .OrderBy(item => item.Order)
                             .ThenBy(item => item.Id, StringComparer.Ordinal))
                {
                    layer.PromptFragments.Add(new PromptFragmentResource
                    {
                        Id = prompt.Id,
                        Text = prompt.Text ?? string.Empty,
                        Enabled = prompt.Enabled,
                        Order = prompt.Order,
                        Role = ScenePackageCodec.ParseRole(prompt.Role),
                    });
                }
                foreach (ScenePackageReferenceImage reference in packageLayer.ReferenceImages
                             .OrderBy(item => item.Order)
                             .ThenBy(item => item.Id, StringComparer.Ordinal))
                {
                    string packageRelative = $"references/{packageLayer.Id}/{reference.FileName}";
                    if (!mapping.TryGetValue(packageRelative, out string? relativePath))
                        return AgasScenePackagePreparedSceneResult.Fail(InvalidCode);
                    layer.Images.Add(new ImageInstanceResource
                    {
                        Id = reference.Id,
                        ImagePath = relativePath,
                        Order = reference.Order,
                        ScaleX = reference.ScaleX,
                        ScaleY = reference.ScaleY,
                        RotationDegrees = reference.RotationDegrees,
                        Opacity = reference.Opacity,
                    });
                }
                foreach (ScenePackageImage packageImage in
                         packageLayer.Images ?? Array.Empty<ScenePackageImage>())
                {
                    if (!mapping.TryGetValue(packageImage.FilePath, out string? relativePath))
                        return AgasScenePackagePreparedSceneResult.Fail(InvalidCode);
                    var image = new ImageInstanceResource
                    {
                        Id = packageImage.Id,
                        ImagePath = relativePath,
                        Order = packageImage.Order,
                        X = packageImage.X,
                        Y = packageImage.Y,
                        ScaleX = packageImage.ScaleX,
                        ScaleY = packageImage.ScaleY,
                        RotationDegrees = packageImage.RotationDegrees,
                        Opacity = packageImage.Opacity,
                    };
                    layer.Images.Add(image);
                }
                scene.Layers.Add(layer);
            }
            if (firstLayerId == null || scene.Layers.Count == 0)
                return AgasScenePackagePreparedSceneResult.Fail(InvalidCode);
            scene.ActiveLayerId = firstLayerId;
            return new AgasScenePackagePreparedSceneResult(true, string.Empty, scene);
        }
        catch
        {
            return AgasScenePackagePreparedSceneResult.Fail(InvalidCode);
        }
    }

    public AgasScenePackagePreparedImport PrepareImport(
        string agasHome,
        string projectRoot,
        string packageRef,
        string newSceneId,
        CancellationToken cancellationToken = default)
    {
        if (!_guard.IsSafeReference(packageRef) || !_guard.IsSafeReference(newSceneId))
            return AgasScenePackagePreparedImport.Fail(InvalidCode);
        string agasRoot;
        try { agasRoot = Path.GetFullPath(agasHome); }
        catch { return AgasScenePackagePreparedImport.Fail(InvalidCode); }
        if (!_guard.TryResolveDirectory(agasRoot, "import-inbox/scene-packages", mustExist: true, out string inboxRoot))
            return AgasScenePackagePreparedImport.Fail(NotFoundCode);
        if (!_guard.TryResolveDirectory(inboxRoot, packageRef, mustExist: true, out string packageDirectory))
            return AgasScenePackagePreparedImport.Fail(NotFoundCode);
        return PrepareImportFromDirectory(
            projectRoot, packageDirectory, newSceneId, expectedPackageDirectoryIdentity: null, cancellationToken);
    }

    public AgasScenePackagePreparedImport PrepareImportBySourceRef(
        string agasHome,
        string projectRoot,
        string sourceRef,
        string newSceneId,
        AgasImportSourceRefBroker broker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);
        if (!_guard.IsSafeReference(newSceneId)
            || !broker.TryResolve(sourceRef, AgasImportKind.ScenePackage, agasHome, out AgasImportSourceGrant? grant)
            || grant == null)
            return AgasScenePackagePreparedImport.Fail(NotFoundCode);
        return PrepareImportFromDirectory(
            projectRoot,
            grant.SourcePath,
            newSceneId,
            grant.NativeIdentities[grant.SourcePath],
            cancellationToken);
    }

    private AgasScenePackagePreparedImport PrepareImportFromDirectory(
        string projectRoot,
        string packageDirectory,
        string newSceneId,
        string? expectedPackageDirectoryIdentity,
        CancellationToken cancellationToken)
    {
        ProjectAccessPathIdentity packageDirectoryIdentity;
        try { packageDirectoryIdentity = _guard.Capture(packageDirectory); }
        catch { return AgasScenePackagePreparedImport.Fail(NotFoundCode); }
        if (!packageDirectoryIdentity.Exists || !packageDirectoryIdentity.IsDirectory || packageDirectoryIdentity.IsLinkOrReparse
            || (expectedPackageDirectoryIdentity != null
                && !string.Equals(
                    packageDirectoryIdentity.IdentityToken,
                    expectedPackageDirectoryIdentity,
                    StringComparison.Ordinal)))
            return AgasScenePackagePreparedImport.Fail(NotFoundCode);
        if (!_guard.TryResolveExistingFile(packageDirectory, "scene.json", out string manifestPath))
            return AgasScenePackagePreparedImport.Fail(NotFoundCode);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        CancellationToken token = timeout.Token;
        string operationId = Guid.NewGuid().ToString("N");
        ScenePackageImportJournal? journal = null;
        try
        {
            token.ThrowIfCancellationRequested();
            FileInfo manifestInfo = new(manifestPath);
            ProjectAccessPathIdentity manifestIdentity = _guard.Capture(manifestPath);
            if (manifestInfo.Length > ScenePackageSafetyLimits.MaximumManifestBytes)
                return AgasScenePackagePreparedImport.Fail(TooLargeCode);
            long manifestLength = manifestInfo.Length;
            DateTime manifestLastWriteUtc = manifestInfo.LastWriteTimeUtc;
            byte[] manifestBytes = ReadBounded(manifestPath, ScenePackageSafetyLimits.MaximumManifestBytes, token);
            string manifestText = new UTF8Encoding(false, true).GetString(manifestBytes);
            ScenePackageDocument document = ScenePackageCodec.Deserialize(manifestText);
            _ = BuildReferences(document);
            IReadOnlyList<ScenePackageTransportArtifact> packageArtifacts =
                ScenePackageTransportArtifactCatalog.ForImport(document);

            long payload = manifestBytes.LongLength;
            var sources = new List<ImportSourceArtifact>();
            foreach (ScenePackageTransportArtifact artifact in packageArtifacts)
            {
                token.ThrowIfCancellationRequested();
                if (!_guard.TryResolveExistingFile(packageDirectory, artifact.PackagePath, out string source))
                    return AgasScenePackagePreparedImport.Fail(InvalidCode);
                long length = new FileInfo(source).Length;
                if (length > ScenePackageTransportArtifactCatalog.MaximumBytes(artifact.Type))
                    return AgasScenePackagePreparedImport.Fail(TooLargeCode);
                payload = checked(payload + length);
                if (payload > ScenePackageSafetyLimits.MaximumPackagePayloadBytes)
                    return AgasScenePackagePreparedImport.Fail(TooLargeCode);
                FileInfo sourceInfo = new(source);
                sources.Add(new ImportSourceArtifact(
                    artifact,
                    source,
                    sourceInfo.Length,
                    sourceInfo.LastWriteTimeUtc,
                    _guard.Capture(source).IdentityToken,
                    ScenePackageFileNamePolicy.SanitizeStem(Path.GetFileName(artifact.PackagePath))));
            }
            manifestInfo.Refresh();
            if (manifestInfo.Length != manifestLength
                || manifestInfo.LastWriteTimeUtc != manifestLastWriteUtc
                || _guard.Capture(manifestPath).IdentityToken != manifestIdentity.IdentityToken
                || _guard.Capture(packageDirectory).IdentityToken != packageDirectoryIdentity.IdentityToken)
                return AgasScenePackagePreparedImport.Fail(InvalidCode);

            if (!_guard.TryResolveDirectory(projectRoot, "assets/imported/packages", mustExist: false, out string finalDirectory))
                return AgasScenePackagePreparedImport.Fail(InvalidCode);
            Directory.CreateDirectory(finalDirectory);
            if (!_guard.TryResolveDirectory(projectRoot, "assets/imported/packages", mustExist: true, out finalDirectory))
                return AgasScenePackagePreparedImport.Fail(InvalidCode);
            journal = _journals.CreatePending(
                projectRoot,
                operationId,
                newSceneId,
                $".agas/scenes/{newSceneId}.tres");
            string stagingDirectory = Path.Combine(projectRoot, ".agas", "import-staging", operationId, "payload");
            Directory.CreateDirectory(stagingDirectory);
            if (!_guard.TryResolveDirectory(projectRoot, $".agas/import-staging/{operationId}/payload", mustExist: true, out stagingDirectory))
                throw new InvalidDataException();
            var stagingArtifacts = new List<ScenePackageOwnedStagingArtifact>
            {
                new(
                    $".agas/import-staging/{operationId}/payload",
                    ScenePackageStagingArtifactKind.Directory,
                    _guard.CaptureOwnedDirectoryIdentityToken(
                        stagingDirectory, stagingDirectory, journal.OperationId, journal.OwnerToken)),
            };
            journal = _journals.UpdateArtifacts(
                projectRoot, journal, Array.Empty<ScenePackageOwnedArtifact>(), stagingArtifacts);
            var staged = new List<StagedImportArtifact>();
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < sources.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                string stagedPath = Path.Combine(stagingDirectory, $"{index:D6}.tmp");
                string stagedRelative = $".agas/import-staging/{operationId}/payload/{index:D6}.tmp";
                (journal, _) = CopyRegisteredImportTemporary(
                    projectRoot,
                    journal,
                    stagingArtifacts,
                    sources[index].Source,
                    stagedPath,
                    stagedRelative,
                    ScenePackageTransportArtifactCatalog.MaximumBytes(sources[index].Artifact.Type),
                    token);
                if (!_guard.TryResolveExistingFile(projectRoot, stagedRelative, out stagedPath))
                    throw new InvalidDataException();
                ScenePackageFileIntegrity actualIntegrity =
                    ScenePackageTransportArtifactCatalog.InspectAndCreateIntegrity(
                        stagedPath,
                        sources[index].Artifact,
                        _imageDecoder);
                FileInfo sourceAfter = new(sources[index].Source);
                sourceAfter.Refresh();
                if (sourceAfter.Length != sources[index].Length
                    || sourceAfter.LastWriteTimeUtc != sources[index].LastWriteUtc
                    || _guard.Capture(sources[index].Source).IdentityToken != sources[index].Identity)
                    throw new InvalidDataException();
                string? relativeFinal = null;
                string? finalPath = null;
                if (sources[index].Artifact.IsImage)
                {
                    string extension = Path.GetExtension(sources[index].Artifact.PackagePath).ToLowerInvariant();
                    string uniqueStem = sources[index].SafeStem;
                    for (int suffix = 2; !usedNames.Add(uniqueStem + extension); suffix++)
                        uniqueStem = $"{sources[index].SafeStem}-{suffix}";
                    relativeFinal = $"assets/imported/packages/{operationId}-{uniqueStem}{extension}";
                    if (!_guard.TryResolveOwnedTarget(projectRoot, relativeFinal, out finalPath))
                        throw new InvalidDataException();
                }
                staged.Add(new StagedImportArtifact(
                    stagedPath,
                    stagedRelative,
                    sources[index].Artifact.PackagePath,
                    relativeFinal,
                    finalPath,
                    sources[index].Artifact.Type,
                    actualIntegrity));
            }
            string stagedManifestPath = Path.Combine(stagingDirectory, "scene.json");
            string stagedManifestRelative = $".agas/import-staging/{operationId}/payload/scene.json";
            journal = WriteRegisteredImportTemporary(
                projectRoot,
                journal,
                stagingArtifacts,
                stagedManifestPath,
                stagedManifestRelative,
                manifestBytes,
                token);
            if (!_guard.TryResolveExistingFile(projectRoot, stagedManifestRelative, out stagedManifestPath))
                throw new InvalidDataException();

            StagedImportArtifact[] published = staged.Where(item => item.IsPublishedProjectAsset).ToArray();
            ScenePackageOwnedArtifact[] owned = published.Select(item =>
                new ScenePackageOwnedArtifact(
                    item.RelativeFinal!,
                    _guard.CaptureOwnedFileIdentityToken(
                        item.Staged, item.Final!, journal.OperationId, journal.OwnerToken))).ToArray();
            ScenePackageStagingArtifactPublication[] publications = published.Select(item =>
                new ScenePackageStagingArtifactPublication(
                    item.StagedRelative,
                    item.RelativeFinal!)).ToArray();
            journal = _journals.UpdateArtifacts(
                projectRoot, journal, owned, stagingArtifacts, publications);
            for (int index = 0; index < published.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                StagedImportArtifact stagedItem = published[index];
                string stagedPath = stagedItem.Staged;
                string relativeFinal = stagedItem.RelativeFinal!;
                if (!_guard.TryResolveOwnedTarget(projectRoot, relativeFinal, out string finalPath)
                    || !string.Equals(finalPath, stagedItem.Final, StringComparison.Ordinal)
                    || File.Exists(finalPath) || Directory.Exists(finalPath))
                    throw new InvalidDataException();
                if (!_guard.TryResolveExistingFile(projectRoot,
                        $".agas/import-staging/{operationId}/payload/{Path.GetFileName(stagedPath)}", out stagedPath))
                    throw new InvalidDataException();
                File.Move(stagedPath, finalPath);
                if (!_guard.TryResolveExistingFile(projectRoot, relativeFinal, out finalPath)
                    || _guard.CaptureOwnedFileIdentityToken(
                        finalPath, finalPath, journal.OperationId, journal.OwnerToken) !=
                       owned[index].IdentityToken)
                    throw new InvalidDataException();
            }
            AgasScenePackageAssetMapping[] mappings = published.Select(item =>
                new AgasScenePackageAssetMapping(item.PackageRelative, item.RelativeFinal!)).ToArray();
            AgasScenePackagePreparedArtifact[] preparedArtifacts = staged.Select(item =>
                new AgasScenePackagePreparedArtifact(
                    item.PackageRelative,
                    item.RelativeFinal ?? item.StagedRelative,
                    item.Type,
                    item.Integrity,
                    item.IsPublishedProjectAsset)).ToArray();
            return new(
                true,
                string.Empty,
                operationId,
                newSceneId,
                manifestText,
                mappings,
                preparedArtifacts);
        }
        catch (OperationCanceledException)
        {
            if (journal != null) _reconciler.Reconcile(projectRoot, operationId, NeverDurableProbe.Instance);
            return AgasScenePackagePreparedImport.Fail(CanceledCode);
        }
        catch (OverflowException)
        {
            if (journal != null) _reconciler.Reconcile(projectRoot, operationId, NeverDurableProbe.Instance);
            return AgasScenePackagePreparedImport.Fail(TooLargeCode);
        }
        catch (ScenePackageImageTooLargeException)
        {
            if (journal != null) _reconciler.Reconcile(projectRoot, operationId, NeverDurableProbe.Instance);
            return AgasScenePackagePreparedImport.Fail(TooLargeCode);
        }
        catch (PackagePayloadTooLargeException)
        {
            if (journal != null) _reconciler.Reconcile(projectRoot, operationId, NeverDurableProbe.Instance);
            return AgasScenePackagePreparedImport.Fail(TooLargeCode);
        }
        catch (PackageArtifactTooLargeException)
        {
            if (journal != null) _reconciler.Reconcile(projectRoot, operationId, NeverDurableProbe.Instance);
            return AgasScenePackagePreparedImport.Fail(TooLargeCode);
        }
        catch
        {
            if (journal != null) _reconciler.Reconcile(projectRoot, operationId, NeverDurableProbe.Instance);
            return AgasScenePackagePreparedImport.Fail(InvalidCode);
        }
    }

    public AgasScenePackageExportResult ExportToExchange(
        string agasHome,
        string projectRoot,
        string packageRef,
        ReadOnlyMemory<byte> manifestUtf8,
        IReadOnlyList<AgasScenePackageExportFile> files,
        AgasImportSourceRefBroker broker,
        IAgasDurableCommitGate? commitGate = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);
        commitGate ??= new AgasAllowDurableCommitGate();
        if (!_guard.IsSafeReference(packageRef)
            || manifestUtf8.Length > ScenePackageSafetyLimits.MaximumManifestBytes)
            return AgasScenePackageExportResult.Fail(
                manifestUtf8.Length > ScenePackageSafetyLimits.MaximumManifestBytes ? TooLargeCode : InvalidCode);
        string agasRoot;
        try { agasRoot = Path.GetFullPath(agasHome); }
        catch { return AgasScenePackageExportResult.Fail(InvalidCode); }
        try { Directory.CreateDirectory(agasRoot); }
        catch { return AgasScenePackageExportResult.Fail(InvalidCode); }
        if (!_guard.TryResolveDirectory(agasRoot, "import-exchange/scene-packages/.staging", mustExist: false, out string stagingRoot))
            return AgasScenePackageExportResult.Fail(InvalidCode);
        try { Directory.CreateDirectory(stagingRoot); }
        catch { return AgasScenePackageExportResult.Fail(InvalidCode); }
        if (!_guard.TryResolveDirectory(agasRoot, "import-exchange/scene-packages/.staging", mustExist: true, out stagingRoot))
            return AgasScenePackageExportResult.Fail(InvalidCode);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        CancellationToken token = timeout.Token;
        string operationId = Guid.NewGuid().ToString("N");
        string ownerToken = Guid.NewGuid().ToString("N");
        string stagingRelative = $"import-exchange/scene-packages/.staging/{operationId}";
        string stagingDirectory = Path.Combine(stagingRoot, operationId);
        string marker = Path.Combine(stagingDirectory, "owner.v1");
        var artifacts = new List<ExportOwnedStagingArtifact>();
        bool published = false;
        string? preparedSourceRef = null;
        try
        {
            string manifestText = new UTF8Encoding(false, true).GetString(manifestUtf8.Span);
            ScenePackageDocument document = ScenePackageCodec.Deserialize(manifestText);
            _ = BuildReferences(document);
            IReadOnlyList<ScenePackageTransportArtifact> manifestArtifacts =
                ScenePackageTransportArtifactCatalog.ForExport(document);
            Dictionary<string, AgasScenePackageExportFile> suppliedFiles = files.ToDictionary(
                item => item.PackageRelativePath.Replace('\\', '/'),
                StringComparer.Ordinal);
            if (!manifestArtifacts.Select(item => item.PackagePath).Order(StringComparer.Ordinal).SequenceEqual(
                    suppliedFiles.Keys.Order(StringComparer.Ordinal),
                    StringComparer.Ordinal))
                return AgasScenePackageExportResult.Fail(InvalidCode);

            Directory.CreateDirectory(stagingDirectory);
            RegisterExportDirectory(artifacts, stagingDirectory, stagingDirectory, operationId, ownerToken);
            WriteRegisteredExportTemporary(
                artifacts, marker, stagingDirectory, operationId, ownerToken,
                output =>
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(operationId);
                    output.Write(bytes, 0, bytes.Length);
                });
            long total = 0;
            var integrity = new List<ScenePackageFileIntegrity>();
            foreach (ScenePackageTransportArtifact artifact in manifestArtifacts)
            {
                token.ThrowIfCancellationRequested();
                AgasScenePackageExportFile file = suppliedFiles[artifact.PackagePath];
                if (file.Type != ScenePackageFileType.Unspecified && file.Type != artifact.Type)
                    return AgasScenePackageExportResult.Fail(InvalidCode);
                if (!TryResolveExportSource(
                        projectRoot,
                        agasRoot,
                        file,
                        out string sourcePath)
                    || !TrySafePackageRelative(artifact.PackagePath, out string relative))
                    return AgasScenePackageExportResult.Fail(InvalidCode);
                FileInfo sourceBefore = new(sourcePath);
                sourceBefore.Refresh();
                long length = sourceBefore.Length;
                DateTime lastWriteUtc = sourceBefore.LastWriteTimeUtc;
                string sourceIdentity = _guard.Capture(sourcePath).IdentityToken;
                long maximum = ScenePackageTransportArtifactCatalog.MaximumBytes(artifact.Type);
                if (length > maximum)
                    return AgasScenePackageExportResult.Fail(TooLargeCode);
                total = checked(total + length);
                if (total > ScenePackageSafetyLimits.MaximumPackagePayloadBytes)
                    return AgasScenePackageExportResult.Fail(TooLargeCode);
                string destination = Path.Combine(stagingDirectory, Path.Combine(relative.Split('/')));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                RegisterExportAncestors(
                    artifacts, Path.GetDirectoryName(destination)!, stagingDirectory, operationId, ownerToken);
                WriteRegisteredExportTemporary(
                    artifacts, destination, stagingDirectory, operationId, ownerToken,
                    output =>
                    {
                        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        CopyBounded(input, output, maximum, token);
                    });
                if (!_guard.TryResolveExistingFile(agasRoot, $"{stagingRelative}/{relative}", out destination))
                    return AgasScenePackageExportResult.Fail(InvalidCode);
                var effectiveArtifact = artifact with
                {
                    ExpectedIntegrity = artifact.ExpectedIntegrity ?? file.ExpectedIntegrity,
                };
                ScenePackageFileIntegrity actual =
                    ScenePackageTransportArtifactCatalog.InspectAndCreateIntegrity(
                        destination,
                        effectiveArtifact,
                        _imageDecoder);
                integrity.Add(actual);
                sourceBefore.Refresh();
                if (sourceBefore.Length != length
                    || sourceBefore.LastWriteTimeUtc != lastWriteUtc
                    || _guard.Capture(sourcePath).IdentityToken != sourceIdentity)
                    return AgasScenePackageExportResult.Fail(InvalidCode);
            }
            byte[] publishedManifest = manifestUtf8.ToArray();
            if (document.SchemaVersion == ScenePackageCodec.CurrentSchemaVersion)
            {
                document = document with { Integrity = Array.AsReadOnly(integrity.ToArray()) };
                ScenePackageCodec.ValidateForTransport(document);
                publishedManifest = Encoding.UTF8.GetBytes(ScenePackageCodec.Serialize(document));
            }
            if (publishedManifest.Length > ScenePackageSafetyLimits.MaximumManifestBytes)
                return AgasScenePackageExportResult.Fail(TooLargeCode);
            total = checked(total + publishedManifest.LongLength);
            if (total > ScenePackageSafetyLimits.MaximumPackagePayloadBytes)
                return AgasScenePackageExportResult.Fail(TooLargeCode);
            string stagedManifest = Path.Combine(stagingDirectory, "scene.json");
            WriteRegisteredExportTemporary(
                artifacts,
                stagedManifest,
                stagingDirectory,
                operationId,
                ownerToken,
                output => output.Write(publishedManifest, 0, publishedManifest.Length));
            string finalRelative = $"import-exchange/scene-packages/{packageRef}";
            if (!_guard.TryResolveOwnedTarget(agasRoot, finalRelative, out string finalDirectory)
                || Directory.Exists(finalDirectory) || File.Exists(finalDirectory))
                return AgasScenePackageExportResult.Fail(InvalidCode);
            if (!TryValidateExportStagingTree(
                    agasRoot,
                    stagingRelative,
                    stagingDirectory,
                    marker,
                    operationId,
                    ownerToken,
                    artifacts))
                return AgasScenePackageExportResult.Fail(InvalidCode);
            var identities = new Dictionary<string, string>(StringComparer.Ordinal);
            long preparedSize = 0;
            foreach (ExportOwnedStagingArtifact artifact in artifacts)
            {
                if (string.Equals(artifact.CanonicalPath, marker, StringComparison.Ordinal)) continue;
                string relative = Path.GetRelativePath(stagingDirectory, artifact.CanonicalPath);
                string finalPath = relative == "."
                    ? finalDirectory
                    : Path.Combine(finalDirectory, relative);
                ProjectAccessPathIdentity identity = _guard.Capture(artifact.CanonicalPath);
                if (!identity.Exists || identity.IsLinkOrReparse
                    || identity.IsDirectory != (artifact.Kind == ScenePackageStagingArtifactKind.Directory))
                    return AgasScenePackageExportResult.Fail(InvalidCode);
                identities[finalPath] = identity.IdentityToken;
                if (!identity.IsDirectory) preparedSize = checked(preparedSize + new FileInfo(artifact.CanonicalPath).Length);
            }
            string finalManifest = Path.Combine(finalDirectory, "scene.json");
            if (preparedSize != total || !identities.ContainsKey(finalManifest))
                return AgasScenePackageExportResult.Fail(InvalidCode);
            preparedSourceRef = broker.Prepare(
                AgasImportKind.ScenePackage,
                agasRoot,
                finalDirectory,
                packageRef,
                total,
                identities);

            token.ThrowIfCancellationRequested();
            if (!commitGate.TryBeginDurableCommit()) return AgasScenePackageExportResult.Fail(CanceledCode);
            Directory.Move(stagingDirectory, finalDirectory);
            published = true;
            if (!broker.ActivatePrepared(preparedSourceRef))
                throw new InvalidOperationException("Published exchange source capability activation failed.");
            try
            {
                _copyFaultInjector?.AfterExchangePublished();
                TryDeleteFile(Path.Combine(finalDirectory, "owner.v1"));
            }
            catch
            {
                // The directory rename is the commit point. Post-commit cleanup/diagnostics
                // cannot rewrite the durable success receipt or revoke its prepared ref.
            }
            return new(
                true,
                string.Empty,
                packageRef,
                preparedSourceRef,
                manifestArtifacts.Count(item => item.Type == ScenePackageFileType.ReferenceImage));
        }
        catch (OperationCanceledException)
        {
            return AgasScenePackageExportResult.Fail(CanceledCode);
        }
        catch (OverflowException)
        {
            return AgasScenePackageExportResult.Fail(TooLargeCode);
        }
        catch (ScenePackageImageTooLargeException)
        {
            return AgasScenePackageExportResult.Fail(TooLargeCode);
        }
        catch (PackagePayloadTooLargeException)
        {
            return AgasScenePackageExportResult.Fail(TooLargeCode);
        }
        catch (PackageArtifactTooLargeException)
        {
            return AgasScenePackageExportResult.Fail(TooLargeCode);
        }
        catch
        {
            return AgasScenePackageExportResult.Fail(InvalidCode);
        }
        finally
        {
            if (!published)
            {
                if (preparedSourceRef != null) broker.Revoke(preparedSourceRef);
                TryDeleteOwnedDirectory(
                    agasRoot,
                    stagingRelative,
                    stagingDirectory,
                    marker,
                    operationId,
                    ownerToken,
                    artifacts);
            }
        }
    }

    [Obsolete("Project-local scene package export is retained for UI compatibility; MCP uses ExportToExchange.")]
    public AgasScenePackageExportResult Export(
        string projectRoot,
        string packageRef,
        ReadOnlyMemory<byte> manifestUtf8,
        IReadOnlyList<AgasScenePackageExportFile> files,
        IAgasDurableCommitGate? commitGate = null,
        CancellationToken cancellationToken = default)
    {
        commitGate ??= new AgasAllowDurableCommitGate();
        if (!_guard.IsSafeReference(packageRef)
            || manifestUtf8.Length > ScenePackageSafetyLimits.MaximumManifestBytes)
            return AgasScenePackageExportResult.Fail(manifestUtf8.Length > ScenePackageSafetyLimits.MaximumManifestBytes ? TooLargeCode : InvalidCode);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        CancellationToken token = timeout.Token;
        string operationId = Guid.NewGuid().ToString("N");
        string ownerToken = Guid.NewGuid().ToString("N");
        string stagingDirectory = Path.Combine(projectRoot, ".agas", "scene-exports", ".staging", operationId);
        string marker = Path.Combine(stagingDirectory, "owner.v1");
        var stagingArtifacts = new List<ExportOwnedStagingArtifact>();
        bool published = false;
        try
        {
            string manifestText = new UTF8Encoding(false, true).GetString(manifestUtf8.Span);
            ScenePackageDocument document = ScenePackageCodec.Deserialize(manifestText);
            IReadOnlyList<string> manifestReferences = BuildReferences(document);
            if (!manifestReferences.OrderBy(item => item, StringComparer.Ordinal).SequenceEqual(
                    files.Select(item => item.PackageRelativePath.Replace('\\', '/')).OrderBy(item => item, StringComparer.Ordinal),
                    StringComparer.Ordinal))
                return AgasScenePackageExportResult.Fail(InvalidCode);
            if (!_guard.TryResolveDirectory(projectRoot, ".agas/scene-exports/.staging", mustExist: false, out _))
                return AgasScenePackageExportResult.Fail(InvalidCode);
            Directory.CreateDirectory(stagingDirectory);
            RegisterExportDirectory(stagingArtifacts, stagingDirectory, stagingDirectory, operationId, ownerToken);
            WriteRegisteredExportTemporary(
                stagingArtifacts,
                marker,
                stagingDirectory,
                operationId,
                ownerToken,
                output =>
                {
                    byte[] markerBytes = Encoding.UTF8.GetBytes(operationId);
                    output.Write(markerBytes, 0, markerBytes.Length);
                });
            string stagedManifest = Path.Combine(stagingDirectory, "scene.json");
            WriteRegisteredExportTemporary(
                stagingArtifacts,
                stagedManifest,
                stagingDirectory,
                operationId,
                ownerToken,
                output => output.Write(manifestUtf8.Span));
            long total = manifestUtf8.Length;
            foreach (AgasScenePackageExportFile file in files)
            {
                token.ThrowIfCancellationRequested();
                if (!_guard.TryResolveExistingFile(
                        projectRoot,
                        file.SourceProjectRelativePath.Replace('\\', '/'),
                        out string sourcePath))
                    return AgasScenePackageExportResult.Fail(InvalidCode);
                if (!TrySafePackageRelative(file.PackageRelativePath, out string relative))
                    return AgasScenePackageExportResult.Fail(InvalidCode);
                string extension = Path.GetExtension(relative).ToLowerInvariant();
                if (!ProjectPathRules.AllowedImageExtensionsWithDot.Contains(extension))
                    return AgasScenePackageExportResult.Fail(InvalidCode);
                FileInfo sourceBefore = new(sourcePath);
                long length = sourceBefore.Length;
                DateTime lastWriteUtc = sourceBefore.LastWriteTimeUtc;
                string sourceIdentity = _guard.Capture(sourcePath).IdentityToken;
                if (length > ScenePackageSafetyLimits.MaximumImageBytes) return AgasScenePackageExportResult.Fail(TooLargeCode);
                total = checked(total + length);
                if (total > ScenePackageSafetyLimits.MaximumPackagePayloadBytes) return AgasScenePackageExportResult.Fail(TooLargeCode);
                string destination = Path.Combine(stagingDirectory, Path.Combine(relative.Split('/')));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                RegisterExportAncestors(
                    stagingArtifacts,
                    Path.GetDirectoryName(destination)!,
                    stagingDirectory,
                    operationId,
                    ownerToken);
                WriteRegisteredExportTemporary(
                    stagingArtifacts,
                    destination,
                    stagingDirectory,
                    operationId,
                    ownerToken,
                    output =>
                    {
                        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        CopyBounded(input, output, ScenePackageSafetyLimits.MaximumImageBytes, token);
                    });
                if (!_guard.TryResolveExistingFile(projectRoot,
                        $".agas/scene-exports/.staging/{operationId}/{relative}", out destination))
                    return AgasScenePackageExportResult.Fail(InvalidCode);
                ScenePackageImageInspector.Inspect(destination, extension, _imageDecoder);
                sourceBefore.Refresh();
                if (sourceBefore.Length != length
                    || sourceBefore.LastWriteTimeUtc != lastWriteUtc
                    || _guard.Capture(sourcePath).IdentityToken != sourceIdentity)
                    return AgasScenePackageExportResult.Fail(InvalidCode);
            }
            token.ThrowIfCancellationRequested();
            if (!commitGate.TryBeginDurableCommit()) return AgasScenePackageExportResult.Fail(CanceledCode);
            string finalRelative = $".agas/scene-exports/{packageRef}";
            if (!_guard.TryResolveOwnedTarget(projectRoot, finalRelative, out string finalDirectory)
                || Directory.Exists(finalDirectory) || File.Exists(finalDirectory))
                return AgasScenePackageExportResult.Fail(InvalidCode);
            Directory.Move(stagingDirectory, finalDirectory);
            published = true;
            TryDeleteFile(Path.Combine(finalDirectory, "owner.v1"));
            return new(true, string.Empty, packageRef, string.Empty, files.Count);
        }
        catch (OperationCanceledException)
        {
            return AgasScenePackageExportResult.Fail(CanceledCode);
        }
        catch (OverflowException)
        {
            return AgasScenePackageExportResult.Fail(TooLargeCode);
        }
        catch (ScenePackageImageTooLargeException)
        {
            return AgasScenePackageExportResult.Fail(TooLargeCode);
        }
        catch (PackagePayloadTooLargeException)
        {
            return AgasScenePackageExportResult.Fail(TooLargeCode);
        }
        catch
        {
            return AgasScenePackageExportResult.Fail(InvalidCode);
        }
        finally
        {
            if (!published)
                TryDeleteOwnedDirectory(
                    projectRoot,
                    $".agas/scene-exports/.staging/{operationId}",
                    stagingDirectory,
                    marker,
                    operationId,
                    ownerToken,
                    stagingArtifacts);
        }
    }

    private IReadOnlyList<string> BuildReferences(ScenePackageDocument document)
    {
        ScenePackageCodec.Validate(document);
        if (document.Layers.Count > ScenePackageSafetyLimits.MaximumLayers) throw new PackagePayloadTooLargeException();
        var layerIds = new HashSet<string>(StringComparer.Ordinal);
        var references = new List<string>();
        int promptCount = 0;
        foreach (ScenePackageLayer layer in document.Layers)
        {
            if (!_guard.IsSafeReference(layer.Id) || !layerIds.Add(layer.Id)) throw new InvalidDataException();
            var promptIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (ScenePackagePromptFragment prompt in layer.PromptFragments ?? Array.Empty<ScenePackagePromptFragment>())
            {
                promptCount++;
                if (promptCount > ScenePackageSafetyLimits.MaximumPromptFragments) throw new PackagePayloadTooLargeException();
                if (!_guard.IsSafeReference(prompt.Id) || !promptIds.Add(prompt.Id)) throw new InvalidDataException();
                if (prompt.Role is not ("General" or "Environment" or "NegativeConstraint" or "general" or "environment" or "negativeConstraint"))
                    throw new InvalidDataException();
            }
            var referenceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (ScenePackageReferenceImage image in layer.ReferenceImages ?? Array.Empty<ScenePackageReferenceImage>())
            {
                if (references.Count >= ScenePackageSafetyLimits.MaximumReferenceImages) throw new PackagePayloadTooLargeException();
                if (!_guard.IsSafeReference(image.Id) || !referenceIds.Add(image.Id)
                    || !_guard.IsSafeBasename(image.FileName)) throw new InvalidDataException();
                references.Add($"references/{layer.Id}/{image.FileName}");
            }
        }
        if (references.Distinct(StringComparer.Ordinal).Count() != references.Count) throw new InvalidDataException();
        return references;
    }

    private (ScenePackageImportJournal Journal, long Copied) CopyRegisteredImportTemporary(
        string projectRoot,
        ScenePackageImportJournal journal,
        List<ScenePackageOwnedStagingArtifact> artifacts,
        string source,
        string destination,
        string relativeDestination,
        long maximum,
        CancellationToken token)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        return WriteRegisteredImportTemporaryCore(
            projectRoot,
            journal,
            artifacts,
            destination,
            relativeDestination,
            maximum,
            token,
            output => CopyBounded(input, output, maximum, token));
    }

    private ScenePackageImportJournal WriteRegisteredImportTemporary(
        string projectRoot,
        ScenePackageImportJournal journal,
        List<ScenePackageOwnedStagingArtifact> artifacts,
        string destination,
        string relativeDestination,
        byte[] bytes,
        CancellationToken token)
    {
        (ScenePackageImportJournal updated, _) = WriteRegisteredImportTemporaryCore(
            projectRoot,
            journal,
            artifacts,
            destination,
            relativeDestination,
            bytes.Length,
            token,
            output =>
            {
                token.ThrowIfCancellationRequested();
                output.Write(bytes, 0, bytes.Length);
                return bytes.Length;
            });
        return updated;
    }

    private (ScenePackageImportJournal Journal, long Written) WriteRegisteredImportTemporaryCore(
        string projectRoot,
        ScenePackageImportJournal journal,
        List<ScenePackageOwnedStagingArtifact> artifacts,
        string destination,
        string relativeDestination,
        long maximum,
        CancellationToken token,
        Func<FileStream, long> write)
    {
        string? nativeIdentity = null;
        ScenePackageOwnedStagingArtifact? slot = null;
        bool completed = false;
        try
        {
            long written;
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                ProjectAccessPathIdentity created = _guard.Capture(destination);
                if (!created.Exists || created.IsDirectory || created.IsLinkOrReparse) throw new InvalidDataException();
                nativeIdentity = created.IdentityToken;
                slot = new ScenePackageOwnedStagingArtifact(
                    relativeDestination,
                    ScenePackageStagingArtifactKind.File,
                    _guard.CaptureOwnedFileIdentityToken(
                        destination, destination, journal.OperationId, journal.OwnerToken));
                artifacts.Add(slot);
                journal = _journals.UpdateArtifacts(
                    projectRoot, journal, Array.Empty<ScenePackageOwnedArtifact>(), artifacts);
                _copyFaultInjector?.AfterTemporarySlotRegistered(destination);
                written = write(output);
                if (written > maximum) throw new PackagePayloadTooLargeException();
                output.Flush(flushToDisk: true);
            }
            ProjectAccessPathIdentity after = _guard.Capture(destination);
            if (!after.Exists || after.IsDirectory || after.IsLinkOrReparse
                || !string.Equals(after.IdentityToken, nativeIdentity, StringComparison.Ordinal))
                throw new InvalidDataException();
            int slotIndex = artifacts.IndexOf(slot);
            if (slotIndex < 0) throw new InvalidDataException();
            artifacts[slotIndex] = slot with
            {
                IdentityToken = _guard.CaptureOwnedFileIdentityToken(
                    destination, destination, journal.OperationId, journal.OwnerToken),
            };
            journal = _journals.UpdateArtifacts(
                projectRoot, journal, Array.Empty<ScenePackageOwnedArtifact>(), artifacts);
            completed = true;
            return (journal, written);
        }
        finally
        {
            if (!completed && slot != null && nativeIdentity != null)
            {
                try
                {
                    ProjectAccessPathIdentity current = _guard.Capture(destination);
                    if (current.Exists && !current.IsDirectory && !current.IsLinkOrReparse
                        && string.Equals(current.IdentityToken, nativeIdentity, StringComparison.Ordinal))
                        File.Delete(destination);
                    artifacts.Remove(slot);
                    _journals.UpdateArtifacts(
                        projectRoot, journal, Array.Empty<ScenePackageOwnedArtifact>(), artifacts);
                }
                catch
                {
                    // Retain journal evidence when ownership or journal state changed.
                }
            }
        }
    }

    private static long CopyBounded(FileStream input, FileStream output, long maximum, CancellationToken token)
    {
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            total += read;
            if (total > maximum) throw new PackagePayloadTooLargeException();
            output.Write(buffer, 0, read);
        }
        return total;
    }

    private static byte[] ReadBounded(string source, long maximum, CancellationToken token)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new MemoryStream(capacity: (int)Math.Min(maximum, input.Length));
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            total += read;
            if (total > maximum) throw new PackagePayloadTooLargeException();
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static long CopyBounded(string source, string destination, long maximum, CancellationToken token)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            total += read;
            if (total > maximum) throw new PackagePayloadTooLargeException();
            output.Write(buffer, 0, read);
        }
        output.Flush(flushToDisk: true);
        return total;
    }

    private static bool TrySafePackageRelative(string value, out string relative)
    {
        relative = value?.Trim() ?? string.Empty;
        if (relative.Length == 0 || relative != value || relative.Contains('\\')
            || Path.IsPathRooted(relative) || ProjectPathRules.ContainsUrlOrBase64(relative)) return false;
        string[] segments = relative.Split('/', StringSplitOptions.None);
        return segments.All(segment => segment.Length > 0 && segment is not ("." or ".."));
    }

    private bool TryResolveExportSource(
        string projectRoot,
        string agasRoot,
        AgasScenePackageExportFile file,
        out string sourcePath)
    {
        sourcePath = string.Empty;
        string root = file.SourceScope switch
        {
            AgasScenePackageExportSourceScope.Project => projectRoot,
            AgasScenePackageExportSourceScope.AgasHome => agasRoot,
            _ => string.Empty,
        };
        if (string.IsNullOrEmpty(root)) return false;
        string value = file.SourceProjectRelativePath.Replace('\\', '/');
        if (!Path.IsPathRooted(value))
            return _guard.TryResolveExistingFile(root, value, out sourcePath);
        try
        {
            string canonicalRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string canonicalSource = Path.GetFullPath(value);
            string prefix = canonicalRoot + Path.DirectorySeparatorChar;
            if (!canonicalSource.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string relative = Path.GetRelativePath(canonicalRoot, canonicalSource).Replace('\\', '/');
            return _guard.TryResolveExistingFile(canonicalRoot, relative, out sourcePath);
        }
        catch
        {
            return false;
        }
    }

    private void RegisterExportAncestors(
        List<ExportOwnedStagingArtifact> artifacts,
        string directory,
        string stagingDirectory,
        string operationId,
        string ownerToken)
    {
        string canonicalRoot = Path.GetFullPath(stagingDirectory);
        string canonicalDirectory = Path.GetFullPath(directory);
        string rootPrefix = canonicalRoot + Path.DirectorySeparatorChar;
        if (!string.Equals(canonicalDirectory, canonicalRoot, StringComparison.Ordinal)
            && !canonicalDirectory.StartsWith(rootPrefix, StringComparison.Ordinal))
            throw new InvalidDataException();
        var pending = new Stack<string>();
        for (string? current = canonicalDirectory;
             current != null && !string.Equals(current, canonicalRoot, StringComparison.Ordinal);
             current = Path.GetDirectoryName(current))
        {
            pending.Push(current);
        }
        if (pending.Count == 0 && !string.Equals(canonicalDirectory, canonicalRoot, StringComparison.Ordinal))
            throw new InvalidDataException();
        while (pending.Count > 0)
            RegisterExportDirectory(artifacts, pending.Pop(), canonicalRoot, operationId, ownerToken);
    }

    private void RegisterExportDirectory(
        List<ExportOwnedStagingArtifact> artifacts,
        string directory,
        string stagingDirectory,
        string operationId,
        string ownerToken)
    {
        string canonicalDirectory = Path.GetFullPath(directory);
        if (artifacts.Any(item => string.Equals(item.CanonicalPath, canonicalDirectory, StringComparison.Ordinal)))
            return;
        string canonicalRoot = Path.GetFullPath(stagingDirectory);
        string? parent = Path.GetDirectoryName(canonicalDirectory);
        if (!string.Equals(canonicalDirectory, canonicalRoot, StringComparison.Ordinal)
            && (parent == null || !artifacts.Any(item =>
                item.Kind == ScenePackageStagingArtifactKind.Directory
                && string.Equals(item.CanonicalPath, parent, StringComparison.Ordinal))))
            throw new InvalidDataException();
        artifacts.Add(new ExportOwnedStagingArtifact(
            canonicalDirectory,
            ScenePackageStagingArtifactKind.Directory,
            _guard.CaptureOwnedDirectoryIdentityToken(
                canonicalDirectory, canonicalDirectory, operationId, ownerToken)));
    }

    private void RegisterExportFile(
        List<ExportOwnedStagingArtifact> artifacts,
        string file,
        string stagingDirectory,
        string operationId,
        string ownerToken)
    {
        string canonicalFile = Path.GetFullPath(file);
        string canonicalRoot = Path.GetFullPath(stagingDirectory);
        string? parent = Path.GetDirectoryName(canonicalFile);
        if (parent == null || !artifacts.Any(item =>
                item.Kind == ScenePackageStagingArtifactKind.Directory
                && string.Equals(item.CanonicalPath, parent, StringComparison.Ordinal))
            || string.Equals(canonicalFile, canonicalRoot, StringComparison.Ordinal)
            || artifacts.Any(item => string.Equals(item.CanonicalPath, canonicalFile, StringComparison.Ordinal)))
            throw new InvalidDataException();
        artifacts.Add(new ExportOwnedStagingArtifact(
            canonicalFile,
            ScenePackageStagingArtifactKind.File,
            _guard.CaptureOwnedFileIdentityToken(
                canonicalFile, canonicalFile, operationId, ownerToken)));
    }

    private void WriteRegisteredExportTemporary(
        List<ExportOwnedStagingArtifact> artifacts,
        string file,
        string stagingDirectory,
        string operationId,
        string ownerToken,
        Action<FileStream> write)
    {
        string? nativeIdentity = null;
        ExportOwnedStagingArtifact? slot = null;
        bool completed = false;
        try
        {
            using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                ProjectAccessPathIdentity created = _guard.Capture(file);
                if (!created.Exists || created.IsDirectory || created.IsLinkOrReparse) throw new InvalidDataException();
                nativeIdentity = created.IdentityToken;
                RegisterExportFile(artifacts, file, stagingDirectory, operationId, ownerToken);
                slot = artifacts.Single(item => string.Equals(item.CanonicalPath, Path.GetFullPath(file), StringComparison.Ordinal));
                _copyFaultInjector?.AfterTemporarySlotRegistered(file);
                write(output);
                output.Flush(flushToDisk: true);
            }
            ProjectAccessPathIdentity after = _guard.Capture(file);
            if (!after.Exists || after.IsDirectory || after.IsLinkOrReparse
                || !string.Equals(after.IdentityToken, nativeIdentity, StringComparison.Ordinal))
                throw new InvalidDataException();
            int index = artifacts.IndexOf(slot);
            if (index < 0) throw new InvalidDataException();
            artifacts[index] = slot with
            {
                IdentityToken = _guard.CaptureOwnedFileIdentityToken(file, file, operationId, ownerToken),
            };
            completed = true;
        }
        finally
        {
            if (!completed && slot != null && nativeIdentity != null)
            {
                try
                {
                    ProjectAccessPathIdentity current = _guard.Capture(file);
                    if (current.Exists && !current.IsDirectory && !current.IsLinkOrReparse
                        && string.Equals(current.IdentityToken, nativeIdentity, StringComparison.Ordinal))
                        File.Delete(file);
                    artifacts.Remove(slot);
                }
                catch
                {
                    // The outer cleanup retains evidence if ownership changed.
                }
            }
        }
    }

    private void TryDeleteOwnedDirectory(
        string guardRoot,
        string relativeStagingDirectory,
        string directory,
        string marker,
        string operationId,
        string ownerToken,
        IReadOnlyList<ExportOwnedStagingArtifact> artifacts)
    {
        try
        {
            if (!TryValidateExportStagingTree(
                    guardRoot,
                    relativeStagingDirectory,
                    directory,
                    marker,
                    operationId,
                    ownerToken,
                    artifacts)) return;
            foreach (ExportOwnedStagingArtifact artifact in artifacts
                         .Where(item => item.Kind == ScenePackageStagingArtifactKind.File)
                         .OrderByDescending(item => item.CanonicalPath.Length))
            {
                if (!ExportArtifactIdentityMatches(artifact, operationId, ownerToken)) return;
                File.Delete(artifact.CanonicalPath);
            }
            foreach (ExportOwnedStagingArtifact artifact in artifacts
                         .Where(item => item.Kind == ScenePackageStagingArtifactKind.Directory)
                         .OrderByDescending(item => item.CanonicalPath.Count(value => value == Path.DirectorySeparatorChar)))
            {
                if (!ExportArtifactIdentityMatches(artifact, operationId, ownerToken)
                    || Directory.EnumerateFileSystemEntries(artifact.CanonicalPath).Any()) return;
                Directory.Delete(artifact.CanonicalPath, recursive: false);
            }
        }
        catch { }
    }

    private bool TryValidateExportStagingTree(
        string guardRoot,
        string relativeStagingDirectory,
        string directory,
        string marker,
        string operationId,
        string ownerToken,
        IReadOnlyList<ExportOwnedStagingArtifact> artifacts)
    {
        string canonicalDirectory;
        string canonicalMarker;
        try
        {
            canonicalDirectory = Path.GetFullPath(directory);
            canonicalMarker = Path.GetFullPath(marker);
        }
        catch
        {
            return false;
        }
        if (!string.Equals(Path.GetFileName(canonicalDirectory), operationId, StringComparison.Ordinal)
            || !string.Equals(canonicalMarker, Path.Combine(canonicalDirectory, "owner.v1"), StringComparison.Ordinal)
            || !_guard.TryResolveDirectory(
                guardRoot,
                relativeStagingDirectory,
                mustExist: true,
                out string resolvedDirectory)
            || !string.Equals(resolvedDirectory, canonicalDirectory, StringComparison.Ordinal))
            return false;

        var expected = new Dictionary<string, ExportOwnedStagingArtifact>(StringComparer.Ordinal);
        foreach (ExportOwnedStagingArtifact artifact in artifacts)
        {
            string canonicalArtifact;
            try { canonicalArtifact = Path.GetFullPath(artifact.CanonicalPath); }
            catch { return false; }
            if (!string.Equals(canonicalArtifact, artifact.CanonicalPath, StringComparison.Ordinal)
                || !expected.TryAdd(canonicalArtifact, artifact)) return false;
            if (string.Equals(canonicalArtifact, canonicalDirectory, StringComparison.Ordinal))
            {
                if (artifact.Kind != ScenePackageStagingArtifactKind.Directory) return false;
                continue;
            }
            string? parent = Path.GetDirectoryName(canonicalArtifact);
            if (parent == null || !expected.TryGetValue(parent, out ExportOwnedStagingArtifact? parentArtifact)
                || parentArtifact.Kind != ScenePackageStagingArtifactKind.Directory) return false;
        }
        if (!expected.TryGetValue(canonicalDirectory, out _)
            || !expected.TryGetValue(canonicalMarker, out ExportOwnedStagingArtifact? markerArtifact)
            || markerArtifact.Kind != ScenePackageStagingArtifactKind.File)
            return false;

        var visited = new HashSet<string>(StringComparer.Ordinal) { canonicalDirectory };
        var pending = new Stack<string>();
        pending.Push(canonicalDirectory);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            string[] entries;
            try { entries = Directory.EnumerateFileSystemEntries(current).ToArray(); }
            catch { return false; }
            foreach (string rawEntry in entries)
            {
                string entry;
                try { entry = Path.GetFullPath(rawEntry); }
                catch { return false; }
                if (!string.Equals(Path.GetDirectoryName(entry), current, StringComparison.Ordinal)
                    || !expected.TryGetValue(entry, out ExportOwnedStagingArtifact? artifact)
                    || !visited.Add(entry)
                    || !ExportArtifactIdentityMatches(artifact, operationId, ownerToken))
                    return false;
                if (artifact.Kind == ScenePackageStagingArtifactKind.Directory) pending.Push(entry);
            }
        }
        return visited.Count == expected.Count
            && ExportArtifactIdentityMatches(expected[canonicalDirectory], operationId, ownerToken)
            && File.Exists(canonicalMarker)
            && string.Equals(File.ReadAllText(canonicalMarker), operationId, StringComparison.Ordinal);
    }

    private bool ExportArtifactIdentityMatches(
        ExportOwnedStagingArtifact artifact,
        string operationId,
        string ownerToken)
    {
        try
        {
            ProjectAccessPathIdentity identity = _guard.Capture(artifact.CanonicalPath);
            if (!identity.Exists || identity.IsLinkOrReparse
                || identity.IsDirectory != (artifact.Kind == ScenePackageStagingArtifactKind.Directory))
                return false;
            string current = artifact.Kind == ScenePackageStagingArtifactKind.File
                ? _guard.CaptureOwnedFileIdentityToken(
                    artifact.CanonicalPath, artifact.CanonicalPath, operationId, ownerToken)
                : _guard.CaptureOwnedDirectoryIdentityToken(
                    artifact.CanonicalPath, artifact.CanonicalPath, operationId, ownerToken);
            return string.Equals(current, artifact.IdentityToken, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private sealed class NeverDurableProbe : IScenePackageDurableStateProbe
    {
        public static NeverDurableProbe Instance { get; } = new();
        public bool IsSceneDurable(string projectRoot, string sceneId, string expectedSceneRelativePath) => false;
    }

    private sealed record ExportOwnedStagingArtifact(
        string CanonicalPath,
        ScenePackageStagingArtifactKind Kind,
        string IdentityToken);

    private sealed record ImportSourceArtifact(
        ScenePackageTransportArtifact Artifact,
        string Source,
        long Length,
        DateTime LastWriteUtc,
        string Identity,
        string SafeStem);

    private sealed record StagedImportArtifact(
        string Staged,
        string StagedRelative,
        string PackageRelative,
        string? RelativeFinal,
        string? Final,
        ScenePackageFileType Type,
        ScenePackageFileIntegrity Integrity)
    {
        internal bool IsPublishedProjectAsset => RelativeFinal != null && Final != null;
    }

    private sealed class PackagePayloadTooLargeException : IOException { }
}
