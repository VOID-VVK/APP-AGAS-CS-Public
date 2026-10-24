using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Features.Ai.UI;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class DynamicImageModelsAcceptance : Node
{
    public override async void _Ready()
    {
        string home = Path.Combine(Path.GetTempPath(), "agas-dynamic-models", Guid.NewGuid().ToString("N"));
        int code = 0;
        try
        {
            var store = new AgasAiSettingsStore(home);
            var profile = new AgasAiProfile("connection", "Mock relay", AgasAiProviderIds.AxonCatNewApi,
                "ignored", "https://relay.example/prefix/v1", "key-a");
            store.SaveProfiles(new AgasAiProfilesDocument(AgasAiProfilesDocument.CurrentResourceVersion,
                profile.Id, "gpt-image-2", new[] { profile }));
            using var models = new ImageModelsAcceptanceFixture
            {
                Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            AiGenerationPanel panel = models.Panel(store);
            AddChild(panel);
            Require(panel.ProfileOptions.Count == 0 && !panel.ResolveProviderProfile().Success,
                "Pending discovery must not expose static or cached model permissions.");
            for (int frame = 0; frame < 3; frame++) await Frame();
            Require(!models.Gate.Task.IsCompleted, "Real Godot frames must progress while model HTTP remains pending.");
            models.Gate.SetResult(true);
            await Until(() => panel.ProfileOptions.Count == 20);
            var category = Required<OptionButton>(panel, "AiGenerationProvider");
            var selector = Required<OptionButton>(panel, "AiGenerationModelPreset");
            await Until(() => category.ItemCount == 4 && selector.ItemCount == 3);
            Require(category.ItemCount == 4 && selector.ItemCount == 3, "Categories must be separate from protocol routes.");
            Require(panel.SelectModelByName("jimeng-image-5.0-lite"), "Jimeng must be discoverable with the same key.");
            Require(category.GetItemMetadata(category.Selected).AsString() == "jimeng" && selector.ItemCount == 12,
                "Jimeng selection must show its twelve-model category.");
            Require(panel.SelectModelByName("future-partner-image") && panel.ResolveProviderProfile().Success,
                "A future metadata-declared model must resolve without editing static presets.");

            models.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            store.SetCredential(profile.Id, "key-b");
            panel.ReloadProfiles();
            Require(panel.ProfileOptions.Count == 0 && !panel.ResolveProviderProfile().Success,
                "Rotating a key must immediately discard the previous key's model permissions.");
            models.Status = HttpStatusCode.Forbidden;
            models.Gate.SetResult(true);
            await Until(() => models.Discovery.GetSnapshot(profile with { ApiKey = "key-b" }).Source == "auth");
            await Frame();
            Require(panel.ProfileOptions.Count == 0 && selector.Disabled && !panel.SelectModelByName("gpt-image-2"),
                "403 must disable selection and never reveal the dated fallback.");
            models.Gate = null;
            models.Status = HttpStatusCode.BadGateway;
            panel.ReloadProfiles();
            await Frame();
            Require(panel.ProfileOptions.Count == 0 && !panel.ResolveProviderProfile().Success,
                "A later network failure must not revive permissions after 403.");
            GD.Print("[AGAS][DYNAMIC-IMAGE-MODELS][PASS] real frames, categories, future model, key rotation, authoritative denial");
            panel.QueueFree();
            await Frame();
        }
        catch (Exception exception)
        {
            code = 1;
            GD.PushError($"[AGAS][DYNAMIC-IMAGE-MODELS][FAIL] {exception}");
        }
        finally
        {
            try { Directory.Delete(home, true); } catch (DirectoryNotFoundException) { }
        }
        GetTree().Quit(code);
    }

    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    private async Task Until(Func<bool> condition)
    {
        for (int frame = 0; frame < 300; frame++)
        {
            if (condition()) return;
            await Frame();
        }
        throw new InvalidOperationException("Model discovery did not settle within 300 Godot frames.");
    }
    private static T Required<T>(Node root, string name) where T : Node =>
        root.FindChild(name, true, false) as T ?? throw new InvalidOperationException($"Missing control: {name}");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
