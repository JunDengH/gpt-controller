using GptAccountManager.Services;

namespace GptAccountManager.Tests;

public sealed class CredentialHelperLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"helper-locator-{Guid.NewGuid():N}");

    [Fact]
    public void LocateReturnsPackagedHelper()
    {
        Directory.CreateDirectory(_root);
        var expected = Path.Combine(_root, CredentialHelperLocator.ExecutableName);
        File.WriteAllBytes(expected, []);

        Assert.Equal(Path.GetFullPath(expected), CredentialHelperLocator.Locate(_root));
    }

    [Fact]
    public void LocateFailsWithFriendlyErrorWhenPackageIsIncomplete()
    {
        var exception = Assert.Throws<FileNotFoundException>(() =>
            CredentialHelperLocator.Locate(_root));

        Assert.Contains("凭据助手缺失", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best effort test cleanup.
        }
    }
}
