using System.Text.Json.Serialization;

namespace GptAccountManager.Models;

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
        _ => "未知会员"
    };

    [JsonIgnore]
    public string OwnershipDisplayName =>
        Ownership.Kind == AccountOwnershipKind.Personal
            ? "个人账号"
            : string.IsNullOrWhiteSpace(Ownership.DisplayName)
                ? "企业账号（名称未知）"
                : Ownership.DisplayName!;
}
