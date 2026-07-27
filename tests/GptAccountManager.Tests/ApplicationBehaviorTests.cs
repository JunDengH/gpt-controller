using GptAccountManager.Models;
using GptAccountManager.ViewModels;
using GptAccountManager.Views;

namespace GptAccountManager.Tests;

public sealed class ApplicationBehaviorTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public void WindowOnlyMinimizesForCloseToTray(
        bool isExiting,
        bool closeToTray,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindow.ShouldMinimizeToTray(isExiting, closeToTray));
    }

    [Theory]
    [InlineData(SwitchStage.ValidatingCredential, "正在验证 Test 的登录状态…")]
    [InlineData(SwitchStage.StoppingChatGpt, "正在关闭 ChatGPT…")]
    [InlineData(SwitchStage.CheckingBlockers, "正在检查共享认证进程…")]
    [InlineData(SwitchStage.WritingCredential, "正在安全写入账号认证…")]
    [InlineData(SwitchStage.LaunchingChatGpt, "正在启动 ChatGPT…")]
    [InlineData(SwitchStage.Completed, "已切换到 Test")]
    public void SwitchStagesHaveUserFacingProgress(
        SwitchStage stage,
        string expected)
    {
        Assert.Equal(
            expected,
            MainWindowViewModel.DescribeSwitchStage(stage, "Test"));
    }
}
