using System.Net.Http;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Features.Ai.UI;
using Xunit;

namespace AppAgasCs.Tests.Core.Ai;

public sealed class AgasAiProviderCatalogTests
{
    [Fact]
    public void Load_SeedsBuiltInsAndAcceptsThirdPartyDescriptorWithoutCoreRegistration()
    {
        using var home = new TemporaryHome();
        AgasAiProviderCatalog initial = AgasAiProviderCatalog.Load(home.Path);
        Assert.Contains(initial.Providers, provider => provider.Id == AgasAiProviderIds.OpenAiCompatible);
        Assert.DoesNotContain(initial.Providers, provider => provider.Id == AgasAiProviderIds.GeminiCompatible);

        WriteProvider(home.Path, "05-third-party.json", ProviderJson(
            "third-party-relay", "Third Party Relay", "openai-compatible", 5));
        AgasAiProviderCatalog reloaded = AgasAiProviderCatalog.Load(home.Path);

        AgasAiProviderDescriptor provider = reloaded.GetRequired("third-party-relay");
        Assert.Equal("Third Party Relay", provider.DisplayName);
        Assert.Equal("openai-compatible", provider.AdapterKind);
        Assert.Equal("third-model", provider.DefaultModelName);
        Assert.Equal("third-party-relay", reloaded.Providers[0].Id);
        Assert.Empty(reloaded.Errors);
    }

    [Fact]
    public void Load_ReflectsProviderDirectoryFileAddEditAndRemoveOnEachSnapshot()
    {
        using var home = new TemporaryHome();
        _ = AgasAiProviderCatalog.Load(home.Path);
        string path = WriteProvider(home.Path, "05-hot-reload.json", ProviderJson(
            "hot-reload", "Hot Reload V1", "openai-compatible", 5));

        AgasAiProviderCatalog added = AgasAiProviderCatalog.Load(home.Path);
        Assert.Equal("Hot Reload V1", added.GetRequired("hot-reload").DisplayName);

        File.WriteAllText(path, ProviderJson("hot-reload", "Hot Reload V2", "openai-compatible", 5));
        AgasAiProviderCatalog edited = AgasAiProviderCatalog.Load(home.Path);
        Assert.Equal("Hot Reload V2", edited.GetRequired("hot-reload").DisplayName);

        File.Delete(path);
        AgasAiProviderCatalog removed = AgasAiProviderCatalog.Load(home.Path);
        Assert.False(removed.TryGet("hot-reload", out _));
        Assert.Contains(removed.Providers, provider => provider.Id == AgasAiProviderIds.OpenAiCompatible);
    }

    [Fact]
    public void Load_DefaultSeedingNeverOverwritesAUserEditedProviderFile()
    {
        using var home = new TemporaryHome();
        AgasAiProviderCatalog initial = AgasAiProviderCatalog.Load(home.Path);
        string openAiPath = Path.Combine(
            AgasAiSettingsPaths.ProvidersDirectory(home.Path),
            "10-openai-compatible.json");
        Assert.True(File.Exists(openAiPath));

        string edited = File.ReadAllText(openAiPath).Replace(
            "GPT Image / OpenAI Compatible",
            "User Edited OpenAI Relay",
            StringComparison.Ordinal);
        File.WriteAllText(openAiPath, edited);
        AgasAiProviderCatalog reloaded = AgasAiProviderCatalog.Load(home.Path);

        Assert.Equal("User Edited OpenAI Relay", reloaded.GetRequired(AgasAiProviderIds.OpenAiCompatible).DisplayName);
        Assert.Equal(initial.Providers.Count, reloaded.Providers.Count);
    }

    [Fact]
    public void Load_IncrementallyMergesBuiltInModelsWithoutOverwritingUserProviderFields()
    {
        using var home = new TemporaryHome();
        _ = AgasAiProviderCatalog.Load(home.Path);
        string path = Path.Combine(
            AgasAiSettingsPaths.ProvidersDirectory(home.Path),
            "10-openai-compatible.json");
        File.WriteAllText(path, """
        {
          "schemaVersion": 1,
          "id": "openai-compatible",
          "displayName": "User Relay",
          "adapterKind": "openai-compatible",
          "defaultBaseUrl": "https://relay.example/v1/",
          "models": [
            { "modelName": "user-image-model", "label": "User Model", "recommended": true }
          ],
          "capabilities": ["textToImage", "referenceEdit", "regionEdit", "nativeAlphaMask"],
          "credential": { "kind": "profileSettings", "label": "Relay Key", "settingsKey": "apiKey" },
          "baseUrlHint": "User hint",
          "baseUrlPlaceholder": "https://relay.example/v1/",
          "order": 77
        }
        """);
        string userFile = File.ReadAllText(path);

        AgasAiProviderDescriptor merged = AgasAiProviderCatalog.Load(home.Path)
            .GetRequired(AgasAiProviderIds.OpenAiCompatible);
        AgasAiProviderDescriptor reloaded = AgasAiProviderCatalog.Load(home.Path)
            .GetRequired(AgasAiProviderIds.OpenAiCompatible);

        Assert.Equal("User Relay", merged.DisplayName);
        Assert.Equal("https://relay.example/v1/", merged.DefaultBaseUrl);
        Assert.Equal(new[] { "textToImage", "referenceEdit", "regionEdit", "nativeAlphaMask" }, merged.Capabilities);
        Assert.Equal("Relay Key", merged.Credential.Label);
        Assert.Equal("user-image-model", merged.DefaultModelName);
        Assert.Contains(merged.Models, model => model.ModelName == "user-image-model");
        Assert.Contains(merged.Models, model => model.ModelName == "gpt-image-2.5-flare");
        Assert.Contains(merged.Models, model => model.ModelName == "gpt-image-2.5-sunburst");
        Assert.Equal(merged.Models, reloaded.Models);
        Assert.Equal(merged.Models.Count, merged.Models
            .Select(model => model.ModelName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count());
        Assert.Equal(userFile, File.ReadAllText(path));
    }

    [Fact]
    public void Load_DoesNotUseBuiltInModelsToMakeAnInvalidProviderValid()
    {
        using var home = new TemporaryHome();
        string directory = AgasAiSettingsPaths.ProvidersDirectory(home.Path);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ".agas-provider-schema-1"), "1\n");
        string path = Path.Combine(directory, "user-openai.json");
        File.WriteAllText(path, """
        {
          "schemaVersion": 1,
          "id": "openai-compatible",
          "displayName": "Empty User Relay",
          "adapterKind": "openai-compatible",
          "defaultBaseUrl": "https://relay.example/v1/",
          "models": [],
          "capabilities": ["textToImage"],
          "credential": { "kind": "profileSettings", "label": "API Key", "settingsKey": "apiKey" },
          "baseUrlPlaceholder": "https://relay.example/v1/"
        }
        """);

        AgasAiProviderCatalog catalog = AgasAiProviderCatalog.Load(home.Path);

        Assert.False(catalog.TryGet(AgasAiProviderIds.OpenAiCompatible, out _));
        Assert.Contains(catalog.Errors, error => error.FileName == "user-openai.json");
        Assert.False(File.Exists(Path.Combine(directory, "10-openai-compatible.json")));
        Assert.Contains("\"models\": []", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("vendor.custom", "/v1/")]
    [InlineData("openai-compatible", "/v2/")]
    public void Load_DoesNotOverlayBuiltInModelsOnAnIncompatibleRequestContract(
        string adapterKind,
        string endpointPath)
    {
        using var home = new TemporaryHome();
        _ = AgasAiProviderCatalog.Load(home.Path);
        string path = Path.Combine(
            AgasAiSettingsPaths.ProvidersDirectory(home.Path),
            "10-openai-compatible.json");
        File.WriteAllText(path, $$"""
        {
          "schemaVersion": 1,
          "id": "openai-compatible",
          "displayName": "Custom Contract",
          "adapterKind": "{{adapterKind}}",
          "defaultBaseUrl": "https://relay.example{{endpointPath}}",
          "models": [
            { "modelName": "user-image-model", "label": "User Model", "recommended": true }
          ],
          "capabilities": ["textToImage", "referenceEdit", "regionEdit", "nativeAlphaMask"],
          "credential": { "kind": "profileSettings", "label": "API Key", "settingsKey": "apiKey" },
          "baseUrlPlaceholder": "https://relay.example{{endpointPath}}"
        }
        """);

        AgasAiProviderDescriptor provider = AgasAiProviderCatalog.Load(home.Path)
            .GetRequired(AgasAiProviderIds.OpenAiCompatible);

        Assert.Single(provider.Models);
        Assert.Equal("user-image-model", provider.DefaultModelName);
        Assert.DoesNotContain(provider.Models, model => model.ModelName == "gpt-image-2.5-flare");
    }

    [Fact]
    public void LoadFromDirectory_IsAnExactTestOverrideAndDoesNotSeedBuiltIns()
    {
        using var home = new TemporaryHome();
        string directory = Path.Combine(home.Path, "isolated-providers");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "provider.json"),
            ProviderJson("isolated-relay", "Isolated", "openai-compatible", 1));

        AgasAiProviderCatalog catalog = AgasAiProviderCatalog.LoadFromDirectory(directory);

        Assert.Equal("isolated-relay", Assert.Single(catalog.Providers).Id);
        Assert.Empty(catalog.Errors);
    }

    [Fact]
    public void Load_IsolatesBadSecretUnsafeEndpointAndEveryDuplicateWhileKeepingValidFiles()
    {
        using var home = new TemporaryHome();
        _ = AgasAiProviderCatalog.Load(home.Path);
        WriteProvider(home.Path, "30-valid.json", ProviderJson("valid-relay", "Valid", "openai-compatible", 30));
        WriteProvider(home.Path, "31-duplicate-a.json", ProviderJson("duplicate-relay", "A", "openai-compatible", 31));
        WriteProvider(home.Path, "32-duplicate-b.json", ProviderJson("duplicate-relay", "B", "openai-compatible", 32));
        WriteProvider(home.Path, "33-secret.json", ProviderJson("secret-relay", "Secret", "openai-compatible", 33)
            .Replace("\"order\": 33", "\"apiKey\": \"must-not-load\", \"order\": 33", StringComparison.Ordinal));
        WriteProvider(home.Path, "34-http.json", ProviderJson("http-relay", "HTTP", "openai-compatible", 34)
            .Replace("https://relay.example/v1/", "http://relay.example/v1/", StringComparison.Ordinal));

        AgasAiProviderCatalog catalog = AgasAiProviderCatalog.Load(home.Path);

        Assert.True(catalog.TryGet("valid-relay", out _));
        Assert.False(catalog.TryGet("duplicate-relay", out _));
        Assert.False(catalog.TryGet("secret-relay", out _));
        Assert.False(catalog.TryGet("http-relay", out _));
        Assert.Equal(4, catalog.Errors.Count);
        Assert.Equal(2, catalog.Errors.Count(error => error.Message.Contains("重复", StringComparison.Ordinal)));
        Assert.DoesNotContain(catalog.Errors, error => error.Message.Contains("must-not-load", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_IsolatesNullProviderIdBeforeAttemptingBuiltInModelMerge()
    {
        using var home = new TemporaryHome();
        _ = AgasAiProviderCatalog.Load(home.Path);
        WriteProvider(home.Path, "35-null-id.json", ProviderJson(
            "null-id", "Null ID", "openai-compatible", 35)
            .Replace("\"id\": \"null-id\"", "\"id\": null", StringComparison.Ordinal));

        AgasAiProviderCatalog catalog = AgasAiProviderCatalog.Load(home.Path);

        Assert.True(catalog.TryGet(AgasAiProviderIds.OpenAiCompatible, out _));
        AgasAiProviderConfigurationError error = Assert.Single(
            catalog.Errors, item => item.FileName == "35-null-id.json");
        Assert.Contains("Provider id", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_IsolatesNullModelEntriesWithoutHidingValidProviders()
    {
        using var home = new TemporaryHome();
        _ = AgasAiProviderCatalog.Load(home.Path);
        WriteProvider(home.Path, "36-null-model.json", ProviderJson(
            "null-model", "Null Model", "openai-compatible", 36)
            .Replace(
                "{ \"modelName\": \"third-model\", \"label\": \"Third Model\", \"recommended\": true }",
                "null",
                StringComparison.Ordinal));

        AgasAiProviderCatalog catalog = AgasAiProviderCatalog.Load(home.Path);

        Assert.True(catalog.TryGet(AgasAiProviderIds.OpenAiCompatible, out _));
        AgasAiProviderConfigurationError error = Assert.Single(
            catalog.Errors, item => item.FileName == "36-null-model.json");
        Assert.Contains("models", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_IsolatesCredentialLabelsThatCannotBeSafelyPresented()
    {
        using var home = new TemporaryHome();
        string directory = Path.Combine(home.Path, "credential-label");
        Directory.CreateDirectory(directory);
        string oversized = new('x', 121);
        File.WriteAllText(
            Path.Combine(directory, "oversized.json"),
            ProviderJson("oversized-label", "Oversized", "openai-compatible", 1)
                .Replace("\"label\": \"API Key\"", $"\"label\": \"{oversized}\"", StringComparison.Ordinal));

        AgasAiProviderCatalog catalog = AgasAiProviderCatalog.LoadFromDirectory(directory);

        Assert.Empty(catalog.Providers);
        Assert.Single(catalog.Errors);
    }

    [Fact]
    public void CredentialPresentation_UsesDescriptorLabelAndSafeFallbackWithoutSecretValue()
    {
        using var home = new TemporaryHome();
        string directory = Path.Combine(home.Path, "credential-presentation");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "vendor.json"),
            ProviderJson("vendor-token", "Vendor", "openai-compatible", 1)
                .Replace("\"label\": \"API Key\"", "\"label\": \"Vendor Token\"", StringComparison.Ordinal));
        AgasAiProviderDescriptor provider = AgasAiProviderCatalog.LoadFromDirectory(directory).GetRequired("vendor-token");

        AiCredentialPresentation empty = AiCredentialPresentation.Create(provider, false, string.Empty);
        AiCredentialPresentation saved = AiCredentialPresentation.Create(provider, true, "abc********xyz");
        AiCredentialPresentation fallback = AiCredentialPresentation.Create(null, false, string.Empty);

        Assert.Equal("Vendor Token", empty.Label);
        Assert.Equal("输入 Vendor Token", empty.Placeholder);
        Assert.Equal("未保存 Vendor Token。", empty.Hint);
        Assert.Equal("已保存：abc********xyz", saved.Hint);
        Assert.Equal("API Key", fallback.Label);
        Assert.DoesNotContain("secret", saved.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AdapterRegistry_ResolvesCustomStringKindAndIsolatesBadPluginDll()
    {
        using var home = new TemporaryHome();
        var registry = new AiImageGenerationAdapterRegistry(new[] { new ThirdPartyAdapter() });
        Assert.IsType<ThirdPartyAdapter>(registry.Resolve("vendor.custom-v1"));

        string plugins = Path.Combine(home.Path, "plugins");
        Directory.CreateDirectory(plugins);
        File.WriteAllText(Path.Combine(plugins, "broken.dll"), "not an assembly");
        AiImageGenerationAdapterRegistry defaults = AiImageGenerationAdapterRegistry.CreateDefault(plugins);
        Assert.NotNull(defaults.Resolve("openai-compatible"));
        Assert.Single(defaults.Errors);
        Assert.Equal("broken.dll", defaults.Errors[0].FileName);
    }

    [Fact]
    public void AdapterRegistry_LoadsARealThirdPartyAdapterDllFromTheConfiguredDirectory()
    {
        using var home = new TemporaryHome();
        string plugins = AgasAiSettingsPaths.AdaptersDirectory(home.Path);
        Directory.CreateDirectory(plugins);
        string sourceAssembly = typeof(ThirdPartyPluginAdapter).Assembly.Location;
        string pluginAssembly = Path.Combine(plugins, "Vendor.AgasAdapter.dll");
        File.Copy(sourceAssembly, pluginAssembly);

        AiImageGenerationAdapterRegistry registry = AiImageGenerationAdapterRegistry.CreateDefault(plugins);

        IAiImageGenerationAdapter loaded = registry.Resolve(ThirdPartyPluginAdapter.Kind);
        Assert.Equal(ThirdPartyPluginAdapter.Kind, loaded.AdapterKind);
        Assert.Equal("Vendor.AgasAdapter.dll", Path.GetFileName(loaded.GetType().Assembly.Location));
        Assert.Empty(registry.Errors);
    }

    [Fact]
    public void AdapterRegistry_LoadsCleanContractSdkFixtureAndTreatsManagedDependenciesAsSidecars()
    {
        using var home = new TemporaryHome();
        string plugins = AgasAiSettingsPaths.AdaptersDirectory(home.Path);
        Directory.CreateDirectory(plugins);
        string fixtures = Path.Combine(AppContext.BaseDirectory, "ai-adapter-fixtures");
        Assert.True(Directory.Exists(fixtures), "The independently built Adapter SDK fixture output is missing.");
        File.Copy(Path.Combine(fixtures, "Vendor.AgasAdapter.dll"), Path.Combine(plugins, "10-vendor.dll"));
        File.Copy(Path.Combine(fixtures, "Vendor.AgasAdapter.deps.json"), Path.Combine(plugins, "10-vendor.deps.json"));
        File.Copy(Path.Combine(fixtures, "Vendor.Adapter.Dependency.dll"), Path.Combine(plugins, "Vendor.Adapter.Dependency.dll"));
        File.WriteAllText(Path.Combine(plugins, "Vendor.Native.Sidecar.dll"), "native fixture bytes");
        File.WriteAllText(Path.Combine(plugins, "11-native-sidecar.deps.json"), NativeSidecarDepsJson);
        File.WriteAllText(Path.Combine(plugins, "80-broken.dll"), "not an assembly");
        File.Copy(Path.Combine(fixtures, "Vendor.AgasAdapter.Conflict.dll"), Path.Combine(plugins, "90-conflict.dll"));

        AiImageGenerationAdapterRegistry registry = AiImageGenerationAdapterRegistry.CreateDefault(plugins);

        Assert.Equal("10-vendor.dll", Path.GetFileName(
            registry.Resolve("vendor.fixture-basic-v1").GetType().Assembly.Location));
        IAiImageGenerationAdapter dependent = registry.Resolve("vendor.fixture-dependent-v1");
        Assert.Equal("10-vendor.dll", Path.GetFileName(dependent.GetType().Assembly.Location));
        System.Runtime.Loader.AssemblyLoadContext context =
            System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(dependent.GetType().Assembly)!;
        System.Reflection.Assembly dependency = context.Assemblies.Single(assembly =>
            assembly.GetName().Name == "Vendor.Adapter.Dependency");
        Assert.Equal(new Version(7, 3, 1, 0), dependency.GetName().Version);
        Assert.Collection(
            registry.Errors,
            error => Assert.Equal("80-broken.dll", error.FileName),
            error => Assert.Equal("90-conflict.dll", error.FileName));
        Assert.DoesNotContain(registry.Errors, error => error.FileName == "Vendor.Adapter.Dependency.dll");
        Assert.DoesNotContain(registry.Errors, error => error.FileName == "Vendor.Native.Sidecar.dll");
    }

    [Fact]
    public void AdapterRegistry_LoadsDllsDeterministicallyAndIsolatesLaterKindConflict()
    {
        using var home = new TemporaryHome();
        string plugins = AgasAiSettingsPaths.AdaptersDirectory(home.Path);
        Directory.CreateDirectory(plugins);
        string sourceAssembly = typeof(ThirdPartyPluginAdapter).Assembly.Location;
        File.Copy(sourceAssembly, Path.Combine(plugins, "10-first.dll"));
        File.Copy(sourceAssembly, Path.Combine(plugins, "20-conflict.dll"));

        AiImageGenerationAdapterRegistry registry = AiImageGenerationAdapterRegistry.CreateDefault(plugins);

        Assert.Equal("10-first.dll", Path.GetFileName(registry.Resolve(ThirdPartyPluginAdapter.Kind).GetType().Assembly.Location));
        AiAdapterPluginLoadError conflict = Assert.Single(registry.Errors);
        Assert.Equal("20-conflict.dll", conflict.FileName);
        Assert.Contains("重复", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdapterRegistry_RejectsKindsThatProviderJsonCouldNeverReference()
    {
        Assert.Throws<ArgumentException>(() =>
            new AiImageGenerationAdapterRegistry(new[] { new InvalidKindAdapter() }));
    }

    [Fact]
    public void PackagedSdkDocumentsProviderAndAdapterDropInWorkflow()
    {
        string sdkDirectory = Path.Combine(AppContext.BaseDirectory, "ai-provider-sdk");
        string schemaPath = Path.Combine(sdkDirectory, "ai-provider.schema.json");
        string readmePath = Path.Combine(sdkDirectory, "README.md");

        Assert.True(File.Exists(schemaPath));
        Assert.True(File.Exists(readmePath));
        string readme = File.ReadAllText(readmePath);
        Assert.Contains("$AGAS_HOME/providers/30-company-relay.json", readme, StringComparison.Ordinal);
        Assert.Contains("IAiImageGenerationAdapter", readme, StringComparison.Ordinal);
        Assert.Contains("$AGAS_HOME/ai-adapters", readme, StringComparison.Ordinal);
        Assert.Contains("同一 `id`", readme, StringComparison.Ordinal);
        Assert.Contains("Profile v1/v2", readme, StringComparison.Ordinal);
        string productionSdkDirectory = Path.Combine(
            AppAgasCs.Tests.Core.ProjectRoot.Path,
            ".godot",
            "mono",
            "temp",
            "bin",
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            "ai-provider-sdk");
        string contractAssemblyPath = Path.Combine(productionSdkDirectory, "APP-AGAS-CS.AiAdapter.Contracts.dll");
        string contractPackagePath = Path.Combine(productionSdkDirectory, "APP-AGAS-CS.AiAdapter.Contracts.1.0.0.nupkg");
        Assert.True(File.Exists(contractAssemblyPath));
        Assert.True(File.Exists(contractPackagePath));
        Assert.Contains("APP-AGAS-CS.AiAdapter.Contracts", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("<HintPath>/path/to/agas/APP-AGAS-CS.dll", readme, StringComparison.Ordinal);

        System.Reflection.Assembly contracts = typeof(IAiImageGenerationAdapter).Assembly;
        Assert.Equal("APP-AGAS-CS.AiAdapter.Contracts", contracts.GetName().Name);
        string[] forbiddenDependencies = { "APP-AGAS-CS", "GodotSharp", "Microsoft.Extensions.Hosting" };
        Assert.DoesNotContain(
            contracts.GetReferencedAssemblies(),
            reference => forbiddenDependencies.Contains(reference.Name, StringComparer.Ordinal));
    }

    [Fact]
    public void BuildPipeline_PacksSdkAfterContractsBuildWithoutDuplicateFixtureRoots()
    {
        string projectRoot = AppAgasCs.Tests.Core.ProjectRoot.Path;
        string commonProps = File.ReadAllText(Path.Combine(projectRoot, "APP-AGAS-CS.Common.props"));
        string solution = File.ReadAllText(Path.Combine(projectRoot, "APP-AGAS-CS.sln"));

        Assert.DoesNotContain(
            "<Content Include=\"$(MSBuildThisFileDirectory)sdk/APP-AGAS-CS.AiAdapter.Contracts/bin/",
            commonProps,
            StringComparison.Ordinal);
        Assert.Contains("Name=\"PackAiAdapterContractsForSdkStaging\"", commonProps, StringComparison.Ordinal);
        Assert.Contains("NoBuild=true", commonProps, StringComparison.Ordinal);
        Assert.Contains(
            "DependsOnTargets=\"PackAiAdapterContractsForSdkStaging\"",
            commonProps,
            StringComparison.Ordinal);
        Assert.DoesNotContain("tests\\Fixtures\\AiAdapterPlugin", solution, StringComparison.Ordinal);
        Assert.DoesNotContain("tests\\Fixtures\\AiAdapterConflict", solution, StringComparison.Ordinal);
        Assert.DoesNotContain("tests\\Fixtures\\AiAdapterDependency", solution, StringComparison.Ordinal);
    }

    [Fact]
    public void AdapterContract_RequiresStableStringKindAndHasNoThrowingLegacyModelMember()
    {
        System.Reflection.PropertyInfo property = typeof(IAiImageGenerationAdapter)
            .GetProperty(nameof(IAiImageGenerationAdapter.AdapterKind))
            ?? throw new InvalidOperationException("AdapterKind contract is missing.");

        Assert.True(property.GetMethod?.IsAbstract);
        Assert.Null(typeof(IAiImageGenerationAdapter).GetProperty("ModelType"));
    }

    [Fact]
    public void ProviderContracts_HaveNoModelTypeEnumsOrLegacyConstructors()
    {
        System.Reflection.Assembly assembly = typeof(AgasAiProfile).Assembly;
        Assert.Null(assembly.GetType("AppAgasCs.Features.Ai.Settings.AgasAiModelType"));
        Assert.Null(assembly.GetType("AppAgasCs.Features.Ai.Generation.AiGenerationModelType"));
        Assert.All(
            typeof(AgasAiProfile).GetConstructors(),
            constructor => Assert.Equal(typeof(string), constructor.GetParameters()[2].ParameterType));
        Assert.All(
            typeof(AiGenerationProviderProfile).GetConstructors(),
            constructor => Assert.Equal(typeof(string), constructor.GetParameters()[2].ParameterType));
    }

    [Fact]
    public void ProfilesPersistStableProviderIdWithoutLegacyEnumOrSecret()
    {
        using var home = new TemporaryHome();
        var store = new AgasAiSettingsStore(home.Path);
        var profile = new AgasAiProfile(
            "profile-1", "Third Party", "vendor-provider", "third-model", "https://relay.example/v1/");
        store.SaveProfiles(new AgasAiProfilesDocument(
            AgasAiProfilesDocument.CurrentResourceVersion, profile.Id, new[] { profile }));

        string json = File.ReadAllText(store.ProfilesPath);
        Assert.Contains("\"providerId\": \"vendor-provider\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("modelType", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("vendor-provider", Assert.Single(store.LoadProfiles().Profiles).ProviderId);
    }

    [Fact]
    public async Task Generation_EnforcesDescriptorCapabilitiesInAdditionToAdapterCapabilities()
    {
        using var home = new TemporaryHome();
        using var http = new HttpClient();
        var service = new AiGenerationService(
            http,
            new AiImageGenerationAdapterRegistry(new[] { new ThirdPartyAdapter() }));
        var profile = new AiGenerationProviderProfile(
            "profile-1",
            "Third Party",
            "vendor.custom-v1",
            "third-model",
            "test-key",
            "https://relay.example/v1/",
            AiGenerationAdapterCapabilities.TextToImage);
        AiGenerationReferenceImage reference = AiGenerationReferenceImage.Create(
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYPgPAAEDAQAIicLsAAAAAElFTkSuQmCC"),
            "image/png");
        var request = new AiGenerationRequest(
            home.Path,
            "edit",
            referenceImages: new[] { reference },
            operation: AiGenerationOperation.ReferenceEdit);

        AiGenerationException error = await Assert.ThrowsAsync<AiGenerationException>(
            () => service.GenerateAsync(profile, request));

        Assert.Equal(AiGenerationErrorCode.UnsupportedOperation, error.Code);
    }

    private static string ProviderJson(string id, string displayName, string adapterKind, int order) => $$"""
    {
      "schemaVersion": 1,
      "id": "{{id}}",
      "displayName": "{{displayName}}",
      "adapterKind": "{{adapterKind}}",
      "defaultBaseUrl": "https://relay.example/v1/",
      "models": [
        { "modelName": "third-model", "label": "Third Model", "recommended": true }
      ],
      "capabilities": ["textToImage"],
      "credential": { "kind": "profileSettings", "label": "API Key", "settingsKey": "apiKey" },
      "baseUrlHint": "HTTPS only",
      "order": {{order}}
    }
    """;

    private const string NativeSidecarDepsJson = """
    {
      "runtimeTarget": { "name": ".NETCoreApp,Version=v8.0" },
      "targets": {
        ".NETCoreApp,Version=v8.0": {
          "Vendor.Native.Sidecar/1.0.0": {
            "native": { "runtimes/win-x64/native/Vendor.Native.Sidecar.dll": {} }
          }
        }
      },
      "libraries": {}
    }
    """;

    private static string WriteProvider(string home, string fileName, string json)
    {
        string directory = AgasAiSettingsPaths.ProvidersDirectory(home);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    private sealed class ThirdPartyAdapter : IAiImageGenerationAdapter
    {
        public string AdapterKind => "vendor.custom-v1";

        public Task<GeneratedImageData> GenerateAsync(
            AiGenerationProviderProfile profile,
            AiGenerationRequest request,
            HttpClient providerHttpClient,
            HttpClient imageDownloadClient,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class InvalidKindAdapter : IAiImageGenerationAdapter
    {
        public string AdapterKind => "Vendor Invalid Kind";

        public Task<GeneratedImageData> GenerateAsync(
            AiGenerationProviderProfile profile,
            AiGenerationRequest request,
            HttpClient providerHttpClient,
            HttpClient imageDownloadClient,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TemporaryHome : IDisposable
    {
        public TemporaryHome()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agas-provider-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }
}

/// <summary>
/// Exported from the test assembly so the plugin test exercises a physical DLL,
/// a separate AssemblyLoadContext, host-contract type identity and activation.
/// </summary>
public sealed class ThirdPartyPluginAdapter : IAiImageGenerationAdapter
{
    public const string Kind = "vendor.real-dll-v1";
    public string AdapterKind => Kind;

    public Task<GeneratedImageData> GenerateAsync(
        AiGenerationProviderProfile profile,
        AiGenerationRequest request,
        HttpClient providerHttpClient,
        HttpClient imageDownloadClient,
        CancellationToken cancellationToken) => throw new NotSupportedException();
}
