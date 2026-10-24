using System.Collections;
using System.Collections.Immutable;
using System.Text.Json;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Contracts.Output;
using AppAgasCs.Mcp.Contracts.Runtime;
using AppAgasCs.Application;
using AppAgasCs.Mcp.Runtime;
using Xunit;

namespace AppAgasCs.Core.Tests.Mcp;

public sealed class AgasMcpProjectWave23ContractTests
{
    [Fact]
    public void SceneGetTypedOutput_SerializesPolymorphicItemsWithoutTypeMetadata()
    {
        ImmutableArray<AgasMcpSceneItemDto> items = ImmutableArray.Create<AgasMcpSceneItemDto>(
            new AgasMcpSceneLayerItemDto("layer", "scene-1", "layer-1", "Layer", 0, true, false, false, 0, 1),
            new AgasMcpScenePromptItemDto("prompt", "scene-1", "layer-1", "prompt-1", "sky", "environment", true, 0));
        var data = new AgasMcpSceneGetDataDto(
            "scene-1", "Scene", new AgasMcpViewStateDto(0, 0, 1), items, "layer-1", null);
        AgasMcpToolResponse response = AgasMcpToolResponse.Success(true, false, data);

        string json = JsonSerializer.Serialize(response, AgasMcpOutputContractRegistry.SerializerOptions);

        Assert.Contains("\"kind\":\"layer\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"prompt\"", json, StringComparison.Ordinal);
        Assert.Contains("\"role\":\"environment\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("$type", json, StringComparison.Ordinal);
        Assert.DoesNotContain(":{}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectTypedOutput_CollectionsCannotBeMutatedThroughPublicData()
    {
        var data = new AgasMcpRecentProjectsDataDto(ImmutableArray.Create(
            new AgasMcpRecentProjectDto("projectref-0123456789abcdef0123456789abcdef", "P", 1, true, false)));

        IList list = Assert.IsAssignableFrom<IList>(data.Projects);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Clear());
    }

    [Fact]
    public void SceneGetTypedOutput_BuildsAndValidatesAgainstRuntimeContract()
    {
        AgasMcpOutputContract contract = AgasMcpOutputSchemaBuilder.Build(new[] { typeof(AgasMcpSceneGetDataDto) });
        var data = new AgasMcpSceneGetDataDto(
            "scene-1", "Scene", new AgasMcpViewStateDto(0, 0, 1),
            ImmutableArray<AgasMcpSceneItemDto>.Empty, null, null);

        AgasMcpOutputValidationResult validation = AgasMcpOutputContractRegistry.Validate(
            contract, AgasMcpToolResponse.Success(true, false, data));

        Assert.True(validation.Success, validation.ContractFailure);
    }

    [Fact]
    public void ProjectLifecycleImportOutputDtos_AllBuildJsonSchema202012CompatibleShapes()
    {
        Type[] types =
        {
            typeof(AgasMcpRecentProjectsDataDto), typeof(AgasMcpProjectOpenDataDto),
            typeof(AgasMcpProjectCreateDataDto), typeof(AgasMcpProjectCloseDataDto),
            typeof(AgasMcpProjectSnapshotDataDto), typeof(AgasMcpAssetsListDataDto),
            typeof(AgasMcpImportsListDataDto), typeof(AgasMcpAssetImportDataDto),
            typeof(AgasMcpSceneGetDataDto), typeof(AgasMcpSceneMutationDataDto),
            typeof(AgasMcpScenePackageImportDataDto), typeof(AgasMcpScenePackageExportDataDto),
            typeof(AgasMcpGridMutationDataDto), typeof(AgasMcpLayerMutationDataDto),
            typeof(AgasMcpLayerMergeVisibleDataDto),
            typeof(AgasMcpImageMutationDataDto), typeof(AgasMcpPromptMutationDataDto),
            typeof(AgasMcpViewSetDataDto), typeof(AgasMcpProjectSaveDataDto),
            typeof(AgasMcpSelectionDataDto), typeof(AgasMcpSelectionGetDataDto),
        };

        foreach (Type type in types)
        {
            AgasMcpOutputContract contract = AgasMcpOutputSchemaBuilder.Build(new[] { type });
            Assert.Equal(JsonValueKind.Object, contract.OutputSchema.ValueKind);
            Assert.True(contract.OutputSchema.TryGetProperty("oneOf", out _), type.Name);
        }
    }

    [Fact]
    public void PromptRole_IsStrictLowerCamelAndRoleOnlyUpdateIsAccepted()
    {
        var facade = new AgasMcpProjectFacade(new AgasMcpProjectContextProvider());

        AgasMcpToolResponse add = facade.PromptMutate(
            new AgasPromptMutateInput("add", "scene-1", "layer-1", Text: "sky", Role: "environment"));
        AgasMcpToolResponse update = facade.PromptMutate(
            new AgasPromptMutateInput("update", "scene-1", "layer-1", "prompt-1", Role: "negativeConstraint"));
        AgasMcpToolResponse wrongCase = facade.PromptMutate(
            new AgasPromptMutateInput("update", "scene-1", "layer-1", "prompt-1", Role: "Environment"));
        AgasMcpToolResponse irrelevant = facade.PromptMutate(
            new AgasPromptMutateInput("delete", "scene-1", "layer-1", "prompt-1", Role: "general"));

        Assert.Equal(AgasMcpErrorCodes.NoOpenProject, add.Error?.Code);
        Assert.Equal(AgasMcpErrorCodes.NoOpenProject, update.Error?.Code);
        Assert.Equal(AgasMcpErrorCodes.InvalidOperation, wrongCase.Error?.Code);
        Assert.Equal(AgasMcpErrorCodes.InvalidOperation, irrelevant.Error?.Code);
    }

    [Fact]
    public void ProjectLifecycleImportSceneFailureCodes_AreCentralizedAndStable()
    {
        Assert.Equal(AgasMcpErrorCodes.ImportInvalid, AgasControlledAssetImportService.InvalidCode);
        Assert.Equal(AgasMcpErrorCodes.ImportTooLarge, AgasControlledAssetImportService.TooLargeCode);
        Assert.Equal(AgasMcpErrorCodes.ImportSourceNotFound, AgasControlledAssetImportService.NotFoundCode);
        Assert.Equal(AgasMcpErrorCodes.PackageInvalid, AgasScenePackageStagingService.InvalidCode);
        Assert.Equal(AgasMcpErrorCodes.PackageTooLarge, AgasScenePackageStagingService.TooLargeCode);
        Assert.Equal(AgasMcpErrorCodes.SceneInvalid, AgasSceneDetailErrorCodes.Invalid);
        Assert.Equal(AgasMcpErrorCodes.SceneSnapshotTooLarge, AgasSceneDetailErrorCodes.SnapshotTooLarge);
        Assert.Equal(AgasMcpErrorCodes.CursorInvalid, AgasSceneDetailErrorCodes.CursorInvalid);
        Assert.Equal(AgasMcpErrorCodes.CursorCapacity, AgasSceneDetailErrorCodes.CursorCapacity);
    }

    [Fact]
    public async Task SceneGridAndImportToolFailureVariantsUseTypedStableEnvelopes()
    {
        var provider = new AgasMcpProjectContextProvider();
        var project = new AgasMcpProjectFacade(provider);
        var imports = new AgasMcpImportFacade(provider, new AgasMcpDirtyState(), new UnusedPackageBridge());
        const string sourceRef = "src1-0123456789abcdef0123456789abcdef-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        AgasMcpToolResponse scene = project.SceneGet(new AgasSceneGetInput("scene-1"));
        AgasMcpToolResponse grid = project.GridMutate(new AgasGridMutateInput("setVisible", Enabled: true));
        AgasMcpToolResponse list = imports.List(new AgasImportsListInput(Kind: "Asset"));
        AgasMcpToolResponse asset = imports.AssetImport(
            new AgasAssetsImportInput(sourceRef),
            new AgasMcpCommitExecution(AgasMcpCommitPolicy.ShortAtomicCommit),
            CancellationToken.None);
        AgasMcpToolResponse packageImport = await imports.PackageImportAsync(
            new AgasScenePackageImportInput(sourceRef),
            new AgasMcpCommitExecution(AgasMcpCommitPolicy.StartedMainThreadCompletion),
            CancellationToken.None);
        AgasMcpToolResponse packageExport = imports.PackageExport(
            new AgasScenePackageExportInput("scene-1"),
            new AgasMcpCommitExecution(AgasMcpCommitPolicy.ShortAtomicCommit),
            CancellationToken.None);

        Assert.Equal(AgasMcpErrorCodes.NoOpenProject, scene.Error?.Code);
        Assert.Equal(AgasMcpErrorCodes.NoOpenProject, grid.Error?.Code);
        Assert.Equal(AgasMcpErrorCodes.InvalidOperation, list.Error?.Code);
        Assert.Equal(AgasMcpErrorCodes.NoOpenProject, asset.Error?.Code);
        Assert.Equal(AgasMcpErrorCodes.NoOpenProject, packageImport.Error?.Code);
        Assert.Equal(AgasMcpErrorCodes.NoOpenProject, packageExport.Error?.Code);
        Assert.All(new[] { scene, grid, list, asset, packageImport, packageExport }, response =>
        {
            Assert.False(response.Ok);
            Assert.Null(response.Data);
        });
    }

    private sealed class UnusedPackageBridge : IAgasMcpScenePackageBridge
    {
        public AgasMcpScenePackageExportCapture CaptureExport(string sceneId) =>
            throw new InvalidOperationException("No-project gate must precede package capture.");

        public ValueTask<AgasMcpScenePackagePublishResult> PublishPreparedImportAsync(
            AgasScenePackagePreparedImport prepared,
            AgasMcpCommitExecution execution,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No-project gate must precede package publish.");
    }
}
