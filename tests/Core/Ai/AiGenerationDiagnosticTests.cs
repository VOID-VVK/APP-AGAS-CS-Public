using System.Text.Json;
using AppAgasCs.Features.Ai.Generation;
using Xunit;

namespace AppAgasCs.Tests.Core.Ai;

public sealed class AiGenerationDiagnosticTests
{
    [Fact]
    public async Task DeferredJsonSerializationFailure_PreservesJsonStageWithoutSensitiveText()
    {
        using var home = new TemporaryHome();
        const string secret = "DEFERRED_JSON_KEY_SECRET";
        const string sensitiveMessage =
            "Authorization: Bearer DEFERRED_JSON_KEY_SECRET https://relay.example/private DEFERRED_JSON_PROMPT_SECRET";
        var profile = new AiGenerationProviderProfile(
            "nano-safe",
            "Nano",
            "gemini-compatible",
            "gemini-3-pro-image-preview",
            secret,
            "https://relay.example/v1beta");
        using var content = new GeminiCompatibleImageAdapter.StagedJsonContent(
            new ThrowingJsonPayload(sensitiveMessage));

        AiGenerationAdapterStageException failure = await Assert.ThrowsAsync<AiGenerationAdapterStageException>(
            () => content.ReadAsByteArrayAsync());
        AiGenerationSafeDiagnostic diagnostic = AiGenerationDiagnostics.Create(
            AiGenerationErrorCode.ProviderRequestFailed,
            AiGenerationFailurePhase.ProviderRequest,
            AiGenerationDiagnostics.CategoryFor(failure),
            profile,
            exception: failure);
        var sink = new AiGenerationFileDiagnosticSink(home.Path);
        sink.TryWrite(diagnostic);

        Assert.Equal(AiGenerationAdapterStage.JsonSerialization, diagnostic.AdapterStage);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.ExceptionType));
        string log = File.ReadAllText(sink.LogPath);
        Assert.Contains("\"adapterStage\":\"JsonSerialization\"", log, StringComparison.Ordinal);
        Assert.Contains("\"exceptionType\":", log, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        Assert.DoesNotContain("DEFERRED_JSON_PROMPT_SECRET", log, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveMessage, log, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FileSink_WritesSafeJsonlWithRestrictedModeAndRotates()
    {
        using var home = new TemporaryHome();
        const string apiKey = "DIAG_KEY_SECRET";
        const string prompt = "DIAG_PROMPT_SECRET";
        const string body = "DIAG_BODY_SECRET";
        const string base64 = "DIAG_BASE64_SECRET";
        const string requestId = "DIAG_REQUEST_ID_SECRET";
        const string innerException = "DIAG_INNER_SECRET";
        const string fullUrl = "https://relay.example/v1/private";
        var profile = new AiGenerationProviderProfile(
            "profile-safe",
            "Relay",
            "openai-compatible",
            "gpt-image-2",
            apiKey,
            "https://relay.example/v1");
        var sourceException = new InvalidOperationException(body, new Exception(innerException));
        AiGenerationSafeDiagnostic diagnostic = AiGenerationDiagnostics.Create(
            AiGenerationErrorCode.ProviderRequestFailed,
            AiGenerationFailurePhase.ProviderResponse,
            AiGenerationDiagnostics.CategoryFor(sourceException),
            profile,
            httpStatus: 401,
            upstreamCode: "invalid_api_key",
            exception: sourceException,
            adapterStage: AiGenerationAdapterStage.ResponseRead);
        var sink = new AiGenerationFileDiagnosticSink(home.Path, maximumBytes: 256, backupCount: 2);

        sink.TryWrite(diagnostic);
        sink.TryWrite(diagnostic with { DiagnosticId = Guid.NewGuid().ToString("N") });

        Assert.True(File.Exists(sink.LogPath));
        Assert.True(File.Exists(sink.LogPath + ".1"));
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode expected = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            Assert.Equal(expected, File.GetUnixFileMode(sink.LogPath));
            Assert.Equal(expected, File.GetUnixFileMode(sink.LogPath + ".1"));
        }

        string jsonl = File.ReadAllText(sink.LogPath);
        string line = Assert.Single(jsonl.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("diagnosticId").GetString()));
        Assert.Equal("ProviderRequestFailed", root.GetProperty("errorCode").GetString());
        Assert.Equal("ProviderResponse", root.GetProperty("phase").GetString());
        Assert.Equal("InvalidOperation", root.GetProperty("category").GetString());
        Assert.Equal(401, root.GetProperty("httpStatus").GetInt32());
        Assert.Equal("invalid_api_key", root.GetProperty("upstreamCode").GetString());
        Assert.Equal("relay.example", root.GetProperty("endpointHost").GetString());
        Assert.Equal("System.InvalidOperationException", root.GetProperty("exceptionType").GetString());
        Assert.Equal("System.Exception", root.GetProperty("innerExceptionType").GetString());
        Assert.Equal("ResponseRead", root.GetProperty("adapterStage").GetString());

        string allLogs = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(sink.LogDirectory, "ai-generation.log*").Select(File.ReadAllText));
        foreach (string forbidden in new[]
                 {
                     apiKey,
                     prompt,
                     body,
                     base64,
                     requestId,
                     innerException,
                     fullUrl,
                     "apiKey",
                     "prompt",
                     "body",
                     "base64",
                     "requestId",
                     "request-id",
                     "Authorization: Bearer",
                     "http://",
                     "https://",
                 })
        {
            Assert.DoesNotContain(forbidden, allLogs, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void FileSink_RequestGeometryContainsOnlyAllowListedMetadata()
    {
        using var home = new TemporaryHome();
        var sink = new AiGenerationFileDiagnosticSink(home.Path);
        var diagnostic = new AiGenerationSafeRequestDiagnostic(
            "requestgeometry0123456789",
            "RegionEdit",
            "NanoBanana",
            "relay.example",
            2448,
            1440,
            2448,
            1440,
            1152,
            480,
            48,
            48,
            2448,
            1440,
            2448,
            1440,
            1);

        sink.TryWriteRequest(diagnostic);

        string line = Assert.Single(File.ReadAllLines(sink.LogPath));
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        Assert.Equal("providerRequest", root.GetProperty("eventType").GetString());
        Assert.Equal("RegionEdit", root.GetProperty("operation").GetString());
        Assert.Equal(2448, root.GetProperty("requestPixelWidth").GetInt32());
        Assert.Equal(1440, root.GetProperty("requestPixelHeight").GetInt32());
        Assert.Equal(2448, root.GetProperty("qPixelWidth").GetInt32());
        Assert.Equal(1440, root.GetProperty("qPixelHeight").GetInt32());
        Assert.Equal(1152, root.GetProperty("wRelativeX").GetInt32());
        Assert.Equal(480, root.GetProperty("wRelativeY").GetInt32());
        Assert.Equal(48, root.GetProperty("wPixelWidth").GetInt32());
        Assert.Equal(48, root.GetProperty("wPixelHeight").GetInt32());
        Assert.Equal(2448, root.GetProperty("sourcePngIhdrWidth").GetInt32());
        Assert.Equal(1440, root.GetProperty("sourcePngIhdrHeight").GetInt32());
        Assert.Equal(1, root.GetProperty("referenceImageCount").GetInt32());

        foreach (string forbidden in new[]
                 {
                     "request prompt secret",
                     "api-key-secret",
                     "data:image/png;base64",
                     "iVBORw0KGgo",
                     "/private/project/path",
                     "https://relay.example/v1/private?key=secret",
                     "prompt",
                     "base64",
                     "apiKey",
                     "projectRoot",
                     "sceneId",
                     "layerId",
                 })
        {
            Assert.DoesNotContain(forbidden, line, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void UiText_ContainsStableSafeFieldsOnly()
    {
        var diagnostic = new AiGenerationSafeDiagnostic(
            "diag0123456789",
            AiGenerationErrorCode.ProviderRequestFailed,
            AiGenerationFailurePhase.ProviderResponse,
            AiGenerationFailureCategory.ProviderRejected,
            "profile",
            "GptImage",
            "gpt-image-2",
            "relay.example",
            401,
            "invalid_api_key");

        string text = AiGenerationDiagnostics.FormatForUi(diagnostic);

        Assert.Contains("阶段：读取服务响应", text, StringComparison.Ordinal);
        Assert.Contains("类别：服务拒绝请求", text, StringComparison.Ordinal);
        Assert.Contains("错误码：ProviderRequestFailed", text, StringComparison.Ordinal);
        Assert.Contains("HTTP：401", text, StringComparison.Ordinal);
        Assert.Contains("上游代码：invalid_api_key", text, StringComparison.Ordinal);
        Assert.Contains("主机：relay.example", text, StringComparison.Ordinal);
        Assert.Contains("诊断ID：diag0123456789", text, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", text, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TemporaryHome : IDisposable
    {
        public TemporaryHome()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"agas-ai-diagnostic-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Cleanup must not replace the assertion result.
            }
        }
    }

    private sealed class ThrowingJsonPayload
    {
        private readonly string _message;

        public ThrowingJsonPayload(string message)
        {
            _message = message;
        }

        public string Value => throw new SensitiveJsonException(_message);
    }

    private sealed class SensitiveJsonException : Exception
    {
        public SensitiveJsonException(string message)
            : base(message)
        {
        }
    }
}
