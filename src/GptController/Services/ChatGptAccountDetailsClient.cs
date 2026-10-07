using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using GptController.Models;

namespace GptController.Services;

public interface IChatGptAccountDetailsClient
{
    Task<AccountReadMetadata> ReadAsync(string accessToken, string accountId, CancellationToken cancellationToken = default);
}

public sealed class ChatGptAccountDetailsException(string errorCode, AccountReadMetadata? partialDetails = null) : Exception("官方账号信息暂不可用。")
{
    public string ErrorCode { get; } = errorCode;
    public AccountReadMetadata? PartialDetails { get; } = partialDetails;
}

public sealed class ChatGptAccountDetailsClient(HttpClient httpClient) : IChatGptAccountDetailsClient
{
    private static readonly Uri AccountsEndpoint = new("https://chatgpt.com/backend-api/wham/accounts/check");
    private static readonly Uri WebAccountsEndpoint = new("https://chatgpt.com/backend-api/accounts/check/v4-2023-04-27");

    public async Task<AccountReadMetadata> ReadAsync(string accessToken, string accountId, CancellationToken cancellationToken = default)
    {
        ValidateHeader(accessToken);
        ValidateHeader(accountId);
        var details = await FetchAsync(AccountsEndpoint, accessToken, accountId, cancellationToken);
        if (details is not null && (!NeedsWorkspaceName(details) ||
            AccountOwnership.HasKnownOrganizationName(details.WorkspaceName))) return details;
        // The desktop's web account endpoint can have richer workspace metadata.
        try
        {
            return await FetchAsync(WebAccountsEndpoint, accessToken, accountId, cancellationToken)
                ?? details ?? throw new ChatGptAccountDetailsException("account_not_found");
        }
        catch (ChatGptAccountDetailsException exception) when (details is not null)
        { throw new ChatGptAccountDetailsException(exception.ErrorCode, details); }
    }

    private async Task<AccountReadMetadata?> FetchAsync(Uri endpoint, string token, string accountId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("ChatGPT-Account-Id", accountId);
        request.Headers.UserAgent.ParseAdd("codex-cli");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
                throw new ChatGptAccountDetailsException($"account_details_http_{(int)response.StatusCode}");
            using var payload = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
            return Parse(payload.RootElement, accountId);
        }
        catch (JsonException) { throw new ChatGptAccountDetailsException("account_details_invalid_json"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ChatGptAccountDetailsException("account_details_timeout"); }
        catch (HttpRequestException) { throw new ChatGptAccountDetailsException("account_details_network"); }
        catch (IOException) { throw new ChatGptAccountDetailsException("account_details_network"); }
    }

    internal static AccountReadMetadata? Parse(JsonElement root, string expectedId)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("accounts", out var accounts)) return null;
        JsonElement selected = default;
        if (accounts.ValueKind == JsonValueKind.Array)
        {
            selected = accounts.EnumerateArray().FirstOrDefault(item => item.ValueKind == JsonValueKind.Object &&
                string.Equals(Text(item, "id") ?? Text(item, "account_id"), expectedId, StringComparison.OrdinalIgnoreCase));
        }
        else if (accounts.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in accounts.EnumerateObject())
                if (string.Equals(entry.Name, expectedId, StringComparison.OrdinalIgnoreCase)) { selected = entry.Value; break; }
        }
        if (selected.ValueKind != JsonValueKind.Object) return null;
        var info = selected.TryGetProperty("account", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : selected;
        var actualId = Text(info, "account_id") ?? Text(info, "id") ?? expectedId;
        if (!string.Equals(actualId, expectedId, StringComparison.OrdinalIgnoreCase))
            throw new ChatGptAccountDetailsException("account_details_identity_mismatch");
        var plan = Text(info, "plan_type");
        if (string.IsNullOrWhiteSpace(plan) || plan == "unknown")
            plan = selected.TryGetProperty("entitlement", out var entitlement) && entitlement.ValueKind == JsonValueKind.Object
                ? Text(entitlement, "subscription_plan") : plan;
        var kind = Text(info, "structure") switch
        {
            "personal" => AccountWorkspaceKind.Personal,
            "workspace" => AccountWorkspaceKind.Workspace,
            _ => (AccountWorkspaceKind?)null
        };
        return new AccountReadMetadata(null, plan, actualId, kind, Text(info, "name"), true);
    }

    private static string? Text(JsonElement item, string key) => item.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
    private static bool NeedsWorkspaceName(AccountReadMetadata details) =>
        details.WorkspaceKind == AccountWorkspaceKind.Workspace ||
        details.WorkspaceKind is null && new AccountMetadataService().NormalizePlan(details.PlanType) is
            MembershipPlan.Team or MembershipPlan.Business or MembershipPlan.Enterprise or MembershipPlan.Edu;
    private static void ValidateHeader(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => character is < '!' or > '~'))
            throw new ChatGptAccountDetailsException("account_details_invalid_auth");
    }
}
