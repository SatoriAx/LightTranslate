using System.IO;

namespace LightTranslate;

public sealed class AppSettings
{
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string Model { get; set; } = "deepseek-v4-flash";
    public string ApiProtocol { get; set; } = TranslationApiProtocolPolicy.AutoSetting;
    public string ReasoningEffort { get; set; } = "high";
    public string TargetLanguage { get; set; } = "简体中文";
    public bool AutoHideOnFocusLoss { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public bool AutoCopyTranslation { get; set; } = false;
    public bool EnhanceSmallText { get; set; } = true;
    public CaptureRegion? LastCaptureRegion { get; set; }
}

public static class SettingsStore
{
    private static readonly string DirectoryPath =
        Environment.GetEnvironmentVariable("LIGHTTRANSLATE_DATA_DIR")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LightTranslate");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        var settings = AtomicFileStore.LoadJson(FilePath, () => new AppSettings());
        settings.ApiProtocol = TranslationApiProtocolPolicy.NormalizeSetting(settings.ApiProtocol);
        settings.ReasoningEffort = "high";
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        settings.ApiProtocol = TranslationApiProtocolPolicy.NormalizeSetting(settings.ApiProtocol);
        settings.ReasoningEffort = "high";
        AtomicFileStore.SaveJson(FilePath, settings);
    }
}
