using System.Text.RegularExpressions;
using System.Globalization;
using Discord;

namespace Splinter.Host;

// Pure presentation logic, independently testable without the AMP host or Discord connection.
public static class InfoPanelRenderer
{
    public sealed record PanelView(Embed Embed, MessageComponent? Components, bool ImageOnly = false);

    // Discord can attach an image and show interactive components without an
    // embed. Explicitly send an empty embed array on edits to remove the old
    // Classic embed when a tracked panel changes to Pretty.
    public static Embed[] MessageEmbeds(PanelView view) =>
        view.ImageOnly ? [] : [view.Embed];

    private static readonly Regex LabelledLink = new(
        @"^\[(?<label>[^\]\r\n]{1,128})\]\((?<url>[^()\r\n]{1,1024})\)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static PanelView Render(ShardBotConfig config, ShardSnapshot snapshot,
        Func<ShardControlAction, bool>? actionAvailable = null,
        string? playtimeLeaderboard = null, string? valheimJoinCode = null,
        string? imageAttachmentName = null)
    {
        string title = string.IsNullOrWhiteSpace(config.ServerDisplayName)
            ? snapshot.Name : config.ServerDisplayName.Trim();
        bool transitioning = IsTransitionStatus(snapshot);
        string status = transitioning ? LifecycleNotifications.StatusLabel(snapshot.Status) :
            snapshot.IsRunning ? Nonempty(config.OnlineStatusText, DefaultStatus(snapshot)) :
            Nonempty(config.OfflineStatusText, DefaultStatus(snapshot));
        string icon = transitioning ? "🟡" : snapshot.IsRunning ? "🟢" : "🔴";
        // Match AMP-Discord-Bot's spacious field hierarchy. The game image
        // remains a thumbnail; we retain Splinter's status and resource icons.
        var embed = new EmbedBuilder()
            .WithTitle("Server Info")
            .WithColor(PanelColor(transitioning ? config.TransitionEmbedColor :
                snapshot.IsRunning ? config.OnlineEmbedColor : config.OfflineEmbedColor,
                transitioning ? 0xF1C40Fu : snapshot.IsRunning ? 0x2ECC71u : 0xE74C3Cu))
            .WithCurrentTimestamp();

        embed.AddField("📡 Server Status", $"{icon} {EscapeText(status)}", inline: false);
        embed.AddField("🎮 Server Name", BlockCode(title, 256), inline: false);
        if (!string.IsNullOrWhiteSpace(config.ServerConnectionUrl))
            embed.AddField("🌐 Server IP", BlockCode(config.ServerConnectionUrl.Trim(), 256), inline: false);

        // AMP 2's resource strip is CPU | Memory | Players, in that order.
        // Retain the AMP 3 metrics and distinguish missing data from zero.
        if (snapshot.IsRunning)
        {
            embed.AddField("🖥️ CPU", snapshot.CpuPercent is double cpu
                ? $"{cpu:0.#}%" : "Not reported", inline: true);
            string ram = snapshot.MemoryMb is double used
                ? $"{used:0.#} MB" : "Not reported";
            if (snapshot.MemoryLimitMb is double limit && limit > 0)
                ram += $" / {limit:0.#} MB";
            embed.AddField("🧠 RAM", ram, inline: true);
        }

        if (config.ShowPlayerCount)
        {
            string count = snapshot.Players.HasValue
                ? snapshot.MaxPlayers.HasValue
                    ? $"{snapshot.Players.Value} / {snapshot.MaxPlayers.Value}"
                    : snapshot.Players.Value.ToString()
                : snapshot.MaxPlayers.HasValue ? $"Not reported / {snapshot.MaxPlayers.Value}" : "Not reported by AMP";
            embed.AddField("👥 Players", count, inline: true);
        }

        if (!string.IsNullOrWhiteSpace(config.ModpackUrl))
            embed.AddField("📦 Modpack", FormatModpack(config.ModpackUrl), inline: true);

        if (!string.IsNullOrWhiteSpace(config.ServerPassword))
            embed.AddField("🔑 Server Password", "`" + Code(config.ServerPassword.Trim(), 256) + "`", inline: true);

        if (config.DisplayOnlinePlayerList && snapshot.IsRunning)
        {
            string names = snapshot.OnlinePlayers is null ? "Not reported by AMP" :
                snapshot.OnlinePlayers.Count == 0 ? "No players online" :
                string.Join("\n", snapshot.OnlinePlayers
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => EscapeText(p.Name)));
            embed.AddField("🎮 Online Players", Limit(names, 1024), inline: false);
        }
        if (config.DisplayPlaytimeLeaderboard)
            embed.AddField("🏆 Top 5 Players by Play Time",
                BlockCode(playtimeLeaderboard ?? "No playtime recorded yet.", 1000), inline: false);
        if (config.ValheimJoinCode && !string.IsNullOrWhiteSpace(valheimJoinCode))
            embed.AddField("🔑 Valheim Join Code", "`" + Code(valheimJoinCode, 32) + "`", inline: false);

        if (!string.IsNullOrWhiteSpace(config.AdditionalEmbedFieldTitle) &&
            !string.IsNullOrWhiteSpace(config.AdditionalEmbedFieldText))
        {
            embed.AddField(Limit(config.AdditionalEmbedFieldTitle.Trim(), 256),
                Limit(config.AdditionalEmbedFieldText.Trim(), 1024));
        }

        // Pretty cards are uploaded as one image with the same live Discord
        // components. Avoid duplicating the metrics as embed text beneath it.
        if (config.PanelStyle == InfoPanelStyle.Pretty && !string.IsNullOrWhiteSpace(imageAttachmentName))
        {
            var classicFields = embed.Build().Fields;
            embed = new EmbedBuilder()
                .WithColor(PanelColor(transitioning ? config.TransitionEmbedColor :
                    snapshot.IsRunning ? config.OnlineEmbedColor : config.OfflineEmbedColor,
                    transitioning ? 0xF1C40Fu : snapshot.IsRunning ? 0x2ECC71u : 0xE74C3Cu));
            // Preserve optional Classic features below the card. Only the six
            // primary status/metric fields are represented by the PNG itself.
            var cardFields = new HashSet<string>(StringComparer.Ordinal) {
                "📡 Server Status", "🎮 Server Name", "🌐 Server IP", "🖥️ CPU", "🧠 RAM", "👥 Players"
            };
            foreach (var field in classicFields)
            {
                if (!cardFields.Contains(field.Name))
                    embed.AddField(field.Name, field.Value, field.Inline);
            }
        }
        if (!(config.PanelStyle == InfoPanelStyle.Pretty && !string.IsNullOrWhiteSpace(imageAttachmentName)) &&
            TryWebUrl(config.GameImageUrl, out string thumbnail))
            embed.WithThumbnailUrl(thumbnail);
        // Existing full-sized local image is Classic-only. Pretty uses this
        // slot for the generated card while preserving the attachment protocol.
        if (!string.IsNullOrWhiteSpace(imageAttachmentName))
            embed.WithImageUrl("attachment://" + imageAttachmentName);

        if (!string.IsNullOrWhiteSpace(config.BotTagline))
            embed.WithFooter(Limit(config.BotTagline.Trim(), 2048));

        MessageComponent? components = null;
        var buttons = new ComponentBuilder();
        bool hasButtons = false;
        int buttonIndex = 0;
        foreach (ShardControlAction action in Enum.GetValues<ShardControlAction>())
        {
            if (!ServerControlPermissions.ShowButton(config, action)) continue;
            // Keep configured buttons visible but greyed out when AMP has no
            // binding or the current server state makes the action invalid.
            bool enabled = ControlEnabled(snapshot, action, actionAvailable?.Invoke(action) ?? true);
            buttons.WithButton(action.ToString(), customId: "splinter:control:" + action.ToString().ToLowerInvariant(),
                style: ControlStyle(action), disabled: !enabled, row: buttonIndex++ / 5);
            hasButtons = true;
        }
        if (config.DisplayWhitelistRequestButton)
        {
            buttons.WithButton("Whitelist Request", customId: "splinter:whitelist:request",
                style: ButtonStyle.Primary,
                disabled: !WhitelistReady(config), row: buttonIndex++ / 5);
            hasButtons = true;
        }
        if (config.DisplayManageButton && TryWebUrl(config.ManagementUrl, out string management))
        {
            buttons.WithButton("Manage", style: ButtonStyle.Link, url: management,
                row: buttonIndex++ / 5);
            hasButtons = true;
        }
        if (hasButtons) components = buttons.Build();

        return new PanelView(embed.Build(), components,
            config.PanelStyle == InfoPanelStyle.Pretty && !string.IsNullOrWhiteSpace(imageAttachmentName));
    }

    // Match the original panel's semantic Discord colours: green for Start
    // and Backup, red for Stop and Kill, blue for Restart and Update.
    public static ButtonStyle ControlStyle(ShardControlAction action) => action switch
    {
        ShardControlAction.Start or ShardControlAction.Backup => ButtonStyle.Success,
        ShardControlAction.Stop or ShardControlAction.Kill => ButtonStyle.Danger,
        ShardControlAction.Restart or ShardControlAction.Update => ButtonStyle.Primary,
        _ => ButtonStyle.Secondary
    };

    public static bool WhitelistReady(ShardBotConfig config) =>
        LifecycleNotifications.IsValidChannel(config.WhitelistRequestChannel) &&
        !string.IsNullOrWhiteSpace(config.WhitelistApprovalRole);

    private static bool IsStoppedStatus(string? status) =>
        status is not null && new[] { "Terminated", "Stopped", "Offline", "Inactive", "NotRunning", "Ready" }
            .Contains(status, StringComparer.OrdinalIgnoreCase);

    // A stale Discord button may still be clicked before the panel refreshes.
    // This policy is also applied inside BotSession before dispatch to AMP.
    public static bool ControlEnabled(ShardSnapshot snapshot, ShardControlAction action, bool ampAvailable = true)
    {
        if (!ampAvailable || LifecycleNotifications.ValidStatus(snapshot.Status) is null ||
            IsTransitionStatus(snapshot)) return false;
        bool stopped = !snapshot.IsRunning && IsStoppedStatus(snapshot.Status);
        return action switch
        {
            ShardControlAction.Start => stopped,
            ShardControlAction.Stop or ShardControlAction.Restart or ShardControlAction.Kill => snapshot.IsRunning,
            // AMP 3 supports in-place updates when the agent allows it.
            // Leave native lifecycle/snapshot preflight as the final authority.
            ShardControlAction.Update or ShardControlAction.Backup => snapshot.IsRunning || stopped,
            _ => false
        };
    }

    public static bool IsTransitionStatus(ShardSnapshot snapshot)
    {
        string status = snapshot.Status;
        return new[] { "Starting", "Stopping", "Restarting", "Installing", "Updating",
            "Pending", "Downloading", "Extracting", "Restoring", "BackingUp", "Preparing", "Migrating" }
            .Any(part => status.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryModpackUrl(string? value, out string url)
    {
        url = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        string input = value.Trim();
        Match match = LabelledLink.Match(input);
        return TryWebUrl(match.Success ? match.Groups["url"].Value : input, out url);
    }

    public static bool TryParseColor(string? value, out uint color)
    {
        color = 0;
        if (value is null || value.Length != 7 || value[0] != '#') return false;
        return uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out color);
    }

    private static Color PanelColor(string? configured, uint fallback) =>
        new(TryParseColor(configured, out uint parsed) ? parsed : fallback);

    // Blank, plain text, a bare URL, or [visible label](https://url).
    // Existing ModpackUrl JSON data works unchanged; the name is only a legacy key.
    public static string FormatModpack(string value)
    {
        string trimmed = value.Trim();
        Match match = LabelledLink.Match(trimmed);
        if (match.Success && TryWebUrl(match.Groups["url"].Value, out string link))
            return $"[{EscapeLinkLabel(match.Groups["label"].Value)}]({SafeMarkdownUrl(link)})";
        if (TryWebUrl(trimmed, out string url))
            return $"[Open modpack]({SafeMarkdownUrl(url)})";
        return Limit(EscapeText(trimmed), 1024);
    }

    public static bool TryWebUrl(string? value, out string result)
    {
        result = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Any(char.IsControl))
            return false;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            return false;
        result = uri.AbsoluteUri;
        return true;
    }

    private static string SafeMarkdownUrl(string value) => value.Replace("(", "%28").Replace(")", "%29");
    private static string EscapeLinkLabel(string value) =>
        value.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]")
            .Replace("*", "\\*").Replace("`", "\\`");
    private static string DefaultStatus(ShardSnapshot snapshot)
    {
        if (snapshot.IsRunning) return "Online";
        // AMP 3 uses "Terminated" for a normally stopped game process.
        // Discord players should see the meaningful state, not the process label.
        if (new[] { "Stopped", "Terminated", "Offline", "Inactive", "NotRunning" }
            .Contains(snapshot.Status, StringComparer.OrdinalIgnoreCase))
            return "Offline";
        if (snapshot.Status.Contains("Starting", StringComparison.OrdinalIgnoreCase)) return "Starting";
        if (snapshot.Status.Contains("Stopping", StringComparison.OrdinalIgnoreCase)) return "Stopping";
        return snapshot.Status;
    }
    private static string Nonempty(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    private static string EscapeText(string value) =>
        value.Replace("\\", "\\\\").Replace("*", "\\*").Replace("`", "\\`")
            .Replace("_", "\\_").Replace("[", "\\[").Replace("]", "\\]")
            .Replace("@", "@\u200b").Replace("\n", " ").Replace("\r", " ");
    private static string BlockCode(string value, int length) =>
        "```text\n" + Code(value, length) + "\n```";
    private static string Code(string value, int length) =>
        Limit(value.Replace("`", "ˋ").Replace("\n", "").Replace("\r", ""), length);
    private static string Limit(string value, int length) =>
        value.Length <= length ? value : value[..length];
}
