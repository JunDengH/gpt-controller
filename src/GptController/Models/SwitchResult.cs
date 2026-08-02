namespace GptController.Models;

public sealed record SwitchResult(SwitchStatus Status, string Message)
{
    public bool IsSuccess => Status == SwitchStatus.Success;

    public static SwitchResult Success(string message = "账号切换成功。") =>
        new(SwitchStatus.Success, message);

    public static SwitchResult Failure(SwitchStatus status, string message) =>
        new(status, message);
}
