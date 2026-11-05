#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AGAS.UI;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Tools.Storage;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class EditorUiLocalizationAcceptance : Node
{
    public override async void _Ready()
    {
        string previousLocale = TranslationServer.GetLocale();
        string home = Path.Combine(Path.GetTempPath(), "agas-editor-localization-" + Guid.NewGuid().ToString("N"));
        int exitCode = 0;
        try
        {
            Directory.CreateDirectory(home);
            await VerifyAsync(home);
            GD.Print("[AGAS][EDITOR-LOCALIZATION][PASS]");
        }
        catch (Exception error)
        {
            exitCode = 1;
            GD.PushError($"[AGAS][EDITOR-LOCALIZATION][FAIL] {error}");
        }
        finally
        {
            foreach (Node child in GetChildren()) child.Free();
            TranslationServer.SetLocale(previousLocale);
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
            GetTree().Quit(exitCode);
        }
    }

    private async Task VerifyAsync(string home)
    {
        const string source = "extends RefCounted\nfunc process(image: Image) -> Dictionary:\n\treturn {\"image\": image}\n";
        var catalog = new ToolCatalog(home);
        catalog.CreateTool("locale-first", "设置", source);
        catalog.CreateTool("locale-second", "保存", source);
        catalog.CreateChain("locale-chain", "设置", new[] { "locale-first", "locale-second" });
        var toolbox = new ToolboxPanel(catalog) { Size = new Vector2(1200, 400) };
        AddChild(toolbox);
        var scene = new AgasSceneResource { Id = "locale-scene", ActiveLayerId = "locale-layer" };
        scene.AddLayer(new AgasLayerResource { Id = "locale-layer", Name = "保存" });
        var layers = new LayerTabs { Size = new Vector2(1000, 80) };
        AddChild(layers);
        layers.SetScene(scene);
        await Frames();

        Require(toolbox.SelectItem("locale-first"), "The editable fixture tool must be selectable.");
        CodeEdit code = Required<CodeEdit>(toolbox, "ToolboxCodeEditor");
        LineEdit name = Required<LineEdit>(toolbox, "ToolboxItemName");
        code.Text = source + "\n# 设置 保存 unsaved\n";
        name.Text = "设置 保存 unsaved";
        string editedCode = code.Text;
        string editedName = name.Text;
        ulong codeId = code.GetInstanceId();
        Button layer = Required<Button>(layers, "LayerTabSelect-locale-layer");
        ulong layerId = layer.GetInstanceId();
        int changes = 0;
        code.TextChanged += () => changes++;
        name.TextChanged += _ => changes++;
        Dictionary<string, byte[]> before = Snapshot(home);
        foreach (string locale in new[] { "en", "zh_CN", "en" })
        {
            TranslationServer.SetLocale(locale);
            await Frames();
            Require(code.Text == editedCode && name.Text == editedName && code.GetInstanceId() == codeId,
                "Locale changes must preserve unsaved code, name and editor identity.");
            Require(code.AutoTranslateMode == AutoTranslateModeEnum.Disabled && name.AutoTranslateMode == AutoTranslateModeEnum.Disabled,
                "Editable user fields must opt out of automatic translation.");
            Require(layer.Text == "保存" && layer.GetInstanceId() == layerId && layer.AutoTranslateMode == AutoTranslateModeEnum.Disabled,
                "Layer names equal to native translation keys must remain raw without rebuilding tabs.");
            Require(layer.TooltipText == string.Format(Tr("切换到图层：{0}。右键打开该图层提示词并设为当前图层。"), "保存"),
                "Layer tooltip templates must update while their name argument stays raw.");
            foreach (string listName in new[] { "ToolboxItems", "ToolboxChainItems" })
            {
                ItemList list = Required<ItemList>(toolbox, listName);
                for (int index = 0; index < list.ItemCount; index++)
                    Require(list.GetItemAutoTranslateMode(index) == AutoTranslateModeEnum.Disabled,
                        "Tool and chain user names must never auto-translate.");
            }
            Require(changes == 0, "Locale changes must not emit editable-field change events.");
        }

        Require(toolbox.SelectItem("locale-chain"), "The chain fixture must be selectable.");
        Label details = Required<Label>(toolbox, "ToolboxDetails");
        ItemList steps = Required<ItemList>(toolbox, "ToolboxChainSteps");
        foreach (string locale in new[] { "zh_CN", "en" })
        {
            TranslationServer.SetLocale(locale);
            await Frames();
            if (locale == "en") Require(Tr("工具链 · {0} 步") != "工具链 · {0} 步", "The English catalog must contain the dynamic chain template.");
            Require(details.Text == string.Format(Tr("工具链 · {0} 步"), 2), "Existing dynamic step counts must update immediately.");
            Require(steps.GetItemText(0) == "1. 设置" && steps.GetItemText(1) == "2. 保存",
                "Tool names inside numbered step entries must remain raw.");
            Require(steps.GetItemAutoTranslateMode(0) == AutoTranslateModeEnum.Disabled,
                "Step entries must opt out of automatic translation.");
        }
        Dictionary<string, byte[]> after = Snapshot(home);
        Require(before.Count == after.Count && before.All(entry => after.TryGetValue(entry.Key, out byte[]? bytes) && entry.Value.SequenceEqual(bytes)),
            "Translation-only updates must not write tools, chains or source files.");
    }

    private async Task Frames()
    {
        for (int frame = 0; frame < 3; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static Dictionary<string, byte[]> Snapshot(string home) => Directory.GetFiles(home, "*", SearchOption.AllDirectories)
        .ToDictionary(file => Path.GetRelativePath(home, file), File.ReadAllBytes, StringComparer.Ordinal);

    private static T Required<T>(Node root, string name) where T : Node => root.FindChild(name, true, false) as T
        ?? throw new InvalidOperationException("Missing fixture control: " + name);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
