namespace GptAccountManager.Tests;

internal static class TestAuthFactory
{
    public static byte[] Create(
        string accountId,
        string email = "user@example.com",
        string plan = "plus",
        string? refreshToken = "refresh-token",
        string? organizationId = null,
        IReadOnlyList<(string Id, string Title)>? organizations = null)
    {
        var authClaims = new Dictionary<string, object?>
        {
            ["chatgpt_account_id"] = accountId,
            ["chatgpt_plan_type"] = plan
        };
        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            authClaims["organization_id"] = organizationId;
        }

        if (organizations is not null)
        {
            authClaims["organizations"] = organizations
                .Select(item => new { id = item.Id, title = item.Title })
                .ToArray();
        }

        var payload = new Dictionary<string, object?>
        {
            ["email"] = email,
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            ["https://api.openai.com/auth"] = authClaims
        };
        var idToken = CreateJwt(payload);
        var accessToken = CreateJwt(payload);
        var tokens = new Dictionary<string, object?>
        {
            ["id_token"] = idToken,
            ["access_token"] = accessToken,
            ["account_id"] = accountId
        };
        if (refreshToken is not null)
        {
            tokens["refresh_token"] = refreshToken;
        }

        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            auth_mode = "chatgpt",
            tokens
        });
    }

    public static string CreateJwt(IReadOnlyDictionary<string, object?> payload)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            alg = "none",
            typ = "JWT"
        }));
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        return $"{header}.{body}.signature";
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
