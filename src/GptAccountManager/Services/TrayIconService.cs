using System.Drawing;
using System.Windows.Forms;
using GptAccountManager.ViewModels;
using Application = System.Windows.Application;

namespace GptAccountManager.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _applicationIcon;
    private readonly Action _showWindow;
    private readonly Action _exit;
    private readonly Func<Guid, Task> _switchAccount;
    private bool _disposed;

    public TrayIconService(
        Action showWindow,
        Action exit,
        Func<Guid, Task> switchAccount)
    {
        _showWindow = showWindow;
        _exit = exit;
        _switchAccount = switchAccount;
        _applicationIcon = LoadApplicationIcon();
        _notifyIcon = new NotifyIcon
        {
            Icon = _applicationIcon,
            Text = "GPT Account Manager",
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => _showWindow();
        UpdateAccounts([]);
    }

    public void UpdateAccounts(IReadOnlyCollection<AccountCardViewModel> accounts)
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false
        };

        foreach (var account in accounts)
        {
            var item = new ToolStripMenuItem(account.TrayDisplay)
            {
                Checked = account.IsActive,
                Tag = account.Id
            };
            item.Click += async (_, _) =>
            {
                if (item.Tag is Guid accountId)
                {
                    var task = await Application.Current.Dispatcher.InvokeAsync(
                        () => _switchAccount(accountId));
                    await task;
                }
            };
            menu.Items.Add(item);
        }

        if (accounts.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
        }

        var open = new ToolStripMenuItem("打开主窗口");
        open.Click += (_, _) => _showWindow();
        menu.Items.Add(open);
        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => _exit();
        menu.Items.Add(exit);

        var previous = _notifyIcon.ContextMenuStrip;
        _notifyIcon.ContextMenuStrip = menu;
        previous?.Dispose();
    }

    public void ShowMessage(string title, string message)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(3000);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _applicationIcon.Dispose();
    }

    private static Icon LoadApplicationIcon()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                var extracted = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
                if (extracted is not null)
                {
                    return extracted;
                }
            }
        }
        catch
        {
            // Fall through to a cloned system icon when extraction is unavailable.
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}
