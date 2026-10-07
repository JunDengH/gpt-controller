namespace GptController.Models;

public enum ConnectionProvider
{
    ChatGpt,
    DeepSeek,
    Qwen
}

public enum MembershipPlan
{
    Unknown = 0,
    Free = 1,
    Plus = 2,
    // Historical serialized values retained for existing local profiles.
    Pro5x = 3,
    Pro20x = 4,
    Team = 5,
    Business = 6,
    Enterprise = 7,
    Edu = 8,
    Go = 9,
    Pro = 10,
    Pro10x = 11,
    Pro25x = 12
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
    ConfigurationConflict,
    LaunchFailed,
    RolledBack,
    Failed
}

public enum QuotaRefreshReason
{
    Manual,
    Automatic,
    PostSwitch
}

public enum SwitchStage
{
    ValidatingCredential,
    StoppingChatGpt,
    CheckingBlockers,
    ConfiguringProvider,
    WritingCredential,
    LaunchingChatGpt,
    Completed
}
