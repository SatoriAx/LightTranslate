using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace LightTranslate;

public enum AppHotkey
{
    TranslateClipboard,
    ScreenshotTranslate,
    RepeatScreenshot,
    ToggleContinuous
}

public sealed class HotkeyService : IDisposable
{
    private const int TranslateClipboardId = 0x4311;
    private const int ScreenshotTranslateId = 0x4312;
    private const int RepeatScreenshotId = 0x4313;
    private const int ToggleContinuousId = 0x4314;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint VkT = 0x54;
    private const uint VkX = 0x58;
    private const uint VkR = 0x52;
    private const uint VkF = 0x46;

    private readonly IntPtr _windowHandle;
    private readonly HwndSource _source;
    private bool _disposed;

    public event Action<AppHotkey>? Pressed;
    public string? RegistrationWarning { get; }

    public HotkeyService(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle);
        _source.AddHook(WindowProc);

        var warnings = new List<string>();
        if (!RegisterHotKey(windowHandle, TranslateClipboardId, ModControl | ModAlt, VkT))
            warnings.Add("Ctrl + Alt + T 已被其他程序占用");
        if (!RegisterHotKey(windowHandle, ScreenshotTranslateId, ModControl | ModAlt, VkX))
            warnings.Add("Ctrl + Alt + X 已被其他程序占用");
        if (!RegisterHotKey(windowHandle, RepeatScreenshotId, ModControl | ModAlt, VkR))
            warnings.Add("Ctrl + Alt + R 已被其他程序占用");
        if (!RegisterHotKey(windowHandle, ToggleContinuousId, ModControl | ModAlt, VkF))
            warnings.Add("Ctrl + Alt + F 已被其他程序占用");

        RegistrationWarning = warnings.Count == 0 ? null : string.Join("；", warnings);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey)
            return IntPtr.Zero;

        switch (wParam.ToInt32())
        {
            case TranslateClipboardId:
                Pressed?.Invoke(AppHotkey.TranslateClipboard);
                handled = true;
                break;
            case ScreenshotTranslateId:
                Pressed?.Invoke(AppHotkey.ScreenshotTranslate);
                handled = true;
                break;
            case RepeatScreenshotId:
                Pressed?.Invoke(AppHotkey.RepeatScreenshot);
                handled = true;
                break;
            case ToggleContinuousId:
                Pressed?.Invoke(AppHotkey.ToggleContinuous);
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        UnregisterHotKey(_windowHandle, TranslateClipboardId);
        UnregisterHotKey(_windowHandle, ScreenshotTranslateId);
        UnregisterHotKey(_windowHandle, RepeatScreenshotId);
        UnregisterHotKey(_windowHandle, ToggleContinuousId);
        _source.RemoveHook(WindowProc);
        _disposed = true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
