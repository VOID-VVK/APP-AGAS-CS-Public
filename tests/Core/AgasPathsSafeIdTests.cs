using AppAgasCs.Core.Persistence;
using Xunit;

namespace AppAgasCs.Tests.Core;

public sealed class AgasPathsSafeIdTests
{
    [Fact]
    public void SafeIdContract_AcceptsAtMostOneHundredTwentyCharacters()
    {
        Assert.Equal(120, AgasPaths.MaxSafeIdLength);
        Assert.True(AgasPaths.IsSafeId(new string('a', AgasPaths.MaxSafeIdLength)));
        Assert.False(AgasPaths.IsSafeId(new string('a', AgasPaths.MaxSafeIdLength + 1)));
    }

    [Theory]
    [InlineData("blueprint_01-a", true)]
    [InlineData("../escape", false)]
    [InlineData("blueprint/name", false)]
    [InlineData("蓝图", false)]
    public void SafeIdContract_RejectsNonAsciiOrPathSyntax(string id, bool expected)
    {
        Assert.Equal(expected, AgasPaths.IsSafeId(id));
    }

}
