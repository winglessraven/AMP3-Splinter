namespace Splinter.Host;

public static class ServerControlAudit
{
    // A useful public-facing name, never an opaque AMP Shard GUID.
    // Escape Discord formatting and mentions; never interpolate raw user input.
    public static string Message(ShardControlAction action, ulong userId, ShardBotConfig config,
        ShardSnapshot? snapshot)
        => Message(action.ToString(), userId, config, snapshot);

    public static string Message(string action, ulong userId, ShardBotConfig config,
        ShardSnapshot? snapshot)
    {
        string name = !string.IsNullOrWhiteSpace(config.ServerDisplayName)
            ? config.ServerDisplayName
            : !string.IsNullOrWhiteSpace(snapshot?.Name) ? snapshot.Name
            : !string.IsNullOrWhiteSpace(config.ShardName) ? config.ShardName : "this server";
        string escaped = Escape(name);
        return $"<@{userId}> requested {action} on **{escaped}**.";
    }

    private static string Escape(string name)
    {
        var safe = new System.Text.StringBuilder();
        foreach (char character in name.Trim().Take(120))
        {
            if (char.IsControl(character)) continue;
            if (character == '@') { safe.Append("@\u200b"); continue; }
            if (character is '*' or '_' or '~' or '`' or '|' or '\\' or '>' or '<')
                safe.Append('\\');
            safe.Append(character);
        }
        return safe.Length == 0 ? "this server" : safe.ToString();
    }
}
