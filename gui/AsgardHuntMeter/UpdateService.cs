using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AsgardHuntMeter;

public sealed record UpdateInfo(
    string Version,
    string Tag,
    string Name,
    string Body,
    string ReleaseUrl,
    string AssetName,
    string DownloadUrl,
    long Size,
    string? Sha256);

/// <summary>GitHub Releases 기반 업데이트 확인/다운로드/적용.</summary>
public static class UpdateService
{
    public const string Repository = "qopwa1234/asgard-hunt-meter";
    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);

    public static string CurrentVersionText => CurrentVersion.ToString(3);

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("AsgardHuntMeter/" + CurrentVersion.ToString(3));
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>최신 릴리스가 현재 버전보다 새로우면 정보를 반환한다.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{Repository}/releases/latest";
        var json = await Http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
        {
            return null;
        }
        if (latest <= CurrentVersion)
        {
            return null;
        }

        string? assetName = null;
        string? downloadUrl = null;
        long size = 0;
        string? sha = null;
        string? shaAssetUrl = null;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.StartsWith("AsgardHuntMeter-", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    assetName = name;
                    downloadUrl = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                    size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                    if (a.TryGetProperty("digest", out var dig) && dig.GetString() is { } digest &&
                        digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    {
                        sha = digest[7..];
                    }
                }
                else if (name.EndsWith(".zip.sha256", StringComparison.OrdinalIgnoreCase))
                {
                    shaAssetUrl = a.TryGetProperty("browser_download_url", out var d2) ? d2.GetString() : null;
                }
            }
        }

        if (assetName is null || string.IsNullOrWhiteSpace(downloadUrl))
        {
            return null;
        }

        if (sha is null && !string.IsNullOrWhiteSpace(shaAssetUrl))
        {
            try
            {
                var text = await Http.GetStringAsync(shaAssetUrl, ct);
                var m = Regex.Match(text, "[0-9a-fA-F]{64}");
                if (m.Success) sha = m.Value.ToLowerInvariant();
            }
            catch
            {
                // 해시 파일이 없어도 다운로드는 허용 (경고만)
            }
        }

        return new UpdateInfo(
            latest.ToString(3),
            tag,
            root.TryGetProperty("name", out var nm) ? nm.GetString() ?? tag : tag,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            root.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? "" : "",
            assetName,
            downloadUrl,
            size,
            sha);
    }

    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AsgardHuntMeter", "updates");
        Directory.CreateDirectory(dir);
        var safeVersion = string.Concat(info.Version.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dest = Path.Combine(dir, $"AsgardHuntMeter-{safeVersion}.zip");
        var part = dest + ".part";

        using (var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? info.Size;
            await using var input = await resp.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(part);
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                if (total > 0) progress?.Report(received * 100.0 / total);
            }
            if (info.Size > 0 && received != info.Size)
            {
                throw new IOException($"다운로드 크기 불일치 ({received} / {info.Size})");
            }
        }

        if (!string.IsNullOrWhiteSpace(info.Sha256))
        {
            await using var fs = File.OpenRead(part);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new InvalidDataException("업데이트 파일 무결성 검사에 실패했습니다.");
            }
        }

        File.Move(part, dest, true);
        progress?.Report(100);
        return dest;
    }

    /// <summary>ZIP을 풀고, 앱 종료 후 파일을 교체하는 PowerShell 스크립트를 실행한다.</summary>
    public static bool ApplyUpdate(string zipPath, string version, out string message)
    {
        message = "";
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AsgardHuntMeter", "updates", "apply-" + version);
            if (Directory.Exists(root)) Directory.Delete(root, true);
            var files = Path.Combine(root, "files");
            Directory.CreateDirectory(files);
            ZipFile.ExtractToDirectory(zipPath, files, true);

            if (!File.Exists(Path.Combine(files, "AsgardHuntMeter.exe")))
            {
                message = "업데이트 ZIP에 AsgardHuntMeter.exe가 없습니다.";
                return false;
            }

            var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var probe = Path.Combine(appDir, ".update-probe");
            File.WriteAllText(probe, "1");
            File.Delete(probe);

            var script = Path.Combine(root, "apply-update.ps1");
            var appDirPs = appDir.Replace("'", "''");
            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'Stop'");
            sb.AppendLine("$log = Join-Path $PSScriptRoot 'update.log'");
            sb.AppendLine("try {");
            sb.AppendLine("  Start-Sleep -Seconds 2");
            sb.AppendLine("  Get-Process -Name 'AsgardHuntMeter' -ErrorAction SilentlyContinue | Stop-Process -Force");
            sb.AppendLine("  Get-Process -Name 'asgard-core*' -ErrorAction SilentlyContinue | Stop-Process -Force");
            sb.AppendLine("  Start-Sleep -Milliseconds 800");
            sb.AppendLine($"  Copy-Item -Path (Join-Path $PSScriptRoot 'files\\*') -Destination '{appDirPs}' -Recurse -Force");
            sb.AppendLine($"  Start-Process -FilePath (Join-Path '{appDirPs}' 'AsgardHuntMeter.exe')");
            sb.AppendLine("} catch {");
            sb.AppendLine("  $_ | Out-File -FilePath $log -Append -Encoding utf8");
            sb.AppendLine("  exit 1");
            sb.AppendLine("}");
            File.WriteAllText(script, sb.ToString(), new UTF8Encoding(true));

            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
            message = "업데이트 적용을 위해 앱을 재시작합니다.";
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    public static void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo($"https://github.com/{Repository}/releases/latest") { UseShellExecute = true });
        }
        catch
        {
        }
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.0} MB" : $"{bytes / 1024.0:0} KB";
}
