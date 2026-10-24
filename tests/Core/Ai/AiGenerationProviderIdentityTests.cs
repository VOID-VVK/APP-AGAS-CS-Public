using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using Xunit;

namespace AppAgasCs.Tests.Core.Ai;

public sealed class AiGenerationProviderIdentityTests
{
    [Fact]
    public void NewConstructor_PreservesProviderIdIndependentFromAdapterKind()
    {
        var profile = new AiGenerationProviderProfile(
            "profile-1",
            "Third Party",
            "third-party-provider",
            "vendor.adapter-v2",
            "image-model",
            "credential",
            "https://relay.example/v1/");

        Assert.Equal("third-party-provider", profile.ProviderId);
        Assert.Equal("vendor.adapter-v2", profile.AdapterKind);
        AgasMcpAiExecutionIdentity execution = AgasMcpAiSafeProjector.ProjectExecutionIdentity(
            profile.Id,
            profile.ProviderId,
            profile.AdapterKind,
            profile.ModelName,
            new[] { profile.ApiKey });
        Assert.Equal("third-party-provider", execution.ProviderId);
        Assert.Equal("vendor.adapter-v2", execution.AdapterKind);
    }

    [Fact]
    public void LegacyConstructor_RemainsAvailableAndMapsProviderIdToAdapterKind()
    {
        var profile = new AiGenerationProviderProfile(
            "profile-1",
            "Legacy Source",
            "openai-compatible",
            "image-model",
            "credential",
            "https://relay.example/v1/");

        Assert.Equal("openai-compatible", profile.ProviderId);
        Assert.Equal("openai-compatible", profile.AdapterKind);
        Assert.NotNull(typeof(AiGenerationProviderProfile).GetConstructor(new[]
        {
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(AiGenerationAdapterCapabilities?),
        }));
    }

    [Fact]
    public void ConnectionModelResolver_IgnoresLegacyProviderAndModelMetadata()
    {
        var legacyConnection = new AgasAiProfile(
            "profile-1",
            "Legacy Connection",
            "third-party-provider",
            "legacy-per-connection-model",
            "https://relay.example/v1/",
            "credential");

        using var models = new ImageModelDiscoveryFixture();
        models.Prime(legacyConnection);
        AiGenerationProviderProfile resolved = AgasAiConnectionModelResolver.Resolve(
            legacyConnection,
            "GPT-IMAGE-2.5-FLARE", models.Discovery);

        Assert.Equal(AgasAiProviderIds.AxonCatNewApi, resolved.ProviderId);
        Assert.Equal(AgasAiUnifiedModelCatalog.ConnectionAdapterKind, resolved.AdapterKind);
        Assert.Equal("gpt-image-2.5-flare", resolved.ModelName);
        Assert.Equal(new Uri("https://relay.example/v1/"), resolved.BaseUri);
        Assert.Equal("credential", resolved.ApiKey);
    }
}
