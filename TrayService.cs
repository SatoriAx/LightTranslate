using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LightTranslate;

public sealed class TrayService : IDisposable
{
    private readonly App _app;
    private readonly NotifyIcon _notifyIcon;
    private Icon _icon;
    private readonly ToolStripMenuItem _continuousItem;

    public TrayService(App app)
    {
        _app = app;
        _icon = CreateTrayIcon(false);

        var menu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(30, 33, 36),
            ForeColor = Color.FromArgb(247, 244, 238),
            ShowImageMargin = false,
            Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors())
        };

        menu.Items.Add(CreateItem("输入翻译", (_, _) => _app.ShowMainWindow(true)));
        menu.Items.Add(CreateItem("截屏翻译", (_, _) => _app.TriggerScreenshotPrototype()));
        menu.Items.Add(CreateItem("重复上次选区", (_, _) => _app.TriggerRepeatScreenshot()));
        _continuousItem = CreateItem("开启固定选区连续翻译", (_, _) => _app.ToggleContinuousCapture());
        menu.Items.Add(_continuousItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CreateItem("设置", (_, _) => _app.OpenSettings()));
        menu.Items.Add(CreateItem("退出", (_, _) => _app.ExitApplication()));

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = GetNormalTooltip(),
            ContextMenuStrip = menu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => _app.ShowMainWindow(true);
        _app.ContinuousCaptureStateChanged += UpdateContinuousItem;
    }

    private static ToolStripMenuItem CreateItem(string text, EventHandler onClick)
    {
        var item = new ToolStripMenuItem(text)
        {
            AutoSize = false,
            Width = 188,
            Height = 34,
            Padding = new Padding(12, 0, 8, 0),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular)
        };
        item.Click += onClick;
        return item;
    }

    private static Icon CreateTrayIcon(bool continuousActive)
    {
        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);

        using var applicationIcon = TryExtractApplicationIcon();
        if (applicationIcon is not null)
        {
            graphics.DrawIcon(applicationIcon, new Rectangle(0, 0, 32, 32));
        }
        else
        {
            using var background = new SolidBrush(Color.FromArgb(29, 32, 35));
            using var border = new Pen(Color.FromArgb(211, 183, 143), 1.8F);
            using var accent = new SolidBrush(Color.FromArgb(230, 206, 170));
            graphics.FillEllipse(background, 2, 2, 28, 28);
            graphics.DrawEllipse(border, 3, 3, 26, 26);
            graphics.FillRectangle(accent, 10, 9, 3, 14);
            graphics.FillRectangle(accent, 10, 20, 12, 3);
        }

        if (continuousActive)
        {
            using var dotBorder = new SolidBrush(Color.FromArgb(29, 32, 35));
            using var dot = new SolidBrush(Color.FromArgb(148, 170, 154));
            graphics.FillEllipse(dotBorder, 19, 18, 13, 13);
            graphics.FillEllipse(dot, 22, 21, 7, 7);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static Icon? TryExtractApplicationIcon()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            return string.IsNullOrWhiteSpace(executablePath)
                ? null
                : Icon.ExtractAssociatedIcon(executablePath);
        }
        catch
        {
            return null;
        }
    }

    private void UpdateContinuousItem(bool enabled)
    {
        _continuousItem.Text = enabled ? "停止固定选区连续翻译 · HIGH" : "开启固定选区连续翻译";
        _notifyIcon.Text = enabled
            ? $"LightTranslate · {GetProtocolShortName()} · 连续 HIGH"
            : GetNormalTooltip();

        var previousIcon = _icon;
        _icon = CreateTrayIcon(enabled);
        _notifyIcon.Icon = _icon;
        previousIcon.Dispose();
    }

    private static string GetNormalTooltip()
    {
        return $"LightTranslate · {GetProtocolShortName()} · HIGH / MAX";
    }

    private static string GetProtocolShortName()
    {
        return TranslationApiProtocolPolicy.Resolve(SettingsStore.Load()) == TranslationApiProtocol.Responses
            ? "Responses"
            : "Chat";
    }

    public void Dispose()
    {
        _app.ContinuousCaptureStateChanged -= UpdateContinuousItem;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Color.FromArgb(49, 53, 57);
        public override Color MenuItemBorder => Color.FromArgb(74, 78, 82);
        public override Color ToolStripDropDownBackground => Color.FromArgb(30, 33, 36);
        public override Color ImageMarginGradientBegin => Color.FromArgb(30, 33, 36);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(30, 33, 36);
        public override Color ImageMarginGradientEnd => Color.FromArgb(30, 33, 36);
        public override Color SeparatorDark => Color.FromArgb(58, 62, 66);
        public override Color SeparatorLight => Color.FromArgb(58, 62, 66);
    }
}
