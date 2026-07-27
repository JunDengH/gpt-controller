using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using GptAccountManager.Infrastructure;
using GptAccountManager.ViewModels;
using Application = System.Windows.Application;

namespace GptAccountManager.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _applicationIcon;
    private readonly Action _showWindow;
    private readonly Action _exit;
    private ContextMenuStrip? _contextMenu;
    private bool _disposed;

    public TrayIconService(
        Action showWindow,
        Action exit)
    {
        _showWindow = showWindow;
        _exit = exit;
        _applicationIcon = LoadApplicationIcon();
        _notifyIcon = new NotifyIcon
        {
            Icon = _applicationIcon,
            Text = "GPT Account Manager",
            Visible = true
        };
        _notifyIcon.MouseClick += NotifyIcon_MouseClick;
        UpdateAccounts([]);
    }

    public void UpdateAccounts(IReadOnlyCollection<AccountCardViewModel> accounts)
    {
        if (_disposed)
        {
            return;
        }

        var menu = new RoundedContextMenuStrip
        {
            AutoClose = true,
            BackColor = TrayPalette.Surface,
            ForeColor = TrayPalette.TextPrimary,
            Padding = new Padding(0, 6, 0, 6),
            Renderer = new TrayMenuRenderer(),
            ShowCheckMargin = false,
            ShowImageMargin = false
        };

        var activeAccount = accounts.FirstOrDefault(account => account.IsActive);
        menu.Items.Add(CreateAccountItem(activeAccount));

        var open = CreateActionItem("打开应用");
        open.Click += (_, _) => ShowWindow();
        menu.Items.Add(open);
        var exit = CreateActionItem("退出应用");
        exit.ForeColor = TrayPalette.Danger;
        exit.Click += (_, _) => ExitApplication();
        menu.Items.Add(exit);

        var previous = _contextMenu;
        _contextMenu = menu;
        _notifyIcon.ContextMenuStrip = menu;
        CloseAndDispose(previous);
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
        _notifyIcon.MouseClick -= NotifyIcon_MouseClick;
        _notifyIcon.ContextMenuStrip = null;
        CloseAndDispose(_contextMenu);
        _contextMenu = null;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _applicationIcon.Dispose();
    }

    private void NotifyIcon_MouseClick(
        object? sender,
        MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ShowWindow();
        }
    }

    private void ShowWindow()
    {
        if (_disposed)
        {
            return;
        }

        if (Application.Current.Dispatcher.CheckAccess())
        {
            _showWindow();
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(_showWindow);
    }

    private void ExitApplication()
    {
        if (_disposed)
        {
            return;
        }

        if (Application.Current.Dispatcher.CheckAccess())
        {
            _exit();
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(_exit);
    }

    private static ToolStripLabel CreateAccountItem(
        AccountCardViewModel? account)
    {
        var text = account is null
            ? "当前账号 · 尚未登录"
            : $"{account.Nickname}  ·  {account.Email}";

        return new ToolStripLabel(text)
        {
            AccessibleDescription = "当前登录账号的名称和邮箱",
            AccessibleName = "当前登录账号",
            AutoSize = false,
            Font = new Font(
                SystemFonts.MessageBoxFont?.FontFamily ??
                FontFamily.GenericSansSerif,
                9.5f,
                FontStyle.Bold),
            ForeColor = TrayPalette.TextPrimary,
            Height = 44,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            TextAlign = ContentAlignment.MiddleLeft,
            ToolTipText = text,
            Width = 312
        };
    }

    private static ToolStripMenuItem CreateActionItem(string text) =>
        new(text)
        {
            AutoSize = false,
            ForeColor = TrayPalette.TextPrimary,
            Height = 40,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            TextAlign = ContentAlignment.MiddleLeft,
            Width = 312
        };

    private static void CloseAndDispose(ContextMenuStrip? menu)
    {
        if (menu is null)
        {
            return;
        }

        if (menu.Visible)
        {
            menu.Close(ToolStripDropDownCloseReason.CloseCalled);
        }

        menu.Dispose();
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

    private static class TrayPalette
    {
        public static readonly Color Surface = Color.FromArgb(43, 44, 46);
        public static readonly Color SurfaceRaised = Color.FromArgb(49, 50, 52);
        public static readonly Color SurfaceHover = Color.FromArgb(57, 58, 61);
        public static readonly Color Border = Color.FromArgb(74, 75, 79);
        public static readonly Color TextPrimary = Color.FromArgb(244, 244, 245);
        public static readonly Color Danger = Color.FromArgb(228, 138, 138);
    }

    private sealed class RoundedContextMenuStrip : ContextMenuStrip
    {
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyWindowShape();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ApplyWindowShape();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
            {
                ApplyWindowShape();
            }
        }

        private void ApplyWindowShape()
        {
            if (!IsHandleCreated || Width <= 0 || Height <= 0)
            {
                return;
            }

            if (WindowsDwm.TryApplyDefaultRoundedCorners(Handle))
            {
                var previousRegion = Region;
                Region = null;
                previousRegion?.Dispose();
                WindowsDwm.RemoveSystemBorder(Handle);
                return;
            }

            var radius = Math.Max(
                1,
                (int)Math.Round(9 * DeviceDpi / 96d));
            var bounds = new Rectangle(
                0,
                0,
                Math.Max(1, Width - 1),
                Math.Max(1, Height - 1));
            using var path = CreateRoundedRectangle(bounds, radius);
            var roundedRegion = new Region(path);
            var previous = Region;
            Region = roundedRegion;
            previous?.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var region = Region;
                Region = null;
                region?.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
    {
        public TrayMenuRenderer()
            : base(new TrayMenuColorTable())
        {
            RoundedEdges = true;
        }

        protected override void OnRenderMenuItemBackground(
            ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || e.ToolStrip is not { } toolStrip)
            {
                return;
            }

            var bounds = TrayMenuLayout.GetRowBounds(
                e.Item.Size,
                toolStrip.DeviceDpi);
            using var path = CreateRoundedRectangle(
                bounds,
                ScaleForDpi(toolStrip, 7));
            using var brush = new SolidBrush(TrayPalette.SurfaceHover);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderItemText(
            ToolStripItemTextRenderEventArgs e)
        {
            if (e.ToolStrip is not { } toolStrip ||
                string.IsNullOrEmpty(e.Text))
            {
                return;
            }

            var bounds = TrayMenuLayout.GetTextBounds(
                e.Item.Size,
                toolStrip.DeviceDpi);
            var flags =
                TextFormatFlags.SingleLine |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix |
                TextFormatFlags.Left;

            var color = e.Item.Enabled
                ? e.Item.ForeColor
                : SystemColors.GrayText;
            TextRenderer.DrawText(
                e.Graphics,
                e.Text,
                e.TextFont,
                bounds,
                color,
                flags);
        }

        protected override void OnRenderToolStripBackground(
            ToolStripRenderEventArgs e)
        {
            var bounds = new Rectangle(
                0,
                0,
                Math.Max(1, e.ToolStrip.Width - 1),
                Math.Max(1, e.ToolStrip.Height - 1));
            using var path = CreateRoundedRectangle(
                bounds,
                ScaleForDpi(e.ToolStrip, 9));
            using var brush = new SolidBrush(TrayPalette.Surface);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderToolStripBorder(
            ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(TrayPalette.Border);
            var bounds = new Rectangle(
                0,
                0,
                Math.Max(0, e.ToolStrip.Width - 1),
                Math.Max(0, e.ToolStrip.Height - 1));
            using var path = CreateRoundedRectangle(
                bounds,
                ScaleForDpi(e.ToolStrip, 9));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawPath(pen, path);
        }
    }

    private sealed class TrayMenuColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground =>
            TrayPalette.Surface;

        public override Color ImageMarginGradientBegin =>
            TrayPalette.Surface;

        public override Color ImageMarginGradientMiddle =>
            TrayPalette.Surface;

        public override Color ImageMarginGradientEnd =>
            TrayPalette.Surface;

        public override Color MenuBorder => TrayPalette.Border;

        public override Color MenuItemBorder =>
            TrayPalette.SurfaceHover;

        public override Color MenuItemSelected =>
            TrayPalette.SurfaceHover;

        public override Color MenuItemSelectedGradientBegin =>
            TrayPalette.SurfaceHover;

        public override Color MenuItemSelectedGradientEnd =>
            TrayPalette.SurfaceHover;

        public override Color MenuItemPressedGradientBegin =>
            TrayPalette.SurfaceRaised;

        public override Color MenuItemPressedGradientEnd =>
            TrayPalette.SurfaceRaised;
    }

    private static GraphicsPath CreateRoundedRectangle(
        Rectangle bounds,
        int radius)
    {
        var safeRadius = Math.Max(
            1,
            Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
        var diameter = safeRadius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(
            bounds.Right - diameter,
            bounds.Top,
            diameter,
            diameter,
            270,
            90);
        path.AddArc(
            bounds.Right - diameter,
            bounds.Bottom - diameter,
            diameter,
            diameter,
            0,
            90);
        path.AddArc(
            bounds.Left,
            bounds.Bottom - diameter,
            diameter,
            diameter,
            90,
            90);
        path.CloseFigure();
        return path;
    }

    private static int ScaleForDpi(ToolStrip toolStrip, int logicalPixels) =>
        Math.Max(
            1,
            (int)Math.Round(logicalPixels * toolStrip.DeviceDpi / 96d));
}

internal static class TrayMenuLayout
{
    private const int LogicalTextInset = 12;
    private const int LogicalVerticalInset = 2;

    public static Rectangle GetRowBounds(
        Size itemSize,
        int dpi)
    {
        var verticalInset = ScaleForDpi(LogicalVerticalInset, dpi);
        return new Rectangle(
            0,
            verticalInset,
            Math.Max(1, itemSize.Width),
            Math.Max(1, itemSize.Height - (verticalInset * 2)));
    }

    public static Rectangle GetTextBounds(
        Size itemSize,
        int dpi)
    {
        var rowBounds = GetRowBounds(itemSize, dpi);
        var textInset = ScaleForDpi(LogicalTextInset, dpi);
        return new Rectangle(
            rowBounds.Left + textInset,
            rowBounds.Top,
            Math.Max(1, rowBounds.Width - (textInset * 2)),
            rowBounds.Height);
    }

    private static int ScaleForDpi(int logicalPixels, int dpi) =>
        Math.Max(
            1,
            (int)Math.Round(logicalPixels * dpi / 96d));
}
