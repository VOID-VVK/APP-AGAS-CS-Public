using Xunit;

namespace AppAgasCs.Tests.Core;

public sealed class AgasProjectInitializationTests
{
    [Fact]
    public void InitializerSource_DefinesSingleEmptySceneFixtureAndAgasPaths()
    {
        string source = File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/Core/Persistence/AgasProjectInitializer.cs"));

        Assert.Contains("scene-001", source);
        Assert.Contains(".agas/scenes/scene-001.tres", source);
        Assert.Contains("ActiveSceneId = \"scene-001\"", source);
        Assert.DoesNotContain("scene-002", source);
        Assert.DoesNotContain("scene-003", source);
        Assert.Contains("single empty scene", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectLifecycle_DoesNotCreateOrValidateLegacyBlueprintIndex()
    {
        string initializer = File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/Core/Persistence/AgasProjectInitializer.cs"));
        string migration = File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/Core/Persistence/AgasProjectMigration.cs"));

        Assert.DoesNotContain("BlueprintsDirectory", initializer, StringComparison.Ordinal);
        Assert.DoesNotContain("BlueprintProjectIndexValidator", initializer, StringComparison.Ordinal);
        Assert.DoesNotContain("BlueprintProjectIndexValidator", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("project.Blueprints", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectGodot_MainScenePointsToUiMainTscn()
    {
        string projectGodot = File.ReadAllText(Path.Combine(ProjectRoot.Path, "project.godot"));

        Assert.Contains("run/main_scene=\"res://src/UI/Main.tscn\"", projectGodot);
        Assert.Contains("project/assembly_name=\"APP-AGAS-CS\"", projectGodot);
    }
}
