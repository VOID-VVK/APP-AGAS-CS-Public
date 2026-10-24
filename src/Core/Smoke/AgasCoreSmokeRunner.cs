using System;
using System.IO;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using Godot;

namespace AppAgasCs.Core.Smoke;

public partial class AgasCoreSmokeRunner : Node
{
    public override void _Ready()
    {
        try
        {
            Run();
            GD.Print("[AGAS_CORE_SMOKE] PASS");
            GetTree().Quit(0);
        }
        catch (Exception ex)
        {
            GD.PrintErr("[AGAS_CORE_SMOKE] FAIL: " + ex);
            GetTree().Quit(1);
        }
    }

    private static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "agas-godot-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        AgasProjectInitializer initializer = new();
        Require(initializer.EnsureInitialized(root));

        RequireFile(AgasPaths.ProjectFile(root));
        RequireFile(AgasPaths.WorkspaceLayoutFile(root));
        RequireFile(AgasPaths.SceneFile(root, "scene-001"));
        Require(!File.Exists(AgasPaths.SceneFile(root, "scene-002")), "新项目不应自带 scene-002。");
        Require(!File.Exists(AgasPaths.SceneFile(root, "scene-003")), "新项目不应自带 scene-003。");

        AgasResourceRepository repository = new();
        AgasProjectResource project = RequireLoad(repository.Load<AgasProjectResource>(AgasPaths.ProjectFile(root)));
        Require(project.ActiveSceneId == "scene-001", "ActiveSceneId 未指向 scene-001。");
        Require(project.Grid.SizePx == 48, "默认 Grid.SizePx 不是 48。");
        Require(project.Scenes.Count == 1, "新项目默认 scene 索引数量应为 1。");

        AgasSceneResource scene = RequireLoad(repository.Load<AgasSceneResource>(AgasPaths.SceneFile(root, "scene-001")));
        Require(scene.Layers.Count == 1, "scene-001 默认 layer 数量不是 1。");
        AgasLayerResource layer = scene.Layers[0];
        layer.Images.Add(new ImageInstanceResource
        {
            Id = "image-001",
            ImagePath = "images/pixel.png",
            Order = 1,
            X = 48.0,
            Y = 96.0,
            ScaleX = 1.5,
            ScaleY = 2.0,
            RotationDegrees = 30.0,
            Opacity = 0.75,
        });
        layer.PromptFragments.Add(new PromptFragmentResource
        {
            Id = "prompt-001",
            Text = "pixel hero",
            Enabled = true,
            Order = 1,
        });
        Require(repository.Save(scene, AgasPaths.SceneFile(root, scene.Id)));

        AgasSceneResource reloaded = RequireLoad(repository.Load<AgasSceneResource>(AgasPaths.SceneFile(root, scene.Id)));
        Require(reloaded.Layers[0].Images.Count == 1, "图片实例未 roundtrip。 ");
        Require(reloaded.Layers[0].Images[0].ImagePath == "images/pixel.png", "ImagePath roundtrip 不一致。");
        Require(Math.Abs(reloaded.Layers[0].Images[0].X - 48.0) < 0.001, "图片 X roundtrip 不一致。");
        Require(reloaded.Layers[0].PromptFragments.Count == 1, "PromptFragments 未 roundtrip。");
        Require(reloaded.Layers[0].PromptFragments[0].Text == "pixel hero", "PromptFragment.Text roundtrip 不一致。");

        string imagesDir = Path.Combine(root, "images");
        Directory.CreateDirectory(imagesDir);
        string pngPath = Path.Combine(imagesDir, "pixel.png");
        using Image png = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        png.SetPixel(0, 0, new Color(1, 0, 0, 1));
        Require(png.SavePng(pngPath) == Error.Ok, "测试 PNG 写入失败。");
        Image loadedImage = RequireLoad(ExternalImageLoader.LoadProjectImage(root, "images/pixel.png"));
        Require(loadedImage.GetWidth() == 1 && loadedImage.GetHeight() == 1, "Image.LoadFromFile 加载尺寸不一致。");

        AgasPersistenceResult absoluteImageRejected = ExternalImageLoader.LoadProjectImage(root, pngPath).ErrorMessage is not null
            ? AgasPersistenceResult.Ok()
            : AgasPersistenceResult.Fail("绝对图片路径未被拒绝。");
        Require(absoluteImageRejected);

        // Save-failure atomicity uses a disposable second scene path (not a default fixture).
        string scenePath = AgasPaths.SceneFile(root, "scene-save-probe");
        AgasSceneResource original = AgasProjectInitializer.CreateDefaultScene("scene-save-probe", "Original Scene", 1);
        Require(repository.Save(original, scenePath));
        string blockingTempDirectory = scenePath + ".tmp.tres";
        Directory.CreateDirectory(blockingTempDirectory);
        try
        {
            AgasSceneResource replacement = AgasProjectInitializer.CreateDefaultScene("scene-save-probe", "Replacement Scene", 1);
            AgasPersistenceResult failed = repository.Save(replacement, scenePath);
            Require(!failed.Success, "临时路径被目录占用时保存不应成功。");
            AgasSceneResource kept = RequireLoad(repository.Load<AgasSceneResource>(scenePath));
            Require(kept.Name == "Original Scene", "保存失败后旧文件未保留。");
        }
        finally
        {
            Directory.Delete(blockingTempDirectory, recursive: true);
        }
    }

    private static void Require(AgasPersistenceResult result)
    {
        if (!result.Success)
        {
            throw new InvalidOperationException(result.ErrorMessage ?? "操作失败。");
        }
    }

    private static T RequireLoad<T>(AgasLoadResult<T> result)
    {
        if (!result.Success || result.Resource is null)
        {
            throw new InvalidOperationException(result.ErrorMessage ?? "加载失败。");
        }

        return result.Resource;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("预期文件不存在。", path);
        }
    }
}
