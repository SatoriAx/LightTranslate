using System.Diagnostics;
using System.IO;
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
    private const string ReleaseApiUrl = $"https://api.github.com/repos/{Repo}/releases/latest";
    private const string AssetExeName = "LightTranslate-windows-x64.exe";
    private const string AssetShaName = AssetExeName + ".sha256";
    private const string UpdaterResourceName = "LightTranslate.Updater.exe";

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(90)
    };

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public static string DefaultTargetDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty;

    public static async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseApiUrl);
            request.Headers.UserAgent.ParseAdd("LightTranslate/1.0 (auto-update)");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await Client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? "" : "";
            if (!Version.TryParse(tag.TrimStart('v'), out var remoteVersion))
                return null;
            if (remoteVersion <= CurrentVersion)
                return null;

            string? exeUrl = null;
            string? shaUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                    var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() ?? "" : "";
                    if (name == AssetExeName)
                        exeUrl = url;
                    else if (name == AssetShaName)
                        shaUrl = url;
                }
            }

            return exeUrl is null ? null : new UpdateInfo(remoteVersion, tag, exeUrl, shaUrl ?? "");
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
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? 0L;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = File.Create(tempExe);
                var buffer = new byte[81920];
                long read = 0;
                while (true)
                {
                    var count = await source.ReadAsync(buffer, cancellationToken);
                    if (count <= 0)
                        break;
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
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
                using var response = await Client.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                    expected = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            }

            if (expected.Length == 64)
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(
                    File.OpenRead(tempExe),
                    cancellationToken));
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
            MainProcessName = "LightTranslate",
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
