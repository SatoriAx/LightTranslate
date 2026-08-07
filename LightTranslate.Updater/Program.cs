using System.Diagnostics;
using System.Text.Json;

namespace LightTranslate.Updater;

public sealed class UpdateMarker
{
    public string NewExe { get; set; } = string.Empty;
    public string TargetExe { get; set; } = string.Empty;
    public string BackupExe { get; set; } = string.Empty;
    public string MainProcessName { get; set; } = string.Empty;
    public bool Restart { get; set; } = true;
}

public static class Program
{
    private const int WaitSeconds = 60;

    public static int Main(string[] args)
    {
        var markerPath = args.Length > 0 ? args[0].Trim('"') : string.Empty;
        if (string.IsNullOrWhiteSpace(markerPath) || !File.Exists(markerPath))
        {
            WriteError($"用法: LightTranslate.Updater <update.json>（标记文件不存在: {markerPath}）");
            return 2;
        }

        UpdateMarker marker;
        try
        {
            marker = JsonSerializer.Deserialize<UpdateMarker>(File.ReadAllText(markerPath))
                     ?? throw new InvalidDataException("标记文件为空");
        }
        catch (Exception ex)
        {
            WriteError($"标记文件解析失败: {ex.Message}");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(marker.TargetExe) || string.IsNullOrWhiteSpace(marker.NewExe))
        {
            WriteError("标记文件缺少 TargetExe / NewExe");
            return 2;
        }

        try
        {
            WaitForMainProcessExit(marker.MainProcessName);

            if (File.Exists(marker.TargetExe))
                File.Move(marker.TargetExe, marker.BackupExe, overwrite: true);

            File.Move(marker.NewExe, marker.TargetExe);

            TryDelete(markerPath);
            TryDeleteDirectory(Path.GetDirectoryName(markerPath));

            if (marker.Restart && File.Exists(marker.TargetExe))
                Process.Start(new ProcessStartInfo(marker.TargetExe) { UseShellExecute = true });

            WriteLog($"更新完成: {marker.TargetExe}");
            return 0;
        }
        catch (Exception ex)
        {
            TryRollback(marker);
            WriteError($"更新失败: {ex}");
            return 1;
        }
    }

    private static void WaitForMainProcessExit(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return;

        var self = Process.GetCurrentProcess();
        var deadline = DateTime.UtcNow.AddSeconds(WaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (!Process.GetProcessesByName(processName).Any(p => p.Id != self.Id))
                return;
            Thread.Sleep(300);
        }

        var stillRunning = Process.GetProcessesByName(processName).Any(p => p.Id != self.Id);
        if (stillRunning)
            throw new TimeoutException($"等待主进程 {processName} 退出超时（{WaitSeconds}s）");
    }

    private static void TryRollback(UpdateMarker marker)
    {
        try
        {
            if (!File.Exists(marker.TargetExe) && File.Exists(marker.BackupExe))
                File.Move(marker.BackupExe, marker.TargetExe);
        }
        catch
        {
        }
    }

    private static void TryDelete(string path)
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

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void WriteLog(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "LightTranslate-update.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static void WriteError(string message)
    {
        WriteLog(message);
        Console.Error.WriteLine(message);
    }
}
