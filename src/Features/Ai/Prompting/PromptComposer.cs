#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AppAgasCs.Core.Resources;

namespace AppAgasCs.Features.Ai.Prompting;

public enum PromptCompositionMode
{
    ReferenceEdit,
    TextToImage,
}

public sealed record PromptFragmentContent(
    string Text,
    bool Enabled,
    int Order,
    PromptFragmentRole Role = PromptFragmentRole.General);

public static class PromptComposer
{
    public const string TaskHeader = "[生图要求]";
    public const string ReferenceEditTask =
        "随请求提供的是参考图。请按照提示词和参考图完成生成或编辑，保留提示词或编辑蒙版明确要求保留的内容，并返回与参考图相同尺寸的完整图片。"
        + "建筑物、房屋、屋顶轮廓必须正南正北、与画布网格正交，禁止45度或任意斜向放置；道路、弯道、分叉可以斜向或弯曲。";
    private const string LegacyReferenceEditTask =
        "随请求提供的是参考图。请保持参考图中已有内容不变，仅补全其中的透明或空白部分，并返回与参考图相同尺寸的完整图片。"
        + "建筑物、房屋、屋顶轮廓必须正南正北、与画布网格正交，禁止45度或任意斜向放置；道路、弯道、分叉可以斜向或弯曲。";
    public const string TextToImageTask = "请根据以上内容生成一张完整图片。";

    private static readonly HashSet<string> CanonicalTasks = new(StringComparer.Ordinal)
    {
        ReferenceEditTask,
        LegacyReferenceEditTask,
        TextToImageTask,
        $"{TaskHeader}\n{ReferenceEditTask}",
        $"{TaskHeader}\n{LegacyReferenceEditTask}",
        $"{TaskHeader}\n{TextToImageTask}",
    };

    public static string Compose(
        IEnumerable<PromptFragmentResource>? fragments,
        string? customPrompt,
        PromptCompositionMode mode)
        => Compose(
            fragments?.Select(fragment => new PromptFragmentContent(
                fragment.Text,
                fragment.Enabled,
                fragment.Order,
                fragment.Role)),
            customPrompt,
            mode);

    public static string Compose(
        IEnumerable<PromptFragmentContent>? fragments,
        string? customPrompt,
        PromptCompositionMode mode)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sections = (fragments ?? Array.Empty<PromptFragmentContent>())
            .Where(fragment => fragment.Enabled)
            .OrderBy(fragment => fragment.Order)
            .Select(fragment => NormalizeContent(fragment.Text))
            .Where(section => IsContentSection(section) && seen.Add(section))
            .ToList();
        string custom = NormalizeContent(customPrompt);
        if (IsContentSection(custom) && seen.Add(custom))
        {
            sections.Add(custom);
        }

        string task = mode == PromptCompositionMode.TextToImage
            ? TextToImageTask
            : ReferenceEditTask;
        sections.Add($"{TaskHeader}\n{task}");
        return string.Join("\n", sections.Select((section, index) =>
            index == sections.Count - 1 && index > 0 ? $"\n{section}" : section));
    }

    public static string InsertTechnicalBeforeTask(string composedPrompt, string? technicalDetails)
    {
        string prompt = composedPrompt?.Trim() ?? string.Empty;
        string technical = technicalDetails?.Trim() ?? string.Empty;
        if (technical.Length == 0)
        {
            return prompt;
        }

        string taskPrefix = $"{TaskHeader}\n";
        string marker = $"\n\n{taskPrefix}";
        int taskIndex = prompt.StartsWith(taskPrefix, StringComparison.Ordinal)
            ? 0
            : prompt.LastIndexOf(marker, StringComparison.Ordinal);
        if (taskIndex < 0)
        {
            return prompt.Length == 0 ? technical : $"{prompt}\n\n{technical}";
        }
        return taskIndex == 0
            ? $"{technical}\n\n{prompt}"
            : $"{prompt[..taskIndex]}\n\n{technical}{prompt[taskIndex..]}";
    }

    public static string EnsureCanonicalTask(string prompt, PromptCompositionMode mode)
    {
        string value = prompt?.Trim() ?? string.Empty;
        string task = mode == PromptCompositionMode.TextToImage ? TextToImageTask : ReferenceEditTask;
        return value.EndsWith($"{TaskHeader}\n{task}", StringComparison.Ordinal)
            ? value
            : Compose(Array.Empty<PromptFragmentContent>(), value, mode);
    }

    public static string LimitPreservingFinalTask(string prompt, int maximumRunes)
    {
        string value = prompt ?? string.Empty;
        if (maximumRunes < 1 || value.EnumerateRunes().Count() <= maximumRunes)
        {
            return value;
        }
        int taskIndex = value.LastIndexOf(TaskHeader, StringComparison.Ordinal);
        if (taskIndex < 0)
        {
            return string.Concat(value.EnumerateRunes().Take(maximumRunes).Select(rune => rune.ToString()));
        }
        string task = value[taskIndex..];
        int taskRunes = task.EnumerateRunes().Count();
        int prefixBudget = Math.Max(0, maximumRunes - taskRunes - 2);
        string prefix = string.Concat(value[..taskIndex].TrimEnd().EnumerateRunes()
            .Take(prefixBudget).Select(rune => rune.ToString()));
        return prefix.Length == 0 ? task : $"{prefix}\n\n{task}";
    }

    private static string NormalizeContent(string? text)
    {
        string value = text?.Trim() ?? string.Empty;
        string legacySuffix = $"\n\n{TaskHeader}\n{LegacyReferenceEditTask}";
        return value.EndsWith(legacySuffix, StringComparison.Ordinal)
            ? value[..^legacySuffix.Length].TrimEnd()
            : value;
    }

    private static bool IsContentSection(string value) =>
        value.Length > 0 && !CanonicalTasks.Contains(value);
}
