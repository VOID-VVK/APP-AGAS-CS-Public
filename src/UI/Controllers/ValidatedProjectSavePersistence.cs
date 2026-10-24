using System;
using System.IO;
using AppAgasCs.Application;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Mcp;

namespace AGAS.UI.Controllers;

internal sealed class ValidatedProjectSavePersistence : IProjectSavePersistence
{
    private readonly AgasResourceRepository _repository;
    private readonly AgasMcpPathValidator _pathValidator;

    public ValidatedProjectSavePersistence(
        AgasResourceRepository repository,
        AgasMcpPathValidator pathValidator)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _pathValidator = pathValidator ?? throw new ArgumentNullException(nameof(pathValidator));
    }

    public AgasPersistenceResult SaveScene(string projectRoot, AgasSceneResource scene)
    {
        AgasMcpPathValidationResult path = _pathValidator.ValidateAgasSceneFile(projectRoot, scene.Id);
        return path.Success
            ? _repository.Save(scene, path.AbsolutePath)
            : AgasPersistenceResult.Fail(path.Message);
    }

    public AgasPersistenceResult SaveProject(string projectRoot, AgasProjectResource project)
    {
        AgasMcpPathValidationResult path = _pathValidator.ValidateAgasProjectFile(projectRoot);
        return path.Success
            ? _repository.Save(project, path.AbsolutePath)
            : AgasPersistenceResult.Fail(path.Message);
    }

    public AgasPersistenceResult SaveLayout(string projectRoot, WorkspaceLayoutResource layout)
    {
        AgasMcpPathValidationResult path = _pathValidator.ValidateAgasLayoutFile(projectRoot);
        return path.Success
            ? _repository.Save(layout, path.AbsolutePath)
            : AgasPersistenceResult.Fail(path.Message);
    }

    public AgasPersistenceResult DeleteScene(string projectRoot, string sceneId)
    {
        AgasMcpPathValidationResult path = _pathValidator.ValidateAgasSceneFile(projectRoot, sceneId);
        if (!path.Success)
        {
            return AgasPersistenceResult.Fail(path.Message);
        }

        try
        {
            if (File.Exists(path.AbsolutePath))
            {
                File.Delete(path.AbsolutePath);
            }
            return AgasPersistenceResult.Ok();
        }
        catch (Exception exception)
        {
            return AgasPersistenceResult.Fail($"删除场景文件失败：{exception.Message}");
        }
    }
}
