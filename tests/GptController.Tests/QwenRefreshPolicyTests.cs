using GptController.Models;
using GptController.ViewModels;

namespace GptController.Tests;

public sealed class QwenRefreshPolicyTests
{
    private static readonly AccountCardViewModel QwenCard = new(
        new QwenConnection
        {
            Model = "qwen3-coder-plus",
            Region = QwenRegion.Virginia,
            Models = [new ApiModelDescriptor { Id = "qwen3-coder-plus" }]
        });

    [Fact]
    public void AutomaticRefreshSkipsQwenWithoutBlockingManualRefresh()
    {
        Assert.True(MainWindowViewModel.ShouldSkipRefresh(
            QwenCard,
            QuotaRefreshReason.Automatic));
        Assert.False(MainWindowViewModel.ShouldSkipRefresh(
            QwenCard,
            QuotaRefreshReason.Manual));
    }
}
