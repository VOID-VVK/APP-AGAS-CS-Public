using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AGAS.UI;
using AppAgasCs.Application.Contracts;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Features.Ai.UI;
using Godot;

public partial class GrokImageQualityAcceptance : Node
{
    private const string Prefix = "[AGAS][GROK-IMAGE-QUALITY]";
    private string? _home;
    private string? _projectRoot;
    private string? _previousHome;

    public override async void _Ready()
    {
        int exitCode = 0;
        try
        {
            await VerifyAsync();
            GD.Print($"{Prefix}[PASS]");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {exception}");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, _previousHome);
            DeleteDirectory(_home);
            DeleteDirectory(_projectRoot);
            GetTree().Quit(exitCode);
        }
    }

    private async Task VerifyAsync()
    {
        _home = Path.Combine(Path.GetTempPath(), "agas-grok-quality-home-" + Guid.NewGuid().ToString("N"));
        _projectRoot = Path.Combine(Path.GetTempPath(), "agas-grok-quality-project-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_projectRoot);
        _previousHome = System.Environment.GetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable);
        System.Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, _home);

        var connection = new AgasAiProfile(
            "quality-connection",
            "Quality relay",
            "legacy-provider",
            "legacy-model",
            "https://relay.example/v1/");
        var store = new AgasAiSettingsStore(_home);
        store.SaveProfiles(new AgasAiProfilesDocument(
            AgasAiProfilesDocument.CurrentResourceVersion,
            connection.Id,
            "gpt-image-2",
            new[] { connection }));
        store.SetCredential(connection.Id, "synthetic-quality-key");

        var workspace = new MainWorkspace { CustomMinimumSize = new Vector2(1200, 760) };
        AddChild(workspace);
        await Frames(2);
        workspace.OpenProject(_projectRoot);
        await Frames(4);

        OptionButton models = Required<OptionButton>(workspace, "AiGenerationProfileSelector");
        OptionButton quality = Required<OptionButton>(workspace, "AiGenerationQualitySelector");
        AiGenerationPanel panel = Required<AiGenerationPanel>(workspace, "AiGenerationPanel");
        Require(models.ItemCount == 7, "The top selector must expose all seven unified models.");
        Require(ReferenceEquals(models.GetParent(), quality.GetParent())
                && quality.GetParent()?.GetParent()?.Name == "WorkspaceToolbar",
            "The compact quality selector must sit beside the model selector in the top workspace toolbar.");
        Require(!quality.Visible && quality.Disabled, "Quality must be hidden for GPT Image 2.");
        Require(quality.GetSelectedId() == (int)AiGenerationQuality.Medium
                && panel.SelectedQuality == AiGenerationQuality.Medium,
            "A fresh workspace must default Grok Image 2.0 quality to medium.");

        SelectModel(models, "grok-imagine-image-2.0");
        Require(quality.Visible && !quality.Disabled, "Grok Image 2.0 must reveal the quality selector.");
        int lowIndex = Enumerable.Range(0, quality.ItemCount).Single(index =>
            quality.GetItemId(index) == (int)AiGenerationQuality.Low);
        quality.Select(lowIndex);
        quality.EmitSignal(OptionButton.SignalName.ItemSelected, (long)lowIndex);
        Require(panel.SelectedQuality == AiGenerationQuality.Low, "The low option must update panel state.");

        AiGenerationPreparationInput? captured = null;
        panel.PrepareGenerationRequest = input =>
        {
            captured = input;
            return AiGenerationPreparationResult.Fail("Captured without HTTP.");
        };
        AiGenerationCanvasContext context = Context(workspace);
        panel.BeginCanvasGeneration(context, "quality low", submit: true, target: AiGenerationTarget.AddToCurrentLayer);
        int mediumIndex = Enumerable.Range(0, quality.ItemCount).Single(index =>
            quality.GetItemId(index) == (int)AiGenerationQuality.Medium);
        quality.Select(mediumIndex);
        quality.EmitSignal(OptionButton.SignalName.ItemSelected, (long)mediumIndex);
        await Frames(2);
        Require(captured?.Profile.ModelName == "grok-imagine-image-2.0"
                && captured.Quality == AiGenerationQuality.Low,
            "A Grok Image 2.0 request must freeze low before a next-frame quality change.");

        SelectModel(models, "gpt-image-2.5-flare");
        Require(!quality.Visible && quality.Disabled, "Switching away from Grok Image 2.0 must hide quality.");
        captured = null;
        panel.BeginCanvasGeneration(context, "quality isolation", submit: true, target: AiGenerationTarget.AddToCurrentLayer);
        await Frames(2);
        Require(captured?.Profile.ModelName == "gpt-image-2.5-flare" && captured.Quality == null,
            "A previous low selection must not leak quality into another model request.");

        SelectModel(models, "grok-imagine-image-2.0");
        quality.Select(lowIndex);
        quality.EmitSignal(OptionButton.SignalName.ItemSelected, (long)lowIndex);
        workspace.QueueFree();
        await Frames(2);

        var reopened = new MainWorkspace { CustomMinimumSize = new Vector2(1200, 760) };
        AddChild(reopened);
        await Frames(2);
        reopened.OpenProject(_projectRoot);
        await Frames(4);
        OptionButton reopenedQuality = Required<OptionButton>(reopened, "AiGenerationQualitySelector");
        AiGenerationPanel reopenedPanel = Required<AiGenerationPanel>(reopened, "AiGenerationPanel");
        Require(reopenedQuality.Visible
                && reopenedQuality.GetSelectedId() == (int)AiGenerationQuality.Medium
                && reopenedPanel.SelectedQuality == AiGenerationQuality.Medium,
            "Quality must not persist; a recreated workspace defaults Grok Image 2.0 to medium.");
        reopened.QueueFree();
        await Frames(2);
    }

    private static AiGenerationCanvasContext Context(MainWorkspace workspace) => new(
        workspace.ProjectRoot,
        workspace.CurrentProjectId,
        workspace.CurrentScene!.Id,
        workspace.CurrentScene.ActiveLayerId,
        HasSelectedImage: false,
        SelectedImageId: null,
        CanEditCurrentLayer: true,
        GridSizePx: 48,
        RegionMetadata: new AiGenerationRegionMetadata(new AiGenerationGridRegion(0, 0, 1, 1), null));

    private static void SelectModel(OptionButton models, string model)
    {
        int index = Enumerable.Range(0, models.ItemCount).Single(item =>
            models.GetItemMetadata(item).AsString() == model);
        models.Select(index);
        models.EmitSignal(OptionButton.SignalName.ItemSelected, (long)index);
    }

    private async Task Frames(int count)
    {
        for (int index = 0; index < count; index++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static T Required<T>(Node root, string name) where T : Node =>
        root.FindChild(name, recursive: true, owned: false) as T
        ?? throw new InvalidOperationException($"Missing control: {name}");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void DeleteDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
