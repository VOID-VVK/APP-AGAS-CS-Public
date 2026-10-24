#nullable enable

namespace AppAgasCs.Features.Ai.Generation;

public enum AiGenerationFailurePhase
{
    Preflight,
    ProviderRequest,
    ProviderResponse,
    ImageDownload,
    SaveResult,
    PanelRunner,
}

public enum AiGenerationFailureCategory
{
    ProviderRejected,
    NameResolution,
    Connection,
    SecureConnection,
    Proxy,
    InvalidResponse,
    InvalidOperation,
    Io,
    Timeout,
    Canceled,
    Unexpected,
}

/// <summary>
/// Fixed, non-sensitive adapter checkpoints. These values may be written to the
/// diagnostic log; never replace them with request-derived text.
/// </summary>
public enum AiGenerationAdapterStage
{
    EndpointUriCreation,
    HttpClientAccess,
    RequestConstruction,
    PayloadConstruction,
    JsonSerialization,
    RequestSend,
    ResponseRead,
}

public sealed record AiGenerationSafeDiagnostic(
    string DiagnosticId,
    AiGenerationErrorCode ErrorCode,
    AiGenerationFailurePhase Phase,
    AiGenerationFailureCategory Category,
    string ProfileId,
    string AdapterKind,
    string ModelName,
    string EndpointHost,
    int? HttpStatus = null,
    string? UpstreamCode = null,
    string? ExceptionType = null,
    string? InnerExceptionType = null,
    AiGenerationAdapterStage? AdapterStage = null);

/// <summary>
/// Allow-listed geometry emitted once when a provider request starts. It is
/// intentionally limited to enum values, counts and pixel coordinates: no
/// prompt, credentials, image bytes/base64, paths or canvas identifiers.
/// </summary>
public sealed record AiGenerationSafeRequestDiagnostic(
    string DiagnosticId,
    string Operation,
    string AdapterKind,
    string EndpointHost,
    int RequestPixelWidth,
    int RequestPixelHeight,
    int? QPixelWidth,
    int? QPixelHeight,
    int? WRelativeX,
    int? WRelativeY,
    int? WPixelWidth,
    int? WPixelHeight,
    int? SourcePngIhdrWidth,
    int? SourcePngIhdrHeight,
    int? MaskPngIhdrWidth,
    int? MaskPngIhdrHeight,
    int ReferenceImageCount);

public interface IAiGenerationDiagnosticSink
{
    void TryWrite(AiGenerationSafeDiagnostic diagnostic);

    void TryWriteRequest(AiGenerationSafeRequestDiagnostic diagnostic)
    {
    }
}
