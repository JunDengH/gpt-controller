using GptController.Models;

namespace GptController.Services;

public sealed record AccountReadMetadata(
    string? Email,
    string? PlanType,
    string? AccountId = null,
    AccountWorkspaceKind? WorkspaceKind = null,
    string? WorkspaceName = null,
    bool HasLiveAccountDetails = false);

public enum AccountWorkspaceKind
{
    Personal,
    Workspace
}

public sealed record ResolvedAccountMetadata(
    string Email,
    string AccountId,
    MembershipPlan MembershipPlan,
    AccountOwnership Ownership,
    string? RawPlanType = null,
    bool AccountMetadataVerified = false,
    string? AccountMetadataErrorCode = null);

public sealed class AccountMetadataService
{
    private readonly IChatGptAccountDetailsClient? _detailsClient;

    public AccountMetadataService(IChatGptAccountDetailsClient? detailsClient = null) => _detailsClient = detailsClient;

    public async Task<ResolvedAccountMetadata> ResolveAsync(
        AuthClaims claims, string? accessToken, string? quotaPlanType = null,
        AccountReadMetadata? accountRead = null, AccountProfile? cached = null,
        CancellationToken cancellationToken = default)
    {
        var expectedId = claims.AccountId ?? accountRead?.AccountId ?? cached?.AccountId;
        if (!string.IsNullOrWhiteSpace(claims.AccountId) && !string.IsNullOrWhiteSpace(accountRead?.AccountId) &&
            !string.Equals(claims.AccountId, accountRead.AccountId, StringComparison.OrdinalIgnoreCase))
            return Resolve(claims, quotaPlanType, accountRead, cached);
        string? errorCode = null;
        if (_detailsClient is not null && !string.IsNullOrWhiteSpace(accessToken) && !string.IsNullOrWhiteSpace(expectedId))
        {
            try
            {
                var details = await _detailsClient.ReadAsync(accessToken, expectedId, cancellationToken);
                accountRead = (accountRead ?? new AccountReadMetadata(null, null)) with
                {
                    AccountId = details.AccountId,
                    PlanType = details.PlanType ?? accountRead?.PlanType,
                    WorkspaceKind = details.WorkspaceKind ?? accountRead?.WorkspaceKind,
                    WorkspaceName = details.WorkspaceName ?? accountRead?.WorkspaceName,
                    HasLiveAccountDetails = true
                };
            }
            catch (ChatGptAccountDetailsException exception)
            {
                errorCode = exception.ErrorCode;
                if (exception.PartialDetails is { } partial &&
                    string.Equals(partial.AccountId, expectedId, StringComparison.OrdinalIgnoreCase))
                    accountRead = (accountRead ?? new AccountReadMetadata(null, null)) with
                    {
                        AccountId = partial.AccountId,
                        PlanType = partial.PlanType ?? accountRead?.PlanType,
                        WorkspaceKind = partial.WorkspaceKind ?? accountRead?.WorkspaceKind,
                        WorkspaceName = partial.WorkspaceName ?? accountRead?.WorkspaceName,
                        HasLiveAccountDetails = true
                    };
            }
        }
        var resolved = Resolve(claims, quotaPlanType, accountRead, cached);
        return resolved with
        {
            AccountMetadataVerified = accountRead?.HasLiveAccountDetails == true || cached?.AccountMetadataVerified == true,
            AccountMetadataErrorCode = errorCode
        };
    }

    public ResolvedAccountMetadata Resolve(
        AuthClaims claims,
        string? quotaPlanType = null,
        AccountReadMetadata? accountRead = null,
        AccountProfile? cached = null)
    {
        var accountId =
            FirstNonEmpty(accountRead?.AccountId, claims.AccountId, cached?.AccountId)
            ?? throw new InvalidDataException("The account identifier is missing.");
        var cachedForAccount = cached is not null &&
                               string.Equals(
                                   cached.AccountId,
                                   accountId,
                                   StringComparison.OrdinalIgnoreCase)
            ? cached
            : null;
        var email =
            FirstNonEmpty(accountRead?.Email, claims.Email, cachedForAccount?.Email)
            ?? "未知邮箱";

        var remotePlanTypes = accountRead?.HasLiveAccountDetails == true
            ? new[] { accountRead.PlanType, quotaPlanType, claims.PlanType }
            : new[] { quotaPlanType, accountRead?.PlanType, claims.PlanType };
        remotePlanTypes = remotePlanTypes.Select(value =>
            string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase) ? null : value).ToArray();
        // A fresh, unfamiliar plan must not be overwritten by an older token/cache.
        var rawPlan = FirstNonEmpty(remotePlanTypes) ?? cachedForAccount?.RawPlanType
            ?? (cachedForAccount is null ? null : ToRawPlan(cachedForAccount.MembershipPlan));
        var plan = NormalizePlan(rawPlan);
        if (rawPlan is null && cachedForAccount is not null)
            plan = cachedForAccount.MembershipPlan;

        var ownership = ResolveOwnership(
            plan,
            claims,
            accountId,
            accountRead,
            cachedForAccount);
        return new ResolvedAccountMetadata(email, accountId, plan, ownership, rawPlan,
            accountRead?.HasLiveAccountDetails == true || cachedForAccount?.AccountMetadataVerified == true);
    }

    public MembershipPlan NormalizePlan(string? rawPlan)
    {
        if (string.IsNullOrWhiteSpace(rawPlan))
        {
            return MembershipPlan.Unknown;
        }

        var normalized = rawPlan
            .Trim()
            .ToLowerInvariant()
            .Replace('_', ' ')
            .Replace('-', ' ');
        normalized = string.Join(
            ' ',
            normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return normalized switch
        {
            "free" or "guest" => MembershipPlan.Free,
            "go" or "chatgpt go" => MembershipPlan.Go,
            "plus" or "chatgpt plus" => MembershipPlan.Plus,
            "prolite" or "pro lite" or "pro 5x" or "pro5x" or "pro 100" or "pro100" or "chatgptprolite" => MembershipPlan.Pro5x,
            "pro" or "chatgpt pro" or "pro 10x" or "pro10x" or "pro 200" or "pro200" or "chatgptpro" => MembershipPlan.Pro10x,
            "promax" or "pro max" or "pro 25x" or "pro25x" or "pro 500" or "pro500" or "chatgptpromax" => MembershipPlan.Pro25x,
            "pro 20x" => MembershipPlan.Pro20x,
            "team" or "chatgpt team" => MembershipPlan.Business,
            "business" or
                "team business" or
                "teambusiness" or
                "chatgpt team business" or
                "chatgpt business" or
                "self serve business" or
                "self serve business prolite" or
                "self serve business usage based" => MembershipPlan.Business,
            "enterprise" or
                "chatgpt enterprise" or
                "hc" or
                "ent26" or
                "enterprise cbp automation" or
                "enterprise cbp usage based" =>
                MembershipPlan.Enterprise,
            "education" or "edu" or "edu plus" or "edu pro" or "chatgpt edu" => MembershipPlan.Edu,
            _ => MembershipPlan.Unknown
        };
    }

    private static AccountOwnership ResolveOwnership(
        MembershipPlan plan,
        AuthClaims claims,
        string accountId,
        AccountReadMetadata? accountRead,
        AccountProfile? cached)
    {
        if (accountRead?.WorkspaceKind == AccountWorkspaceKind.Personal)
        {
            return AccountOwnership.Personal;
        }

        var cachedOwnership = CachedOrganizationForAccount(cached, accountId);
        var exact = FindOrganization(claims.Organizations, accountId);
        if (accountRead?.WorkspaceKind == AccountWorkspaceKind.Workspace)
        {
            return AccountOwnership.Organization(
                FirstNonEmpty(
                    exact?.Id,
                    cachedOwnership?.OrganizationId,
                    accountId),
                FirstKnownOrganizationName(
                    accountRead.WorkspaceName,
                    exact?.Title,
                    cachedOwnership?.DisplayName));
        }

        if (!IsWorkspacePlan(plan))
        {
            return plan == MembershipPlan.Unknown && cachedOwnership is not null
                ? cachedOwnership
                : AccountOwnership.Personal;
        }

        var organizationId = FirstNonEmpty(
            exact?.Id,
            cachedOwnership?.OrganizationId,
            accountId);
        var displayName = FirstKnownOrganizationName(
            accountRead?.WorkspaceName,
            exact?.Title,
            cachedOwnership?.DisplayName);
        return AccountOwnership.Organization(organizationId, displayName);
    }

    private static bool IsWorkspacePlan(MembershipPlan plan) =>
        plan is MembershipPlan.Team or
            MembershipPlan.Business or
            MembershipPlan.Enterprise or
            MembershipPlan.Edu;

    private static OrganizationClaim? FindOrganization(
        IReadOnlyList<OrganizationClaim> organizations,
        string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : organizations
                .Where(organization => string.Equals(
                    organization.Id,
                    id,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(organization =>
                    AccountOwnership.HasKnownOrganizationName(organization.Title))
                .FirstOrDefault();

    private static AccountOwnership? CachedOrganizationForAccount(
        AccountProfile? cached,
        string accountId) =>
        cached is not null &&
        string.Equals(cached.AccountId, accountId, StringComparison.OrdinalIgnoreCase) &&
        cached.Ownership.Kind == AccountOwnershipKind.Organization &&
        AccountOwnership.HasKnownOrganizationName(cached.Ownership.DisplayName)
            ? AccountOwnership.Organization(
                FirstNonEmpty(cached.Ownership.OrganizationId, accountId),
                cached.Ownership.DisplayName)
            : null;

    private static string? FirstKnownOrganizationName(params string?[] candidates) =>
        candidates.FirstOrDefault(AccountOwnership.HasKnownOrganizationName)?.Trim();

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? ToRawPlan(MembershipPlan plan) => plan switch
    {
        MembershipPlan.Free => "free",
        MembershipPlan.Go => "go",
        MembershipPlan.Plus => "plus",
        MembershipPlan.Pro5x => "prolite",
        MembershipPlan.Pro10x => "pro",
        MembershipPlan.Pro25x => "promax",
        MembershipPlan.Pro or MembershipPlan.Pro20x => null,
        MembershipPlan.Team => "business",
        MembershipPlan.Business => "business",
        MembershipPlan.Enterprise => "enterprise",
        MembershipPlan.Edu => "edu",
        _ => null
    };
}
