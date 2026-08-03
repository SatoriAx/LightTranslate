using System.IO;
using System.Text;

namespace LightTranslate;

public static class AppLogService
{
    private const long MaximumLogBytes = 512 * 1024;
    private static readonly object Gate = new();
    private static readonly string DirectoryPath =
        Environment.GetEnvironmentVariable("LIGHTTRANSLATE_DATA_DIR")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LightTranslate");
    private static readonly string LogPath = Path.Combine(DirectoryPath, "lighttranslate.log");

    public static void LogException(string context, Exception exception)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                RotateIfNeeded();
                var entry = new StringBuilder()
                    .AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {context}")
                    .AppendLine(exception.ToString())
                    .AppendLine(new string('-', 72))
                    .ToString();
                File.AppendAllText(LogPath, entry, new UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }

    public static string GetLogPath() => LogPath;

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogPath) || new FileInfo(LogPath).Length < MaximumLogBytes)
            return;

        var previousPath = LogPath + ".old";
        if (File.Exists(previousPath))
            File.Delete(previousPath);
        File.Move(LogPath, previousPath);
    }
}
