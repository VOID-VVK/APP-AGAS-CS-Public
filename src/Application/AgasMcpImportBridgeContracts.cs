#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Mcp.Runtime;

namespace AppAgasCs.Application;

public sealed record AgasMcpImportProjectCapture(
    string ProjectRoot,
    string ProjectId,
    bool RequiresReadOnly);

public sealed record AgasMcpScenePackageExportWork(
    AgasMcpImportProjectCapture Project,
    AgasMcpScenePackageExportCapture Package);

public sealed record AgasMcpScenePackageExportCapture
{
    public AgasMcpScenePackageExportCapture(
        bool success,
        string code,
        string packageRef,
        ReadOnlyMemory<byte> manifestUtf8,
        IReadOnlyList<AgasScenePackageExportFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        Success = success;
        Code = code ?? string.Empty;
        PackageRef = packageRef ?? string.Empty;
        ManifestUtf8 = manifestUtf8.ToArray();
        Files = Array.AsReadOnly(System.Linq.Enumerable.ToArray(files));
    }

    public bool Success { get; }
    public string Code { get; }
    public string PackageRef { get; }
    public ReadOnlyMemory<byte> ManifestUtf8 { get; }
    public IReadOnlyList<AgasScenePackageExportFile> Files { get; }

    public static AgasMcpScenePackageExportCapture Fail(string code) =>
        new(false, code, string.Empty, ReadOnlyMemory<byte>.Empty, Array.Empty<AgasScenePackageExportFile>());
}

public sealed record AgasMcpScenePackagePublishResult(
    bool Success,
    string Code,
    string SceneId,
    string Name,
    IReadOnlyList<string> DirtySceneIds)
{
    public static AgasMcpScenePackagePublishResult Fail(string code) =>
        new(false, code, string.Empty, string.Empty, Array.Empty<string>());
}

/// <summary>Main-owned Resource/session projection and package publish port.</summary>
public interface IAgasMcpScenePackageBridge
{
    AgasMcpScenePackageExportCapture CaptureExport(string sceneId);

    ValueTask<AgasMcpScenePackagePublishResult> PublishPreparedImportAsync(
        AgasScenePackagePreparedImport prepared,
        AgasMcpCommitExecution execution,
        CancellationToken cancellationToken);
}
