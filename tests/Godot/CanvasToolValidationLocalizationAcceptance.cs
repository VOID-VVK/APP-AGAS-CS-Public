#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AGAS.UI;
using AGAS.UI.Controllers;
using AppAgasCs.Features.Tools.Execution;
using AppAgasCs.UI.Shared;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class CanvasToolValidationLocalizationAcceptance : Node
{
    private int _checks;

    public override async void _Ready()
    {
        string previousLocale = TranslationServer.GetLocale();
        int exitCode = 0;
        try
        {
            var workspace = new CanvasToolValidationOnlyWorkspace();
            AddChild(workspace);
            await Frames();
            ulong labelId = workspace.Status.GetInstanceId();
            string home = System.Environment.GetEnvironmentVariable("AGAS_HOME") ?? throw new InvalidOperationException("An isolated AGAS_HOME is required.");
            Dictionary<string, byte[]> before = Snapshot(home);
            var failures = new[]
            {
                CanvasToolRunResult.Fail(CanvasToolRunErrorCodes.ContextUnavailable, "请先选中可编辑图层中的图片。"),
                CanvasToolRunResult.Fail(CanvasToolRunErrorCodes.ToolChanged, "所选工具在执行期间发生变化，请刷新后重试。"),
                CanvasToolRunResult.Fail(CanvasToolRunErrorCodes.ContextStale, "执行期间项目、场景、图层或所选图片已变化，结果未应用。"),
                CanvasToolRunResult.Fail(CanvasToolRunErrorCodes.ApplyFailed, "画布目标已变化或不可编辑，工具结果未应用。"),
            };
            foreach (CanvasToolRunResult failure in failures)
            {
                Inject(workspace, failure);
                foreach (string locale in new[] { "en", "zh_CN", "en" })
                {
                    AppLocale.Apply(locale);
                    await Frames();
                    if (locale == "en") Require(AppLocale.Translate(failure.Message) != failure.Message, "The real English native catalog must contain the validation hint.");
                    Verify(workspace, labelId, failure.Code, AppLocale.Translate(failure.Message));
                }
            }
            Inject(workspace, CanvasToolRunResult.Fail(CanvasToolRunErrorCodes.ContextUnavailable, "项目为只读状态（设置）。"));
            foreach (string locale in new[] { "zh_CN", "en" })
            {
                AppLocale.Apply(locale);
                await Frames();
                Verify(workspace, labelId, CanvasToolRunErrorCodes.ContextUnavailable, AppLocale.Format("项目为只读状态（{0}）。", "设置"));
            }
            foreach (string raw in new[] { failures[0].Message, "保存 {\"path\":\"设置\"}" })
            {
                Inject(workspace, CanvasToolRunResult.Fail(ImageToolRunErrorCodes.ScriptFailed, raw));
                foreach (string locale in new[] { "en", "zh_CN", "en" })
                {
                    AppLocale.Apply(locale);
                    await Frames();
                    Verify(workspace, labelId, ImageToolRunErrorCodes.ScriptFailed, raw);
                }
            }
            Dictionary<string, byte[]> after = Snapshot(home);
            Require(before.Count == after.Count && before.All(entry => after.TryGetValue(entry.Key, out byte[]? bytes) && entry.Value.SequenceEqual(bytes)), "UI fault injection and locale changes must not write user/tool files.");
            Require(!Directory.EnumerateFiles(home, "*.png", SearchOption.AllDirectories).Any(), "No tool output may be generated.");
            workspace.Free();
            GD.Print($"[AGAS][CANVAS-TOOL-VALIDATION-LOCALIZATION][PASS] {_checks} checks; no tool execution");
        }
        catch (Exception error)
        {
            exitCode = 1;
            GD.PushError($"[AGAS][CANVAS-TOOL-VALIDATION-LOCALIZATION][FAIL] {error}");
        }
        finally
        {
            foreach (Node child in GetChildren()) child.Free();
            TranslationServer.SetLocale(previousLocale);
            GetTree().Quit(exitCode);
        }
    }

    private void Verify(CanvasToolValidationOnlyWorkspace workspace, ulong labelId, string code, string detail)
    {
        string expected = AppLocale.Format("工具执行失败（{0}）：{1}", code, detail);
        Require(workspace.Status.Text == expected, $"Expected '{expected}', got '{workspace.Status.Text}'.");
        Require(workspace.Status.GetInstanceId() == labelId && workspace.Status.AutoTranslateMode == Node.AutoTranslateModeEnum.Disabled, "Locale changes must only update the existing status label.");
        Require((bool)typeof(MainWorkspace).GetField("_canvasToolRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workspace)! == false, "Fault injection must not begin a tool action.");
    }

    private static void Inject(MainWorkspace workspace, CanvasToolRunResult failure) => typeof(MainWorkspace)
        .GetMethod("SetCanvasToolFailureStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(workspace, new object[] { failure });

    private async Task Frames()
    {
        for (int frame = 0; frame < 3; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static Dictionary<string, byte[]> Snapshot(string home) => Directory.GetFiles(home, "*", SearchOption.AllDirectories)
        .ToDictionary(file => Path.GetRelativePath(home, file), File.ReadAllBytes, StringComparer.Ordinal);

    private void Require(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal partial class CanvasToolValidationOnlyWorkspace : MainWorkspace
{
    internal Label Status { get; } = new() { Name = "SaveStatusLabel", AutoTranslateMode = AutoTranslateModeEnum.Disabled };

    public override void _Ready()
    {
        AddChild(Status);
        typeof(MainWorkspace).GetField("_statusLabel", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, Status);
    }
}
