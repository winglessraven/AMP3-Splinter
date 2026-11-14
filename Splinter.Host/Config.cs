using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Splinter.Host;

// Names intentionally match AMP-Discord-Bot 1.36's presence types.
public enum BotPresenceType
{
    CustomStatus,
    Playing,
    Watching,
    Listening,
    Competing
}

// Shared with Splinter.Admin through its linked Config.cs compilation unit.
public enum WhitelistPlayerIdType
{
    Username,
    SteamID
}

public enum InfoPanelStyle
{
    Classic,
    Pretty
}

public sealed class GlobalConfig
{
    public int SchemaVersion { get; set; } = 1;
    public int DiscoverEverySeconds { get; set; } = 5;
}

public sealed class ShardBotConfig
{
    public Guid ShardId { get; set; }
    public string ShardName { get; set; } = "";
    public bool Enabled { get; set; } = false;
    public ulong GuildId { get; set; } = 0;
    public ulong PanelChannelId { get; set; } = 0;
    public bool ShowPlayerCount { get; set; } = true;
    public string PresenceTemplate { get; set; } = "{ServerName}: {State}";
    public BotPresenceType PresenceType { get; set; } = BotPresenceType.CustomStatus;
    // Information panel options, based on the original AMP 2 plugin.
    // Discord destinations stay in PanelRegistry and are never typed into AMP.
    public string ServerDisplayName { get; set; } = "";
    public string BotTagline { get; set; } = "";
    public string ServerConnectionUrl { get; set; } = "";
    public string ModpackUrl { get; set; } = "";
    public string GameImageUrl { get; set; } = "";
    public string InfoPanelImageFile { get; set; } = "";
    // Opt-in PNG card. Empty override uses AMP's template artwork.
    public InfoPanelStyle PanelStyle { get; set; } = InfoPanelStyle.Pretty;
    public string PrettyPanelBackgroundFile { get; set; } = "";
    public string AdditionalEmbedFieldTitle { get; set; } = "";
    public string AdditionalEmbedFieldText { get; set; } = "";
    public string ManagementUrl { get; set; } = "";
    public bool DisplayManageButton { get; set; } = false;
    public string ServerPassword { get; set; } = "";
    public string OnlineEmbedColor { get; set; } = "#2ECC71";
    public string OfflineEmbedColor { get; set; } = "#E74C3C";
    public string TransitionEmbedColor { get; set; } = "#F1C40F";
    public bool EnableWebPanel { get; set; } = false;
    // Server control commands are restricted to an explicitly configured Discord role.
    public bool RestrictFunctionsToDiscordRole { get; set; } = true;
    public string DiscordRoleName { get; set; } = "";
    public bool DisplayStartButton { get; set; }
    public bool DisplayStopButton { get; set; }
    public bool DisplayRestartButton { get; set; }
    public bool DisplayKillButton { get; set; }
    public bool DisplayUpdateButton { get; set; }
    public bool DisplayBackupButton { get; set; }
    // AMP-Discord-Bot's Commands settings: independently disable slash commands.
    // Buttons are governed by their separate Display ... Button options.
    public bool EnableInfoCommand { get; set; } = true;
    public bool EnableStartCommand { get; set; } = true;
    public bool EnableStopCommand { get; set; } = true;
    public bool EnableRestartCommand { get; set; } = true;
    public bool EnableKillCommand { get; set; } = true;
    public bool EnableUpdateCommand { get; set; } = true;
    public bool EnableBackupCommand { get; set; } = true;
    public bool EnableConsoleCommand { get; set; } = true;
    public bool LogButtonPressesAndCommands { get; set; }
    public string ButtonLogChannel { get; set; } = "";

    public bool DisplayWhitelistRequestButton { get; set; }
    public string WhitelistRequestChannel { get; set; } = "";
    public string WhitelistApprovalRole { get; set; } = "";
    public string WhitelistAutoApproveRole { get; set; } = "";
    public string CustomWhitelistCommand { get; set; } = "";
    public WhitelistPlayerIdType WhitelistPlayerIdType { get; set; } = global::Splinter.Host.WhitelistPlayerIdType.Username;

    // Original AMP-Discord-Bot console, player and chat features. Disabled by default.
    public bool SendConsoleToDiscord { get; set; }
    public string ConsoleDiscordChannel { get; set; } = "";
    public int ConsoleToDiscordBatchTimer { get; set; } = 30;
    public string ExcludeConsoleOutput { get; set; } = "";
    public bool PostJoinLeaveMessages { get; set; }
    public string JoinLeaveMessagesChannel { get; set; } = "";
    public bool DisplayOnlinePlayerList { get; set; }
    public bool DisplayPlaytimeLeaderboard { get; set; }
    public bool EnablePlaytimeCommand { get; set; } = true;
    public bool EnableFullPlaytimeListCommand { get; set; } = true;
    public bool SendChatToDiscord { get; set; }
    // Empty retains the existing Minecraft relay pending verification of AMP's
    // console level mapping; an explicit value opts into game-neutral forwarding.
    public string ChatConsoleLevel { get; set; } = "";
    // Diagnostics sample metadata only and never persist console/chat contents.
    public bool TraceConsoleMetadata { get; set; }
    public bool SendChatFromDiscordToServer { get; set; }
    public string ChatDiscordChannel { get; set; } = "";
    public bool ValheimJoinCode { get; set; }

    // Server lifecycle notifications are read-only and disabled by default.
    public bool NotifyServerStatus { get; set; } = false;
    public int BotRefreshIntervalSeconds { get; set; } = 60;
    public string StatusNotificationChannel { get; set; } = "";
    public string OnlineStatusText { get; set; } = "";
    public string OfflineStatusText { get; set; } = "";

}

public static class ConfigStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Root
    {
        get
        {
            string? ampRoot = Environment.GetEnvironmentVariable("AMP3_ROOT");
            if (string.IsNullOrWhiteSpace(ampRoot))
                throw new InvalidOperationException("AMP3_ROOT is not set. Refusing to create config outside the AMP data root.");
            return Path.Combine(Path.GetFullPath(ampRoot), "splinter");
        }
    }

    public static string ConfigPath(Guid id) => Path.Combine(Root, "shards", $"{id:D}.json");
    public static string VariablesPath(Guid id) => Path.Combine(Root, "variables", id.ToString("D"), "BotVariables.json");
    public static string SecretPath(Guid id) => Path.Combine(Root, "secrets", $"{id:D}.amp");
    public static string StatePath(Guid id) => Path.Combine(Root, "state", $"{id:D}.json");
    public static string WebPanelPath(Guid id) => Path.Combine(Root, "web", $"{id:D}.html");
    public static string LifecyclePath(Guid id) => Path.Combine(Root, "lifecycle", $"{id:D}.json");
    public static string GlobalPath => Path.Combine(Root, "global.json");

    public static void EnsureFolders()
    {
        Directory.CreateDirectory(Root);
        foreach (string child in new[] { "shards", "secrets", "state", "lifecycle", "logs", "variables", "web", "console", "playtime", "whitelist", "snapshots" })
            Directory.CreateDirectory(Path.Combine(Root, child));
    }

    public static T Read<T>(string file, T fallback) where T : class
    {
        if (!File.Exists(file)) return fallback;
        return JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json) ?? fallback;
    }

    public static void Write<T>(string file, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json), Encoding.UTF8);
            File.Move(tmp, file, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    public static ShardBotConfig GetOrCreate(Guid shardId, string? name = null)
    {
        string file = ConfigPath(shardId);
        if (!File.Exists(file))
        {
            var initial = new ShardBotConfig { ShardId = shardId, ShardName = name ?? "", Enabled = false };
            // Atomic create prevents overwriting an administrator's changes.
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            try
            {
                using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(stream, initial, Json);
            }
            catch (IOException) when (File.Exists(file)) { }
        }
        var config = Read(file, new ShardBotConfig { ShardId = shardId });
        if (config.ShardId != shardId)
            throw new InvalidDataException($"Shard ID mismatch in config file {Path.GetFileName(file)}");
        return config;
    }
}
