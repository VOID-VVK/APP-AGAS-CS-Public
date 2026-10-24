using Xunit;

namespace AppAgasCs.Tests.Core;

public sealed class AgasResourceRoundTripTests
{
    [Fact]
    public void ResourceSources_DefineRequiredCustomResourceFields()
    {
        string resources = string.Concat(Directory.GetFiles(Path.Combine(ProjectRoot.Path, "src/Core/Resources"), "*.cs").Select(File.ReadAllText));

        foreach (string required in new[]
        {
            "[GlobalClass]",
            "ResourceVersion",
            "ProjectId",
            "ActiveSceneId",
            "GridSettingsResource",
            "SceneIndexResource",
            "ActiveLayerId",
            "SceneViewStateResource",
            "ImageInstanceResource",
            "ImageBlueprintBindingResource",
            "BlueprintInputImageBindingResource",
            "BlueprintModuleDependencyResource",
            "PromptFragmentResource",
            "WorkspaceLayoutResource",
            "LeftPanelWidth",
            "ActiveBottomTab",
        })
        {
            Assert.Contains(required, resources);
        }
    }

    [Fact]
    public void RepositorySource_UsesResourceSaverTempFileReplacementAndResourceLoader()
    {
        string source = Read("src/Core/Persistence/AgasResourceRepository.cs");

        Assert.Contains("ResourceSaver.Save(resource, tempPath)", source);
        Assert.Contains("targetPath + \".tmp.tres\"", source);
        Assert.Contains("File.Move(tempPath, targetPath, overwrite: true)", source);
        Assert.Contains("ResourceLoader.Load(fullPath)", source);
        Assert.Contains("保存失败", source);
    }

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(ProjectRoot.Path, relativePath));
}
