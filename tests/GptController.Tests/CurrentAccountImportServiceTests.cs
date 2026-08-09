using System.Text;
using System.Text.Json;
using GptController.Infrastructure;
using GptController.Models;
using GptController.Services;

namespace GptController.Tests;

public sealed class CurrentAccountImportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "GptController.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FindLiveProfileIdentifiesOriginalAccountWithoutChangingActiveState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string accountId = "original-chatgpt-account";
        var paths = new AppPaths(_root, _root);
        paths.EnsureCreated();
        Directory.CreateDirectory(paths.CodexHome);
        var vault = new ProfileVault(paths);
        var credential = CreateCredential(accountId);
        var profile = await vault.UpsertProfileAsync(
            new AccountProfile
            {
                AccountId = accountId,
                Email = "original@example.com",
                Nickname = "Original",
                IsActive = false
            },
            credential);
        await File.WriteAllBytesAsync(paths.LiveAuthFile, credential);
        var service = new CurrentAccountImportService(
            paths,
            vault,
            new AccountMetadataService());

        var found = await service.FindLiveProfileAsync();

        Assert.Equal(profile.Id, found?.Id);
        Assert.False((await vault.GetProfileAsync(profile.Id))!.IsActive);
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
            // Best effort cleanup.
        }
    }

    private static byte[] CreateCredential(string accountId)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new Dictionary<string, object?>
            {
                ["https://api.openai.com/auth"] =
                    new Dictionary<string, string>
                    {
                        ["chatgpt_account_id"] = accountId
                    }
            });
        var jwt = $"{Base64Url(Encoding.UTF8.GetBytes("{}"))}.{Base64Url(payload)}.signature";
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            tokens = new
            {
                id_token = jwt,
                access_token = jwt,
                refresh_token = "refresh-token",
                account_id = accountId
            }
        });
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
