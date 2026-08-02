using GptController.Models;
using GptController.Mvvm;

namespace GptController.ViewModels;

public sealed class AccountCardViewModel : ObservableObject
{
    public static readonly Guid DeepSeekCardId =
        new("DEE05EE4-0000-4000-8000-000000000004");

    private AccountProfile? _profile;
    private DeepSeekConnection? _deepSeek;
    private bool _isRefreshing;

    public AccountCardViewModel(AccountProfile profile)
    {
        _profile = profile;
    }

    public AccountCardViewModel(DeepSeekConnection connection)
    {
        _deepSeek = connection;
    }

    public ConnectionProvider Provider => IsDeepSeek
        ? ConnectionProvider.DeepSeek
        : ConnectionProvider.ChatGpt;
    public bool IsDeepSeek => _deepSeek is not null;
    public AccountProfile Profile => _profile ?? throw new InvalidOperationException(
        "DeepSeek 连接不包含 ChatGPT 账号档案。");
    public AccountProfile? ChatGptProfile => _profile;
    public DeepSeekConnection? DeepSeekProfile => _deepSeek;
    public Guid Id => IsDeepSeek ? DeepSeekCardId : Profile.Id;
    public string Nickname => IsDeepSeek ? _deepSeek!.Nickname : Profile.Nickname;
    public string Email => IsDeepSeek
        ? $"API Key {_deepSeek!.MaskedApiKey}"
        : Profile.Email;
    public bool IsActive => IsDeepSeek ? _deepSeek!.IsActive : Profile.IsActive;
    public bool CanDelete => !IsActive;
    public string ProviderDisplayName => IsDeepSeek ? "DeepSeek API" : "ChatGPT OAuth";
    public bool IsOrganization =>
        !IsDeepSeek && Profile.Ownership.Kind == AccountOwnershipKind.Organization;
    public string PlanDisplayName => IsDeepSeek
        ? $"{_deepSeek!.Model} · Responses API"
        : Profile.PlanDisplayName;
    public string OwnershipDisplayName => IsDeepSeek
        ? FormatDeepSeekAvailability(_deepSeek!)
        : Profile.OwnershipDisplayName;
    public string MetricsTitle => IsDeepSeek ? "API 余额" : "使用额度";
    public string PrimaryMetricLabel => IsDeepSeek ? "CNY" : "5 小时";
    public string SecondaryMetricLabel => IsDeepSeek ? "USD" : "每周";
    public string UpdatedLabel => IsDeepSeek ? "最近验证" : "数据更新";
    public string RefreshToolTip => IsDeepSeek ? "刷新 API 余额" : "刷新额度与会员信息";
    public string EditToolTip => IsDeepSeek ? "编辑 DeepSeek 连接" : "编辑昵称";

    public double FiveHourRemainingValue =>
        IsDeepSeek ? 0 : Math.Clamp(Profile.Quota?.FiveHourRemainingPercent ?? 0, 0, 100);

    public string FiveHourRemainingText =>
        IsDeepSeek
            ? FormatMoney(_deepSeek!.CnyBalance, "¥")
            : FormatRemaining(Profile.Quota?.FiveHourRemainingPercent);

    public string FiveHourResetValueText =>
        IsDeepSeek
            ? "人民币总余额"
            : FormatReset(Profile.Quota?.FiveHourResetsAt);

    public double WeeklyRemainingValue =>
        IsDeepSeek ? 0 : Math.Clamp(Profile.Quota?.RemainingPercent ?? 0, 0, 100);

    public string WeeklyRemainingText =>
        IsDeepSeek
            ? FormatMoney(_deepSeek!.UsdBalance, "$")
            : FormatRemaining(Profile.Quota?.RemainingPercent);

    public string WeeklyResetValueText =>
        IsDeepSeek
            ? "美元总余额"
            : FormatReset(Profile.Quota?.ResetsAt);

    public string PrimaryMetricDetailText => IsDeepSeek
        ? FiveHourResetValueText
        : $"重置 {FiveHourResetValueText}";

    public string SecondaryMetricDetailText => IsDeepSeek
        ? WeeklyResetValueText
        : $"重置 {WeeklyResetValueText}";

    public double QuotaRemainingValue => WeeklyRemainingValue;

    public string QuotaRemainingText => WeeklyRemainingText;

    public string QuotaResetText =>
        IsDeepSeek
            ? SecondaryMetricDetailText
            : Profile.Quota?.ResetsAt is { } reset
            ? $"重置：{reset.ToLocalTime():M月d日 HH:mm}"
            : "重置时间暂不可用";

    public string QuotaResetValueText => WeeklyResetValueText;

    public string QuotaUpdatedText =>
        IsDeepSeek
            ? _deepSeek!.LastValidatedAt is { } validated
                ? $"验证：{validated.ToLocalTime():M月d日 HH:mm}"
                : "尚未验证 API"
            : Profile.Quota is { } quota
            ? $"更新：{quota.FetchedAt.ToLocalTime():M月d日 HH:mm}"
            : "尚未获取额度";

    public string QuotaUpdatedValueText =>
        IsDeepSeek
            ? _deepSeek!.LastValidatedAt is { } validated
                ? $"{validated.ToLocalTime():yyyy-MM-dd HH:mm}"
                : "尚未验证"
            : Profile.Quota is { } quota
            ? $"{quota.FetchedAt.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "尚未获取";

    public string QuotaStatusText => IsDeepSeek
        ? _deepSeek!.Status switch
        {
            DeepSeekConnectionStatus.Available => "API 可用",
            DeepSeekConnectionStatus.AuthenticationRequired => "Key 无效",
            DeepSeekConnectionStatus.PaymentRequired => "余额不足",
            DeepSeekConnectionStatus.RateLimited => "请求受限",
            DeepSeekConnectionStatus.Stale => "显示上次数据",
            DeepSeekConnectionStatus.Unavailable => "暂不可用",
            _ => "尚未验证"
        }
        : Profile.Quota switch
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

    public bool HasFreshQuota => IsDeepSeek
        ? _deepSeek!.Status == DeepSeekConnectionStatus.Available
        : Profile.Quota?.Status == QuotaStatus.Fresh;

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
        RaiseConnectionPropertiesChanged();
    }

    public void UpdateDeepSeek(DeepSeekConnection connection)
    {
        if (!IsDeepSeek || !string.Equals(
                connection.Id,
                DeepSeekConnection.FixedId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("不能用其他连接更新当前卡片。");
        }

        _deepSeek = connection;
        RaiseConnectionPropertiesChanged();
    }

    private void RaiseConnectionPropertiesChanged()
    {
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(ChatGptProfile));
        OnPropertyChanged(nameof(DeepSeekProfile));
        OnPropertyChanged(nameof(Provider));
        OnPropertyChanged(nameof(IsDeepSeek));
        OnPropertyChanged(nameof(Nickname));
        OnPropertyChanged(nameof(Email));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(ProviderDisplayName));
        OnPropertyChanged(nameof(IsOrganization));
        OnPropertyChanged(nameof(PlanDisplayName));
        OnPropertyChanged(nameof(OwnershipDisplayName));
        OnPropertyChanged(nameof(MetricsTitle));
        OnPropertyChanged(nameof(PrimaryMetricLabel));
        OnPropertyChanged(nameof(SecondaryMetricLabel));
        OnPropertyChanged(nameof(UpdatedLabel));
        OnPropertyChanged(nameof(RefreshToolTip));
        OnPropertyChanged(nameof(EditToolTip));
        OnPropertyChanged(nameof(FiveHourRemainingValue));
        OnPropertyChanged(nameof(FiveHourRemainingText));
        OnPropertyChanged(nameof(FiveHourResetValueText));
        OnPropertyChanged(nameof(PrimaryMetricDetailText));
        OnPropertyChanged(nameof(WeeklyRemainingValue));
        OnPropertyChanged(nameof(WeeklyRemainingText));
        OnPropertyChanged(nameof(WeeklyResetValueText));
        OnPropertyChanged(nameof(SecondaryMetricDetailText));
        OnPropertyChanged(nameof(QuotaRemainingValue));
        OnPropertyChanged(nameof(QuotaRemainingText));
        OnPropertyChanged(nameof(QuotaResetText));
        OnPropertyChanged(nameof(QuotaResetValueText));
        OnPropertyChanged(nameof(QuotaUpdatedText));
        OnPropertyChanged(nameof(QuotaUpdatedValueText));
        OnPropertyChanged(nameof(QuotaStatusText));
        OnPropertyChanged(nameof(HasFreshQuota));
    }

    private static string FormatDeepSeekAvailability(DeepSeekConnection connection) =>
        connection.IsAvailable switch
        {
            true => "官方 API · 余额可用",
            false => "官方 API · 当前不可用",
            null => "官方 API · 等待验证"
        };

    private static string FormatMoney(decimal? amount, string symbol) =>
        amount is { } value ? $"{symbol}{value:N2}" : "—";

    private static string FormatRemaining(double? remaining) =>
        remaining is { } value
            ? $"{Math.Round(value):0}%"
            : "—";

    private static string FormatReset(DateTimeOffset? reset) =>
        reset is { } value
            ? $"{value.ToLocalTime():M月d日 HH:mm}"
            : "暂不可用";
}
