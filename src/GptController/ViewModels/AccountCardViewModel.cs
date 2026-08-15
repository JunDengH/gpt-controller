using GptController.Models;
using GptController.Mvvm;

namespace GptController.ViewModels;

public sealed class AccountCardViewModel : ObservableObject
{
    public static readonly Guid DeepSeekCardId =
        new("DEE05EE4-0000-4000-8000-000000000004");
    public static readonly Guid QwenCardId =
        new("0A7E0000-0000-4000-8000-000000000001");

    private AccountProfile? _profile;
    private DeepSeekConnection? _deepSeek;
    private QwenConnection? _qwen;
    private bool _isRefreshing;

    public AccountCardViewModel(AccountProfile profile)
    {
        _profile = profile;
    }

    public AccountCardViewModel(DeepSeekConnection connection)
    {
        _deepSeek = connection;
    }

    public AccountCardViewModel(QwenConnection connection)
    {
        _qwen = connection;
    }

    public ConnectionProvider Provider => IsDeepSeek
        ? ConnectionProvider.DeepSeek
        : IsQwen
            ? ConnectionProvider.Qwen
            : ConnectionProvider.ChatGpt;
    public ConnectionCardKind CardKind => IsApiProvider
        ? ConnectionCardKind.ApiProvider
        : ConnectionCardKind.OAuthAccount;
    public ApiConnectionCardPresentation? ApiPresentation => IsDeepSeek
        ? CreateDeepSeekPresentation(_deepSeek!)
        : IsQwen
            ? CreateQwenPresentation(_qwen!)
            : null;
    public bool IsApiProvider => _deepSeek is not null || _qwen is not null;
    public bool IsDeepSeek => _deepSeek is not null;
    public bool IsQwen => _qwen is not null;
    public AccountProfile Profile => _profile ?? throw new InvalidOperationException(
        "API 连接不包含 ChatGPT 账号档案。");
    public AccountProfile? ChatGptProfile => _profile;
    public DeepSeekConnection? DeepSeekProfile => _deepSeek;
    public QwenConnection? QwenProfile => _qwen;
    public Guid Id => IsDeepSeek ? DeepSeekCardId : IsQwen ? QwenCardId : Profile.Id;
    public string Nickname => IsDeepSeek
        ? _deepSeek!.Nickname
        : IsQwen
            ? "千问 API"
            : Profile.Nickname;
    public string Email => IsApiProvider ? string.Empty : Profile.Email;
    public bool IsActive => IsDeepSeek
        ? _deepSeek!.IsActive
        : IsQwen
            ? _qwen!.IsActive
            : Profile.IsActive;
    public bool CanDelete => IsApiProvider || !IsActive;
    public string ProviderDisplayName => IsDeepSeek
        ? "DeepSeek API"
        : IsQwen
            ? "千问 API"
            : "ChatGPT OAuth";
    public bool IsOrganization =>
        !IsApiProvider &&
        IsOrganizationPlan(Profile.MembershipPlan) &&
        Profile.Ownership.Kind == AccountOwnershipKind.Organization;
    public string PlanDisplayName => IsDeepSeek
        ? $"{_deepSeek!.Model} · Responses API"
        : IsQwen
            ? $"{_qwen!.Model} · Responses API"
            : Profile.PlanDisplayName;
    public string ApiModelDisplayName => IsDeepSeek
        ? DeepSeekDefaults.GetModelDisplayName(_deepSeek!.Model)
        : IsQwen
            ? _qwen!.Model
            : string.Empty;
    public string ApiModelDescription => IsDeepSeek
        ? _deepSeek!.Model == DeepSeekDefaults.ProModel
            ? "更强推理与复杂编码"
            : "高并发、快速且经济"
        : IsQwen
            ? $"{QwenRegions.GetDisplayName(_qwen!.Region)} · 动态模型列表"
            : string.Empty;
    public bool IsFlashModel =>
        IsDeepSeek && _deepSeek!.Model == DeepSeekDefaults.FlashModel;
    public bool IsProModel =>
        IsDeepSeek && _deepSeek!.Model == DeepSeekDefaults.ProModel;
    public string OwnershipDisplayName => IsDeepSeek
        ? FormatDeepSeekAvailability(_deepSeek!)
        : IsQwen
            ? "阿里云百炼 · Responses API"
            : CompanyDisplayName;
    public string CompanyDisplayName =>
        IsOrganization
            ? FormatOrganizationDisplayName(Profile.Ownership.DisplayName)
            : string.Empty;
    public string AccountIdentityDisplayName => IsApiProvider
        ? string.Empty
        : string.Join(
            " · ",
            new[] { Email, CompanyDisplayName }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
    public string AccountIdentitySeparator =>
        !string.IsNullOrWhiteSpace(Email) &&
        !string.IsNullOrWhiteSpace(CompanyDisplayName)
            ? " · "
            : string.Empty;
    public string MetricsTitle => IsDeepSeek ? "API 余额" : IsQwen ? "模型状态" : "使用额度";
    public string PrimaryMetricLabel => IsDeepSeek ? "CNY" : IsQwen ? "可用模型" : "5 小时";
    public string SecondaryMetricLabel => IsDeepSeek ? "USD" : IsQwen ? "地域" : "每周";
    public string UpdatedLabel => IsApiProvider ? "最近验证" : "数据更新";
    public string RefreshToolTip => IsDeepSeek
        ? "刷新 API 余额"
        : IsQwen
            ? "重新获取千问模型列表"
            : "刷新额度与会员信息";

    public double FiveHourRemainingValue =>
        IsApiProvider ? 0 : Math.Clamp(Profile.Quota?.FiveHourRemainingPercent ?? 0, 0, 100);

    public string FiveHourRemainingText =>
        IsDeepSeek
            ? FormatMoney(_deepSeek!.CnyBalance, "¥")
            : IsQwen
                ? _qwen!.Models.Count.ToString()
                : FormatRemaining(Profile.Quota?.FiveHourRemainingPercent);

    public string FiveHourResetValueText =>
        IsDeepSeek
            ? "人民币总余额"
            : IsQwen
                ? "动态获取"
                : FormatReset(Profile.Quota?.FiveHourResetsAt);

    public double WeeklyRemainingValue =>
        IsApiProvider ? 0 : Math.Clamp(Profile.Quota?.RemainingPercent ?? 0, 0, 100);

    public string WeeklyRemainingText =>
        IsDeepSeek
            ? FormatMoney(_deepSeek!.UsdBalance, "$")
            : IsQwen
                ? QwenRegions.GetDisplayName(_qwen!.Region)
                : FormatRemaining(Profile.Quota?.RemainingPercent);

    public string WeeklyResetValueText =>
        IsDeepSeek
            ? "美元总余额"
            : IsQwen
                ? "模型地域"
                : FormatReset(Profile.Quota?.ResetsAt);

    public string PrimaryMetricDetailText => IsApiProvider
        ? FiveHourResetValueText
        : $"重置 {FiveHourResetValueText}";

    public string SecondaryMetricDetailText => IsApiProvider
        ? WeeklyResetValueText
        : $"重置 {WeeklyResetValueText}";

    public double QuotaRemainingValue => WeeklyRemainingValue;

    public string QuotaRemainingText => WeeklyRemainingText;

    public string QuotaResetText =>
        IsDeepSeek
            ? SecondaryMetricDetailText
            : IsQwen
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
            : IsQwen
            ? _qwen!.LastValidatedAt is { } qwenValidated
                ? $"验证：{qwenValidated.ToLocalTime():M月d日 HH:mm}"
                : "尚未验证 API"
            : Profile.Quota is { } quota
            ? $"更新：{quota.FetchedAt.ToLocalTime():M月d日 HH:mm}"
            : "尚未获取额度";

    public string QuotaUpdatedValueText =>
        IsDeepSeek
            ? _deepSeek!.LastValidatedAt is { } validated
                ? $"{validated.ToLocalTime():yyyy-MM-dd HH:mm}"
                : "尚未验证"
            : IsQwen
            ? _qwen!.LastValidatedAt is { } qwenValidated
                ? $"{qwenValidated.ToLocalTime():yyyy-MM-dd HH:mm}"
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
        : IsQwen
        ? FormatApiStatus(_qwen!.Status)
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
        : IsQwen
        ? _qwen!.Status == ApiConnectionStatus.Available
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

    public void UpdateQwen(QwenConnection connection)
    {
        if (!IsQwen || !string.Equals(
                connection.Id,
                QwenConnection.FixedId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("不能用其他连接更新当前卡片。");
        }

        _qwen = connection;
        RaiseConnectionPropertiesChanged();
    }

    private void RaiseConnectionPropertiesChanged()
    {
        OnPropertyChanged(nameof(CardKind));
        OnPropertyChanged(nameof(ApiPresentation));
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(ChatGptProfile));
        OnPropertyChanged(nameof(DeepSeekProfile));
        OnPropertyChanged(nameof(QwenProfile));
        OnPropertyChanged(nameof(Provider));
        OnPropertyChanged(nameof(IsApiProvider));
        OnPropertyChanged(nameof(IsDeepSeek));
        OnPropertyChanged(nameof(IsQwen));
        OnPropertyChanged(nameof(Nickname));
        OnPropertyChanged(nameof(Email));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(ProviderDisplayName));
        OnPropertyChanged(nameof(IsOrganization));
        OnPropertyChanged(nameof(PlanDisplayName));
        OnPropertyChanged(nameof(ApiModelDisplayName));
        OnPropertyChanged(nameof(ApiModelDescription));
        OnPropertyChanged(nameof(IsFlashModel));
        OnPropertyChanged(nameof(IsProModel));
        OnPropertyChanged(nameof(OwnershipDisplayName));
        OnPropertyChanged(nameof(CompanyDisplayName));
        OnPropertyChanged(nameof(AccountIdentityDisplayName));
        OnPropertyChanged(nameof(AccountIdentitySeparator));
        OnPropertyChanged(nameof(MetricsTitle));
        OnPropertyChanged(nameof(PrimaryMetricLabel));
        OnPropertyChanged(nameof(SecondaryMetricLabel));
        OnPropertyChanged(nameof(UpdatedLabel));
        OnPropertyChanged(nameof(RefreshToolTip));
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

    private static bool IsOrganizationPlan(MembershipPlan plan) =>
        plan is MembershipPlan.Team or
            MembershipPlan.Business or
            MembershipPlan.Enterprise or
            MembershipPlan.Edu;

    private static string FormatOrganizationDisplayName(string? displayName) =>
        AccountOwnership.HasKnownOrganizationName(displayName)
            ? displayName!.Trim()
            : AccountOwnership.UnknownOrganizationDisplayName;

    private static ApiConnectionCardPresentation CreateDeepSeekPresentation(
        DeepSeekConnection connection)
    {
        var health = MapDeepSeekHealth(connection.Status);
        var primaryMetric = new ApiMetricPresentation(
            "CNY",
            FormatMoney(connection.CnyBalance, "¥"),
            "人民币余额");

        return new ApiConnectionCardPresentation
        {
            ProviderName = "DeepSeek API",
            Model = connection.Model,
            ProtocolDisplayName = "Responses API",
            EndpointHost = new Uri(DeepSeekDefaults.BaseUrl).Host,
            Health = health,
            StatusText = FormatDeepSeekStatus(connection.Status),
            LastValidatedAt = connection.LastValidatedAt,
            LastValidatedText = connection.LastValidatedAt is { } validated
                ? $"{validated.ToLocalTime():yyyy-MM-dd HH:mm}"
                : "尚未验证",
            PrimaryMetric = primaryMetric,
            Metrics = [primaryMetric]
        };
    }

    private static ApiConnectionCardPresentation CreateQwenPresentation(
        QwenConnection connection)
    {
        var primaryMetric = new ApiMetricPresentation(
            $"{QwenRegions.GetShortDisplayName(connection.Region)} · 模型",
            connection.Models.Count.ToString(),
            connection.IsModelCacheStale ? "上次缓存" : "动态获取");
        return new ApiConnectionCardPresentation
        {
            ProviderName = "千问 API",
            Model = connection.Model,
            ProtocolDisplayName = "Responses API",
            EndpointHost = QwenRegions.Get(connection.Region)
                .CreateBaseUrl(connection.WorkspaceId)
                .Host,
            Health = MapApiHealth(connection.Status),
            StatusText = FormatApiStatus(connection.Status),
            LastValidatedAt = connection.LastValidatedAt,
            LastValidatedText = connection.LastValidatedAt is { } validated
                ? $"{validated.ToLocalTime():yyyy-MM-dd HH:mm}"
                : "尚未验证",
            PrimaryMetric = primaryMetric,
            Metrics = [primaryMetric]
        };
    }

    private static ConnectionDisplayHealth MapDeepSeekHealth(
        DeepSeekConnectionStatus status) => status switch
        {
            DeepSeekConnectionStatus.Available => ConnectionDisplayHealth.Healthy,
            DeepSeekConnectionStatus.AuthenticationRequired =>
                ConnectionDisplayHealth.AuthenticationRequired,
            DeepSeekConnectionStatus.PaymentRequired =>
                ConnectionDisplayHealth.PaymentRequired,
            DeepSeekConnectionStatus.RateLimited =>
                ConnectionDisplayHealth.RateLimited,
            DeepSeekConnectionStatus.Stale => ConnectionDisplayHealth.Stale,
            DeepSeekConnectionStatus.Unavailable => ConnectionDisplayHealth.Unavailable,
            _ => ConnectionDisplayHealth.Unknown
        };

    private static string FormatDeepSeekStatus(DeepSeekConnectionStatus status) =>
        status switch
        {
            DeepSeekConnectionStatus.Available => "API 可用",
            DeepSeekConnectionStatus.AuthenticationRequired => "认证无效",
            DeepSeekConnectionStatus.PaymentRequired => "余额不足",
            DeepSeekConnectionStatus.RateLimited => "请求受限",
            DeepSeekConnectionStatus.Stale => "显示上次数据",
            DeepSeekConnectionStatus.Unavailable => "暂不可用",
            _ => "尚未验证"
        };

    private static ConnectionDisplayHealth MapApiHealth(ApiConnectionStatus status) =>
        status switch
        {
            ApiConnectionStatus.Available => ConnectionDisplayHealth.Healthy,
            ApiConnectionStatus.AuthenticationRequired =>
                ConnectionDisplayHealth.AuthenticationRequired,
            ApiConnectionStatus.PaymentRequired => ConnectionDisplayHealth.PaymentRequired,
            ApiConnectionStatus.RateLimited => ConnectionDisplayHealth.RateLimited,
            ApiConnectionStatus.Stale => ConnectionDisplayHealth.Stale,
            ApiConnectionStatus.Unavailable => ConnectionDisplayHealth.Unavailable,
            _ => ConnectionDisplayHealth.Unknown
        };

    private static string FormatApiStatus(ApiConnectionStatus status) => status switch
    {
        ApiConnectionStatus.Available => "API 可用",
        ApiConnectionStatus.AuthenticationRequired => "Key 无效",
        ApiConnectionStatus.PaymentRequired => "余额不足",
        ApiConnectionStatus.RateLimited => "请求受限",
        ApiConnectionStatus.Stale => "显示上次模型列表",
        ApiConnectionStatus.Unavailable => "暂不可用",
        _ => "尚未验证"
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
