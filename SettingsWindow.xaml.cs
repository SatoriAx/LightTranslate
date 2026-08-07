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
    private UpdateInfo? _pendingUpdate;

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
        ApiProtocolBox.SelectedValue = settings.ApiProtocol;
        UpdateApiProtocolHint();
        TranslateEffortBox.SelectedValue = settings.TranslateEffort;
        ExplainEffortBox.SelectedValue = settings.ExplainEffort;
        PolishEffortBox.SelectedValue = settings.PolishEffort;
        ApiKeyBox.Password = SecretStore.LoadApiKey();
        AutoCopyCheck.IsChecked = settings.AutoCopyTranslation;
        EnhanceSmallTextCheck.IsChecked = settings.EnhanceSmallText;
        AutoHideCheck.IsChecked = settings.AutoHideOnFocusLoss;
        StartWithWindowsCheck.IsChecked = IsStartupEnabled();
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is not null)
        {
            await DownloadAndUpdateAsync(_pendingUpdate);
            return;
        }

        try
        {
            CheckUpdateButton.IsEnabled = false;
            CheckUpdateLabel.Text = "检查中…";
            UpdateStatusText.Text = $"当前版本 {UpdateService.CurrentVersion.ToString(3)}";

            var info = await UpdateService.CheckForUpdateAsync();
            if (info is null)
            {
                UpdateStatusText.Text = "已是最新版本";
                CheckUpdateLabel.Text = "检查更新";
                return;
            }

            _pendingUpdate = info;
            UpdateStatusText.Text = $"发现新版本 {info.Version}";
            CheckUpdateLabel.Text = "下载并更新";
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"检查失败：{ex.Message}";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async Task DownloadAndUpdateAsync(UpdateInfo info)
    {
        try
        {
            CheckUpdateButton.IsEnabled = false;
            CheckUpdateLabel.Text = "下载中…";
            UpdateStatusText.Text = "正在连接 GitHub…";

            var targetDirectory = UpdateService.DefaultTargetDirectory;
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                UpdateStatusText.Text = "无法定位程序目录，请手动下载更新";
                CheckUpdateButton.IsEnabled = true;
                CheckUpdateLabel.Text = "检查更新";
                return;
            }

            var progress = new Progress<int>(percent =>
                UpdateStatusText.Text = $"正在下载 {percent}%");
            var newExe = await UpdateService.DownloadAsync(info, targetDirectory, progress);

            UpdateStatusText.Text = "校验完成，正在重启应用…";
            UpdateService.LaunchUpdater(newExe);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"更新失败：{ex.Message}";
            CheckUpdateLabel.Text = "重试";
            CheckUpdateButton.IsEnabled = true;
        }
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
            var settings = SettingsStore.Load();
            var protocol = TranslationApiProtocolPolicy.GetResolvedDisplayName(settings);
            var effort = settings.TranslateEffort.ToUpperInvariant();
            SaveStateText.Text = $"正在以 {protocol} · {effort} 测试连接…";
            var service = new TranslationService();
            var result = await service.TranslateAsync("Hello, world.", "Simplified Chinese");
            SaveStateText.Text = $"连接正常 · {protocol} · {effort} · {TrimPreview(result)}";
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
        settings.ApiProtocol = ApiProtocolBox.SelectedValue?.ToString()
                               ?? TranslationApiProtocolPolicy.AutoSetting;
        settings.TranslateEffort = TranslateEffortBox.SelectedValue?.ToString() ?? "medium";
        settings.ExplainEffort = ExplainEffortBox.SelectedValue?.ToString() ?? "max";
        settings.PolishEffort = PolishEffortBox.SelectedValue?.ToString() ?? "max";
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

    private void ApiProtocolBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateApiProtocolHint();
    }

    private void UpdateApiProtocolHint()
    {
        if (ApiProtocolHintText is null || ApiProtocolBox is null)
            return;

        ApiProtocolHintText.Text = ApiProtocolBox.SelectedValue?.ToString() switch
        {
            TranslationApiProtocolPolicy.ResponsesSetting =>
                "使用 /responses 与语义化 SSE；适合原生支持 Responses 的服务",
            TranslationApiProtocolPolicy.ChatCompletionsSetting =>
                "使用 /chat/completions；兼容传统 OpenAI-compatible 服务",
            _ => "DeepSeek 官方 V4 Flash 自动使用原生 Responses API"
        };
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
