using System.Text.Json;
using System.Text.Json.Nodes;

namespace GptController.Services;

public sealed record AuthDocumentInfo(
    string? IdToken,
    string? AccessToken,
    string? RefreshToken,
    string? StoredAccountId)
{
    public bool HasManagedTokens =>
        !string.IsNullOrWhiteSpace(IdToken) &&
        !string.IsNullOrWhiteSpace(AccessToken);

    public bool HasRefreshToken => !string.IsNullOrWhiteSpace(RefreshToken);
}

public static class AuthDocument
{
    public static AuthDocumentInfo Inspect(ReadOnlySpan<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("auth.json root must be an object.");
        }

        var root = document.RootElement;
        var tokens = TryGetObject(root, "tokens");
        var idToken = ReadString(tokens, "id_token") ?? ReadString(tokens, "idToken");
        var accessToken = ReadString(tokens, "access_token") ?? ReadString(tokens, "accessToken");
        var refreshToken = ReadString(tokens, "refresh_token") ?? ReadString(tokens, "refreshToken");
        var accountId =
            ReadString(tokens, "account_id") ??
            ReadString(tokens, "accountId") ??
            ReadString(root, "account_id") ??
            ReadString(root, "accountId");

        return new AuthDocumentInfo(idToken, accessToken, refreshToken, accountId);
    }

    public static byte[] RemoveRefreshToken(ReadOnlySpan<byte> bytes)
    {
        var root = JsonNode.Parse(bytes)
            ?? throw new InvalidDataException("auth.json could not be parsed.");
        if (root["tokens"] is JsonObject tokens)
        {
            tokens.Remove("refresh_token");
            tokens.Remove("refreshToken");
        }

        return JsonSerializer.SerializeToUtf8Bytes(root, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    public static bool SemanticallyEqual(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        try
        {
            var leftNode = JsonNode.Parse(left);
            var rightNode = JsonNode.Parse(right);
            return JsonNode.DeepEquals(leftNode, rightNode);
        }
        catch
        {
            return left.SequenceEqual(right);
        }
    }

    private static JsonElement? TryGetObject(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.Object
            ? value
            : null;
    }

    private static string? ReadString(JsonElement? element, string propertyName)
    {
        if (element is not { } value ||
            !value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var result = property.GetString();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
