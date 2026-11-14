using Discord;
using System.Text.RegularExpressions;

namespace Splinter.Host;

public sealed class WhitelistRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Player { get; set; } = "";
    public ulong RequesterId { get; set; }
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WhitelistRequestState
{
    public List<WhitelistRequest> Requests { get; set; } = [];

    public static string PathFor(Guid shardId) =>
        Path.Combine(ConfigStore.Root, "whitelist", shardId.ToString("D") + ".json");
    public static WhitelistRequestState Load(Guid shardId) =>
        ConfigStore.Read(PathFor(shardId), new WhitelistRequestState());
    public void Save(Guid shardId) => ConfigStore.Write(PathFor(shardId), this);
}

public static class WhitelistRules
{
    private static readonly Regex Username = new(@"^[A-Za-z0-9_]{3,16}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Steam64 = new(@"^7656119\d{10}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool ValidPlayer(string? player, WhitelistPlayerIdType type) =>
        player is not null && (type == WhitelistPlayerIdType.Username
            ? Username.IsMatch(player) : Steam64.IsMatch(player));

    public static bool ValidTemplate(string? template) =>
        template is not null && template.Length <= 200 &&
        !template.Any(char.IsControl) && !template.Contains(';') &&
        !template.Contains('`') && !template.StartsWith('/') &&
        template.Replace("{player}", "", StringComparison.OrdinalIgnoreCase).IndexOfAny(new[] { '{', '}' }) < 0;

    public static string BuildCommand(string template, string player, WhitelistPlayerIdType type)
    {
        if (!ValidPlayer(player, type) || !ValidTemplate(template))
            throw new ArgumentException("Invalid player ID or whitelist command template.");
        string value = string.IsNullOrWhiteSpace(template) ? "whitelist add" : template.Trim();
        string command = value.Contains("{player}", StringComparison.OrdinalIgnoreCase)
            ? value.Replace("{player}", player, StringComparison.OrdinalIgnoreCase)
            : value + " " + player;
        if (!ShardConsoleBridge.IsValidCommand(command))
            throw new ArgumentException("Whitelist command is invalid.");
        return command;
    }

    public static bool HasRole(IEnumerable<(ulong Id, string Name)> roles, string configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return false;
        string reference = configured.Trim();
        return roles.Any(r => (ulong.TryParse(reference, out ulong roleId) && r.Id == roleId) ||
                              string.Equals(r.Name, reference, StringComparison.OrdinalIgnoreCase));
    }
}

// Same request message is edited in place as a moderator reviews it.
// Rendering stays independent of the Discord gateway for regression testing.
public static class WhitelistRequestEmbed
{
    public static Embed Render(WhitelistRequest request, string serverName, string? reviewer = null)
    {
        string status = request.Status switch
        {
            "Approved" => "Approved",
            "Denied" => "Denied",
            _ => "Pending"
        };
        var colour = status switch
        {
            "Approved" => Color.Green,
            "Denied" => Color.Red,
            _ => Color.Orange
        };
        string name = string.IsNullOrWhiteSpace(serverName) ? "Game Server" : serverName;
        name = Safe(name, 120);
        var embed = new EmbedBuilder()
            .WithTitle("Whitelist Request")
            .WithColor(colour)
            .AddField("Server", name, inline: false)
            .AddField("Player", "`" + CodePlayer(request.Player, 32) + "`", inline: true)
            .AddField("Requested by", $"<@{request.RequesterId}>", inline: true)
            .AddField("Status", status, inline: true)
            .WithTimestamp(request.RequestedAt);
        if (status != "Pending" && !string.IsNullOrWhiteSpace(reviewer))
            embed.AddField("Reviewed by", Safe(reviewer, 80), inline: false);
        return embed.Build();
    }

    // The player ID is already enclosed in inline-code delimiters. Escaping
    // underscores there changes the visible username (Steve_123 -> Steve\_123).
    private static string CodePlayer(string value, int limit) =>
        new string(value.Where(c => !char.IsControl(c)).Take(limit).ToArray())
            .Replace("`", "ˋ", StringComparison.Ordinal)
            .Replace("@", "@\u200b", StringComparison.Ordinal);

    private static string Safe(string value, int limit)
    {
        string clean = new(value.Where(c => !char.IsControl(c)).Take(limit).ToArray());
        return clean.Replace("@", "@\u200b", StringComparison.Ordinal)
            .Replace("`", "ˋ", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
