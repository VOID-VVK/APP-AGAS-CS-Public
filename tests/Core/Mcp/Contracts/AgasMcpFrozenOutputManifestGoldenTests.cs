using System;
using System.Collections.Generic;
using System.Linq;
using AppAgasCs.Mcp.Contracts.Output;
using Xunit;

namespace AppAgasCs.Tests.Core.Mcp.Contracts;

public sealed class AgasMcpFrozenOutputManifestGoldenTests
{
    private static readonly string[] ExpectedToolNames =
    {
        "agas.projects.recent",
        "agas.project.open",
        "agas.project.create",
        "agas.project.close",
        "agas.project.snapshot",
        "agas.assets.list",
        "agas.imports.list",
        "agas.assets.import",
        "agas.scene.get",
        "agas.scene.mutate",
        "agas.scene.package.import",
        "agas.scene.package.export",
        "agas.grid.mutate",
        "agas.layer.mutate",
        "agas.layer.mergeVisible",
        "agas.image.mutate",
        "agas.prompt.mutate",
        "agas.view.set",
        "agas.project.save",
        "agas.selection.set",
        "agas.selection.get",
        "agas.ai.profiles.list",
        "agas.ai.models.list",
        "agas.ai.profile.activate",
        "agas.ai.model.set",
        "agas.ai.diagnostics",
        "agas.ai.generate",
        "agas.ai.status",
        "agas.ai.cancel",
        "agas.ai.apply",
    };

    [Fact]
    public void V1_IsIndependentCompleteOrderedThirtyToolOracle()
    {
        AgasMcpFrozenOutputManifestVersion manifest = AgasMcpFrozenOutputManifest.V1;

        Assert.Equal("v1", manifest.Version);
        Assert.Equal(ExpectedToolNames, manifest.Tools.Select(tool => tool.ToolName));
        Assert.Equal(30, manifest.Tools.Select(tool => tool.ToolName).Distinct(StringComparer.Ordinal).Count());
        Assert.All(manifest.Tools, tool => Assert.NotEmpty(tool.SuccessVariants));
        Assert.All(manifest.SharedShapes, shape => Assert.True(shape.Closed));
        Assert.All(manifest.Tools.SelectMany(tool => tool.SuccessVariants), shape => Assert.True(shape.Closed));
        Assert.All(manifest.Tools.SelectMany(tool => tool.FailureDataVariants), shape => Assert.True(shape.Closed));
        Assert.All(
            manifest.Tools.SelectMany(tool => tool.SuccessVariants),
            variant => Assert.Equal(variant.Fields.Count, variant.Fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count()));
    }

    [Fact]
    public void V1_FreezesCloseAndAiOverrides()
    {
        AgasMcpFrozenOutputTool close = Tool("agas.project.close");
        Assert.Equal(
            new[] { "projectStillOpen", "saved", "aiCancellationRequested", "saveState" },
            close.SuccessVariants.Single().Fields.Select(field => field.Name));
        Assert.Single(close.FailureDataVariants);
        Assert.True(close.AllowsFailureWithoutData);

        AgasMcpFrozenOutputTool status = Tool("agas.ai.status");
        Assert.Equal(new[] { "AgasMcpAiJobDto", "AgasMcpAiStatusListDataDto" }, status.SuccessVariants.Select(item => item.Name));
        foreach (string variantName in new[] { "AiJobQueued", "AiJobRunning", "AiJobSucceeded", "AiJobFailed", "AiJobCanceled" })
        {
            AgasMcpFrozenOutputShape variant = Shared(variantName);
            Assert.Contains(variant.Fields, field => field.Name == "alreadyApplied" && field.Required);
        }

        AgasMcpFrozenOutputShape apply = Tool("agas.ai.apply").SuccessVariants.Single();
        Assert.Equal("const Succeeded", apply.Fields.Single(field => field.Name == "state").Constraint);

    }

    [Fact]
    public void V1_RepresentsUnionsAsMetadataAndNeverAsWireFields()
    {
        AgasMcpFrozenOutputShape sceneItem = Shared("SceneItem");
        Assert.Empty(sceneItem.Fields);
        Assert.Equal(
            new[] { "SceneItemLayer", "SceneItemImage", "SceneItemPrompt" },
            sceneItem.UnionVariants);

        AgasMcpFrozenOutputShape aiJob = Tool("agas.ai.status").SuccessVariants
            .Single(shape => shape.Name == "AgasMcpAiJobDto");
        Assert.Empty(aiJob.Fields);
        Assert.Equal(
            new[] { "AiJobQueued", "AiJobRunning", "AiJobSucceeded", "AiJobFailed", "AiJobCanceled" },
            aiJob.UnionVariants);

        Assert.DoesNotContain(
            AllShapes().SelectMany(shape => shape.Fields),
            field => field.Name is "oneOf" or "variant");
        Assert.Throws<ArgumentException>(() => new AgasMcpFrozenOutputShape(
            "InvalidUnion",
            new[] { new AgasMcpFrozenOutputField("oneOf", "SomeVariant") }));
    }

    [Fact]
    public void V1_UsesDedicatedProviderIdPrimitiveForEveryProviderIdOutput()
    {
        Assert.Equal(
            "1..80;^[a-z0-9][a-z0-9._-]{0,79}$",
            Primitive("providerId").Fields.Single().Constraint);

        AgasMcpFrozenOutputField[] providerFields = AllShapes()
            .SelectMany(shape => shape.Fields)
            .Where(field => field.Name == "providerId")
            .ToArray();
        Assert.Equal(7, providerFields.Length);
        Assert.All(providerFields, field => Assert.Equal("providerId", field.Type));
    }

    [Fact]
    public void V1_FreezesLightweightImportListingAndOpaqueExchangeExport()
    {
        Assert.Equal(
            "^src1-[a-f0-9]{32}-[a-f0-9]{64}$;process-local;signed;15m expiry;no encoded path",
            Primitive("sourceRef").Fields.Single().Constraint);
        Assert.Equal(
            new[] { "items", "count", "totalCount", "hasMore", "nextOffset" },
            Tool("agas.imports.list").SuccessVariants.Single().Fields.Select(field => field.Name));
        Assert.Equal(
            new[] { "kind", "name", "sizeBytes", "mediaType", "validationState", "sourceRef" },
            Shared("ImportItem").Fields.Select(field => field.Name));
        Assert.Equal(
            "unverified|invalid",
            Shared("ImportItem").Fields.Single(field => field.Name == "validationState").Constraint);
        Assert.Equal(
            new[] { "packageRef", "sourceRef", "referenceImageCount" },
            Tool("agas.scene.package.export").SuccessVariants.Single().Fields.Select(field => field.Name));
    }

    [Fact]
    public void V1_FreezesCapabilityDomainAndOptionalOmission()
    {
        const string expectedCapabilities =
            "subset:textToImage|referenceEdit|regionEdit|nativeAlphaMask|guidedRegionEdit;" +
            "canonical-order:textToImage>referenceEdit>regionEdit>nativeAlphaMask>guidedRegionEdit";
        foreach (string shapeName in new[] { "AiProfileRow", "AiProviderRow" })
        {
            AgasMcpFrozenOutputField capabilities = Shared(shapeName).Fields.Single(field => field.Name == "capabilities");
            Assert.Equal(expectedCapabilities, capabilities.Constraint);
        }
        AgasMcpFrozenOutputField diagnosticCapabilities = Tool("agas.ai.diagnostics")
            .SuccessVariants.Single().Fields.Single(field => field.Name == "capabilities");
        Assert.Equal(expectedCapabilities, diagnosticCapabilities.Constraint);

        string[] capabilityValues = expectedCapabilities
            .Split(';')[0]
            .Remove(0, "subset:".Length)
            .Split('|');
        string[] canonicalOrder = expectedCapabilities
            .Split(';')[1]
            .Remove(0, "canonical-order:".Length)
            .Split('>');
        Assert.Equal(
            new[] { "textToImage", "referenceEdit", "regionEdit", "nativeAlphaMask", "guidedRegionEdit" },
            capabilityValues);
        Assert.Equal(capabilityValues, canonicalOrder);

        Assert.False(Shared("Diagnostic").Fields.Single(field => field.Name == "httpStatus").Required);
        Assert.False(Tool("agas.scene.get").SuccessVariants.Single().Fields.Single(field => field.Name == "nextCursor").Required);
        Assert.False(Tool("agas.scene.get").SuccessVariants.Single().Fields.Single(field => field.Name == "readme").Required);
    }

    [Fact]
    public void V1_CollectionsAreExternallyReadOnly()
    {
        Assert.Throws<NotSupportedException>(() =>
            ((IList<AgasMcpFrozenOutputTool>)AgasMcpFrozenOutputManifest.V1.Tools).Add(Tool("agas.project.open")));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<AgasMcpFrozenOutputField>)Shared("Grid").Fields).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)Shared("SceneItem").UnionVariants).Clear());
    }

    private static IEnumerable<AgasMcpFrozenOutputShape> AllShapes() =>
        AgasMcpFrozenOutputManifest.V1.Primitives
            .Concat(AgasMcpFrozenOutputManifest.V1.SharedShapes)
            .Concat(AgasMcpFrozenOutputManifest.V1.Tools.SelectMany(tool => tool.SuccessVariants))
            .Concat(AgasMcpFrozenOutputManifest.V1.Tools.SelectMany(tool => tool.FailureDataVariants));

    private static AgasMcpFrozenOutputTool Tool(string name) =>
        AgasMcpFrozenOutputManifest.V1.Tools.Single(tool => tool.ToolName == name);

    private static AgasMcpFrozenOutputShape Shared(string name) =>
        AgasMcpFrozenOutputManifest.V1.SharedShapes.Single(shape => shape.Name == name);

    private static AgasMcpFrozenOutputShape Primitive(string name) =>
        AgasMcpFrozenOutputManifest.V1.Primitives.Single(shape => shape.Name == name);
}
