using Microsoft.Win32;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace LightTranslate;

public partial class SettingsWindow : Window
{
    private const string StartupValueName = "LightTranslate";
    private readonly DispatcherTimer _clearKeyResetTimer;
    private bool _clearKeyArmed;

    public SettingsWindow()
    {
        InitializeComponent();
        _clearKeyResetTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _clearKeyResetTimer.Tick += (_, _) => ResetClearKeyConfirmation();
        LoadSettings();
    }

    private void LoadSettings()
    {
        var settings = SettingsStore.Load();
        BaseUrlBox.Text = settings.BaseUrl;
        ModelBox.Text = settings.Model;
        ApiKeyBox.Password = SecretStore.LoadApiKey();
        AutoCopyCheck.IsChecked = settings.AutoCopyTranslation;
        EnhanceSmallTextCheck.IsChecked = settings.EnhanceSmallText;
        AutoHideCheck.IsChecked = settings.AutoHideOnFocusLoss;
        StartWithWindowsCheck.IsChecked = IsStartupEnabled();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var keyUpdated = SaveCurrentSettings();
            SaveStateText.Text = keyUpdated ? "设置已保存 · 密钥已加密" : "设置已安全保存";
        }
        catch (Exception ex)
        {
            SaveStateText.Text = $"保存失败：{ex.Message}";
        }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveCurrentSettings();
            SaveStateText.Text = "正在以普通翻译 HIGH 测试连接…";
            var service = new TranslationService();
            var result = await service.TranslateAsync("Hello, world.", "Simplified Chinese");
            SaveStateText.Text = $"连接正常 · HIGH · {TrimPreview(result)}";
        }
        catch (Exception ex)
        {
            SaveStateText.Text = ex.Message;
        }
    }

    private bool SaveCurrentSettings()
    {
        var settings = SettingsStore.Load();
        settings.BaseUrl = BaseUrlBox.Text.Trim();
        settings.Model = ModelBox.Text.Trim();
        settings.ReasoningEffort = "high";
        settings.AutoCopyTranslation = AutoCopyCheck.IsChecked == true;
        settings.EnhanceSmallText = EnhanceSmallTextCheck.IsChecked == true;
        settings.AutoHideOnFocusLoss = AutoHideCheck.IsChecked == true;
        settings.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        SettingsStore.Save(settings);

        var keyUpdated = !string.IsNullOrWhiteSpace(ApiKeyBox.Password);
        if (keyUpdated)
            SecretStore.SaveApiKey(ApiKeyBox.Password);

        ApplyStartupSetting(settings.StartWithWindows);
        return keyUpdated;
    }

    private void ClearApiKey_Click(object sender, RoutedEventArgs e)
    {
        if (!_clearKeyArmed)
        {
            _clearKeyArmed = true;
            ClearApiKeyLabel.Text = "再次确认";
            SaveStateText.Text = "再次点击才会清除本机 API Key";
            _clearKeyResetTimer.Stop();
            _clearKeyResetTimer.Start();
            return;
        }

        SecretStore.ClearApiKey();
        ApiKeyBox.Clear();
        ResetClearKeyConfirmation();
        SaveStateText.Text = "API Key 已从本机清除";
    }

    private void ResetClearKeyConfirmation()
    {
        _clearKeyResetTimer.Stop();
        _clearKeyArmed = false;
        ClearApiKeyLabel.Text = "清除密钥";
    }

    private static string TrimPreview(string value)
    {
        var compact = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return compact.Length <= 28 ? compact : compact[..28] + "…";
    }

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue(StartupValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyStartupSetting(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (!enabled)
            {
                key.DeleteValue(StartupValueName, false);
                return;
            }

            var executable = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executable))
                key.SetValue(StartupValueName, $"\"{executable}\"");
        }
        catch
        {
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        WindowDragHelper.BeginDrag(this, e);
    }
}
