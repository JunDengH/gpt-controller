using GptController.Models;

namespace GptController.Services;

public sealed record AccountReadMetadata(
    string? Email,
    string? PlanType,
    string? AccountId = null);

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
        var email =
            FirstNonEmpty(accountRead?.Email, claims.Email, cached?.Email)
            ?? "未知邮箱";

        var plan = NormalizePlan(
            FirstNonEmpty(
                quotaPlanType,
                accountRead?.PlanType,
                claims.PlanType,
                cached is null ? null : ToRawPlan(cached.MembershipPlan)));

        var ownership = ResolveOwnership(plan, claims, accountId);
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
            "business" => MembershipPlan.Business,
            _ => MembershipPlan.Unknown
        };
    }

    private static AccountOwnership ResolveOwnership(
        MembershipPlan plan,
        AuthClaims claims,
        string accountId)
    {
        if (plan is not (MembershipPlan.Team or MembershipPlan.Business))
        {
            return AccountOwnership.Personal;
        }

        var candidateIds = new[] { claims.OrganizationId, accountId }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var exact = claims.Organizations.FirstOrDefault(
            organization =>
                !string.IsNullOrWhiteSpace(organization.Id) &&
                candidateIds.Contains(organization.Id));
        if (exact is not null)
        {
            return AccountOwnership.Organization(exact.Id, exact.Title);
        }

        var titledOrganizations = claims.Organizations
            .Where(organization => !string.IsNullOrWhiteSpace(organization.Title))
            .ToList();
        if (titledOrganizations.Count == 1)
        {
            var only = titledOrganizations[0];
            return AccountOwnership.Organization(only.Id ?? claims.OrganizationId, only.Title);
        }

        return AccountOwnership.Organization(claims.OrganizationId, null);
    }

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
        _ => null
    };
}
