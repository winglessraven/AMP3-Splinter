using System.Text.Json.Serialization;

namespace Splinter.Host;

// One managed information panel per Discord channel, per Shard. A panel is
// created only by /info. Periodic status updates NEVER create messages.
public sealed class PanelLocation
{
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }
    public string? ImageHash { get; set; }
    public string? ImageFileName { get; set; }
}

public sealed class PanelRegistryState
{
    public List<PanelLocation> Panels { get; set; } = [];

    // Fields from the early one-panel implementation; migrated on next save.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ulong ChannelId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ulong MessageId { get; set; }

    [JsonIgnore]
    public bool RequiresSave { get; set; }
}

public static class PanelRegistry
{
    public static PanelRegistryState Load(Guid shardId, ShardBotConfig config)
    {
        PanelRegistryState state = ConfigStore.Read(ConfigStore.StatePath(shardId), new PanelRegistryState());
        state.Panels ??= [];
        if (state.ChannelId != 0 || state.MessageId != 0)
        {
            state.RequiresSave = true;
            // Only migrate a message we actually know exists. A legacy *channel*
            // without a message ID is not permission to post a new panel.
            if (state.ChannelId != 0 && state.MessageId != 0 &&
                !state.Panels.Any(p => p.ChannelId == state.ChannelId && p.MessageId == state.MessageId))
                state.Panels.Insert(0, new PanelLocation { ChannelId = state.ChannelId, MessageId = state.MessageId });
        }

        // PanelChannelId is a legacy configuration property retained for JSON
        // compatibility only. Never turn it into a message to publish at startup.
        // Deduplicate older installations which could track several panels in
        // the same channel. The last known message wins; don't delete Discord
        // messages automatically (users can clean up older duplicates manually).
        var latestByChannel = new Dictionary<ulong, PanelLocation>();
        foreach (PanelLocation? panel in state.Panels)
        {
            if (panel is null || panel.ChannelId == 0 || panel.MessageId == 0)
            {
                state.RequiresSave = true;
                continue;
            }
            if (latestByChannel.ContainsKey(panel.ChannelId)) state.RequiresSave = true;
            latestByChannel[panel.ChannelId] = panel;
        }
        state.Panels = latestByChannel.Values.ToList();
        return state;
    }

    public static void Save(Guid shardId, PanelRegistryState state)
    {
        state.ChannelId = 0;
        state.MessageId = 0;
        state.RequiresSave = false;
        ConfigStore.Write(ConfigStore.StatePath(shardId), state);
    }

    public static void Track(PanelRegistryState state, ulong guildId, ulong channelId, ulong messageId)
    {
        if (channelId == 0 || messageId == 0)
            throw new ArgumentOutOfRangeException(nameof(messageId), "A real Discord message and channel are required.");
        PanelLocation? existing = state.Panels.FirstOrDefault(p => p.ChannelId == channelId);
        if (existing is not null)
        {
            existing.GuildId = guildId != 0 ? guildId : existing.GuildId;
            existing.MessageId = messageId;
        }
        else
            state.Panels.Add(new PanelLocation { GuildId = guildId, ChannelId = channelId, MessageId = messageId });
        state.RequiresSave = true;
    }
}
