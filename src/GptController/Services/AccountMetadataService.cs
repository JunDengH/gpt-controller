using GptController.Models;

namespace GptController.Services;

public sealed record AccountReadMetadata(
    string? Email,
    string? PlanType,
    string? AccountId = null,
    AccountWorkspaceKind? WorkspaceKind = null,
    string? WorkspaceName = null);

public enum AccountWorkspaceKind
{
    Personal,
    Workspace
}

public sealed record ResolvedAccountMetadata(
    string Email,
    string AccountId,
    MembershipPlan MembershipPlan,
    AccountOwnership Ownership);

public sealed class AccountMetadataService
{
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

        var plan = NormalizePlan(
            FirstNonEmpty(
                quotaPlanType,
                accountRead?.PlanType,
                claims.PlanType,
                cachedForAccount is null
                    ? null
                    : ToRawPlan(cachedForAccount.MembershipPlan)));

        var ownership = ResolveOwnership(
            plan,
            claims,
            accountId,
            accountRead,
            cachedForAccount);
        return new ResolvedAccountMetadata(email, accountId, plan, ownership);
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
            "plus" or "chatgpt plus" => MembershipPlan.Plus,
            "prolite" or "pro lite" or "pro 5x" => MembershipPlan.Pro5x,
            "pro" or "pro 20x" => MembershipPlan.Pro20x,
            "team" => MembershipPlan.Team,
            "business" or
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
            "education" or "edu" or "chatgpt edu" => MembershipPlan.Edu,
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
        if (accountRead?.WorkspaceKind == AccountWorkspaceKind.Workspace)
        {
            return AccountOwnership.Organization(
                accountId,
                FirstKnownOrganizationName(
                    accountRead.WorkspaceName,
                    FindOrganization(claims.Organizations, accountId)?.Title,
                    cachedOwnership?.DisplayName));
        }

        if (!IsWorkspacePlan(plan))
        {
            return plan == MembershipPlan.Unknown && cachedOwnership is not null
                ? cachedOwnership
                : AccountOwnership.Personal;
        }

        var exact = FindOrganization(claims.Organizations, accountId) ??
                    FindOrganization(claims.Organizations, claims.OrganizationId);
        var organizationId = FirstNonEmpty(
            exact?.Id,
            claims.OrganizationId,
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
        cached.Ownership.Kind == AccountOwnershipKind.Organization
            ? AccountOwnership.Organization(
                cached.Ownership.OrganizationId,
                cached.Ownership.DisplayName)
            : null;

    private static string? FirstKnownOrganizationName(params string?[] candidates) =>
        candidates.FirstOrDefault(AccountOwnership.HasKnownOrganizationName)?.Trim();

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? ToRawPlan(MembershipPlan plan) => plan switch
    {
        MembershipPlan.Free => "free",
        MembershipPlan.Plus => "plus",
        MembershipPlan.Pro5x => "prolite",
        MembershipPlan.Pro20x => "pro",
        MembershipPlan.Team => "team",
        MembershipPlan.Business => "business",
        MembershipPlan.Enterprise => "enterprise",
        MembershipPlan.Edu => "edu",
        _ => null
    };
}
