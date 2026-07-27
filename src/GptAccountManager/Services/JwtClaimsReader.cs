using System.Text;
using System.Text.Json;

namespace GptAccountManager.Services;

public sealed record OrganizationClaim(string? Id, string? Title);

public sealed record AuthClaims(
    string? Email,
    string? AccountId,
    string? OrganizationId,
    string? PlanType,
    IReadOnlyList<OrganizationClaim> Organizations,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? AccessTokenExpiresAt);

public static class JwtClaimsReader
{
    private const string AuthNamespace = "https://api.openai.com/auth";
    private const string ProfileNamespace = "https://api.openai.com/profile";

    public static AuthClaims Read(AuthDocumentInfo auth)
    {
        if (string.IsNullOrWhiteSpace(auth.IdToken))
        {
            return new AuthClaims(
                null,
                auth.StoredAccountId,
                null,
                null,
                [],
                null,
                null);
        }

        using var idDocument = DecodePayload(auth.IdToken);
        using var accessDocument = string.IsNullOrWhiteSpace(auth.AccessToken)
            ? null
            : TryDecodePayload(auth.AccessToken);

        var idRoot = idDocument.RootElement;
        var accessRoot = accessDocument?.RootElement;
        var idAuth = GetObject(idRoot, AuthNamespace);
        var accessAuth = accessRoot is { } access
            ? GetObject(access, AuthNamespace)
            : null;
        var idProfile = GetObject(idRoot, ProfileNamespace);
        var accessProfile = accessRoot is { } accessProfileRoot
            ? GetObject(accessProfileRoot, ProfileNamespace)
            : null;

        var organizations = ReadOrganizations(idAuth)
            .Concat(ReadOrganizations(accessAuth))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) || !string.IsNullOrWhiteSpace(item.Title))
            .GroupBy(item => $"{item.Id}\0{item.Title}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var email =
            ReadString(idRoot, "email") ??
            ReadString(idRoot, "preferred_username") ??
            ReadString(idRoot, "upn") ??
            ReadString(idProfile, "email") ??
            ReadString(accessRoot, "email") ??
            ReadString(accessRoot, "preferred_username") ??
            ReadString(accessRoot, "upn") ??
            ReadString(accessProfile, "email");

        var accountId =
            ReadString(idAuth, "chatgpt_account_id") ??
            ReadString(idAuth, "account_id") ??
            ReadString(accessAuth, "chatgpt_account_id") ??
            ReadString(accessAuth, "account_id") ??
            auth.StoredAccountId;

        var organizationId =
            ReadString(idAuth, "organization_id") ??
            ReadString(idAuth, "chatgpt_organization_id") ??
            ReadString(idAuth, "org_id") ??
            ReadString(accessAuth, "organization_id") ??
            ReadString(accessAuth, "chatgpt_organization_id") ??
            ReadString(accessAuth, "org_id");

        var plan =
            ReadString(idAuth, "chatgpt_plan_type") ??
            ReadString(accessAuth, "chatgpt_plan_type");

        var expirations = new[]
        {
            ReadExpiration(idRoot),
            accessRoot is { } accessExpirationRoot
                ? ReadExpiration(accessExpirationRoot)
                : null
        }.Where(value => value.HasValue).Select(value => value!.Value).ToArray();

        return new AuthClaims(
            email,
            accountId,
            organizationId,
            plan,
            organizations,
            expirations.Length == 0 ? null : expirations.Min(),
            accessRoot is { } accessTokenRoot
                ? ReadExpiration(accessTokenRoot)
                : null);
    }

    private static JsonDocument DecodePayload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
        {
            throw new InvalidDataException("JWT payload is missing.");
        }

        var normalized = parts[1].Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
        return JsonDocument.Parse(json);
    }

    private static JsonDocument? TryDecodePayload(string token)
    {
        try
        {
            return DecodePayload(token);
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement? GetObject(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Object
            ? property
            : null;
    }

    private static string? ReadString(JsonElement? element, string propertyName)
    {
        if (element is not { } source ||
            source.ValueKind != JsonValueKind.Object ||
            !source.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static IReadOnlyList<OrganizationClaim> ReadOrganizations(JsonElement? auth)
    {
        if (auth is not { } source ||
            !source.TryGetProperty("organizations", out var organizations) ||
            organizations.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<OrganizationClaim>();
        foreach (var organization in organizations.EnumerateArray())
        {
            if (organization.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            result.Add(new OrganizationClaim(
                ReadString(organization, "id"),
                ReadString(organization, "title") ?? ReadString(organization, "name")));
        }

        return result;
    }

    private static DateTimeOffset? ReadExpiration(JsonElement payload)
    {
        if (!payload.TryGetProperty("exp", out var expiration) ||
            !expiration.TryGetInt64(out var seconds) ||
            seconds <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch
        {
            return null;
        }
    }
}
