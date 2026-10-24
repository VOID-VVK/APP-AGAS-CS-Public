#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AGAS.UI;
using AppAgasCs.Application;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Prompting;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class PromptPanelLayoutAcceptance : Node
{
    private const string Prefix = "[AGAS][PROMPT-PANEL-LAYOUT]";
    private int _checks;

    public override async void _Ready()
    {
        int exitCode = 0;
        try
        {
            await RunAsync();
            GD.Print($"{Prefix}[PASS] {_checks} checks");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] after {_checks} checks: {exception}");
        }
        finally
        {
            GetTree().Quit(exitCode);
        }
    }

    private async Task RunAsync()
    {
        var tabs = new TabContainer { Size = new Vector2(480, 320) };
        var panel = new PromptPanel();
        AddChild(tabs);
        tabs.AddChild(panel);
        await Frames(3);

        var split = FindRequired<HSplitContainer>(panel, "PromptPreviewSplit");
        var fragments = FindRequired<Control>(panel, "PromptFragmentSection");
        var previewSection = FindRequired<Control>(panel, "PromptMergedPreviewSection");
        var preview = FindRequired<TextEdit>(panel, "PromptMergedPreview");
        var add = FindRequired<Button>(panel, "AddPromptFragmentButton");

        Require(split.GetChild(0) == fragments && split.GetChild(1) == previewSection,
            "Prompt fragments must be left of the merged preview.");
        Require(previewSection.GlobalPosition.X > fragments.GlobalPosition.X,
            "Prompt sections did not lay out horizontally.");
        Require(!ContainsLabel(panel, "提示词") && add.GetParent()?.GetParent() == fragments,
            "The duplicate title must be removed while the fragment command remains on the left.");

        // The real bottom dock can lay out briefly while narrow before the 1600px
        // workspace is ready. The divider must still center at its final width.
        tabs.Size = new Vector2(1600, 320);
        await Frames(3);
        Require(split.Size.X > 1500 && Math.Abs(previewSection.Size.X - fragments.Size.X) <= 2,
            $"An untouched 1600px dock must center its divider (left={fragments.Size.X}, right={previewSection.Size.X}).");

        tabs.Size = new Vector2(1900, 320);
        await Frames(3);
        Require(Math.Abs(previewSection.Size.X - fragments.Size.X) <= 2,
            $"An untouched dock must stay centered after resize (left={fragments.Size.X}, right={previewSection.Size.X}).");

        tabs.Hide();
        await Frames(2);
        tabs.Show();
        await Frames(3);
        Require(Math.Abs(previewSection.Size.X - fragments.Size.X) <= 2,
            $"An untouched dock must stay centered after being shown (left={fragments.Size.X}, right={previewSection.Size.X}).");

        var layer = new AgasLayerResource { Id = "prompt-layout-layer" };
        layer.AddPromptFragment(new PromptFragmentResource { Id = "alpha", Text = "alpha", Enabled = true, Order = 0 });
        layer.AddPromptFragment(new PromptFragmentResource { Id = "beta", Text = "beta", Enabled = true, Order = 1 });
        layer.AddPromptFragment(new PromptFragmentResource { Id = "duplicate", Text = "alpha", Enabled = true, Order = 2 });
        panel.SetLayer(layer);
        Require(preview.Text.Contains("alpha", StringComparison.Ordinal)
                && preview.Text.Contains("beta", StringComparison.Ordinal)
                && preview.Text.Split("alpha", StringSplitOptions.None).Length == 2,
            "The merged preview must reflect each distinct whole fragment once.");

        int initialWidth = (int)Math.Round(previewSection.Size.X);
        split.EmitSignal(SplitContainer.SignalName.DragStarted);
        split.SplitOffsets = new[] { split.SplitOffsets[0] + 100 };
        await Frames(2);
        split.EmitSignal(SplitContainer.SignalName.DragEnded);
        await Frames(2);
        int draggedWidth = (int)Math.Round(previewSection.Size.X);
        Require(draggedWidth < initialWidth - 60,
            "Dragging the horizontal divider must change the preview width.");

        tabs.Size = new Vector2(1600, 320);
        await Frames(3);
        Require(Math.Abs(previewSection.Size.X - draggedWidth) <= 2,
            $"Resizing the host must retain the user's preview width (dragged={draggedWidth}, resized={previewSection.Size.X}).");

        await CheckMultilineAsync(tabs, panel, layer, preview);

        tabs.QueueFree();
        await Frames(1);
        await CheckProductionThemeLayoutAsync();
    }

    private async Task CheckProductionThemeLayoutAsync()
    {
        var panel = new PromptPanel { Theme = AgasIdeTheme.Create(), Size = new Vector2(1600, 600) };
        AddChild(panel);
        var layer = new AgasLayerResource();
        layer.AddPromptFragment(new PromptFragmentResource { Text = "first\nsecond\nthird", Order = 0 });
        layer.AddPromptFragment(new PromptFragmentResource { Text = "next", Order = 1 });
        panel.SetLayer(layer);
        await Frames(3);
        var list = FindRequired<VBoxContainer>(panel, "PromptFragmentList");
        var row = (Control)list.GetChild(0);
        var editor = FindRequired<TextEdit>(row, "PromptFragmentTextEdit");
        var grip = FindRequired<Control>(row, "PromptFragmentResizeHandle");
        StyleBox style = editor.GetThemeStylebox("normal");
        float textHeight = editor.Size.Y - style.GetMargin(Side.Top) - style.GetMargin(Side.Bottom);
        Require(Near(editor.Size.Y, 180) && editor.GetThemeFontSize("font_size") == 32
                && editor.GetVisibleLineCount() >= 3 && textHeight >= 3 * editor.GetLineHeight(),
            $"Production theme default must fit three complete 32px text lines (height={editor.Size.Y}, usable={textHeight}, line={editor.GetLineHeight()}).");
        foreach (int height in new[] { 180, 64, 320 })
        {
            if (!Near(editor.Size.Y, height))
            {
                await DragGripAsync(grip, new Vector2(0, height - editor.Size.Y));
            }
            Require(Near(editor.Size.Y, height), $"Production themed editor must retain {height}px height.");
            CheckRowBounds(row, editor);
            CheckGrip(editor, grip);
            var header = (Control)FindRequired<Button>(panel, "AddPromptFragmentButton").GetParent();
            var scroll = FindRequired<ScrollContainer>(panel, "PromptFragmentScroll");
            var title = FindRequired<Label>(panel, "PromptMergedPreviewTitle");
            var preview = FindRequired<TextEdit>(panel, "PromptMergedPreview");
            Require(Near(scroll.GlobalPosition.Y - header.GetGlobalRect().End.Y, 12)
                    && Near(preview.GlobalPosition.Y - title.GetGlobalRect().End.Y, 12)
                    && Near(((Control)list.GetChild(1)).GlobalPosition.Y - row.GetGlobalRect().End.Y, 12),
                $"Both header-content gaps and the list gap must remain 12px at {height}px editor height.");
        }
        panel.QueueFree();
        await Frames(1);
    }

    private async Task CheckMultilineAsync(TabContainer tabs, PromptPanel panel,
        AgasLayerResource layer, TextEdit preview)
    {
        var scene = new AgasSceneResource { Id = "prompt-layout-scene", ActiveLayerId = layer.Id };
        scene.AddLayer(layer);
        var session = new ProjectSession();
        session.AcceptProject(new AgasProjectResource());
        session.ChangeCurrentScene(scene);
        var mutations = new ProjectMutationService();
        int textCallbacks = 0;
        int actionCallbacks = 0;
        void Mutate(PromptMutationCommand command)
        {
            ProjectMutationResult result = mutations.MutatePrompt(session, command);
            Require(result.Success, $"Real prompt mutation failed: {result.Error}: {result.Message}");
            panel.SetLayer(layer);
        }
        panel.PromptTextUpdateRequested += (fragment, value) =>
        {
            textCallbacks++;
            Mutate(new PromptMutationCommand(PromptMutationKind.Update, scene.Id, layer.Id, fragment.Id, Text: value));
        };
        panel.PromptEnabledSetRequested += (fragment, enabled) =>
        {
            actionCallbacks++;
            Mutate(new PromptMutationCommand(PromptMutationKind.SetEnabled, scene.Id, layer.Id, fragment.Id, Enabled: enabled));
        };
        panel.PromptMoveRequested += (_, fragment, direction) =>
        {
            actionCallbacks++;
            Mutate(new PromptMutationCommand(PromptMutationKind.Move, scene.Id, layer.Id, fragment.Id, Direction: direction));
        };
        panel.PromptDeleteRequested += (_, fragment) =>
        {
            actionCallbacks++;
            Mutate(new PromptMutationCommand(PromptMutationKind.Delete, scene.Id, layer.Id, fragment.Id));
        };
        panel.PromptAddRequested += _ =>
        {
            actionCallbacks++;
            Mutate(new PromptMutationCommand(PromptMutationKind.Add, scene.Id, layer.Id, Text: "new fragment"));
        };

        tabs.Size = new Vector2(1600, 800);
        panel.SetLayer(layer);
        await Frames(3);
        var list = FindRequired<VBoxContainer>(panel, "PromptFragmentList");
        Control Row(int index) => (Control)list.GetChild(index);
        TextEdit Editor(int index) => FindRequired<TextEdit>(Row(index), "PromptFragmentTextEdit");
        TextEdit editor = Editor(0);
        Control grip = FindRequired<Control>(Row(0), "PromptFragmentResizeHandle");
        Require(!Descendants<LineEdit>(list).Any() && editor.Editable
                && editor.WrapMode == TextEdit.LineWrappingMode.Boundary,
            "Fragment rows must use an editable wrapping TextEdit, without a single-line fallback.");
        Require(Near(editor.Size.Y, 180), $"Initial fragment editor height must be 180px, got {editor.Size.Y}.");
        Require(editor.GetVisibleLineCount() >= 3,
            "The default editor must have room for at least three visible text lines without changing its font.");
        var header = (Control)FindRequired<Button>(panel, "AddPromptFragmentButton").GetParent();
        var fragmentScroll = FindRequired<ScrollContainer>(panel, "PromptFragmentScroll");
        Require(Near(fragmentScroll.GlobalPosition.Y - header.GetGlobalRect().End.Y, 12),
            "Fragment header and list viewport must have a 12px gap.");
        Require(Near(Row(1).GlobalPosition.Y - Row(0).GetGlobalRect().End.Y, 12),
            "Fragment rows must have a consistent 12px vertical gap.");
        CheckGrip(editor, grip);
        CheckRowBounds(Row(0), editor);

        PromptFragmentResource fragment = layer.PromptFragmentsSnapshot.Single(item => item.Id == "alpha");
        editor.GrabFocus();
        editor.SetCaretLine(0);
        editor.SetCaretColumn(editor.Text.Length);
        await KeyAsync(Key.Enter);
        Require(editor.Text == "alpha\n" && fragment.Text == editor.Text && textCallbacks > 0,
            "Actual Enter input must insert and immediately synchronize a newline through the wired mutation callback.");
        await KeyAsync(Key.B, 'b');
        Require(editor.Text == "alpha\nb" && fragment.Text == editor.Text
                && preview.Text.Contains("alpha\nb", StringComparison.Ordinal),
            "Typing after Enter must continue updating the resource and merged preview without focus loss.");
        Require(ReferenceEquals(editor, Editor(0)) && editor.HasFocus()
                && editor.GetCaretLine() == 1 && editor.GetCaretColumn() == 1
                && ReferenceEquals(grip, FindRequired<Control>(Row(0), "PromptFragmentResizeHandle")),
            "Synchronous SetLayer refresh must retain editor, grip, focus and caret.");

        editor.BeginComplexOperation();
        editor.InsertTextAtCaret(" undo-marker");
        editor.EndComplexOperation();
        await Frames(2);
        Require(fragment.Text == "alpha\nb undo-marker" && editor.HasUndo(),
            "TextChanged must synchronize insertion while retaining native editor undo history.");
        panel.SetLayer(layer);
        editor.Undo();
        await Frames(2);
        Require(editor.Text == "alpha\nb" && fragment.Text == editor.Text && editor.HasRedo(),
            "Editor undo after refresh must restore and synchronize the multiline value.");
        editor.Redo();
        await Frames(2);
        Require(editor.Text == "alpha\nb undo-marker" && fragment.Text == editor.Text,
            "Editor redo must remain functional across synchronous refresh.");

        float width = editor.Size.X;
        await DragGripAsync(grip, new Vector2(500, 80));
        Require(Near(editor.Size.Y, 260) && editor.Size.X <= width + 1,
            $"Diagonal grip drag must change only height (got {editor.Size}).");
        CheckRowBounds(Row(0), editor);
        await DragGripAsync(grip, new Vector2(700, 0));
        Require(Near(editor.Size.Y, 260) && editor.Size.X <= width + 1,
            "Horizontal grip drag must not expand editor width or change height.");
        panel.SetLayer(layer);
        await Frames(2);
        Require(ReferenceEquals(editor, Editor(0)) && Near(editor.Size.Y, 260)
                && ReferenceEquals(grip, FindRequired<Control>(Row(0), "PromptFragmentResizeHandle")),
            "Manual editor height and grip must survive a layer refresh.");
        await DragGripAsync(grip, new Vector2(1000, 1000));
        Require(editor.Size.Y == 320 && editor.Size.X <= width + 1,
            $"Grip height must clamp to exactly 320px without width expansion (initial width={width}, got={editor.Size}).");
        CheckRowBounds(Row(0), editor);
        await DragGripAsync(grip, new Vector2(-1000, -1000));
        Require(editor.Size.Y == 64 && editor.Size.X <= width + 1,
            $"Grip height must clamp to exactly 64px without width expansion (initial width={width}, got={editor.Size}).");
        CheckRowBounds(Row(0), editor);
        await CheckContinuousDragAsync(editor, grip);
        CheckGrip(editor, grip);

        string longText = string.Join("\n", Enumerable.Range(0, 60).Select(index => $"line {index}: multiline prompt content"));
        editor.GrabFocus();
        editor.SelectAll();
        editor.InsertTextAtCaret(longText);
        await Frames(3);
        Require(fragment.Text == longText && editor.GetVScrollBar().Visible
                && editor.GetVScrollBar().MaxValue > editor.GetVScrollBar().Page && Near(editor.Size.Y, 64),
            "Long multiline content must synchronize and scroll inside the manually sized editor.");
        await CheckWheelRoutingAsync(tabs, panel, list, editor);
        editor.SetCaretLine(20);
        editor.SetCaretColumn(4);
        editor.ScrollVertical = 15;
        await Frames(2);
        double scroll = editor.ScrollVertical;
        panel.SetLayer(layer);
        await Frames(2);
        Require(scroll > 0 && Math.Abs(editor.ScrollVertical - scroll) < 0.1
                && editor.HasFocus() && editor.GetCaretLine() == 20 && editor.GetCaretColumn() == 4
                && editor.HasUndo() && ReferenceEquals(editor, Editor(0)),
            "Refresh must retain long-content scroll, focus, caret and undo history.");

        foreach (int hostWidth in new[] { 700, 1900 })
        {
            tabs.Size = new Vector2(hostWidth, 600);
            await Frames(3);
            CheckRowBounds(Row(0), editor);
            CheckGrip(editor, grip);
            Require(editor.Size.X <= FindRequired<Control>(panel, "PromptFragmentSection").Size.X + 1,
                $"Editor must remain within its container at host width {hostWidth}.");
        }
        CheckRoundTrip(scene, fragment);

        var enabled = Descendants<CheckBox>(Row(0)).Single();
        enabled.ButtonPressed = false;
        await Frames(2);
        Require(!fragment.Enabled && !preview.Text.Contains("line 0:", StringComparison.Ordinal),
            "Checkbox must route a real enable mutation and remove the whole multiline fragment from preview.");
        Descendants<CheckBox>(Row(0)).Single().ButtonPressed = true;
        await Frames(2);
        Require(fragment.Enabled && preview.Text.Contains(longText, StringComparison.Ordinal),
            "Re-enabling must restore the complete multiline fragment.");
        ActionButton(Row(0), "下移").EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(2);
        Require(fragment.Order == 1 && Editor(1).Text == longText, "Down action must update real order and display the multiline row in the new position.");
        ActionButton(Row(1), "上移").EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(2);
        Require(fragment.Order == 0 && Editor(0).Text == longText, "Up action must restore real fragment order.");
        ActionButton(Row(2), "删除").EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(2);
        Require(layer.PromptFragmentsSnapshot.Count == 2 && list.GetChildCount() == 2,
            "Delete action must remove its real fragment and row.");
        FindRequired<Button>(panel, "AddPromptFragmentButton").EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(2);
        Require(layer.PromptFragmentsSnapshot.Count == 3 && list.GetChildCount() == 3 && actionCallbacks == 6,
            "Add/enabled/order/delete actions must all invoke the real mutation boundary.");

        foreach (bool locked in new[] { true, false })
        {
            layer.Locked = locked;
            layer.Visible = locked;
            panel.SetLayer(layer);
            await Frames(2);
            TextEdit readOnly = Editor(0);
            Control readOnlyGrip = FindRequired<Control>(Row(0), "PromptFragmentResizeHandle");
            int callbacksBefore = textCallbacks + actionCallbacks;
            string textBefore = fragment.Text;
            Require(!readOnly.Editable && Descendants<BaseButton>(list).All(button => button.Disabled)
                    && FindRequired<Button>(panel, "AddPromptFragmentButton").Disabled,
                "Locked or hidden layers must disable text, checkbox and all row/add commands.");
            readOnly.GrabFocus();
            await KeyAsync(Key.Enter);
            await KeyAsync(Key.X, 'x');
            await DragGripAsync(readOnlyGrip, new Vector2(100, 100));
            Require(fragment.Text == textBefore && callbacksBefore == textCallbacks + actionCallbacks,
                "Read-only keyboard input and grip interaction must not mutate prompt content.");
        }
        layer.Visible = true;
        layer.Locked = false;
        panel.SetLayer(null);
        await Frames(2);
        Require(!Descendants<TextEdit>(list).Any() && !Descendants<Control>(list).Any(control => control.Name == "PromptFragmentResizeHandle")
                && FindRequired<Button>(panel, "AddPromptFragmentButton").Disabled && string.IsNullOrEmpty(preview.Text),
            "Null layer must clear editors, grips and preview and disable add.");
    }

    private void CheckRoundTrip(AgasSceneResource scene, PromptFragmentResource fragment)
    {
        string directory = Path.Combine(Path.GetTempPath(), "agas-prompt-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var repository = new AgasResourceRepository();
            string path = Path.Combine(directory, "scene.tres");
            Require(repository.Save(scene, path).Success, "Multiline scene resource save failed.");
            var loaded = repository.LoadFresh<AgasSceneResource>(path);
            Require(loaded.Success && loaded.Resource != null && !ReferenceEquals(scene, loaded.Resource),
                "Multiline scene must reload from disk without reusing the in-memory resource.");
            var fragments = loaded.Resource!.LayersSnapshot.Single().PromptFragmentsSnapshot;
            Require(fragments.Single(item => item.Id == fragment.Id).Text == fragment.Text,
                "Resource save/load must preserve every newline and the full multiline text.");
            foreach (PromptCompositionMode mode in Enum.GetValues<PromptCompositionMode>())
            {
                string generated = PromptComposer.Compose(fragments, "custom\nsecond custom line", mode);
                Require(generated.Contains(fragment.Text, StringComparison.Ordinal)
                        && generated.Contains("custom\nsecond custom line", StringComparison.Ordinal)
                        && generated == PromptComposer.Compose(scene.LayersSnapshot.Single().PromptFragmentsSnapshot, "custom\nsecond custom line", mode),
                    $"Generated {mode} prompt must preserve multiline sections after resource reload.");
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private async Task KeyAsync(Key key, uint unicode = 0)
    {
        GetViewport().PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Unicode = unicode, Pressed = true });
        GetViewport().PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Unicode = unicode, Pressed = false });
        await Frames(2);
    }

    private async Task DragGripAsync(Control grip, Vector2 delta)
    {
        Vector2 start = grip.GlobalPosition + grip.Size / 2;
        GetViewport().NotifyMouseEntered();
        GetViewport().PushInput(new InputEventMouseMotion { Position = start, GlobalPosition = start }, true);
        await Frames(1);
        GetViewport().PushInput(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true, Position = start, GlobalPosition = start
        }, true);
        GetViewport().PushInput(new InputEventMouseMotion
        {
            Position = start + delta, GlobalPosition = start + delta, Relative = delta, ButtonMask = MouseButtonMask.Left
        }, true);
        GetViewport().PushInput(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = false, Position = start + delta, GlobalPosition = start + delta
        }, true);
        await Frames(3);
    }

    private void CheckGrip(TextEdit editor, Control grip)
    {
        Rect2 text = editor.GetGlobalRect();
        Rect2 handle = grip.GetGlobalRect();
        Require(handle.Size.X > 0 && handle.Size.Y > 0 && handle.Position.X >= text.Position.X
                && handle.Position.Y >= text.Position.Y && handle.End.X <= text.End.X
                && handle.End.Y <= text.End.Y && (handle.End - text.End).Length() <= 3,
            $"Resize grip must remain inside the actual TextEdit bottom-right corner within 3px (editor={text}, grip={handle}).");
    }

    private async Task CheckContinuousDragAsync(TextEdit editor, Control grip)
    {
        Vector2 start = grip.GlobalPosition + grip.Size / 2;
        GetViewport().NotifyMouseEntered();
        GetViewport().PushInput(new InputEventMouseMotion { Position = start, GlobalPosition = start }, true);
        await Frames(1);
        GetViewport().PushInput(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true, Position = start, GlobalPosition = start
        }, true);
        Vector2 position = start;
        async Task Move(Vector2 delta)
        {
            position += delta;
            GetViewport().PushInput(new InputEventMouseMotion
            {
                Position = position, GlobalPosition = position, Relative = delta, ButtonMask = MouseButtonMask.Left
            }, true);
            await Frames(3);
        }
        await Move(new Vector2(1000, 1000));
        Require(editor.Size.Y == 320, "Continuous overshoot must clamp actual TextEdit height to exactly 320px.");
        CheckGrip(editor, grip);
        await Move(new Vector2(0, -10));
        Require(editor.Size.Y == 310, "Reversing 10px after maximum overshoot in the same drag must immediately reduce height to 310px.");
        await Move(new Vector2(-2000, -2000));
        Require(editor.Size.Y == 64, "Continuous overshoot must clamp actual TextEdit height to exactly 64px.");
        CheckGrip(editor, grip);
        await Move(new Vector2(0, 10));
        Require(editor.Size.Y == 74, "Reversing 10px after minimum overshoot in the same drag must immediately increase height to 74px.");
        Require(!grip.GetGlobalRect().HasPoint(position), "Capture cancellation regression must release outside the grip.");
        GetViewport().PushInput(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = false, Position = position, GlobalPosition = position
        }, true);
        await Move(new Vector2(0, 40));
        Require(editor.Size.Y == 74, "Motion carrying a left-button mask after outside release must not resize the editor.");
        await DragGripAsync(grip, new Vector2(0, -10));
        Require(editor.Size.Y == 64, "A fresh drag after capture cancellation must still resize to the exact minimum.");
    }

    private async Task CheckWheelRoutingAsync(TabContainer tabs, PromptPanel panel, VBoxContainer list, TextEdit editor)
    {
        tabs.Size = new Vector2(1600, 220);
        editor.SetCaretLine(0);
        editor.ScrollVertical = 0;
        var outer = FindRequired<ScrollContainer>(panel, "PromptFragmentScroll");
        outer.ScrollVertical = 0;
        await Frames(3);
        Require(outer.GetVScrollBar().Visible && outer.GetVScrollBar().MaxValue > outer.GetVScrollBar().Page,
            "Wheel routing fixture must overflow the actual fragment list.");
        await WheelAsync(editor.GlobalPosition + new Vector2(editor.Size.X / 2, 12), 1);
        Require(editor.ScrollVertical > 0 && outer.ScrollVertical == 0,
            $"Wheel inside editor must scroll text without moving the fragment list (text={editor.ScrollVertical}, list={outer.ScrollVertical}).");
        double textScroll = editor.ScrollVertical;
        await WheelAsync(outer.GlobalPosition + new Vector2(3, outer.Size.Y / 2), 40);
        Require(outer.ScrollVertical > 0 && Math.Abs(editor.ScrollVertical - textScroll) < 0.1,
            "Wheel outside editor in the list must scroll fragments without moving editor text.");
        Control last = (Control)list.GetChild(list.GetChildCount() - 1);
        Rect2 visible = outer.GetGlobalRect();
        Require(last.GetGlobalRect().End.Y <= visible.End.Y + 1 && last.GetGlobalRect().End.Y > visible.Position.Y,
            $"Wheel scrolling must make the last fragment row reachable (last={last.GetGlobalRect()}, viewport={visible}).");
        CheckRowBounds(last, FindRequired<TextEdit>(last, "PromptFragmentTextEdit"));
        outer.ScrollVertical = 0;
        tabs.Size = new Vector2(1600, 600);
        await Frames(3);
    }

    private async Task WheelAsync(Vector2 position, int count)
    {
        GetViewport().NotifyMouseEntered();
        GetViewport().PushInput(new InputEventMouseMotion { Position = position, GlobalPosition = position }, true);
        await Frames(1);
        for (int index = 0; index < count; index++)
        {
            GetViewport().PushInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.WheelDown, Pressed = true, Position = position, GlobalPosition = position, Factor = 1
            }, true);
            GetViewport().PushInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.WheelDown, Pressed = false, Position = position, GlobalPosition = position
            }, true);
            await Frames(1);
        }
        await Frames(3);
    }

    private void CheckRowBounds(Control row, TextEdit editor)
    {
        BaseButton[] buttons = Descendants<BaseButton>(row).ToArray();
        Require(buttons.Length == 4, "Each editor row must retain checkbox, up, down and delete controls.");
        Rect2 bounds = row.GetGlobalRect();
        Rect2 text = editor.GetGlobalRect();
        Require(text.Size.X > 20 && text.Position.X >= bounds.Position.X - 1 && text.End.X <= bounds.End.X + 1,
            "Fragment editor width must fit the container-managed row.");
        foreach (BaseButton button in buttons)
        {
            Rect2 rect = button.GetGlobalRect();
            Require(rect.Size.X > 0 && rect.Position.X >= bounds.Position.X - 1 && rect.End.X <= bounds.End.X + 1
                    && (rect.End.X <= text.Position.X + 1 || rect.Position.X >= text.End.X - 1),
                $"Adjacent checkbox/order/delete button must stay inside row and outside editor: {button.TooltipText}.");
            Require(Near(rect.GetCenter().Y, bounds.GetCenter().Y)
                    && button.SizeFlagsVertical == Control.SizeFlags.ShrinkCenter,
                $"Checkbox/order/delete controls must stay vertically centered after editor resize: {button.Name}.");
        }
        var checkbox = Descendants<CheckBox>(row).Single();
        Require(Near(checkbox.Size.X, checkbox.GetCombinedMinimumSize().X)
                && Near(checkbox.Size.Y, checkbox.GetCombinedMinimumSize().Y)
                && checkbox.Size.Y < editor.Size.Y,
            "The short checkbox must retain its natural dimensions rather than stretching with the editor.");
        var actions = buttons.Where(button => button is not CheckBox).ToArray();
        Require(actions.Length == 3 && actions.All(button => Near(button.Size.X, 48) && Near(button.Size.Y, 48)),
            "Up, down and delete must have uniform 48px square dimensions.");
        var children = row.GetChildren().OfType<Control>().ToArray();
        for (int index = 1; index < children.Length; index++)
        {
            Require(Near(children[index].GlobalPosition.X - children[index - 1].GetGlobalRect().End.X, 12),
                "Every adjacent checkbox/editor/order/delete pair must retain the same 12px horizontal gap.");
        }
    }

    private static Button ActionButton(Node row, string tooltip) =>
        Descendants<Button>(row).Single(button => button is not CheckBox && button.TooltipText.Contains(tooltip, StringComparison.Ordinal));

    private static IEnumerable<T> Descendants<T>(Node root) where T : Node
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static bool Near(float actual, float expected) => Math.Abs(actual - expected) <= 1;

    private async Task Frames(int count)
    {
        for (int index = 0; index < count; index++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static T FindRequired<T>(Node root, string name) where T : Node =>
        root.FindChild(name, recursive: true, owned: false) as T
        ?? throw new InvalidOperationException($"Missing {name}.");

    private static bool ContainsLabel(Node root, string text)
    {
        if (root is Label label && label.Text == text)
        {
            return true;
        }

        foreach (Node child in root.GetChildren())
        {
            if (ContainsLabel(child, text))
            {
                return true;
            }
        }

        return false;
    }

    private void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
        _checks++;
    }
}
