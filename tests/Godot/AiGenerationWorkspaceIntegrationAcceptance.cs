using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AGAS.UI;
using Agas.Features.Canvas;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.UI;
using AppAgasCs.Features.Ai.Workspace;
using Godot;

public partial class AiGenerationWorkspaceIntegrationAcceptance : Node
{
    private const string Prefix = "[AGAS][AI-WORKSPACE-INTEGRATION]";
    private string? _root;
    private string? _reopenRoot;
    private string? _concurrentReopenRoot;
    private string? _recoveryReopenRoot;

    public override async void _Ready()
    {
        int exitCode = 0;
        try
        {
            await RunAll();
            GD.Print($"{Prefix}[PASS]");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {exception}");
        }
        finally
        {
            if (_root != null)
            {
                try { Directory.Delete(_root, recursive: true); } catch { }
            }
            if (_reopenRoot != null)
            {
                try { Directory.Delete(_reopenRoot, recursive: true); } catch { }
            }
            if (_concurrentReopenRoot != null)
            {
                try { Directory.Delete(_concurrentReopenRoot, recursive: true); } catch { }
            }
            if (_recoveryReopenRoot != null)
            {
                try { Directory.Delete(_recoveryReopenRoot, recursive: true); } catch { }
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GetTree().Quit(exitCode);
        }
    }

    private async System.Threading.Tasks.Task RunAll()
    {
        _root = Path.Combine(Path.GetTempPath(), "agas-ai-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "assets", "generated"));
        SaveImage(Path.Combine(_root, "assets", "context.png"), Colors.Orange);
        SaveImage(Path.Combine(_root, "assets", "before.png"), Colors.Blue);
        SaveImage(Path.Combine(_root, "assets", "generated", "replace.png"), Colors.Green);
        SaveImage(Path.Combine(_root, "assets", "generated", "add.png"), Colors.Red);
        SaveImage(Path.Combine(_root, "assets", "generated", "legacy-w.png"), Colors.Blue, 48, 48);
        SaveImage(Path.Combine(_root, "assets", "generated", "fallback.png"), Colors.Purple);
        string scopedQOnlyCandidate = "agas/Scene 001--scene-001/Layer 1--scene-001-layer-001/q-only.png";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(
            _root,
            scopedQOnlyCandidate.Replace('/', Path.DirectorySeparatorChar)))!);
        SaveImage(Path.Combine(_root, scopedQOnlyCandidate.Replace('/', Path.DirectorySeparatorChar)), Colors.Yellow);
        SaveImage(Path.Combine(_root, "assets", "generated", "save-fail.png"), Colors.Orange);
        SaveImage(Path.Combine(_root, "assets", "generated", "concurrent-a.png"), Colors.Cyan);
        SaveImage(Path.Combine(_root, "assets", "generated", "concurrent-b.png"), Colors.Magenta);
        SaveImage(Path.Combine(_root, "assets", "generated", "concurrent-c.png"), Colors.White);

        var workspace = new MainWorkspace { CustomMinimumSize = new Vector2(1200, 760) };
        AddChild(workspace);
        await Frame();
        workspace.OpenProject(_root);
        await Frame();

        CanvasWorkspace canvas = FindRequired<CanvasWorkspace>(workspace, "CanvasWorkspaceHost");
        AiGenerationPanel panel = FindRequired<AiGenerationPanel>(workspace, "AiGenerationPanel");
        IAgasAiGenerationWorkspaceBridge bridge = workspace.AiGenerationWorkspaceBridge;
        ProjectFilesTree files = FindRequired<ProjectFilesTree>(workspace, "Files");
        SpinBox gridSize = FindRequired<SpinBox>(workspace, "GridSizeSpinBox");
        Assert.True(canvas.TryAddImageFromProjectRelativePath("assets/context.png", new Vector2(0, 48)), "Q context fixture should be added.");
        Assert.True(canvas.TryAddImageFromProjectRelativePath("assets/before.png", new Vector2(48, 96)), "Fixture image should be added.");
        var selected = canvas.GetSelectedImage() as ImageInstanceResource;
        Assert.NotNull(selected, "Added image should be selected.");
        AgasLayerResource layer = workspace.CurrentScene!.Layers.First(item => item.Id == workspace.CurrentScene.ActiveLayerId);
        SetWorldRegion(canvas, CanvasRegionTool.Selection, new Rect2(0, 48, 144, 144));
        SetWorldRegion(canvas, CanvasRegionTool.Replacement, new Rect2(48, 96, 48, 48));
        AiGenerationCanvasContext? bridgedContext = bridge.CaptureCurrentContext(CanvasPatchLayerScope.AllVisibleLayers);
        Assert.NotNull(bridgedContext, "The shared bridge must expose the live manual canvas context.");
        Assert.Equal(workspace.CurrentScene!.Id, bridgedContext!.SceneId,
            "The shared bridge must freeze the active manual scene instead of a parallel MCP model.");
        Assert.Equal(MainWorkspace.DefaultCanvasAiFillPrompt, bridge.ResolvePrompt(),
            "The shared bridge must use the manual active-layer prompt fallback.");
        CanvasPatchCapture replaceCapture = canvas.CaptureCurrentPatch(selected!.Id);
        string projectId = workspace.CurrentProjectId;
        string sceneId = workspace.CurrentScene.Id;
        string layerId = layer.Id;
        var generationContext = new AiGenerationCanvasContext(
            workspace.ProjectRoot,
            projectId,
            sceneId,
            layerId,
            HasSelectedImage: true,
            SelectedImageId: selected.Id,
            CanEditCurrentLayer: true,
            GridSizePx: canvas.GetGridSizePx(),
            RegionMetadata: new AiGenerationRegionMetadata(
                new AiGenerationGridRegion(0, 1, 3, 3),
                new AiGenerationGridRegion(1, 2, 1, 1)),
            SceneName: workspace.CurrentScene.Name,
            LayerName: layer.Name);
        var overlayState = new AiGenerationOverlayState(
            101,
            generationContext,
            new Rect2(0, 48, 144, 144),
            AiGenerationOverlayPhase.Generating,
            1,
            "AI #101 生成中 00:01");
        panel.GenerationOverlaysChanged?.Invoke(new[] { overlayState });
        Assert.Equal(1, canvas.AiGenerationOverlays.Count, "Workspace must forward the frozen task overlay to the canvas.");
        Assert.Equal(new Rect2(0, 48, 144, 144), canvas.AiGenerationOverlays[0].WorldRect, "Workspace must preserve the request-time Q world rectangle.");
        gridSize.Value = 32;
        await Frame();
        Assert.Equal(32, canvas.GetGridSizePx(), "Grid fixture must update through MainWorkspace.SetGridSize.");
        Assert.Equal(1, canvas.AiGenerationOverlays.Count, "Changing grid size must not hide an active frozen task overlay.");
        gridSize.Value = 48;
        await Frame();

        SetWorldRegion(canvas, CanvasRegionTool.Replacement, new Rect2(96, 96, 48, 48));
        Assert.Equal(1, canvas.AiGenerationOverlays.Count, "Changing W must not cancel or hide the active task overlay.");
        Assert.Equal(new Rect2(0, 48, 144, 144), canvas.AiGenerationOverlays[0].WorldRect, "The overlay must stay pinned to its original Q region.");
        SetWorldRegion(canvas, CanvasRegionTool.Replacement, new Rect2(48, 96, 48, 48));
        var generationProfile = new AiGenerationProviderProfile(
            "test-profile",
            "Test Relay",
            "openai-compatible",
            "gpt-image-2",
            "test-key",
            "https://relay.example/v1");
        AiGenerationPreparationResult prepared = panel.PrepareGenerationRequest!(new AiGenerationPreparationInput(
            generationProfile,
            generationContext,
            AiGenerationTarget.ReplaceSelected,
            "replace grass"));
        Assert.True(prepared.Success, prepared.Message);
        Assert.NotNull(prepared.Request, "Workspace should build a provider-neutral region-edit request.");
        Assert.Equal(AiGenerationOperation.RegionEdit, prepared.Request!.Operation, "W generation should never degrade to text-to-image.");
        Assert.NotNull(prepared.Request.EditInput, "Region edit should include the renderer source canvas and mask.");
        Assert.Equal(replaceCapture.ReferenceWorldPixels.Size.X, prepared.Request.Width, "Provider output width should follow the full reference canvas.");
        Assert.Equal(replaceCapture.ReferenceWorldPixels.Size.Y, prepared.Request.Height, "Provider output height should follow the full reference canvas.");
        AiGenerationEditInput preparedEdit = prepared.Request.EditInput!;
        Assert.Equal(144, preparedEdit.SourceCanvas.PixelWidth, "The uploaded source width must be the complete Q width, not W.");
        Assert.Equal(144, preparedEdit.SourceCanvas.PixelHeight, "The uploaded source height must be the complete Q height, not W.");
        Assert.Equal(144, preparedEdit.Mask.PixelWidth, "The upload mask width must match complete Q.");
        Assert.Equal(144, preparedEdit.Mask.PixelHeight, "The upload mask height must match complete Q.");
        Assert.Equal(new AiGenerationPixelRegion(48, 48, 48, 48), preparedEdit.EditRegionPixels,
            "W must be expressed relative to the complete Q raster.");
        Assert.True(preparedEdit.SourceCanvas.Bytes.SequenceEqual(replaceCapture.SourceCanvasPng),
            "The provider source must be the exact complete Q capture with W punched out.");
        Assert.False(preparedEdit.SourceCanvas.Bytes.SequenceEqual(replaceCapture.ReplacementPatchPng),
            "The provider source must never degrade to the W-only replacement crop.");
        using (Image preparedSource = LoadPng(preparedEdit.SourceCanvas.Bytes))
        using (Image preparedMask = LoadPng(preparedEdit.Mask.Bytes))
        {
            Assert.True(preparedSource.GetPixel(4, 4).A > 0.99f,
                "A real Q pixel outside W must be preserved in the uploaded source.");
            for (int y = 0; y < 144; y++)
            {
                for (int x = 0; x < 144; x++)
                {
                    bool insideW = x is >= 48 and < 96 && y is >= 48 and < 96;
                    if (insideW)
                    {
                        Assert.True(preparedSource.GetPixel(x, y).A < 0.01f,
                            $"Uploaded source W pixel ({x},{y}) must be transparent.");
                        Assert.True(preparedMask.GetPixel(x, y).A < 0.01f,
                            $"Uploaded mask W pixel ({x},{y}) must be transparent.");
                    }
                    else
                    {
                        Assert.True(preparedMask.GetPixel(x, y).A > 0.99f,
                            $"Uploaded mask Q\\W pixel ({x},{y}) must remain opaque.");
                    }
                }
            }
        }
        Assert.Equal(sceneId, prepared.Request.StorageScope.SceneId, "Request must freeze the source scene id for storage.");
        Assert.Equal(workspace.CurrentScene.Name, prepared.Request.StorageScope.SceneName, "Request must freeze the source scene name for storage.");
        Assert.Equal(layerId, prepared.Request.StorageScope.LayerId, "Request must freeze the source layer id for storage.");
        Assert.Equal(layer.Name, prepared.Request.StorageScope.LayerName, "Request must freeze the source layer name for storage.");

        SetWorldRegion(canvas, CanvasRegionTool.Replacement, new Rect2(96, 144, 96, 48));
        Assert.False(canvas.TryCaptureCurrentPatch(out _, out string outsideCaptureError, selected.Id),
            "A W region partially outside Q must be rejected by canvas capture.");
        Assert.True(outsideCaptureError.Contains("W 必须位于 Q 区域内", StringComparison.Ordinal),
            "Canvas capture must preserve the explicit W containment error.");
        var outsideContext = generationContext with
        {
            RegionMetadata = new AiGenerationRegionMetadata(
                new AiGenerationGridRegion(0, 1, 3, 3),
                new AiGenerationGridRegion(2, 3, 2, 1))
        };
        AiGenerationPreparationResult outsidePrepared = panel.PrepareGenerationRequest(new AiGenerationPreparationInput(
            generationProfile,
            outsideContext,
            AiGenerationTarget.ReplaceSelected,
            "replace grass"));
        Assert.False(outsidePrepared.Success, "Workspace request preparation must reject W outside Q before sending.");
        Assert.True(outsidePrepared.Message.Contains("W 必须位于 Q 区域内", StringComparison.Ordinal),
            "Workspace preparation must preserve the explicit W containment error.");

        // Concurrent multi-box workflow: first Q/W is active, then the user redraws an
        // adjacent Q for the next tile. A leftover W outside the new Q used to disable
        // generation and make the second box look broken even though capacity remained.
        SetWorldRegion(canvas, CanvasRegionTool.Selection, new Rect2(0, 48, 144, 144));
        SetWorldRegion(canvas, CanvasRegionTool.Replacement, new Rect2(48, 96, 48, 48));
        Assert.True(canvas.ReplacementRegion.HasValue, "Fixture requires an active W hole inside the first Q.");
        SetWorldRegion(canvas, CanvasRegionTool.Selection, new Rect2(144, 48, 144, 144));
        Assert.False(canvas.ReplacementRegion.HasValue,
            "Redrawing Q that no longer contains W must clear the stale W so the next concurrent box can generate.");
        Assert.Equal(new CanvasGridRegion(3, 1, 3, 3), canvas.SelectionRegion,
            "The second concurrent Q must replace the previous selection.");
        Assert.False(canvas.TrySetRegions(new CanvasGridRegion(0, 1, 3, 3), new CanvasGridRegion(5, 1, 1, 1)),
            "Programmatic region set must reject W outside Q.");
        Assert.True(canvas.TrySetRegions(new CanvasGridRegion(0, 1, 3, 3), new CanvasGridRegion(1, 2, 1, 1)),
            "Programmatic region set must accept W inside Q.");
        SetWorldRegion(canvas, CanvasRegionTool.Selection, new Rect2(0, 48, 144, 144));
        SetWorldRegion(canvas, CanvasRegionTool.Replacement, new Rect2(48, 96, 48, 48));

        int beforeReplaceApply = layer.Images.Count;
        string originalSelectedPath = selected.ImagePath;
        AiGeneratedImageApplyResult replaced = panel.ApplyGeneratedImage!(new AiGeneratedImageApplication(
            new AiGenerationResult(
                "assets/generated/replace.png",
                "image/png",
                32,
                AiGenerationEffectiveOperation.GuidedRegionEdit,
                AiGenerationEditGuarantee.GuidedReference,
                8,
                8),
            AiGenerationTarget.ReplaceSelected,
            workspace.ProjectRoot,
            projectId,
            sceneId,
            layerId,
            selected.Id,
            replaceCapture,
            AiGenerationOperation.RegionEdit,
            ExpectedSceneName: workspace.CurrentScene.Name,
            ExpectedLayerName: layer.Name));
        Assert.True(replaced.Success, replaced.Message);
        string scopedDirectory = $"agas/{workspace.CurrentScene.Name}/{layer.Name}/";
        Assert.Equal(originalSelectedPath, selected.ImagePath,
            "Complete-Q writeback must preserve the underlying selected image instead of destructively replacing it.");
        Assert.Equal(beforeReplaceApply + 1, layer.Images.Count,
            "ReplaceSelected generation must add one complete-Q overlay so Q is represented even outside the selected image.");
        ImageInstanceResource replacementOverlay = layer.Images.OrderBy(item => item.Order).Last();
        Assert.True(replacementOverlay.ImagePath.StartsWith(scopedDirectory, StringComparison.Ordinal),
            "Applied complete-Q result should use the frozen scene/layer generated directory.");
        Assert.False(replacementOverlay.ImagePath == "assets/generated/replace.png", "Apply should create an immutable normalized Q image instead of referencing the raw candidate.");
        Assert.Same(selected, canvas.GetSelectedImage(), "Complete-Q overlay application should preserve selection.");
        Assert.False(workspace.HasUnsavedChanges, "A successful AI application must immediately save the complete workspace.");
        using (Image appliedImage = Image.LoadFromFile(Path.Combine(_root, replacementOverlay.ImagePath.Replace('/', Path.DirectorySeparatorChar))))
        {
            Assert.Equal(144, appliedImage.GetWidth(), "Complete-Q writeback width must equal Q.");
            Assert.Equal(144, appliedImage.GetHeight(), "Complete-Q writeback height must equal Q.");
            Assert.True(appliedImage.GetPixel(0, 0).G > 0.8f, "Q pixels outside W must also come from the provider result.");
            Assert.True(appliedImage.GetPixel(72, 72).G > 0.8f, "W pixels must come from the provider result.");
        }

        string persistedReplacementPath = replacementOverlay.ImagePath;
        _reopenRoot = _root + "-reopen";
        CopyDirectory(_root, _reopenRoot);
        var reopened = new MainWorkspace { CustomMinimumSize = new Vector2(1200, 760) };
        AddChild(reopened);
        await Frame();
        reopened.OpenProject(_reopenRoot);
        await Frame();
        AgasLayerResource reopenedLayer = reopened.CurrentScene!.Layers.Single(item => item.Id == layerId);
        ImageInstanceResource reopenedReplacement = reopenedLayer.Images.Single(item => item.ImagePath == persistedReplacementPath);
        Assert.Equal(persistedReplacementPath, reopenedReplacement.ImagePath,
            "Reopening the autosaved project must restore the immutable generated image reference.");
        Assert.Equal(0.0, reopenedReplacement.X, "Reopening must restore the request-time Q X coordinate.");
        Assert.Equal(48.0, reopenedReplacement.Y, "Reopening must restore the request-time Q Y coordinate.");
        reopened.QueueFree();
        await Frame();
        Assert.True(canvas.Undo(), "Applied complete-Q overlay should be undoable.");
        Assert.Equal(beforeReplaceApply, layer.Images.Count, "Undo should remove only the generated Q overlay.");
        Assert.Equal(originalSelectedPath, selected.ImagePath, "Undo must leave the underlying selected image unchanged.");
        Assert.True(canvas.Redo(), "Applied complete-Q overlay should be redoable.");
        Assert.Equal(beforeReplaceApply + 1, layer.Images.Count, "Redo should restore the generated Q overlay.");

        var switchedLayer = new AgasLayerResource
        {
            Id = "layer-switched",
            Name = "Switched",
            Order = layer.Order + 1,
            Visible = true,
            Locked = false,
        };
        workspace.CurrentScene.Layers.Add(switchedLayer);
        Assert.True(canvas.SetCurrentLayer(switchedLayer), "Fixture should switch to another layer.");
        int originCount = layer.Images.Count;
        int switchedCount = switchedLayer.Images.Count;
        AiGeneratedImageApplyResult originAdd = panel.ApplyGeneratedImage(new AiGeneratedImageApplication(
            new AiGenerationResult("assets/generated/add.png", "image/png", 32),
            AiGenerationTarget.AddToCurrentLayer,
            workspace.ProjectRoot,
            projectId,
            sceneId,
            layerId,
            null,
            replaceCapture,
            AiGenerationOperation.RegionEdit));
        Assert.True(originAdd.Success, originAdd.Message);
        Assert.Equal(switchedCount, switchedLayer.Images.Count, "A frozen result must not land in the newly active layer.");
        Assert.Equal(originCount + 1, layer.Images.Count, "A frozen result must write back to the request-time layer.");

        int originAfterFirst = layer.Images.Count;
        AiGeneratedImageApplyResult originReplace = panel.ApplyGeneratedImage(new AiGeneratedImageApplication(
            new AiGenerationResult("assets/generated/add.png", "image/png", 32),
            AiGenerationTarget.ReplaceSelected,
            workspace.ProjectRoot,
            projectId,
            sceneId,
            layerId,
            selected.Id,
            replaceCapture,
            AiGenerationOperation.RegionEdit));
        Assert.True(originReplace.Success, originReplace.Message);
        Assert.Equal(originAfterFirst + 1, layer.Images.Count, "A frozen replacement must still write back to the request-time layer.");

        Assert.True(canvas.SetCurrentLayer(layer), "Fixture should return to the original layer.");
        int beforeFrozenRegionApply = layer.Images.Count;
        CanvasPatchCapture staleRegionCapture = canvas.CaptureCurrentPatch("__agas_layer_patch__");
        SetWorldRegion(canvas, CanvasRegionTool.Replacement, new Rect2(96, 96, 48, 48));
        AiGeneratedImageApplyResult staleRegion = panel.ApplyGeneratedImage(new AiGeneratedImageApplication(
            new AiGenerationResult("assets/generated/replace.png", "image/png", 32),
            AiGenerationTarget.AddToCurrentLayer,
            workspace.ProjectRoot,
            projectId,
            sceneId,
            layerId,
            null,
            staleRegionCapture,
            AiGenerationOperation.RegionEdit));
        Assert.True(staleRegion.Success, "Changing only W after submission must still apply the frozen task result.");
        Assert.Equal(beforeFrozenRegionApply + 1, layer.Images.Count, "Frozen-region application must add one image.");
        ImageInstanceResource frozenRegionItem = layer.Images.OrderBy(item => item.Order).Last();
        Assert.Equal(0.0, frozenRegionItem.X, "Frozen task output must use the frozen Q X, independent of a newly selected W.");
        Assert.Equal(48.0, frozenRegionItem.Y, "Frozen task output must use the frozen Q Y, independent of a newly selected W.");
        SetWorldRegion(canvas, CanvasRegionTool.Replacement, new Rect2(48, 96, 48, 48));

        int beforeMissingTargetFallback = layer.Images.Count;
        Assert.True(canvas.TryAddImageFromProjectRelativePath("assets/before.png", new Vector2(48, 96)),
            "Fallback fixture target should be added and selected.");
        var fallbackTarget = canvas.GetSelectedImage() as ImageInstanceResource;
        Assert.NotNull(fallbackTarget, "Fallback fixture target must be selected.");
        CanvasPatchCapture missingTargetCapture = canvas.CaptureCurrentPatch(fallbackTarget!.Id);
        Assert.True(canvas.DeleteSelected(), "Fallback fixture target should be removable after request capture.");
        AiGeneratedImageApplyResult missingTargetFallback = panel.ApplyGeneratedImage(new AiGeneratedImageApplication(
            new AiGenerationResult("assets/generated/fallback.png", "image/png", 32),
            AiGenerationTarget.ReplaceSelected,
            workspace.ProjectRoot,
            projectId,
            sceneId,
            layerId,
            fallbackTarget.Id,
            missingTargetCapture,
            AiGenerationOperation.RegionEdit));
        Assert.True(missingTargetFallback.Success, missingTargetFallback.Message);
        Assert.Equal(beforeMissingTargetFallback + 1, layer.Images.Count,
            "A generated replacement whose target disappeared must still add one non-destructive complete-Q overlay.");
        ImageInstanceResource fallbackItem = layer.Images.OrderBy(item => item.Order).Last();
        Assert.Equal(0.0, fallbackItem.X, "Missing-target fallback must use the frozen Q X.");
        Assert.Equal(48.0, fallbackItem.Y, "Missing-target fallback must use the frozen Q Y.");

        int beforeCount = layer.Images.Count;
        CanvasPatchCapture addCapture = canvas.CaptureCurrentPatch("__agas_layer_patch__");
        var addApplication = new AiGeneratedImageApplication(
            new AiGenerationResult("assets/generated/add.png", "image/png", 32),
            AiGenerationTarget.AddToCurrentLayer,
            workspace.ProjectRoot,
            projectId,
            sceneId,
            layerId,
            null,
            addCapture,
            AiGenerationOperation.RegionEdit);
        AiGeneratedImageApplyResult added = panel.ApplyGeneratedImage(addApplication);
        Assert.True(added.Success, added.Message);
        Assert.Equal(beforeCount + 1, layer.Images.Count, "Add target should append an image to the current layer.");
        ImageInstanceResource patchItem = layer.Images.OrderBy(item => item.Order).Last();
        Assert.Equal(0.0, patchItem.X, "Layer output should start at the exact Q world X.");
        Assert.Equal(48.0, patchItem.Y, "Layer output should start at the exact Q world Y.");
        using (Image patchImage = Image.LoadFromFile(Path.Combine(_root, patchItem.ImagePath.Replace('/', Path.DirectorySeparatorChar))))
        {
            Assert.Equal(144, patchImage.GetWidth(), "Layer output width should equal the Q world-pixel width.");
            Assert.Equal(144, patchImage.GetHeight(), "Layer output height should equal the Q world-pixel height.");
        }
        Assert.True(
            TreeContainsPath(files, patchItem.ImagePath),
            $"Files tree should refresh and expose the locally composed patch path. Expected={patchItem.ImagePath}; Visible={VisibleTreePaths(files)}");
        Assert.True(canvas.Undo(), "Layer patch add should be undoable.");
        Assert.Equal(beforeCount, layer.Images.Count, "Undo should remove the applied patch instance.");
        Assert.True(canvas.Redo(), "Layer patch add should be redoable.");
        Assert.Equal(beforeCount + 1, layer.Images.Count, "Redo should restore the patch instance.");
        panel.DiscardGeneratedImage!(addApplication);
        Assert.False(File.Exists(Path.Combine(_root, "assets", "generated", "add.png")), "Consumed raw candidate should be cleaned after explicit apply.");

        int beforeLegacyWCount = layer.Images.Count;
        var legacyWApplication = new AiGeneratedImageApplication(
            new AiGenerationResult(
                "assets/generated/legacy-w.png",
                "image/png",
                32,
                AiGenerationEffectiveOperation.GuidedRegionEdit,
                AiGenerationEditGuarantee.GuidedReference,
                48,
                48),
            AiGenerationTarget.AddToCurrentLayer,
            workspace.ProjectRoot,
            projectId,
            sceneId,
            layerId,
            null,
            addCapture,
            AiGenerationOperation.RegionEdit);
        AiGeneratedImageApplyResult legacyWApplied = panel.ApplyGeneratedImage(legacyWApplication);
        Assert.True(legacyWApplied.Success, legacyWApplied.Message);
        Assert.Equal(beforeLegacyWCount + 1, layer.Images.Count,
            "A legacy W-only provider response must still apply as one complete-Q image.");
        ImageInstanceResource legacyWItem = layer.Images.OrderBy(item => item.Order).Last();
        using (Image legacyWImage = Image.LoadFromFile(Path.Combine(_root, legacyWItem.ImagePath.Replace('/', Path.DirectorySeparatorChar))))
        {
            Assert.Equal(144, legacyWImage.GetWidth(), "Legacy W-only response must be expanded to Q width.");
            Assert.Equal(144, legacyWImage.GetHeight(), "Legacy W-only response must be expanded to Q height.");
            Assert.True(legacyWImage.GetPixel(4, 4).A > 0.9f,
                "Legacy W-only response must retain frozen Q context outside W.");
            Assert.True(legacyWImage.GetPixel(72, 72).B > 0.8f,
                "Legacy W-only response must fill the transparent W hole.");
        }
        Assert.True(canvas.Undo(), "Legacy W-only complete-Q application should be undoable.");
        Assert.Equal(beforeLegacyWCount, layer.Images.Count,
            "Undo must remove the complete-Q image created from a legacy W-only response.");
        panel.DiscardGeneratedImage(legacyWApplication);

        canvas.ClearRegions();
        SetWorldRegion(canvas, CanvasRegionTool.Selection, new Rect2(0, 48, 144, 144));
        var qOnlyContext = generationContext with
        {
            SelectedImageId = (canvas.GetSelectedImage() as ImageInstanceResource)?.Id,
            RegionMetadata = new AiGenerationRegionMetadata(
                new AiGenerationGridRegion(0, 1, 3, 3),
                null)
        };
        CanvasPatchCapture qOnlyCapture = canvas.CaptureCurrentPatch(
            "__agas_layer_patch__",
            CanvasPatchLayerScope.CurrentLayer,
            layer.Id);
        AiGenerationPreparationResult qOnlyPrepared = panel.PrepareGenerationRequest(new AiGenerationPreparationInput(
            generationProfile,
            qOnlyContext,
            AiGenerationTarget.AddToCurrentLayer,
            "generate complete area"));
        Assert.True(qOnlyPrepared.Success, qOnlyPrepared.Message);
        Assert.NotNull(qOnlyPrepared.Request, "Q-only preparation must produce a provider request.");
        Assert.NotNull(qOnlyPrepared.PatchCapture, "Q-only preparation must preserve its immutable capture.");
        Assert.Equal(CanvasPatchGenerationMode.FullRegionGeneration, qOnlyPrepared.PatchCapture!.GenerationMode,
            "Q-only capture must use full-region generation mode.");
        Assert.Equal(AiGenerationOperation.ReferenceEdit, qOnlyPrepared.Request!.Operation,
            "Q-only requests must use reference edit.");
        Assert.Null(qOnlyPrepared.Request.EditInput, "Q-only requests must not fabricate an edit mask.");
        Assert.Equal(1, qOnlyPrepared.Request.ReferenceImages.Count, "Q-only requests must send exactly one complete Q image.");
        Assert.True(qOnlyPrepared.Request.ReferenceImages[0].Bytes.SequenceEqual(qOnlyCapture.ReplacementPatchPng),
            "Q-only reference input must be the complete Q composite crop.");
        using (Image qSource = LoadPng(qOnlyCapture.SourceCanvasPng))
        using (Image qReference = LoadPng(qOnlyPrepared.Request.ReferenceImages[0].Bytes))
        {
            Assert.Equal(144, qReference.GetWidth(), "Q-only reference width must match Q.");
            Assert.Equal(144, qReference.GetHeight(), "Q-only reference height must match Q.");
            Color sourcePixel = qSource.GetPixel(48, 48);
            Color referencePixel = qReference.GetPixel(48, 48);
            Assert.True(sourcePixel.A > 0.9f, "Q-only source must retain real canvas pixels instead of clearing Q.");
            Assert.True(referencePixel.IsEqualApprox(sourcePixel), "Q-only reference pixels must equal the complete Q composite.");
        }

        int beforeQOnlyCount = layer.Images.Count;
        var qOnlyApplication = new AiGeneratedImageApplication(
            new AiGenerationResult(scopedQOnlyCandidate, "image/png", 32),
            AiGenerationTarget.AddToCurrentLayer,
            workspace.ProjectRoot,
            projectId,
            sceneId,
            layerId,
            null,
            qOnlyPrepared.PatchCapture,
            AiGenerationOperation.ReferenceEdit,
            CanvasPatchLayerScope.CurrentLayer);
        AiGeneratedImageApplyResult qOnlyAdded = panel.ApplyGeneratedImage(qOnlyApplication);
        Assert.True(qOnlyAdded.Success, qOnlyAdded.Message);
        Assert.Equal(beforeQOnlyCount + 1, layer.Images.Count, "Q-only add must append one generated image.");
        ImageInstanceResource qOnlyItem = layer.Images.OrderBy(item => item.Order).Last();
        Assert.Equal(0.0, qOnlyItem.X, "Q-only result must start at Q world X.");
        Assert.Equal(48.0, qOnlyItem.Y, "Q-only result must start at Q world Y.");
        using (Image qOnlyImage = Image.LoadFromFile(Path.Combine(_root, qOnlyItem.ImagePath.Replace('/', Path.DirectorySeparatorChar))))
        {
            Assert.Equal(144, qOnlyImage.GetWidth(), "Q-only result width must equal Q width.");
            Assert.Equal(144, qOnlyImage.GetHeight(), "Q-only result height must equal Q height.");
        }
        panel.DiscardGeneratedImage(qOnlyApplication);
        Assert.False(File.Exists(Path.Combine(_root, scopedQOnlyCandidate.Replace('/', Path.DirectorySeparatorChar))),
            "Consumed scene/layer-scoped Q-only raw candidate should be cleaned after apply.");

        CanvasPatchCapture concurrentCapture = canvas.CaptureCurrentPatch(
            "__agas_layer_patch__",
            CanvasPatchLayerScope.CurrentLayer,
            layer.Id);
        string[] outOfOrderCandidates =
        {
            "assets/generated/concurrent-c.png",
            "assets/generated/concurrent-a.png",
            "assets/generated/concurrent-b.png",
        };
        var outOfOrderApplied = new List<(string Id, string ImagePath, double X, double Y)>();
        foreach (string candidate in outOfOrderCandidates)
        {
            int countBeforeApply = layer.Images.Count;
            var application = new AiGeneratedImageApplication(
                new AiGenerationResult(candidate, "image/png", 32),
                AiGenerationTarget.AddToCurrentLayer,
                workspace.ProjectRoot,
                projectId,
                sceneId,
                layerId,
                null,
                concurrentCapture,
                AiGenerationOperation.ReferenceEdit,
                CanvasPatchLayerScope.CurrentLayer);
            AiGeneratedImageApplyResult result = panel.ApplyGeneratedImage(application);
            Assert.True(result.Success, $"Out-of-order completion {candidate} must apply and autosave: {result.Message}");
            Assert.False(workspace.HasUnsavedChanges,
                "Each out-of-order completion must finish a complete save before the next main-thread callback.");
            Assert.Equal(countBeforeApply + 1, layer.Images.Count,
                "Each out-of-order completion must append exactly one frozen-region result.");
            ImageInstanceResource item = layer.Images.OrderBy(image => image.Order).Last();
            outOfOrderApplied.Add((item.Id, item.ImagePath, item.X, item.Y));
            panel.DiscardGeneratedImage(application);
        }
        Assert.Equal(3, outOfOrderApplied.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count(),
            "Out-of-order completions must retain three independent image instances.");
        Assert.Equal(3, outOfOrderApplied.Select(item => item.ImagePath).Distinct(StringComparer.Ordinal).Count(),
            "Out-of-order completions must retain three immutable composed assets without overwriting.");

        _concurrentReopenRoot = _root + "-concurrent-reopen";
        CopyDirectory(_root, _concurrentReopenRoot);
        var concurrentReopened = new MainWorkspace { CustomMinimumSize = new Vector2(1200, 760) };
        AddChild(concurrentReopened);
        await Frame();
        concurrentReopened.OpenProject(_concurrentReopenRoot);
        await Frame();
        AgasLayerResource concurrentReopenedLayer = concurrentReopened.CurrentScene!.Layers.Single(item => item.Id == layerId);
        foreach ((string id, string imagePath, double x, double y) in outOfOrderApplied)
        {
            ImageInstanceResource persisted = concurrentReopenedLayer.Images.Single(image => image.Id == id);
            Assert.Equal(imagePath, persisted.ImagePath,
                "The last out-of-order save must include every previously completed image reference.");
            Assert.Equal(x, persisted.X, "Out-of-order autosave must preserve the frozen X coordinate.");
            Assert.Equal(y, persisted.Y, "Out-of-order autosave must preserve the frozen Y coordinate.");
            Assert.True(File.Exists(Path.Combine(_concurrentReopenRoot, imagePath.Replace('/', Path.DirectorySeparatorChar))),
                "Every out-of-order immutable image asset must exist after reopening from disk.");
        }
        concurrentReopened.QueueFree();
        await Frame();

        CanvasPatchCapture saveFailureCapture = canvas.CaptureCurrentPatch(
            "__agas_layer_patch__",
            CanvasPatchLayerScope.CurrentLayer,
            layer.Id);
        var saveFailureApplication = new AiGeneratedImageApplication(
            new AiGenerationResult("assets/generated/save-fail.png", "image/png", 32),
            AiGenerationTarget.AddToCurrentLayer,
            workspace.ProjectRoot,
            projectId,
            sceneId,
            layerId,
            null,
            saveFailureCapture,
            AiGenerationOperation.ReferenceEdit,
            CanvasPatchLayerScope.CurrentLayer);
        Assert.False(workspace.HasUnsavedChanges,
            "The fixture must be clean before exercising the MCP-style apply persistence policy.");
        int beforeBridgeApplyCount = layer.Images.Count;
        AiGeneratedImageApplyResult bridgeApplied = bridge.Apply(
            saveFailureApplication,
            AiGenerationApplyPersistence.MarkDirtyOnly);
        Assert.True(bridgeApplied.Success && bridgeApplied.Applied, bridgeApplied.Message);
        Assert.True(bridgeApplied.Message.Contains("尚未保存", StringComparison.Ordinal),
            "MarkDirtyOnly must explicitly report that project resources were not persisted.");
        Assert.Equal(beforeBridgeApplyCount + 1, layer.Images.Count,
            "The MCP persistence policy must reuse the same canvas application logic.");
        Assert.False(workspace.HasUnsavedChanges,
            "The bridge must leave MCP dirty ownership to the facade instead of creating a parallel UI dirty flag.");

        string saveBlocker = AgasPaths.SceneFile(_root, sceneId) + ".tmp.tres";
        Directory.CreateDirectory(saveBlocker);
        int beforeSaveFailureCount = layer.Images.Count;
        AiGeneratedImageApplyResult appliedButUnsaved = panel.ApplyGeneratedImage(saveFailureApplication);
        Assert.False(appliedButUnsaved.Success, "A failed automatic save must not be reported as fully successful.");
        Assert.True(appliedButUnsaved.Applied, "A failed automatic save must preserve and report the canvas application.");
        Assert.True(appliedButUnsaved.Message.Contains("Ctrl/Cmd+S", StringComparison.Ordinal),
            "The autosave failure must explain how to retry manually.");
        Assert.Equal(beforeSaveFailureCount + 1, layer.Images.Count,
            "An automatic save failure must not roll back the generated image application.");
        Assert.True(workspace.HasUnsavedChanges, "An automatic save failure must keep the workspace dirty.");
        ImageInstanceResource saveFailureItem = layer.Images.OrderBy(item => item.Order).Last();
        string saveFailureItemId = saveFailureItem.Id;
        string saveFailureFinalPath = saveFailureItem.ImagePath;
        double saveFailureX = saveFailureItem.X;
        double saveFailureY = saveFailureItem.Y;
        Assert.True(File.Exists(Path.Combine(_root, saveFailureFinalPath.Replace('/', Path.DirectorySeparatorChar))),
            "The immutable composed PNG must remain on disk when automatic project saving fails.");
        panel.DiscardGeneratedImage(saveFailureApplication);
        Directory.Delete(saveBlocker);
        workspace._UnhandledKeyInput(new InputEventKey
        {
            Keycode = Key.S,
            PhysicalKeycode = Key.S,
            Pressed = true,
            CtrlPressed = true,
        });
        Assert.False(workspace.HasUnsavedChanges, "Ctrl/Cmd+S must persist an applied result after automatic save recovery.");

        workspace.QueueFree();
        await Frame();

        _recoveryReopenRoot = _root + "-recovery-reopen";
        CopyDirectory(_root, _recoveryReopenRoot);
        var recoveryReopened = new MainWorkspace { CustomMinimumSize = new Vector2(1200, 760) };
        AddChild(recoveryReopened);
        await Frame();
        recoveryReopened.OpenProject(_recoveryReopenRoot);
        await Frame();
        AgasLayerResource recoveredLayer = recoveryReopened.CurrentScene!.Layers.Single(item => item.Id == layerId);
        ImageInstanceResource recoveredImage = recoveredLayer.Images.Single(item => item.Id == saveFailureItemId);
        Assert.Equal(saveFailureFinalPath, recoveredImage.ImagePath,
            "Manual save recovery must persist the applied image reference in the scene.");
        Assert.Equal(saveFailureX, recoveredImage.X,
            "Manual save recovery must persist the frozen X coordinate.");
        Assert.Equal(saveFailureY, recoveredImage.Y,
            "Manual save recovery must persist the frozen Y coordinate.");
        Assert.True(File.Exists(Path.Combine(_recoveryReopenRoot, recoveredImage.ImagePath.Replace('/', Path.DirectorySeparatorChar))),
            "Manual save recovery must retain the referenced immutable composed PNG after reopening.");
        recoveryReopened.QueueFree();
        await Frame();
    }

    private async System.Threading.Tasks.Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private static void SetWorldRegion(CanvasWorkspace canvas, CanvasRegionTool tool, Rect2 worldRect)
    {
        canvas.SetActiveRegionTool(tool);
        Vector2 start = canvas.WorldToScreen(worldRect.Position + Vector2.One);
        Vector2 end = canvas.WorldToScreen(worldRect.End - Vector2.One);
        canvas._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = start });
        canvas._GuiInput(new InputEventMouseMotion { Position = end, Relative = end - start, ButtonMask = MouseButtonMask.Left });
        canvas._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = end });
    }

    private static bool TreeContainsPath(Tree tree, string expected)
    {
        TreeItem? root = tree.GetRoot();
        return root != null && Enumerate(root).Any(item => item.GetMetadata(0).AsString() == expected);
    }

    private static string VisibleTreePaths(Tree tree)
    {
        TreeItem? root = tree.GetRoot();
        return root == null
            ? "<no-root>"
            : string.Join(",", Enumerate(root)
                .Select(item => item.GetMetadata(0).AsString())
                .Where(path => !string.IsNullOrWhiteSpace(path)));
    }

    private static IEnumerable<TreeItem> Enumerate(TreeItem item)
    {
        for (TreeItem? child = item.GetFirstChild(); child != null; child = child.GetNext())
        {
            yield return child;
            foreach (TreeItem descendant in Enumerate(child))
            {
                yield return descendant;
            }
        }
    }

    private static T FindRequired<T>(Node root, string name) where T : Node
    {
        T? found = root.FindChild(name, recursive: true, owned: false) as T;
        return found ?? throw new InvalidOperationException($"Missing node: {name}");
    }

    private static void SaveImage(string path, Color color, int width = 8, int height = 8)
    {
        Image image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        image.Fill(color);
        Error error = image.SavePng(path);
        if (error != Error.Ok)
        {
            throw new InvalidOperationException($"Failed to save fixture image: {error}");
        }
    }

    private static Image LoadPng(byte[] png)
    {
        var image = new Image();
        if (image.LoadPngFromBuffer(png) != Error.Ok)
        {
            image.Dispose();
            throw new InvalidOperationException("Failed to load captured PNG.");
        }
        return image;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static class Assert
    {
        public static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        public static void False(bool value, string message) => True(!value, message);
        public static void Null(object? value, string message) => True(value == null, message);
        public static void NotNull(object? value, string message) => True(value != null, message);
        public static void Same(object expected, object? actual, string message) => True(ReferenceEquals(expected, actual), message);
        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException($"{message} Expected={expected?.ToString()} Actual={actual?.ToString()}");
            }
        }
    }
}
