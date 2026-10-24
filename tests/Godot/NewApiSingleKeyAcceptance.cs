using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AGAS.UI.Dialogs;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Features.Ai.UI;
using Godot;

namespace AppAgasCs.Tests.Godot;

/// <summary>Real Godot controls and persistence; all generation HTTP is intercepted, never billed.</summary>
public partial class NewApiSingleKeyAcceptance : Node
{
    private const string Prefix = "[AGAS][NEWAPI-SINGLE-KEY]";
    private const string MockKey = "sk-local-newapi-acceptance-not-a-real-key";
    private static readonly string[] Models =
    {
        "gpt-image-2",
        "gpt-image-2.5-flare",
        "gpt-image-2.5-sunburst",
        "grok-imagine-image-quality",
        "grok-imagine-image",
        "grok-imagine-image-2.0",
        "gemini-image",
    };

    public override async void _Ready()
    {
        GD.Print($"{Prefix}[ISOLATION] Godot user data: {OS.GetUserDataDir()}");
        int exitCode = 0;
        try
        {
            if (System.Environment.GetEnvironmentVariable("AGAS_NEWAPI_REOPEN_ONLY") == "1")
            {
                await VerifyRestartAsync();
                GD.Print($"{Prefix}[PASS] fresh-process restart retained model and credential");
            }
            else
            {
                await VerifyAsync();
                GD.Print($"{Prefix}[PASS] settings, single credential, seven models, eleven intercepted requests, three Grok edit rejections, persisted reopen");
            }
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {exception}");
        }
        GetTree().Quit(exitCode);
    }

    private async Task VerifyRestartAsync()
    {
        var store = new AgasAiSettingsStore();
        AgasAiProfilesDocument document = store.LoadProfiles();
        AgasAiProfile saved = document.Profiles.Single();
        Require(saved.ApiKey == MockKey && document.ActiveModelName == Models[^1] && saved.BaseUrl == "http://127.0.0.1:19877",
            "Fresh-process verification requires the prior isolated mock profile.");
        var panel = new AiGenerationPanel();
        AddChild(panel);
        await Frame();
        var resolution = panel.ResolveProviderProfile();
        Require(resolution.Success && resolution.Profile?.ApiKey == MockKey
            && resolution.Profile.ModelName == document.ActiveModelName && resolution.Profile.Id == saved.Id,
            "App restart must preserve the selected model, profile and credential without prompting again.");
        panel.QueueFree();
        await Frame();
    }

    private async Task VerifyAsync()
    {
        GetTree().Root.GuiEmbedSubwindows = true;
        GetTree().Root.Size = new Vector2I(1440, 1000);
        string home = AgasAiSettingsPaths.ResolveHome();
        Require(home.StartsWith(Path.GetTempPath(), StringComparison.Ordinal)
            || home.StartsWith("/private/var/", StringComparison.Ordinal), "Acceptance requires an isolated temporary AGAS_HOME.");
        var store = new AgasAiSettingsStore(home);
        Require(store.LoadProfiles().Profiles.Count == 0, "Acceptance must not inherit real profiles.");
        var dialog = new SettingsDialog();
        dialog.Open(this);
        await Frame();
        LineEdit key = Required<LineEdit>(dialog, "AiApiKey");
        LineEdit baseUrl = Required<LineEdit>(dialog, "AiBaseUrl");
        Require(baseUrl.Text.TrimEnd('/') == "https://ainewdev.cc", "New profiles must default to New API.");
        Require(key.Secret && string.IsNullOrEmpty(key.Text), "New profile must have a blank masked credential input.");
        key.Text = MockKey;
        key.EmitSignal(LineEdit.SignalName.TextChanged, key.Text);
        Required<Button>(dialog, "SaveAiProfile").EmitSignal(Button.SignalName.Pressed);
        AgasAiProfile saved = store.LoadProfiles().Profiles.Single();
        string profileId = saved.Id;
        Require(saved.ProviderId == "axoncat-newapi", "New profile must use the unified New API provider.");
        Require(store.GetCredential(profileId) == MockKey, "The one entered credential must persist.");
        Require(string.IsNullOrEmpty(key.Text), "Saved secret must not be copied into the text editor.");

        Require(dialog.FindChild("AiModelPreset", true, false) == null
            && dialog.FindChild("AiModelName", true, false) == null,
            "Settings must configure connections only; model switching belongs to the generation selector.");

        if (DisplayServer.GetName() != "headless")
        {
            // Container/AcceptDialog resize notifications cascade across several deferred layout passes.
            for (int frame = 0; frame < 10; frame++) await Frame();
            Rect2 saveRect = Required<Button>(dialog, "SaveAiProfile").GetGlobalRect();
            GD.Print($"{Prefix}[LAYOUT] root={GetTree().Root.Size} dialog={dialog.Size} position={dialog.Position} save={saveRect}");
            foreach (string name in new[] { "SettingsMargin", "SettingsSplit", "SettingsDetail", "AiSettingsPage", "AiProfileDetailsScroll", "AiProfileDetails", "AiSettingsStatus" })
            {
                Control control = Required<Control>(dialog, name);
                GD.Print($"{Prefix}[LAYOUT] {name} size={control.Size} min={control.GetCombinedMinimumSize()}");
            }
            ScrollContainer scroll = Required<ScrollContainer>(dialog, "AiProfileDetailsScroll");
            GD.Print($"{Prefix}[LAYOUT] scroll={scroll.VerticalScrollMode}/{scroll.HorizontalScrollMode}");
            Require(dialog.Position.Y >= 0 && dialog.Position.Y + dialog.Size.Y <= GetTree().Root.Size.Y,
                "Settings must fit inside the available window height.");
            Require(saveRect.End.Y + dialog.Position.Y <= GetTree().Root.Size.Y,
                "The Save button must remain visible inside the available window.");
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            string evidence = System.Environment.GetEnvironmentVariable("AGAS_NEWAPI_SCREENSHOT")
                ?? Path.Combine(Path.GetTempPath(), "agas-newapi-settings.png");
            Require(GetViewport().GetTexture().GetImage().SavePng(evidence) == Error.Ok, "Settings screenshot must save.");
            GD.Print($"{Prefix}[SCREENSHOT] {evidence}");
        }
        dialog.Hide();
        dialog.QueueFree();
        await Frame();

        // Loopback is test-only. The HTTP handler below rejects every unrecognized request and never opens a socket.
        saved = saved with { BaseUrl = "http://127.0.0.1:19877" };
        store.SaveProfiles(new AgasAiProfilesDocument(
            AgasAiProfilesDocument.CurrentResourceVersion,
            profileId,
            store.LoadProfiles().ActiveModelName,
            new[] { saved }));
        var panel = new AiGenerationPanel();
        AddChild(panel);
        await Frame();
        using Image fixture = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8);
        fixture.Fill(Colors.CornflowerBlue);
        byte[] png = fixture.SavePngToBuffer();
        using var transport = new MockTransport(png);
        using var client = new System.Net.Http.HttpClient(transport);
        var service = new AiGenerationService(client, imageDownloadClient: client, timeout: TimeSpan.FromSeconds(10));
        string projectRoot = Path.Combine(home, "mock-generated-project");
        Directory.CreateDirectory(projectRoot);

        foreach (string model in Models)
        {
            SelectModel(Required<OptionButton>(panel, "AiGenerationModelPreset"), model);
            AgasAiProfilesDocument switched = store.LoadProfiles();
            Require(switched.ActiveModelName == model && switched.ActiveProfileId == profileId,
                "Model switching must update only the global model selection.");
            Require(switched.Profiles.Single().BaseUrl == saved.BaseUrl && store.GetCredential(profileId) == MockKey,
                "Model switching must preserve the connection URL and key.");
            var resolution = panel.ResolveProviderProfile();
            Require(resolution.Success && resolution.Profile != null, "Generation panel must resolve the selected profile.");
            AiGenerationProviderProfile profile = resolution.Profile!;
            Require(profile.Id == profileId && profile.ApiKey == MockKey && profile.ModelName == model,
                "Runtime selection must retain one profile/key across all models.");
            Require(profile.AdapterKind == "newapi-compatible", "Runtime selection must retain the unified adapter.");
            bool isGrok = model.StartsWith("grok-", StringComparison.Ordinal);
            bool supportsReferenceEdit = profile.DeclaredCapabilities?.HasFlag(
                AiGenerationAdapterCapabilities.ReferenceEdit) == true;
            Require(supportsReferenceEdit == !isGrok,
                "Only GPT and Gemini models must declare reference editing support.");
            foreach (bool reference in new[] { false, true })
            {
                var request = new AiGenerationRequest(projectRoot, "生成一只奶牛猫", referenceImages:
                    reference ? new[] { AiGenerationReferenceImage.Create(png, "image/png") } : null);
                if (reference && isGrok)
                {
                    int requestCountBeforeRejection = transport.RequestCount;
                    try
                    {
                        await service.GenerateAsync(profile, request);
                        throw new InvalidOperationException("Grok reference editing must be rejected.");
                    }
                    catch (AiGenerationException exception)
                    {
                        Require(exception.Code == AiGenerationErrorCode.UnsupportedOperation,
                            "Grok reference editing must fail with UnsupportedOperation.");
                    }
                    Require(transport.RequestCount == requestCountBeforeRejection,
                        "Grok reference editing must be rejected before any HTTP request.");
                    continue;
                }

                transport.Expect(model, reference);
                AiGenerationResult result = await service.GenerateAsync(profile, request);
                Require(File.Exists(Path.Combine(projectRoot, result.RelativePath)), "Mock generation must save a real decoded PNG.");
                Require(result.SizeBytes == png.Length, "Stored image must match mock response bytes.");
                Require(result.EffectiveOperation == (reference ? AiGenerationEffectiveOperation.ReferenceEdit : AiGenerationEffectiveOperation.TextToImage),
                    "Operation semantics must survive adapter routing.");
            }
        }
        Require(transport.RequestCount == 11, "Exactly eleven supported generation requests must be intercepted.");
        panel.QueueFree();
        await Frame();
        var reopened = new AiGenerationPanel();
        AddChild(reopened);
        await Frame();
        var reopenedProfile = reopened.ResolveProviderProfile();
        Require(reopenedProfile.Success && reopenedProfile.Profile?.ModelName == Models[^1]
            && reopenedProfile.Profile.ApiKey == MockKey && reopenedProfile.Profile.Id == profileId,
            "Fresh panel and store must reload the saved model and same credential.");
        reopened.QueueFree();
        await Frame();
    }

    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private static void SelectModel(OptionButton options, string model)
    {
        for (int index = 0; index < options.ItemCount; index++)
        {
            if (options.GetItemMetadata(index).AsString() != model) continue;
            options.Select(index);
            options.EmitSignal(OptionButton.SignalName.ItemSelected, (long)index);
            return;
        }
        throw new InvalidOperationException($"Missing New API model preset: {model}");
    }

    private static T Required<T>(Node root, string name) where T : Node =>
        root.FindChild(name, true, false) as T ?? throw new InvalidOperationException($"Missing control: {name}");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class MockTransport(byte[] png) : HttpMessageHandler
    {
        private string _model = string.Empty;
        private bool _reference;
        public int RequestCount { get; private set; }

        public void Expect(string model, bool reference) => (_model, _reference) = (model, reference);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Require(request.Method == HttpMethod.Post && request.RequestUri?.Host == "127.0.0.1", "Only intercepted loopback generation is permitted.");
            Require(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == MockKey,
                "All models must send the same Bearer key.");
            Require(request.Content != null, "Generation needs a payload.");
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            string path = request.RequestUri!.AbsolutePath;
            GD.Print($"{Prefix}[MOCK] {_model} reference={_reference} {path} {request.Content.Headers.ContentType?.MediaType}");
            bool gemini = _model == "gemini-image";
            Require(path == (gemini ? "/v1/chat/completions" : _reference ? "/v1/images/edits" : "/v1/images/generations"),
                "Model must route to the New API standard endpoint, never responses or antigravity.");
            if (!gemini && _reference)
            {
                Require(request.Content.Headers.ContentType?.MediaType == "multipart/form-data", "GPT reference edit requires multipart upload.");
                Require(body.Contains(_model, StringComparison.Ordinal) && body.Contains("image/png", StringComparison.Ordinal),
                    "Multipart must include the selected model and PNG upload.");
            }
            else
            {
                using JsonDocument payload = JsonDocument.Parse(body);
                Require(payload.RootElement.GetProperty("model").GetString() == _model, "JSON must send the selected model ID unchanged.");
                if (_model == "grok-imagine-image-2.0")
                {
                    Require(payload.RootElement.GetProperty("quality").GetString() == "medium",
                        "Grok Imagine Image 2.0 must default to medium quality.");
                }
                if (gemini)
                {
                    Require(payload.RootElement.TryGetProperty("messages", out _), "Gemini requires chat messages.");
                    Require(body.Contains("data:image/png;base64,", StringComparison.Ordinal) == _reference,
                        "Gemini must include reference image only for reference editing.");
                }
            }
            string encoded = Convert.ToBase64String(png);
            object response = gemini
                ? new { choices = new[] { new { message = new { content = $"![image](data:image/png;base64,{encoded})" } } } }
                : (object)new { data = new[] { new { b64_json = encoded } } };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json"),
            };
        }
    }
}
