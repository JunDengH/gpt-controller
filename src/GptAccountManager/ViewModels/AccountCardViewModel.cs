using GptAccountManager.Models;

namespace GptAccountManager.ViewModels;

public sealed class AccountCardViewModel
{
    public AccountCardViewModel(AccountProfile profile)
    {
        Profile = profile;
    }

    public AccountProfile Profile { get; }
    public Guid Id => Profile.Id;
    public string Nickname => Profile.Nickname;
    public string Email => Profile.Email;
    public bool IsActive => Profile.IsActive;
    public bool CanDelete => !Profile.IsActive;
    public string PlanDisplayName => Profile.PlanDisplayName;
    public string OwnershipDisplayName => Profile.OwnershipDisplayName;

    public double QuotaRemainingValue =>
        Math.Clamp(Profile.Quota?.RemainingPercent ?? 0, 0, 100);

    public string QuotaRemainingText =>
        Profile.Quota?.RemainingPercent is { } remaining
            ? $"{Math.Round(remaining):0}%"
            : "—";

    public string QuotaResetText =>
        Profile.Quota?.ResetsAt is { } reset
            ? $"重置：{reset.ToLocalTime():M月d日 HH:mm}"
            : "重置时间暂不可用";

    public string QuotaUpdatedText =>
        Profile.Quota is { } quota
            ? $"更新：{quota.FetchedAt.ToLocalTime():M月d日 HH:mm}"
            : "尚未获取额度";

    public string QuotaStatusText => Profile.Quota?.Status switch
    {
        QuotaStatus.Fresh => "数据最新",
        QuotaStatus.Stale => "显示上次数据",
        QuotaStatus.AuthenticationRequired => "需要重新登录",
        _ => "额度不可用"
    };

    public bool HasFreshQuota => Profile.Quota?.Status == QuotaStatus.Fresh;

    public string TrayDisplay
    {
        get
        {
            var quota = Profile.Quota?.RemainingPercent is { } remaining
                ? $"周剩余 {Math.Round(remaining):0}%"
                : "周额度 —";
            return $"{Nickname} · {PlanDisplayName} · {OwnershipDisplayName} · {quota}";
        }
    }
}
