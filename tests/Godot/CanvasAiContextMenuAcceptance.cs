using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AGAS.UI;
using AGAS.UI.Dialogs;
using Agas.Features.Canvas;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Features.Ai.UI;
using AppAgasCs.Features.Ai.Workspace;
using Godot;

public partial class CanvasAiContextMenuAcceptance : Node
{
    private const string Prefix = "[AGAS][CANVAS-AI-CONTEXT-MENU]";
    private string? _root;
    private string? _home;
    private string? _previousHome;

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
            System.Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, _previousHome);
            if (_root != null) { try { Directory.Delete(_root, recursive: true); } catch { } }
            if (_home != null) { try { Directory.Delete(_home, recursive: true); } catch { } }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GetTree().Quit(exitCode);
        }
    }

    private async System.Threading.Tasks.Task RunAll()
    {
        GetWindow().Size = new Vector2I(1200, 760);
        GetWindow().GuiEmbedSubwindows = true;
        await Frames(2);
        _root = Path.Combine(Path.GetTempPath(), "agas-ai-context-project-" + Guid.NewGuid().ToString("N"));
        _home = Path.Combine(Path.GetTempPath(), "agas-ai-context-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        SaveImage(Path.Combine(_root, "assets", "selected.png"));
        _previousHome = System.Environment.GetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable);
        System.Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, _home);

        var workspace = new MainWorkspace { CustomMinimumSize = new Vector2(1200, 760) };
        AddChild(workspace);
        await Frames(2);
        workspace.OpenProject(_root);
        await Frames(4);

        CanvasWorkspace canvas = FindRequired<CanvasWorkspace>(workspace, "CanvasWorkspaceHost");
        AiGenerationPanel panel = FindRequired<AiGenerationPanel>(workspace, "AiGenerationPanel");
        TextEdit prompt = FindRequired<TextEdit>(panel, "AiGenerationPrompt");
        Label regionStatus = FindRequired<Label>(panel, "AiGenerationRegionStatus");
        Label generationStatus = FindRequired<Label>(panel, "AiGenerationStatus");
        Label globalStatus = FindRequired<Label>(workspace, "SaveStatusLabel");
        TabContainer bottomTabs = FindRequired<TabContainer>(workspace, "BottomDockTabs");
        OptionButton profileSelector = FindRequired<OptionButton>(workspace, "AiGenerationProfileSelector");
        OptionButton providerSelector = FindRequired<OptionButton>(panel, "AiGenerationProvider");
        LineEdit modelNameInput = FindRequired<LineEdit>(panel, "AiGenerationModelName");
        OptionButton targetSelector = FindRequired<OptionButton>(panel, "AiGenerationTarget");
        PromptPanel promptPanel = FindRequired<PromptPanel>(workspace, "PromptPanel");
        Require(promptPanel.FindChild("AddLayerReferenceButton", true, false) == null
                && promptPanel.FindChild("LayerReferenceStatusLabel", true, false) == null
                && promptPanel.FindChild("LayerReferenceImageDialog", true, false) == null,
            "Prompt must not expose the obsolete per-layer reference picker or status row.");
        Button addPromptFragment = FindRequired<Button>(promptPanel, "AddPromptFragmentButton");
        Require(addPromptFragment.Text == "+ 片段", "Removing reference UI must preserve the prompt-fragment command.");
        AgasLayerResource layer = workspace.CurrentScene!.Layers.First(item => item.Id == workspace.CurrentScene.ActiveLayerId);
        layer.PromptFragments.Add(new PromptFragmentResource { Id = "disabled", Text = "must not appear", Enabled = false, Order = 1 });
        Require(canvas.TryAddImageFromProjectRelativePath("assets/selected.png", new Vector2(48, 96)), "Fixture image should be selected before right-click sending.");
        Require(profileSelector.ItemCount == 7, "Compact model selector must expose the unified catalog even before a connection is configured.");
        Require(bottomTabs.GetChildCount() == 2
                && bottomTabs.GetChild(0) is PromptPanel
                && bottomTabs.GetChild(1) is ToolboxPanel
                && panel.GetParent() != bottomTabs
                && !panel.Visible,
            "The bottom dock must contain Prompt and Tools while the AI task controller stays hidden.");
        Control toolbar = FindRequired<Control>(workspace, "WorkspaceToolbar");
        Require(!FindDescendants<Button>(toolbar).Any(button => button.Text == "AI 生图"),
            "The obsolete top AI-generation button must be removed.");

        Vector2 requestedPosition = new(320, 240);
        canvas._GuiInput(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Right,
            Pressed = true,
            Position = requestedPosition
        });
        await Frames(2);
        PopupMenu menu = FindRequired<PopupMenu>(workspace, "CanvasAiContextMenu");
        Require(menu.Visible, "Canvas right click must open its AI context menu.");
        PopupMenu toolMenu = FindRequired<PopupMenu>(workspace, "CanvasToolMenu");
        foreach (PopupMenu popup in new[] { menu, toolMenu })
        {
            StyleBox popupPanel = popup.GetThemeStylebox("panel");
            Require(popupPanel.ContentMarginLeft == 12 && popupPanel.ContentMarginRight == 12
                    && popupPanel.ContentMarginTop == 10 && popupPanel.ContentMarginBottom == 10,
                "Canvas menus must retain padding on all four sides.");
            Require(popup.GetThemeConstant("item_start_padding") == 8
                    && popup.GetThemeConstant("item_end_padding") == 8
                    && popup.GetThemeConstant("v_separation") == 12,
                "Canvas menu text must have horizontal and vertical breathing room.");
            Require(!popup.ShrinkWidth && popup.WrapControls,
                "Canvas menus must preserve their requested width while growing to fit their contents.");
            Require(!popup.HasThemeFontSizeOverride("font_size"),
                "Canvas menu spacing must not introduce a font-size override.");
        }
        int menuFontSize = menu.GetThemeFontSize("font_size");
        RequireCanvasMenuFits(menu, "main-first-popup", menuFontSize);
        string[] expectedItems = { "发送 AI 填充", "仅其他可见图层发送", "所有图层发送", "自定义提示词发送", "自定义 + 所有图层" };
        Require(Enumerable.Range(0, menu.ItemCount).Any(index => menu.GetItemText(index) == "工具箱"),
            "Canvas context menu must expose the toolbox submenu.");
        int[] commandIndexes = expectedItems.Select(text => Enumerable.Range(0, menu.ItemCount)
                .Single(index => menu.GetItemText(index) == text))
            .ToArray();
        string[] actualItems = commandIndexes
            .Select(menu.GetItemText)
            .ToArray();
        Require(actualItems.SequenceEqual(expectedItems), "Canvas AI menu text must match the reference project.");
        Require(commandIndexes.All(menu.IsItemDisabled), "All AI send commands must be disabled until Q is selected.");
        Require(!menu.HideOnItemSelection, "The send menu must stay visible long enough to paint a loading label.");

        int mergeIndex = Enumerable.Range(0, menu.ItemCount)
            .Single(index => menu.GetItemText(index) == "合并可见图层到当前层");
        string mergeText = menu.GetItemText(mergeIndex);
        menu.Hide();
        await Frames(2);
        menu.SetItemText(mergeIndex, mergeText + "（长文字回归：完整显示并保留左右边距）");
        menu.Position = new Vector2I(100, 100);
        menu.Popup();
        await Frames(2);
        RequireCanvasMenuFits(menu, "main-long-label", menuFontSize);
        Require(menu.Size.X > 360, "Long main-menu text must expand beyond the minimum width.");

        menu.Hide();
        await Frames(2);
        menu.SetItemText(mergeIndex, mergeText);
        menu.Position = GetWindow().Size - new Vector2I(380, 2);
        menu.Popup();
        await Frames(2);
        RequireCanvasMenuFits(menu, "main-bottom-edge-reopen", menuFontSize);
        Require(menu.Size.X == Math.Max(360, Mathf.CeilToInt(menu.GetContentsMinimumSize().X * menu.ContentScaleFactor)),
            "Reopening with normal text must restore the owner minimum or natural content width.");

        int toolboxIndex = Enumerable.Range(0, menu.ItemCount)
            .Single(index => menu.GetItemText(index) == "工具箱");
        menu.SetFocusedItem(toolboxIndex);
        GetWindow().PushInput(new InputEventKey { Keycode = Key.Right, Pressed = true });
        await Frames(2);
        RequireCanvasMenuFits(toolMenu, "submenu-first-popup", menuFontSize);
        toolMenu.Hide();
        await Frames(2);
        string toolText = toolMenu.GetItemText(0);
        toolMenu.SetItemText(0, "工具箱长文字回归：完整显示所有字符并保留菜单四周留白和文字内边距");
        menu.SetFocusedItem(toolboxIndex);
        GetWindow().PushInput(new InputEventKey { Keycode = Key.Right, Pressed = true });
        await Frames(2);
        RequireCanvasMenuFits(toolMenu, "submenu-long-label", menuFontSize);
        Require(toolMenu.Size.X > 360, "Long submenu text must expand beyond the minimum width.");
        toolMenu.Hide();
        await Frames(2);
        toolMenu.SetItemText(0, toolText);
        menu.SetFocusedItem(toolboxIndex);
        GetWindow().PushInput(new InputEventKey { Keycode = Key.Right, Pressed = true });
        await Frames(2);
        RequireCanvasMenuFits(toolMenu, "submenu-reopen", menuFontSize);
        Require(toolMenu.Size.X == Math.Max(360, Mathf.CeilToInt(toolMenu.GetContentsMinimumSize().X * toolMenu.ContentScaleFactor)),
            "Reopening a submenu must restore the owner minimum or natural content width.");
        toolMenu.Hide();
        menu.Hide();
        await Frames(1);
        Button toolboxMenuButton = FindRequired<Button>(workspace, "CanvasToolboxMenuButton");
        toolboxMenuButton.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(2);
        Require(menu.Visible, "The visible toolbox button must open the same canvas context menu.");

        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 1L);
        await Frames(3);
        Require(bottomTabs.CurrentTab == 0 && !bottomTabs.IsVisibleInTree(),
            "AI send must not open the bottom dock or switch away from Prompt.");
        Require(regionStatus.Text.Contains("当前活动图层", StringComparison.Ordinal), "Current-layer send must pass current-layer scope.");
        Require(generationStatus.Text.Contains("请先框选 Q", StringComparison.Ordinal), "Missing Q must show an explicit message and not send a request.");
        Require(prompt.Text == MainWorkspace.DefaultCanvasAiFillPrompt, "An empty active-layer prompt must use the explicit default Q-generation prompt.");
        Require(targetSelector.GetSelectedId() == (int)AiGenerationTarget.AddToCurrentLayer, "Right-click sending must add a patch even when an image is selected.");
        Require(GetViewport().GuiGetFocusOwner() != prompt, "AI send must not focus the hidden prompt editor.");

        SetWorldRegion(canvas, CanvasRegionTool.Selection, new Rect2(48, 96, 48, 48));
        canvas._GuiInput(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Right,
            Pressed = true,
            Position = requestedPosition
        });
        await Frames(2);
        int otherVisibleIndex = menu.GetItemIndex(6);
        Require(commandIndexes.Where(index => index != otherVisibleIndex).All(index => !menu.IsItemDisabled(index)),
            "Q-only selection must enable AI send commands that do not require another visible layer.");
        Require(menu.IsItemDisabled(otherVisibleIndex),
            "Background-only send must remain disabled when no other visible layer exists.");
        var backgroundLayer = new AgasLayerResource
        {
            Id = "legacy-reference-background",
            Name = "参考背景",
            Order = layer.Order - 1,
            Visible = true,
            Locked = true,
        };
        backgroundLayer.Images.Add(new ImageInstanceResource
        {
            Id = "legacy-reference-image",
            ImagePath = "assets/selected.png",
        });
        workspace.CurrentScene.Layers.Add(backgroundLayer);
        canvas._GuiInput(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Right,
            Pressed = true,
            Position = requestedPosition
        });
        await Frames(2);
        Require(!menu.IsItemDisabled(otherVisibleIndex),
            "A visible locked legacy-reference background must enable background-only send without crashing Prompt.");
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 6L);
        await Frames(3);
        Require(regionStatus.Text.Contains("仅其他可见图层", StringComparison.Ordinal),
            "Background-only send must pass VisibleLayersExceptCurrent into the generation context.");
        Require(targetSelector.GetSelectedId() == (int)AiGenerationTarget.AddToCurrentLayer,
            "Background-only references must never redirect generated output away from the current layer.");

        var guardedProfile = new AgasAiProfile(
            "profile-generation-guard",
            "门禁计数连接",
            AgasAiProviderIds.OpenAiCompatible,
            "gpt-image-2",
            "https://provider-must-not-be-called.example/v1/");
        var guardedStore = new AgasAiSettingsStore(_home);
        guardedStore.SaveProfiles(new AgasAiProfilesDocument(
            AgasAiProfilesDocument.CurrentResourceVersion,
            guardedProfile.Id,
            guardedProfile.ModelName,
            new[] { guardedProfile }));
        guardedStore.SetCredential(guardedProfile.Id, "valid-test-credential");
        panel.ReloadProfiles(guardedProfile.Id);
        var countingRunner = new CountingRunner();
        IAiGenerationRunner originalRunner = ReplaceRunner(panel, countingRunner);

        layer.Visible = false;
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 1L);
        await Frames(2);
        AcceptDialog blockedDialog = FindRequired<AcceptDialog>(workspace, "AiGenerationBlockedDialog");
        Label blockedMessage = FindRequired<Label>(blockedDialog, "AiGenerationBlockedMessage");
        ScrollContainer blockedMessageScroll = FindRequired<ScrollContainer>(blockedDialog, "AiGenerationBlockedMessageScroll");
        MarginContainer blockedContentMargin = FindRequired<MarginContainer>(blockedDialog, "AiGenerationBlockedContentMargin");
        Require(blockedDialog.Visible
                && blockedDialog.Title == "无法开始生图"
                && blockedDialog.DialogText.Contains("已隐藏", StringComparison.Ordinal)
                && blockedDialog.DialogText.Contains("眼睛图标", StringComparison.Ordinal)
                && blockedDialog.DialogText.Contains("未发送生图请求", StringComparison.Ordinal),
            "A hidden target layer must show a direct recovery dialog before generation starts.");
        Require(blockedDialog.DialogAutowrap
                && !blockedDialog.GetLabel().Visible
                && blockedMessage.Text == blockedDialog.DialogText
                && blockedMessage.AutowrapMode == TextServer.AutowrapMode.WordSmart
                && blockedMessage.SizeFlagsHorizontal == Control.SizeFlags.ExpandFill
                && blockedMessage.SizeFlagsVertical == Control.SizeFlags.ExpandFill
                && blockedMessage.CustomMinimumSize.X == 0
                && blockedContentMargin.CustomMinimumSize.X == 0
                && blockedMessageScroll.HorizontalScrollMode == ScrollContainer.ScrollMode.Disabled
                && blockedMessageScroll.VerticalScrollMode == ScrollContainer.ScrollMode.Auto,
            "The blocked-generation dialog must use its padded, wrapping message layout.");
        Require(blockedContentMargin.GetThemeConstant("margin_left") >= 20
                && blockedContentMargin.GetThemeConstant("margin_right") >= 20
                && blockedContentMargin.GetThemeConstant("margin_top") >= 16
                && blockedContentMargin.GetThemeConstant("margin_bottom") >= 16,
            $"The blocked-generation dialog must keep comfortable content padding; actual margins are " +
            $"{blockedContentMargin.GetThemeConstant("margin_left")}/" +
            $"{blockedContentMargin.GetThemeConstant("margin_top")}/" +
            $"{blockedContentMargin.GetThemeConstant("margin_right")}/" +
            $"{blockedContentMargin.GetThemeConstant("margin_bottom")}.");
        blockedDialog.Theme = AgasIdeTheme.Create(28);
        blockedDialog.Hide();
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 1L);
        await Frames(3);
        Require(blockedDialog.Visible
                && blockedDialog.Size.X <= 560
                && blockedDialog.Size.Y >= 220
                && blockedDialog.Size.Y <= 360,
            "The blocked-generation dialog must stay compact and grow vertically instead of overflowing at 28px UI text.");
        Require(blockedMessage.GetVisibleLineCount() == blockedMessage.GetLineCount()
                && !blockedMessageScroll.GetGlobalRect().Intersects(blockedDialog.GetOkButton().GetGlobalRect()),
            "Large dialog text must remain fully visible without overlapping the acknowledgement button.");

        blockedDialog.Theme = AgasIdeTheme.Create(48);
        blockedDialog.Hide();
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 1L);
        await Frames(3);
        Rect2I supportedViewport = new(Vector2I.Zero, (Vector2I)GetViewport().GetVisibleRect().Size);
        Rect2I supportedDialogRect = new(blockedDialog.Position, blockedDialog.Size);
        Require(supportedViewport.Grow(-24).Encloses(supportedDialogRect)
                && blockedDialog.Size.X <= 560
                && blockedDialog.Size.Y <= 360,
            $"The 48px dialog must fit the supported 1200x760 viewport; viewport={supportedViewport}, dialog={supportedDialogRect}.");
        Require(blockedMessage.GetVisibleLineCount() == blockedMessage.GetLineCount()
                && !blockedMessage.ClipText
                && !blockedMessageScroll.GetGlobalRect().Intersects(blockedDialog.GetOkButton().GetGlobalRect()),
            "The supported 48px layout must preserve readable, accessible text above the acknowledgement button.");
        VScrollBar supportedScrollBar = blockedMessageScroll.GetVScrollBar();
        Require(supportedScrollBar.Visible
                && supportedScrollBar.MaxValue > supportedScrollBar.Page
                && blockedMessage.Size.Y > blockedMessageScroll.Size.Y,
            $"The supported 48px message must expose real scroll range; max={supportedScrollBar.MaxValue}, page={supportedScrollBar.Page}, content={blockedMessage.Size.Y}, viewport={blockedMessageScroll.Size.Y}.");
        blockedMessageScroll.ScrollVertical = (int)Math.Ceiling(supportedScrollBar.MaxValue - supportedScrollBar.Page);
        await Frames(2);
        Require(blockedMessageScroll.ScrollVertical > 0
                && blockedMessageScroll.ScrollVertical + blockedMessageScroll.Size.Y >= blockedMessage.Size.Y - 1,
            "The supported 48px message must scroll far enough to reveal its final line.");
        blockedMessageScroll.ScrollVertical = 0;
        blockedDialog.Theme = AgasIdeTheme.Create(28);

        Vector2 previousWorkspaceMinimum = workspace.CustomMinimumSize;
        workspace.CustomMinimumSize = Vector2.Zero;
        GetWindow().Size = new Vector2I(420, 520);
        await Frames(3);
        blockedDialog.Hide();
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 1L);
        await Frames(3);
        Rect2I narrowViewport = new(Vector2I.Zero, (Vector2I)GetViewport().GetVisibleRect().Size);
        Rect2I narrowDialogRect = new(blockedDialog.Position, blockedDialog.Size);
        Rect2I paddedViewport = narrowViewport.Grow(-24);
        Require(blockedDialog.Visible
                && paddedViewport.Encloses(narrowDialogRect),
            $"The blocked-generation dialog must preserve 24px viewport gutters at narrow sizes; viewport={narrowViewport}, dialog={narrowDialogRect}.");
        Require(blockedMessage.GetVisibleLineCount() == blockedMessage.GetLineCount()
                && !blockedMessageScroll.GetGlobalRect().Intersects(blockedDialog.GetOkButton().GetGlobalRect()),
            "Narrow dialog text must wrap without clipping or overlapping its acknowledgement button.");

        blockedDialog.Theme = AgasIdeTheme.Create(48);
        blockedDialog.Hide();
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 1L);
        await Frames(3);
        narrowViewport = new Rect2I(Vector2I.Zero, (Vector2I)GetViewport().GetVisibleRect().Size);
        narrowDialogRect = new Rect2I(blockedDialog.Position, blockedDialog.Size);
        paddedViewport = narrowViewport.Grow(-24);
        Require(blockedDialog.Visible
                && paddedViewport.Encloses(narrowDialogRect)
                && blockedDialog.Size.Y <= 360,
            $"The 48px blocked-generation dialog must stay inside narrow viewport gutters; viewport={narrowViewport}, dialog={narrowDialogRect}.");
        Require(blockedMessage.GetVisibleLineCount() == blockedMessage.GetLineCount()
                && !blockedMessage.ClipText
                && !blockedMessageScroll.GetGlobalRect().Intersects(blockedDialog.GetOkButton().GetGlobalRect())
                && blockedMessageScroll.GetVScrollBar().Visible
                && blockedMessageScroll.GetVScrollBar().MaxValue > blockedMessageScroll.GetVScrollBar().Page
                && blockedMessage.Size.Y > blockedMessageScroll.Size.Y,
            "The 48px message must remain scrollable without text clipping or button overlap.");
        VScrollBar narrowScrollBar = blockedMessageScroll.GetVScrollBar();
        blockedMessageScroll.ScrollVertical = (int)Math.Ceiling(narrowScrollBar.MaxValue - narrowScrollBar.Page);
        await Frames(2);
        Require(blockedMessageScroll.ScrollVertical > 0
                && blockedMessageScroll.ScrollVertical + blockedMessageScroll.Size.Y >= blockedMessage.Size.Y - 1,
            "The narrow 48px message must scroll far enough to reveal its final line.");
        blockedMessageScroll.ScrollVertical = 0;
        blockedDialog.Hide();
        GetWindow().Size = new Vector2I(1200, 760);
        workspace.CustomMinimumSize = previousWorkspaceMinimum;
        blockedDialog.Theme = AgasIdeTheme.Create(28);
        await Frames(3);
        Require(!panel.IsPreparingGeneration && panel.ActiveGenerationCount == 0,
            "A hidden target layer must not prepare or send a provider request.");
        Require(countingRunner.RequestCount == 0,
            "A hidden target layer must stop before the injected provider runner.");
        Require(commandIndexes.All(index => menu.GetItemText(index) != "发送中…"),
            "A hidden-layer rejection must not leave an AI menu command in its loading state.");
        blockedDialog.Hide();

        layer.Visible = true;
        layer.Locked = true;
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 3L);
        await Frames(2);
        Require(blockedDialog.Visible
                && blockedDialog.DialogText.Contains("已锁定", StringComparison.Ordinal)
                && blockedDialog.DialogText.Contains("锁图标", StringComparison.Ordinal)
                && blockedDialog.DialogText.Contains("未发送生图请求", StringComparison.Ordinal),
            "A locked target layer must show a direct recovery dialog before opening the custom prompt.");
        Require(workspace.FindChild("CanvasAiCustomPromptDialog", recursive: true, owned: false) == null,
            "A locked target layer must be rejected before the custom-prompt dialog is created.");
        Require(!panel.IsPreparingGeneration && panel.ActiveGenerationCount == 0,
            "A locked target layer must not prepare or send a provider request.");
        Require(countingRunner.RequestCount == 0,
            "A locked target layer must stop before the injected provider runner.");
        blockedDialog.Hide();
        layer.Locked = false;

        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 3L);
        await Frames(2);
        TextInputDialog guardedCustomDialog = FindRequired<TextInputDialog>(workspace, "CanvasAiCustomPromptDialog");
        LineEdit guardedCustomInput = FindRequired<LineEdit>(guardedCustomDialog, "TextInput");
        string promptBeforeLockedConfirmation = prompt.Text;
        layer.Locked = true;
        guardedCustomInput.Text = "must-not-send";
        guardedCustomDialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
        await Frames(2);
        Require(blockedDialog.Visible
                && blockedDialog.DialogText.Contains("已锁定", StringComparison.Ordinal),
            "Custom-prompt confirmation must recheck the target layer and show the lock dialog.");
        Require(prompt.Text == promptBeforeLockedConfirmation
                && !panel.IsPreparingGeneration
                && panel.ActiveGenerationCount == 0,
            "Locking the target while the custom prompt is open must stop before provider preparation.");
        Require(countingRunner.RequestCount == 0,
            "A target locked during custom-prompt entry must stop before the injected provider runner.");
        blockedDialog.Hide();
        layer.Locked = false;

        layer.Visible = false;
        layer.Locked = true;
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 2L);
        await Frames(2);
        Require(blockedDialog.Visible
                && blockedDialog.DialogText.Contains("已隐藏且已锁定", StringComparison.Ordinal)
                && blockedDialog.DialogText.Contains("眼睛图标", StringComparison.Ordinal)
                && blockedDialog.DialogText.Contains("锁图标", StringComparison.Ordinal),
            "A hidden and locked target layer must explain both recovery actions in one dialog.");
        Require(countingRunner.RequestCount == 0
                && !panel.IsPreparingGeneration
                && panel.ActiveGenerationCount == 0,
            "A hidden and locked target layer must stop before the injected provider runner.");
        blockedDialog.Hide();
        layer.Visible = true;
        layer.Locked = false;

        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 1L);
        await Frames(3);
        Require(countingRunner.RequestCount == 1 && panel.ActiveGenerationCount == 1,
            "The unlocked positive control must reach the injected runner with the same valid profile and credential.");
        FindRequired<Button>(panel, "AiCancelButton").EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(2);
        Require(panel.ActiveGenerationCount == 0,
            "The injected positive-control request must be canceled before restoring the production runner.");
        ReplaceRunner(panel, originalRunner);

        var defaultContext = new AiGenerationCanvasContext(
            workspace.ProjectRoot,
            workspace.CurrentProjectId,
            workspace.CurrentScene.Id,
            layer.Id,
            HasSelectedImage: true,
            SelectedImageId: (canvas.GetSelectedImage() as ImageInstanceResource)?.Id,
            CanEditCurrentLayer: true,
            GridSizePx: canvas.GetGridSizePx(),
            RegionMetadata: new AiGenerationRegionMetadata(
                new AiGenerationGridRegion(1, 2, 1, 1),
                null),
            LayerScope: CanvasPatchLayerScope.CurrentLayer);
        AiGenerationPreparationResult defaultPrepared = panel.PrepareGenerationRequest!(new AiGenerationPreparationInput(
            new AiGenerationProviderProfile(
                "default-prompt-check",
                "Default Prompt Check",
                "openai-compatible",
                "gpt-image-2",
                "unused-test-key",
                "https://relay.example/v1/"),
            defaultContext,
            AiGenerationTarget.AddToCurrentLayer,
            prompt.Text));
        Require(defaultPrepared.Success && defaultPrepared.Request != null, "The fallback prompt must prepare a valid Q-only request.");
        Require(defaultPrepared.Request!.Operation == AiGenerationOperation.ReferenceEdit,
            "Q-only preparation must use reference edit instead of a synthetic region mask.");
        Require(defaultPrepared.Request.EditInput == null && defaultPrepared.Request.ReferenceImages.Count == 1,
            "Q-only preparation must send one complete Q reference image without edit input.");
        Require(defaultPrepared.Request!.Prompt.StartsWith(MainWorkspace.DefaultCanvasAiFillPrompt, StringComparison.Ordinal),
            "An empty layer prompt request must contain the explicit default Q-generation prompt.");

        var profile = new AgasAiProfile(
            "profile-after-startup",
            "设置后新增中转站",
            AgasAiProviderIds.OpenAiCompatible,
            "gpt-image-2",
            "https://relay.example/v1/");
        new AgasAiSettingsStore(_home).SaveProfiles(new AgasAiProfilesDocument(
            AgasAiProfilesDocument.CurrentResourceVersion,
            profile.Id,
            new[] { profile }));
        layer.PromptFragments.Add(new PromptFragmentResource { Id = "base-1", Text = "base prompt", Enabled = true, Order = 0 });
        canvas.GrabFocus();
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 1L);
        Require(menu.GetItemText(commandIndexes[0]) == "发送中…",
            "A successful send click must immediately switch the menu item to a loading label.");
        await Frames(2);
        Require(profileSelector.ItemCount == 7
                && Enumerable.Range(0, profileSelector.ItemCount).All(index =>
                    !profileSelector.GetItemText(index).Contains(profile.Name, StringComparison.Ordinal)),
            "Right-click sending must keep the compact selector model-only.");
        Require(providerSelector.GetItemMetadata(providerSelector.Selected).AsString() == AgasAiUnifiedModelCatalog.ConnectionAdapterKind
                && modelNameInput.Text == profile.ModelName,
            "The first connection loaded after startup must use the global model and unified adapter.");
        Require(generationStatus.Text.Contains("API Key", StringComparison.Ordinal), "The newly reloaded relay must reach credential validation without issuing a network request.");
        Require(globalStatus.Text.Contains("API Key", StringComparison.Ordinal),
            "Hidden-panel errors must be forwarded to the existing global status.");
        Require(bottomTabs.CurrentTab == 0 && !bottomTabs.IsVisibleInTree(),
            "A real enabled AI send must leave the Prompt dock collapsed.");
        Require(GetViewport().GuiGetFocusOwner() == canvas,
            "A real enabled AI send must preserve canvas focus instead of focusing hidden prompt UI.");
        Require(prompt.Text.StartsWith("base prompt", StringComparison.Ordinal)
                && !prompt.Text.StartsWith(MainWorkspace.DefaultCanvasAiFillPrompt, StringComparison.Ordinal),
            "Configured layer fragments must replace the fallback prompt while preserving the canonical generation task.");
        Require(targetSelector.GetSelectedId() == (int)AiGenerationTarget.AddToCurrentLayer, "Current-layer sending must keep the fixed add target.");

        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 2L);
        await Frames(2);
        Require(regionStatus.Text.Contains("所有可见图层", StringComparison.Ordinal), "All-layer send must pass all-visible scope.");
        Require(generationStatus.Text.Contains("API Key", StringComparison.Ordinal), "Missing relay credentials must stay in the panel without issuing an uncontrolled request.");
        Require(targetSelector.GetSelectedId() == (int)AiGenerationTarget.AddToCurrentLayer, "All-layer sending must keep the fixed add target.");
        Require(!Directory.Exists(Path.Combine(_root, "assets", "generated")), "No generated artifact may appear without a configured relay.");

        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 3L);
        await Frames(2);
        TextInputDialog customDialog = FindRequired<TextInputDialog>(workspace, "CanvasAiCustomPromptDialog");
        LineEdit customInput = FindRequired<LineEdit>(customDialog, "TextInput");
        customInput.Text = "custom detail";
        customDialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
        await Frames(3);
        Require(prompt.Text.StartsWith("base prompt\ncustom detail", StringComparison.Ordinal),
            "Custom send must append the collected prompt before the canonical generation task.");
        Require(regionStatus.Text.Contains("当前活动图层", StringComparison.Ordinal), "Custom current-layer send must preserve current-layer scope.");
        Require(targetSelector.GetSelectedId() == (int)AiGenerationTarget.AddToCurrentLayer, "Custom current-layer sending must keep the fixed add target.");
        Require(bottomTabs.CurrentTab == 0 && !bottomTabs.IsVisibleInTree()
                && GetViewport().GuiGetFocusOwner() != prompt,
            "Custom sending must not expose or focus the hidden AI panel.");

        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 4L);
        await Frames(2);
        customInput.Text = string.Empty;
        customDialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
        await Frames(3);
        Require(generationStatus.Text.Contains("自定义提示词为空", StringComparison.Ordinal), "Empty custom prompt must disable sending with an explicit message.");
        Require(regionStatus.Text.Contains("所有可见图层", StringComparison.Ordinal), "Custom all-layer send must preserve all-visible scope.");
        Require(targetSelector.GetSelectedId() == (int)AiGenerationTarget.AddToCurrentLayer, "Custom all-layer sending must keep the fixed add target.");
    }

    private static void SaveImage(string path)
    {
        using Image image = Image.CreateEmpty(16, 16, false, Image.Format.Rgba8);
        image.Fill(Colors.CornflowerBlue);
        Error error = image.SavePng(path);
        if (error != Error.Ok)
        {
            throw new InvalidOperationException($"Failed to save fixture image: {error}");
        }
    }

    private static IAiGenerationRunner ReplaceRunner(AiGenerationPanel panel, IAiGenerationRunner replacement)
    {
        FieldInfo field = typeof(AiGenerationPanel).GetField("_runner", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AiGenerationPanel runner field is unavailable.");
        var previous = field.GetValue(panel) as IAiGenerationRunner
            ?? throw new InvalidOperationException("AiGenerationPanel runner is unavailable.");
        field.SetValue(panel, replacement);
        return previous;
    }

    private sealed class CountingRunner : IAiGenerationRunner
    {
        public int RequestCount { get; private set; }

        public Task<AiGenerationResult> GenerateAsync(
            AiGenerationProviderProfile profile,
            AiGenerationRequest request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var completion = new TaskCompletionSource<AiGenerationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return completion.Task;
        }
    }

    private static void SetWorldRegion(CanvasWorkspace canvas, CanvasRegionTool tool, Rect2 worldRect)
    {
        canvas.SetActiveRegionTool(tool);
        Vector2 start = canvas.WorldToScreen(worldRect.Position + Vector2.One);
        Vector2 end = canvas.WorldToScreen(worldRect.End - Vector2.One);
        canvas._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = start });
        canvas._GuiInput(new InputEventMouseMotion { Position = end, Relative = end - start, ButtonMask = MouseButtonMask.Left });
        canvas._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = end });
    }

    private async System.Threading.Tasks.Task Frames(int count)
    {
        for (int index = 0; index < count; index++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private void RequireCanvasMenuFits(PopupMenu popup, string scenario, int fontSize)
    {
        Vector2 contentsMinimum = popup.GetContentsMinimumSize() * popup.ContentScaleFactor;
        Require(popup.Visible, $"{scenario}: the menu must actually be open.");
        Require(popup.Size.X >= 360 && popup.Size.X >= Mathf.CeilToInt(contentsMinimum.X),
            $"{scenario}: actual popup width must respect the minimum and fit the longest label.");
        Require(popup.Size.Y >= Mathf.CeilToInt(contentsMinimum.Y),
            $"{scenario}: actual popup height must fit the final row and bottom padding.");
        Require(popup.Position.Y >= 0 && popup.Position.Y + popup.Size.Y <= GetWindow().Size.Y,
            $"{scenario}: the bottom row must remain inside the parent window.");
        Require(popup.Position.X >= 0 && popup.Position.X + popup.Size.X <= GetWindow().Size.X,
            $"{scenario}: the entire menu width must remain inside the parent window.");
        Require(popup.GetThemeFontSize("font_size") == fontSize && !popup.HasThemeFontSizeOverride("font_size"),
            $"{scenario}: opening and resizing must not change the font size.");
        PanelContainer panel = popup.GetChildren(includeInternal: true).OfType<PanelContainer>().Single();
        ScrollContainer scroll = panel.GetChildren(includeInternal: true).OfType<ScrollContainer>().Single();
        Require(!scroll.GetVScrollBar().IsVisibleInTree() && scroll.ScrollVertical == 0,
            $"{scenario}: all rows must fit without vertical clipping or scrolling.");
        GD.Print($"{Prefix}[MENU-LAYOUT] {scenario} size={popup.Size} min={popup.MinSize} contents={contentsMinimum} "
            + $"position={popup.Position} font={fontSize} bottom-padding={popup.GetThemeStylebox("panel").ContentMarginBottom}");
    }

    private static T FindRequired<T>(Node root, string name) where T : Node =>
        root.FindChild(name, recursive: true, owned: false) as T
        ?? throw new InvalidOperationException($"Required node is missing: {name}");

    private static System.Collections.Generic.IEnumerable<T> FindDescendants<T>(Node root) where T : Node
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is T typed)
            {
                yield return typed;
            }
            foreach (T descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
