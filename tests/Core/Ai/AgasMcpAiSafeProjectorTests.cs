using AppAgasCs.Features.Ai.Settings;
using Xunit;

namespace AppAgasCs.Tests.Core.Ai;

public sealed class AgasMcpAiSafeProjectorTests
{
    [Fact]
    public void ExecutionIdentity_ReplacesCredentialBearingIdentifiersAndFreeTextByFieldType()
    {
        const string secret = "exact-secret";

        AgasMcpAiExecutionIdentity identity = AgasMcpAiSafeProjector.ProjectExecutionIdentity(
            "profile-exact-secret",
            "provider-exact-secret",
            "adapter/exact-secret",
            "model-exact-secret",
            new[] { secret });

        Assert.Equal("redacted", identity.ProfileId);
        Assert.Equal("redacted", identity.ProviderId);
        Assert.Equal("[redacted]", identity.AdapterKind);
        Assert.Equal("[redacted]", identity.ModelName);
        Assert.DoesNotContain(secret, identity.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectionDoesNotNeedCredentialRetention_StatusCanUseFrozenSafeIdentityAfterRotation()
    {
        const string oldSecret = "old-secret";
        AgasMcpAiExecutionIdentity frozen = AgasMcpAiSafeProjector.ProjectExecutionIdentity(
            "profile-1", "provider-1", "adapter", $"alias-{oldSecret}", new[] { oldSecret });

        AgasMcpAiExecutionIdentity laterProjection = AgasMcpAiSafeProjector.ProjectExecutionIdentity(
            frozen.ProfileId,
            frozen.ProviderId,
            frozen.AdapterKind,
            frozen.ModelName,
            new[] { "rotated-secret" });

        Assert.Equal(frozen, laterProjection);
        Assert.DoesNotContain(oldSecret, laterProjection.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("valid-id", "valid-id")]
    [InlineData("vendor.provider", "redacted")]
    [InlineData("", "redacted")]
    public void SafeIdentifier_EnforcesFrozenWireAlphabet(string input, string expected) =>
        Assert.Equal(expected, AgasMcpAiSafeProjector.SafeIdentifier(input, Array.Empty<string>()));

    [Fact]
    public void ProfileQueriesAndDiagnostics_UseOneSafeProjectionAndCanonicalCapabilityOrder()
    {
        string root = Path.Combine(Path.GetTempPath(), "agas-safe-profile-query", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string providersPath = AgasAiSettingsPaths.ProvidersDirectory(root);
            Directory.CreateDirectory(providersPath);
            File.WriteAllText(Path.Combine(providersPath, "vendor.json"), """
            {
              "schemaVersion": 1,
              "id": "vendor.provider",
              "displayName": "Provider super-secret",
              "adapterKind": "vendor-adapter",
              "defaultBaseUrl": "https://relay.example/v1/",
              "models": [{"modelName":"model-safe","label":"Label super-secret","recommended":true}],
              "capabilities": ["guidedRegionEdit","textToImage","nativeAlphaMask","regionEdit"],
              "credential": {"kind":"profileSettings","label":"API Key","settingsKey":"apiKey"}
            }
            """);
            var store = new AgasAiSettingsStore(root);
            var profile = new AgasAiProfile(
                "profile-1",
                "Relay super-secret",
                "vendor.provider",
                "alias-super-secret",
                "https://private.example/v1/");
            store.SaveProfiles(new AgasAiProfilesDocument(3, profile.Id, new[] { profile }));
            store.SetCredential(profile.Id, "super-secret");
            Func<AgasAiProviderCatalog> catalog = () => AgasAiProviderCatalog.LoadFromDirectory(providersPath);
            using var models = new ImageModelDiscoveryFixture();
            models.Prime(store);
            var service = new AgasAiProfileSettingsService(store, catalog, models.Discovery);

            AgasAiSafeProfileDescriptor row = Assert.Single(service.ListProfiles());
            AgasAiSafeProviderDescriptor provider = Assert.Single(service.ListProviders());
            AgasAiConfigurationDiagnostic diagnostic = service.Diagnose();

            Assert.Equal(AgasAiProviderIds.AxonCatNewApi, row.ProviderId);
            Assert.Equal(AgasAiProviderIds.AxonCatNewApi, provider.ProviderId);
            Assert.Equal(AgasAiProviderIds.AxonCatNewApi, diagnostic.ProviderId);
            Assert.Equal("[redacted]", row.Name);
            Assert.Equal("AxonCat New API", row.ProviderDisplayName);
            Assert.Equal("[redacted]", row.ModelName);
            Assert.Equal("AxonCat New API", provider.DisplayName);
            Assert.Equal(20, provider.Models.Count);
            Assert.False(provider.SupportsCustomModel);
            Assert.Empty(row.Capabilities);
            Assert.False(diagnostic.NetworkChecked);
            Assert.False(diagnostic.Ready);
            Assert.Equal(new[] { "MODEL_UNSUPPORTED" }, diagnostic.IssueCodes);
            Assert.Equal("[redacted]", diagnostic.ModelName);
            Assert.Null(typeof(AgasAiSafeProfileDescriptor).GetProperty("BaseUrl"));
            Assert.Null(typeof(AgasAiSafeProviderDescriptor).GetProperty("Credential"));
            Assert.DoesNotContain("super-secret", row.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret", provider.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret", diagnostic.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }
}
