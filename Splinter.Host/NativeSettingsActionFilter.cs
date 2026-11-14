using System.Collections;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Splinter.Host;

// Experimental AMP 3.0.0 adapter; deliberately separate from the Discord bot.
// No game AppSpec/template changes and no modification of AMP's installed JS.
internal static class NativeSettingsPreview
{
    private const string FlagFile = "native-settings-preview.enabled";

    public static bool Enabled
    {
        get
        {
            try { return File.Exists(Path.Combine(ConfigStore.Root, FlagFile)); }
            catch (Exception ex)
            {
                BotLog.Write("Native-settings preview not enabled: " + AmpFacade.CleanError(ex));
                return false;
            }
        }
    }
}

public sealed class NativeSettingsActionFilter : IAsyncActionFilter
{
    // UI-only placeholder; never persisted and never accepted as a new token.
    internal const string ConfiguredTokenMask = "********";
    private const string Prefix = "Splinter.";
    private const string Category = "Splinter:smart_toy";
    private static readonly SemaphoreSlim Writes = new(1, 1);

    private sealed record SettingField(
        string Key, string Subcategory, string DisplayName, string Description,
        string InputType, int Order, IReadOnlyDictionary<string, string>? EnumValues = null, bool Secret = false);

    // AMP's native enum control: keys are persisted configuration values,
    // while the labels are displayed by AMP's settings renderer.
    private static readonly IReadOnlyDictionary<string, string> PresenceTypeOptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(BotPresenceType.CustomStatus)] = "Custom Status",
            [nameof(BotPresenceType.Playing)] = "Playing",
            [nameof(BotPresenceType.Watching)] = "Watching",
            [nameof(BotPresenceType.Listening)] = "Listening",
            [nameof(BotPresenceType.Competing)] = "Competing"
        };

    private static readonly IReadOnlyDictionary<string, string> PanelStyleOptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(InfoPanelStyle.Classic)] = "Classic (Discord embed)",
            [nameof(InfoPanelStyle.Pretty)] = "Pretty (rendered image)"
        };

    private static readonly IReadOnlyDictionary<string, string> WhitelistIdOptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(WhitelistPlayerIdType.Username)] = "Username",
            [nameof(WhitelistPlayerIdType.SteamID)] = "SteamID"
        };

    // These are user-facing settings. Guild membership is discovered through Discord,
    // and panel destinations are captured by Discord commands rather than typed into AMP.
    // Password changes are intercepted here; no token is passed to AMP Shard configuration.
    private static readonly SettingField[] Fields =
    [
        new("Splinter.DiscordToken", "01 Bot Setup", "Discord Bot Token",
            "Set or replace this Shard's token. Asterisks indicate an existing token; leave them unchanged to keep it.",
            "password", 101, Secret: true),
        new("Splinter.Enabled", "01 Bot Setup", "Enable Discord Bot",
            "Keep this Shard's Discord bot connected, even while the game server is stopped. Requires an existing Discord token.",
            "checkbox", 102),
        new("Splinter.BotRefreshIntervalSeconds", "01 Bot Setup", "Bot Refresh Interval",
            "Seconds between presence and information-panel refreshes; minimum 30, maximum 300. Status notifications are not delayed.",
            "text", 103),
        new("Splinter.PresenceType", "01 Bot Setup", "Bot Presence Type",
            "Choose how the bot activity appears in Discord.",
            "enum", 104, PresenceTypeOptions),
        new("Splinter.PresenceTemplate", "01 Bot Setup", "Bot Presence Text",
            "Supports {ServerName}, {State}, {OnlinePlayers}, {MaximumPlayers} and custom Shard BotVariables.json placeholders.",
            "text", 105),
        new("Splinter.ServerDisplayName", "02 Info Panel - Basics", "Server Display Name",
            "The name displayed in the Discord information panel. Blank uses the Shard name.",
            "text", 201),
        new("Splinter.ServerConnectionUrl", "02 Info Panel - Basics", "Server Connection URL",
            "Address players can use to join the server.",
            "text", 202),
        new("Splinter.ServerPassword", "02 Info Panel - Basics", "Server Password",
            "Optional password to display publicly in the information panel for players. Do not enter administrative credentials.",
            "text", 203),
        new("Splinter.ModpackUrl", "02 Info Panel - Basics", "Modpack",
            "Enter plain text or a URL. For a named link, put the display name in square brackets followed immediately by the URL in parentheses.",
            "text", 204),
        new("Splinter.BotTagline", "02 Info Panel - Basics", "Bot Tagline",
            "Text displayed in the information panel footer.",
            "text", 205),
        new("Splinter.ShowPlayerCount", "02 Info Panel - Basics", "Display Player Count",
            "Show the online player count when AMP reports one.",
            "checkbox", 206),
        new("Splinter.DisplayOnlinePlayerList", "02 Info Panel - Basics", "Display Online Player List",
            "Show connected player names reported by AMP in the information panel.", "checkbox", 207),
        new("Splinter.DisplayPlaytimeLeaderboard", "02 Info Panel - Basics", "Display Playtime Leaderboard",
            "Show accumulated player sessions in the information panel.", "checkbox", 208),
        new("Splinter.OnlineStatusText", "02 Info Panel - Basics", "Running Status Text",
            "Optional replacement for the status shown while the game server is running.",
            "text", 209),
        new("Splinter.OfflineStatusText", "02 Info Panel - Basics", "Stopped Status Text",
            "Optional replacement for the status shown while the game server is stopped.",
            "text", 210),
        new("Splinter.PanelStyle", "03 Info Panel - Images and Links", "Panel Style",
            "Classic preserves the existing embed. Pretty creates a status-card PNG with normal Discord buttons.",
            "enum", 300, PanelStyleOptions),
        new("Splinter.GameImageUrl", "03 Info Panel - Images and Links", "Game Image URL",
            "Public HTTPS or HTTP image URL used as the panel thumbnail.",
            "text", 301),
        new("Splinter.InfoPanelImageFile", "03 Info Panel - Images and Links", "Info Panel Image File",
            "Optional PNG, JPG, GIF or WEBP image relative to the Shard application folder. Used as the panel large image.",
            "text", 302),
        new("Splinter.PrettyPanelBackgroundFile", "03 Info Panel - Images and Links", "Pretty Panel Background Override",
            "Optional relative PNG/JPG/WEBP file inside the Shard application folder. Blank uses AMP's template artwork.",
            "text", 303),
        new("Splinter.ManagementUrl", "03 Info Panel - Images and Links", "Management URL",
            "Full HTTPS or HTTP URL for the optional Manage button.",
            "text", 304),
        new("Splinter.DisplayManageButton", "03 Info Panel - Images and Links", "Display Manage Button",
            "Add a Manage link button when a management URL is configured.",
            "checkbox", 305),
        new("Splinter.AdditionalEmbedFieldTitle", "04 Info Panel - Extras and Colours", "Additional Embed Field Title",
            "Optional extra panel field title.",
            "text", 401),
        new("Splinter.AdditionalEmbedFieldText", "04 Info Panel - Extras and Colours", "Additional Embed Field Text",
            "Optional extra panel field content.",
            "text", 402),
        new("Splinter.OnlineEmbedColor", "04 Info Panel - Extras and Colours", "Online Embed Colour",
            "Seven-character hexadecimal colour, e.g. #2ECC71.", "text", 403),
        new("Splinter.OfflineEmbedColor", "04 Info Panel - Extras and Colours", "Offline Embed Colour",
            "Seven-character hexadecimal colour, e.g. #E74C3C.", "text", 404),
        new("Splinter.TransitionEmbedColor", "04 Info Panel - Extras and Colours", "Transition Embed Colour",
            "Seven-character hexadecimal colour, e.g. #F1C40F.", "text", 405),
        new("Splinter.RestrictFunctionsToDiscordRole", "05 Server Controls", "Restrict Functions to Discord Role",
            "Only members of the configured Discord role may control this Shard from Discord.", "checkbox", 501),
        new("Splinter.DiscordRoleName", "05 Server Controls", "Discord Role Name",
            "Role name or ID authorised to use server-management commands and buttons.", "text", 502),
        new("Splinter.DisplayStartButton", "05 Server Controls", "Display Start Button",
            "Show the Start button on the information panel.", "checkbox", 503),
        new("Splinter.DisplayStopButton", "05 Server Controls", "Display Stop Button",
            "Show the Stop button on the information panel.", "checkbox", 504),
        new("Splinter.DisplayRestartButton", "05 Server Controls", "Display Restart Button",
            "Show the Restart button on the information panel.", "checkbox", 505),
        new("Splinter.DisplayKillButton", "05 Server Controls", "Display Kill Button",
            "Show the Kill button on the information panel.", "checkbox", 506),
        new("Splinter.DisplayUpdateButton", "05 Server Controls", "Display Update Button",
            "Show the Update button on the information panel.", "checkbox", 507),
        new("Splinter.DisplayBackupButton", "05 Server Controls", "Display Backup Button",
            "Show the Backup button on the information panel.", "checkbox", 508),
        new("Splinter.ButtonLogChannel", "05 Server Controls", "Button Log Channel (Name or ID)",
            "Where command usage is logged; blank means the interaction channel.", "text", 509),
        new("Splinter.LogButtonPressesAndCommands", "05 Server Controls", "Log Button Presses and Commands",
            "Send server-control audit messages to a configured Discord channel.", "checkbox", 510),
        new("Splinter.EnableInfoCommand", "06 Slash Commands", "Enable /info Command",
            "Register /info in Discord for this Shard. Does not affect information-panel buttons.", "checkbox", 601),
        new("Splinter.EnableStartCommand", "06 Slash Commands", "Enable /start-server Command",
            "Register /start-server in Discord for this Shard. Does not affect information-panel buttons.", "checkbox", 602),
        new("Splinter.EnableStopCommand", "06 Slash Commands", "Enable /stop-server Command",
            "Register /stop-server in Discord for this Shard. Does not affect information-panel buttons.", "checkbox", 603),
        new("Splinter.EnableRestartCommand", "06 Slash Commands", "Enable /restart-server Command",
            "Register /restart-server in Discord for this Shard. Does not affect information-panel buttons.", "checkbox", 604),
        new("Splinter.EnableKillCommand", "06 Slash Commands", "Enable /kill-server Command",
            "Register /kill-server in Discord for this Shard. Does not affect information-panel buttons.", "checkbox", 605),
        new("Splinter.EnableUpdateCommand", "06 Slash Commands", "Enable /update-server Command",
            "Register /update-server in Discord for this Shard. Does not affect information-panel buttons.", "checkbox", 606),
        new("Splinter.EnableBackupCommand", "06 Slash Commands", "Enable /take-backup Command",
            "Register /take-backup in Discord for this Shard. Does not affect information-panel buttons.", "checkbox", 607),
        new("Splinter.EnableConsoleCommand", "06 Slash Commands", "Enable /console Command",
            "Register the original /console command for role-authorised server console input.", "checkbox", 608),
        new("Splinter.EnablePlaytimeCommand", "06 Slash Commands", "Enable /playtime Command",
            "Show the top five players by recorded playtime.", "checkbox", 609),
        new("Splinter.EnableFullPlaytimeListCommand", "06 Slash Commands", "Enable /full-playtime-list Command",
            "Show a longer playtime list with last-seen dates.", "checkbox", 610),
        new("Splinter.ConsoleDiscordChannel", "07 Console Relay", "Console Discord Channel",
            "Channel name or ID for console output. Can be configured before or after enabling the relay.", "text", 701),
        new("Splinter.SendConsoleToDiscord", "07 Console Relay", "Send Console to Discord",
            "Forward game-console output once a valid channel is set; safe to enable first.", "checkbox", 702),
        new("Splinter.ConsoleToDiscordBatchTimer", "07 Console Relay", "Console to Discord Batch Timer",
            "Minimum seconds between Discord console-output batches (10-300).", "text", 703),
        new("Splinter.ExcludeConsoleOutput", "07 Console Relay", "Exclude Console Output",
            "One filter per line. An asterisk matches any characters.", "text", 704),
        new("Splinter.TraceConsoleMetadata", "07 Console Relay", "Inspect AMP Console Metadata",
            "Temporary opt-in diagnostics: log only level, kind, origin, stream ID and sequence for up to 120 new lines per Shard. " +
            "No console/chat text is logged. Turn off when finished; re-enable to reset the sample limit.", "checkbox", 705),
        new("Splinter.ChatDiscordChannel", "08 Chat Relay", "Chat Discord Channel",
            "Destination for chat relays. Can be set before enabling chat.", "text", 801),
        new("Splinter.SendChatToDiscord", "08 Chat Relay", "Send Chat to Discord",
            "Relay AMP lines classified CHAT; on current betas, fall back to Minecraft chat detection until CHAT is available.", "checkbox", 802),
        new("Splinter.ChatConsoleLevel", "08 Chat Relay", "AMP Chat Log Level (Experimental)",
            "Defaults to CHAT when blank, with the existing Minecraft fallback while AMP 3 beta labels chat INFO. " +
            "An explicit level uses only that AMP classification, with no game-specific parsing.", "text", 803),
        new("Splinter.SendChatFromDiscordToServer", "08 Chat Relay", "Send Chat from Discord to Server",
            "Forward messages in the selected Discord channel through the Minecraft say command. Requires Message Content intent.",
            "checkbox", 804),
        new("Splinter.JoinLeaveMessagesChannel", "09 Player Notifications", "Post Join and Leave Messages Channel (Name or ID)",
            "Destination for player join/leave messages; you may enable notifications before setting it.", "text", 901),
        new("Splinter.PostJoinLeaveMessages", "09 Player Notifications", "Post Join and Leave Messages",
            "Post join/leave messages once the destination channel is configured; safe to enable first.", "checkbox", 902),
        new("Splinter.StatusNotificationChannel", "10 Server Notifications", "Status Notification Channel (Name or ID)",
            "Channel name or ID for status notices. May be entered before or after enabling notifications.",
            "text", 1001),
        new("Splinter.NotifyServerStatus", "10 Server Notifications", "Post Server Status Change Messages",
            "Post messages for AMP Shard status changes, including Starting, Stopping, Installing and Updating. No message on initial discovery.",
            "checkbox", 1002),
        new("Splinter.WhitelistRequestChannel", "11 Whitelist", "Whitelist Request Channel",
            "Destination for whitelist approval requests. May be set after enabling the button.", "text", 1101),
        new("Splinter.WhitelistApprovalRole", "11 Whitelist", "Whitelist Approval Role",
            "Discord approval role. Requests stay inactive until both role and channel are configured.", "text", 1102),
        new("Splinter.WhitelistAutoApproveRole", "11 Whitelist", "Whitelist Auto Approve Role",
            "Optional role name or ID whose members are approved without staff review.", "text", 1103),
        new("Splinter.WhitelistPlayerIdType", "11 Whitelist", "Whitelist Player ID Type",
            "Choose Minecraft username or Steam64 ID.", "enum", 1104, WhitelistIdOptions),
        new("Splinter.CustomWhitelistCommand", "11 Whitelist", "Custom Whitelist Command",
            "Optional console command, with {player} as placeholder or player appended. Blank uses whitelist add.",
            "text", 1105),
        new("Splinter.DisplayWhitelistRequestButton", "11 Whitelist", "Display Whitelist Request Button",
            "Show a button; it remains disabled until a request channel and approval role are configured.", "checkbox", 1106),
        new("Splinter.EnableWebPanel", "12 Web Panel", "Enable Web Panel",
            "Write a standalone read-only HTML status panel for this Shard under the Splinter data directory. Web hosting must be configured separately.",
            "checkbox", 1201),
        new("Splinter.ValheimJoinCode", "13 Game Specific", "Valheim Join Code",
            "Display a Valheim join code when detected in console output.", "checkbox", 1301)
    ];

    private static readonly IReadOnlyDictionary<string, SettingField> FieldMap =
        Fields.ToDictionary(field => field.Key, StringComparer.OrdinalIgnoreCase);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!IsTarget(context, out Guid shardId, out string action))
        {
            await next();
            return;
        }

        if (action.Equals("GetSettings", StringComparison.Ordinal))
        {
            ActionExecutedContext completed = await next(); // AMP performs its usual settings.read check.
            if (completed.Exception != null || completed.Canceled ||
                completed.Result is not OkObjectResult ok || ok.Value is not IEnumerable original)
                return;

            try
            {
                var items = new List<object>();
                foreach (object? existing in original)
                    if (existing is not null) items.Add(existing);

                ShardBotConfig config = ReadConfig(shardId);
                // AMP's existing renderer builds category/subcategory navigation from metadata.
                // Neither AMP's AppSpec nor its bundled JS are changed.
                foreach (SettingField field in Fields)
                {
                    items.Add(new
                    {
                        Spec = new
                        {
                            FieldName = field.Key,
                            Category,
                            field.Subcategory,
                            field.DisplayName,
                            Description = field.Key == "Splinter.DiscordToken"
                                ? field.Description + (SecretStore.HasToken(shardId) ? " Token configured." : " No token configured.")
                                : field.Description,
                            field.InputType,
                            IsSecret = field.Secret,
                            field.EnumValues,
                            Platform = 7,
                            Deployment = 0,
                            Hidden = false,
                            ReadOnly = false,
                            field.Order
                        },
                        Value = ReadValue(config, field.Key)
                    });
                }
                ok.Value = items;
                ok.DeclaredType = null;
            }
            catch (Exception ex)
            {
                // An optional UI overlay must never take down AMP's own settings.
                BotLog.Write("Native-settings read overlay failed: " + AmpFacade.CleanError(ex));
            }
            return;
        }

        if (!action.Equals("PatchSettings", StringComparison.Ordinal) ||
            !context.ActionArguments.TryGetValue("changes", out object? argument) ||
            argument is not Dictionary<string, string> changes ||
            !changes.Keys.Any(k => k.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)))
        {
            await next();
            return;
        }

        // Accept atomic Splinter-only batches; never partially apply a mixed game/bot batch.
        // A caller cannot bypass validation by changing key casing or inventing new fields.
        if (changes.Count == 0 ||
            changes.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != changes.Count ||
            changes.Keys.Any(key => !FieldMap.ContainsKey(key)))
        {
            context.Result = new BadRequestObjectResult(new
            {
                error = "A Splinter update must contain only supported Splinter fields. Send game settings separately."
            });
            return;
        }

        // This action filter runs before AMP's ShardsController action. Its normal
        // authorization has NOT run yet, so reuse AMP's per-Shard verifier.
        try
        {
            (IActionResult? refusal, Guid spireId) = await VerifyShardWriteAsync(context, shardId);
            if (refusal is not null)
            {
                context.Result = refusal;
                return;
            }

            // Field-granular permission checks are required for EVERY changed field,
            // not just for the existing enable checkbox.
            foreach (string key in changes.Keys)
            {
                if (!await CanWriteSettingAsync(context, spireId, shardId, FieldMap[key]))
                {
                    context.Result = new ForbidResult();
                    return;
                }
            }

            // Validate the entire batch BEFORE modifying any configuration.
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach ((string key, string raw) in changes)
            {
                if (!TryParseValue(key, raw, out object? parsed, out string error))
                {
                    context.Result = new BadRequestObjectResult(new { error });
                    return;
                }
                values.Add(key, parsed!);
            }

            await Writes.WaitAsync(context.HttpContext.RequestAborted);
            try
            {
                string file = ConfigStore.ConfigPath(shardId);
                if (!File.Exists(file))
                {
                    context.Result = new NotFoundObjectResult(new { error = "Splinter configuration for this Shard was not discovered." });
                    return;
                }

                ShardBotConfig config = ConfigStore.GetOrCreate(shardId);
                // Channel-dependent switches are intentionally saveable before
                // the destination is entered (AMP PATCHes fields individually).
                // Each field is still validated independently above. Runtime
                // delivery and whitelist interactions fail closed until the
                // required destination/role has been configured.

                string? replacement = values.TryGetValue("Splinter.DiscordToken", out object? tokenValue)
                    ? (string)tokenValue : null;
                if (replacement == ConfiguredTokenMask)
                {
                    if (!SecretStore.HasToken(shardId))
                    {
                        context.Result = new BadRequestObjectResult(new { error = "No stored token exists to preserve." });
                        return;
                    }
                    replacement = null;
                }
                if (values.TryGetValue("Splinter.Enabled", out object? enabledValue) &&
                    (bool)enabledValue &&
                    string.IsNullOrEmpty(replacement) && !SecretStore.HasToken(shardId))
                {
                    context.Result = new ObjectResult(new
                    {
                        error = "Configure the Discord Bot Token in this Shard's Splinter settings before enabling the bot."
                    }) { StatusCode = StatusCodes.Status409Conflict };
                    return;
                }

                if (values.TryGetValue("Splinter.Enabled", out enabledValue) &&
                    (bool)enabledValue && string.IsNullOrEmpty(replacement) &&
                    string.IsNullOrEmpty(SecretStore.ReadToken(shardId, context.HttpContext.RequestServices)))
                {
                    context.Result = new ObjectResult(new { error = "A valid AMP-protected Discord token is required." })
                        { StatusCode = StatusCodes.Status409Conflict };
                    return;
                }

                // Empty or unchanged masked input preserves the AMP-protected credential.
                if (!string.IsNullOrEmpty(replacement))
                    SecretStore.WriteToken(shardId, replacement, context.HttpContext.RequestServices);

                var changedFields = new List<string>();
                bool previousEnabled = config.Enabled;
                foreach ((string key, object value) in values)
                {
                    if (FieldMap[key].Secret) continue;
                    if (Apply(config, key, value)) changedFields.Add(FieldMap[key].Key);
                }
                if (!string.IsNullOrEmpty(replacement)) changedFields.Add("Splinter.DiscordToken");

                if (changedFields.Count > 0)
                {
                    ConfigStore.Write(file, config); // Atomic replacement; supervisor reconciles on its next poll.
                    string actor = context.HttpContext.User.Identity?.Name ?? "unknown";
                    actor = actor.Replace('\r', ' ').Replace('\n', ' ');
                    string summary = string.Join(", ", changedFields);
                    if (previousEnabled != config.Enabled)
                        summary += $" (Enabled: {previousEnabled} -> {config.Enabled})";
                    // Never log field values: a future field might contain sensitive data.
                    BotLog.Write($"Native-settings updated for Shard {shardId:D}: {summary} (user={actor})");
                }
                context.Result = new NoContentResult();
            }
            finally { Writes.Release(); }
        }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex)
        {
            BotLog.Write("Native-settings write failed closed: " + AmpFacade.CleanError(ex));
            context.Result = new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static ShardBotConfig ReadConfig(Guid shardId)
    {
        // A GET should never create configuration before discovery or mutate AMP state.
        return File.Exists(ConfigStore.ConfigPath(shardId))
            ? ConfigStore.GetOrCreate(shardId)
            : new ShardBotConfig { ShardId = shardId };
    }

    private static string ReadValue(ShardBotConfig config, string key) => key switch
    {
        "Splinter.Enabled" => config.Enabled ? "true" : "false",
        "Splinter.DiscordToken" => SecretStore.HasToken(config.ShardId) ? ConfiguredTokenMask : "", // Mask only; never expose token.
        "Splinter.BotRefreshIntervalSeconds" => config.BotRefreshIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "Splinter.PresenceTemplate" => config.PresenceTemplate ?? "",
        "Splinter.PresenceType" => config.PresenceType.ToString(),
        "Splinter.ServerDisplayName" => config.ServerDisplayName ?? "",
        "Splinter.BotTagline" => config.BotTagline ?? "",
        "Splinter.ServerConnectionUrl" => config.ServerConnectionUrl ?? "",
        "Splinter.ModpackUrl" => config.ModpackUrl ?? "",
        "Splinter.PanelStyle" => config.PanelStyle.ToString(),
        "Splinter.PrettyPanelBackgroundFile" => config.PrettyPanelBackgroundFile ?? "",
        "Splinter.GameImageUrl" => config.GameImageUrl ?? "",
        "Splinter.InfoPanelImageFile" => config.InfoPanelImageFile ?? "",
        "Splinter.ServerPassword" => config.ServerPassword ?? "",
        "Splinter.OnlineEmbedColor" => config.OnlineEmbedColor ?? "#2ECC71",
        "Splinter.OfflineEmbedColor" => config.OfflineEmbedColor ?? "#E74C3C",
        "Splinter.TransitionEmbedColor" => config.TransitionEmbedColor ?? "#F1C40F",
        "Splinter.RestrictFunctionsToDiscordRole" => config.RestrictFunctionsToDiscordRole ? "true" : "false",
        "Splinter.DiscordRoleName" => config.DiscordRoleName ?? "",
        "Splinter.DisplayStartButton" => config.DisplayStartButton ? "true" : "false",
        "Splinter.DisplayStopButton" => config.DisplayStopButton ? "true" : "false",
        "Splinter.DisplayRestartButton" => config.DisplayRestartButton ? "true" : "false",
        "Splinter.DisplayKillButton" => config.DisplayKillButton ? "true" : "false",
        "Splinter.DisplayUpdateButton" => config.DisplayUpdateButton ? "true" : "false",
        "Splinter.DisplayBackupButton" => config.DisplayBackupButton ? "true" : "false",
        "Splinter.LogButtonPressesAndCommands" => config.LogButtonPressesAndCommands ? "true" : "false",
        "Splinter.ButtonLogChannel" => config.ButtonLogChannel ?? "",
        "Splinter.EnableInfoCommand" => config.EnableInfoCommand ? "true" : "false",
        "Splinter.EnableStartCommand" => config.EnableStartCommand ? "true" : "false",
        "Splinter.EnableStopCommand" => config.EnableStopCommand ? "true" : "false",
        "Splinter.EnableRestartCommand" => config.EnableRestartCommand ? "true" : "false",
        "Splinter.EnableKillCommand" => config.EnableKillCommand ? "true" : "false",
        "Splinter.EnableUpdateCommand" => config.EnableUpdateCommand ? "true" : "false",
        "Splinter.EnableBackupCommand" => config.EnableBackupCommand ? "true" : "false",
        "Splinter.EnableConsoleCommand" => config.EnableConsoleCommand ? "true" : "false",
        "Splinter.EnableWebPanel" => config.EnableWebPanel ? "true" : "false",
        "Splinter.ShowPlayerCount" => config.ShowPlayerCount ? "true" : "false",
        "Splinter.AdditionalEmbedFieldTitle" => config.AdditionalEmbedFieldTitle ?? "",
        "Splinter.AdditionalEmbedFieldText" => config.AdditionalEmbedFieldText ?? "",
        "Splinter.ManagementUrl" => config.ManagementUrl ?? "",
        "Splinter.DisplayManageButton" => config.DisplayManageButton ? "true" : "false",
        "Splinter.OnlineStatusText" => config.OnlineStatusText ?? "",
        "Splinter.OfflineStatusText" => config.OfflineStatusText ?? "",
        "Splinter.SendConsoleToDiscord" => config.SendConsoleToDiscord ? "true" : "false",
        "Splinter.ConsoleDiscordChannel" => config.ConsoleDiscordChannel ?? "",
        "Splinter.ConsoleToDiscordBatchTimer" => config.ConsoleToDiscordBatchTimer.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "Splinter.ExcludeConsoleOutput" => config.ExcludeConsoleOutput ?? "",
        "Splinter.PostJoinLeaveMessages" => config.PostJoinLeaveMessages ? "true" : "false",
        "Splinter.JoinLeaveMessagesChannel" => config.JoinLeaveMessagesChannel ?? "",
        "Splinter.DisplayOnlinePlayerList" => config.DisplayOnlinePlayerList ? "true" : "false",
        "Splinter.DisplayPlaytimeLeaderboard" => config.DisplayPlaytimeLeaderboard ? "true" : "false",
        "Splinter.EnablePlaytimeCommand" => config.EnablePlaytimeCommand ? "true" : "false",
        "Splinter.EnableFullPlaytimeListCommand" => config.EnableFullPlaytimeListCommand ? "true" : "false",
        "Splinter.SendChatToDiscord" => config.SendChatToDiscord ? "true" : "false",
        "Splinter.ChatConsoleLevel" => config.ChatConsoleLevel ?? "",
        "Splinter.TraceConsoleMetadata" => config.TraceConsoleMetadata ? "true" : "false",
        "Splinter.SendChatFromDiscordToServer" => config.SendChatFromDiscordToServer ? "true" : "false",
        "Splinter.ChatDiscordChannel" => config.ChatDiscordChannel ?? "",
        "Splinter.DisplayWhitelistRequestButton" => config.DisplayWhitelistRequestButton ? "true" : "false",
        "Splinter.WhitelistRequestChannel" => config.WhitelistRequestChannel ?? "",
        "Splinter.WhitelistApprovalRole" => config.WhitelistApprovalRole ?? "",
        "Splinter.WhitelistAutoApproveRole" => config.WhitelistAutoApproveRole ?? "",
        "Splinter.CustomWhitelistCommand" => config.CustomWhitelistCommand ?? "",
        "Splinter.WhitelistPlayerIdType" => config.WhitelistPlayerIdType.ToString(),
        "Splinter.ValheimJoinCode" => config.ValheimJoinCode ? "true" : "false",
        "Splinter.NotifyServerStatus" => config.NotifyServerStatus ? "true" : "false",
        "Splinter.StatusNotificationChannel" => config.StatusNotificationChannel ?? "",
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };

    private static bool TryParseValue(string key, string? raw, out object? parsed, out string error)
    {
        parsed = null;
        error = "";
        // Keys are canonicalized by the field map, rather than depending on user casing.
        string canonical = FieldMap[key].Key;
        switch (canonical)
        {
            case "Splinter.DiscordToken":
                if (raw is not null && raw.Length <= 512 && !raw.Any(char.IsControl))
                {
                    parsed = raw.Trim();
                    return true;
                }
                error = "Splinter.DiscordToken must be a valid token (maximum 512 characters).";
                break;
            case "Splinter.BotRefreshIntervalSeconds":
                if (int.TryParse(raw, out int interval) && interval is >= 30 and <= 300)
                {
                    parsed = interval;
                    return true;
                }
                error = "Splinter.BotRefreshIntervalSeconds must be between 30 and 300 seconds.";
                break;
            case "Splinter.Enabled":
                if (bool.TryParse(raw, out bool enabled))
                {
                    parsed = enabled;
                    return true;
                }
                error = "Splinter.Enabled must be true or false.";
                break;
            case "Splinter.PresenceTemplate":
                if (!string.IsNullOrWhiteSpace(raw) && raw.Length <= 120 &&
                    !raw.Any(char.IsControl))
                {
                    parsed = raw;
                    return true;
                }
                error = "Splinter.PresenceTemplate must contain 1-120 characters and no control characters.";
                break;
            case "Splinter.PresenceType":
                if (!string.IsNullOrWhiteSpace(raw) &&
                    Enum.GetNames<BotPresenceType>().Any(name =>
                        name.Equals(raw.Trim(), StringComparison.OrdinalIgnoreCase)) &&
                    Enum.TryParse<BotPresenceType>(raw.Trim(), ignoreCase: true, out var presenceType))
                {
                    parsed = presenceType;
                    return true;
                }
                error = "Splinter.PresenceType must be CustomStatus, Playing, Watching, Listening or Competing.";
                break;
            case "Splinter.ServerDisplayName":
                if (raw is not null && raw.Length <= 256 && !raw.Any(char.IsControl))
                {
                    parsed = raw.Trim();
                    return true;
                }
                error = "Splinter.ServerDisplayName must have at most 256 characters and no control characters.";
                break;
            case "Splinter.BotTagline":
                if (raw is not null && raw.Length <= 2048 && !raw.Any(char.IsControl))
                {
                    parsed = raw.Trim();
                    return true;
                }
                error = "Splinter.BotTagline must have at most 2048 characters and no control characters.";
                break;
            case "Splinter.ServerConnectionUrl":
                if (raw is not null && raw.Length <= 256 && !raw.Any(char.IsControl))
                {
                    parsed = raw.Trim();
                    return true;
                }
                error = "Splinter.ServerConnectionUrl must have at most 256 characters and no control characters.";
                break;
            case "Splinter.ModpackUrl":
                // Unlike an image URL or a link button, this field can be ordinary
                // descriptive text or Discord Markdown [label](https://url).
                if (raw is not null && raw.Length <= 1024 && !raw.Any(char.IsControl))
                {
                    parsed = raw.Trim();
                    return true;
                }
                error = "Modpack information must be at most 1024 characters with no control characters.";
                break;
            case "Splinter.PanelStyle":
                if (Enum.TryParse<InfoPanelStyle>(raw, ignoreCase: false, out var style) &&
                    Enum.IsDefined(style))
                {
                    parsed = style;
                    return true;
                }
                error = "Panel Style must be Classic or Pretty.";
                break;
            case "Splinter.PrettyPanelBackgroundFile":
                if (PanelImageFiles.ValidSetting(raw) &&
                    (string.IsNullOrWhiteSpace(raw) ||
                     new[] { ".png", ".jpg", ".jpeg", ".webp" }
                       .Contains(Path.GetExtension(raw), StringComparer.OrdinalIgnoreCase)))
                {
                    parsed = raw?.Trim() ?? "";
                    return true;
                }
                error = "Pretty Panel Background Override must be a relative PNG/JPG/WEBP file in the Shard folder.";
                break;
            case "Splinter.GameImageUrl":
                if (string.IsNullOrWhiteSpace(raw) || InfoPanelRenderer.TryWebUrl(raw, out _))
                {
                    parsed = raw?.Trim() ?? "";
                    return true;
                }
                error = "Splinter.GameImageUrl must be blank or a valid HTTP/HTTPS URL (max 1024 characters).";
                break;
            case "Splinter.InfoPanelImageFile":
                if (PanelImageFiles.ValidSetting(raw))
                {
                    parsed = raw?.Trim() ?? "";
                    return true;
                }
                error = "Info Panel Image File must be a relative PNG/JPG/GIF/WEBP file inside the Shard application directory.";
                break;
            case "Splinter.ServerPassword":
                if (raw is not null && raw.Length <= 256 && !raw.Any(char.IsControl))
                {
                    parsed = raw.Trim();
                    return true;
                }
                error = "Splinter.ServerPassword must be at most 256 characters with no control characters.";
                break;
            case "Splinter.OnlineEmbedColor":
            case "Splinter.OfflineEmbedColor":
            case "Splinter.TransitionEmbedColor":
                if (InfoPanelRenderer.TryParseColor(raw, out _))
                {
                    parsed = raw!.ToUpperInvariant();
                    return true;
                }
                error = canonical + " must be a six-digit hexadecimal colour (#RRGGBB).";
                break;
            case "Splinter.DiscordRoleName":
                if (raw is not null && raw.Length <= 100 && !raw.Any(char.IsControl))
                {
                    parsed = raw.Trim();
                    return true;
                }
                error = "Discord role name or ID must be at most 100 characters.";
                break;
            case "Splinter.ButtonLogChannel":
                if (raw is not null && (raw.Length == 0 || LifecycleNotifications.IsValidChannel(raw)))
                {
                    parsed = raw.Trim();
                    return true;
                }
                error = "Button log channel must be a channel name or numeric channel ID.";
                break;
            case "Splinter.RestrictFunctionsToDiscordRole":
            case "Splinter.DisplayStartButton":
            case "Splinter.DisplayStopButton":
            case "Splinter.DisplayRestartButton":
            case "Splinter.DisplayKillButton":
            case "Splinter.DisplayUpdateButton":
            case "Splinter.DisplayBackupButton":
            case "Splinter.EnableInfoCommand":
            case "Splinter.EnableStartCommand":
            case "Splinter.EnableStopCommand":
            case "Splinter.EnableRestartCommand":
            case "Splinter.EnableKillCommand":
            case "Splinter.EnableUpdateCommand":
            case "Splinter.EnableBackupCommand":
            case "Splinter.EnableConsoleCommand":
            case "Splinter.LogButtonPressesAndCommands":
                if (bool.TryParse(raw, out bool adminFlag))
                {
                    parsed = adminFlag;
                    return true;
                }
                error = canonical + " must be true or false.";
                break;
            case "Splinter.EnableWebPanel":
                if (bool.TryParse(raw, out bool option))
                {
                    parsed = option;
                    return true;
                }
                error = canonical + " must be true or false.";
                break;
            case "Splinter.DisplayWhitelistRequestButton":
                if (bool.TryParse(raw, out bool whitelistEnabled))
                {
                    parsed = whitelistEnabled;
                    return true;
                }
                error = canonical + " must be true or false.";
                break;
            case "Splinter.WhitelistRequestChannel":
                if (raw is not null && (raw.Length == 0 || LifecycleNotifications.IsValidChannel(raw)))
                {
                    parsed = raw.Trim();
                    return true;
                }
                error = "Whitelist request channel must be a channel name or numeric ID.";
                break;
            case "Splinter.WhitelistApprovalRole":
            case "Splinter.WhitelistAutoApproveRole":
                if (raw is not null && raw.Length <= 100 && !raw.Any(char.IsControl))
                {
                    parsed = raw.Trim();
                    return true;
                }
                error = "Whitelist role name or ID must have at most 100 characters.";
                break;
            case "Splinter.CustomWhitelistCommand":
                if (WhitelistRules.ValidTemplate(raw))
                {
                    parsed = raw!.Trim();
                    return true;
                }
                error = "Invalid whitelist command template (maximum 200 characters; no control characters).";
                break;
            case "Splinter.WhitelistPlayerIdType":
                if (!string.IsNullOrWhiteSpace(raw) &&
                    Enum.TryParse<WhitelistPlayerIdType>(raw, true, out var idType) &&
                    Enum.GetNames<WhitelistPlayerIdType>().Any(name =>
                        name.Equals(raw, StringComparison.OrdinalIgnoreCase)))
                {
                    parsed = idType;
                    return true;
                }
                error = "Whitelist player ID type must be Username or SteamID.";
                break;
            case "Splinter.ChatConsoleLevel":
                if (raw is not null && (string.IsNullOrWhiteSpace(raw) ||
                    System.Text.RegularExpressions.Regex.IsMatch(raw.Trim(),
                        @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,31}$",
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(100))))
                {
                    parsed = raw.Trim();
                    return true;
                }
                error = "AMP chat log level must be blank or a simple label of up to 32 characters.";
                break;
            case "Splinter.ConsoleToDiscordBatchTimer":
                if (int.TryParse(raw, out int batchInterval) && batchInterval is >= 10 and <= 300)
                {
                    parsed = batchInterval;
                    return true;
                }
                error = "Console batch timer must be between 10 and 300 seconds.";
                break;
            case "Splinter.ConsoleDiscordChannel":
            case "Splinter.JoinLeaveMessagesChannel":
            case "Splinter.ChatDiscordChannel":
                if (raw is not null && (raw.Length == 0 || LifecycleNotifications.IsValidChannel(raw)))
                {
                    parsed = raw.Trim();
                    return true;
                }
                error = "Channel must be a valid Discord channel name or ID.";
                break;
            case "Splinter.ExcludeConsoleOutput":
                if (raw is not null && raw.Length <= 1000 &&
                    !raw.Any(c => char.IsControl(c) && c != '\n' && c != '\r'))
                {
                    parsed = raw;
                    return true;
                }
                error = "Console exclusion patterns must be no longer than 1000 characters.";
                break;
            case "Splinter.SendConsoleToDiscord":
            case "Splinter.PostJoinLeaveMessages":
            case "Splinter.DisplayOnlinePlayerList":
            case "Splinter.DisplayPlaytimeLeaderboard":
            case "Splinter.EnablePlaytimeCommand":
            case "Splinter.EnableFullPlaytimeListCommand":
            case "Splinter.TraceConsoleMetadata":
            case "Splinter.SendChatToDiscord":
            case "Splinter.SendChatFromDiscordToServer":
            case "Splinter.ValheimJoinCode":
                if (bool.TryParse(raw, out bool relayFlag))
                {
                    parsed = relayFlag;
                    return true;
                }
                error = canonical + " must be true or false.";
                break;
            case "Splinter.NotifyServerStatus":
                if (bool.TryParse(raw, out bool notifyServerStatus))
                {
                    parsed = notifyServerStatus;
                    return true;
                }
                error = "Splinter.NotifyServerStatus must be true or false.";
                break;
            case "Splinter.StatusNotificationChannel":
                if (raw is not null && (raw.Length == 0 || LifecycleNotifications.IsValidChannel(raw)))
                {
                    parsed = raw.Trim();
                    return true;
                }
                error = "Splinter.StatusNotificationChannel must be a channel name or numeric channel ID (max 100 characters).";
                break;
            case "Splinter.ShowPlayerCount":
                if (bool.TryParse(raw, out bool showPlayerCount))
                {
                    parsed = showPlayerCount;
                    return true;
                }
                error = "Splinter.ShowPlayerCount must be true or false.";
                break;
            case "Splinter.AdditionalEmbedFieldTitle":
                if (raw is not null && raw.Length <= 256 && !raw.Any(char.IsControl))
                {
                    parsed = raw.Trim();
                    return true;
                }
                error = "Splinter.AdditionalEmbedFieldTitle must have at most 256 characters and no control characters.";
                break;
            case "Splinter.AdditionalEmbedFieldText":
                if (raw is not null && raw.Length <= 1024 && !raw.Any(char.IsControl))
                {
                    parsed = raw.Trim();
                    return true;
                }
                error = "Splinter.AdditionalEmbedFieldText must have at most 1024 characters and no control characters.";
                break;
            case "Splinter.ManagementUrl":
                if (string.IsNullOrWhiteSpace(raw) || InfoPanelRenderer.TryWebUrl(raw, out _))
                {
                    parsed = raw?.Trim() ?? "";
                    return true;
                }
                error = "Splinter.ManagementUrl must be blank or a valid HTTP/HTTPS URL (max 1024 characters).";
                break;
            case "Splinter.DisplayManageButton":
                if (bool.TryParse(raw, out bool displayManageButton))
                {
                    parsed = displayManageButton;
                    return true;
                }
                error = "Splinter.DisplayManageButton must be true or false.";
                break;
            case "Splinter.OnlineStatusText":
                if (raw is not null && raw.Length <= 128 && !raw.Any(char.IsControl))
                {
                    parsed = raw.Trim();
                    return true;
                }
                error = "Splinter.OnlineStatusText must have at most 128 characters and no control characters.";
                break;
            case "Splinter.OfflineStatusText":
                if (raw is not null && raw.Length <= 128 && !raw.Any(char.IsControl))
                {
                    parsed = raw.Trim();
                    return true;
                }
                error = "Splinter.OfflineStatusText must have at most 128 characters and no control characters.";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(key));
        }
        return false;
    }

    private static bool Apply(ShardBotConfig config, string key, object value)
    {
        switch (FieldMap[key].Key)
        {
            case "Splinter.Enabled":
                bool enabled = (bool)value;
                if (config.Enabled == enabled) return false;
                config.Enabled = enabled;
                return true;
            case "Splinter.PresenceTemplate":
                string template = (string)value;
                if (config.PresenceTemplate == template) return false;
                config.PresenceTemplate = template;
                return true;
            case "Splinter.PresenceType":
                var type = (BotPresenceType)value;
                if (config.PresenceType == type) return false;
                config.PresenceType = type;
                return true;
            case "Splinter.ServerDisplayName":
                if (config.ServerDisplayName == (string)value) return false;
                config.ServerDisplayName = (string)value;
                return true;
            case "Splinter.BotTagline":
                if (config.BotTagline == (string)value) return false;
                config.BotTagline = (string)value;
                return true;
            case "Splinter.ServerConnectionUrl":
                if (config.ServerConnectionUrl == (string)value) return false;
                config.ServerConnectionUrl = (string)value;
                return true;
            case "Splinter.ModpackUrl":
                if (config.ModpackUrl == (string)value) return false;
                config.ModpackUrl = (string)value;
                return true;
            case "Splinter.PanelStyle":
                if (config.PanelStyle == (InfoPanelStyle)value) return false;
                config.PanelStyle = (InfoPanelStyle)value;
                return true;
            case "Splinter.PrettyPanelBackgroundFile":
                if (config.PrettyPanelBackgroundFile == (string)value) return false;
                config.PrettyPanelBackgroundFile = (string)value;
                return true;
            case "Splinter.GameImageUrl":
                if (config.GameImageUrl == (string)value) return false;
                config.GameImageUrl = (string)value;
                return true;
            case "Splinter.InfoPanelImageFile":
                if (config.InfoPanelImageFile == (string)value) return false;
                config.InfoPanelImageFile = (string)value;
                return true;
            case "Splinter.ShowPlayerCount":
                if (config.ShowPlayerCount == (bool)value) return false;
                config.ShowPlayerCount = (bool)value;
                return true;
            case "Splinter.AdditionalEmbedFieldTitle":
                if (config.AdditionalEmbedFieldTitle == (string)value) return false;
                config.AdditionalEmbedFieldTitle = (string)value;
                return true;
            case "Splinter.AdditionalEmbedFieldText":
                if (config.AdditionalEmbedFieldText == (string)value) return false;
                config.AdditionalEmbedFieldText = (string)value;
                return true;
            case "Splinter.ManagementUrl":
                if (config.ManagementUrl == (string)value) return false;
                config.ManagementUrl = (string)value;
                return true;
            case "Splinter.DisplayManageButton":
                if (config.DisplayManageButton == (bool)value) return false;
                config.DisplayManageButton = (bool)value;
                return true;
            case "Splinter.OnlineStatusText":
                if (config.OnlineStatusText == (string)value) return false;
                config.OnlineStatusText = (string)value;
                return true;
            case "Splinter.OfflineStatusText":
                if (config.OfflineStatusText == (string)value) return false;
                config.OfflineStatusText = (string)value;
                return true;
            case "Splinter.ServerPassword":
                if (config.ServerPassword == (string)value) return false;
                config.ServerPassword = (string)value;
                return true;
            case "Splinter.OnlineEmbedColor":
                if (config.OnlineEmbedColor == (string)value) return false;
                config.OnlineEmbedColor = (string)value;
                return true;
            case "Splinter.OfflineEmbedColor":
                if (config.OfflineEmbedColor == (string)value) return false;
                config.OfflineEmbedColor = (string)value;
                return true;
            case "Splinter.TransitionEmbedColor":
                if (config.TransitionEmbedColor == (string)value) return false;
                config.TransitionEmbedColor = (string)value;
                return true;
            case "Splinter.RestrictFunctionsToDiscordRole":
                if (config.RestrictFunctionsToDiscordRole == (bool)value) return false;
                config.RestrictFunctionsToDiscordRole = (bool)value;
                return true;
            case "Splinter.DiscordRoleName":
                if (config.DiscordRoleName == (string)value) return false;
                config.DiscordRoleName = (string)value;
                return true;
            case "Splinter.DisplayStartButton":
                if (config.DisplayStartButton == (bool)value) return false;
                config.DisplayStartButton = (bool)value;
                return true;
            case "Splinter.DisplayStopButton":
                if (config.DisplayStopButton == (bool)value) return false;
                config.DisplayStopButton = (bool)value;
                return true;
            case "Splinter.DisplayRestartButton":
                if (config.DisplayRestartButton == (bool)value) return false;
                config.DisplayRestartButton = (bool)value;
                return true;
            case "Splinter.DisplayKillButton":
                if (config.DisplayKillButton == (bool)value) return false;
                config.DisplayKillButton = (bool)value;
                return true;
            case "Splinter.DisplayUpdateButton":
                if (config.DisplayUpdateButton == (bool)value) return false;
                config.DisplayUpdateButton = (bool)value;
                return true;
            case "Splinter.DisplayBackupButton":
                if (config.DisplayBackupButton == (bool)value) return false;
                config.DisplayBackupButton = (bool)value;
                return true;
            case "Splinter.EnableInfoCommand":
                if (config.EnableInfoCommand == (bool)value) return false;
                config.EnableInfoCommand = (bool)value;
                return true;
            case "Splinter.EnableStartCommand":
                if (config.EnableStartCommand == (bool)value) return false;
                config.EnableStartCommand = (bool)value;
                return true;
            case "Splinter.EnableStopCommand":
                if (config.EnableStopCommand == (bool)value) return false;
                config.EnableStopCommand = (bool)value;
                return true;
            case "Splinter.EnableRestartCommand":
                if (config.EnableRestartCommand == (bool)value) return false;
                config.EnableRestartCommand = (bool)value;
                return true;
            case "Splinter.EnableKillCommand":
                if (config.EnableKillCommand == (bool)value) return false;
                config.EnableKillCommand = (bool)value;
                return true;
            case "Splinter.EnableUpdateCommand":
                if (config.EnableUpdateCommand == (bool)value) return false;
                config.EnableUpdateCommand = (bool)value;
                return true;
            case "Splinter.EnableBackupCommand":
                if (config.EnableBackupCommand == (bool)value) return false;
                config.EnableBackupCommand = (bool)value;
                return true;
            case "Splinter.EnableConsoleCommand":
                if (config.EnableConsoleCommand == (bool)value) return false;
                config.EnableConsoleCommand = (bool)value;
                return true;
            case "Splinter.LogButtonPressesAndCommands":
                if (config.LogButtonPressesAndCommands == (bool)value) return false;
                config.LogButtonPressesAndCommands = (bool)value;
                return true;
            case "Splinter.ButtonLogChannel":
                if (config.ButtonLogChannel == (string)value) return false;
                config.ButtonLogChannel = (string)value;
                return true;
            case "Splinter.EnableWebPanel":
                if (config.EnableWebPanel == (bool)value) return false;
                config.EnableWebPanel = (bool)value;
                return true;
            case "Splinter.BotRefreshIntervalSeconds":
                if (config.BotRefreshIntervalSeconds == (int)value) return false;
                config.BotRefreshIntervalSeconds = (int)value;
                return true;
            case "Splinter.DisplayWhitelistRequestButton":
                if (config.DisplayWhitelistRequestButton == (bool)value) return false;
                config.DisplayWhitelistRequestButton = (bool)value;
                return true;
            case "Splinter.WhitelistRequestChannel":
                if (config.WhitelistRequestChannel == (string)value) return false;
                config.WhitelistRequestChannel = (string)value;
                return true;
            case "Splinter.WhitelistApprovalRole":
                if (config.WhitelistApprovalRole == (string)value) return false;
                config.WhitelistApprovalRole = (string)value;
                return true;
            case "Splinter.WhitelistAutoApproveRole":
                if (config.WhitelistAutoApproveRole == (string)value) return false;
                config.WhitelistAutoApproveRole = (string)value;
                return true;
            case "Splinter.CustomWhitelistCommand":
                if (config.CustomWhitelistCommand == (string)value) return false;
                config.CustomWhitelistCommand = (string)value;
                return true;
            case "Splinter.WhitelistPlayerIdType":
                if (config.WhitelistPlayerIdType == (WhitelistPlayerIdType)value) return false;
                config.WhitelistPlayerIdType = (WhitelistPlayerIdType)value;
                return true;
            case "Splinter.SendConsoleToDiscord":
                if (config.SendConsoleToDiscord == (bool)value) return false;
                config.SendConsoleToDiscord = (bool)value;
                return true;
            case "Splinter.ConsoleDiscordChannel":
                if (config.ConsoleDiscordChannel == (string)value) return false;
                config.ConsoleDiscordChannel = (string)value;
                return true;
            case "Splinter.ConsoleToDiscordBatchTimer":
                if (config.ConsoleToDiscordBatchTimer == (int)value) return false;
                config.ConsoleToDiscordBatchTimer = (int)value;
                return true;
            case "Splinter.ExcludeConsoleOutput":
                if (config.ExcludeConsoleOutput == (string)value) return false;
                config.ExcludeConsoleOutput = (string)value;
                return true;
            case "Splinter.PostJoinLeaveMessages":
                if (config.PostJoinLeaveMessages == (bool)value) return false;
                config.PostJoinLeaveMessages = (bool)value;
                return true;
            case "Splinter.JoinLeaveMessagesChannel":
                if (config.JoinLeaveMessagesChannel == (string)value) return false;
                config.JoinLeaveMessagesChannel = (string)value;
                return true;
            case "Splinter.DisplayOnlinePlayerList":
                if (config.DisplayOnlinePlayerList == (bool)value) return false;
                config.DisplayOnlinePlayerList = (bool)value;
                return true;
            case "Splinter.DisplayPlaytimeLeaderboard":
                if (config.DisplayPlaytimeLeaderboard == (bool)value) return false;
                config.DisplayPlaytimeLeaderboard = (bool)value;
                return true;
            case "Splinter.EnablePlaytimeCommand":
                if (config.EnablePlaytimeCommand == (bool)value) return false;
                config.EnablePlaytimeCommand = (bool)value;
                return true;
            case "Splinter.EnableFullPlaytimeListCommand":
                if (config.EnableFullPlaytimeListCommand == (bool)value) return false;
                config.EnableFullPlaytimeListCommand = (bool)value;
                return true;
            case "Splinter.TraceConsoleMetadata":
                if (config.TraceConsoleMetadata == (bool)value) return false;
                config.TraceConsoleMetadata = (bool)value;
                return true;
            case "Splinter.ChatConsoleLevel":
                if (config.ChatConsoleLevel == (string)value) return false;
                config.ChatConsoleLevel = (string)value;
                return true;
            case "Splinter.SendChatToDiscord":
                if (config.SendChatToDiscord == (bool)value) return false;
                config.SendChatToDiscord = (bool)value;
                return true;
            case "Splinter.SendChatFromDiscordToServer":
                if (config.SendChatFromDiscordToServer == (bool)value) return false;
                config.SendChatFromDiscordToServer = (bool)value;
                return true;
            case "Splinter.ChatDiscordChannel":
                if (config.ChatDiscordChannel == (string)value) return false;
                config.ChatDiscordChannel = (string)value;
                return true;
            case "Splinter.ValheimJoinCode":
                if (config.ValheimJoinCode == (bool)value) return false;
                config.ValheimJoinCode = (bool)value;
                return true;
            case "Splinter.NotifyServerStatus":
                if (config.NotifyServerStatus == (bool)value) return false;
                config.NotifyServerStatus = (bool)value;
                return true;
            case "Splinter.StatusNotificationChannel":
                if (config.StatusNotificationChannel == (string)value) return false;
                config.StatusNotificationChannel = (string)value;
                return true;
            default:
                throw new ArgumentOutOfRangeException(nameof(key));
        }
    }

    private static bool IsTarget(ActionExecutingContext context, out Guid shardId, out string action)
    {
        shardId = default;
        action = "";
        if (context.Controller?.GetType().FullName != "webapi.Controllers.ShardsController" ||
            !context.ActionArguments.TryGetValue("id", out object? id) || id is not Guid parsed ||
            parsed == Guid.Empty ||
            !context.ActionDescriptor.RouteValues.TryGetValue("action", out string? name) ||
            (name != "GetSettings" && name != "PatchSettings"))
            return false;
        shardId = parsed;
        action = name;
        return true;
    }

    private static async Task<(IActionResult? Refusal, Guid SpireId)> VerifyShardWriteAsync(
        ActionExecutingContext context, Guid shardId)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated != true)
            return (new UnauthorizedResult(), Guid.Empty);

        MethodInfo? auth = context.Controller.GetType().GetMethod(
            "AuthShardAsync", BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, types: new[] { typeof(Guid), typeof(string) }, modifiers: null);
        if (auth is null)
            throw new MissingMethodException("AMP ShardsController.AuthShardAsync(Guid,string) not available");

        if (auth.Invoke(context.Controller, new object[] { shardId, "settings.write" }) is not Task task)
            throw new InvalidOperationException("AMP Shard authorization task was unavailable");
        await task.WaitAsync(context.HttpContext.RequestAborted);

        object? tuple = task.GetType().GetProperty("Result")?.GetValue(task);
        if (tuple is null) throw new InvalidOperationException("AMP Shard authorization returned no result");
        object? denied = tuple.GetType().GetField("Item2")?.GetValue(tuple);
        if (denied is IActionResult refusal) return (refusal, Guid.Empty);
        object? shard = tuple.GetType().GetField("Item1")?.GetValue(tuple);
        if (shard is null) throw new InvalidOperationException("AMP Shard authorization returned no Shard");
        if (shard.GetType().GetProperty("SpireId")?.GetValue(shard) is not Guid spireId || spireId == Guid.Empty)
            throw new InvalidOperationException("AMP authorized Shard has no SpireId");
        return (null, spireId);
    }

    private static async Task<bool> CanWriteSettingAsync(
        ActionExecutingContext context, Guid spireId, Guid shardId, SettingField field)
    {
        Assembly webapi = context.Controller.GetType().Assembly;
        Type permissionType = webapi.GetType("webapi.Auth.PermissionContext", throwOnError: true)!;
        object permission = context.HttpContext.RequestServices.GetRequiredService(permissionType);
        MethodInfo? check = permissionType.GetMethod("CanOnShardSettingWriteAsync", BindingFlags.Public | BindingFlags.Instance);
        if (check is null || check.GetParameters().Length != 5)
            throw new MissingMethodException("AMP field permission method is not compatible");
        object? result = check.Invoke(permission, new object?[] {
            spireId.ToString("D"), shardId.ToString("D"), Category, field.Subcategory, field.Key
        });
        return result switch
        {
            ValueTask<bool> valueTask => await valueTask,
            Task<bool> boolTask => await boolTask.WaitAsync(context.HttpContext.RequestAborted),
            _ => throw new InvalidOperationException("Unexpected AMP field permission response")
        };
    }
}
