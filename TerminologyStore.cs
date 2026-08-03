using System.IO;

namespace LightTranslate;

public sealed class TerminologyEntry
{
    public string Source { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
}

public sealed record TerminologyParseResult(
    IReadOnlyList<TerminologyEntry> Entries,
    IReadOnlyList<int> InvalidLineNumbers);

public static class TerminologyStore
{
    private static readonly object Gate = new();
    private static readonly string DirectoryPath =
        Environment.GetEnvironmentVariable("LIGHTTRANSLATE_DATA_DIR")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LightTranslate");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "terminology.json");

    public static IReadOnlyList<TerminologyEntry> Load()
    {
        lock (Gate)
        {
            return AtomicFileStore.LoadJson(FilePath, () => new List<TerminologyEntry>())
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Source) && !string.IsNullOrWhiteSpace(entry.Target))
                .ToList();
        }
    }

    public static void Save(IEnumerable<TerminologyEntry> entries)
    {
        lock (Gate)
        {
            var normalized = entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Source) && !string.IsNullOrWhiteSpace(entry.Target))
                .Select(entry => new TerminologyEntry
                {
                    Source = entry.Source.Trim(),
                    Target = entry.Target.Trim()
                })
                .DistinctBy(entry => entry.Source, StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .ToList();

            AtomicFileStore.SaveJson(FilePath, normalized);
        }
    }

    public static IReadOnlyList<TerminologyEntry> GetRelevant(string sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
            return [];

        return Load()
            .Where(entry => sourceText.Contains(entry.Source, StringComparison.OrdinalIgnoreCase))
            .Take(24)
            .ToList();
    }

    public static string ToEditableText(IEnumerable<TerminologyEntry> entries)
    {
        return string.Join(Environment.NewLine, entries.Select(entry => $"{entry.Source} = {entry.Target}"));
    }

    public static TerminologyParseResult ParseEditableTextWithDiagnostics(string text)
    {
        var entries = new List<TerminologyEntry>();
        var invalidLineNumbers = new List<int>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var separator = line.IndexOf('=');
            if (separator < 1 || separator >= line.Length - 1)
            {
                invalidLineNumbers.Add(index + 1);
                continue;
            }

            entries.Add(new TerminologyEntry
            {
                Source = line[..separator].Trim(),
                Target = line[(separator + 1)..].Trim()
            });
        }

        return new TerminologyParseResult(entries, invalidLineNumbers);
    }

    public static IReadOnlyList<TerminologyEntry> ParseEditableText(string text)
    {
        return ParseEditableTextWithDiagnostics(text).Entries;
    }
}
