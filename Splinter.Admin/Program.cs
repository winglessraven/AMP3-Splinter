using Splinter.Host;
using Splinter.Admin;

internal static class Program
{
    public static int Main(string[] args)
    {
        try { return Execute(args); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Execute(string[] args)
    {
        if (args.Length < 1 || args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Splinter administration utility\n" +
                "  inspect-amp-settings <path>   Inspect AMP assemblies without loading them or reading secrets\n" +
                "  inspect-amp-actions <path>    Inspect AMP Shard control method signatures (metadata only)\n" +
                "  inspect-amp-snapshots-events <path>   Inspect snapshot and player-event API signatures (metadata only)\n" +
                "  inspect-amp-chat-events <path>   Inspect chat/message event interfaces (metadata only)\n" +
                "  list                          List discovered Shard configurations\n" +
                "  enable <shard-guid>           Enable bot (native AMP token required)\n" +
                "  disable <shard-guid>          Disable bot\n" +
                "  panel <shard-guid> <id|0>     Legacy panel channel migration only; use /info in Discord\n" +
                "  presence <shard-guid> <text>  Presence; {ServerName}, {State}, {OnlinePlayers}, {MaximumPlayers}\n\n" +
                "Set AMP3_ROOT to your AMP installation data directory before using this tool.");
            return 0;
        }
        // Metadata inspection must work without AMP3_ROOT and must never create
        // Splinter configuration or touch token files.
        if (args[0].Equals("inspect-amp-settings", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length != 2) throw new ArgumentException("Specify the AMP 3 version directory or an AMP DLL path.");
            return AmpMetadataInspector.Run(args[1], Console.Out, Console.Error);
        }
        if (args[0].Equals("inspect-amp-actions", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length != 2) throw new ArgumentException("Specify the extracted AMP 3 assembly directory.");
            return AmpMetadataInspector.RunActions(args[1], Console.Out, Console.Error);
        }
        if (args[0].Equals("inspect-amp-snapshots-events", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length != 2) throw new ArgumentException("Specify the extracted AMP 3 assembly directory.");
            return AmpMetadataInspector.RunSnapshotEvents(args[1], Console.Out, Console.Error);
        }
        if (args[0].Equals("inspect-amp-chat-events", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length != 2) throw new ArgumentException("Specify extracted AMP 3 DLLs or a managed DLL path.");
            return AmpMetadataInspector.RunChatEvents(args[1], Console.Out, Console.Error);
        }
        ConfigStore.EnsureFolders();
        string verb = args[0].ToLowerInvariant();
        if (verb == "list")
        {
            var files = Directory.GetFiles(Path.Combine(ConfigStore.Root, "shards"), "*.json");
            if (files.Length == 0) Console.WriteLine("No Shards discovered yet. Launch AMP with the hosted module first.");
            foreach (string file in files.OrderBy(x => x))
            {
                var config = ConfigStore.Read(file, new ShardBotConfig());
                Console.WriteLine($"{config.ShardId:D}  {(config.Enabled ? "ENABLED " : "disabled")}  {config.ShardName}  token={(File.Exists(ConfigStore.SecretPath(config.ShardId)) ? "stored" : "missing")}");
            }
            return 0;
        }
        if (args.Length < 2 || !Guid.TryParse(args[1], out Guid id) || id == Guid.Empty)
            throw new ArgumentException("Specify a valid Shard GUID as the second argument. Use 'list' to see IDs.");
        string fileName = ConfigStore.ConfigPath(id);
        if (!File.Exists(fileName)) throw new FileNotFoundException("No config for that Shard; it must be discovered by AMP first.");
        ShardBotConfig settings = ConfigStore.GetOrCreate(id);
        switch (verb)
        {
            case "enable":
                if (!File.Exists(ConfigStore.SecretPath(id))) throw new InvalidOperationException("No AMP-protected token stored. Configure the Discord Bot Token in Shard → App Settings → Splinter first.");
                settings.Enabled = true;
                break;
            case "disable":
                settings.Enabled = false;
                break;
            case "panel":
                settings.PanelChannelId = ParseSnowflake(args);
                Console.WriteLine("Legacy migration: new information panels are created using /info in Discord.");
                break;
            case "presence":
                if (args.Length < 3 || string.IsNullOrWhiteSpace(args[2])) throw new ArgumentException("Provide a presence template.");
                settings.PresenceTemplate = string.Join(' ', args.Skip(2));
                break;
            default:
                throw new ArgumentException("Unknown command; run 'help' for supported commands.");
        }
        ConfigStore.Write(fileName, settings);
        Console.WriteLine($"Saved {verb} for Shard {id:D}. AMP will apply it on the next discovery cycle.");
        return 0;
    }

    private static ulong ParseSnowflake(string[] args)
    {
        if (args.Length < 3 || !ulong.TryParse(args[2], out ulong id))
            throw new ArgumentException("Specify a numeric Discord ID as the third argument.");
        return id;
    }
}
