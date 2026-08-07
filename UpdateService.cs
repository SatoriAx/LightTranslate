using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace LightTranslate;

public sealed record UpdateInfo(
    Version Version,
    string TagName,
    string ExeDownloadUrl,
    string Sha256DownloadUrl);

public static class UpdateService
{
    private const string Repo = "SatoriAx/LightTranslate";
    private const string AssetExeName = "LightTranslate-windows-x64.exe";
    private const string AssetShaName = AssetExeName + ".sha256";
    private const string UpdaterResourceName = "LightTranslate.Updater.exe";

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    private static readonly HttpClient CheckClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    private static readonly HttpClient DownloadClient = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public static string DefaultTargetDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty;

    public static async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://github.com/{Repo}/releases/latest");
            request.Headers.UserAgent.ParseAdd("LightTranslate/1.0 (auto-update)");

            using var response = await CheckClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Found && response.StatusCode != HttpStatusCode.RedirectKeepVerb)
                return null;

            var location = response.Headers.Location?.ToString() ?? string.Empty;
            if (!location.Contains("/tag/", StringComparison.OrdinalIgnoreCase))
                return null;

            var tag = location.TrimEnd('/').Split('/')[^1];
            if (!Version.TryParse(tag.TrimStart('v'), out var remoteVersion))
                return null;
            if (remoteVersion <= CurrentVersion)
                return null;

            return new UpdateInfo(
                remoteVersion,
                tag,
                $"https://github.com/{Repo}/releases/download/{tag}/{AssetExeName}",
                $"https://github.com/{Repo}/releases/download/{tag}/{AssetShaName}");
        }
        catch
        {
            return null;
        }
    }

    public static async Task<string> DownloadAsync(
        UpdateInfo info,
        string targetDirectory,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(targetDirectory);
        var tempExe = Path.Combine(targetDirectory, AssetExeName + ".new.tmp");
        var newExe = Path.Combine(targetDirectory, AssetExeName + ".new");

        try
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, info.ExeDownloadUrl))
            {
                request.Headers.UserAgent.ParseAdd("LightTranslate/1.0 (auto-update)");
                using var response = await DownloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? 0L;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var target = File.Create(tempExe);
                var buffer = new byte[81920];
                long read = 0;
                while (true)
                {
                    var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (count <= 0)
                        break;
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    read += count;
                    if (total > 0)
                        progress?.Report((int)(read * 100 / total));
                }
            }

            var expected = string.Empty;
            if (!string.IsNullOrWhiteSpace(info.Sha256DownloadUrl))
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, info.Sha256DownloadUrl);
                request.Headers.UserAgent.ParseAdd("LightTranslate/1.0 (auto-update)");
                using var response = await DownloadClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    expected = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
            }

            if (expected.Length == 64)
            {
                var actual = Convert.ToHexString(SHA256.HashData(
                    await File.ReadAllBytesAsync(tempExe, cancellationToken).ConfigureAwait(false)));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("下载文件 SHA-256 校验不匹配，已中止更新");
            }

            File.Move(tempExe, newExe, overwrite: true);
            return newExe;
        }
        finally
        {
            TryDelete(tempExe);
        }
    }

    public static void LaunchUpdater(string newExePath)
    {
        var targetExe = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前程序路径");
        var updaterDir = Path.Combine(Path.GetTempPath(), "LightTranslate-update");
        Directory.CreateDirectory(updaterDir);

        var marker = new UpdateMarkerForUpdater
        {
            NewExe = newExePath,
            TargetExe = targetExe,
            BackupExe = targetExe + ".bak",
            // 进程名从当前 exe 文件名动态取：用户运行的可能是重命名后的文件
            // （如 LightTranslate-windows-x64.exe），硬编码进程名会让更新器等待落空
            MainProcessName = Path.GetFileNameWithoutExtension(targetExe),
            Restart = true
        };
        var markerPath = Path.Combine(updaterDir, "update.json");
        File.WriteAllText(markerPath, JsonSerializer.Serialize(marker));

        var updaterPath = Path.Combine(updaterDir, "LightTranslate.Updater.exe");
        using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(UpdaterResourceName)
               ?? throw new InvalidOperationException("更新器资源缺失"))
        using (var file = File.Create(updaterPath))
        {
            resource.CopyTo(file);
        }

        Process.Start(new ProcessStartInfo(updaterPath, $"\"{markerPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    public static void TryCleanupStaleArtifacts()
    {
        try
        {
            var directory = DefaultTargetDirectory;
            if (string.IsNullOrWhiteSpace(directory))
                return;
            TryDelete(Path.Combine(directory, AssetExeName + ".new.tmp"));
            TryDelete(Path.Combine(directory, AssetExeName + ".new"));
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
}

public sealed class UpdateMarkerForUpdater
{
    public string NewExe { get; set; } = string.Empty;
    public string TargetExe { get; set; } = string.Empty;
    public string BackupExe { get; set; } = string.Empty;
    public string MainProcessName { get; set; } = string.Empty;
    public bool Restart { get; set; } = true;
}
