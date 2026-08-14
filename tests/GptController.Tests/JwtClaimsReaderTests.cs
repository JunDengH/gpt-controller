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

    [Fact]
    public void OrganizationClaimsKeepIdsForExactWorkspaceSelection()
    {
        var auth = new AuthDocumentInfo(
            CreateJwt(new Dictionary<string, object?>
            {
                ["https://api.openai.com/auth"] =
                    new Dictionary<string, object?>
                    {
                        ["chatgpt_account_id"] = "workspace-current",
                        ["organization_id"] = "workspace-current",
                        ["chatgpt_plan_type"] = "business",
                        ["organizations"] = new object[]
                        {
                            new Dictionary<string, string>
                            {
                                ["id"] = "workspace-other",
                                ["title"] = "其他组织"
                            },
                            new Dictionary<string, string>
                            {
                                ["id"] = "workspace-current",
                                ["name"] = "当前组织"
                            }
                        }
                    }
            }),
            CreateJwt(new Dictionary<string, object?>
            {
                ["https://api.openai.com/auth"] =
                    new Dictionary<string, object?>
                    {
                        ["organizations"] = new object[]
                        {
                            new Dictionary<string, string>
                            {
                                ["id"] = "workspace-third",
                                ["title"] = "第三组织"
                            }
                        }
                    }
            }),
            "refresh-token",
            "workspace-current");

        var claims = JwtClaimsReader.Read(auth);

        Assert.Equal("workspace-current", claims.AccountId);
        Assert.Equal("workspace-current", claims.OrganizationId);
        Assert.Equal("business", claims.PlanType);
        Assert.Collection(
            claims.Organizations,
            organization =>
            {
                Assert.Equal("workspace-other", organization.Id);
                Assert.Equal("其他组织", organization.Title);
            },
            organization =>
            {
                Assert.Equal("workspace-current", organization.Id);
                Assert.Equal("当前组织", organization.Title);
            },
            organization =>
            {
                Assert.Equal("workspace-third", organization.Id);
                Assert.Equal("第三组织", organization.Title);
            });
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
