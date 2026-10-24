using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Prompting;
using Xunit;

namespace AppAgasCs.Tests.Core.Ai;

public sealed class PromptComposerTests
{
    private const string LegacyTask =
        "随请求提供的是参考图。请保持参考图中已有内容不变，仅补全其中的透明或空白部分，并返回与参考图相同尺寸的完整图片。"
        + "建筑物、房屋、屋顶轮廓必须正南正北、与画布网格正交，禁止45度或任意斜向放置；道路、弯道、分叉可以斜向或弯曲。";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyFixedFragment_IsReplacedWithoutOverridingExplicitRepaint(bool withHeader)
    {
        string repaint = "把参考图的彩色占形重绘为草，黑色部分重绘为泥土。";
        string old = withHeader ? $"{PromptComposer.TaskHeader}\n{LegacyTask}" : LegacyTask;
        string composed = PromptComposer.Compose(
            new[] { new PromptFragmentContent(old, true, 0), new PromptFragmentContent(repaint, true, 1) },
            null, PromptCompositionMode.ReferenceEdit);
        Assert.Contains(repaint, composed);
        Assert.DoesNotContain("仅补全其中的透明或空白部分", composed);
        Assert.Contains("编辑蒙版明确要求保留", composed);
        Assert.Equal(1, Count(composed, PromptComposer.TaskHeader));
    }

    [Fact]
    public void EnsureCanonicalTask_MigratesPreviouslyComposedPromptAndIsIdempotent()
    {
        string old = $"Repaint the opaque occupancy map as grass and dirt.\n\n{PromptComposer.TaskHeader}\n{LegacyTask}";
        string migrated = PromptComposer.EnsureCanonicalTask(old, PromptCompositionMode.ReferenceEdit);
        Assert.StartsWith("Repaint the opaque occupancy map as grass and dirt.", migrated);
        Assert.DoesNotContain("仅补全其中的透明或空白部分", migrated);
        Assert.Equal(1, Count(migrated, PromptComposer.TaskHeader));
        Assert.Equal(migrated, PromptComposer.EnsureCanonicalTask(migrated, PromptCompositionMode.ReferenceEdit));
    }

    [Fact]
    public void ReferenceEdit_ComposesOrderedFragmentsCustomAndOneFinalTask()
    {
        PromptFragmentContent[] fragments =
        {
            Fragment("negative", "no vehicles", 2, PromptFragmentRole.NegativeConstraint),
            Fragment("environment", "ruined city", 0, PromptFragmentRole.Environment),
            Fragment("disabled", "must not appear", 1, PromptFragmentRole.General, enabled: false),
            Fragment("empty", "   ", 3, PromptFragmentRole.General),
            Fragment("legacy-task", PromptComposer.ReferenceEditTask, 4, PromptFragmentRole.General),
        };

        string composed = PromptComposer.Compose(fragments, "extra detail", PromptCompositionMode.ReferenceEdit);

        Assert.Equal(
            $"ruined city\nno vehicles\nextra detail\n\n[生图要求]\n{PromptComposer.ReferenceEditTask}",
            composed);
        Assert.Equal(1, Count(composed, PromptComposer.ReferenceEditTask));
        Assert.EndsWith(PromptComposer.ReferenceEditTask, composed, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyContent_ProducesOnlyOneTaskWithoutFallbackDuplication()
    {
        string composed = PromptComposer.Compose(Array.Empty<PromptFragmentContent>(), null, PromptCompositionMode.ReferenceEdit);

        Assert.Equal($"[生图要求]\n{PromptComposer.ReferenceEditTask}", composed);
    }

    [Fact]
    public void RepeatedWholeFragments_AppearOnceInFirstOccurrenceOrder()
    {
        PromptFragmentContent[] fragments =
        {
            Fragment("first", "alpha\nsecond line", 0, PromptFragmentRole.Environment),
            Fragment("second", "beta", 1, PromptFragmentRole.General),
            Fragment("repeat", "  alpha\nsecond line  ", 2, PromptFragmentRole.General),
            Fragment("similar", "alpha\nsecond line extra", 3, PromptFragmentRole.General),
        };

        string composed = PromptComposer.Compose(fragments, "beta", PromptCompositionMode.ReferenceEdit);

        Assert.Equal(
            $"alpha\nsecond line\nbeta\nalpha\nsecond line extra\n\n[生图要求]\n{PromptComposer.ReferenceEditTask}",
            composed);
    }

    [Fact]
    public void TextToImageTask_DoesNotClaimAnAttachedReference()
    {
        string composed = PromptComposer.Compose(
            new[] { Fragment("content", "ruined city", 0, PromptFragmentRole.Environment) },
            null,
            PromptCompositionMode.TextToImage);

        Assert.EndsWith(PromptComposer.TextToImageTask, composed, StringComparison.Ordinal);
        Assert.DoesNotContain("参考图", PromptComposer.TextToImageTask, StringComparison.Ordinal);
        Assert.DoesNotContain("attached", PromptComposer.TextToImageTask, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TechnicalDetails_AreInsertedBeforeTheFinalCanonicalTask()
    {
        string composed = PromptComposer.Compose(
            new[] { Fragment("content", "ruined city", 0, PromptFragmentRole.Environment) },
            null,
            PromptCompositionMode.ReferenceEdit);

        string withTechnical = PromptComposer.InsertTechnicalBeforeTask(composed, "[Technical]\ncanvas=64x64");

        Assert.Equal(
            $"ruined city\n\n[Technical]\ncanvas=64x64\n\n[生图要求]\n{PromptComposer.ReferenceEditTask}",
            withTechnical);
        Assert.EndsWith(PromptComposer.ReferenceEditTask, withTechnical, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyContent_TechnicalDetailsStillPrecedeTheSingleFinalTask()
    {
        string composed = PromptComposer.Compose(
            Array.Empty<PromptFragmentContent>(), null, PromptCompositionMode.ReferenceEdit);

        string withTechnical = PromptComposer.InsertTechnicalBeforeTask(composed, "[Technical]\ncanvas=64x64");

        Assert.Equal(
            $"[Technical]\ncanvas=64x64\n\n[生图要求]\n{PromptComposer.ReferenceEditTask}",
            withTechnical);
        Assert.Equal(1, Count(withTechnical, PromptComposer.ReferenceEditTask));
        Assert.EndsWith(PromptComposer.ReferenceEditTask, withTechnical, StringComparison.Ordinal);
    }

    private static PromptFragmentContent Fragment(
        string id,
        string text,
        int order,
        PromptFragmentRole role,
        bool enabled = true) => new(text, enabled, order, role);

    private static int Count(string value, string token) =>
        value.Split(token, StringSplitOptions.None).Length - 1;
}
