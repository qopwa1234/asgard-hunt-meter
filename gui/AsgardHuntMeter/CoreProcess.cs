using System.Diagnostics;
using System.Text;

namespace AsgardHuntMeter;

/// <summary>asgard-core.exe (listen --json) 자식 프로세스 관리 + stdout JSON 라인 스트리밍.</summary>
public sealed class CoreProcess : IDisposable
{
    private Process? _proc;

    /// <summary>stdout 한 줄 (JSON). 백그라운드 스레드에서 발생.</summary>
    public event Action<string>? LineReceived;

    /// <summary>stderr 한 줄 (dumpcap/코어 진단 메시지).</summary>
    public event Action<string>? ErrorLine;

    public bool Running => _proc is { HasExited: false };

    public string ExePath { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 45017;
    public string Key { get; set; } = "";
    public string? OutDir { get; private set; }

    /// <summary>관리 코어/설정값/개발 트리 순서로 asgard-core.exe 탐색.</summary>
    public static string FindCoreExe(string? configured)
    {
        var managed = AppSettings.ManagedCorePath;
        if (File.Exists(managed))
        {
            return managed;
        }
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }
        var baseDir = AppSettings.AppDirectory;
        var legacy = Path.Combine(baseDir, "asgard-core.exe");
        if (File.Exists(legacy))
        {
            return legacy;
        }
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var cand = Path.Combine(dir.FullName, "core", "target", "release", "asgard-core.exe");
            if (File.Exists(cand))
            {
                return cand;
            }
        }
        return "";
    }

    public void Start()
    {
        if (Running)
        {
            return;
        }
        StopStaleCoreProcesses();
        if (string.IsNullOrWhiteSpace(ExePath) || !File.Exists(ExePath))
        {
            throw new FileNotFoundException("필수 구성 요소를 찾을 수 없습니다. 앱을 다시 실행해 주세요.", ExePath);
        }

        OutDir = Path.Combine(AppSettings.SettingsDir, "sessions", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(OutDir);

        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add("listen");
        psi.ArgumentList.Add("--host");
        psi.ArgumentList.Add(Host);
        // 서버가 월드/채널별로 여러 포트를 쓰므로 알려진 기본 포트와 범위를 함께 전달한다.
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add("45000..45100");
        foreach (var p in new[] { Port, 45016, 5010 }.Distinct())
        {
            psi.ArgumentList.Add("--port");
            psi.ArgumentList.Add(p.ToString());
        }
        if (!string.IsNullOrWhiteSpace(Key))
        {
            psi.ArgumentList.Add("--key");
            psi.ArgumentList.Add(Key);
        }
        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add("--out");
        psi.ArgumentList.Add(OutDir);

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                LineReceived?.Invoke(e.Data);
            }
        };
        _proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                ErrorLine?.Invoke(e.Data);
            }
        };
        _proc.Start();
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
    }

    // 이전 버전의 코어가 업데이트 중 남아 있으면 새 코어와 동시에 패킷을 잡을 수 있다.
    // 같은 앱 폴더의 asgard-core* 프로세스만 정리해 새 코어 하나만 실행되게 한다.
    private static void StopStaleCoreProcesses()
    {
        var appDir = AppSettings.AppDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!p.ProcessName.StartsWith("asgard-core", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var path = p.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }
                var dir = Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.Equals(dir, appDir, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                p.Kill(entireProcessTree: true);
                p.WaitForExit(1500);
            }
            catch
            {
                // 이미 종료됐거나 접근할 수 없는 프로세스는 무시한다.
            }
        }
    }

    public void Stop()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(3000);
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        Stop();
        _proc?.Dispose();
        _proc = null;
    }
}
