#nullable enable

using System;
using System.IO;
using System.Threading.Tasks;
using AGAS.UI;
using Agas.Features.Canvas;
using AppAgasCs.Core.Resources;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class PropertiesFilePreviewAcceptance : Node
{
    private const string Prefix = "[AGAS][PROPERTIES-FILE-PREVIEW]";

    public override async void _Ready()
    {
        int exitCode = 0;
        string originalLocale = TranslationServer.GetLocale();
        string? home = System.Environment.GetEnvironmentVariable("AGAS_HOME");
        string fixture = Path.Combine(home ?? Path.GetTempPath(), "properties-preview-" + Guid.NewGuid().ToString("N"));
        try
        {
            Require(!string.IsNullOrWhiteSpace(home), "Run through the isolated acceptance runner.");
            Directory.CreateDirectory(fixture);
            string path = Path.Combine(fixture, "preview.png");
            using (Image image = Image.CreateEmpty(80, 40, false, Image.Format.Rgba8))
            {
                image.Fill(Colors.Red);
                Require(image.SavePng(path) == Error.Ok, "Could not write the isolated PNG fixture.");
            }

            var canvas = new CanvasWorkspace();
            var panel = new PropertiesPanel { Size = new Vector2(320, 600), Theme = AgasIdeTheme.Create(14) };
            AddChild(canvas);
            AddChild(panel);
            panel.BindCanvas(canvas);
            canvas.SelectionChanged += () => panel.SetSelectedImage(canvas.GetSelectedImage());
            var selected = new ImageInstanceResource
            {
                Id = "preview-image", ImagePath = "assets/canvas.png", X = 12, Y = 34,
                ScaleX = 1.5, ScaleY = 2, RotationDegrees = 45, Opacity = 0.75
            };
            var layer = new AgasLayerResource { Id = "preview-layer" };
            layer.AddImage(selected);
            var scene = new AgasSceneResource { Id = "preview-scene", ActiveLayerId = layer.Id };
            scene.AddLayer(layer);
            canvas.SetWorkspace(new AgasProjectResource(), scene, fixture);
            canvas.SelectImage(selected);
            await Frames();

            Control details = Required<Control>(panel, "PropertiesContent");
            Control splitter = Required<Control>(panel, "PropertiesPreviewSplitter");
            Control preview = Required<Control>(panel, "ImagePreviewPanel");
            TextureRect texture = Required<TextureRect>(panel, "SelectedImagePreview");
            Label status = Required<Label>(panel, "PreviewStatusLabel");
            SpinBox[] fields = Numbers(panel);
            Require(details.IsVisibleInTree() && fields.Length == 6, "Canvas selection must retain all six property editors.");
            float canvasPreviewHeight = preview.Size.Y;

            panel.ShowFilePreview("assets/preview.png", path);
            await Frames();
            RequirePreviewOnly(details, splitter, preview);
            Require(preview.Size.Y > canvasPreviewHeight, "Hiding Details must release vertical space to preview.");
            Require(texture.Texture?.GetSize() == new Vector2(80, 40)
                && texture.StretchMode == TextureRect.StretchModeEnum.KeepAspectCentered,
                "File preview must preserve the image and aspect mode.");
            Require(status.Text == "assets/preview.png", "File preview must preserve the relative image path.");
            Require(Required<LineEdit>(panel, "PropertiesFields").Text == string.Empty, "File selection must clear stale canvas properties.");
            fields[0].Value = 999;
            Require(selected.X == 12, "Hidden file-preview fields must not mutate the previously selected canvas image.");

            foreach (string locale in new[] { "en", "zh_CN" })
            {
                TranslationServer.SetLocale(locale);
                await Frames();
                RequirePreviewOnly(details, splitter, preview);
                Require(status.Text == "assets/preview.png", "Locale refresh must not translate the user image path.");
            }
            foreach (Vector2 size in new[] { new Vector2(240, 220), new Vector2(320, 600) })
            {
                panel.Size = size;
                await Frames();
                RequirePreviewOnly(details, splitter, preview);
                Require(panel.Size.Y <= size.Y + 1 && splitter.GetGlobalRect().End.Y <= panel.GetGlobalRect().End.Y + 1,
                    "Preview-only layout must not overflow the small Properties root.");
            }

            canvas.SelectImage(selected);
            await Frames();
            Require(details.IsVisibleInTree() && texture.Texture == null, "Canvas selection must restore Details and discard stale file preview.");
            double[] values = { 21, 43, 1.26, 2.51, 90, 0.5 };
            for (int index = 0; index < fields.Length; index++) fields[index].Value = values[index];
            Require(Math.Abs(selected.X - 21) < 0.0001 && Math.Abs(selected.Y - 43) < 0.0001
                && Math.Abs(selected.ScaleX - 1.26) < 0.0001 && Math.Abs(selected.ScaleY - 2.51) < 0.0001
                && Math.Abs(selected.RotationDegrees - 90) < 0.0001 && Math.Abs(selected.Opacity - 0.5) < 0.0001 && canvas.CanUndo(),
                $"Canvas edits must remain undoable: {selected.X}, {selected.Y}, {selected.ScaleX}, {selected.ScaleY}, {selected.RotationDegrees}, {selected.Opacity}, undo={canvas.CanUndo()}.");
            panel.Editable = false;
            fields[0].Value = 500;
            Require(selected.X == 21, "Read-only property edits must remain blocked.");
            panel.Editable = true;
            panel.SetSelectedImage(selected);
            panel.MutationGuard = _ => false;
            fields[0].Value = 600;
            Require(selected.X == 21, "The canvas property mutation guard must remain intact.");
            panel.MutationGuard = null;

            panel.ShowFilePreview("assets/preview.png", path);
            canvas.ClearImageSelection();
            await Frames();
            Require(details.IsVisibleInTree() && Required<Label>(panel, "PropertiesEmptyState").Visible,
                "Ordinary clear must retain the original Details empty-selection prompt.");
            Require(texture.Texture == null && status.Text != "assets/preview.png", "Clearing canvas selection must discard stale file preview.");
            panel.ShowSceneReadme("user readme");
            panel.ShowFilePreview("assets/preview.png", path);
            await Frames();
            RequirePreviewOnly(details, splitter, preview);
            Require(!Required<Control>(panel, "SceneReadmePanel").Visible, "File selection must leave the scene-readme mode.");
            panel.ShowSceneReadme("user readme");
            canvas.SelectImage(selected);
            await Frames();
            Require(details.IsVisibleInTree() && !Required<Control>(panel, "SceneReadmePanel").Visible,
                "Canvas selection must leave scene-readme mode and restore Details.");
            GD.Print($"{Prefix}[PASS] preview-only geometry, aspect/path, locale, small pane, canvas edits/guards and transitions");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {exception}");
        }
        finally
        {
            foreach (Node child in GetChildren()) child.Free();
            TranslationServer.SetLocale(originalLocale);
            if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true);
            GetTree().Quit(exitCode);
        }
    }

    private static void RequirePreviewOnly(Control details, Control splitter, Control preview)
    {
        Require(!details.Visible && !details.IsVisibleInTree(), "File-preview mode must hide the entire Details section.");
        Require(Math.Abs(preview.Position.Y) <= 1 && Math.Abs(preview.Size.Y - splitter.Size.Y) <= 1,
            "Hidden Details must reserve zero height, including splitter spacing.");
    }

    private static SpinBox[] Numbers(Node panel)
    {
        var fields = new System.Collections.Generic.List<SpinBox>();
        Collect(panel, fields);
        return fields.ToArray();
    }

    private static void Collect(Node parent, System.Collections.Generic.List<SpinBox> fields)
    {
        foreach (Node child in parent.GetChildren())
        {
            if (child is SpinBox spin) fields.Add(spin);
            else Collect(child, fields);
        }
    }

    private static T Required<T>(Node parent, string name) where T : Node
    {
        Node node = parent.FindChild(name, recursive: true, owned: false)
            ?? throw new InvalidOperationException($"Missing control: {name}");
        if (node is T control) return control;
        foreach (Node child in node.GetChildren())
            if (child is T field) return field;
        foreach (Node child in node.GetChildren())
            foreach (Node nested in child.GetChildren())
                if (nested is T field) return field;
        throw new InvalidOperationException($"Missing {typeof(T).Name}: {name}");
    }

    private async Task Frames()
    {
        for (int index = 0; index < 4; index++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
