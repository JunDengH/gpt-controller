namespace GptAccountManager.Models;

public enum MembershipPlan
{
    Unknown,
    Free,
    Plus,
    Pro5x,
    Pro20x,
    Team,
    Business
}

public enum AccountOwnershipKind
{
    Personal,
    Organization
}

public enum QuotaStatus
{
    Unavailable,
    Fresh,
    Stale,
    AuthenticationRequired
}

public enum SwitchStatus
{
    Success,
    Cancelled,
    ProcessBlocked,
    AuthenticationInvalid,
    LaunchFailed,
    RolledBack,
    Failed
}
