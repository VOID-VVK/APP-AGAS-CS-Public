using System.Text.Json;
using System.Runtime.CompilerServices;
using AppAgasCs.Application;
using AppAgasCs.Core.Resources;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using Xunit;

namespace AppAgasCs.Tests.Core.Mcp;

public sealed class AgasMcpToolRuntimeTests
{
    private const string InvalidToolArgumentsMessage = "Tool arguments did not match the expected JSON shape.";

    public static TheoryData<Exception, string> RejectionMappings => new()
    {
        { new AgasMcpToolExecutionRejectedException(AgasMcpToolExecutionRejectReason.Busy), AgasMcpErrorCodes.Busy },
        { new AgasMcpToolExecutionRejectedException(AgasMcpToolExecutionRejectReason.RateLimited), AgasMcpErrorCodes.RateLimited },
        { new TimeoutException("timeout"), AgasMcpErrorCodes.Timeout },
        { new AgasMcpToolExecutionRejectedException(AgasMcpToolExecutionRejectReason.Stopped), AgasMcpErrorCodes.Stopped },
        { new OperationCanceledException(), AgasMcpErrorCodes.Stopped },
    };

    public static IEnumerable<object[]> ProjectFacadeToolArguments()
    {
        yield return new object[] { AgasMcpToolNames.ProjectSnapshot, Args(new { }) };
        yield return new object[] { AgasMcpToolNames.AssetsList, Args(new { }) };
        yield return new object[] { AgasMcpToolNames.SceneMutate, Args(new { op = "add", name = "Scene" }) };
        yield return new object[] { AgasMcpToolNames.LayerMutate, Args(new { op = "add", sceneId = "scene-001", name = "Layer" }) };
        yield return new object[] { AgasMcpToolNames.ImageMutate, Args(new { op = "add", sceneId = "scene-001", layerId = "layer-001", imagePath = "assets/a.png" }) };
        yield return new object[] { AgasMcpToolNames.PromptMutate, Args(new { op = "add", sceneId = "scene-001", layerId = "layer-001", text = "Prompt" }) };
        yield return new object[] { AgasMcpToolNames.ViewSet, Args(new { sceneId = "scene-001", cameraX = 1, cameraY = 2, zoom = 1 }) };
        yield return new object[] { AgasMcpToolNames.ProjectSave, Args(new { }) };
        yield return new object[] { AgasMcpToolNames.SelectionSet, Args(new { op = "clearRegions", sceneId = "scene-001" }) };
        yield return new object[] { AgasMcpToolNames.SelectionGet, Args(new { }) };
    }

    public static IEnumerable<object[]> ProjectFacadeCaseAliasArguments()
    {
        yield return new object[] { AgasMcpToolNames.ProjectSnapshot, Args(new { IncludeScenes = true }) };
        yield return new object[] { AgasMcpToolNames.AssetsList, Args(new { Query = "png" }) };
        yield return new object[] { AgasMcpToolNames.SceneMutate, Args(new { Op = "add", name = "Scene" }) };
        yield return new object[] { AgasMcpToolNames.LayerMutate, Args(new { Op = "add", sceneId = "scene-001", name = "Layer" }) };
        yield return new object[] { AgasMcpToolNames.ImageMutate, Args(new { Op = "add", sceneId = "scene-001", layerId = "layer-001", imagePath = "assets/a.png" }) };
        yield return new object[] { AgasMcpToolNames.PromptMutate, Args(new { Op = "add", sceneId = "scene-001", layerId = "layer-001", text = "Prompt" }) };
        yield return new object[] { AgasMcpToolNames.ViewSet, Args(new { SceneId = "scene-001", cameraX = 1, cameraY = 2, zoom = 1 }) };
        yield return new object[] { AgasMcpToolNames.SelectionSet, Args(new { Op = "clearRegions", sceneId = "scene-001" }) };
    }

    public static IEnumerable<object[]> ProjectFacadeInvalidContractArguments()
    {
        yield return new object[] { "view missing cameraX", AgasMcpToolNames.ViewSet, ArgsJson("{\"sceneId\":\"scene-001\",\"cameraY\":2,\"zoom\":1}") };
        yield return new object[] { "view missing cameraY", AgasMcpToolNames.ViewSet, ArgsJson("{\"sceneId\":\"scene-001\",\"cameraX\":1,\"zoom\":1}") };
        yield return new object[] { "selection missing column", AgasMcpToolNames.SelectionSet, ArgsJson("{\"op\":\"setRegions\",\"sceneId\":\"scene-001\",\"selection\":{\"row\":0,\"width\":1,\"height\":1}}") };
        yield return new object[] { "selection missing row", AgasMcpToolNames.SelectionSet, ArgsJson("{\"op\":\"setRegions\",\"sceneId\":\"scene-001\",\"selection\":{\"column\":0,\"width\":1,\"height\":1}}") };
        yield return new object[] { "optional explicit null", AgasMcpToolNames.ProjectSnapshot, ArgsJson("{\"includeImages\":null}") };
        yield return new object[] { "duplicate extensions", AgasMcpToolNames.AssetsList, ArgsJson("{\"extensions\":[\"png\",\"png\"]}") };
        yield return new object[] { "invalid enum", AgasMcpToolNames.SceneMutate, ArgsJson("{\"op\":\"copy\"}") };
        yield return new object[] { "invalid range", AgasMcpToolNames.ViewSet, ArgsJson("{\"sceneId\":\"scene-001\",\"cameraX\":1,\"cameraY\":2,\"zoom\":21}") };
        yield return new object[] { "invalid type", AgasMcpToolNames.ViewSet, ArgsJson("{\"sceneId\":\"scene-001\",\"cameraX\":\"1\",\"cameraY\":2,\"zoom\":1}") };
        yield return new object[] { "image move missing coordinates", AgasMcpToolNames.ImageMutate, ArgsJson("{\"op\":\"move\",\"sceneId\":\"scene-001\",\"layerId\":\"layer-001\",\"imageId\":\"image-001\"}") };
        yield return new object[] { "image update missing change", AgasMcpToolNames.ImageMutate, ArgsJson("{\"op\":\"update\",\"sceneId\":\"scene-001\",\"layerId\":\"layer-001\",\"imageId\":\"image-001\"}") };
    }

    public static IEnumerable<object[]> SelectionNumericRegionViolations()
    {
        yield return new object[] { "selection", "{\"column\":0,\"row\":0,\"width\":0,\"height\":1}" };
        yield return new object[] { "selection", "{\"column\":0,\"row\":0,\"width\":1,\"height\":0}" };
        yield return new object[] { "selection", "{\"column\":2147483648,\"row\":0,\"width\":1,\"height\":1}" };
        yield return new object[] { "selection", "{\"column\":0,\"row\":-2147483649,\"width\":1,\"height\":1}" };
        yield return new object[] { "selection", "{\"column\":0,\"row\":0,\"width\":2147483648,\"height\":1}" };
        yield return new object[] { "replacement", "{\"column\":0,\"row\":0,\"width\":0,\"height\":1}" };
        yield return new object[] { "replacement", "{\"column\":0,\"row\":0,\"width\":1,\"height\":0}" };
        yield return new object[] { "replacement", "{\"column\":0,\"row\":0,\"width\":1,\"height\":2147483648}" };
    }

    public static IEnumerable<object[]> SelectionStructuralRegionViolations()
    {
        yield return new object[] { "missing column", "selection", "{\"row\":0,\"width\":1,\"height\":1}" };
        yield return new object[] { "missing row", "selection", "{\"column\":0,\"width\":1,\"height\":1}" };
        yield return new object[] { "wrong type", "selection", "{\"column\":\"0\",\"row\":0,\"width\":1,\"height\":1}" };
        yield return new object[] { "null", "selection", "null" };
        yield return new object[] { "unknown", "replacement", "{\"column\":0,\"row\":0,\"width\":1,\"height\":1,\"extra\":true}" };
        yield return new object[] { "case", "replacement", "{\"Column\":0,\"row\":0,\"width\":1,\"height\":1}" };
    }

    [Theory]
    [MemberData(nameof(RejectionMappings))]
    public async Task ExecuteToolAsync_MapsExecutorRuntimeFailuresToContractErrors(Exception exception, string expectedCode)
    {
        var runtime = new AgasMcpToolRuntime(
            new ThrowingExecutor(exception), null!, (AgasMcpProjectFacade?)null);

        AgasMcpToolResponse response = await runtime.ExecuteToolAsync(
            "session",
            AgasMcpToolNames.ProjectSnapshot,
            new Dictionary<string, JsonElement>(),
            CancellationToken.None);

        Assert.False(response.Ok);
        Assert.False(response.Saved);
        Assert.False(response.Dirty);
        Assert.Null(response.Data);
        Assert.NotNull(response.Error);
        Assert.Equal(expectedCode, response.Error.Code);
        Assert.False(string.IsNullOrWhiteSpace(response.Error.Message));
        Assert.False(string.IsNullOrWhiteSpace(response.Error.Suggestion));
    }

    [Theory]
    [MemberData(nameof(ProjectFacadeToolArguments))]
    public void ProjectFacadeExecute_RejectsUnknownPropertiesForEveryTool(
        string toolName,
        IReadOnlyDictionary<string, JsonElement> validArguments)
    {
        var arguments = new Dictionary<string, JsonElement>(validArguments, StringComparer.Ordinal)
        {
            ["unexpectedField"] = JsonSerializer.SerializeToElement(true),
        };
        var facade = new AgasMcpProjectFacade(new AgasMcpProjectContextProvider());

        AgasMcpToolResponse response = facade.Execute(toolName, arguments);

        Assert.False(response.Ok);
        Assert.Equal(AgasMcpErrorCodes.InvalidOperation, response.Error?.Code);
        Assert.Equal("Tool arguments did not match the expected JSON shape.", response.Error?.Message);
    }

    [Theory]
    [MemberData(nameof(ProjectFacadeCaseAliasArguments))]
    public void ProjectFacadeExecute_RejectsCaseMismatchedPropertyAliases(
        string toolName,
        IReadOnlyDictionary<string, JsonElement> arguments)
    {
        var facade = new AgasMcpProjectFacade(new AgasMcpProjectContextProvider());

        AgasMcpToolResponse response = facade.Execute(toolName, arguments);

        Assert.False(response.Ok);
        Assert.Equal(AgasMcpErrorCodes.InvalidOperation, response.Error?.Code);
        Assert.Equal("Tool arguments did not match the expected JSON shape.", response.Error?.Message);
    }

    [Theory]
    [MemberData(nameof(ProjectFacadeInvalidContractArguments))]
    public void ProjectFacadeExecute_RejectsContractViolationsWithOneStableSafeError(
        string caseName,
        string toolName,
        IReadOnlyDictionary<string, JsonElement> arguments)
    {
        _ = caseName;
        var facade = new AgasMcpProjectFacade(new AgasMcpProjectContextProvider());

        AgasMcpToolResponse response = facade.Execute(toolName, arguments);

        Assert.False(response.Ok);
        Assert.Equal(AgasMcpErrorCodes.InvalidOperation, response.Error?.Code);
        Assert.Equal(InvalidToolArgumentsMessage, response.Error?.Message);
        Assert.False(facade.DirtyState.IsDirty);
    }

    [Fact]
    public void ProjectFacadeExecute_RejectsNameOver120RunesBeforeBusinessDispatch()
    {
        var facade = new AgasMcpProjectFacade(new AgasMcpProjectContextProvider());
        string name = string.Concat(Enumerable.Repeat("😀", 121));

        AgasMcpToolResponse response = facade.Execute(
            AgasMcpToolNames.SceneMutate,
            Args(new { op = "add", name }));

        Assert.False(response.Ok);
        Assert.Equal(AgasMcpErrorCodes.InvalidOperation, response.Error?.Code);
        Assert.Equal(InvalidToolArgumentsMessage, response.Error?.Message);
        Assert.False(facade.DirtyState.IsDirty);
    }

    [Fact]
    public void ImageUpdate_BlankPathIsRejectedWithoutProducingDirtyState()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "agas-mcp-runtime-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectRoot);
        try
        {
            var project = (AgasProjectResource)RuntimeHelpers.GetUninitializedObject(typeof(AgasProjectResource));
            project.ProjectId = "project-runtime-test";
            project.ResourceVersion = AgasProjectResource.CurrentResourceVersion;
            var layout = (WorkspaceLayoutResource)RuntimeHelpers.GetUninitializedObject(typeof(WorkspaceLayoutResource));
            var session = new ProjectSession();
            session.Reset(projectRoot);
            session.AcceptProject(project);
            AgasMcpProjectContextProvider provider = new();
            provider.RegisterContext(new AgasMcpProjectContext(session, layout));
            var facade = new AgasMcpProjectFacade(provider);

            AgasMcpToolResponse response = facade.Execute(
                AgasMcpToolNames.ImageMutate,
                Args(new
                {
                    op = "update",
                    sceneId = "scene-001",
                    layerId = "scene-001-layer-001",
                    imageId = "image-001",
                    imagePath = "   ",
                }));

            Assert.False(response.Ok);
            Assert.Equal(AgasMcpErrorCodes.InvalidOperation, response.Error?.Code);
            Assert.False(response.Dirty);
            Assert.False(facade.DirtyState.IsDirty);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void DirectTypedProjectHandlers_RejectInvalidAnnotatedInputsBeforeProjectLookup()
    {
        var facade = new AgasMcpProjectFacade(new AgasMcpProjectContextProvider());
        AgasMcpToolResponse[] responses =
        {
            facade.AssetsList(new AgasAssetsListInput(Limit: 0)),
            facade.SceneMutate(new AgasSceneMutateInput("copy")),
            facade.LayerMutate(new AgasLayerMutateInput("move", "scene-001", "layer-001")),
            facade.ImageMutate(new AgasImageMutateInput("update", "scene-001", "layer-001", "image-001")),
            facade.PromptMutate(new AgasPromptMutateInput("move", "scene-001", "layer-001", "prompt-001", Direction: 0)),
            facade.ViewSet(new AgasViewSetInput("scene-001", 0, 0, 21)),
            facade.ViewSet(new AgasViewSetInput("scene-001", double.NaN, 0, 1)),
            facade.SelectionSet(new AgasSelectionSetInput("setRegions", "scene-001")),
        };

        Assert.All(responses, response =>
        {
            Assert.False(response.Ok);
            Assert.Equal(AgasMcpErrorCodes.InvalidOperation, response.Error?.Code);
            Assert.Equal(InvalidToolArgumentsMessage, response.Error?.Message);
            Assert.False(response.Dirty);
        });
        Assert.False(facade.DirtyState.IsDirty);
    }

    [Fact]
    public void DirectTypedProjectHandlers_AcceptRuneBoundariesBeforeProjectLookup()
    {
        var facade = new AgasMcpProjectFacade(new AgasMcpProjectContextProvider());
        string query = string.Concat(Enumerable.Repeat("😀", 200));
        string promptText = string.Concat(Enumerable.Repeat("😀", 4000));

        AgasMcpToolResponse assets = facade.AssetsList(new AgasAssetsListInput(Query: query));
        AgasMcpToolResponse prompt = facade.PromptMutate(
            new AgasPromptMutateInput("add", "scene-001", "layer-001", Text: promptText));

        Assert.Equal(AgasMcpErrorCodes.NoOpenProject, assets.Error?.Code);
        Assert.Equal(AgasMcpErrorCodes.NoOpenProject, prompt.Error?.Code);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("update")]
    public void DirectTypedImageMutate_RejectsBlankOnlyImagePathBeforeProjectLookup(string op)
    {
        var facade = new AgasMcpProjectFacade(new AgasMcpProjectContextProvider());
        string? imageId = op == "update" ? "image-001" : null;

        AgasMcpToolResponse response = facade.ImageMutate(
            new AgasImageMutateInput(op, "scene-001", "layer-001", imageId, ImagePath: "   "));

        Assert.False(response.Ok);
        Assert.Equal(AgasMcpErrorCodes.InvalidOperation, response.Error?.Code);
        Assert.False(response.Dirty);
        Assert.False(facade.DirtyState.IsDirty);
    }

    [Fact]
    public void DirectTypedImageUpdate_BlankPathWithAnotherChangeContinuesWithoutTreatingPathAsMutation()
    {
        var facade = new AgasMcpProjectFacade(new AgasMcpProjectContextProvider());
        var input = new AgasImageMutateInput(
            "update",
            "scene-001",
            "layer-001",
            "image-001",
            ImagePath: "   ",
            X: 12);

        AgasMcpToolResponse response = facade.ImageMutate(input);

        Assert.Equal(AgasMcpErrorCodes.NoOpenProject, response.Error?.Code);
        Assert.False(AgasMcpProjectFacade.HasNonBlankImagePath(input));
        Assert.False(response.Dirty);
        Assert.False(facade.DirtyState.IsDirty);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("delete")]
    public void DirectTypedImageMutate_IgnoresBlankPathForOperationsThatDoNotUseIt(string op)
    {
        var facade = new AgasMcpProjectFacade(new AgasMcpProjectContextProvider());
        var input = new AgasImageMutateInput(
            op,
            "scene-001",
            "layer-001",
            "image-001",
            ImagePath: "   ",
            X: op == "move" ? 12 : null);

        AgasMcpToolResponse response = facade.ImageMutate(input);

        Assert.Equal(AgasMcpErrorCodes.NoOpenProject, response.Error?.Code);
        Assert.False(AgasMcpProjectFacade.HasNonBlankImagePath(input));
        Assert.False(response.Dirty);
        Assert.False(facade.DirtyState.IsDirty);
    }

    [Theory]
    [InlineData("selection", false)]
    [InlineData("selection", true)]
    [InlineData("replacement", false)]
    [InlineData("replacement", true)]
    public void SelectionSetExecute_RejectsNestedRegionUnknownOrCaseMismatchedProperties(
        string regionName,
        bool caseMismatch)
    {
        object invalidRegion = caseMismatch
            ? new { Column = 0, row = 0, width = 1, height = 1 }
            : new { column = 0, row = 0, width = 1, height = 1, unexpected = true };
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["op"] = "setRegions",
            ["sceneId"] = "scene-001",
            ["selection"] = new { column = 0, row = 0, width = 1, height = 1 },
            [regionName] = invalidRegion,
        };
        var facade = new AgasMcpProjectFacade(new AgasMcpProjectContextProvider());

        AgasMcpToolResponse response = facade.Execute(
            AgasMcpToolNames.SelectionSet,
            Args(arguments));

        Assert.False(response.Ok);
        Assert.Equal(AgasMcpErrorCodes.InvalidOperation, response.Error?.Code);
        Assert.Equal("Tool arguments did not match the expected JSON shape.", response.Error?.Message);
    }

    [Theory]
    [MemberData(nameof(SelectionNumericRegionViolations))]
    public void SelectionSetExecute_MapsNumericRegionViolationsToSelectionInvalid(
        string regionName,
        string invalidRegionJson)
    {
        string selection = regionName == "selection"
            ? invalidRegionJson
            : "{\"column\":0,\"row\":0,\"width\":1,\"height\":1}";
        string replacement = regionName == "replacement"
            ? $",\"replacement\":{invalidRegionJson}"
            : string.Empty;
        IReadOnlyDictionary<string, JsonElement> arguments = ArgsJson(
            $"{{\"op\":\"setRegions\",\"sceneId\":\"scene-001\",\"selection\":{selection}{replacement}}}");
        var facade = new AgasMcpProjectFacade(new AgasMcpProjectContextProvider());

        AgasMcpToolResponse response = facade.Execute(AgasMcpToolNames.SelectionSet, arguments);

        Assert.False(response.Ok);
        Assert.Equal(AgasMcpErrorCodes.SelectionInvalid, response.Error?.Code);
        Assert.False(response.Dirty);
    }

    [Theory]
    [InlineData("selection", 0, 1)]
    [InlineData("selection", 1, 0)]
    [InlineData("replacement", 0, 1)]
    [InlineData("replacement", 1, 0)]
    public void SelectionSetDirectTyped_MapsNumericRegionViolationsToSelectionInvalid(
        string regionName,
        int width,
        int height)
    {
        AgasMcpGridRegionDto valid = new(0, 0, 1, 1);
        AgasMcpGridRegionDto invalid = new(0, 0, width, height);
        AgasSelectionSetInput input = regionName == "selection"
            ? new AgasSelectionSetInput("setRegions", "scene-001", invalid)
            : new AgasSelectionSetInput("setRegions", "scene-001", valid, invalid);
        var facade = new AgasMcpProjectFacade(new AgasMcpProjectContextProvider());

        AgasMcpToolResponse response = facade.SelectionSet(input);

        Assert.False(response.Ok);
        Assert.Equal(AgasMcpErrorCodes.SelectionInvalid, response.Error?.Code);
        Assert.False(response.Dirty);
    }

    [Theory]
    [MemberData(nameof(SelectionStructuralRegionViolations))]
    public void SelectionSetExecute_KeepsStructuralRegionViolationsAsInvalidOperation(
        string caseName,
        string regionName,
        string invalidRegionJson)
    {
        _ = caseName;
        string selection = regionName == "selection"
            ? invalidRegionJson
            : "{\"column\":0,\"row\":0,\"width\":1,\"height\":1}";
        string replacement = regionName == "replacement"
            ? $",\"replacement\":{invalidRegionJson}"
            : string.Empty;
        IReadOnlyDictionary<string, JsonElement> arguments = ArgsJson(
            $"{{\"op\":\"setRegions\",\"sceneId\":\"scene-001\",\"selection\":{selection}{replacement}}}");
        var facade = new AgasMcpProjectFacade(new AgasMcpProjectContextProvider());

        AgasMcpToolResponse response = facade.Execute(AgasMcpToolNames.SelectionSet, arguments);

        Assert.False(response.Ok);
        Assert.Equal(AgasMcpErrorCodes.InvalidOperation, response.Error?.Code);
        Assert.Equal(InvalidToolArgumentsMessage, response.Error?.Message);
        Assert.False(response.Dirty);
    }

    [Fact]
    public void ReadOnlyAccess_RejectsEveryFacadeMutationBeforeContextOrDirtyBinding()
    {
        AgasMcpProjectContextProvider provider = new();
        provider.RegisterSource(new ReadOnlySource("PROJECT_VERSION_UNSUPPORTED"));
        AgasMcpProjectFacade facade = new(provider);

        AgasMcpToolResponse[] direct =
        {
            facade.SceneMutate(new AgasSceneMutateInput("add", Name: "blocked")),
            facade.LayerMutate(new AgasLayerMutateInput("add", "scene", Name: "blocked")),
            facade.ImageMutate(new AgasImageMutateInput("add", "scene", "layer", ImagePath: "assets/a.png")),
            facade.PromptMutate(new AgasPromptMutateInput("add", "scene", "layer", Text: "blocked")),
            facade.ViewSet(new AgasViewSetInput("scene", 1, 2, 1)),
            facade.SelectionSet(new AgasSelectionSetInput("clearRegions", "scene")),
            facade.ProjectSave(new AgasProjectSaveInput()),
        };

        Assert.All(direct, AssertReadOnly);
        Assert.False(facade.DirtyState.IsDirty);
    }

    [Fact]
    public void ReadOnlyAccess_ExecuteDispatchReturnsTheSameStableErrorForEveryMutationTool()
    {
        AgasMcpProjectContextProvider provider = new();
        provider.RegisterSource(new ReadOnlySource("BLUEPRINT_INDEX_VERSION_UNSUPPORTED"));
        AgasMcpProjectFacade facade = new(provider);
        (string Tool, IReadOnlyDictionary<string, JsonElement>? Args)[] calls =
        {
            (AgasMcpToolNames.SceneMutate, Args(new { op = "add", name = "blocked" })),
            (AgasMcpToolNames.LayerMutate, Args(new { op = "add", sceneId = "scene", name = "blocked" })),
            (AgasMcpToolNames.ImageMutate, Args(new { op = "add", sceneId = "scene", layerId = "layer", imagePath = "assets/a.png" })),
            (AgasMcpToolNames.PromptMutate, Args(new { op = "add", sceneId = "scene", layerId = "layer", text = "blocked" })),
            (AgasMcpToolNames.ViewSet, Args(new { sceneId = "scene", cameraX = 1, cameraY = 2, zoom = 1 })),
            (AgasMcpToolNames.SelectionSet, Args(new { op = "clearRegions", sceneId = "scene" })),
            (AgasMcpToolNames.ProjectSave, null),
        };

        foreach ((string tool, IReadOnlyDictionary<string, JsonElement>? args) in calls)
        {
            AssertReadOnly(facade.Execute(tool, args));
        }
        Assert.False(facade.DirtyState.IsDirty);
    }

    [Fact]
    public void MergeAccessStates_SourceWritableCannotRelaxReadOnlyContext()
    {
        AgasMcpProjectAccessState access = AgasMcpProjectContextProvider.MergeAccessStates(
            new AgasMcpProjectAccessState(true, false, string.Empty),
            new AgasMcpProjectAccessState(true, true, "PROJECT_VERSION_UNSUPPORTED"));

        Assert.True(access.HasOpenProject);
        Assert.True(access.RequiresReadOnly);
        Assert.Equal("PROJECT_VERSION_UNSUPPORTED", access.ReadOnlyReasonCode);
    }

    [Fact]
    public void MergeAccessStates_UsesStrictestStateAndReadOnlyContextReason()
    {
        AgasMcpProjectAccessState merged = AgasMcpProjectContextProvider.MergeAccessStates(
            new AgasMcpProjectAccessState(false, true, "SOURCE_READ_ONLY"),
            new AgasMcpProjectAccessState(true, true, "CONTEXT_READ_ONLY"));

        Assert.True(merged.HasOpenProject);
        Assert.True(merged.RequiresReadOnly);
        Assert.Equal("CONTEXT_READ_ONLY", merged.ReadOnlyReasonCode);
    }

    private static IReadOnlyDictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    private static IReadOnlyDictionary<string, JsonElement> ArgsJson(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static void AssertReadOnly(AgasMcpToolResponse response)
    {
        Assert.False(response.Ok);
        Assert.False(response.Saved);
        Assert.False(response.Dirty);
        Assert.Equal(AgasMcpErrorCodes.ProjectReadOnly, response.Error?.Code);
    }

    private sealed class ReadOnlySource : IAgasMcpProjectContextSource
    {
        private readonly string _reason;

        public ReadOnlySource(string reason)
        {
            _reason = reason;
        }

        public AgasMcpProjectContext? GetCurrentMcpProjectContext() => null;
        public AgasMcpProjectAccessState GetCurrentMcpProjectAccessState() => new(true, true, _reason);
    }

    private sealed class ThrowingExecutor : IAgasMcpToolExecutor
    {
        private readonly Exception _exception;

        public ThrowingExecutor(Exception exception)
        {
            _exception = exception;
        }

        public Task<T> ExecuteAsync<T>(
            string? sessionId,
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default) =>
            Task.FromException<T>(_exception);

        public Task ExecuteAsync(
            string? sessionId,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default) =>
            Task.FromException(_exception);

        public void Stop()
        {
        }
    }
}
