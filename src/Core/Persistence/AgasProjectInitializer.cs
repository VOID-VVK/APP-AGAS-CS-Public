using System;
using System.IO;
using AppAgasCs.Core.Resources;

namespace AppAgasCs.Core.Persistence;

public sealed class AgasProjectInitializer
{
    private readonly AgasResourceRepository _repository;

    public AgasProjectInitializer(AgasResourceRepository? repository = null)
    {
        _repository = repository ?? new AgasResourceRepository();
    }

    public AgasPersistenceResult EnsureInitialized(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return AgasPersistenceResult.Fail("初始化失败：项目目录为空。");
        }

        string root = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(root))
        {
            return AgasPersistenceResult.Fail($"初始化失败：项目目录不存在：{root}");
        }

        string projectFile = AgasPaths.ProjectFile(root);
        if (File.Exists(projectFile))
        {
            AgasLoadResult<AgasProjectResource> projectLoad = _repository.Load<AgasProjectResource>(projectFile);
            if (!projectLoad.Success || projectLoad.Resource is null)
            {
                return AgasPersistenceResult.Fail(projectLoad.ErrorMessage ?? "初始化失败：无法加载项目文件。");
            }

            if (AgasProjectResource.IsOlderThanSupportedResourceVersion(projectLoad.Resource.ResourceVersion))
            {
                return AgasPersistenceResult.Fail("PROJECT_VERSION_INVALID: 项目版本低于最早支持版本。");
            }

            if (projectLoad.Resource.RequiresReadOnly)
            {
                return AgasPersistenceResult.Ok();
            }

            return AgasPersistenceResult.Ok();
        }

        Directory.CreateDirectory(AgasPaths.AgasDirectory(root));
        Directory.CreateDirectory(AgasPaths.ScenesDirectory(root));
        AgasProjectResource project = CreateDefaultProject(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        WorkspaceLayoutResource layout = new()
        {
            LeftPanelWidth = 280.0,
            PreviewHeight = 180.0,
            RightPanelWidth = 280.0,
            BottomDockHeight = 240.0,
            BottomDockCollapsed = true,
        };
        // New projects start with a single empty scene; users add more explicitly.
        AgasSceneResource defaultScene = CreateDefaultScene("scene-001", "Scene", 0);
        AgasPersistenceResult sceneSave = _repository.Save(defaultScene, AgasPaths.SceneFile(root, defaultScene.Id));
        if (!sceneSave.Success)
        {
            return sceneSave;
        }

        AgasPersistenceResult layoutSave = _repository.Save(layout, AgasPaths.WorkspaceLayoutFile(root));
        if (!layoutSave.Success)
        {
            return layoutSave;
        }

        return _repository.Save(project, projectFile);
    }

    public static AgasProjectResource CreateDefaultProject(string projectName)
    {
        AgasProjectResource project = new()
        {
            ProjectId = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(projectName) ? "AGAS Project" : projectName,
            ActiveSceneId = "scene-001",
            Grid = new GridSettingsResource
            {
                SizePx = 48,
                SnapEnabled = true,
                Visible = true,
            },
        };

        project.Scenes.Add(new SceneIndexResource
        {
            Id = "scene-001",
            Name = "Scene",
            RelativePath = ".agas/scenes/scene-001.tres",
            Order = 0,
        });
        return project;
    }

    public static AgasSceneResource CreateDefaultScene(string id, string name, int order)
    {
        string layerId = $"{id}-layer-001";
        AgasSceneResource scene = new()
        {
            Id = id,
            Name = name,
            ActiveLayerId = layerId,
            ViewState = new SceneViewStateResource
            {
                CameraX = 0.0,
                CameraY = 0.0,
                Zoom = 1.0,
            },
        };

        scene.Layers.Add(new AgasLayerResource
        {
            Id = layerId,
            Name = "Layer 1",
            Order = order,
            Visible = true,
            Locked = false,
        });
        return scene;
    }
}
