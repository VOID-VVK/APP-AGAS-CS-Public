using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;

namespace AppAgasCs.Mcp;

public sealed class AgasMcpDirtyState
{
    private readonly object _gate = new();
    private readonly HashSet<string> _dirtySceneIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingDeletedSceneIds = new(StringComparer.Ordinal);
    private string _projectRoot = string.Empty;
    private string _projectId = string.Empty;

    public bool DirtyProject { get; private set; }
    public bool DirtyLayout { get; private set; }
    public string ProjectRoot
    {
        get
        {
            lock (_gate) return _projectRoot;
        }
    }
    public string ProjectId
    {
        get
        {
            lock (_gate) return _projectId;
        }
    }

    public bool IsDirty
    {
        get
        {
            lock (_gate)
            {
                return DirtyProject || DirtyLayout || _dirtySceneIds.Count > 0 || _pendingDeletedSceneIds.Count > 0;
            }
        }
    }

    public bool TryBindProject(string projectRoot, string projectId, out string errorMessage)
    {
        string root = NormalizeRoot(projectRoot);
        string id = NormalizeProjectId(projectId);
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(id))
            {
                errorMessage = "Current project identity is unavailable.";
                return false;
            }

            if (IsUnboundLocked())
            {
                _projectRoot = root;
                _projectId = id;
                errorMessage = string.Empty;
                return true;
            }

            if (ProjectMatchesLocked(root, id))
            {
                errorMessage = string.Empty;
                return true;
            }

            if (IsDirtyLocked())
            {
                errorMessage = "MCP dirty state belongs to another project. Save or close that project before operating on the current project.";
                return false;
            }

            ClearLocked();
            _projectRoot = root;
            _projectId = id;
            errorMessage = string.Empty;
            return true;
        }
    }

    public bool IsForProject(string projectRoot, string projectId)
    {
        string root = NormalizeRoot(projectRoot);
        string id = NormalizeProjectId(projectId);
        lock (_gate)
        {
            return ProjectMatchesLocked(root, id);
        }
    }

    public AgasMcpDirtySnapshot? SnapshotForProject(string projectRoot, string projectId)
    {
        string root = NormalizeRoot(projectRoot);
        string id = NormalizeProjectId(projectId);
        lock (_gate)
        {
            return ProjectMatchesLocked(root, id) ? SnapshotLocked() : null;
        }
    }

    public void ClearForProject(string projectRoot, string projectId)
    {
        string root = NormalizeRoot(projectRoot);
        string id = NormalizeProjectId(projectId);
        lock (_gate)
        {
            if (!ProjectMatchesLocked(root, id))
            {
                return;
            }

            ClearLocked();
        }
    }

    public void MarkProjectDirty()
    {
        lock (_gate) DirtyProject = true;
    }

    public void MarkLayoutDirty()
    {
        lock (_gate) DirtyLayout = true;
    }

    public void MarkSceneDirty(string sceneId)
    {
        if (string.IsNullOrWhiteSpace(sceneId)) return;
        lock (_gate) _dirtySceneIds.Add(sceneId);
    }

    public void MarkSceneDeleted(string sceneId)
    {
        if (string.IsNullOrWhiteSpace(sceneId)) return;
        lock (_gate)
        {
            _pendingDeletedSceneIds.Add(sceneId);
            _dirtySceneIds.Remove(sceneId);
            DirtyProject = true;
        }
    }

    public void ClearProjectDirty()
    {
        lock (_gate) DirtyProject = false;
    }

    public void ClearLayoutDirty()
    {
        lock (_gate) DirtyLayout = false;
    }

    public void ClearSceneDirty(string sceneId)
    {
        lock (_gate) _dirtySceneIds.Remove(sceneId);
    }

    public void ClearPendingDelete(string sceneId)
    {
        lock (_gate) _pendingDeletedSceneIds.Remove(sceneId);
    }

    public AgasMcpDirtySnapshot Snapshot()
    {
        lock (_gate)
        {
            return SnapshotLocked();
        }
    }

    public IReadOnlyList<string> PendingDirtyFiles(AgasProjectResource? project = null)
    {
        AgasMcpDirtySnapshot snapshot = Snapshot();
        List<string> files = new();
        files.AddRange(snapshot.DirtySceneIds.Select(SceneFile));
        if (snapshot.DirtyProject)
        {
            files.Add($"{AgasPaths.AgasDirectoryName}/{AgasPaths.ProjectFileName}");
        }
        if (snapshot.DirtyLayout)
        {
            files.Add($"{AgasPaths.AgasDirectoryName}/{AgasPaths.WorkspaceLayoutFileName}");
        }
        files.AddRange(snapshot.PendingDeletedSceneIds.Select(SceneFile));
        return Array.AsReadOnly(files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    public static string SceneFile(string sceneId) => $"{AgasPaths.AgasDirectoryName}/{AgasPaths.ScenesDirectoryName}/{sceneId}.tres";

    private AgasMcpDirtySnapshot SnapshotLocked() =>
        new(
            _projectRoot,
            _projectId,
            DirtyProject,
            DirtyLayout,
            _dirtySceneIds.Order(StringComparer.Ordinal).ToArray(),
            _pendingDeletedSceneIds.Order(StringComparer.Ordinal).ToArray());

    private bool IsDirtyLocked() => DirtyProject || DirtyLayout || _dirtySceneIds.Count > 0 || _pendingDeletedSceneIds.Count > 0;

    private bool IsUnboundLocked() => string.IsNullOrWhiteSpace(_projectRoot) && string.IsNullOrWhiteSpace(_projectId);

    private bool ProjectMatchesLocked(string projectRoot, string projectId) =>
        string.Equals(_projectRoot, projectRoot, StringComparison.Ordinal)
        && string.Equals(_projectId, projectId, StringComparison.Ordinal);

    private void ClearLocked()
    {
        DirtyProject = false;
        DirtyLayout = false;
        _dirtySceneIds.Clear();
        _pendingDeletedSceneIds.Clear();
    }

    private static string NormalizeRoot(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeProjectId(string projectId) => projectId.Trim();
}

public sealed record AgasMcpDirtySnapshot
{
    public AgasMcpDirtySnapshot(
        string ProjectRoot,
        string ProjectId,
        bool DirtyProject,
        bool DirtyLayout,
        IReadOnlyList<string> DirtySceneIds,
        IReadOnlyList<string> PendingDeletedSceneIds)
    {
        this.ProjectRoot = ProjectRoot ?? throw new ArgumentNullException(nameof(ProjectRoot));
        this.ProjectId = ProjectId ?? throw new ArgumentNullException(nameof(ProjectId));
        this.DirtyProject = DirtyProject;
        this.DirtyLayout = DirtyLayout;
        this.DirtySceneIds = Freeze(DirtySceneIds, nameof(DirtySceneIds));
        this.PendingDeletedSceneIds = Freeze(PendingDeletedSceneIds, nameof(PendingDeletedSceneIds));
    }

    public string ProjectRoot { get; }
    public string ProjectId { get; }
    public bool DirtyProject { get; }
    public bool DirtyLayout { get; }
    public IReadOnlyList<string> DirtySceneIds { get; }
    public IReadOnlyList<string> PendingDeletedSceneIds { get; }
    public bool IsDirty => DirtyProject || DirtyLayout || DirtySceneIds.Count > 0 || PendingDeletedSceneIds.Count > 0;

    private static IReadOnlyList<string> Freeze(IReadOnlyList<string> source, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        if (source.Any(value => value is null))
        {
            throw new ArgumentException("MCP dirty snapshots cannot contain null identifiers.", parameterName);
        }
        return Array.AsReadOnly(source.ToArray());
    }
}
