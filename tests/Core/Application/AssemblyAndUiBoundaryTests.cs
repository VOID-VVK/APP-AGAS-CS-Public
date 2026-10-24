using System.Xml.Linq;
using Xunit;

namespace AppAgasCs.Tests.Core.Application;

public sealed class AssemblyAndUiBoundaryTests
{
    [Fact]
    public void ProductionProject_ExcludesAllTestSources()
    {
        XDocument project = XDocument.Load(Path.Combine(ProjectRoot.Path, "APP-AGAS-CS.csproj"));
        XElement[] compileItems = project.Descendants("Compile").ToArray();

        Assert.Contains(compileItems, item => (string?)item.Attribute("Remove") == "tests/**/*.cs");
        Assert.DoesNotContain(compileItems, item =>
            ((string?)item.Attribute("Include"))?.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) == true);
        Assert.True(File.Exists(Path.Combine(ProjectRoot.Path, "tests/Godot/APP-AGAS-CS.Godot.Acceptance.csproj")));
    }

    [Fact]
    public void GodotAcceptanceProject_UsesAnIsolatedAssemblyAndOutputDirectory()
    {
        XDocument production = XDocument.Load(Path.Combine(ProjectRoot.Path, "APP-AGAS-CS.csproj"));
        XDocument acceptance = XDocument.Load(Path.Combine(
            ProjectRoot.Path,
            "tests/Godot/APP-AGAS-CS.Godot.Acceptance.csproj"));

        string productionAssembly = production.Descendants("AssemblyName").Single().Value;
        string acceptanceAssembly = acceptance.Descendants("AssemblyName").Single().Value;
        string acceptanceOutput = acceptance.Descendants("OutputPath").Single().Value;

        Assert.Equal("APP-AGAS-CS", productionAssembly);
        Assert.Equal("APP-AGAS-CS.Godot.Acceptance", acceptanceAssembly);
        Assert.NotEqual(productionAssembly, acceptanceAssembly);
        Assert.Contains("$(BaseOutputPath)$(Configuration)", acceptanceOutput, StringComparison.Ordinal);
        Assert.Contains(
            acceptance.Descendants("BaseOutputPath"),
            property => property.Value.Contains("$(MSBuildThisFileDirectory)bin/", StringComparison.Ordinal));
        Assert.Contains(
            acceptance.Descendants("Target"),
            target => (string?)target.Attribute("Name") == "ValidateAcceptanceBuildIsolation");

        string runnerPath = Path.Combine(ProjectRoot.Path, "tests/Godot/run-acceptance.sh");
        Assert.True(File.Exists(runnerPath));
        string runner = File.ReadAllText(runnerPath);
        Assert.Contains("mktemp -d", runner, StringComparison.Ordinal);
        Assert.Contains("project/assembly_name=\"APP-AGAS-CS.Godot.Acceptance\"", runner, StringComparison.Ordinal);
        Assert.Contains("$snapshot/.godot/mono/temp/bin/Debug", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void AiGenerationRegistry_DoesNotDependOnAChildAdapterNamespace()
    {
        string generationRoot = Path.Combine(ProjectRoot.Path, "src/Features/Ai/Generation");
        string registry = File.ReadAllText(Path.Combine(generationRoot, "AiImageGenerationAdapterRegistry.cs"));
        string[] builtInAdapterSources = Directory.GetFiles(
            Path.Combine(generationRoot, "Adapters"),
            "*.cs",
            SearchOption.TopDirectoryOnly);

        Assert.NotEmpty(builtInAdapterSources);
        Assert.DoesNotContain("AppAgasCs.Features.Ai.Generation.Adapters", registry, StringComparison.Ordinal);
        Assert.All(builtInAdapterSources, source => Assert.Contains(
            "namespace AppAgasCs.Features.Ai.Generation;",
            File.ReadAllText(source),
            StringComparison.Ordinal));
    }

    [Fact]
    public void FeatureAndDialogViews_DependOnSharedThemingInsteadOfRootUi()
    {
        string aiPanel = File.ReadAllText(Path.Combine(
            ProjectRoot.Path,
            "src/Features/Ai/UI/AiGenerationPanel.cs"));
        string settingsDialog = File.ReadAllText(Path.Combine(
            ProjectRoot.Path,
            "src/UI/Dialogs/SettingsDialog.cs"));

        Assert.Contains("using AppAgasCs.UI.Theming;", aiPanel, StringComparison.Ordinal);
        Assert.DoesNotContain("AGAS.UI.AgasIdeTheme", aiPanel, StringComparison.Ordinal);
        Assert.DoesNotContain("using AGAS.UI;", aiPanel, StringComparison.Ordinal);

        Assert.Contains("using AppAgasCs.UI.Theming;", settingsDialog, StringComparison.Ordinal);
        Assert.DoesNotContain("AgasIdeTheme.", settingsDialog, StringComparison.Ordinal);
        Assert.DoesNotContain("using AGAS.UI;", settingsDialog, StringComparison.Ordinal);
    }

    [Fact]
    public void AiGenerationPanel_DelegatesProviderProfileAndModelControlState()
    {
        string panel = File.ReadAllText(Path.Combine(
            ProjectRoot.Path,
            "src/Features/Ai/UI/AiGenerationPanel.cs"));
        string controller = File.ReadAllText(Path.Combine(
            ProjectRoot.Path,
            "src/Features/Ai/UI/AiGenerationProfileController.cs"));

        Assert.Contains("AiGenerationProfileController _profileController", panel, StringComparison.Ordinal);
        Assert.Contains("_profileController.ResolveProviderProfile", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("AgasAiProviderCatalog", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("OnProviderSelected", panel, StringComparison.Ordinal);
        Assert.Contains("internal sealed class AiGenerationProfileController", controller, StringComparison.Ordinal);
        Assert.Contains("AgasAiUnifiedModelCatalog.Models", controller, StringComparison.Ordinal);
        Assert.Contains("AgasAiConnectionModelResolver.Resolve", controller, StringComparison.Ordinal);
        Assert.Contains("_fieldMerge.SetActiveModel", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("OnProviderSelected", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveSwitchFrom", controller, StringComparison.Ordinal);
        Assert.Contains("internal AiGenerationProfileResolution ResolveProviderProfile", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void CanvasCommandImplementations_StayInsideCanvasNamespaceBoundary()
    {
        string commandRoot = Path.Combine(ProjectRoot.Path, "src/Features/Canvas/Commands");
        string[] sources = Directory.GetFiles(commandRoot, "*.cs", SearchOption.TopDirectoryOnly);

        Assert.NotEmpty(sources);
        Assert.All(sources, source =>
            Assert.Contains("namespace Agas.Features.Canvas;", File.ReadAllText(source), StringComparison.Ordinal));
        Assert.DoesNotContain(
            "using Agas.Features.Canvas;",
            File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/Features/Commands/CanvasEditScope.cs")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ThemeMarginOverrides_AreCentralizedInSharedFactory()
    {
        string sourceRoot = Path.Combine(ProjectRoot.Path, "src");
        string[] owners = Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("AddThemeConstantOverride(\"margin_", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(ProjectRoot.Path, path).Replace('\\', '/'))
            .ToArray();

        Assert.Equal(new[] { "src/UI/Shared/AgasMarginContainerFactory.cs" }, owners);
    }

    [Fact]
    public void LegacyBlueprintFeatureUi_IsRemoved()
    {
        string blueprintUi = Path.Combine(ProjectRoot.Path, "src/Features/Blueprint/UI");
        string[] sources = Directory.Exists(blueprintUi)
            ? Directory.GetFiles(blueprintUi, "*.cs", SearchOption.AllDirectories)
            : Array.Empty<string>();
        Assert.Empty(sources);
    }

    [Fact]
    public void Solution_MapsReleaseConfigurationsToReleaseBuilds()
    {
        string solution = File.ReadAllText(Path.Combine(ProjectRoot.Path, "APP-AGAS-CS.sln"));

        Assert.DoesNotContain(".Release|Any CPU.ActiveCfg = Debug|", solution, StringComparison.Ordinal);
        Assert.DoesNotContain(".Release|x64.ActiveCfg = Debug|", solution, StringComparison.Ordinal);
        Assert.DoesNotContain(".Release|x86.ActiveCfg = Debug|", solution, StringComparison.Ordinal);
    }
}
