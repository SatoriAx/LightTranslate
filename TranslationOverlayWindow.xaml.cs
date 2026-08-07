using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;

namespace LightTranslate;

public partial class TranslationOverlayWindow : Window
{
    private const int DefaultGapPixels = 12;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint WdaExcludeFromCapture = 0x00000011;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly TranslationService _translationService = new();
    private readonly TranslationCancellationManager _translationRequests = new();
    private CaptureRegion _anchor;
    private IntPtr _handle;
    private bool _dismissRaised;
    private bool _positionQueued;
    private long _translationGeneration;

    public event Action? DismissRequested;

    public TranslationOverlayWindow(CaptureRegion anchor)
    {
        _anchor = CloneRegion(anchor);
        InitializeComponent();
    }

    public void UpdateAnchor(CaptureRegion anchor)
    {
        _anchor = CloneRegion(anchor);
        QueuePositionUpdate();
    }

    public void ShowWithoutActivation()
    {
        ShowActivated = false;
        if (!IsVisible)
            Show();
        QueuePositionUpdate();
    }

    public void ShowOcrState(string message = "正在识别截图")
    {
        StatusText.Text = message;
        StatusDot.Fill = FindBrush("AccentBrush");
        ResultTextBox.Text = "正在读取选区中的文字…";
        ResultTextBox.Foreground = FindBrush("TextMutedBrush");
        DetailText.Text = "截图留在本机，仅发送识别后的文字";
        CopyButton.IsEnabled = false;
        QueuePositionUpdate();
    }

    public void ShowTranslatingState()
    {
        StatusText.Text = "OCR 完成 · 正在翻译";
        StatusDot.Fill = FindBrush("AccentBrightBrush");
        ResultTextBox.Text = "正在生成译文…";
        ResultTextBox.Foreground = FindBrush("TextSecondaryBrush");
        DetailText.Text = "普通翻译 · HIGH";
        CopyButton.IsEnabled = false;
        QueuePositionUpdate();
    }

    public void ShowStreamingText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        StatusText.Text = "正在翻译";
        StatusDot.Fill = FindBrush("AccentBrightBrush");
        ResultTextBox.Foreground = FindBrush("TextPrimaryBrush");
        ResultTextBox.Text = text;
        ResultTextBox.CaretIndex = ResultTextBox.Text.Length;
        ResultTextBox.ScrollToEnd();
        CopyButton.IsEnabled = true;
        QueuePositionUpdate();
    }

    public void ShowCompleted(string translation, string detail)
    {
        StatusText.Text = "翻译完成";
        StatusDot.Fill = FindBrush("SuccessBrush");
        ResultTextBox.Foreground = FindBrush("TextPrimaryBrush");
        ResultTextBox.Text = translation;
        ResultTextBox.CaretIndex = 0;
        ResultTextBox.ScrollToHome();
        DetailText.Text = detail;
        CopyButton.IsEnabled = !string.IsNullOrWhiteSpace(translation);
        QueuePositionUpdate();
    }

    public void ShowError(string message)
    {
        StatusText.Text = "截图翻译失败";
        StatusDot.Fill = FindBrush("DangerBrush");
        ResultTextBox.Foreground = FindBrush("TextSecondaryBrush");
        ResultTextBox.Text = string.IsNullOrWhiteSpace(message) ? "发生未知错误" : message;
        ResultTextBox.CaretIndex = 0;
        ResultTextBox.ScrollToHome();
        DetailText.Text = "右键关闭后可重新截图";
        CopyButton.IsEnabled = false;
        QueuePositionUpdate();
    }

    public void ShowStatusOnly(string status, string detail, bool stopped = false)
    {
        StatusText.Text = status;
        StatusDot.Fill = FindBrush(stopped ? "TextMutedBrush" : "SuccessBrush");
        DetailText.Text = detail;
        QueuePositionUpdate();
    }

    public void CloseProgrammatically()
    {
        _dismissRaised = true;
        Close();
    }

    public void CancelActiveRequest()
    {
        _translationRequests.CancelCurrent();
        _translationGeneration++;
    }

    public async Task<bool> LoadAndTranslateAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowError("没有识别到文字，请重新框选");
            return false;
        }

        var cancellation = _translationRequests.BeginNewRequest();
        var generation = ++_translationGeneration;
        var streamed = new StringBuilder();
        var progress = new Progress<string>(piece =>
        {
            if (generation != _translationGeneration)
                return;
            streamed.Append(piece);
            ShowStreamingText(streamed.ToString());
        });

        ShowTranslatingState();

        try
        {
            var result = await _translationService.TranslateStreamingAsync(
                text,
                "Simplified Chinese",
                TranslationAction.Translate,
                existingTranslation: null,
                progress,
                cancellation.Token);

            if (generation != _translationGeneration)
                return false;

            ShowCompleted(result, "普通翻译 · HIGH · 右键关闭");

            HistoryStore.Add(new TranslationHistoryEntry
            {
                SourceText = text,
                ResultText = result,
                Action = "翻译",
                TargetLanguage = "简体中文"
            });

            if (SettingsStore.Load().AutoCopyTranslation)
                TryCopyTranslation(result);

            return true;
        }
        catch (OperationCanceledException)
        {
            if (generation == _translationGeneration)
                ShowStatusOnly("已取消", "右键关闭", stopped: true);
            return false;
        }
        catch (Exception ex)
        {
            AppLogService.LogException("截图翻译失败", ex);
            if (generation == _translationGeneration)
                ShowError(ex.Message);
            return false;
        }
    }

    public static DrawingRectangle CalculatePlacement(
        DrawingRectangle workingArea,
        DrawingRectangle anchor,
        DrawingSize overlaySize,
        int gapPixels = DefaultGapPixels)
    {
        if (workingArea.Width <= 0 || workingArea.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(workingArea), "屏幕工作区尺寸无效");

        var width = Math.Clamp(Math.Max(1, overlaySize.Width), 1, workingArea.Width);
        var height = Math.Clamp(Math.Max(1, overlaySize.Height), 1, workingArea.Height);
        var gap = Math.Max(0, gapPixels);

        var centeredX = anchor.Left + (anchor.Width - width) / 2;
        var x = Math.Clamp(centeredX, workingArea.Left, workingArea.Right - width);

        var belowY = anchor.Bottom + gap;
        var aboveY = anchor.Top - gap - height;
        int y;
        if (belowY + height <= workingArea.Bottom)
        {
            y = belowY;
        }
        else if (aboveY >= workingArea.Top)
        {
            y = aboveY;
        }
        else
        {
            var belowSpace = Math.Max(0, workingArea.Bottom - anchor.Bottom - gap);
            var aboveSpace = Math.Max(0, anchor.Top - workingArea.Top - gap);
            var preferredY = belowSpace >= aboveSpace ? belowY : aboveY;
            y = Math.Clamp(preferredY, workingArea.Top, workingArea.Bottom - height);
        }

        return new DrawingRectangle(x, y, width, height);
    }

    private void QueuePositionUpdate()
    {
        if (_positionQueued)
            return;

        _positionQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _positionQueued = false;
            PositionNearAnchor();
        }), DispatcherPriority.Loaded);
    }

    private void PositionNearAnchor()
    {
        if (_handle == IntPtr.Zero || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(candidate =>
                         candidate.DeviceName.Equals(_anchor.ScreenDeviceName, StringComparison.OrdinalIgnoreCase))
                     ?? System.Windows.Forms.Screen.FromPoint(new DrawingPoint(
                         _anchor.X + Math.Max(0, _anchor.Width / 2),
                         _anchor.Y + Math.Max(0, _anchor.Height / 2)));

        var dpi = VisualTreeHelper.GetDpi(this);
        var overlaySize = new DrawingSize(
            Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY)));
        var anchor = new DrawingRectangle(
            _anchor.X,
            _anchor.Y,
            Math.Max(1, _anchor.Width),
            Math.Max(1, _anchor.Height));
        var placement = CalculatePlacement(screen.WorkingArea, anchor, overlaySize);

        SetWindowPos(
            _handle,
            HwndTopmost,
            placement.X,
            placement.Y,
            0,
            0,
            SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        try
        {
            SetWindowDisplayAffinity(_handle, WdaExcludeFromCapture);
        }
        catch
        {
            // Windows 版本或显示驱动不支持时，连续截图仍会在捕获前临时隐藏浮层。
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        QueuePositionUpdate();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        QueuePositionUpdate();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryCopyTranslation(ResultTextBox.Text))
        {
            StatusText.Text = "译文已复制";
            StatusDot.Fill = FindBrush("SuccessBrush");
        }
        else
        {
            StatusText.Text = "复制失败";
            StatusDot.Fill = FindBrush("DangerBrush");
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        RequestDismiss();
    }

    private void Window_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        RequestDismiss();
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        RequestDismiss();
        e.Handled = true;
    }

    private void RequestDismiss()
    {
        if (_dismissRaised)
            return;

        _dismissRaised = true;
        CancelActiveRequest();
        DismissRequested?.Invoke();
        Close();
    }

    private static Brush FindBrush(string key)
    {
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.White;
    }

    private static bool TryCopyTranslation(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static CaptureRegion CloneRegion(CaptureRegion region)
    {
        return new CaptureRegion
        {
            ScreenDeviceName = region.ScreenDeviceName,
            X = region.X,
            Y = region.Y,
            Width = region.Width,
            Height = region.Height
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
