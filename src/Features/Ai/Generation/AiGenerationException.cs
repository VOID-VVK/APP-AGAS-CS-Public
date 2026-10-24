using System;

namespace AppAgasCs.Features.Ai.Generation;

public enum AiGenerationErrorCode
{
    AdapterNotFound,
    InvalidConfiguration,
    RequestCanceled,
    RequestTimedOut,
    ProviderRequestFailed,
    InvalidProviderResponse,
    UnsupportedImage,
    UnsupportedOperation,
    InvalidEditInput,
    SaveFailed
}

public sealed class AiGenerationException : Exception
{
    public AiGenerationException(
        AiGenerationErrorCode code,
        string safeMessage,
        AiGenerationSafeDiagnostic? diagnostic = null)
        : base(safeMessage)
    {
        Code = code;
        Diagnostic = diagnostic;
    }

    public AiGenerationErrorCode Code { get; }

    public AiGenerationSafeDiagnostic? Diagnostic { get; }
}
