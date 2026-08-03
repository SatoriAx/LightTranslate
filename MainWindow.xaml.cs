using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LightTranslate;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _statusResetTimer;
    private readonly TranslationService _translationService = new();
    private readonly TranslationCancellationManager _translationRequests = new();
    private HotkeyService? _hotkeyService;
    private bool _forceClose;
    private bool _languagesSwapped;
    private bool _isTranslating;
    private long _translationGeneration;

    public MainWindow()
    {
        InitializeComponent();

        _statusResetTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3.2)
        };
        _statusResetTimer.Tick += (_, _) =>
        {
            _statusResetTimer.Stop();
            ServiceStateText.Text = GetProviderStatusText();
        };

        Loaded += (_, _) =>
        {
            SourceTextBox.Focus();
            SourceTextBox.CaretIndex = SourceTextBox.Text.Length;
            ServiceStateText.Text = GetProviderStatusText();
        };

        Deactivated += (_, _) =>
        {
            if (SettingsStore.Load().AutoHideOnFocusLoss && IsVisible)
                Hide();
        };
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _hotkeyService = new HotkeyService(handle);
        _hotkeyService.Pressed += HandleHotkey;

        if (!string.IsNullOrWhiteSpace(_hotkeyService.RegistrationWarning))
            ShowStatus(_hotkeyService.RegistrationWarning!);
    }

    private void HandleHotkey(AppHotkey hotkey)
    {
        if (Application.Current is not App app)
            return;

        Dispatcher.Invoke(() =>
        {
            switch (hotkey)
            {
                case AppHotkey.TranslateClipboard:
                    app.ShowMainWindow(true);
                    break;
                case AppHotkey.ScreenshotTranslate:
                    app.TriggerScreenshotPrototype();
                    break;
                case AppHotkey.RepeatScreenshot:
                    app.TriggerRepeatScreenshot();
                    break;
                case AppHotkey.ToggleContinuous:
                    app.ToggleContinuousCapture();
                    break;
            }
        });
    }

    public void FocusSourceInput()
    {
        SourceTextBox.Focus();
        SourceTextBox.CaretIndex = SourceTextBox.Text.Length;
    }

    public void ImportClipboardText()
    {
        try
        {
            if (!Clipboard.ContainsText())
            {
                ShowStatus("剪贴板里没有可翻译的文字");
                return;
            }

            var text = Clipboard.GetText().Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowStatus("剪贴板里没有可翻译的文字");
                return;
            }

            SourceTextBox.Text = NormalizeClipboardText(text);
            SourceTextBox.CaretIndex = SourceTextBox.Text.Length;
            ShowStatus("已从剪贴板带入文字");
        }
        catch
        {
            ShowStatus("剪贴板暂时被其他程序占用");
        }
    }

    public void ShowProcessingStatus(string message)
    {
        _statusResetTimer.Stop();
        ServiceStateText.Text = message;
    }

    public void ShowExternalError(string message) => ShowStatus(message);

    public void CancelActiveRequest()
    {
        if (_isTranslating)
            CancelCurrentTranslation();
    }

    public void UpdateContinuousState(bool enabled)
    {
        ContinuousButtonLabel.Text = enabled ? "停止" : "连续";
        if (enabled)
            ModeLabel.Text = "固定选区监听中 · HIGH";
        else if (!_isTranslating)
            ModeLabel.Text = _languagesSwapped ? "自然表达 · HIGH" : "均衡翻译 · HIGH";
    }

    public async Task<bool> LoadOcrTextAndTranslateAsync(string text)
    {
        SourceTextBox.Text = text;
        SourceTextBox.CaretIndex = SourceTextBox.Text.Length;
        ShowProcessingStatus("OCR 识别完成，正在以 HIGH 翻译…");
        return await RunActionAsync(TranslationAction.Translate);
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    private static string NormalizeClipboardText(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        while (normalized.Contains("\n\n\n"))
            normalized = normalized.Replace("\n\n\n", "\n\n");
        return normalized.Trim();
    }

    private void ShowStatus(string message)
    {
        ServiceStateText.Text = message;
        _statusResetTimer.Stop();
        _statusResetTimer.Start();
    }

    private static string GetProviderStatusText()
    {
        var settings = SettingsStore.Load();
        return !string.IsNullOrWhiteSpace(settings.Model) && SecretStore.HasApiKey()
            ? $"{settings.Model} · {TranslationApiProtocolPolicy.GetResolvedDisplayName(settings)} · HIGH / MAX"
            : "需要配置 AI";
    }

    private void SourceTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        SourcePlaceholder.Visibility = string.IsNullOrEmpty(SourceTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        if (_isTranslating)
        {
            CancelCurrentTranslation();
            return;
        }

        await RunActionAsync(TranslationAction.Translate);
    }

    private async Task<bool> RunActionAsync(TranslationAction action)
    {
        if (string.IsNullOrWhiteSpace(SourceTextBox.Text))
        {
            ShowStatus("先放入一段需要处理的文字");
            SourceTextBox.Focus();
            return false;
        }

        var sourceText = SourceTextBox.Text.Trim();
        var existingTranslation = TranslationTextBox.Text;

        _statusResetTimer.Stop();
        var cancellation = _translationRequests.BeginNewRequest();
        var generation = ++_translationGeneration;
        _isTranslating = true;

        TranslateButtonLabel.Text = "取消";
        TranslationTextBox.Clear();
        TranslationPlaceholder.Visibility = Visibility.Collapsed;
        ModeLabel.Text = action switch
        {
            TranslationAction.Explain => "看懂原文 · MAX",
            TranslationAction.Polish => "精校译文 · MAX",
            _ => Application.Current is App app && app.IsContinuousCaptureEnabled
                ? "固定选区监听中 · HIGH"
                : _languagesSwapped ? "自然表达 · HIGH" : "均衡翻译 · HIGH"
        };
        var activeSettings = SettingsStore.Load();
        var activeProtocol = TranslationApiProtocolPolicy.GetResolvedDisplayName(activeSettings);
        ServiceStateText.Text = action switch
        {
            TranslationAction.Explain => $"正在通过 {activeProtocol} · MAX 解释原文与语气…",
            TranslationAction.Polish => $"正在通过 {activeProtocol} · MAX 核对并精校译文…",
            _ => $"正在请求 {activeSettings.Model} · {activeProtocol} · HIGH…"
        };

        var streamed = new StringBuilder();
        var progress = new Progress<string>(piece =>
        {
            if (generation != _translationGeneration)
                return;

            streamed.Append(piece);
            TranslationTextBox.Text = streamed.ToString();
            TranslationTextBox.CaretIndex = TranslationTextBox.Text.Length;
            TranslationTextBox.ScrollToEnd();
        });

        try
        {
            var targetLanguage = action == TranslationAction.Explain
                ? "Simplified Chinese"
                : _languagesSwapped ? "English" : "Simplified Chinese";
            var result = await _translationService.TranslateStreamingAsync(
                sourceText,
                targetLanguage,
                action,
                existingTranslation,
                progress,
                cancellation.Token);

            if (generation != _translationGeneration)
                return false;

            TranslationTextBox.Text = result;
            TranslationTextBox.CaretIndex = 0;
            TranslationTextBox.ScrollToHome();
            TranslationPlaceholder.Visibility = Visibility.Collapsed;

            var actionLabel = action switch
            {
                TranslationAction.Explain => "看懂",
                TranslationAction.Polish => "精校",
                _ => "翻译"
            };
            HistoryStore.Add(new TranslationHistoryEntry
            {
                SourceText = sourceText,
                ResultText = result,
                Action = actionLabel,
                TargetLanguage = action == TranslationAction.Explain
                    ? "简体中文说明"
                    : _languagesSwapped ? "英语" : "简体中文"
            });

            var resultUnchanged = action == TranslationAction.Translate &&
                                  NormalizeForComparison(sourceText) == NormalizeForComparison(result);
            if (action != TranslationAction.Explain && SettingsStore.Load().AutoCopyTranslation)
            {
                var copied = TryCopyTranslation();
                if (resultUnchanged)
                    ShowStatus(copied
                        ? "翻译完成 · 结果与原文相同 · 已自动复制"
                        : "翻译完成 · 结果与原文相同 · 自动复制失败");
                else
                    ShowStatus(copied ? $"{actionLabel}完成 · 已自动复制" : $"{actionLabel}完成 · 自动复制失败");
            }
            else if (resultUnchanged)
            {
                ShowStatus("翻译完成 · 结果与原文相同，可能是名称、型号或无需翻译");
            }
            else
            {
                ShowStatus($"{actionLabel}完成");
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            if (generation == _translationGeneration)
                ShowStatus("已取消当前请求");
            return false;
        }
        catch (Exception ex)
        {
            AppLogService.LogException($"{action} 请求失败", ex);
            if (generation == _translationGeneration)
                ShowStatus(ex.Message);
            return false;
        }
        finally
        {
            _translationRequests.CompleteRequest(cancellation);
            if (generation == _translationGeneration)
            {
                _isTranslating = false;
                TranslateButtonLabel.Text = "开始翻译";
                if (Application.Current is App app && app.IsContinuousCaptureEnabled)
                    ModeLabel.Text = "固定选区监听中 · HIGH";
                else if (action == TranslationAction.Translate)
                    ModeLabel.Text = _languagesSwapped ? "自然表达 · HIGH" : "均衡翻译 · HIGH";
            }
        }
    }

    private static string NormalizeForComparison(string value)
    {
        return value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
    }

    private void CancelCurrentTranslation()
    {
        if (!_isTranslating)
            return;

        _statusResetTimer.Stop();
        _translationRequests.CancelCurrent();
        ServiceStateText.Text = "正在取消…";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TranslationTextBox.Text))
        {
            ShowStatus("当前还没有结果");
            return;
        }

        ShowStatus(TryCopyTranslation() ? "内容已复制" : "剪贴板暂时被其他程序占用");
    }

    private bool TryCopyTranslation()
    {
        try
        {
            Clipboard.SetText(TranslationTextBox.Text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async void Explain_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(TranslationAction.Explain);
    }

    private async void Polish_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(TranslationAction.Polish);
    }

    private void Term_Click(object sender, RoutedEventArgs e)
    {
        var window = new TerminologyWindow { Owner = this };
        window.ShowDialog();
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        var window = new HistoryWindow { Owner = this };
        window.EntrySelected += LoadHistoryEntry;
        window.ShowDialog();
    }

    private void LoadHistoryEntry(TranslationHistoryEntry entry)
    {
        SourceTextBox.Text = entry.SourceText;
        TranslationTextBox.Text = entry.ResultText;
        SourceTextBox.CaretIndex = SourceTextBox.Text.Length;
        TranslationPlaceholder.Visibility = Visibility.Collapsed;
        ModeLabel.Text = entry.Action;
        ShowStatus("已载入历史记录");
    }

    private void Continuous_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
            app.ToggleContinuousCapture();
    }

    private void ClearSource_Click(object sender, RoutedEventArgs e)
    {
        if (_isTranslating)
            CancelCurrentTranslation();
        SourceTextBox.Clear();
        TranslationTextBox.Clear();
        TranslationPlaceholder.Visibility = Visibility.Visible;
        SourceTextBox.Focus();
        ShowStatus("已清空");
    }

    private void SwapLanguages_Click(object sender, RoutedEventArgs e)
    {
        _languagesSwapped = !_languagesSwapped;
        SourceLanguageText.Text = _languagesSwapped ? "简体中文" : "自动识别";
        SourceLanguageCode.Text = _languagesSwapped ? "  ZH" : "  AUTO";
        TargetLanguageText.Text = _languagesSwapped ? "English" : "简体中文";
        TargetLanguageCode.Text = _languagesSwapped ? "  EN" : "  ZH";
        ModeLabel.Text = _languagesSwapped ? "自然表达 · HIGH" : "均衡翻译 · HIGH";
        ShowStatus(_languagesSwapped ? "已切换为中译英 · HIGH" : "已切换为自动识别后译中 · HIGH");
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
            app.OpenSettings();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private void TitleBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        WindowDragHelper.BeginDrag(this, e);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_isTranslating)
                CancelCurrentTranslation();
            else
                Hide();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Translate_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_forceClose)
        {
            _translationRequests.Dispose();
            _hotkeyService?.Dispose();
            return;
        }

        e.Cancel = true;
        Hide();
    }
}
