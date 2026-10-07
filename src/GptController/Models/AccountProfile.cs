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
    public string? RawPlanType { get; init; }
    public bool AccountMetadataVerified { get; init; }
    public string? AccountMetadataErrorCode { get; init; }
    public AccountOwnership Ownership { get; init; } = AccountOwnership.Personal;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastVerifiedAt { get; init; }
    public QuotaSnapshot? Quota { get; init; }

    [JsonIgnore]
    public string PlanDisplayName => MembershipPlan switch
    {
        MembershipPlan.Free => "Free",
        MembershipPlan.Go => "Go",
        MembershipPlan.Plus => "Plus",
        MembershipPlan.Pro5x => "Pro 5x",
        MembershipPlan.Pro10x => "Pro 10x",
        MembershipPlan.Pro25x => "Pro 25x",
        MembershipPlan.Pro20x => "Pro（旧档位，待刷新）",
        MembershipPlan.Pro => "Pro（档位未返回）",
        MembershipPlan.Team => "Business",
        MembershipPlan.Business => "Business",
        MembershipPlan.Enterprise => "Enterprise",
        MembershipPlan.Edu => "Edu",
        _ => string.IsNullOrWhiteSpace(RawPlanType) ? "未知会员" : RawPlanType
    };

    [JsonIgnore]
    public string OwnershipDisplayName =>
        (IsWorkspacePlan(MembershipPlan) || AccountMetadataVerified) &&
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
