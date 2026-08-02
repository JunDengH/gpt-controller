using GptController.Infrastructure;

namespace GptController.Tests;

public sealed class ApplicationInfoTests
{
    [Fact]
    public void InformationalVersionKeepsPrereleaseAndDropsBuildMetadata()
    {
        var version = ApplicationInfo.NormalizeVersion(
            "1.2.3-beta.1+abcdef",
            new Version(9, 9, 9, 9));

        Assert.Equal("1.2.3-beta.1", version);
    }

    [Fact]
    public void AssemblyVersionFallbackOmitsZeroRevision()
    {
        var version = ApplicationInfo.NormalizeVersion(
            null,
            new Version(1, 2, 3, 0));

        Assert.Equal("1.2.3", version);
    }
}
