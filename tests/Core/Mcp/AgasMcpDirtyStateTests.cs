using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using Xunit;

namespace AppAgasCs.Tests.Core.Mcp;

public sealed class AgasMcpDirtyStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryBindProject_RejectsSecondProjectWhileFirstProjectHasDirtyOrPendingDeleteState(bool pendingDelete)
    {
        var dirty = new AgasMcpDirtyState();
        string projectA = NewProjectRoot("a");
        string projectB = NewProjectRoot("b");

        Assert.True(dirty.TryBindProject(projectA, "project-a", out string bindAError), bindAError);
        if (pendingDelete)
        {
            dirty.MarkSceneDeleted("shared-scene");
        }
        else
        {
            dirty.MarkSceneDirty("shared-scene");
        }

        bool bindB = dirty.TryBindProject(projectB, "project-b", out string bindBError);

        Assert.False(bindB);
        Assert.Contains("another project", bindBError, StringComparison.Ordinal);
        AgasMcpDirtySnapshot snapshotA = dirty.Snapshot();
        Assert.Equal(Normalize(projectA), snapshotA.ProjectRoot);
        Assert.Equal("project-a", snapshotA.ProjectId);
        Assert.True(snapshotA.IsDirty);
        if (pendingDelete)
        {
            Assert.True(snapshotA.DirtyProject);
            Assert.Empty(snapshotA.DirtySceneIds);
            Assert.Equal(new[] { "shared-scene" }, snapshotA.PendingDeletedSceneIds);
        }
        else
        {
            Assert.False(snapshotA.DirtyProject);
            Assert.Equal(new[] { "shared-scene" }, snapshotA.DirtySceneIds);
            Assert.Empty(snapshotA.PendingDeletedSceneIds);
        }
    }

    [Fact]
    public void TryBindProject_RejectsSecondProjectWhileFirstProjectHasPendingDelete()
    {
        var dirty = new AgasMcpDirtyState();
        string projectA = NewProjectRoot("a");
        string projectB = NewProjectRoot("b");

        Assert.True(dirty.TryBindProject(projectA, "project-a", out string bindAError), bindAError);
        dirty.MarkSceneDeleted("shared-scene");

        bool bindB = dirty.TryBindProject(projectB, "project-b", out string bindBError);

        Assert.False(bindB);
        Assert.Contains("another project", bindBError, StringComparison.Ordinal);
        AgasMcpDirtySnapshot snapshotA = dirty.Snapshot();
        Assert.Equal(Normalize(projectA), snapshotA.ProjectRoot);
        Assert.Equal("project-a", snapshotA.ProjectId);
        Assert.True(snapshotA.DirtyProject);
        Assert.Empty(snapshotA.DirtySceneIds);
        Assert.Equal(new[] { "shared-scene" }, snapshotA.PendingDeletedSceneIds);
    }

    [Fact]
    public void SnapshotAndClearForProject_DoNotCrossTalkWhenProjectsUseSameSceneId()
    {
        var dirty = new AgasMcpDirtyState();
        string projectA = NewProjectRoot("a");
        string projectB = NewProjectRoot("b");

        Assert.True(dirty.TryBindProject(projectA, "project-a", out string bindAError), bindAError);
        dirty.MarkSceneDirty("same-scene");

        Assert.Null(dirty.SnapshotForProject(projectB, "project-b"));
        dirty.ClearForProject(projectB, "project-b");
        AgasMcpDirtySnapshot stillA = Assert.IsType<AgasMcpDirtySnapshot>(dirty.SnapshotForProject(projectA, "project-a"));
        Assert.Equal(new[] { "same-scene" }, stillA.DirtySceneIds);

        dirty.ClearForProject(projectA, "project-a");
        Assert.False(dirty.IsDirty);
        Assert.True(dirty.TryBindProject(projectB, "project-b", out string bindBError), bindBError);
        dirty.MarkSceneDirty("same-scene");

        Assert.Null(dirty.SnapshotForProject(projectA, "project-a"));
        AgasMcpDirtySnapshot snapshotB = Assert.IsType<AgasMcpDirtySnapshot>(dirty.SnapshotForProject(projectB, "project-b"));
        Assert.Equal("project-b", snapshotB.ProjectId);
        Assert.Equal(new[] { "same-scene" }, snapshotB.DirtySceneIds);
    }

    [Fact]
    public void ClearForProject_ClearsOnlyMatchingBoundProject()
    {
        var dirty = new AgasMcpDirtyState();
        string projectA = NewProjectRoot("a");
        string projectB = NewProjectRoot("b");

        Assert.True(dirty.TryBindProject(projectA, "project-a", out string bindError), bindError);
        dirty.MarkProjectDirty();
        dirty.MarkLayoutDirty();
        dirty.MarkSceneDirty("scene-a");

        dirty.ClearForProject(projectB, "project-b");
        Assert.True(dirty.IsDirty);
        Assert.NotNull(dirty.SnapshotForProject(projectA, "project-a"));

        dirty.ClearForProject(projectA, "project-a");

        Assert.False(dirty.IsDirty);
        AgasMcpDirtySnapshot snapshot = dirty.Snapshot();
        Assert.Equal(Normalize(projectA), snapshot.ProjectRoot);
        Assert.Equal("project-a", snapshot.ProjectId);
        Assert.Empty(snapshot.DirtySceneIds);
        Assert.Empty(snapshot.PendingDeletedSceneIds);
    }

    [Fact]
    public void ProjectSave_SourceKeepsDirtyStateWhenSaveFailureBranchesAreHit()
    {
        string source = File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/Mcp/AgasMcpProjectFacade.cs"));

        Assert.Contains("before.DirtySceneIds.ToArray()", source);
        Assert.Contains("Dirty scene is not loaded in memory.", source);
        Assert.Contains($"AgasMcpErrorCodes.{nameof(AgasMcpErrorCodes.SceneNotFound)}", source);
        Assert.Contains("if (result.Success)\n            {\n                savedFiles.Add(logical);\n                _dirtyState.ClearSceneDirty(sceneId);", source);
        Assert.Contains("else\n            {\n                failedFiles.Add(_errorMapper.ToFailure(logical, result));\n            }", source);
        Assert.Contains("string[] pending = _dirtyState.PendingDirtyFiles(context.Project).ToArray();", source);
        Assert.Contains("receiptTracker?.Publish(BuildReceipt());", source);
        Assert.Contains("AgasMcpProjectSaveDataDto data = BuildReceipt();", source);
        Assert.Contains("return AgasMcpToolResponse.Success(saved: !_dirtyState.IsDirty, dirty: _dirtyState.IsDirty, data);", source);
    }

    private static string NewProjectRoot(string name) =>
        Path.Combine(Path.GetTempPath(), "agas-mcp-dirty-tests", name, Guid.NewGuid().ToString("N"));

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
