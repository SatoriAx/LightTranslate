using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LightTranslate;

public static class WindowDragHelper
{
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;

    public static void BeginDrag(Window window, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed)
            return;

        if (IsInsideButton(e.OriginalSource as DependencyObject))
            return;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        ReleaseCapture();
        SendMessage(handle, WmNcLeftButtonDown, new IntPtr(HtCaption), IntPtr.Zero);
        e.Handled = true;
    }

    private static bool IsInsideButton(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is ButtonBase)
                return true;

            current = current is Visual or Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
