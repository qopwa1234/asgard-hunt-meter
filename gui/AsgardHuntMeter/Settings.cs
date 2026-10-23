using System.Text.Json;

namespace AsgardHuntMeter;

/// <summary>GUI 설정. %APPDATA%\AsgardHuntMeter\settings.json 에 저장 (데이터 보존 정책).</summary>
public sealed class AppSettings
{
    // Managed defaults. These are intentionally fixed in the UI.
    public const string DefaultHost = "210.101.85.79";
    public const int DefaultPort = 45017;
    public const string DefaultCommunityUrl = "https://asgard-community.qopwa1234.workers.dev";

    public static string AppDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    public static string ManagedCoreFileName =>
        $"asgard-core-{typeof(AppSettings).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}.exe";

    public static string ManagedCorePath => Path.Combine(AppDirectory, ManagedCoreFileName);

    public string Host { get; set; } = DefaultHost;
    public int Port { get; set; } = DefaultPort;
    public string Key { get; set; } = "";
    public string CorePath { get; set; } = ManagedCorePath;
    public bool AutoStart { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public string ChatKeywords { get; set; } = "";
    public bool KeywordAlert { get; set; } = true;
    public bool WhisperAlwaysAlert { get; set; } = true;
    public bool ShowSystemMessages { get; set; }
    public int BuffWarnMinutes { get; set; } = 5;
    public string DiscordWebhook { get; set; } = "";
    public string TelegramToken { get; set; } = "";
    public string TelegramChatId { get; set; } = "";
    public int InventorySlots { get; set; } = 832;
    public int InventoryWarnFree { get; set; } = 10;
    public string HuntTag { get; set; } = "";
    public long TargetGph { get; set; }
    public int PipOpacity { get; set; } = 85;
    public int PipFontSize { get; set; } = 10;
    public bool PipTopMost { get; set; } = true;
    public bool PipOnStart { get; set; }
    public bool AutoExpense { get; set; } = true;
    public long PotionUnitCost { get; set; }
    public long FoodUnitCost { get; set; }
    public string CommunityUrl { get; set; } = DefaultCommunityUrl;
    public string CommunityWorld { get; set; } = "";
    public string CommunityNickname { get; set; } = "";
    public string CommunityToken { get; set; } = "";
    public bool CommunityAutoSubmit { get; set; } = true;
    public string DonationUrl { get; set; } = "";
    public bool CheckUpdatesOnStart { get; set; } = true;

    public static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AsgardHuntMeter");

    public static string SettingsFile => Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile));
                if (s != null)
                {
                    s.NormalizeManagedDefaults();
                    return s;
                }
            }
        }
        catch
        {
            // 손상된 설정 파일은 무시하고 기본값 사용
        }
        return new AppSettings();
    }

    private void NormalizeManagedDefaults()
    {
        // These values are part of the managed service configuration.
        Host = DefaultHost;
        Port = DefaultPort;
        CommunityUrl = DefaultCommunityUrl;
        CorePath = ManagedCorePath;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsFile,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
}
