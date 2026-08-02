using GptController.Services;

namespace GptController.Tests;

public sealed class CodexVersionServiceTests
{
    [Theory]
    [InlineData("codex-cli 0.146.0", 0, 146, 0)]
    [InlineData("codex 1.2.3-beta.1", 1, 2, 3)]
    public void ParseReadsSemanticVersion(
        string input,
        int major,
        int minor,
        int build)
    {
        Assert.Equal(new Version(major, minor, build), CodexVersionService.Parse(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    public void ParseRejectsMissingVersion(string input)
    {
        Assert.Null(CodexVersionService.Parse(input));
    }

    [Fact]
    public void MinimumVersionMatchesCommandBackedProviderRequirement()
    {
        Assert.Equal(new Version(0, 146, 0), CodexVersionService.MinimumDeepSeekVersion);
    }
}
