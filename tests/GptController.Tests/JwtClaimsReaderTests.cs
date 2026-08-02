using System.Text;
using System.Text.Json;
using GptController.Services;

namespace GptController.Tests;

public sealed class JwtClaimsReaderTests
{
    [Fact]
    public void AccessTokenExpirationIsReadSeparately()
    {
        var expected = new DateTimeOffset(
            2026,
            7,
            27,
            12,
            5,
            0,
            TimeSpan.Zero);
        var auth = new AuthDocumentInfo(
            CreateJwt(new Dictionary<string, object?>
            {
                ["https://api.openai.com/auth"] =
                    new Dictionary<string, string>
                    {
                        ["chatgpt_account_id"] = "account"
                    }
            }),
            CreateJwt(new Dictionary<string, object?>
            {
                ["exp"] = expected.ToUnixTimeSeconds()
            }),
            "refresh-token",
            "account");

        var claims = JwtClaimsReader.Read(auth);

        Assert.Equal(expected, claims.AccessTokenExpiresAt);
        Assert.Equal("account", claims.AccountId);
    }

    [Fact]
    public void InvalidAccessTokenDoesNotInvalidateIdTokenClaims()
    {
        var auth = new AuthDocumentInfo(
            CreateJwt(new Dictionary<string, object?>
            {
                ["email"] = "test@example.com"
            }),
            "invalid-access-token",
            "refresh-token",
            "account");

        var claims = JwtClaimsReader.Read(auth);

        Assert.Equal("test@example.com", claims.Email);
        Assert.Null(claims.AccessTokenExpiresAt);
    }

    private static string CreateJwt(object payload)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("{}"));
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        return $"{header}.{body}.signature";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
