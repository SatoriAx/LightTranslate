using System.IO;

namespace LightTranslate;

public sealed class TranslationHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string SourceText { get; set; } = string.Empty;
    public string ResultText { get; set; } = string.Empty;
    public string Action { get; set; } = "翻译";
    public string TargetLanguage { get; set; } = "简体中文";

    public string Preview
    {
        get
        {
            var compact = SourceText.Replace("\r", " ").Replace("\n", " ").Trim();
            return compact.Length <= 58 ? compact : compact[..58] + "…";
        }
    }

    public string TimeLabel => CreatedAt.ToString("MM-dd  HH:mm");
}

public static class HistoryStore
{
    private const int MaximumEntries = 20;
    private static readonly object Gate = new();
    private static readonly string DirectoryPath =
        Environment.GetEnvironmentVariable("LIGHTTRANSLATE_DATA_DIR")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LightTranslate");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "history.json");

    public static IReadOnlyList<TranslationHistoryEntry> Load()
    {
        lock (Gate)
        {
            return AtomicFileStore.LoadJson(FilePath, () => new List<TranslationHistoryEntry>())
                .OrderByDescending(entry => entry.CreatedAt)
                .Take(MaximumEntries)
                .ToList();
        }
    }

    public static void Add(TranslationHistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.SourceText) || string.IsNullOrWhiteSpace(entry.ResultText))
            return;

        lock (Gate)
        {
            var entries = Load().ToList();
            entries.RemoveAll(existing =>
                existing.SourceText == entry.SourceText &&
                existing.ResultText == entry.ResultText &&
                existing.Action == entry.Action);
            entries.Insert(0, entry);
            AtomicFileStore.SaveJson(FilePath, entries.Take(MaximumEntries).ToList());
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            DeleteIfExists(FilePath);
            DeleteIfExists(FilePath + ".bak");
        }
    }

    private static void DeleteIfExists(string path)
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
