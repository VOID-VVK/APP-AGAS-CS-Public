using System;
using System.Collections.Generic;
using System.Linq;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;

namespace AppAgasCs.Application;

public interface IProjectSavePersistence
{
    AgasPersistenceResult SaveScene(string projectRoot, AgasSceneResource scene);
    AgasPersistenceResult SaveProject(string projectRoot, AgasProjectResource project);
    AgasPersistenceResult SaveLayout(string projectRoot, WorkspaceLayoutResource layout);
    AgasPersistenceResult DeleteScene(string projectRoot, string sceneId);
}

public sealed record ProjectSaveRequest(
    string ProjectRoot,
    AgasProjectResource Project,
    WorkspaceLayoutResource Layout,
    AgasSceneResource CurrentScene,
    IReadOnlyCollection<AgasSceneResource> LoadedScenes,
    IReadOnlyCollection<string> DirtySceneIds,
    ISet<string> UiPendingDeletedSceneIds,
    IReadOnlyCollection<string> McpPendingDeletedSceneIds,
    bool RequiresReadOnly,
    string ReadOnlyReasonCode);

public sealed record ProjectSaveResult(bool Success, string Message, bool PartialSaved)
{
    public static ProjectSaveResult Ok() => new(true, string.Empty, true);
    public static ProjectSaveResult Fail(string message, bool partialSaved) => new(false, message, partialSaved);
}

/// <summary>
/// Owns the controlled project-save transaction ordering. User tools are stored
/// outside this project transaction.
/// </summary>
public sealed class ProjectSaveCoordinator
{
    private readonly IProjectSavePersistence _persistence;

    public ProjectSaveCoordinator(IProjectSavePersistence persistence)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
    }

    public AgasPersistenceResult SaveCurrentScene(
        string projectRoot,
        AgasSceneResource? currentScene,
        bool requiresReadOnly,
        string readOnlyReasonCode)
    {
        if (requiresReadOnly)
        {
            return AgasPersistenceResult.Fail($"PROJECT_READ_ONLY: {readOnlyReasonCode}");
        }

        return currentScene == null
            ? AgasPersistenceResult.Fail("保存失败：当前场景为空。")
            : _persistence.SaveScene(projectRoot, currentScene);
    }

    public ProjectSaveResult Save(ProjectSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequiresReadOnly)
        {
            return ProjectSaveResult.Fail($"项目为只读状态（{request.ReadOnlyReasonCode}）。", false);
        }

        AgasPersistenceResult migration = AgasProjectMigration.PrepareForControlledSave(request.Project);
        if (!migration.Success)
        {
            return ProjectSaveResult.Fail(
                migration.ErrorMessage ?? "项目版本不受支持。",
                false);
        }

        bool partialSaved = false;

        HashSet<string> projectSceneIds = request.Project.Scenes
            .Select(scene => scene.Id)
            .ToHashSet(StringComparer.Ordinal);
        Dictionary<string, AgasSceneResource> loadedById = request.LoadedScenes
            .Append(request.CurrentScene)
            .Where(scene => !string.IsNullOrWhiteSpace(scene.Id))
            .GroupBy(scene => scene.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (string dirtySceneId in request.DirtySceneIds)
        {
            if (!projectSceneIds.Contains(dirtySceneId) || !loadedById.ContainsKey(dirtySceneId))
            {
                return ProjectSaveResult.Fail(
                    $"MCP dirty scene is not loaded for UI save: {dirtySceneId}",
                    partialSaved);
            }
        }

        foreach (AgasSceneResource scene in loadedById.Values.OrderBy(scene => scene.Id, StringComparer.Ordinal))
        {
            if (!projectSceneIds.Contains(scene.Id))
            {
                continue;
            }

            AgasPersistenceResult sceneSave = _persistence.SaveScene(request.ProjectRoot, scene);
            if (!sceneSave.Success)
            {
                return ProjectSaveResult.Fail(sceneSave.ErrorMessage ?? "保存场景失败。", partialSaved);
            }
        }
        partialSaved = true;

        AgasPersistenceResult projectSave = _persistence.SaveProject(request.ProjectRoot, request.Project);
        if (!projectSave.Success)
        {
            return ProjectSaveResult.Fail(projectSave.ErrorMessage ?? "保存 project.tres 失败。", partialSaved);
        }
        partialSaved = true;

        AgasPersistenceResult layoutSave = _persistence.SaveLayout(request.ProjectRoot, request.Layout);
        if (!layoutSave.Success)
        {
            return ProjectSaveResult.Fail(layoutSave.ErrorMessage ?? "保存 workspace-layout.tres 失败。", partialSaved);
        }
        partialSaved = true;

        foreach (string sceneId in request.UiPendingDeletedSceneIds.ToArray())
        {
            AgasPersistenceResult delete = _persistence.DeleteScene(request.ProjectRoot, sceneId);
            if (!delete.Success)
            {
                return ProjectSaveResult.Fail(delete.ErrorMessage ?? "处理 UI 待删除场景失败。", partialSaved);
            }
            request.UiPendingDeletedSceneIds.Remove(sceneId);
        }

        foreach (string sceneId in request.McpPendingDeletedSceneIds)
        {
            AgasPersistenceResult delete = _persistence.DeleteScene(request.ProjectRoot, sceneId);
            if (!delete.Success)
            {
                return ProjectSaveResult.Fail(delete.ErrorMessage ?? "处理 MCP 待删除场景失败。", partialSaved);
            }
        }

        return ProjectSaveResult.Ok();
    }
}
