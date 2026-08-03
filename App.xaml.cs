using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace LightTranslate;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private readonly OcrService _ocrService = new();
    private TrayService? _trayService;
    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;
    private readonly DispatcherTimer _continuousTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(1600)
    };
    private bool _continuousBusy;
    private string _lastContinuousImageHash = string.Empty;
    private string _lastContinuousOcrText = string.Empty;

    public bool IsContinuousCaptureEnabled => _continuousTimer.IsEnabled;
    public event Action<bool>? ContinuousCaptureStateChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        if (TryRunCommandMode(e.Args))
            return;

        var instanceName = Environment.GetEnvironmentVariable("LIGHTTRANSLATE_INSTANCE_NAME")
                           ?? "LightTranslate_SingleInstance";
        _singleInstance = new Mutex(true, instanceName, out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _continuousTimer.Tick += async (_, _) => await CaptureContinuousRegionAsync();
        _trayService = new TrayService(this);
        _mainWindow.Show();
    }

    private bool TryRunCommandMode(string[] args)
    {
        if (args.Length == 2 && args[0].Equals("--configure-provider-file", StringComparison.OrdinalIgnoreCase))
        {
            ConfigureProviderFromFile(args[1]);
            Shutdown();
            return true;
        }

        if (args.Length == 2 && args[0].Equals("--smoke-test", StringComparison.OrdinalIgnoreCase))
        {
            RunSmokeTest(args[1]);
            Shutdown();
            return true;
        }

        if (args.Length == 2 && args[0].Equals("--cancel-smoke", StringComparison.OrdinalIgnoreCase))
        {
            RunCancellationSmokeTest(args[1]);
            Shutdown();
            return true;
        }

        if (args.Length == 2 && args[0].Equals("--data-smoke", StringComparison.OrdinalIgnoreCase))
        {
            RunDataSmokeTest(args[1]);
            Shutdown();
            return true;
        }

        if (args.Length == 2 && args[0].Equals("--stream-timeout-smoke", StringComparison.OrdinalIgnoreCase))
        {
            RunStreamTimeoutSmokeTest(args[1]);
            Shutdown();
            return true;
        }

        if (args.Length == 2 && args[0].Equals("--window-smoke", StringComparison.OrdinalIgnoreCase))
        {
            RunWindowSmokeTest(args[1]);
            Shutdown();
            return true;
        }

        if (args.Length == 2 && args[0].Equals("--resilience-smoke", StringComparison.OrdinalIgnoreCase))
        {
            RunResilienceSmokeTest(args[1]);
            Shutdown();
            return true;
        }

        if (args.Length == 2 && args[0].Equals("--effort-smoke", StringComparison.OrdinalIgnoreCase))
        {
            RunEffortPolicySmokeTest(args[1]);
            Shutdown();
            return true;
        }

        if (args.Length == 3 && args[0].Equals("--ocr-smoke", StringComparison.OrdinalIgnoreCase))
        {
            RunOcrSmokeTest(args[1], args[2]);
            Shutdown();
            return true;
        }

        if (args.Length == 3 && args[0].Equals("--capture-smoke", StringComparison.OrdinalIgnoreCase))
        {
            RunCaptureSmokeTest(args[1], args[2]);
            Shutdown();
            return true;
        }

        if (args.Length == 3 && args[0].Equals("--pipeline-smoke", StringComparison.OrdinalIgnoreCase))
        {
            RunPipelineSmokeTest(args[1], args[2]);
            Shutdown();
            return true;
        }

        if (args.Length == 4 && args[0].Equals("--action-smoke", StringComparison.OrdinalIgnoreCase))
        {
            RunActionSmokeTest(args[1], args[2], args[3]);
            Shutdown();
            return true;
        }

        return false;
    }

    private static void ConfigureProviderFromFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var provider = JsonSerializer.Deserialize<ProviderBootstrap>(json)
                           ?? throw new InvalidOperationException("配置文件内容无效");

            if (string.IsNullOrWhiteSpace(provider.BaseUrl) ||
                string.IsNullOrWhiteSpace(provider.Model) ||
                string.IsNullOrWhiteSpace(provider.ApiKey))
                throw new InvalidOperationException("配置文件缺少 BaseUrl、Model 或 ApiKey");

            var settings = SettingsStore.Load();
            settings.BaseUrl = provider.BaseUrl.Trim();
            settings.Model = provider.Model.Trim();
            settings.ReasoningEffort = "high";
            SettingsStore.Save(settings);
            SecretStore.SaveApiKey(provider.ApiKey);
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }

    private static void RunStreamTimeoutSmokeTest(string outputPath)
    {
        var result = new SmokeTestResult();
        var testDirectory = Path.Combine(Path.GetTempPath(), "LightTranslate-timeout-smoke-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("LIGHTTRANSLATE_DATA_DIR", testDirectory);
        Environment.SetEnvironmentVariable("LIGHTTRANSLATE_STREAM_TIMEOUT_SECONDS", "1");

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var capturedRequest = string.Empty;
            var serverTask = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                var buffer = new byte[65536];
                var read = await stream.ReadAsync(buffer);
                capturedRequest = Encoding.UTF8.GetString(buffer, 0, read);

                var headers = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers);
                await stream.FlushAsync();
                await Task.Delay(3000);
            });

            SettingsStore.Save(new AppSettings
            {
                BaseUrl = $"http://127.0.0.1:{port}/v1",
                Model = "timeout-test",
                ReasoningEffort = "high"
            });
            SecretStore.SaveApiKey("test-key");

            var timeoutObserved = false;
            try
            {
                var service = new TranslationService();
                service.TranslateAsync("Hello", "Simplified Chinese").GetAwaiter().GetResult();
                result.Error = "流式响应未按预期超时";
            }
            catch (TimeoutException ex)
            {
                timeoutObserved = true;
                result.Translation = ex.Message;
            }

            try
            {
                serverTask.GetAwaiter().GetResult();
            }
            catch
            {
            }

            var highLocked = capturedRequest.Contains("\"reasoning_effort\":\"high\"", StringComparison.Ordinal) &&
                             capturedRequest.Contains("\"thinking\":{\"type\":\"enabled\"}", StringComparison.Ordinal);
            result.Success = timeoutObserved && highLocked;
            if (!highLocked)
                result.Error = "普通翻译请求没有锁定 HIGH 与 thinking enabled";
            else if (timeoutObserved)
                result.Translation += " · HIGH_PAYLOAD=PASS";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }
        finally
        {
            listener.Stop();
            try
            {
                if (Directory.Exists(testDirectory))
                    Directory.Delete(testDirectory, true);
            }
            catch
            {
            }
        }

        File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static void RunEffortPolicySmokeTest(string outputPath)
    {
        var result = new SmokeTestResult();
        var testDirectory = Path.Combine(Path.GetTempPath(), "LightTranslate-effort-smoke-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("LIGHTTRANSLATE_DATA_DIR", testDirectory);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var capturedRequests = new List<string>();
            var serverTask = Task.Run(async () =>
            {
                for (var index = 0; index < 2; index++)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    await using var stream = client.GetStream();
                    capturedRequests.Add(await ReadLocalHttpRequestAsync(stream));

                    var body = Encoding.UTF8.GetBytes(
                        "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}\n\ndata: [DONE]\n\n");
                    var headers = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers);
                    await stream.WriteAsync(body);
                    await stream.FlushAsync();
                    await Task.Delay(50);
                }
            });

            SettingsStore.Save(new AppSettings
            {
                BaseUrl = $"http://127.0.0.1:{port}/v1",
                Model = "effort-test",
                ReasoningEffort = "high"
            });
            SecretStore.SaveApiKey("test-key");

            var service = new TranslationService();
            service.TranslateStreamingAsync(
                    "Hello",
                    "Simplified Chinese",
                    TranslationAction.Translate,
                    existingTranslation: null,
                    onDelta: null)
                .GetAwaiter()
                .GetResult();
            service.TranslateStreamingAsync(
                    "Hello",
                    "Simplified Chinese",
                    TranslationAction.Explain,
                    existingTranslation: null,
                    onDelta: null)
                .GetAwaiter()
                .GetResult();
            serverTask.GetAwaiter().GetResult();

            var translatePayloadValid = capturedRequests.Count >= 1 &&
                                        capturedRequests[0].Contains("\"reasoning_effort\":\"high\"", StringComparison.Ordinal) &&
                                        capturedRequests[0].Contains("\"thinking\":{\"type\":\"enabled\"}", StringComparison.Ordinal);
            var explainPayloadValid = capturedRequests.Count >= 2 &&
                                      capturedRequests[1].Contains("\"reasoning_effort\":\"max\"", StringComparison.Ordinal) &&
                                      capturedRequests[1].Contains("\"thinking\":{\"type\":\"enabled\"}", StringComparison.Ordinal);

            result.Success = translatePayloadValid && explainPayloadValid;
            result.Translation = result.Success ? "EFFORT_PAYLOADS=PASS" : "EFFORT_PAYLOADS=FAIL";
            if (!result.Success)
                result.Error = "普通翻译 HIGH 或看懂 MAX 的请求载荷不符合策略";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }
        finally
        {
            listener.Stop();
            try
            {
                if (Directory.Exists(testDirectory))
                    Directory.Delete(testDirectory, true);
            }
            catch
            {
            }
        }

        File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static async Task<string> ReadLocalHttpRequestAsync(NetworkStream stream)
    {
        using var memory = new MemoryStream();
        var singleByte = new byte[1];
        var matched = 0;
        var terminator = new byte[] { 13, 10, 13, 10 };
        while (matched < terminator.Length)
        {
            var count = await stream.ReadAsync(singleByte);
            if (count == 0)
                break;

            memory.WriteByte(singleByte[0]);
            matched = singleByte[0] == terminator[matched]
                ? matched + 1
                : singleByte[0] == terminator[0] ? 1 : 0;
        }

        var headerBytes = memory.ToArray();
        var headerText = Encoding.ASCII.GetString(headerBytes);
        var contentLength = 0;
        var chunked = false;
        foreach (var line in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line["Content-Length:".Length..].Trim(), out var parsedLength))
            {
                contentLength = parsedLength;
            }
            else if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) &&
                     line.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                chunked = true;
            }
        }

        using var body = new MemoryStream();
        if (chunked)
        {
            while (true)
            {
                var sizeLine = await ReadAsciiLineAsync(stream)
                               ?? throw new EndOfStreamException("分块请求在长度行前结束");
                var sizeText = sizeLine.Split(';', 2)[0].Trim();
                if (!int.TryParse(
                        sizeText,
                        System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var chunkSize))
                    throw new InvalidDataException("无法解析本地测试请求的分块长度");

                if (chunkSize == 0)
                {
                    await ReadAsciiLineAsync(stream);
                    break;
                }

                var chunk = new byte[chunkSize];
                var chunkOffset = 0;
                while (chunkOffset < chunk.Length)
                {
                    var count = await stream.ReadAsync(chunk.AsMemory(chunkOffset));
                    if (count == 0)
                        throw new EndOfStreamException("分块请求正文提前结束");
                    chunkOffset += count;
                }

                body.Write(chunk);
                await ReadAsciiLineAsync(stream);
            }
        }
        else if (contentLength > 0)
        {
            var bodyBytes = new byte[contentLength];
            var offset = 0;
            while (offset < bodyBytes.Length)
            {
                var count = await stream.ReadAsync(bodyBytes.AsMemory(offset));
                if (count == 0)
                    break;
                offset += count;
            }

            body.Write(bodyBytes, 0, offset);
        }

        return headerText + Encoding.UTF8.GetString(body.ToArray());
    }

    private static async Task<string?> ReadAsciiLineAsync(NetworkStream stream)
    {
        using var line = new MemoryStream();
        var singleByte = new byte[1];
        var previousWasCarriageReturn = false;
        while (true)
        {
            var count = await stream.ReadAsync(singleByte);
            if (count == 0)
                return line.Length == 0 ? null : Encoding.ASCII.GetString(line.ToArray());

            var value = singleByte[0];
            if (previousWasCarriageReturn && value == 10)
            {
                var bytes = line.ToArray();
                return Encoding.ASCII.GetString(bytes, 0, Math.Max(0, bytes.Length - 1));
            }

            line.WriteByte(value);
            previousWasCarriageReturn = value == 13;
        }
    }

    private static void RunResilienceSmokeTest(string outputPath)
    {
        var result = new SmokeTestResult();
        var testDirectory = Path.Combine(Path.GetTempPath(), "LightTranslate-resilience-smoke-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("LIGHTTRANSLATE_DATA_DIR", testDirectory);
        try
        {
            using var manager = new TranslationCancellationManager();

            var completedRequest = manager.BeginNewRequest();
            manager.CompleteRequest(completedRequest);
            var retryRequest = manager.BeginNewRequest();
            var retryStarted = !retryRequest.IsCancellationRequested;
            manager.CompleteRequest(retryRequest);

            var staleRequest = manager.BeginNewRequest();
            var latestRequest = manager.BeginNewRequest();
            var staleCanceled = staleRequest.IsCancellationRequested;
            var latestStillActive = !latestRequest.IsCancellationRequested;
            manager.CompleteRequest(staleRequest);
            var latestSurvivedStaleCompletion = !latestRequest.IsCancellationRequested;
            var manualCancelWorked = manager.CancelCurrent() && latestRequest.IsCancellationRequested;
            manager.CompleteRequest(latestRequest);

            AppLogService.LogException("韧性测试", new InvalidOperationException("test-only"));
            var logWritten = File.Exists(AppLogService.GetLogPath()) &&
                             File.ReadAllText(AppLogService.GetLogPath()).Contains("韧性测试", StringComparison.Ordinal);
            var effortPolicyWorks =
                TranslationReasoningPolicy.GetEffort(TranslationAction.Translate) == "high" &&
                TranslationReasoningPolicy.GetEffort(TranslationAction.Explain) == "max" &&
                TranslationReasoningPolicy.GetEffort(TranslationAction.Polish) == "max";

            result.Success = retryStarted && staleCanceled && latestStillActive &&
                             latestSurvivedStaleCompletion && manualCancelWorked && logWritten &&
                             effortPolicyWorks;
            result.Translation = result.Success
                ? "REQUEST_RETRY_LOG_AND_EFFORT_POLICY=PASS"
                : "REQUEST_RETRY_LOG_AND_EFFORT_POLICY=FAIL";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }
        finally
        {
            try
            {
                if (Directory.Exists(testDirectory))
                    Directory.Delete(testDirectory, true);
            }
            catch
            {
            }
        }

        File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static void RunWindowSmokeTest(string outputPath)
    {
        var result = new SmokeTestResult();
        try
        {
            var historyWindow = new HistoryWindow();
            historyWindow.Show();
            historyWindow.UpdateLayout();
            historyWindow.Close();

            var terminologyWindow = new TerminologyWindow();
            terminologyWindow.Show();
            terminologyWindow.UpdateLayout();
            terminologyWindow.Close();

            var settingsWindow = new SettingsWindow();
            settingsWindow.Show();
            settingsWindow.UpdateLayout();
            settingsWindow.Close();

            result.Success = true;
            result.Translation = "WINDOWS_OK";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static void RunDataSmokeTest(string outputPath)
    {
        var result = new DataSmokeTestResult();
        var testDirectory = Path.Combine(Path.GetTempPath(), "LightTranslate-data-smoke-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("LIGHTTRANSLATE_DATA_DIR", testDirectory);
        try
        {
            var parseResult = TerminologyStore.ParseEditableTextWithDiagnostics(
                "DeepSeek = 深度求索\nAPI = 接口\n错误格式行");
            result.InvalidTermLineCount = parseResult.InvalidLineNumbers.Count;
            TerminologyStore.Save(parseResult.Entries);
            TerminologyStore.Save([
                .. parseResult.Entries,
                new TerminologyEntry { Source = "OCR", Target = "文字识别" }
            ]);

            File.WriteAllText(Path.Combine(testDirectory, "terminology.json"), "{ invalid json");
            result.TermCount = TerminologyStore.Load().Count;
            result.RelevantTermCount = TerminologyStore.GetRelevant("DeepSeek API").Count;
            var terminologyRecovered = result.TermCount == 2;

            HistoryStore.Add(new TranslationHistoryEntry
            {
                SourceText = "Hello",
                ResultText = "你好",
                Action = "翻译"
            });
            HistoryStore.Add(new TranslationHistoryEntry
            {
                SourceText = "Hello",
                ResultText = "你好",
                Action = "翻译"
            });
            File.WriteAllText(Path.Combine(testDirectory, "history.json"), "{ invalid json");
            result.HistoryCount = HistoryStore.Load().Count;
            var historyRecovered = result.HistoryCount == 1;

            SettingsStore.Save(new AppSettings { BaseUrl = "https://first.example/v1", Model = "first", ReasoningEffort = "high" });
            SettingsStore.Save(new AppSettings { BaseUrl = "https://second.example/v1", Model = "second", ReasoningEffort = "low" });
            File.WriteAllText(Path.Combine(testDirectory, "settings.json"), "{ invalid json");
            var recoveredSettings = SettingsStore.Load();
            result.SettingsRecovery = recoveredSettings.BaseUrl == "https://first.example/v1" &&
                                      recoveredSettings.ReasoningEffort == "high";

            SecretStore.SaveApiKey("first-key");
            SecretStore.SaveApiKey("second-key");
            File.WriteAllBytes(Path.Combine(testDirectory, "api-key.dat"), [1, 2, 3, 4]);
            result.SecretRecovery = SecretStore.LoadApiKey() == "first-key";
            SecretStore.ClearApiKey();
            result.SecretClear = !SecretStore.HasApiKey();

            result.AtomicRecovery = terminologyRecovered && historyRecovered;
            result.Success = result.InvalidTermLineCount == 1 &&
                             result.RelevantTermCount == 2 &&
                             result.AtomicRecovery &&
                             result.SettingsRecovery &&
                             result.SecretRecovery &&
                             result.SecretClear;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }
        finally
        {
            try
            {
                if (Directory.Exists(testDirectory))
                    Directory.Delete(testDirectory, true);
            }
            catch
            {
            }
        }

        File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static void RunCancellationSmokeTest(string outputPath)
    {
        var result = new SmokeTestResult();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        try
        {
            var service = new TranslationService();
            service.TranslateAsync(
                    new string('A', 12000),
                    "Simplified Chinese",
                    cancellation.Token)
                .GetAwaiter()
                .GetResult();
            result.Success = false;
            result.Error = "请求未按预期取消";
        }
        catch (OperationCanceledException)
        {
            result.Success = true;
            result.Translation = "CANCELED";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static void RunSmokeTest(string outputPath)
    {
        var result = new SmokeTestResult();
        try
        {
            var service = new TranslationService();
            result.Translation = service.TranslateAsync(
                    "The translation tool is ready.",
                    "Simplified Chinese")
                .GetAwaiter()
                .GetResult();
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static void RunCaptureSmokeTest(string imageOutputPath, string resultOutputPath)
    {
        var result = new CaptureSmokeTestResult();
        CaptureSelectionResult? capture = null;
        try
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen
                         ?? throw new InvalidOperationException("没有可用屏幕");
            capture = ScreenCaptureService.CaptureRegion(new CaptureRegion
            {
                ScreenDeviceName = screen.DeviceName,
                X = screen.Bounds.Left + 40,
                Y = screen.Bounds.Top + 40,
                Width = Math.Min(420, screen.Bounds.Width - 40),
                Height = Math.Min(240, screen.Bounds.Height - 40)
            });
            File.Copy(capture.ImagePath, imageOutputPath, true);
            using var bitmap = new System.Drawing.Bitmap(imageOutputPath);
            result.Width = bitmap.Width;
            result.Height = bitmap.Height;
            result.Success = bitmap.Width >= 8 && bitmap.Height >= 8;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }
        finally
        {
            if (capture is not null)
            {
                try
                {
                    if (File.Exists(capture.ImagePath))
                        File.Delete(capture.ImagePath);
                }
                catch
                {
                }
            }
        }

        File.WriteAllText(resultOutputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static void RunOcrSmokeTest(string imagePath, string outputPath)
    {
        var result = new OcrSmokeTestResult();
        try
        {
            using var service = new OcrService();
            result.Text = service.RecognizeAsync(imagePath).GetAwaiter().GetResult();
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static void RunActionSmokeTest(string actionName, string sourceText, string outputPath)
    {
        var result = new SmokeTestResult();
        try
        {
            var action = actionName.ToLowerInvariant() switch
            {
                "explain" => TranslationAction.Explain,
                "polish" => TranslationAction.Polish,
                _ => TranslationAction.Translate
            };
            var service = new TranslationService();
            result.Translation = service.TranslateStreamingAsync(
                    sourceText,
                    "Simplified Chinese",
                    action,
                    action == TranslationAction.Polish ? "翻译工具准备。" : null,
                    onDelta: null)
                .GetAwaiter()
                .GetResult();
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static void RunPipelineSmokeTest(string imagePath, string outputPath)
    {
        var result = new PipelineSmokeTestResult();
        try
        {
            using var ocr = new OcrService();
            result.OcrText = ocr.RecognizeAsync(imagePath).GetAwaiter().GetResult();
            var translator = new TranslationService();
            result.Translation = translator.TranslateAsync(
                    result.OcrText,
                    "Simplified Chinese")
                .GetAwaiter()
                .GetResult();
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogService.LogException("WPF Dispatcher 未处理异常", e.Exception);
        e.Handled = true;

        if (_mainWindow is not null)
        {
            ShowMainWindow();
            _mainWindow.ShowExternalError("发生异常，已阻止程序退出；详情已写入本地日志");
        }
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogService.LogException("未观察到的后台任务异常", e.Exception);
        e.SetObserved();
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            AppLogService.LogException("进程级未处理异常", exception);
    }

    public void ShowMainWindow(bool readClipboard = false)
    {
        if (_mainWindow is null)
            return;

        if (!_mainWindow.IsVisible)
            _mainWindow.Show();

        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;

        _mainWindow.Activate();
        _mainWindow.FocusSourceInput();

        if (readClipboard)
            _mainWindow.ImportClipboardText();
    }

    public void OpenSettings()
    {
        if (_settingsWindow is null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.Owner = _mainWindow;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void TriggerScreenshotPrototype()
    {
        StartScreenshotTranslation();
    }

    public void ToggleContinuousCapture()
    {
        if (_mainWindow is null)
            return;

        if (_continuousTimer.IsEnabled)
        {
            StopContinuousCapture("连续翻译已停止");
            return;
        }

        var settings = SettingsStore.Load();
        if (settings.LastCaptureRegion is not { IsUsable: true })
        {
            ShowMainWindow();
            _mainWindow.ShowExternalError("请先按 Ctrl + Alt + X 框选一个固定区域");
            return;
        }

        _lastContinuousImageHash = string.Empty;
        _lastContinuousOcrText = string.Empty;
        _continuousTimer.Start();
        ContinuousCaptureStateChanged?.Invoke(true);
        _mainWindow.UpdateContinuousState(true);
        ShowMainWindow();
        _mainWindow.ShowProcessingStatus("固定选区连续翻译已开启 · HIGH · Ctrl + Alt + F 可停止");
        _ = CaptureContinuousRegionAsync();
    }

    public async void TriggerRepeatScreenshot()
    {
        if (_mainWindow is null)
            return;

        try
        {
            var settings = SettingsStore.Load();
            if (settings.LastCaptureRegion is not { IsUsable: true } region)
            {
                ShowMainWindow();
                _mainWindow.ShowExternalError("还没有上次选区，请先按 Ctrl + Alt + X 框选一次");
                return;
            }

            _mainWindow.Hide();
            var capture = await Task.Run(() => ScreenCaptureService.CaptureRegion(region));
            await ProcessCaptureAsync(capture, "正在重新识别上次选区…");
        }
        catch (Exception ex)
        {
            AppLogService.LogException("重复上次选区失败", ex);
            ShowMainWindow();
            _mainWindow.ShowExternalError(ex.Message);
        }
    }

    private void StartScreenshotTranslation()
    {
        if (_mainWindow is null)
            return;

        _mainWindow.Hide();
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var overlay = new CaptureOverlayWindow(screen);

        overlay.CaptureCanceled += () => ShowMainWindow();
        overlay.CaptureCompleted += async capture =>
        {
            try
            {
                var settings = SettingsStore.Load();
                settings.LastCaptureRegion = capture.Region;
                SettingsStore.Save(settings);
            }
            catch (Exception ex)
            {
                AppLogService.LogException("保存上次截图区域失败", ex);
            }

            await ProcessCaptureAsync(capture, "正在载入中英日 OCR 模型并识别…");
        };

        overlay.Show();
        overlay.Activate();
    }

    private async Task CaptureContinuousRegionAsync()
    {
        if (_continuousBusy || _mainWindow is null || !_continuousTimer.IsEnabled)
            return;

        _continuousBusy = true;
        CaptureSelectionResult? capture = null;
        try
        {
            var settings = SettingsStore.Load();
            if (settings.LastCaptureRegion is not { IsUsable: true } region)
            {
                StopContinuousCapture("固定选区已失效，请重新框选");
                return;
            }

            capture = await Task.Run(() => ScreenCaptureService.CaptureRegion(region));
            var imageHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(capture.ImagePath)));
            if (imageHash == _lastContinuousImageHash)
                return;

            var text = await _ocrService.RecognizeAsync(capture.ImagePath);
            if (text == _lastContinuousOcrText)
            {
                _lastContinuousImageHash = imageHash;
                return;
            }

            _mainWindow.ShowProcessingStatus("固定选区内容已变化，正在以 HIGH 翻译…");
            var translated = await _mainWindow.LoadOcrTextAndTranslateAsync(text);
            if (translated)
            {
                _lastContinuousImageHash = imageHash;
                _lastContinuousOcrText = text;
            }
            else
            {
                _lastContinuousImageHash = string.Empty;
            }
        }
        catch (Exception ex)
        {
            AppLogService.LogException("固定选区连续翻译失败", ex);
            _mainWindow.ShowExternalError($"连续翻译：{ex.Message}");
        }
        finally
        {
            if (capture is not null)
            {
                try
                {
                    if (File.Exists(capture.ImagePath))
                        File.Delete(capture.ImagePath);
                }
                catch
                {
                }
            }

            _continuousBusy = false;
        }
    }

    private void StopContinuousCapture(string status)
    {
        _continuousTimer.Stop();
        _mainWindow?.CancelActiveRequest();
        _lastContinuousImageHash = string.Empty;
        _lastContinuousOcrText = string.Empty;
        ContinuousCaptureStateChanged?.Invoke(false);
        _mainWindow?.UpdateContinuousState(false);
        _mainWindow?.ShowProcessingStatus(status);
    }

    private async Task ProcessCaptureAsync(CaptureSelectionResult capture, string status)
    {
        if (_mainWindow is null)
            return;

        try
        {
            ShowMainWindow();
            _mainWindow.ShowProcessingStatus(status);
            var text = await _ocrService.RecognizeAsync(capture.ImagePath);
            await _mainWindow.LoadOcrTextAndTranslateAsync(text);
        }
        catch (Exception ex)
        {
            AppLogService.LogException("截图 OCR 或翻译失败", ex);
            ShowMainWindow();
            _mainWindow.ShowExternalError(ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(capture.ImagePath))
                    File.Delete(capture.ImagePath);
            }
            catch
            {
            }
        }
    }

    public void ExitApplication()
    {
        _continuousTimer.Stop();
        _trayService?.Dispose();
        _settingsWindow?.Close();
        _mainWindow?.ForceClose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        _ocrService.Dispose();
        _trayService?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

public sealed class ProviderBootstrap
{
    public string BaseUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}

public sealed class SmokeTestResult
{
    public bool Success { get; set; }
    public string Translation { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}

public sealed class OcrSmokeTestResult
{
    public bool Success { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}

public sealed class DataSmokeTestResult
{
    public bool Success { get; set; }
    public int TermCount { get; set; }
    public int RelevantTermCount { get; set; }
    public int HistoryCount { get; set; }
    public int InvalidTermLineCount { get; set; }
    public bool AtomicRecovery { get; set; }
    public bool SettingsRecovery { get; set; }
    public bool SecretRecovery { get; set; }
    public bool SecretClear { get; set; }
    public string Error { get; set; } = string.Empty;
}

public sealed class CaptureSmokeTestResult
{
    public bool Success { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Error { get; set; } = string.Empty;
}

public sealed class PipelineSmokeTestResult
{
    public bool Success { get; set; }
    public string OcrText { get; set; } = string.Empty;
    public string Translation { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}
