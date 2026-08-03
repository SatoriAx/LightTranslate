using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace LightTranslate;

public partial class CaptureOverlayWindow : Window
{
    private readonly System.Windows.Forms.Screen _screen;
    private readonly System.Drawing.Bitmap _screenBitmap;
    private Point _startPoint;
    private bool _selecting;

    public event Action<CaptureSelectionResult>? CaptureCompleted;
    public event Action? CaptureCanceled;

    public CaptureOverlayWindow(System.Windows.Forms.Screen screen)
    {
        _screen = screen;
        _screenBitmap = CaptureScreen(screen.Bounds);
        InitializeComponent();
        ScreenshotImage.Source = CreateBitmapSource(_screenBitmap);
        Cursor = Cursors.Cross;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var bounds = _screen.Bounds;
        SetWindowPos(handle, new IntPtr(-1), bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0040);
    }

    private void CaptureRoot_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = ClampPoint(e.GetPosition(CaptureRoot));
        _selecting = true;
        CaptureRoot.CaptureMouse();
        SelectionBorder.Visibility = Visibility.Visible;
        SizeBadge.Visibility = Visibility.Visible;
        UpdateSelection(_startPoint);
        e.Handled = true;
    }

    private void CaptureRoot_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_selecting)
            return;

        UpdateSelection(ClampPoint(e.GetPosition(CaptureRoot)));
    }

    private void CaptureRoot_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_selecting)
            return;

        _selecting = false;
        CaptureRoot.ReleaseMouseCapture();
        var endPoint = ClampPoint(e.GetPosition(CaptureRoot));
        var selection = CreateSelectionRect(_startPoint, endPoint);

        if (selection.Width < 8 || selection.Height < 8)
        {
            CancelCapture();
            return;
        }

        try
        {
            var result = CropSelection(selection);
            CaptureCompleted?.Invoke(result);
            Close();
        }
        catch
        {
            CancelCapture();
        }

        e.Handled = true;
    }

    private void CaptureRoot_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        CancelCapture();
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelCapture();
            e.Handled = true;
        }
    }

    private void UpdateSelection(Point current)
    {
        var rect = CreateSelectionRect(_startPoint, current);
        Canvas.SetLeft(SelectionBorder, rect.Left);
        Canvas.SetTop(SelectionBorder, rect.Top);
        SelectionBorder.Width = rect.Width;
        SelectionBorder.Height = rect.Height;

        SizeText.Text = $"{Math.Round(rect.Width)} × {Math.Round(rect.Height)}";
        SizeBadge.Margin = new Thickness(
            Math.Min(rect.Right + 9, Math.Max(0, CaptureRoot.ActualWidth - 90)),
            Math.Min(rect.Bottom + 9, Math.Max(0, CaptureRoot.ActualHeight - 32)),
            0,
            0);
    }

    private CaptureSelectionResult CropSelection(Rect selection)
    {
        var scaleX = _screenBitmap.Width / Math.Max(1.0, CaptureRoot.ActualWidth);
        var scaleY = _screenBitmap.Height / Math.Max(1.0, CaptureRoot.ActualHeight);

        var x = Math.Clamp((int)Math.Round(selection.Left * scaleX), 0, _screenBitmap.Width - 1);
        var y = Math.Clamp((int)Math.Round(selection.Top * scaleY), 0, _screenBitmap.Height - 1);
        var width = Math.Clamp((int)Math.Round(selection.Width * scaleX), 1, _screenBitmap.Width - x);
        var height = Math.Clamp((int)Math.Round(selection.Height * scaleY), 1, _screenBitmap.Height - y);

        var cropRect = new System.Drawing.Rectangle(x, y, width, height);
        using var cropped = _screenBitmap.Clone(cropRect, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var path = ScreenCaptureService.CreateTemporaryPath();
        cropped.Save(path, System.Drawing.Imaging.ImageFormat.Png);

        return new CaptureSelectionResult(path, new CaptureRegion
        {
            ScreenDeviceName = _screen.DeviceName,
            X = _screen.Bounds.Left + x,
            Y = _screen.Bounds.Top + y,
            Width = width,
            Height = height
        });
    }

    private void CancelCapture()
    {
        CaptureCanceled?.Invoke();
        Close();
    }

    private Point ClampPoint(Point point)
    {
        return new Point(
            Math.Clamp(point.X, 0, CaptureRoot.ActualWidth),
            Math.Clamp(point.Y, 0, CaptureRoot.ActualHeight));
    }

    private static Rect CreateSelectionRect(Point start, Point end)
    {
        return new Rect(
            Math.Min(start.X, end.X),
            Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X),
            Math.Abs(end.Y - start.Y));
    }

    private static System.Drawing.Bitmap CaptureScreen(System.Drawing.Rectangle bounds)
    {
        var bitmap = new System.Drawing.Bitmap(
            bounds.Width,
            bounds.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, System.Drawing.CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    private static BitmapSource CreateBitmapSource(System.Drawing.Bitmap bitmap)
    {
        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                handle,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(handle);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _screenBitmap.Dispose();
        base.OnClosed(e);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
