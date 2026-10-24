using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Contracts.Runtime;
using AppAgasCs.Mcp.Runtime;

namespace AppAgasCs.Mcp;

/// <summary>Typed MCP projection over independent AI connection and global model settings.</summary>
public sealed class AgasMcpAiSettingsFacade
{
    private const string InvalidArgumentsMessage = "Tool arguments did not match the expected JSON shape.";

    private readonly AgasAiProfileSettingsService _service;
    private readonly IAgasAiSettingsMutationLeaseBridge _mutationBridge;
    private readonly Func<AgasAiEffectiveProfileSelection?> _captureEffectiveCurrent;
    private readonly AgasMcpDirtyState _dirtyState;

    public AgasMcpAiSettingsFacade(
        AgasAiProfileSettingsService service,
        IAgasAiSettingsMutationLeaseBridge mutationBridge,
        AgasMcpDirtyState dirtyState,
        Func<AgasAiEffectiveProfileSelection?>? captureEffectiveCurrent = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _mutationBridge = mutationBridge ?? throw new ArgumentNullException(nameof(mutationBridge));
        _dirtyState = dirtyState ?? throw new ArgumentNullException(nameof(dirtyState));
        _captureEffectiveCurrent = captureEffectiveCurrent ?? (() => null);
    }

    public AgasMcpToolResponse Profiles(AgasAiProfilesListInput input)
    {
        if (!Validate(input)) return Invalid();
        AgasAiProfilesQueryResult result = _service.QueryProfiles(_captureEffectiveCurrent());
        return Success(new AgasMcpAiProfilesDataDto(
            result.Profiles.Select(ToProfileRow).ToArray(),
            result.ProviderConfigurationErrorCount,
            result.ActiveProfileId,
            result.EffectiveCurrent == null ? null : new AgasMcpAiEffectiveProfileDto(
                result.EffectiveCurrent.ProfileId,
                result.EffectiveCurrent.ProviderId,
                result.EffectiveCurrent.AdapterKind,
                result.EffectiveCurrent.ModelName,
                result.EffectiveCurrent.Source)));
    }

    public AgasMcpToolResponse Models(AgasAiModelsListInput input)
    {
        if (!Validate(input)) return Invalid();
        AgasAiProvidersQueryResult result = _service.QueryProviders();
        return Success(new AgasMcpAiModelsDataDto(
            result.Providers.Select(provider => new AgasMcpAiProviderRowDto(
                provider.ProviderId,
                provider.DisplayName,
                provider.AdapterKind,
                provider.DefaultModelName,
                provider.Capabilities,
                provider.SupportsCustomModel,
                provider.Models.Select(model => new AgasMcpAiModelRowDto(
                    model.ModelName,
                    model.Label,
                    model.Recommended)).ToArray())).ToArray(),
            result.ProviderConfigurationErrorCount));
    }

    public AgasMcpToolResponse Activate(
        AgasAiProfileActivateInput input,
        CancellationToken cancellationToken = default)
    {
        var execution = new AgasMcpCommitExecution(AgasMcpCommitPolicy.ShortAtomicCommit);
        return Activate(input, execution, cancellationToken);
    }

    public AgasMcpToolResponse Activate(
        AgasAiProfileActivateInput input,
        AgasMcpCommitExecution execution,
        CancellationToken cancellationToken = default)
    {
        if (!Validate(input)) return Invalid();
        RequireShortAtomic(execution);
        AgasAiSettingsMutationResult result = _service.ActivateProfile(
            input.ProfileId,
            _mutationBridge,
            new ExecutionCommitGate(execution),
            cancellationToken);
        CompleteCommitIfStarted(execution);
        if (!result.Success) return MutationFailure(result.Failure);
        return Success(new AgasMcpAiProfileActivatedDataDto(
            result.ProfileId,
            result.ProviderId,
            result.ModelName,
            result.CredentialConfigured,
            result.Persisted,
            result.UiSynchronized));
    }

    public AgasMcpToolResponse SetModel(
        AgasAiModelSetInput input,
        CancellationToken cancellationToken = default)
    {
        var execution = new AgasMcpCommitExecution(AgasMcpCommitPolicy.ShortAtomicCommit);
        return SetModel(input, execution, cancellationToken);
    }

    public AgasMcpToolResponse SetModel(
        AgasAiModelSetInput input,
        AgasMcpCommitExecution execution,
        CancellationToken cancellationToken = default)
    {
        if (!Validate(input)) return Invalid();
        RequireShortAtomic(execution);
        AgasAiSettingsMutationResult result = _service.SetActiveModel(
            input.ModelName,
            _mutationBridge,
            new ExecutionCommitGate(execution),
            cancellationToken);
        CompleteCommitIfStarted(execution);
        if (!result.Success) return MutationFailure(result.Failure);
        return Success(new AgasMcpAiModelSetDataDto(
            result.ProfileId,
            result.ProviderId,
            result.ModelName,
            result.KnownPreset,
            result.CredentialConfigured,
            result.Persisted,
            result.UiSynchronized));
    }

    public AgasMcpToolResponse Diagnostics(AgasAiDiagnosticsInput input)
    {
        if (!Validate(input)) return Invalid();
        AgasAiConfigurationDiagnostic diagnostic = _service.Diagnose(
            input.ProfileId,
            string.IsNullOrWhiteSpace(input.ProfileId) ? _captureEffectiveCurrent() : null);
        if (diagnostic.IssueCodes.Contains("PROFILE_NOT_FOUND", StringComparer.Ordinal))
        {
            return Failure(
                AgasMcpErrorCodes.AiProfileNotFound,
                "The requested AI profile was not found.",
                "Call agas.ai.profiles.list and retry with a returned profileId.");
        }

        AgasMcpAiIssueDto[] issues = diagnostic.IssueCodes
            .Select(ToIssue)
            .OrderBy(issue => issue.Code, StringComparer.Ordinal)
            .ToArray();
        return Success(new AgasMcpAiDiagnosticsDataDto(
            diagnostic.ProfileId,
            diagnostic.ProviderId,
            diagnostic.ModelName,
            diagnostic.CredentialConfigured,
            diagnostic.ProviderAvailable,
            diagnostic.Ready,
            diagnostic.NetworkChecked,
            diagnostic.Capabilities,
            issues));
    }

    /// <summary>Must be invoked from the Godot main-thread postcommit stage.</summary>
    public AgasMcpToolResponse FinalizeStagedUiOnMainThread(AgasMcpToolResponse response)
    {
        if (_mutationBridge is not AgasAiSettingsUiMutationBridge staged)
            return response;
        if (!response.Ok)
        {
            staged.FinishWithoutCommitOnMainThread();
            return response;
        }

        bool synchronized = staged.FinalizeOnMainThread();
        object? data = response.Data switch
        {
            AgasMcpAiProfileActivatedDataDto value => value with { UiSynchronized = synchronized },
            AgasMcpAiModelSetDataDto value => value with { UiSynchronized = synchronized },
            _ => response.Data,
        };
        return data == null ? response : new AgasMcpToolResponse(
            response.Ok,
            response.Saved,
            response.Dirty,
            data,
            response.Warnings,
            response.Error);
    }

    private static AgasMcpAiProfileRowDto ToProfileRow(AgasAiSafeProfileDescriptor profile) => new(
        profile.ProfileId,
        profile.Name,
        profile.ProviderId,
        profile.ProviderDisplayName,
        profile.ModelName,
        profile.CredentialConfigured,
        profile.ProviderAvailable,
        profile.Capabilities);

    private static AgasMcpAiIssueDto ToIssue(string code) => code switch
    {
        "CREDENTIAL_MISSING" => new AgasMcpAiIssueDto(
            AgasMcpErrorCodes.AiCredentialMissing,
            "The AI connection has no configured credential.",
            "Configure the connection credential in AGAS settings before generating."),
        "PROVIDER_UNAVAILABLE" => new AgasMcpAiIssueDto(
            AgasMcpErrorCodes.AiProviderUnavailable,
            "The AI provider configuration is unavailable.",
            "Repair or install the provider configuration, then retry."),
        "MODEL_MISSING" => new AgasMcpAiIssueDto(
            AgasMcpErrorCodes.AiModelInvalid,
            "No global image model is selected.",
            "Select a model with agas.ai.model.set."),
        "MODEL_UNSUPPORTED" => new AgasMcpAiIssueDto(
            AgasMcpErrorCodes.AiModelInvalid,
            "The selected global image model is not supported.",
            "Call agas.ai.models.list and select one of the returned models."),
        _ => new AgasMcpAiIssueDto(
            AgasMcpErrorCodes.InternalError,
            "The AI profile configuration could not be validated.",
            "Review AGAS settings and retry."),
    };

    private AgasMcpToolResponse MutationFailure(AgasAiSettingsMutationFailure failure) => failure switch
    {
        AgasAiSettingsMutationFailure.Conflict => Failure(
            AgasMcpErrorCodes.AiSettingsConflict,
            "AI settings changed while this operation was being prepared.",
            "Finish pending settings edits and retry."),
        AgasAiSettingsMutationFailure.ProfileNotFound => Failure(
            AgasMcpErrorCodes.AiProfileNotFound,
            "The requested or active AI connection was not found.",
            "Call agas.ai.profiles.list and retry with an available connection."),
        AgasAiSettingsMutationFailure.ProviderUnavailable => Failure(
            AgasMcpErrorCodes.AiProviderUnavailable,
            "The AI provider configuration is unavailable.",
            "Repair or install the provider configuration, then retry."),
        AgasAiSettingsMutationFailure.ModelInvalid => Failure(
            AgasMcpErrorCodes.AiModelInvalid,
            "The model name is invalid, unsupported, or contains protected credential material.",
            "Call agas.ai.models.list and use one of the returned model names."),
        AgasAiSettingsMutationFailure.Canceled => Failure(
            AgasMcpErrorCodes.Stopped,
            "The AI settings operation was canceled before commit.",
            "Retry while the MCP runtime is running."),
        _ => Failure(
            AgasMcpErrorCodes.InternalError,
            "AI settings could not be persisted.",
            "Check AGAS logs and retry."),
    };

    private static bool Validate<T>(T input) where T : class =>
        AgasMcpContractRegistry.ValidateInstance(input).Success;

    private AgasMcpToolResponse Success(object data) =>
        AgasMcpToolResponse.Success(!_dirtyState.IsDirty, _dirtyState.IsDirty, data);

    private AgasMcpToolResponse Invalid() => Failure(
        AgasMcpErrorCodes.InvalidOperation,
        InvalidArgumentsMessage,
        "Check the documented AI tool input schema and retry.");

    private AgasMcpToolResponse Failure(string code, string message, string suggestion) =>
        AgasMcpToolResponse.Failure(!_dirtyState.IsDirty, _dirtyState.IsDirty, code, message, suggestion);

    private static void RequireShortAtomic(AgasMcpCommitExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (execution.Policy != AgasMcpCommitPolicy.ShortAtomicCommit)
            throw new ArgumentException("AI settings mutation requires ShortAtomicCommit.", nameof(execution));
    }

    private static void CompleteCommitIfStarted(AgasMcpCommitExecution execution)
    {
        if (execution.State == AgasMcpCommitExecutionState.ShortCommitting)
            execution.CompleteCommit();
    }

    private sealed class ExecutionCommitGate : IAgasAiSettingsCommitGate
    {
        private readonly AgasMcpCommitExecution _execution;
        public ExecutionCommitGate(AgasMcpCommitExecution execution) => _execution = execution;
        public bool TryBeginAtomicReplace() => _execution.TryBeginShortAtomicCommit();
    }
}
