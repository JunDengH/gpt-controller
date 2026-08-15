using System.Text.Json.Serialization;

namespace GptController.Models;

public sealed record AccountProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Nickname { get; init; }
    public required string Email { get; init; }
    public required string AccountId { get; init; }
    public bool IsActive { get; init; }
    public MembershipPlan MembershipPlan { get; init; } = MembershipPlan.Unknown;
    public AccountOwnership Ownership { get; init; } = AccountOwnership.Personal;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastVerifiedAt { get; init; }
    public QuotaSnapshot? Quota { get; init; }

    [JsonIgnore]
    public string PlanDisplayName => MembershipPlan switch
    {
        MembershipPlan.Free => "Free",
        MembershipPlan.Plus => "Plus",
        MembershipPlan.Pro5x => "Pro 5x",
        MembershipPlan.Pro20x => "Pro 20x",
        MembershipPlan.Team => "Team",
        MembershipPlan.Business => "Business",
        MembershipPlan.Enterprise => "Enterprise",
        MembershipPlan.Edu => "Edu",
        _ => "未知会员"
    };

    [JsonIgnore]
    public string OwnershipDisplayName =>
        IsWorkspacePlan(MembershipPlan) &&
        Ownership.Kind == AccountOwnershipKind.Organization
            ? AccountOwnership.HasKnownOrganizationName(Ownership.DisplayName)
                ? Ownership.DisplayName!.Trim()
                : AccountOwnership.UnknownOrganizationDisplayName
            : string.Empty;

    private static bool IsWorkspacePlan(MembershipPlan plan) =>
        plan is MembershipPlan.Team or
            MembershipPlan.Business or
            MembershipPlan.Enterprise or
            MembershipPlan.Edu;
}
