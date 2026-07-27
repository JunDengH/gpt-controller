using GptAccountManager.Models;
using GptAccountManager.Mvvm;

namespace GptAccountManager.ViewModels;

public sealed class AccountCardViewModel : ObservableObject
{
    private AccountProfile _profile;
    private bool _isRefreshing;

    public AccountCardViewModel(AccountProfile profile)
    {
        _profile = profile;
    }

    public AccountProfile Profile => _profile;
    public Guid Id => Profile.Id;
    public string Nickname => Profile.Nickname;
    public string Email => Profile.Email;
    public bool IsActive => Profile.IsActive;
    public bool CanDelete => !Profile.IsActive;
    public bool IsOrganization =>
        Profile.Ownership.Kind == AccountOwnershipKind.Organization;
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

    public string QuotaResetValueText =>
        Profile.Quota?.ResetsAt is { } reset
            ? $"{reset.ToLocalTime():M月d日 HH:mm}"
            : "暂不可用";

    public string QuotaUpdatedText =>
        Profile.Quota is { } quota
            ? $"更新：{quota.FetchedAt.ToLocalTime():M月d日 HH:mm}"
            : "尚未获取额度";

    public string QuotaUpdatedValueText =>
        Profile.Quota is { } quota
            ? $"{quota.FetchedAt.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "尚未获取";

    public string QuotaStatusText => Profile.Quota switch
    {
        { Status: QuotaStatus.Fresh } => "数据最新",
        { Status: QuotaStatus.Stale, ErrorCode: "active_refresh_deferred" } =>
            "等待 ChatGPT 更新登录状态",
        { Status: QuotaStatus.Stale } => "显示上次数据",
        {
            Status: QuotaStatus.AuthenticationRequired,
            ErrorCode: "invalid_auth" or "confirmed_unauthorized"
        } => "需要重新登录",
        { Status: QuotaStatus.AuthenticationRequired } => "等待重新验证",
        _ => "额度不可用"
    };

    public bool HasFreshQuota => Profile.Quota?.Status == QuotaStatus.Fresh;

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set => SetProperty(ref _isRefreshing, value);
    }

    public void UpdateProfile(AccountProfile profile)
    {
        if (profile.Id != Id)
        {
            throw new InvalidOperationException("不能用其他账号更新当前卡片。");
        }

        _profile = profile;
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(Nickname));
        OnPropertyChanged(nameof(Email));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(IsOrganization));
        OnPropertyChanged(nameof(PlanDisplayName));
        OnPropertyChanged(nameof(OwnershipDisplayName));
        OnPropertyChanged(nameof(QuotaRemainingValue));
        OnPropertyChanged(nameof(QuotaRemainingText));
        OnPropertyChanged(nameof(QuotaResetText));
        OnPropertyChanged(nameof(QuotaResetValueText));
        OnPropertyChanged(nameof(QuotaUpdatedText));
        OnPropertyChanged(nameof(QuotaUpdatedValueText));
        OnPropertyChanged(nameof(QuotaStatusText));
        OnPropertyChanged(nameof(HasFreshQuota));
    }
}
