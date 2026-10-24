using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Contracts.Output;
using AppAgasCs.Mcp.Contracts.Runtime;
using AppAgasCs.Mcp.Runtime;
using System.Text.Json;
using Xunit;

namespace AppAgasCs.Tests.Core.Mcp;

public sealed class AgasMcpWave23RegistryParityTests
{
    [Fact]
    public void Registry_IsExactly30_WithFrozenOrderOutputVariantsTimeoutsAndPolicies()
    {
        Assert.Equal(30, AgasMcpToolNames.All.Count);
        Assert.Equal(30, AgasMcpToolDescriptorRegistry.All.Count);
        Assert.Equal(30, AgasMcpFrozenOutputManifest.V1.Tools.Count);
        Assert.Equal(AgasMcpToolNames.All, AgasMcpToolDescriptorRegistry.All.Select(item => item.Name));
        Assert.Equal(AgasMcpToolNames.All, AgasMcpFrozenOutputManifest.V1.Tools.Select(item => item.ToolName));
        Assert.Equal(30, AgasMcpToolNames.All.Distinct(StringComparer.Ordinal).Count());

        string[] heavy =
        [
            AgasMcpToolNames.AssetsImport,
            AgasMcpToolNames.ScenePackageImport,
            AgasMcpToolNames.ScenePackageExport,
        ];
        Assert.Equal(heavy.Order(StringComparer.Ordinal), AgasMcpToolDescriptorRegistry.All
            .Where(item => item.Timeout == TimeSpan.FromSeconds(120))
            .Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.All(AgasMcpToolDescriptorRegistry.All.Where(item => !heavy.Contains(item.Name, StringComparer.Ordinal)),
            item => Assert.Equal(TimeSpan.FromSeconds(30), item.Timeout));

        AssertPolicy(AgasMcpCommitPolicy.StartedMainThreadCompletion,
            AgasMcpToolNames.ProjectOpen,
            AgasMcpToolNames.ProjectCreate,
            AgasMcpToolNames.ProjectClose,
            AgasMcpToolNames.ProjectSave,
            AgasMcpToolNames.ScenePackageImport);
        AssertPolicy(AgasMcpCommitPolicy.ShortAtomicCommit,
            AgasMcpToolNames.AiProfileActivate,
            AgasMcpToolNames.AiModelSet,
            AgasMcpToolNames.AssetsImport,
            AgasMcpToolNames.ScenePackageExport);

        foreach ((AgasMcpToolDescriptor descriptor, AgasMcpFrozenOutputTool frozen) in
                 AgasMcpToolDescriptorRegistry.All.Zip(AgasMcpFrozenOutputManifest.V1.Tools))
        {
            Assert.Equal(frozen.SuccessVariants.Select(shape => shape.Name),
                descriptor.OutputContract!.SuccessDataTypes.Select(type => type.Name));
            Assert.Equal(frozen.FailureDataVariants.Select(shape => shape.Name),
                descriptor.OutputContract.FailureDataTypes.Select(type => type.Name));
            Assert.Equal(frozen.AllowsFailureWithoutData, descriptor.OutputContract.AllowsFailureWithoutData);
            Assert.Equal("https://json-schema.org/draft/2020-12/schema",
                descriptor.OutputContract.OutputSchema.GetProperty("$schema").GetString());
            Assert.Equal("object", descriptor.OutputContract.OutputSchema.GetProperty("type").GetString());
            AssertRootFieldParity(descriptor, frozen);
        }
    }

    [Fact]
    public void Registry_FreezesAnnotationsAndDispatchExactly()
    {
        string[] openWorld = [AgasMcpToolNames.AiGenerate];
        Assert.Equal(openWorld.Order(StringComparer.Ordinal), AgasMcpToolDescriptorRegistry.All
            .Where(item => item.OpenWorldHint).Select(item => item.Name).Order(StringComparer.Ordinal));
        string[] orchestrated =
        [
            AgasMcpToolNames.ProjectOpen, AgasMcpToolNames.ProjectCreate, AgasMcpToolNames.ProjectClose,
            AgasMcpToolNames.ImportsList, AgasMcpToolNames.AssetsImport, AgasMcpToolNames.SceneGet,
            AgasMcpToolNames.ScenePackageImport, AgasMcpToolNames.ScenePackageExport, AgasMcpToolNames.ProjectSave,
            AgasMcpToolNames.AiProfilesList, AgasMcpToolNames.AiModelsList, AgasMcpToolNames.AiProfileActivate,
            AgasMcpToolNames.AiModelSet, AgasMcpToolNames.AiDiagnostics,
        ];
        Assert.Equal(orchestrated.Order(StringComparer.Ordinal), AgasMcpToolDescriptorRegistry.All
            .Where(item => item.DispatchMode == AgasMcpToolDispatchMode.AsyncOrchestrated)
            .Select(item => item.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AiSucceeded_EnforcesAppliedMetadataConditionInSchemaConstructorAndRuntime()
    {
        AgasMcpToolDescriptor status = AgasMcpToolDescriptorRegistry.Get(AgasMcpToolNames.AiStatus);
        JsonElement succeededSchema = status.OutputContract!.OutputSchema.GetProperty("oneOf")[0]
            .GetProperty("properties").GetProperty("data").GetProperty("oneOf")[0]
            .GetProperty("oneOf")[2];
        Assert.Equal(2, succeededSchema.GetProperty("allOf").GetArrayLength());

        DateTimeOffset now = new(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
        var identity = new AgasMcpAiExecutionIdentityDto("profile-1", "vendor.provider", "adapter", "model");
        var unapplied = new AgasMcpAiJobSucceededDto(
            "aijob-0123456789abcdef0123456789abcdef", "Succeeded", "TextToImage", identity,
            now, false, now, now, "assets/generated/result.png", "done");
        var applied = new AgasMcpAiJobSucceededDto(
            "aijob-0123456789abcdef0123456789abcdef", "Succeeded", "TextToImage", identity,
            now, true, now, now, "assets/generated/result.png", "done",
            appliedRelativePath: "assets/generated/applied.png", appliedAtUtc: now);
        Assert.True(AgasMcpOutputContractRegistry.Validate(
            status.OutputContract,
            AgasMcpToolResponse.Success(false, false, unapplied)).Success);
        Assert.True(AgasMcpOutputContractRegistry.Validate(
            status.OutputContract,
            AgasMcpToolResponse.Success(false, false, applied)).Success);
        Assert.Throws<ArgumentException>(() => new AgasMcpAiJobSucceededDto(
            "aijob-0123456789abcdef0123456789abcdef", "Succeeded", "TextToImage", identity,
            now, true, now, now, "assets/generated/result.png", "done"));
        Assert.Throws<ArgumentException>(() => new AgasMcpAiJobSucceededDto(
            "aijob-0123456789abcdef0123456789abcdef", "Succeeded", "TextToImage", identity,
            now, false, now, now, "assets/generated/result.png", "done",
            appliedRelativePath: "assets/generated/applied.png", appliedAtUtc: now));
    }

    private static void AssertPolicy(AgasMcpCommitPolicy policy, params string[] expected)
    {
        Assert.Equal(expected.Order(StringComparer.Ordinal), AgasMcpToolDescriptorRegistry.All
            .Where(item => item.CommitPolicy == policy).Select(item => item.Name).Order(StringComparer.Ordinal));
    }

    private static void AssertRootFieldParity(
        AgasMcpToolDescriptor descriptor,
        AgasMcpFrozenOutputTool frozen)
    {
        JsonElement dataSchema = descriptor.OutputContract!.OutputSchema.GetProperty("oneOf")[0]
            .GetProperty("properties").GetProperty("data");
        JsonElement[] actualVariants = descriptor.OutputContract.SuccessDataTypes.Count == 1
            ? [dataSchema]
            : dataSchema.GetProperty("oneOf").EnumerateArray().ToArray();
        Assert.Equal(frozen.SuccessVariants.Count, actualVariants.Length);
        for (int index = 0; index < actualVariants.Length; index++)
        {
            AgasMcpFrozenOutputShape expected = frozen.SuccessVariants[index];
            if (expected.UnionVariants.Count > 0) continue;
            JsonElement actual = actualVariants[index];
            Assert.Equal(expected.Fields.Select(field => field.Name),
                actual.GetProperty("properties").EnumerateObject().Select(property => property.Name));
            Assert.Equal(expected.Fields.Where(field => field.Required).Select(field => field.Name),
                actual.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        }
    }
}
