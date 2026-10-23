using System.Net.Http;
using System.Security.Cryptography;

namespace AsgardHuntMeter;

/// <summary>
/// 앱 실행에 필요한 구성 요소를 준비한다.
/// 릴리스 ZIP에는 포함하지 않고, 공식 서버에서 내려받아 무결성을 확인한 뒤 보관한다.
/// </summary>
public static class CoreProvisioner
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string LastError { get; private set; } = "";

    public static async Task<string?> EnsureAsync(Action<string>? status = null, CancellationToken ct = default)
    {
        var path = AppSettings.ManagedCorePath;
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            return path;
        }

        await Gate.WaitAsync(ct);
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return path;
            }

            LastError = "";
            var version = typeof(AppSettings).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            var url = AppSettings.DefaultCommunityUrl.TrimEnd('/') +
                      "/api/client/core/" + Uri.EscapeDataString(version);

            status?.Invoke("구성 요소 준비 중...");

            var dir = Path.GetDirectoryName(path) ?? AppSettings.AppDirectory;
            Directory.CreateDirectory(dir);
            var part = path + ".part";
            try
            {
                if (File.Exists(part)) File.Delete(part);
            }
            catch
            {
            }

            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = "서버 응답 " + (int)resp.StatusCode;
                return null;
            }

            var expectedSha = resp.Headers.TryGetValues("X-Asgard-Sha256", out var values)
                ? values.FirstOrDefault()
                : null;
            if (string.IsNullOrWhiteSpace(expectedSha))
            {
                LastError = "무결성 정보가 없습니다.";
                return null;
            }

            var expectedSize = resp.Content.Headers.ContentLength ?? 0;
            await using (var input = await resp.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                await input.CopyToAsync(output, 81920, ct);
            }

            var downloaded = new FileInfo(part);
            if (expectedSize > 0 && downloaded.Length != expectedSize)
            {
                LastError = "다운로드 크기가 일치하지 않습니다.";
                TryDelete(part);
                return null;
            }

            string actualSha;
            await using (var fs = File.OpenRead(part))
            {
                actualSha = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            }
            if (!actualSha.Equals(expectedSha.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                LastError = "무결성 검사에 실패했습니다.";
                TryDelete(part);
                return null;
            }

            File.Move(part, path, true);
            CleanupOldCores(path);
            status?.Invoke("구성 요소 준비 완료");
            return path;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void CleanupOldCores(string keep)
    {
        try
        {
            var dir = Path.GetDirectoryName(keep);
            if (string.IsNullOrWhiteSpace(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, "asgard-core-*.exe"))
            {
                if (string.Equals(file, keep, StringComparison.OrdinalIgnoreCase)) continue;
                TryDelete(file);
            }
        }
        catch
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }
}
