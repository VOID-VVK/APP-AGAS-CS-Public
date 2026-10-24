using AppAgasCs.Features.Ai.Settings;
using Xunit;

namespace AppAgasCs.Tests.Core.Ai;

public sealed class AgasAiUnifiedModelCatalogTests
{
    [Fact]
    public void Models_ExposeSevenCanonicalEntriesInDeterministicOrder()
    {
        Assert.Equal(
            new[]
            {
                "gpt-image-2",
                "gpt-image-2.5-flare",
                "gpt-image-2.5-sunburst",
                "grok-imagine-image-quality",
                "grok-imagine-image",
                "grok-imagine-image-2.0",
                "gemini-image",
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

    [Theory]
    [InlineData("grok-imagine-image-quality")]
    [InlineData("grok-imagine-image")]
    [InlineData("grok-imagine-image-2.0")]
    public void GrokCapabilities_AreConservativeWithoutVerifiedEditContract(string modelName)
    {
        Assert.True(AgasAiUnifiedModelCatalog.TryGet(modelName, out AgasAiUnifiedModel model));
        Assert.Equal(new[] { "textToImage" }, model.Capabilities);
    }
}
