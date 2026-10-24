#nullable enable

using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AppAgasCs.Features.Ai.Settings;

namespace AppAgasCs.Features.Ai.Generation;

/// <summary>
/// Carries only allow-listed exception metadata across the adapter boundary.
/// The original exception, message and stack are deliberately not retained.
/// </summary>
internal sealed class AiGenerationAdapterStageException : Exception
{
    public AiGenerationAdapterStageException(AiGenerationAdapterStage stage, Exception source)
        : base("The image provider adapter stage failed.")
    {
        AdapterStage = stage;
        Category = AiGenerationDiagnostics.CategoryFor(source);
        ExceptionType = AiGenerationDiagnostics.SafeExceptionType(source.GetType());
        InnerExceptionType = source.InnerException is null
            ? null
            : AiGenerationDiagnostics.SafeExceptionType(source.InnerException.GetType());
        HttpStatus = source is HttpRequestException http && http.StatusCode.HasValue
            ? (int)http.StatusCode.Value
            : null;
    }

    public AiGenerationAdapterStage AdapterStage { get; }
    public AiGenerationFailureCategory Category { get; }
    public string? ExceptionType { get; }
    public string? InnerExceptionType { get; }
    public int? HttpStatus { get; }
}

public sealed class AiGenerationFileDiagnosticSink : IAiGenerationDiagnosticSink
{
    private const long DefaultMaximumBytes = 1024 * 1024;
    private const int DefaultBackupCount = 2;
    private readonly object _gate = new();
    private readonly long _maximumBytes;
    private readonly int _backupCount;

    public AiGenerationFileDiagnosticSink(
        string? homeDirectory = null,
        long maximumBytes = DefaultMaximumBytes,
        int backupCount = DefaultBackupCount)
    {
        if (maximumBytes < 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }
        if (backupCount < 1 || backupCount > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(backupCount));
        }
        string home = string.IsNullOrWhiteSpace(homeDirectory)
            ? AgasAiSettingsPaths.ResolveHome()
            : Path.GetFullPath(homeDirectory);
        LogDirectory = Path.Combine(home, "logs");
        LogPath = Path.Combine(LogDirectory, "ai-generation.log");
        _maximumBytes = maximumBytes;
        _backupCount = backupCount;
    }

    public string LogDirectory { get; }

    public string LogPath { get; }

    public void TryWrite(AiGenerationSafeDiagnostic diagnostic)
    {
        if (diagnostic == null)
        {
            return;
        }
        try
        {
            string line = JsonSerializer.Serialize(new
            {
                utc = DateTimeOffset.UtcNow.ToString("O"),
                diagnosticId = diagnostic.DiagnosticId,
                errorCode = diagnostic.ErrorCode.ToString(),
                phase = diagnostic.Phase.ToString(),
                category = diagnostic.Category.ToString(),
                profileId = diagnostic.ProfileId,
                adapterKind = diagnostic.AdapterKind,
                modelName = diagnostic.ModelName,
                endpointHost = diagnostic.EndpointHost,
                httpStatus = diagnostic.HttpStatus,
                upstreamCode = diagnostic.UpstreamCode,
                exceptionType = diagnostic.ExceptionType,
                innerExceptionType = diagnostic.InnerExceptionType,
                adapterStage = diagnostic.AdapterStage?.ToString(),
            }) + Environment.NewLine;
            byte[] encoded = Encoding.UTF8.GetBytes(line);
            lock (_gate)
            {
                Directory.CreateDirectory(LogDirectory);
                HardenDirectory(LogDirectory);
                RotateIfNeeded(encoded.LongLength);
                using (var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    stream.Write(encoded);
                    stream.Flush(flushToDisk: true);
                }
                HardenFile(LogPath);
            }
        }
        catch
        {
            // Diagnostics must never replace the safe primary failure.
        }
    }

    public void TryWriteRequest(AiGenerationSafeRequestDiagnostic diagnostic)
    {
        if (diagnostic == null)
        {
            return;
        }
        TryWriteLine(new
        {
            utc = DateTimeOffset.UtcNow.ToString("O"),
            eventType = "providerRequest",
            diagnosticId = diagnostic.DiagnosticId,
            operation = diagnostic.Operation,
            adapterKind = diagnostic.AdapterKind,
            endpointHost = diagnostic.EndpointHost,
            requestPixelWidth = diagnostic.RequestPixelWidth,
            requestPixelHeight = diagnostic.RequestPixelHeight,
            qPixelWidth = diagnostic.QPixelWidth,
            qPixelHeight = diagnostic.QPixelHeight,
            wRelativeX = diagnostic.WRelativeX,
            wRelativeY = diagnostic.WRelativeY,
            wPixelWidth = diagnostic.WPixelWidth,
            wPixelHeight = diagnostic.WPixelHeight,
            sourcePngIhdrWidth = diagnostic.SourcePngIhdrWidth,
            sourcePngIhdrHeight = diagnostic.SourcePngIhdrHeight,
            maskPngIhdrWidth = diagnostic.MaskPngIhdrWidth,
            maskPngIhdrHeight = diagnostic.MaskPngIhdrHeight,
            referenceImageCount = diagnostic.ReferenceImageCount,
        });
    }

    private void TryWriteLine(object payload)
    {
        try
        {
            string line = JsonSerializer.Serialize(payload) + Environment.NewLine;
            byte[] encoded = Encoding.UTF8.GetBytes(line);
            lock (_gate)
            {
                Directory.CreateDirectory(LogDirectory);
                HardenDirectory(LogDirectory);
                RotateIfNeeded(encoded.LongLength);
                using (var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    stream.Write(encoded);
                    stream.Flush(flushToDisk: true);
                }
                HardenFile(LogPath);
            }
        }
        catch
        {
            // Diagnostics must never replace the primary request or failure.
        }
    }

    private void RotateIfNeeded(long incomingBytes)
    {
        if (!File.Exists(LogPath) || new FileInfo(LogPath).Length + incomingBytes <= _maximumBytes)
        {
            return;
        }
        string oldest = LogPath + "." + _backupCount;
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }
        for (int index = _backupCount - 1; index >= 1; index--)
        {
            string source = LogPath + "." + index;
            if (File.Exists(source))
            {
                File.Move(source, LogPath + "." + (index + 1), overwrite: true);
            }
        }
        File.Move(LogPath, LogPath + ".1", overwrite: true);
        HardenFile(LogPath + ".1");
    }

    private static void HardenDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void HardenFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

public static class AiGenerationDiagnostics
{
    private static readonly Lazy<IAiGenerationDiagnosticSink> DefaultSinkHolder = new(
        () => new AiGenerationFileDiagnosticSink(),
        isThreadSafe: true);

    public static IAiGenerationDiagnosticSink DefaultSink => DefaultSinkHolder.Value;

    public static AiGenerationSafeRequestDiagnostic CreateRequest(
        AiGenerationProviderProfile profile,
        AiGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);

        AiGenerationPixelRegion? edit = request.EditInput?.EditRegionPixels;
        AiGenerationReferenceImage? source = request.EditInput?.SourceCanvas;
        AiGenerationReferenceImage? mask = request.EditInput?.Mask;
        bool hasQ = request.RegionMetadata?.SelectionRegion != null;
        string secret = profile.ApiKey;

        return new AiGenerationSafeRequestDiagnostic(
            Guid.NewGuid().ToString("N"),
            request.Operation.ToString(),
            profile.AdapterKind,
            SafeValue(profile.BaseUri.DnsSafeHost, 253, secret),
            request.Width,
            request.Height,
            hasQ ? request.Width : null,
            hasQ ? request.Height : null,
            edit?.X,
            edit?.Y,
            edit?.Width,
            edit?.Height,
            source?.PixelWidth,
            source?.PixelHeight,
            mask?.PixelWidth,
            mask?.PixelHeight,
            request.ReferenceImages.Count + (source == null ? 0 : 1));
    }

    public static AiGenerationSafeDiagnostic Create(
        AiGenerationErrorCode errorCode,
        AiGenerationFailurePhase phase,
        AiGenerationFailureCategory category,
        AiGenerationProviderProfile? profile = null,
        int? httpStatus = null,
        string? upstreamCode = null,
        Exception? exception = null,
        AiGenerationAdapterStage? adapterStage = null)
    {
        string secret = profile?.ApiKey ?? string.Empty;
        AiGenerationAdapterStageException? staged = exception as AiGenerationAdapterStageException;
        return new AiGenerationSafeDiagnostic(
            Guid.NewGuid().ToString("N"),
            errorCode,
            phase,
            category,
            SafeValue(profile?.Id, 80, secret),
            profile?.AdapterKind ?? string.Empty,
            SafeValue(profile?.ModelName, 120, secret),
            SafeValue(profile?.BaseUri.DnsSafeHost, 253, secret),
            httpStatus ?? staged?.HttpStatus,
            SafeToken(upstreamCode, secret),
            staged?.ExceptionType ?? SafeExceptionType(exception?.GetType()),
            staged?.InnerExceptionType ?? SafeExceptionType(exception?.InnerException?.GetType()),
            adapterStage ?? staged?.AdapterStage);
    }

    public static AiGenerationSafeDiagnostic Enrich(
        AiGenerationSafeDiagnostic? diagnostic,
        AiGenerationProviderProfile profile,
        AiGenerationErrorCode errorCode,
        AiGenerationFailurePhase fallbackPhase,
        AiGenerationFailureCategory fallbackCategory)
    {
        AiGenerationSafeDiagnostic source = diagnostic ?? Create(errorCode, fallbackPhase, fallbackCategory);
        string secret = profile.ApiKey;
        return source with
        {
            ErrorCode = errorCode,
            ProfileId = SafeValue(profile.Id, 80, secret),
            AdapterKind = profile.AdapterKind,
            ModelName = SafeValue(profile.ModelName, 120, secret),
            EndpointHost = SafeValue(profile.BaseUri.DnsSafeHost, 253, secret),
            UpstreamCode = SafeToken(source.UpstreamCode, secret),
            ExceptionType = SafeExceptionTypeName(source.ExceptionType),
            InnerExceptionType = SafeExceptionTypeName(source.InnerExceptionType),
        };
    }

    internal static Exception AtAdapterStage(Exception exception, AiGenerationAdapterStage stage)
    {
        if (exception is AiGenerationAdapterStageException)
        {
            return exception;
        }
        if (exception is AiGenerationException generationException)
        {
            AiGenerationSafeDiagnostic diagnostic = (generationException.Diagnostic
                ?? Create(
                    generationException.Code,
                    PhaseFor(stage),
                    CategoryFor(generationException.Code))) with
            {
                AdapterStage = stage,
            };
            return new AiGenerationException(generationException.Code, generationException.Message, diagnostic);
        }

        return new AiGenerationAdapterStageException(stage, exception);
    }

    internal static AiGenerationFailurePhase PhaseFor(AiGenerationAdapterStage stage) => stage switch
    {
        AiGenerationAdapterStage.ResponseRead => AiGenerationFailurePhase.ProviderResponse,
        _ => AiGenerationFailurePhase.ProviderRequest,
    };

    public static AiGenerationFailureCategory CategoryFor(AiGenerationErrorCode code) => code switch
    {
        AiGenerationErrorCode.ProviderRequestFailed => AiGenerationFailureCategory.ProviderRejected,
        AiGenerationErrorCode.InvalidProviderResponse or AiGenerationErrorCode.UnsupportedImage => AiGenerationFailureCategory.InvalidResponse,
        AiGenerationErrorCode.RequestTimedOut => AiGenerationFailureCategory.Timeout,
        AiGenerationErrorCode.RequestCanceled => AiGenerationFailureCategory.Canceled,
        AiGenerationErrorCode.SaveFailed => AiGenerationFailureCategory.Io,
        AiGenerationErrorCode.InvalidConfiguration or AiGenerationErrorCode.UnsupportedOperation or AiGenerationErrorCode.InvalidEditInput => AiGenerationFailureCategory.InvalidOperation,
        _ => AiGenerationFailureCategory.Unexpected,
    };

    public static AiGenerationFailureCategory CategoryFor(Exception exception) => exception switch
    {
        AiGenerationAdapterStageException staged => staged.Category,
        HttpRequestException http => CategoryFor(http.HttpRequestError),
        IOException => AiGenerationFailureCategory.Io,
        InvalidOperationException or ObjectDisposedException or ArgumentException => AiGenerationFailureCategory.InvalidOperation,
        _ => AiGenerationFailureCategory.Unexpected,
    };

    public static AiGenerationFailureCategory CategoryFor(HttpRequestError error) => error switch
    {
        HttpRequestError.NameResolutionError => AiGenerationFailureCategory.NameResolution,
        HttpRequestError.ConnectionError => AiGenerationFailureCategory.Connection,
        HttpRequestError.SecureConnectionError or HttpRequestError.VersionNegotiationError => AiGenerationFailureCategory.SecureConnection,
        HttpRequestError.ProxyTunnelError => AiGenerationFailureCategory.Proxy,
        HttpRequestError.ResponseEnded or HttpRequestError.InvalidResponse or HttpRequestError.HttpProtocolError
            => AiGenerationFailureCategory.InvalidResponse,
        HttpRequestError.UserAuthenticationError => AiGenerationFailureCategory.ProviderRejected,
        HttpRequestError.ConfigurationLimitExceeded => AiGenerationFailureCategory.InvalidOperation,
        _ => AiGenerationFailureCategory.Unexpected,
    };

    public static string PhaseText(AiGenerationFailurePhase phase) => phase switch
    {
        AiGenerationFailurePhase.Preflight => "准备请求",
        AiGenerationFailurePhase.ProviderRequest => "发送服务请求",
        AiGenerationFailurePhase.ProviderResponse => "读取服务响应",
        AiGenerationFailurePhase.ImageDownload => "下载生成图片",
        AiGenerationFailurePhase.SaveResult => "保存生成结果",
        AiGenerationFailurePhase.PanelRunner => "运行生成任务",
        _ => "生成任务",
    };

    public static string CategoryText(AiGenerationFailureCategory category) => category switch
    {
        AiGenerationFailureCategory.ProviderRejected => "服务拒绝请求",
        AiGenerationFailureCategory.NameResolution => "域名解析失败",
        AiGenerationFailureCategory.Connection => "连接失败",
        AiGenerationFailureCategory.SecureConnection => "TLS 安全连接失败",
        AiGenerationFailureCategory.Proxy => "代理连接失败",
        AiGenerationFailureCategory.InvalidResponse => "服务响应无效",
        AiGenerationFailureCategory.InvalidOperation => "请求状态无效",
        AiGenerationFailureCategory.Io => "本地读写失败",
        AiGenerationFailureCategory.Timeout => "请求超时",
        AiGenerationFailureCategory.Canceled => "请求已取消",
        _ => "未知客户端错误",
    };

    public static string FormatForUi(AiGenerationSafeDiagnostic diagnostic)
    {
        var parts = new System.Collections.Generic.List<string>
        {
            $"阶段：{PhaseText(diagnostic.Phase)}",
            $"类别：{CategoryText(diagnostic.Category)}",
            $"错误码：{diagnostic.ErrorCode}",
        };
        if (diagnostic.HttpStatus.HasValue)
        {
            parts.Add($"HTTP：{diagnostic.HttpStatus.Value}");
        }
        if (!string.IsNullOrEmpty(diagnostic.UpstreamCode))
        {
            parts.Add($"上游代码：{diagnostic.UpstreamCode}");
        }
        if (!string.IsNullOrEmpty(diagnostic.EndpointHost))
        {
            parts.Add($"主机：{diagnostic.EndpointHost}");
        }
        parts.Add($"诊断ID：{diagnostic.DiagnosticId}");
        return string.Join(" · ", parts);
    }

    public static string? SafeToken(string? value, string secret = "")
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > 64 || ContainsSecret(normalized, secret))
        {
            return null;
        }
        foreach (char character in normalized)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':'))
            {
                return null;
            }
        }
        return normalized;
    }

    public static string? SafeExceptionType(Type? type) =>
        SafeExceptionTypeName(type?.FullName);

    private static string? SafeExceptionTypeName(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > 256)
        {
            return null;
        }
        foreach (char character in normalized)
        {
            if (!(char.IsAsciiLetterOrDigit(character)
                || character is '.' or '+' or '`' or '[' or ']' or ',' or '_'))
            {
                return null;
            }
        }
        return normalized;
    }

    private static string SafeValue(string? value, int maximumLength, string secret)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || ContainsSecret(normalized, secret))
        {
            return string.Empty;
        }
        var builder = new StringBuilder(Math.Min(normalized.Length, maximumLength));
        foreach (char character in normalized)
        {
            if (!char.IsControl(character))
            {
                builder.Append(character);
                if (builder.Length >= maximumLength)
                {
                    break;
                }
            }
        }
        return builder.ToString();
    }

    private static bool ContainsSecret(string value, string secret) =>
        secret.Length > 0 && value.Contains(secret, StringComparison.Ordinal);

}
