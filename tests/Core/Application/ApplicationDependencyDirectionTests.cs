using AppAgasCs.Application;
using AppAgasCs.Application.Contracts;
using Xunit;

namespace AppAgasCs.Tests.Core.Application;

public sealed class ApplicationDependencyDirectionTests
{
    [Fact]
    public void Application_DoesNotReferenceCanvasOrAiWorkspaceFeatureNamespaces()
    {
        string applicationRoot = Path.Combine(ProjectRoot.Path, "src/Application");
        string source = string.Join('\n', Directory.GetFiles(applicationRoot, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText));

        Assert.DoesNotContain("Agas.Features.Canvas", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AppAgasCs.Features.Ai.Workspace", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NeutralPatchAndAiPreparationContracts_AreApplicationOwned()
    {
        Assert.Equal("AppAgasCs.Application.Contracts", typeof(CanvasGridRegion).Namespace);
        Assert.Equal("AppAgasCs.Application.Contracts", typeof(CanvasPatchCapture).Namespace);
        Assert.Equal("AppAgasCs.Application.Contracts", typeof(AiGenerationPreparationInput).Namespace);
        Assert.Equal("AppAgasCs.Application.Contracts", typeof(IAiGenerationRequestPreparationService).Namespace);
        Assert.True(typeof(IAiGenerationRequestPreparationService)
            .IsAssignableFrom(typeof(AiGenerationRequestPreparationService)));

        string workspaceContracts = File.ReadAllText(Path.Combine(
            ProjectRoot.Path,
            "src/Features/Ai/Workspace/AgasAiGenerationWorkspaceContracts.cs"));
        Assert.Contains("using AppAgasCs.Application.Contracts;", workspaceContracts, StringComparison.Ordinal);
        Assert.DoesNotContain("Agas.Features.Canvas", workspaceContracts, StringComparison.Ordinal);
    }

    [Fact]
    public void CanvasLayerFlags_UseApplicationMutationAndHaveNoLegacyCommand()
    {
        string canvas = File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/Features/Canvas/CanvasWorkspace.cs"));
        string documentCommands = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/Features/Canvas/CanvasDocumentCommands.cs"));
        string legacyCommand = Path.Combine(
            ProjectRoot.Path,
            "src/Features/Canvas/Commands/CanvasLayerCommands.cs");

        Assert.Contains("LayerMutationKind.SetVisible", documentCommands, StringComparison.Ordinal);
        Assert.Contains("LayerMutationKind.SetLocked", documentCommands, StringComparison.Ordinal);
        Assert.Contains("LayerMutationKind.SetShared", documentCommands, StringComparison.Ordinal);
        Assert.Contains("ProjectMutationResult MutateLayer", canvas, StringComparison.Ordinal);
        Assert.Contains("_document.SetLayerVisible", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("SetLayerFlagCommand", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("SetLayerFlagCommand", documentCommands, StringComparison.Ordinal);
        Assert.False(File.Exists(legacyCommand));
    }
}
