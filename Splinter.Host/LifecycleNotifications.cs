using System.Text;

namespace Splinter.Host;

// Tracks actual AMP Shard status transitions, including transitional states.
// The first observation is a baseline. State is persisted before delivery so
// reconnects cannot repeatedly announce the same transition.
public static class LifecycleNotifications
{
    public sealed class State
    {
        public string? LastStatus { get; set; }
    }

    public sealed record Transition(string From, string To);

    public static Transition? Observe(Guid shardId, ShardSnapshot snapshot)
    {
        string? current = ValidStatus(snapshot.Status);
        if (current is null) return null;

        string file = ConfigStore.LifecyclePath(shardId);
        var state = ConfigStore.Read(file, new State());
        if (string.Equals(state.LastStatus, current, StringComparison.OrdinalIgnoreCase)) return null;

        string? previous = state.LastStatus;
        state.LastStatus = current;
        ConfigStore.Write(file, state);
        return previous is null ? null : new Transition(previous, current);
    }

    // Do not announce missing/invalid AMP state as a real status transition.
    // Accept future enum values without requiring an AMP beta-specific list.
    public static string? ValidStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        string value = status.Trim();
        if (value.Length > 64 || value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
            !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) return null;
        return value;
    }

    public static bool IsValidChannel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string channel = value.Trim().TrimStart('#');
        return channel.Length is > 0 and <= 100 &&
            channel.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiLetterUpper(c) ||
                              char.IsAsciiDigit(c) || c is '-' or '_');
    }

    public static bool MatchesChannel(string configured, ulong channelId, string name)
    {
        string normalized = configured.Trim().TrimStart('#');
        if (ulong.TryParse(normalized, out ulong snowflake)) return channelId == snowflake;
        return normalized.Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    public static string StatusLabel(string status) => status.ToLowerInvariant() switch
    {
        "runningready" or "running" => "Running",
        "terminated" or "stopped" => "Stopped",
        _ => SplitStatus(status)
    };

    private static string SplitStatus(string status)
    {
        var text = new StringBuilder(status.Length + 8);
        for (int i = 0; i < status.Length; i++)
        {
            char c = status[i];
            if (c is '_' or '-') { if (text.Length > 0 && text[text.Length - 1] != ' ') text.Append(' '); continue; }
            if (i > 0 && char.IsAsciiLetterUpper(c) && char.IsAsciiLetterLower(status[i - 1])) text.Append(' ');
            text.Append(c);
        }
        return text.ToString();
    }

    public static string Message(ShardBotConfig config, ShardSnapshot snapshot, Transition transition)
    {
        string name = string.IsNullOrWhiteSpace(config.ServerDisplayName) ? snapshot.Name : config.ServerDisplayName;
        var escaped = new StringBuilder();
        foreach (char c in name.Trim().Take(120))
        {
            if (char.IsControl(c)) continue;
            if (c is '*' or '_' or '~' or '`' or '|' or '\\' or '>' or '<' or '@') escaped.Append('\\');
            escaped.Append(c);
        }
        return $"🔔 **{escaped}** status changed: **{StatusLabel(transition.From)}** → **{StatusLabel(transition.To)}**.";
    }
}
