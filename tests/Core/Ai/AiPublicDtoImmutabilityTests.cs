using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using Xunit;

namespace AppAgasCs.Core.Tests.Ai;

public sealed class AiPublicDtoImmutabilityTests
{
    [Fact]
    public void GeneratedImageData_DefensivelyCopiesInputAndOutputBytes()
    {
        byte[] source = { 1, 2, 3 };
        var image = new GeneratedImageData(source, "image/png");
        source[0] = 9;
        byte[] exposed = image.Bytes;
        exposed[1] = 8;

        Assert.Equal(new byte[] { 1, 2, 3 }, image.Bytes);
    }

    [Fact]
    public void ProfilesDocument_DefensivelySnapshotsProfileList()
    {
        var original = new AgasAiProfile("id-1", "One", "provider", "model", "https://example.test");
        var replacement = original with { Id = "id-2" };
        AgasAiProfile[] source = { original };
        var document = new AgasAiProfilesDocument(3, original.Id, source);
        source[0] = replacement;

        Assert.Same(original, Assert.Single(document.Profiles));
        Assert.False(document.Profiles is AgasAiProfile[]);
    }

    [Fact]
    public void RemovedPreviewResultContract_DoesNotRemainInProductionSource()
    {
        string contracts = File.ReadAllText(Path.Combine(
            AppAgasCs.Tests.Core.ProjectRoot.Path,
            "src/Application/Contracts/AiGenerationWorkspaceContracts.cs"));
        Assert.DoesNotContain("AiGeneratedImagePreviewResult", contracts, StringComparison.Ordinal);
    }
}
