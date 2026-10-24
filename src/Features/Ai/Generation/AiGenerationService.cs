using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AppAgasCs.Features.Ai.Generation;

public sealed class AiGenerationService
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(8);

    private readonly HttpClient _httpClient;
    private readonly HttpClient _imageDownloadClient;
    private readonly AiImageGenerationAdapterRegistry _registry;
    private readonly AiGeneratedImageStore _imageStore;
    private readonly TimeSpan _timeout;
    private readonly IAiGenerationDiagnosticSink _diagnosticSink;

    public AiGenerationService(
        HttpClient httpClient,
        AiImageGenerationAdapterRegistry? registry = null,
        TimeSpan? timeout = null,
        AiGeneratedImageStore? imageStore = null,
        HttpClient? imageDownloadClient = null,
        IAiGenerationDiagnosticSink? diagnosticSink = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _registry = registry ?? AiImageGenerationAdapterRegistry.CreateDefault();
        _imageDownloadClient = imageDownloadClient ?? AiSecureImageDownloadClient.Instance;
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero && _timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _imageStore = imageStore ?? new AiGeneratedImageStore();
        _diagnosticSink = diagnosticSink ?? AiGenerationDiagnostics.DefaultSink;
    }

    public async Task<AiGenerationResult> GenerateAsync(
        AiGenerationProviderProfile profile,
        AiGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Quality.HasValue && !AiGenerationQualityPolicy.Supports(profile.ModelName))
        {
            throw new AiGenerationException(
                AiGenerationErrorCode.UnsupportedOperation,
                "The selected image model does not support a quality tier.");
        }
        if (AiGenerationQualityPolicy.Supports(profile.ModelName)
            && request.Operation != AiGenerationOperation.TextToImage)
        {
            throw new AiGenerationException(
                AiGenerationErrorCode.UnsupportedOperation,
                "Grok Imagine Image 2.0 supports text-to-image generation only.");
        }

        IAiImageGenerationAdapter adapter = _registry.Resolve(profile.AdapterKind);
        AiGenerationAdapterCapabilities requiredCapability = request.Operation switch
        {
            AiGenerationOperation.TextToImage => AiGenerationAdapterCapabilities.TextToImage,
            AiGenerationOperation.ReferenceEdit => AiGenerationAdapterCapabilities.ReferenceEdit,
            AiGenerationOperation.RegionEdit => AiGenerationAdapterCapabilities.RegionEdit,
            _ => AiGenerationAdapterCapabilities.None
        };
        if (requiredCapability == AiGenerationAdapterCapabilities.None
            || (adapter.Capabilities & requiredCapability) == 0
            || (profile.DeclaredCapabilities.HasValue
                && (profile.DeclaredCapabilities.Value & requiredCapability) == 0))
        {
            throw new AiGenerationException(
                AiGenerationErrorCode.UnsupportedOperation,
                "The selected image provider does not support the requested operation.");
        }
        TryWriteRequestDiagnostic(profile, request);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_timeout != Timeout.InfiniteTimeSpan)
        {
            timeoutSource.CancelAfter(_timeout);
        }

        AiGenerationFailurePhase phase = AiGenerationFailurePhase.ProviderRequest;
        try
        {
            GeneratedImageData image = await adapter.GenerateAsync(
                profile,
                request,
                _httpClient,
                _imageDownloadClient,
                timeoutSource.Token).ConfigureAwait(false);
            phase = AiGenerationFailurePhase.SaveResult;
            return await _imageStore.SaveAsync(
                request.ProjectRoot,
                request.StorageScope,
                image,
                timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            bool canceled = cancellationToken.IsCancellationRequested;
            AiGenerationErrorCode code = canceled ? AiGenerationErrorCode.RequestCanceled : AiGenerationErrorCode.RequestTimedOut;
            AiGenerationSafeDiagnostic diagnostic = AiGenerationDiagnostics.Create(
                code,
                phase,
                canceled ? AiGenerationFailureCategory.Canceled : AiGenerationFailureCategory.Timeout,
                profile);
            _diagnosticSink.TryWrite(diagnostic);
            throw new AiGenerationException(
                code,
                canceled ? "The image generation request was canceled." : "The image generation request timed out.",
                diagnostic);
        }
        catch (AiGenerationException exception)
        {
            AiGenerationSafeDiagnostic diagnostic = AiGenerationDiagnostics.Enrich(
                exception.Diagnostic,
                profile,
                exception.Code,
                phase,
                AiGenerationDiagnostics.CategoryFor(exception.Code));
            _diagnosticSink.TryWrite(diagnostic);
            throw new AiGenerationException(exception.Code, exception.Message, diagnostic);
        }
        catch (Exception exception)
        {
            // Do not preserve upstream exception text or InnerException: either can contain request headers.
            int? status = exception is HttpRequestException http && http.StatusCode.HasValue
                ? (int)http.StatusCode.Value
                : null;
            AiGenerationSafeDiagnostic diagnostic = AiGenerationDiagnostics.Create(
                AiGenerationErrorCode.ProviderRequestFailed,
                exception is AiGenerationAdapterStageException staged
                    ? AiGenerationDiagnostics.PhaseFor(staged.AdapterStage)
                    : phase,
                AiGenerationDiagnostics.CategoryFor(exception),
                profile,
                status,
                exception: exception);
            _diagnosticSink.TryWrite(diagnostic);
            throw new AiGenerationException(
                AiGenerationErrorCode.ProviderRequestFailed,
                "The image provider request failed.",
                diagnostic);
        }
    }

    private void TryWriteRequestDiagnostic(AiGenerationProviderProfile profile, AiGenerationRequest request)
    {
        try
        {
            _diagnosticSink.TryWriteRequest(AiGenerationDiagnostics.CreateRequest(profile, request));
        }
        catch
        {
            // Diagnostics are best-effort and must never block a provider request.
        }
    }
}
