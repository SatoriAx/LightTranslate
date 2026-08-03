using System.IO;
using System.Text;
using System.Text.Json;

namespace LightTranslate;

public static class AtomicFileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static T LoadJson<T>(string filePath, Func<T> fallbackFactory)
    {
        if (!File.Exists(filePath))
            return TryLoadBackup(filePath, fallbackFactory);

        try
        {
            return Deserialize<T>(File.ReadAllText(filePath));
        }
        catch (JsonException)
        {
            QuarantineCorruptFile(filePath);
            return TryLoadBackup(filePath, fallbackFactory);
        }
        catch (IOException)
        {
            return TryLoadBackup(filePath, fallbackFactory);
        }
        catch (UnauthorizedAccessException)
        {
            return fallbackFactory();
        }
    }

    public static void SaveJson<T>(string filePath, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        _ = Deserialize<T>(json);
        SaveBytes(filePath, new UTF8Encoding(false).GetBytes(json));
    }

    public static void SaveBytes(string filePath, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(filePath)
                        ?? throw new InvalidOperationException("无法确定数据文件目录");
        Directory.CreateDirectory(directory);

        var temporaryPath = filePath + ".tmp-" + Guid.NewGuid().ToString("N");
        var backupPath = filePath + ".bak";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            if (File.Exists(filePath))
            {
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
                File.Replace(temporaryPath, filePath, backupPath, true);
            }
            else
            {
                File.Move(temporaryPath, filePath);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    private static T TryLoadBackup<T>(string filePath, Func<T> fallbackFactory)
    {
        var backupPath = filePath + ".bak";
        try
        {
            return File.Exists(backupPath)
                ? Deserialize<T>(File.ReadAllText(backupPath))
                : fallbackFactory();
        }
        catch
        {
            return fallbackFactory();
        }
    }

    private static T Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json)
               ?? throw new JsonException("JSON 内容为空");
    }

    private static void QuarantineCorruptFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return;

            var directory = Path.GetDirectoryName(filePath)!;
            var name = Path.GetFileNameWithoutExtension(filePath);
            var extension = Path.GetExtension(filePath);
            var quarantinePath = Path.Combine(
                directory,
                $"{name}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss-fff}{extension}");
            File.Move(filePath, quarantinePath);
        }
        catch
        {
        }
    }
}
