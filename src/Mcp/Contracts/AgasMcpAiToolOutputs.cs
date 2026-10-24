using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using AppAgasCs.Mcp.Contracts.Output;

namespace AppAgasCs.Mcp.Contracts;

internal static class AgasMcpAiOutputConstraints
{
    public const string SafeId = "^[A-Za-z0-9_-]+$";
    public const string JobId = "^aijob-[a-f0-9]{32}$";
    public const string RelativePath = "^(?!/)(?!.*\\\\)(?!.*(?:^|/)\\.\\.?/).+$";
}

[AgasMcpOutputObject]
public sealed record AgasMcpAiExecutionIdentityDto(
    [property: AgasMcpOutputString(MinimumLength = 1, MaximumLength = 120, Pattern = AgasMcpAiOutputConstraints.SafeId)] string ProfileId,
    [property: AgasMcpOutputProviderId] string ProviderId,
    string AdapterKind,
    string ModelName);

[AgasMcpOutputObject]
public sealed record AgasMcpAiEffectiveProfileDto(
    [property: AgasMcpOutputString(MinimumLength = 1, MaximumLength = 120, Pattern = AgasMcpAiOutputConstraints.SafeId)] string ProfileId,
    [property: AgasMcpOutputProviderId] string ProviderId,
    string AdapterKind,
    string ModelName,
    [property: AgasMcpOutputAllowedValues("liveUi", "persisted")] string Source);

[AgasMcpOutputObject]
public sealed class AgasMcpAiProfileRowDto
{
    public AgasMcpAiProfileRowDto(
        string profileId,
        string name,
        string providerId,
        string providerDisplayName,
        string modelName,
        bool credentialConfigured,
        bool providerAvailable,
        IReadOnlyList<string> capabilities)
    {
        ProfileId = profileId;
        Name = name;
        ProviderId = providerId;
        ProviderDisplayName = providerDisplayName;
        ModelName = modelName;
        CredentialConfigured = credentialConfigured;
        ProviderAvailable = providerAvailable;
        Capabilities = Array.AsReadOnly((capabilities ?? Array.Empty<string>()).ToArray());
    }

    [AgasMcpOutputString(MinimumLength = 1, MaximumLength = 120, Pattern = AgasMcpAiOutputConstraints.SafeId)]
    public string ProfileId { get; }
    public string Name { get; }
    [AgasMcpOutputProviderId]
    public string ProviderId { get; }
    public string ProviderDisplayName { get; }
    public string ModelName { get; }
    public bool CredentialConfigured { get; }
    public bool ProviderAvailable { get; }
    [AgasMcpOutputAllowedValues("textToImage", "referenceEdit", "regionEdit", "nativeAlphaMask", "guidedRegionEdit")]
    public IReadOnlyList<string> Capabilities { get; }
}

[AgasMcpOutputObject]
public sealed record AgasMcpAiModelRowDto(string ModelName, string Label, bool Recommended);

[AgasMcpOutputObject]
public sealed class AgasMcpAiProviderRowDto
{
    public AgasMcpAiProviderRowDto(
        string providerId,
        string displayName,
        string adapterKind,
        string defaultModelName,
        IReadOnlyList<string> capabilities,
        bool supportsCustomModel,
        IReadOnlyList<AgasMcpAiModelRowDto> models)
    {
        ProviderId = providerId;
        DisplayName = displayName;
        AdapterKind = adapterKind;
        DefaultModelName = defaultModelName;
        Capabilities = Array.AsReadOnly((capabilities ?? Array.Empty<string>()).ToArray());
        SupportsCustomModel = supportsCustomModel;
        Models = Array.AsReadOnly((models ?? Array.Empty<AgasMcpAiModelRowDto>()).ToArray());
    }

    [AgasMcpOutputProviderId]
    public string ProviderId { get; }
    public string DisplayName { get; }
    public string AdapterKind { get; }
    public string DefaultModelName { get; }
    [AgasMcpOutputAllowedValues("textToImage", "referenceEdit", "regionEdit", "nativeAlphaMask", "guidedRegionEdit")]
    public IReadOnlyList<string> Capabilities { get; }
    public bool SupportsCustomModel { get; }
    public IReadOnlyList<AgasMcpAiModelRowDto> Models { get; }
}

[AgasMcpOutputObject]
public sealed class AgasMcpAiProfilesDataDto
{
    public AgasMcpAiProfilesDataDto(
        IReadOnlyList<AgasMcpAiProfileRowDto> profiles,
        int providerConfigurationErrorCount,
        string? activeProfileId = null,
        AgasMcpAiEffectiveProfileDto? effectiveCurrent = null)
    {
        Profiles = Array.AsReadOnly((profiles ?? Array.Empty<AgasMcpAiProfileRowDto>()).ToArray());
        ProviderConfigurationErrorCount = providerConfigurationErrorCount;
        ActiveProfileId = activeProfileId;
        EffectiveCurrent = effectiveCurrent;
    }

    public IReadOnlyList<AgasMcpAiProfileRowDto> Profiles { get; }
    [AgasMcpOutputNumber(Minimum = 0)]
    public int ProviderConfigurationErrorCount { get; }
    [AgasMcpOutputOptional]
    [AgasMcpOutputString(MinimumLength = 1, MaximumLength = 120, Pattern = AgasMcpAiOutputConstraints.SafeId)]
    public string? ActiveProfileId { get; }
    [AgasMcpOutputOptional]
    public AgasMcpAiEffectiveProfileDto? EffectiveCurrent { get; }
}

[AgasMcpOutputObject]
public sealed class AgasMcpAiModelsDataDto
{
    public AgasMcpAiModelsDataDto(IReadOnlyList<AgasMcpAiProviderRowDto> providers, int providerConfigurationErrorCount)
    {
        Providers = Array.AsReadOnly((providers ?? Array.Empty<AgasMcpAiProviderRowDto>()).ToArray());
        ProviderConfigurationErrorCount = providerConfigurationErrorCount;
    }

    public IReadOnlyList<AgasMcpAiProviderRowDto> Providers { get; }
    [AgasMcpOutputNumber(Minimum = 0)]
    public int ProviderConfigurationErrorCount { get; }
}

[AgasMcpOutputObject]
public sealed record AgasMcpAiProfileActivatedDataDto(
    [property: AgasMcpOutputString(MinimumLength = 1, MaximumLength = 120, Pattern = AgasMcpAiOutputConstraints.SafeId)] string ProfileId,
    [property: AgasMcpOutputProviderId] string ProviderId,
    string ModelName,
    bool CredentialConfigured,
    bool Persisted,
    bool UiSynchronized);

[AgasMcpOutputObject]
public sealed record AgasMcpAiModelSetDataDto(
    [property: AgasMcpOutputString(MinimumLength = 1, MaximumLength = 120, Pattern = AgasMcpAiOutputConstraints.SafeId)] string ProfileId,
    [property: AgasMcpOutputProviderId] string ProviderId,
    string ModelName,
    bool KnownPreset,
    bool CredentialConfigured,
    bool Persisted,
    bool UiSynchronized);

[AgasMcpOutputObject]
public sealed record AgasMcpAiIssueDto(string Code, string Message, string Suggestion);

[AgasMcpOutputObject]
public sealed class AgasMcpAiDiagnosticsDataDto
{
    public AgasMcpAiDiagnosticsDataDto(
        string profileId,
        string providerId,
        string modelName,
        bool credentialConfigured,
        bool providerAvailable,
        bool ready,
        bool networkChecked,
        IReadOnlyList<string> capabilities,
        IReadOnlyList<AgasMcpAiIssueDto> issues)
    {
        ProfileId = profileId;
        ProviderId = providerId;
        ModelName = modelName;
        CredentialConfigured = credentialConfigured;
        ProviderAvailable = providerAvailable;
        Ready = ready;
        NetworkChecked = networkChecked;
        Capabilities = Array.AsReadOnly((capabilities ?? Array.Empty<string>()).ToArray());
        Issues = Array.AsReadOnly((issues ?? Array.Empty<AgasMcpAiIssueDto>()).ToArray());
    }

    [AgasMcpOutputString(MinimumLength = 1, MaximumLength = 120, Pattern = AgasMcpAiOutputConstraints.SafeId)]
    public string ProfileId { get; }
    [AgasMcpOutputProviderId]
    public string ProviderId { get; }
    public string ModelName { get; }
    public bool CredentialConfigured { get; }
    public bool ProviderAvailable { get; }
    public bool Ready { get; }
    [AgasMcpOutputConst(false)]
    public bool NetworkChecked { get; }
    [AgasMcpOutputAllowedValues("textToImage", "referenceEdit", "regionEdit", "nativeAlphaMask", "guidedRegionEdit")]
    public IReadOnlyList<string> Capabilities { get; }
    public IReadOnlyList<AgasMcpAiIssueDto> Issues { get; }
}

[AgasMcpOutputObject]
public sealed record AgasMcpAiDiagnosticDto(
    string DiagnosticId,
    string ErrorCode,
    string Phase,
    string Category,
    [property: AgasMcpOutputOptional] int? HttpStatus = null,
    [property: AgasMcpOutputOptional] string? UpstreamCode = null);

[AgasMcpOutputObject]
public sealed record AgasMcpAiGenerateDataDto(
    [property: AgasMcpOutputString(MinimumLength = 38, MaximumLength = 38, Pattern = AgasMcpAiOutputConstraints.JobId)] string JobId,
    [property: AgasMcpOutputConst("Queued")] string State,
    [property: AgasMcpOutputAllowedValues("TextToImage", "ReferenceEdit", "MaskedEdit", "GuidedRegionEdit")] string EffectiveOperation,
    AgasMcpAiExecutionIdentityDto Execution,
    DateTimeOffset CreatedAtUtc);

[JsonPolymorphic]
[JsonDerivedType(typeof(AgasMcpAiJobQueuedDto))]
[JsonDerivedType(typeof(AgasMcpAiJobRunningDto))]
[JsonDerivedType(typeof(AgasMcpAiJobSucceededDto))]
[JsonDerivedType(typeof(AgasMcpAiJobFailedDto))]
[JsonDerivedType(typeof(AgasMcpAiJobCanceledDto))]
[AgasMcpOutputUnion(
    typeof(AgasMcpAiJobQueuedDto),
    typeof(AgasMcpAiJobRunningDto),
    typeof(AgasMcpAiJobSucceededDto),
    typeof(AgasMcpAiJobFailedDto),
    typeof(AgasMcpAiJobCanceledDto))]
public interface AgasMcpAiJobDto;

[AgasMcpOutputObject]
public sealed record AgasMcpAiJobQueuedDto(
    [property: AgasMcpOutputString(MinimumLength = 38, MaximumLength = 38, Pattern = AgasMcpAiOutputConstraints.JobId)] string JobId,
    [property: AgasMcpOutputConst("Queued")] string State,
    [property: AgasMcpOutputAllowedValues("TextToImage", "ReferenceEdit", "MaskedEdit", "GuidedRegionEdit")] string EffectiveOperation,
    AgasMcpAiExecutionIdentityDto Execution,
    DateTimeOffset CreatedAtUtc,
    [property: AgasMcpOutputConst(false)] bool AlreadyApplied) : AgasMcpAiJobDto;

[AgasMcpOutputObject]
public sealed record AgasMcpAiJobRunningDto(
    [property: AgasMcpOutputString(MinimumLength = 38, MaximumLength = 38, Pattern = AgasMcpAiOutputConstraints.JobId)] string JobId,
    [property: AgasMcpOutputConst("Running")] string State,
    [property: AgasMcpOutputAllowedValues("TextToImage", "ReferenceEdit", "MaskedEdit", "GuidedRegionEdit")] string EffectiveOperation,
    AgasMcpAiExecutionIdentityDto Execution,
    DateTimeOffset CreatedAtUtc,
    [property: AgasMcpOutputConst(false)] bool AlreadyApplied,
    DateTimeOffset StartedAtUtc) : AgasMcpAiJobDto;

[AgasMcpOutputObject]
[AgasMcpOutputWhenBoolean(nameof(AlreadyApplied), true, new[] { nameof(AppliedRelativePath), nameof(AppliedAtUtc) }, new string[0])]
[AgasMcpOutputWhenBoolean(nameof(AlreadyApplied), false, new string[0], new[] { nameof(AppliedRelativePath), nameof(AppliedAtUtc) })]
public sealed record AgasMcpAiJobSucceededDto : AgasMcpAiJobDto
{
    public AgasMcpAiJobSucceededDto(
        string jobId,
        string state,
        string effectiveOperation,
        AgasMcpAiExecutionIdentityDto execution,
        DateTimeOffset createdAtUtc,
        bool alreadyApplied,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        string resultRelativePath,
        string message,
        string? previewRelativePath = null,
        string? appliedRelativePath = null,
        DateTimeOffset? appliedAtUtc = null)
    {
        if (alreadyApplied != (appliedRelativePath != null && appliedAtUtc.HasValue))
            throw new ArgumentException("Applied path and timestamp must appear together exactly when alreadyApplied is true.");
        JobId = jobId;
        State = state;
        EffectiveOperation = effectiveOperation;
        Execution = execution;
        CreatedAtUtc = createdAtUtc;
        AlreadyApplied = alreadyApplied;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        ResultRelativePath = resultRelativePath;
        Message = message;
        PreviewRelativePath = previewRelativePath;
        AppliedRelativePath = appliedRelativePath;
        AppliedAtUtc = appliedAtUtc;
    }

    [AgasMcpOutputString(MinimumLength = 38, MaximumLength = 38, Pattern = AgasMcpAiOutputConstraints.JobId)] public string JobId { get; }
    [AgasMcpOutputConst("Succeeded")] public string State { get; }
    [AgasMcpOutputAllowedValues("TextToImage", "ReferenceEdit", "MaskedEdit", "GuidedRegionEdit")] public string EffectiveOperation { get; }
    public AgasMcpAiExecutionIdentityDto Execution { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public bool AlreadyApplied { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    [AgasMcpOutputString(MinimumLength = 1, Pattern = AgasMcpAiOutputConstraints.RelativePath)] public string ResultRelativePath { get; }
    public string Message { get; }
    [AgasMcpOutputOptional]
    [AgasMcpOutputString(MinimumLength = 1, Pattern = AgasMcpAiOutputConstraints.RelativePath)] public string? PreviewRelativePath { get; }
    [AgasMcpOutputOptional]
    [AgasMcpOutputString(MinimumLength = 1, Pattern = AgasMcpAiOutputConstraints.RelativePath)] public string? AppliedRelativePath { get; }
    [AgasMcpOutputOptional] public DateTimeOffset? AppliedAtUtc { get; }
}

[AgasMcpOutputObject]
public sealed record AgasMcpAiJobFailedDto(
    [property: AgasMcpOutputString(MinimumLength = 38, MaximumLength = 38, Pattern = AgasMcpAiOutputConstraints.JobId)] string JobId,
    [property: AgasMcpOutputConst("Failed")] string State,
    [property: AgasMcpOutputAllowedValues("TextToImage", "ReferenceEdit", "MaskedEdit", "GuidedRegionEdit")] string EffectiveOperation,
    AgasMcpAiExecutionIdentityDto Execution,
    DateTimeOffset CreatedAtUtc,
    [property: AgasMcpOutputConst(false)] bool AlreadyApplied,
    DateTimeOffset CompletedAtUtc,
    string ErrorCode,
    string Message,
    AgasMcpAiDiagnosticDto Diagnostic,
    [property: AgasMcpOutputOptional] DateTimeOffset? StartedAtUtc = null,
    [property: AgasMcpOutputOptional] string? Hint = null) : AgasMcpAiJobDto;

[AgasMcpOutputObject]
public sealed record AgasMcpAiJobCanceledDto(
    [property: AgasMcpOutputString(MinimumLength = 38, MaximumLength = 38, Pattern = AgasMcpAiOutputConstraints.JobId)] string JobId,
    [property: AgasMcpOutputConst("Canceled")] string State,
    [property: AgasMcpOutputAllowedValues("TextToImage", "ReferenceEdit", "MaskedEdit", "GuidedRegionEdit")] string EffectiveOperation,
    AgasMcpAiExecutionIdentityDto Execution,
    DateTimeOffset CreatedAtUtc,
    [property: AgasMcpOutputConst(false)] bool AlreadyApplied,
    DateTimeOffset CompletedAtUtc,
    [property: AgasMcpOutputOptional] DateTimeOffset? StartedAtUtc = null) : AgasMcpAiJobDto;

[AgasMcpOutputObject]
public sealed class AgasMcpAiStatusListDataDto
{
    public AgasMcpAiStatusListDataDto(IReadOnlyList<AgasMcpAiJobDto> jobs) =>
        Jobs = Array.AsReadOnly((jobs ?? Array.Empty<AgasMcpAiJobDto>()).ToArray());

    public IReadOnlyList<AgasMcpAiJobDto> Jobs { get; }
}

[AgasMcpOutputObject]
public sealed record AgasMcpAiCancelDataDto(
    [property: AgasMcpOutputString(MinimumLength = 38, MaximumLength = 38, Pattern = AgasMcpAiOutputConstraints.JobId)] string JobId,
    [property: AgasMcpOutputAllowedValues("Queued", "Running", "Succeeded", "Failed", "Canceled")] string State,
    bool Canceled);

[AgasMcpOutputObject]
public sealed record AgasMcpAiApplyDataDto(
    [property: AgasMcpOutputString(MinimumLength = 38, MaximumLength = 38, Pattern = AgasMcpAiOutputConstraints.JobId)] string JobId,
    [property: AgasMcpOutputConst("Succeeded")] string State,
    bool AlreadyApplied,
    [property: AgasMcpOutputString(MinimumLength = 1, Pattern = AgasMcpAiOutputConstraints.RelativePath)] string AppliedRelativePath,
    string Message);
