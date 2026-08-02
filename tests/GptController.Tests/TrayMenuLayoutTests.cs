using System.Drawing;
using GptController.Services;

namespace GptController.Tests;

public sealed class TrayMenuLayoutTests
{
    [Theory]
    [InlineData(96, 12)]
    [InlineData(120, 15)]
    [InlineData(144, 18)]
    [InlineData(192, 24)]
    public void ActionRowsFillPopupWidthAndTextRemainsLeftAligned(
        int dpi,
        int expectedTextLeft)
    {
        var itemSize = new Size(312, 40);

        var row = TrayMenuLayout.GetRowBounds(itemSize, dpi);
        var text = TrayMenuLayout.GetTextBounds(itemSize, dpi);

        Assert.Equal(0, row.Left);
        Assert.Equal(itemSize.Width, row.Width);
        Assert.Equal(expectedTextLeft, text.Left);
        Assert.Equal(row.Top, text.Top);
        Assert.Equal(row.Height, text.Height);
        Assert.Equal(
            itemSize.Width - (expectedTextLeft * 2),
            text.Width);
    }
}
