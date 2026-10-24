using AppAgasCs.Features.Ai.Settings;
using Xunit;

namespace AppAgasCs.Tests.Core.Ai;

public sealed class AgasAiUnifiedModelCatalogTests
{
    [Fact]
    public void Models_ExposeDatedSixteenModelFallbackInDeterministicOrder()
    {
        Assert.Equal(
            new[]
            {
                "gpt-image-2",
                "gpt-image-2.5-flare",
                "gpt-image-2.5-sunburst",
                "gemini-image",
                "jimeng-image-5.0-lite", "jimeng-image-2.0-pro", "jimeng-image-3.0", "jimeng-image-3.1",
                "jimeng-image-4.0", "jimeng-image-4.1", "jimeng-image-4.5", "jimeng-image-4.6",
                "jimeng-image-4.7", "jimeng-image-5.0-pro", "jimeng-image-seedream-5.0-flash", "jimeng-image-v8.2",
            },
            AgasAiUnifiedModelCatalog.Models.Select(model => model.ModelName));
        Assert.All(
            AgasAiUnifiedModelCatalog.Models,
            model => Assert.Equal(AgasAiUnifiedModelCatalog.ConnectionAdapterKind, model.AdapterKind));
    }

    [Fact]
    public void TryGet_IsCaseInsensitiveAndReturnsCanonicalModel()
    {
        Assert.True(AgasAiUnifiedModelCatalog.TryGet(" GPT-IMAGE-2.5-FLARE ", out AgasAiUnifiedModel model));
        Assert.Equal("gpt-image-2.5-flare", model.ModelName);
        Assert.Equal("GPT Image 2.5 Flare", model.DisplayName);
        Assert.Contains("nativeAlphaMask", model.Capabilities);
    }

    [Fact]
    public void JimengFallback_UsesVerifiedTextOnlyFixedSizeContract()
    {
        Assert.Equal("2026-10-24", AgasAiUnifiedModelCatalog.SnapshotDate);
        Assert.All(AgasAiUnifiedModelCatalog.Models.Where(model => model.Category == "jimeng"), model =>
        {
            Assert.Equal(new[] { "textToImage" }, model.Capabilities);
            Assert.Equal(new[] { "1:1" }, model.Ratios);
            Assert.Equal("1:1", model.DefaultRatio);
            Assert.Equal(1, model.MaxImages);
        });
    }
}
