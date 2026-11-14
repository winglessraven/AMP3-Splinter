using Discord;
using Microsoft.Extensions.DependencyInjection;
using Discord.WebSocket;
using System.Text.Json;

namespace Splinter.Host;

// One Discord.Net gateway client per enabled Shard; hosted inside one AMP Web API process.
public sealed class BotSession : IAsyncDisposable
{
    // Keep Discord command names aligned with AMP-Discord-Bot wherever possible.
    public const string InfoCommandName = "info";
    public const string ConsoleCommandName = "console";
    private const string LegacyInfoCommandName = "server";
    private readonly Guid _id;
    private readonly ShardControlBridge _controls;
    private readonly ShardConsoleBridge _console;
    private readonly ConsoleHistoryBridge _history;
    private readonly AmpConsoleEvents _liveConsole;
    private readonly SemaphoreSlim _relayGate = new(1, 1);
    private Task? _liveConsoleWorker;
    private volatile bool _liveConsoleActive;
    private string? _lastLiveConsoleError;
    private bool _liveConsoleMissingLogged;
    private readonly ConsoleRelayState _relayState;
    private readonly ConsoleMetadataProbe _metadataProbe = new();
    private readonly ConsoleChatEchoFilter _chatEchoes = new();
    // Shared across bot-session replacements in the same AMP Web API process.
    private static readonly DiscordChatMessageGate InboundChatMessages = new();
    private readonly PlayerPlaytime _playtime;
    private readonly SnapshotFollowUps _snapshotFollowUps;
    private readonly AmpAppFolderBridge _appFolderBridge;
    private readonly AmpArtworkBridge _artworkBridge;
    private byte[]? _ampArtwork;
    private DateTimeOffset _ampArtworkRetryAt = DateTimeOffset.MinValue;
    private string? _ampArtworkSource;
    private PanelImage? _prettyCachedImage;
    private string? _prettyCacheKey;
    private byte[]? _prettyOverrideBytes;
    private string? _prettyOverrideStamp;
    private string? _appDirectory;
    private string? _imageStamp;
    private PanelImage? _cachedImage;
    private string? _imageError;
    private Task? _snapshotFollowUpWorker;
    private DateTimeOffset _relayAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPlaytimeSave = DateTimeOffset.MinValue;
    private bool _rosterSeenThisSession;
    private readonly object _eventSync = new();
    private readonly HashSet<Guid> _seenPlayerEvents = [];
    private readonly Queue<Guid> _seenPlayerEventOrder = new();
    private string? _relayError;
    private bool _chatIntentEnabled;
    public bool ChatIntentEnabled => _chatIntentEnabled;
    private readonly SemaphoreSlim _consoleGate = new(1, 1);
    private readonly SemaphoreSlim _whitelistGate = new(1, 1);
    private readonly SemaphoreSlim _controlGate = new(1, 1);
    private readonly object _registrationSync = new();
    private readonly HashSet<ulong> _pendingRegistrations = new();
    private Task? _registrationWorker;
    private readonly CancellationTokenSource _sessionStop = new();
    private readonly object _queueSync = new();
    private Task? _updateWorker;
    private ShardSnapshot? _queuedSnapshot;
    private string? _lastPresenceStatus;
    private static readonly IReadOnlyDictionary<string, ShardControlAction> ControlNames =
        new Dictionary<string, ShardControlAction>(StringComparer.Ordinal)
        {
            ["start-server"] = ShardControlAction.Start,
            ["stop-server"] = ShardControlAction.Stop,
            ["restart-server"] = ShardControlAction.Restart,
            ["kill-server"] = ShardControlAction.Kill,
            ["update-server"] = ShardControlAction.Update,
            ["take-backup"] = ShardControlAction.Backup
        };
    // Kept independent of the button visibility settings (original Commands tab).
    public static bool CommandEnabled(ShardBotConfig config, string name) => name switch
    {
        InfoCommandName => config.EnableInfoCommand,
        "start-server" => config.EnableStartCommand,
        "stop-server" => config.EnableStopCommand,
        "restart-server" => config.EnableRestartCommand,
        "kill-server" => config.EnableKillCommand,
        "update-server" => config.EnableUpdateCommand,
        "take-backup" => config.EnableBackupCommand,
        ConsoleCommandName => config.EnableConsoleCommand,
        "playtime" => config.EnablePlaytimeCommand,
        "full-playtime-list" => config.EnableFullPlaytimeListCommand,
        _ => false
    };

    private ShardBotConfig _config;
    private string _configFingerprint;
    private string? _token;
    private int _presenceSeconds;
    private DateTimeOffset _panelAt = DateTimeOffset.MinValue;
    private string? _lastPanelStatus;
    private readonly DiscordSocketClient _client;
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly SemaphoreSlim _panelGate = new(1, 1);
    private ShardSnapshot? _snapshot;
    private string? _lastPresence;
    private BotPresenceType? _lastPresenceType;
    private string? _lastVariablesError;
    private UserStatus? _lastUserStatus;
    private DateTimeOffset _presenceAt = DateTimeOffset.MinValue;
    private string? _lastPanelContent;
    private string? _lastPanelImageStamp;
    private bool _ready;
    private bool _disposed;
    public string ConfigVersion { get; }

    public BotSession(Guid id, ShardBotConfig config, string version, string token,
        ShardControlBridge controls, ShardConsoleBridge console, ConsoleHistoryBridge history,
        IServiceScopeFactory scopes)
    {
        _id = id;
        _controls = controls;
        _console = console;
        _history = history;
        _liveConsole = new AmpConsoleEvents(scopes);
        _relayState = ConfigStore.Read(ConsoleRelay.PathFor(id), new ConsoleRelayState());
        _playtime = PlayerPlaytime.Load(id);
        _snapshotFollowUps = new SnapshotFollowUps(id);
        _appFolderBridge = new AmpAppFolderBridge(scopes);
        _artworkBridge = new AmpArtworkBridge(scopes);
        _chatIntentEnabled = ChatIntentRequired(config);
        _config = config;
        _configFingerprint = System.Text.Json.JsonSerializer.Serialize(config, ConfigStore.Json);
        _token = token;
        _presenceSeconds = Math.Clamp(config.BotRefreshIntervalSeconds, 30, 300);
        ConfigVersion = version;
        // Slash interactions provide guild-member role data without privileged intents.
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds |
                (_chatIntentEnabled ? GatewayIntents.GuildMessages | GatewayIntents.MessageContent : (GatewayIntents)0),
            LogLevel = LogSeverity.Warning,
            AlwaysDownloadUsers = false
        });
        _client.Ready += ReadyAsync;
        _client.JoinedGuild += JoinedGuildAsync;
        // Discord.Net must not await slow AMP calls or REST traffic on its
        // gateway event path. Each handler acknowledges its interaction first.
        _client.SlashCommandExecuted += interaction => QueueGatewayWork(
            () => SlashAsync(interaction), "slash");
        _client.ButtonExecuted += interaction => QueueGatewayWork(
            () => ButtonAsync(interaction), "button");
        _client.MessageReceived += message =>
            !_chatIntentEnabled || !_config.SendChatFromDiscordToServer
                ? Task.CompletedTask
                : QueueGatewayWork(() => ChatReceivedAsync(message), "chat");
        _client.ModalSubmitted += modal => QueueGatewayWork(
            () => WhitelistModalAsync(modal), "modal");
        _client.Log += LogAsync;
        _client.Disconnected += DisconnectedAsync;
    }

    // Enabling chat before choosing a channel must not request Discord's
    // privileged Message Content intent or reconnect the bot unnecessarily.
    public static bool ChatIntentRequired(ShardBotConfig config) =>
        config.SendChatFromDiscordToServer &&
        LifecycleNotifications.IsValidChannel(config.ChatDiscordChannel);

    // Presence/native settings are hot-reloaded by the supervisor. A text/type
    // change must not tear down the bot or lose live Discord connections.
    public async Task ApplyConfigAsync(ShardBotConfig config, CancellationToken ct)
    {
        string fingerprint = System.Text.Json.JsonSerializer.Serialize(config, ConfigStore.Json);
        if (fingerprint == _configFingerprint) return;
        await _updateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (fingerprint == _configFingerprint) return;
            _config = config;
            _configFingerprint = fingerprint;
            _presenceSeconds = Math.Clamp(config.BotRefreshIntervalSeconds, 30, 300);
            _presenceAt = DateTimeOffset.MinValue;
            _panelAt = DateTimeOffset.MinValue;
            _lastPanelStatus = null;
            _lastPanelContent = null;
            _imageStamp = null;
            _prettyCachedImage = null;
            _prettyCacheKey = null;
            _prettyOverrideStamp = null;
            _prettyOverrideBytes = null;
            _ampArtworkRetryAt = DateTimeOffset.MinValue;
        }
        finally { _updateGate.Release(); }
        // Discord commands must follow live settings changes, not just reconnects.
        // This runs only when the per-Shard configuration fingerprint changed.
        if (_ready)
        {
            foreach (SocketGuild guild in _client.Guilds)
                QueueRegistration(guild);
            if (_snapshot is not null) QueueUpdate(_snapshot);
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_token is null) return;
        ct.ThrowIfCancellationRequested();
        await _client.LoginAsync(TokenType.Bot, _token).ConfigureAwait(false);
        _token = null; // do not retain a second long-lived copy of the credential
        await _client.StartAsync().ConfigureAwait(false);
        _snapshotFollowUpWorker = Task.Run(() => WatchSnapshotFollowUpsAsync(_sessionStop.Token));
        _liveConsoleWorker = Task.Run(() => RunLiveConsoleAsync(_sessionStop.Token));
        BotLog.Write($"Discord gateway started for Shard {_id:D} (waiting for Ready).");
    }

    private Task QueueGatewayWork(Func<Task> handler, string context)
    {
        if (_disposed) return Task.CompletedTask;
        _ = Task.Run(async () =>
        {
            try { if (!_disposed) await handler().ConfigureAwait(false); }
            catch (Exception ex)
            {
                BotLog.Write($"Shard {_id:D} {context} gateway handler failed: {AmpFacade.CleanError(ex)}");
            }
        });
        return Task.CompletedTask;
    }

    private Task LogAsync(LogMessage msg)
    {
        if (msg.Severity <= LogSeverity.Warning)
        {
            // The Discord.Net message can be empty even when Exception contains
            // the underlying gateway failure. Do not dump raw request bodies.
            string error = msg.Exception is null ? "" :
                " (" + msg.Exception.GetType().Name + ": " +
                SafeDiagnostic(msg.Exception.Message) + ")";
            BotLog.Write($"Discord {_id:D}: {msg.Severity} {msg.Source}: {SafeDiagnostic(msg.Message)}{error}");
        }
        return Task.CompletedTask;
    }

    private static string SafeDiagnostic(string? value)
    {
        // Gateway exception messages may contain connection URLs. Avoid
        // copying URL query strings or token-shaped paths into the public log.
        if (string.IsNullOrWhiteSpace(value)) return "(no message)";
        string text = System.Text.RegularExpressions.Regex.Replace(value,
            @"https?://\S+|wss?://\S+", "[URL removed]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));
        return text.Replace('\r', ' ').Replace('\n', ' ')[..Math.Min(text.Length, 400)];
    }

    private Task DisconnectedAsync(Exception ex)
    {
        _ready = false;
        _lastPresence = null;
        _lastPresenceType = null;
        _lastUserStatus = null;
        _lastPanelContent = null;
        _lastPresenceStatus = null;
        _presenceAt = DateTimeOffset.MinValue;
        BotLog.Write($"Discord {_id:D} disconnected; reconnect managed by Discord.Net: " +
            ex.GetType().Name + ": " + SafeDiagnostic(ex.Message));
        return Task.CompletedTask;
    }

    // Gateway callbacks must return promptly. Command registration performs
    // multiple REST calls and must never block Discord.Net's gateway pump.
    private Task ReadyAsync()
    {
        _ready = true;
        BotLog.Write($"Discord bot ready for Shard {_id:D}; synchronising commands in {_client.Guilds.Count} guild(s).");
        foreach (SocketGuild guild in _client.Guilds) QueueRegistration(guild);
        if (_snapshot is not null) QueueUpdate(_snapshot);
        return Task.CompletedTask;
    }

    private Task JoinedGuildAsync(SocketGuild guild)
    {
        QueueRegistration(guild);
        return Task.CompletedTask;
    }

    private void QueueRegistration(SocketGuild guild)
    {
        if (_disposed) return;
        lock (_registrationSync)
        {
            _pendingRegistrations.Add(guild.Id);
            if (_registrationWorker is null)
                _registrationWorker = Task.Run(ProcessRegistrationsAsync);
        }
    }

    private async Task ProcessRegistrationsAsync()
    {
        while (!_sessionStop.IsCancellationRequested)
        {
            ulong guildId;
            lock (_registrationSync)
            {
                if (_pendingRegistrations.Count == 0)
                {
                    _registrationWorker = null;
                    return;
                }
                guildId = _pendingRegistrations.First();
                _pendingRegistrations.Remove(guildId);
            }
            if (!_ready || _client.GetGuild(guildId) is not SocketGuild guild) continue;
            try { await RegisterGuildCommandsAsync(guild).ConfigureAwait(false); }
            catch (Exception ex)
            { BotLog.Write($"Shard {_id:D} command synchronisation failed: {AmpFacade.CleanError(ex)}"); }
        }
        lock (_registrationSync) _registrationWorker = null;
    }

    // Coalesce incoming AMP snapshots. Slow Discord REST calls and console
    // history must not hold up the supervisor or other Shards' updates.
    public void QueueUpdate(ShardSnapshot snapshot)
    {
        if (_disposed) return;
        _snapshot = snapshot;
        lock (_queueSync)
        {
            _queuedSnapshot = snapshot;
            if (_updateWorker is null)
                _updateWorker = Task.Run(ProcessUpdatesAsync);
        }
    }

    private async Task ProcessUpdatesAsync()
    {
        while (!_sessionStop.IsCancellationRequested)
        {
            ShardSnapshot? next;
            lock (_queueSync)
            {
                next = _queuedSnapshot;
                _queuedSnapshot = null;
                if (next is null) { _updateWorker = null; return; }
            }
            try { await UpdateAsync(next, _sessionStop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_sessionStop.IsCancellationRequested) { break; }
            catch (Exception ex) { BotLog.Write($"Shard {_id:D} asynchronous update failed: {AmpFacade.CleanError(ex)}"); }
        }
        lock (_queueSync) { _updateWorker = null; }
    }

    private async Task RegisterGuildCommandsAsync(SocketGuild guild)
    {
        try
        {
            var existing = await guild.GetApplicationCommandsAsync().ConfigureAwait(false);
            // AMP-Discord-Bot's per-command toggles are reflected in Discord's
            // actual guild command list. Never bulk-overwrite unrelated commands.
            if (CommandEnabled(_config, InfoCommandName))
            {
                if (!existing.Any(c => c.Name == InfoCommandName))
                    await guild.CreateApplicationCommandAsync(new SlashCommandBuilder()
                        .WithName(InfoCommandName)
                        .WithDescription("Post an updating game server information panel here")
                        .Build()).ConfigureAwait(false);
            }
            else
            {
                foreach (var disabled in existing.Where(c => c.Name == InfoCommandName &&
                             c.Type == ApplicationCommandType.Slash))
                    await disabled.DeleteAsync().ConfigureAwait(false);
            }

            foreach (var (name, action) in ControlNames)
            {
                var registered = existing.Where(c => c.Name == name &&
                    c.Type == ApplicationCommandType.Slash).ToArray();
                if (!CommandEnabled(_config, name))
                {
                    foreach (var disabled in registered)
                        await disabled.DeleteAsync().ConfigureAwait(false);
                    continue;
                }
                if (registered.Length == 0)
                    await guild.CreateApplicationCommandAsync(new SlashCommandBuilder()
                        .WithName(name)
                        .WithDescription(action.ToString() + " the associated AMP game server")
                        .Build()).ConfigureAwait(false);
            }

            // Original AMP-Discord-Bot /console: one required command argument.
            var registeredConsole = existing.Where(c => c.Name == ConsoleCommandName &&
                c.Type == ApplicationCommandType.Slash).ToArray();
            if (!CommandEnabled(_config, ConsoleCommandName) || !_console.Available)
            {
                foreach (var disabled in registeredConsole)
                    await disabled.DeleteAsync().ConfigureAwait(false);
            }
            else if (registeredConsole.Length == 0)
            {
                var consoleCommand = new SlashCommandBuilder()
                    .WithName(ConsoleCommandName)
                    .WithDescription("Send a command to the game server console")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("command")
                        .WithDescription("Console command to send")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true));
                await guild.CreateApplicationCommandAsync(consoleCommand.Build()).ConfigureAwait(false);
            }

            foreach (string name in new[] { "playtime", "full-playtime-list" })
            {
                var found = existing.Where(c => c.Name == name && c.Type == ApplicationCommandType.Slash).ToArray();
                if (!CommandEnabled(_config, name))
                {
                    foreach (var old in found) await old.DeleteAsync().ConfigureAwait(false);
                }
                else if (found.Length == 0)
                {
                    await guild.CreateApplicationCommandAsync(new SlashCommandBuilder()
                        .WithName(name)
                        .WithDescription(name == "playtime" ? "Top players by recorded playtime" :
                            "Player playtime including last-seen dates")
                        .Build()).ConfigureAwait(false);
                }
            }

            // This preview previously registered /server. Retire only that command
            // after /info exists, without bulk-overwriting future bot commands.
            // A deleted command can briefly remain visible in Discord clients;
            // SlashAsync continues to handle it during propagation.
            foreach (var old in existing.Where(c => c.Name == LegacyInfoCommandName &&
                                                    c.Type == ApplicationCommandType.Slash))
            {
                try
                {
                    await old.DeleteAsync().ConfigureAwait(false);
                    BotLog.Write($"Shard {_id:D}: retired /{LegacyInfoCommandName} in guild {guild.Id}.");
                }
                catch (Exception ex)
                {
                    BotLog.Write($"Shard {_id:D}: could not retire /{LegacyInfoCommandName} " +
                        $"in guild {guild.Id}: {AmpFacade.CleanError(ex)}");
                }
            }
        }
        catch (Exception ex)
        {
            BotLog.Write($"Shard {_id:D}: /{InfoCommandName} registration failed in guild {guild.Id}: {AmpFacade.CleanError(ex)}");
        }
    }

    private async Task SlashAsync(SocketSlashCommand command)
    {
        if (command.Data.Name is "playtime" or "full-playtime-list")
        {
            if (!CommandEnabled(_config, command.Data.Name))
            {
                await command.RespondAsync("This command is disabled for this Shard.", ephemeral: true).ConfigureAwait(false);
                return;
            }
            bool full = command.Data.Name == "full-playtime-list";
            string board = _playtime.Leaderboard(full ? 25 : 5, DateTimeOffset.UtcNow, full);
            await command.RespondAsync(board.Length > 1900 ? board[..1900] : board,
                ephemeral: false, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
            return;
        }
        if (command.Data.Name == ConsoleCommandName)
        {
            await ConsoleAsync(command).ConfigureAwait(false);
            return;
        }
        if (ControlNames.TryGetValue(command.Data.Name, out ShardControlAction action))
        {
            if (!CommandEnabled(_config, command.Data.Name))
            {
                await command.RespondAsync("This command is disabled for this Shard.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }
            await ServerControlAsync(action, command.GuildId, command.User, command.Channel,
                async () => await command.DeferAsync(ephemeral: true).ConfigureAwait(false),
                async result => await command.FollowupAsync(result, ephemeral: true).ConfigureAwait(false));
            return;
        }
        if (command.Data.Name != InfoCommandName && command.Data.Name != LegacyInfoCommandName) return;
        await command.DeferAsync(ephemeral: true).ConfigureAwait(false);
        if (!CommandEnabled(_config, InfoCommandName))
        {
            await command.FollowupAsync("The information panel command is disabled for this Shard.", ephemeral: true)
                .ConfigureAwait(false);
            return;
        }
        try
        {
            ShardSnapshot? snapshot = _snapshot;
            if (snapshot is null)
            {
                await command.FollowupAsync("AMP has not reported this Shard's status yet.", ephemeral: true).ConfigureAwait(false);
                return;
            }

            // Hold the gate for the WHOLE read / verification / post / save sequence.
            // Two near-simultaneous /info interactions must not create two panels.
            await _panelGate.WaitAsync().ConfigureAwait(false);
            try
            {
                PanelRegistryState state = PanelRegistry.Load(_id, _config);
                PanelImage? image = await CurrentPanelImageAsync(snapshot, _sessionStop.Token).ConfigureAwait(false);
                if (image is not null && command.GuildId is ulong guildId &&
                    _client.GetGuild(guildId) is { } guild && (ulong)image.Bytes.Length > guild.MaxUploadLimit)
                {
                    image = null;
                    BotLog.Write($"Shard {_id:D} panel image exceeds Discord upload limit for guild {guildId}.");
                }
                var view = InfoPanelRenderer.Render(_config, snapshot, _controls.Available,
                    _playtime.Leaderboard(5, DateTimeOffset.UtcNow), _relayState.ValheimJoinCode,
                    image?.FileName);
                PanelLocation? existing = state.Panels.FirstOrDefault(p => p.ChannelId == command.Channel.Id);
                if (existing is not null)
                {
                    if (_client.GetChannel(existing.ChannelId) is not IMessageChannel channel)
                    {
                        await command.FollowupAsync("Cannot access the existing panel in this channel. Check the bot's channel permissions.",
                            ephemeral: true).ConfigureAwait(false);
                        return;
                    }
                    try
                    {
                        if (await channel.GetMessageAsync(existing.MessageId).ConfigureAwait(false) is IUserMessage message)
                        {
                            if (await UpdatePanelMessageAsync(message, existing, view, image).ConfigureAwait(false))
                                state.RequiresSave = true;
                            if (state.RequiresSave) PanelRegistry.Save(_id, state);
                            await command.FollowupAsync("Updated the existing information panel in this channel.",
                                ephemeral: true).ConfigureAwait(false);
                            return;
                        }
                        // Only an explicitly missing response permits a replacement
                        // from /info. Transport/permission failures must not duplicate it.
                    }
                    catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // The user deleted the old panel; /info may create a new one.
                    }
                    catch (Exception ex)
                    {
                        BotLog.Write($"Shard {_id:D}: cannot verify existing panel: {AmpFacade.CleanError(ex)}");
                        await command.FollowupAsync("Cannot update the existing panel; check permissions and the bot log.",
                            ephemeral: true).ConfigureAwait(false);
                        return;
                    }
                    state.Panels.Remove(existing);
                }

                // Only /info creates panels. Post as a normal guild message
                // after acknowledging the interaction ephemerally.
                IUserMessage posted;
                if (image is not null)
                {
                    using var stream = new MemoryStream(image.Bytes, writable: false);
                    using var outgoing = new FileAttachment(stream, image.FileName);
                    posted = await command.Channel.SendFileAsync(outgoing, embed: view.ImageOnly ? null : view.Embed,
                        components: view.Components, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
                }
                else
                    posted = await command.Channel.SendMessageAsync(
                        embed: view.Embed, components: view.Components,
                        allowedMentions: AllowedMentions.None).ConfigureAwait(false);
                PanelRegistry.Track(state, command.GuildId ?? 0, command.Channel.Id, posted.Id);
                PanelLocation created = state.Panels.First(x => x.MessageId == posted.Id);
                created.ImageHash = image?.Hash;
                created.ImageFileName = image?.FileName;
                PanelRegistry.Save(_id, state);
                BotLog.Write($"Shard {_id:D}: information panel created in Discord channel {command.Channel.Id}.");
                await command.FollowupAsync("Information panel created.", ephemeral: true).ConfigureAwait(false);
            }
            finally { _panelGate.Release(); }
        }
        catch (Exception ex)
        {
            BotLog.Write($"Shard {_id:D} /{command.Data.Name} panel command failed: {AmpFacade.CleanError(ex)}");
            try { await command.FollowupAsync("Unable to update the information panel. Check the Splinter log.", ephemeral: true).ConfigureAwait(false); }
            catch { }
        }
    }

    private async Task ConsoleAsync(SocketSlashCommand command)
    {
        try
        {
            await command.DeferAsync(ephemeral: true).ConfigureAwait(false);
            if (!_config.Enabled || !CommandEnabled(_config, ConsoleCommandName))
            {
                await command.FollowupAsync("Console commands are disabled for this Shard.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }
            if (command.GuildId is not ulong guildId ||
                _client.GetGuild(guildId) is not SocketGuild guild)
            {
                await command.FollowupAsync("Console commands can only be used in a Discord server.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }
            SocketGuildUser? member = command.User as SocketGuildUser;
            bool sameGuild = member is not null && member.Guild.Id == guild.Id;
            var roles = sameGuild ? member!.Roles.Select(r => (r.Id, r.Name)).ToArray() :
                Array.Empty<(ulong Id, string Name)>();
            if (!ServerControlPermissions.Allowed(_config, sameGuild, roles))
            {
                await command.FollowupAsync("You do not have the required Discord server role for this action.",
                    ephemeral: true).ConfigureAwait(false);
                return;
            }
            string? input = command.Data.Options.FirstOrDefault(o => o.Name == "command")?.Value as string;
            if (!ShardConsoleBridge.IsValidCommand(input))
            {
                await command.FollowupAsync("Enter a single-line console command (1–1024 characters).",
                    ephemeral: true).ConfigureAwait(false);
                return;
            }
            if (!_console.Available)
            {
                await command.FollowupAsync("AMP's Shard console interface is unavailable.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }
            if (!await _consoleGate.WaitAsync(0).ConfigureAwait(false))
            {
                await command.FollowupAsync("A console command is already being sent.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await _console.SendAsync(_id, input!, timeout.Token).ConfigureAwait(false);
                await command.FollowupAsync("Console command sent to AMP.", ephemeral: true)
                    .ConfigureAwait(false);
                try { await LogAuditAsync(ServerControlAudit.Message("Console", command.User.Id, _config, _snapshot),
                    command.Channel).ConfigureAwait(false); }
                catch (Exception ex) { BotLog.Write("Console audit delivery failed: " + AmpFacade.CleanError(ex)); }
                // Do not log raw console input: it may contain credentials.
                BotLog.Write($"Shard {_id:D}: console command requested by Discord user {command.User.Id} in guild {guildId}.");
            }
            catch (Exception ex)
            {
                BotLog.Write($"Shard {_id:D}: console input failed: {ex.GetType().Name}");
                await command.FollowupAsync("AMP could not accept that console command. Check the Shard state and Splinter log.",
                    ephemeral: true).ConfigureAwait(false);
            }
            finally { _consoleGate.Release(); }
        }
        catch (Exception ex)
        {
            BotLog.Write($"Shard {_id:D}: console interaction failed: {ex.GetType().Name}");
        }
    }

    private async Task ButtonAsync(SocketMessageComponent component)
    {
        if ((component.Data.CustomId ?? "").StartsWith("splinter:whitelist:", StringComparison.Ordinal))
        {
            await WhitelistButtonAsync(component).ConfigureAwait(false);
            return;
        }
        const string prefix = "splinter:control:";
        string customId = component.Data.CustomId ?? "";
        if (!customId.StartsWith(prefix, StringComparison.Ordinal)) return;
        await component.DeferAsync(ephemeral: true).ConfigureAwait(false);
        if (!Enum.TryParse(customId[prefix.Length..], ignoreCase: true, out ShardControlAction action) ||
            !Enum.IsDefined(action))
        {
            await component.FollowupAsync("Unknown server action.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        // A component can only control this Shard if it belongs to a tracked
        // information panel. Never accept arbitrary user-supplied component IDs.
        bool tracked = PanelRegistry.Load(_id, _config).Panels.Any(p =>
            p.MessageId == component.Message.Id && p.ChannelId == component.Channel.Id &&
            p.GuildId == component.GuildId);
        if (!tracked || !ServerControlPermissions.ShowButton(_config, action))
        {
            await component.FollowupAsync("This server-control button is not enabled.", ephemeral: true)
                .ConfigureAwait(false);
            return;
        }
        await ServerControlAsync(action, component.GuildId, component.User, component.Channel,
            () => Task.CompletedTask,
            async result => await component.FollowupAsync(result, ephemeral: true).ConfigureAwait(false));
    }

    private async Task ServerControlAsync(ShardControlAction action, ulong? guildId, IUser user,
        IMessageChannel interactionChannel, Func<Task> defer, Func<string, Task> reply)
    {
        try
        {
            await defer().ConfigureAwait(false);
            if (!_config.Enabled)
            {
                await reply("This Shard's Discord bot has been disabled.").ConfigureAwait(false);
                return;
            }
            if (guildId is not ulong guildSnowflake ||
                _client.GetGuild(guildSnowflake) is not SocketGuild guild)
            {
                await reply("Server controls can only be used within a Discord server.").ConfigureAwait(false);
                return;
            }
            // Discord provides guild-member role information on interactions.
            // Refuse a non-guild user rather than trusting a global account or
            // falling back to an unrelated guild's role cache.
            SocketGuildUser? member = user as SocketGuildUser;
            bool sameGuild = member is not null && member.Guild.Id == guild.Id;
            var roles = sameGuild ? member!.Roles.Select(r => (r.Id, r.Name)).ToArray() :
                Array.Empty<(ulong Id, string Name)>();
            if (!ServerControlPermissions.Allowed(_config, sameGuild, roles))
            {
                await reply("You do not have the required Discord server role for this action.")
                    .ConfigureAwait(false);
                return;
            }
            if (!_controls.Available(action))
            {
                await reply($"{action} is not available in this AMP 3 build. " +
                    "Splinter needs a compatible AMP Shard action interface.").ConfigureAwait(false);
                return;
            }
            if (!await _controlGate.WaitAsync(0).ConfigureAwait(false))
            {
                await reply("Another server action is already in progress.").ConfigureAwait(false);
                return;
            }
            try
            {
                ShardSnapshot? current = _snapshot;
                if (current is null || !InfoPanelRenderer.ControlEnabled(current, action, _controls.Available(action)))
                {
                    await reply($"{action} is not available while this Shard is in its current state. " +
                        "Wait for the status to change and try again.").ConfigureAwait(false);
                    return;
                }
                // Bound Discord interactions so a stalled AMP action cannot hold
                // this Shard's control gate indefinitely.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                if (action == ShardControlAction.Backup)
                {
                    Guid snapshotId = await _controls.TakeSnapshotAsync(_id, timeout.Token)
                        .ConfigureAwait(false);
                    string name = string.IsNullOrWhiteSpace(_config.ServerDisplayName)
                        ? (_snapshot?.Name ?? _config.ShardName) : _config.ServerDisplayName;
                    bool tracked = false;
                    try
                    {
                        _snapshotFollowUps.Track(snapshotId, user.Id, name, DateTimeOffset.UtcNow);
                        tracked = true;
                    }
                    catch (Exception ex)
                    {
                        BotLog.Write($"Shard {_id:D}: snapshot started, but DM tracking failed: {AmpFacade.CleanError(ex)}");
                    }
                    await reply(tracked
                        ? "AMP started creating the snapshot. I'll DM you when it completes or fails."
                        : "AMP started creating the snapshot, but completion notification tracking " +
                          "could not be saved. Check AMP's Snapshots page for the result.")
                        .ConfigureAwait(false);
                }
                else
                {
                    await _controls.ExecuteAsync(action, _id, timeout.Token).ConfigureAwait(false);
                    await reply($"{action} request dispatched to AMP for this Shard.").ConfigureAwait(false);
                }
                try { await LogControlAsync(action, user.Id, interactionChannel).ConfigureAwait(false); }
                catch (Exception ex) { BotLog.Write("Server-control audit delivery failed: " + AmpFacade.CleanError(ex)); }
                BotLog.Write($"Shard {_id:D} {action} requested by Discord user {user.Id} in guild {guildSnowflake}.");
            }
            catch (Exception ex)
            {
                BotLog.Write($"Shard {_id:D}: {action} failed: {AmpFacade.CleanError(ex)}");
                await reply($"AMP could not confirm the {action} request. Check the Splinter log and Shard state.")
                    .ConfigureAwait(false);
            }
            finally { _controlGate.Release(); }
        }
        catch (Exception ex)
        {
            BotLog.Write($"Shard {_id:D} control interaction failed: {AmpFacade.CleanError(ex)}");
        }
    }

    // Snapshot capture is a background AMP task, not a Discord interaction.
    // Poll only when a request has been recorded, so other Shards and the
    // gateway never wait for a backup. Pending DMs survive service restarts.
    private async Task WatchSnapshotFollowUpsAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_ready && _snapshotFollowUps.Count > 0)
                {
                    foreach (SnapshotFollowUp pending in _snapshotFollowUps.Due(DateTimeOffset.UtcNow))
                    {
                        if (ct.IsCancellationRequested) break;
                        try
                        {
                            using var check = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            check.CancelAfter(TimeSpan.FromSeconds(8));
                            AmpSnapshotProgress? state = await _controls.ReadSnapshotProgressAsync(
                                _id, pending.SnapshotId, check.Token).ConfigureAwait(false);
                            if (state is null || !state.Finished) continue;
                            try
                            {
                                IUser? recipient = await _client.GetUserAsync(pending.RequesterId)
                                    .ConfigureAwait(false);
                                if (recipient is null)
                                    throw new InvalidOperationException("Discord user is unavailable.");
                                await recipient.SendMessageAsync(
                                    SnapshotFollowUpMessage.Text(pending, state),
                                    allowedMentions: AllowedMentions.None).ConfigureAwait(false);
                                _snapshotFollowUps.Delivered(pending.SnapshotId);
                                BotLog.Write($"Shard {_id:D}: snapshot {pending.SnapshotId:D} " +
                                    $"finished ({state.Status}); completion DM delivered.");
                            }
                            catch (Exception ex) when (!ct.IsCancellationRequested)
                            {
                                _snapshotFollowUps.DeliveryFailed(pending.SnapshotId, DateTimeOffset.UtcNow);
                                BotLog.Write($"Shard {_id:D}: snapshot completion DM failed " +
                                    $"({ex.GetType().Name}); notification will retry if possible.");
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                        catch (Exception ex)
                        {
                            // AMP's snapshot graph may be temporarily unavailable;
                            // preserve the pending record until the service returns.
                            BotLog.Write($"Shard {_id:D}: snapshot completion check unavailable: " +
                                AmpFacade.CleanError(ex));
                        }
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            BotLog.Write($"Shard {_id:D}: snapshot notification worker stopped: " + AmpFacade.CleanError(ex));
        }
    }

    private async Task LogControlAsync(ShardControlAction action, ulong userId, IMessageChannel origin)
    {
        if (!_config.LogButtonPressesAndCommands) return;
        await LogAuditAsync(ServerControlAudit.Message(action, userId, _config, _snapshot), origin)
            .ConfigureAwait(false);
    }

    private async Task LogAuditAsync(string entry, IMessageChannel origin)
    {
        if (!_config.LogButtonPressesAndCommands) return;
        if (string.IsNullOrWhiteSpace(_config.ButtonLogChannel))
        {
            await origin.SendMessageAsync(entry, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
            return;
        }
        // A name can exist in multiple Discord guilds. Never leak command logs
        // into another guild just because its channel has the same name.
        if (origin is not SocketGuildChannel originChannel) return;
        foreach (var channel in originChannel.Guild.TextChannels.Where(c =>
            LifecycleNotifications.MatchesChannel(_config.ButtonLogChannel, c.Id, c.Name)))
        {
            try { await channel.SendMessageAsync(entry, allowedMentions: AllowedMentions.None).ConfigureAwait(false); }
            catch (Exception ex) { BotLog.Write("Command audit delivery failed: " + AmpFacade.CleanError(ex)); }
        }
    }

    public async Task UpdateAsync(ShardSnapshot snapshot, CancellationToken ct)
    {
        _snapshot = snapshot;
        if (_disposed || !_ready || _client.ConnectionState != ConnectionState.Connected) return;
        await _updateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Status notifications take priority over slower panel and console REST calls.
            // Read-only lifecycle notifications; no administrative API access.
            // Separate failure handling ensures information panels still work.
            if (_config.NotifyServerStatus &&
                LifecycleNotifications.IsValidChannel(_config.StatusNotificationChannel))
            {
                try { await NotifyLifecycleAsync(snapshot, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    BotLog.Write($"Shard {_id:D} lifecycle notification error: {AmpFacade.CleanError(ex)}");
                }
            }
            // Respect the normal rate limit for metrics, but publish status
            // transitions as soon as AMP reports them.
            if (DateTimeOffset.UtcNow - _presenceAt >= TimeSpan.FromSeconds(_presenceSeconds) ||
                !string.Equals(snapshot.Status, _lastPresenceStatus, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    IReadOnlyDictionary<string, string> variables;
                    try
                    {
                        variables = PresenceVariables.Read(_id);
                        _lastVariablesError = null;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                    {
                        // A malformed user variables file must not break presence/panel updates.
                        string error = ex.GetType().Name + ": " + ex.Message;
                        if (_lastVariablesError != error)
                            BotLog.Write($"Shard {_id:D} custom presence variables ignored: {error}");
                        _lastVariablesError = error;
                        variables = new Dictionary<string, string>();
                    }
                    string presence = PresenceRenderer.Render(_config, snapshot, variables);
                    UserStatus online = snapshot.IsRunning ? UserStatus.Online :
                        snapshot.Status.Equals("Stopped", StringComparison.OrdinalIgnoreCase) ||
                        snapshot.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase)
                            ? UserStatus.DoNotDisturb : UserStatus.Idle;
                    if (_lastUserStatus != online)
                    {
                        await _client.SetStatusAsync(online).ConfigureAwait(false);
                        _lastUserStatus = online;
                    }
                    if (_lastPresence != presence || _lastPresenceType != _config.PresenceType)
                    {
                        if (_config.PresenceType == BotPresenceType.CustomStatus)
                            await _client.SetCustomStatusAsync(presence).ConfigureAwait(false);
                        else
                            await _client.SetGameAsync(presence, type: _config.PresenceType switch
                            {
                                BotPresenceType.Watching => ActivityType.Watching,
                                BotPresenceType.Listening => ActivityType.Listening,
                                BotPresenceType.Competing => ActivityType.Competing,
                                _ => ActivityType.Playing
                            }).ConfigureAwait(false);
                        _lastPresence = presence;
                        _lastPresenceType = _config.PresenceType;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // Discord may reject an activity update. Panels still have to work.
                    BotLog.Write($"Shard {_id:D} presence update failed: {AmpFacade.CleanError(ex)}");
                }
                finally { _presenceAt = DateTimeOffset.UtcNow; _lastPresenceStatus = snapshot.Status; }
            }
            PlayerRosterDelta playerChanges = UpdatePlayers(snapshot);
            await UpdatePanelsAsync(snapshot).ConfigureAwait(false);
            await PublishPlayerTransitionsAsync(playerChanges, ct).ConfigureAwait(false);
            // Console history is optional and slower; process it LAST so it
            // never delays primary status and panel delivery.
            try { await PollConsoleAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                string error = ex.GetType().Name + ": " + AmpFacade.CleanError(ex);
                if (_relayError != error) BotLog.Write($"Shard {_id:D} console output unavailable: {error}");
                _relayError = error;
            }

        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { BotLog.Write($"Shard {_id:D} status publish failed: {AmpFacade.CleanError(ex)}"); }
        finally { _updateGate.Release(); }
    }

    private async Task NotifyLifecycleAsync(ShardSnapshot snapshot, CancellationToken ct)
    {
        // The Shard status is recorded before publishing: no duplicate alerts on
        // AMP restarts, Discord reconnections or subsequent polling iterations.
        LifecycleNotifications.Transition? changed = LifecycleNotifications.Observe(_id, snapshot);
        if (changed is null) return;

        string destination = _config.StatusNotificationChannel;
        var channels = _client.Guilds.SelectMany(g => g.TextChannels)
            .Where(c => LifecycleNotifications.MatchesChannel(destination, c.Id, c.Name))
            .DistinctBy(c => c.Id)
            .ToArray();
        if (channels.Length == 0)
        {
            BotLog.Write($"Shard {_id:D}: lifecycle transition observed, but no matching Discord text channel is available.");
            return;
        }

        string message = LifecycleNotifications.Message(_config, snapshot, changed);
        foreach (SocketTextChannel channel in channels)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await channel.SendMessageAsync(message, allowedMentions: AllowedMentions.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                BotLog.Write($"Shard {_id:D}: lifecycle notification to channel {channel.Id} failed: {AmpFacade.CleanError(ex)}");
            }
        }
    }

    // AMP agent's ConnectedUserEvent is immediate. Roster reads remain the
    // authoritative recovery path after missed events or reconnects.
    public async Task HandleAmpPlayerEventAsync(AmpPlayerEvent evt, CancellationToken ct)
    {
        if (evt.ShardId != _id || _disposed || !_config.Enabled) return;
        if (evt.EventId != Guid.Empty)
        {
            lock (_eventSync)
            {
                if (!_seenPlayerEvents.Add(evt.EventId)) return;
                _seenPlayerEventOrder.Enqueue(evt.EventId);
                if (_seenPlayerEventOrder.Count > 2048)
                    _seenPlayerEvents.Remove(_seenPlayerEventOrder.Dequeue());
            }
        }
        bool changed = evt.Kind == "Joined"
            ? _playtime.PlayerJoined(evt.Player, evt.Timestamp)
            : _playtime.PlayerLeft(evt.Player.Id, evt.Timestamp);
        if (!changed) return;
        _playtime.Save(_id);
        _lastPlaytimeSave = DateTimeOffset.UtcNow;
        if (!_ready) return;
        await PublishPlayerTransitionsAsync(evt.Kind == "Joined"
            ? new PlayerRosterDelta([evt.Player], [])
            : new PlayerRosterDelta([], [evt.Player]), ct).ConfigureAwait(false);
    }

    private PlayerRosterDelta UpdatePlayers(ShardSnapshot snapshot)
    {
        // An unavailable AMP roster is not evidence that everyone disconnected.
        if (snapshot.IsRunning && snapshot.OnlinePlayers is null)
            return new PlayerRosterDelta([], []);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var changes = snapshot.IsRunning
            ? _playtime.Reconcile(snapshot.OnlinePlayers!, now)
            : _playtime.Reconcile(Array.Empty<OnlinePlayer>(), now);
        bool announce = _rosterSeenThisSession;
        _rosterSeenThisSession = true;
        if (changes.Joined.Count != 0 || changes.Left.Count != 0 ||
            _lastPlaytimeSave == DateTimeOffset.MinValue ||
            now - _lastPlaytimeSave >= TimeSpan.FromSeconds(60))
        {
            _playtime.Save(_id);
            _lastPlaytimeSave = now;
        }
        return announce ? changes : new PlayerRosterDelta([], []);
    }

    private async Task PublishPlayerTransitionsAsync(PlayerRosterDelta changes, CancellationToken ct)
    {
        if (!_config.PostJoinLeaveMessages ||
            !LifecycleNotifications.IsValidChannel(_config.JoinLeaveMessagesChannel)) return;
        foreach (OnlinePlayer joined in changes.Joined)
        {
            ct.ThrowIfCancellationRequested();
            await SendToConfiguredChannelAsync(_config.JoinLeaveMessagesChannel,
                $"{ConsoleRelay.SafeLine(joined.Name)} joined the server.", ct).ConfigureAwait(false);
        }
        foreach (OnlinePlayer left in changes.Left)
        {
            ct.ThrowIfCancellationRequested();
            await SendToConfiguredChannelAsync(_config.JoinLeaveMessagesChannel,
                $"{ConsoleRelay.SafeLine(left.Name)} left the server.", ct).ConfigureAwait(false);
        }
    }

    // Keep periodic history reconciliation even while the NATS listener is
    // active. Establishing an ordered consumer does not prove it delivers any
    // records; a silent live stream must never disable the working relay.
    // The shared gate and cursor deduplicate history/live deliveries.
    private async Task<bool> PollConsoleAsync(CancellationToken ct, bool force = false)
    {
        if (!_config.TraceConsoleMetadata)
            _metadataProbe.Observe(false, _id, Array.Empty<ConsoleEntry>());
        if (!ConsoleRelayRequested || !_history.Available || !_ready) return false;
        await _relayGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && !ConsoleRelay.ShouldReconcile(_relayAt, DateTimeOffset.UtcNow,
                    _config.ConsoleToDiscordBatchTimer)) return false;
            _relayAt = DateTimeOffset.UtcNow;
            using var historyTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            historyTimeout.CancelAfter(TimeSpan.FromSeconds(6));
            IReadOnlyList<ConsoleEntry> latest;
            try { latest = await _history.LatestAsync(_id, historyTimeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException("AMP console history did not respond within six seconds."); }
            long priorSequence = _relayState.LastSequence;
            bool wasInitialized = _relayState.Initialized;
            string priorCode = _relayState.ValheimJoinCode;
            var unread = ConsoleRelay.Consume(_relayState, latest,
                allowSequenceReset: !_liveConsoleActive);
            // Valheim codes are also recovered from history when initially
            // attaching to a server that was already running.
            if (_config.ValheimJoinCode)
                foreach (ConsoleEntry record in latest)
                    if (ConsoleRelay.ValheimCode(record.Text) is string code) _relayState.ValheimJoinCode = code;
            if (priorSequence != _relayState.LastSequence || wasInitialized != _relayState.Initialized ||
                priorCode != _relayState.ValheimJoinCode)
                ConfigStore.Write(ConsoleRelay.PathFor(_id), _relayState);
            _relayError = null;
            await ForwardConsoleEntriesAsync(unread, "history", ct).ConfigureAwait(false);
            return true;
        }
        finally { _relayGate.Release(); }
    }

    private bool ConsoleRelayRequested => _config.SendConsoleToDiscord || _config.SendChatToDiscord ||
        _config.ValheimJoinCode || _config.TraceConsoleMetadata;

    // Own JetStream ordered consumer for this bot: slow Discord delivery on
    // one Shard cannot delay console events for any other Shard.
    private async Task RunLiveConsoleAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_ready || !ConsoleRelayRequested || !_liveConsole.Available)
            {
                if (_ready && ConsoleRelayRequested && !_liveConsole.Available && !_liveConsoleMissingLogged)
                {
                    BotLog.Write($"Shard {_id:D} AMP live console interfaces unavailable; history polling remains active.");
                    _liveConsoleMissingLogged = true;
                }
                _liveConsoleActive = false;
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                continue;
            }
            try
            {
                // Reconcile the last history page first; then resume by stream
                // sequence so messages published during setup are not lost.
                bool seeded = false;
                try { seeded = await PollConsoleAsync(ct, force: true).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    BotLog.Write($"Shard {_id:D} live console initial history unavailable: " +
                        AmpFacade.CleanError(ex));
                }
                long? cursor = null;
                await _relayGate.WaitAsync(ct).ConfigureAwait(false);
                try { if (seeded && _relayState.Initialized) cursor = _relayState.LastSequence; }
                finally { _relayGate.Release(); }

                await _liveConsole.ListenAsync(_id, cursor, HandleLiveConsoleEntryAsync, () =>
                {
                    _liveConsoleActive = true;
                    _lastLiveConsoleError = null;
                    BotLog.Write($"Shard {_id:D} AMP live console listener active (awaiting records; history reconciliation enabled).");
                }, diagnostic => BotLog.Write($"Shard {_id:D} AMP live console: {diagnostic}"), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Fail closed to the established history-polling mechanism.
                string error = AmpFacade.CleanError(ex);
                if (!string.Equals(_lastLiveConsoleError, error, StringComparison.Ordinal))
                    BotLog.Write($"Shard {_id:D} AMP live console unavailable (history polling active): {error}");
                _lastLiveConsoleError = error;
            }
            finally { _liveConsoleActive = false; }
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
        _liveConsoleActive = false;
    }

    private async Task HandleLiveConsoleEntryAsync(ConsoleEntry entry, CancellationToken ct)
    {
        if (!_ready) throw new InvalidOperationException("Discord gateway is not ready.");
        await _relayGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Advance the cursor even if settings were disabled while the
            // subscription was active, so a later re-enable cannot replay
            // console messages captured during the disabled interval.
            bool forward = ConsoleRelayRequested;
            long prior = _relayState.LastSequence;
            bool initialized = _relayState.Initialized;
            string oldCode = _relayState.ValheimJoinCode;
            IReadOnlyList<ConsoleEntry> unread = ConsoleRelay.ConsumeLive(_relayState, entry);
            if (_config.ValheimJoinCode && ConsoleRelay.ValheimCode(entry.Text) is string code)
                _relayState.ValheimJoinCode = code;
            if (prior != _relayState.LastSequence || initialized != _relayState.Initialized ||
                oldCode != _relayState.ValheimJoinCode)
                ConfigStore.Write(ConsoleRelay.PathFor(_id), _relayState);
            if (forward)
                await ForwardConsoleEntriesAsync(unread, "jetstream", ct).ConfigureAwait(false);
        }
        finally { _relayGate.Release(); }
    }

    private async Task ForwardConsoleEntriesAsync(IReadOnlyList<ConsoleEntry> entries,
        string source, CancellationToken ct)
    {
        foreach (string sample in _metadataProbe.Observe(_config.TraceConsoleMetadata, _id, entries, source))
            BotLog.Write(sample);
        if (entries.Count == 0) return;
        var consoleLines = new List<string>();
        foreach (ConsoleEntry entry in entries)
        {
            string line = entry.Text;
            if (ConsoleRelay.Excluded(line, _config.ExcludeConsoleOutput)) continue;
            if (_config.SendConsoleToDiscord) consoleLines.Add(line);
            if (ConsoleChatRelay.TryFormat(_config, entry, out string message) &&
                !_chatEchoes.IsEcho(line, DateTimeOffset.UtcNow))
                await SendToConfiguredChannelAsync(_config.ChatDiscordChannel, message, ct)
                    .ConfigureAwait(false);
        }
        if (_config.SendConsoleToDiscord)
            foreach (string batch in ConsoleRelay.Batches(consoleLines).Take(8))
                await SendToConfiguredChannelAsync(_config.ConsoleDiscordChannel,
                    "```text\n" + batch + "\n```", ct).ConfigureAwait(false);
    }

    private async Task SendToConfiguredChannelAsync(string destination, string content, CancellationToken ct)
    {
        if (!LifecycleNotifications.IsValidChannel(destination)) return;
        foreach (SocketTextChannel channel in _client.Guilds.SelectMany(g => g.TextChannels)
                     .Where(c => LifecycleNotifications.MatchesChannel(destination, c.Id, c.Name))
                     .DistinctBy(c => c.Id))
        {
            ct.ThrowIfCancellationRequested();
            try { await channel.SendMessageAsync(content, allowedMentions: AllowedMentions.None).ConfigureAwait(false); }
            catch (Exception ex) { BotLog.Write($"Shard {_id:D} Discord relay delivery to {channel.Id} failed: {AmpFacade.CleanError(ex)}"); }
        }
    }

    private async Task WhitelistButtonAsync(SocketMessageComponent component)
    {
        string customId = component.Data.CustomId ?? "";
        if (!_config.DisplayWhitelistRequestButton)
        {
            await component.RespondAsync("Whitelist requests are disabled.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        if (customId == "splinter:whitelist:request")
        {
            if (!InfoPanelRenderer.WhitelistReady(_config))
            {
                await component.RespondAsync("Configure the whitelist request channel and approval role in AMP first.",
                    ephemeral: true).ConfigureAwait(false);
                return;
            }
            // No one may open a request modal from an unrelated Discord message.
            bool tracked = PanelRegistry.Load(_id, _config).Panels.Any(p =>
                p.MessageId == component.Message.Id && p.ChannelId == component.Channel.Id &&
                p.GuildId == component.GuildId);
            if (!tracked || component.GuildId is null)
            {
                await component.RespondAsync("This is not a tracked Splinter information panel.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }
            var modal = new ModalBuilder()
                .WithTitle("Whitelist Request")
                .WithCustomId("splinter:whitelist:modal:" + _id.ToString("N"))
                .AddTextInput(_config.WhitelistPlayerIdType == WhitelistPlayerIdType.SteamID
                    ? "Steam64 ID" : "Minecraft username", "player", TextInputStyle.Short,
                    required: true, maxLength: 32)
                .Build();
            await component.RespondWithModalAsync(modal).ConfigureAwait(false);
            return;
        }

        string[] parts = customId.Split(':');
        if (parts.Length != 4 || parts[0] != "splinter" || parts[1] != "whitelist" ||
            parts[2] is not ("approve" or "deny") || !Guid.TryParseExact(parts[3], "N", out Guid requestId))
        {
            await component.RespondAsync("Unknown whitelist operation.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        if (component.GuildId is not ulong guildId || component.User is not SocketGuildUser member ||
            member.Guild.Id != guildId ||
            !WhitelistRules.HasRole(member.Roles.Select(r => (r.Id, r.Name)), _config.WhitelistApprovalRole))
        {
            await component.RespondAsync("You do not have the required whitelist-approval role.", ephemeral: true)
                .ConfigureAwait(false);
            return;
        }
        await component.DeferAsync(ephemeral: true).ConfigureAwait(false);
        await _whitelistGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = WhitelistRequestState.Load(_id);
            WhitelistRequest? request = state.Requests.SingleOrDefault(r => r.Id == requestId &&
                r.Status == "Pending" && r.ChannelId == component.Channel.Id &&
                r.GuildId == guildId && r.MessageId == component.Message.Id);
            if (request is null)
            {
                await component.FollowupAsync("This request is no longer pending or is not from this Shard.",
                    ephemeral: true).ConfigureAwait(false);
                return;
            }
            if (parts[2] == "approve")
            {
                if (!_console.Available)
                {
                    await component.FollowupAsync("AMP console input is unavailable.", ephemeral: true)
                        .ConfigureAwait(false);
                    return;
                }
                string command = WhitelistRules.BuildCommand(_config.CustomWhitelistCommand,
                    request.Player, _config.WhitelistPlayerIdType);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await _console.SendAsync(_id, command, timeout.Token).ConfigureAwait(false);
                request.Status = "Approved";
            }
            else request.Status = "Denied";
            state.Save(_id);
            if (component.Message is IUserMessage reviewMessage)
            {
                try
                {
                    await reviewMessage.ModifyAsync(x =>
                    {
                        x.Content = "";
                        x.Embed = WhitelistRequestEmbed.Render(request,
                            string.IsNullOrWhiteSpace(_config.ServerDisplayName) ?
                                (_snapshot?.Name ?? _config.ShardName) : _config.ServerDisplayName,
                            component.User.Username);
                        x.Components = new ComponentBuilder().Build();
                        x.AllowedMentions = AllowedMentions.None;
                    }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    BotLog.Write($"Shard {_id:D} whitelist decision saved but embed update failed: {AmpFacade.CleanError(ex)}");
                }
            }
            await component.FollowupAsync($"Request {request.Status.ToLowerInvariant()}.", ephemeral: true)
                .ConfigureAwait(false);
            try
            {
                if (await _client.GetUserAsync(request.RequesterId).ConfigureAwait(false) is IUser requester)
                    await requester.SendMessageAsync($"Your whitelist request for {request.Player} was {request.Status.ToLowerInvariant()}.",
                        allowedMentions: AllowedMentions.None).ConfigureAwait(false);
            }
            catch { /* user DMs are optional */ }
        }
        catch (Exception ex)
        {
            BotLog.Write($"Shard {_id:D} whitelist review failed: {AmpFacade.CleanError(ex)}");
            await component.FollowupAsync("AMP could not complete that whitelist request.", ephemeral: true)
                .ConfigureAwait(false);
        }
        finally { _whitelistGate.Release(); }
    }

    private async Task WhitelistModalAsync(SocketModal modal)
    {
        string expectedId = "splinter:whitelist:modal:" + _id.ToString("N");
        if (modal.Data.CustomId != expectedId) return;
        await modal.DeferAsync(ephemeral: true).ConfigureAwait(false);
        string? player = modal.Data.Components.FirstOrDefault(c => c.CustomId == "player")?.Value?.Trim();
        if (!_config.Enabled || !_config.DisplayWhitelistRequestButton ||
            !InfoPanelRenderer.WhitelistReady(_config) ||
            !WhitelistRules.ValidPlayer(player, _config.WhitelistPlayerIdType))
        {
            await modal.FollowupAsync("Enter a valid username or Steam64 ID.", ephemeral: true)
                .ConfigureAwait(false);
            return;
        }
        await _whitelistGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (modal.GuildId is not ulong originGuild ||
                modal.User is not SocketGuildUser member || member.Guild.Id != originGuild)
            {
                await modal.FollowupAsync("Whitelist requests must originate in a Discord server.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }
            var roles = member.Roles.Select(r => (r.Id, r.Name)).ToArray();
            bool auto = WhitelistRules.HasRole(roles, _config.WhitelistAutoApproveRole);
            if (auto)
            {
                if (!_console.Available)
                {
                    await modal.FollowupAsync("AMP console input is unavailable.", ephemeral: true)
                        .ConfigureAwait(false);
                    return;
                }
                string command = WhitelistRules.BuildCommand(_config.CustomWhitelistCommand,
                    player!, _config.WhitelistPlayerIdType);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await _console.SendAsync(_id, command, timeout.Token).ConfigureAwait(false);
                await modal.FollowupAsync("Whitelist request automatically approved.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }
            var target = _client.Guilds.SelectMany(g => g.TextChannels)
                .FirstOrDefault(c => LifecycleNotifications.MatchesChannel(
                    _config.WhitelistRequestChannel, c.Id, c.Name));
            if (target is null)
            {
                await modal.FollowupAsync("Whitelist request channel is unavailable.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }
            var request = new WhitelistRequest
            {
                Player = player!, RequesterId = modal.User.Id,
                GuildId = target.Guild.Id, ChannelId = target.Id
            };
            var buttons = new ComponentBuilder()
                .WithButton("Approve", "splinter:whitelist:approve:" + request.Id.ToString("N"), ButtonStyle.Success)
                .WithButton("Deny", "splinter:whitelist:deny:" + request.Id.ToString("N"), ButtonStyle.Danger)
                .Build();
            IUserMessage posted = await target.SendMessageAsync(
                embed: WhitelistRequestEmbed.Render(request,
                    string.IsNullOrWhiteSpace(_config.ServerDisplayName) ?
                        (_snapshot?.Name ?? _config.ShardName) : _config.ServerDisplayName),
                components: buttons, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
            request.MessageId = posted.Id;
            var state = WhitelistRequestState.Load(_id);
            state.Requests.Add(request);
            state.Save(_id);
            await modal.FollowupAsync("Whitelist request submitted for review.", ephemeral: true)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BotLog.Write($"Shard {_id:D} whitelist request failed: {AmpFacade.CleanError(ex)}");
            await modal.FollowupAsync("Whitelist request could not be submitted.", ephemeral: true)
                .ConfigureAwait(false);
        }
        finally { _whitelistGate.Release(); }
    }

    private async Task ChatReceivedAsync(SocketMessage message)
    {
        if (!_config.Enabled || !_config.SendChatFromDiscordToServer || !_chatIntentEnabled ||
            message.Author.IsBot || message is not SocketUserMessage msg ||
            msg.Channel is not SocketTextChannel channel ||
            !LifecycleNotifications.MatchesChannel(_config.ChatDiscordChannel, channel.Id, channel.Name) ||
            string.IsNullOrWhiteSpace(msg.Content) || !_console.Available) return;
        // This original feature is intentionally Minecraft-only: the Minecraft
        // 'say' command is NOT suitable for arbitrary games or a shell.
        string clean = msg.Content.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (clean.Length > 350) clean = clean[..350];
        string sender = msg.Author.Username.Replace('\r', ' ').Replace('\n', ' ');
        if (sender.Length > 32) sender = sender[..32];
        string command = $"say [Discord] {sender}: {clean}";
        if (!ShardConsoleBridge.IsValidCommand(command)) return;
        // Claim synchronously before the first await. This prevents repeated
        // gateway callbacks (or a newly created session) from sending the same
        // Discord message to this Shard multiple times.
        if (!InboundChatMessages.TryClaim(_id, msg.Id, DateTimeOffset.UtcNow))
        {
            BotLog.Write($"Shard {_id:D} duplicate Discord chat event ignored; messageId={msg.Id}");
            return;
        }
        string echo = $"[Discord] {sender}: {clean}";
        // Register before sending: AMP may emit the console echo before the
        // console input task returns. Retract it if command delivery fails.
        _chatEchoes.Record(echo, DateTimeOffset.UtcNow);
        BotLog.Write($"Shard {_id:D} Discord chat console publish attempt; messageId={msg.Id}");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _console.SendAsync(_id, command, timeout.Token).ConfigureAwait(false);
            BotLog.Write($"Shard {_id:D} Discord chat console publish completed; messageId={msg.Id}");
        }
        catch (Exception ex)
        {
            _chatEchoes.Forget(echo);
            // A failed/timed-out publish may still have reached AMP. Keep the
            // message ID claimed; do not automatically risk a second send.
            BotLog.Write($"Shard {_id:D} Discord chat relay failed; messageId={msg.Id}: {AmpFacade.CleanError(ex)}");
        }
    }

    private async Task<PanelImage?> CurrentPanelImageAsync(ShardSnapshot snapshot, CancellationToken ct)
    {
        if (_config.PanelStyle == InfoPanelStyle.Pretty)
        {
            try { return await CurrentPrettyPanelAsync(snapshot, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                string reason = AmpFacade.CleanError(ex);
                if (_imageError != reason)
                    BotLog.Write($"Shard {_id:D} pretty panel unavailable; using Classic: {reason}");
                _imageError = reason;
                _prettyCachedImage = null;
                _prettyCacheKey = null;
                _imageStamp = "pretty-failed";
                return null;
            }
        }
        return await CurrentClassicPanelImageAsync(ct).ConfigureAwait(false);
    }

    private async Task<PanelImage?> CurrentPrettyPanelAsync(ShardSnapshot snapshot, CancellationToken ct)
    {
        string overrideFile = _config.PrettyPanelBackgroundFile;
        byte[]? background = null;
        string backgroundStamp = "generic";
        if (!string.IsNullOrWhiteSpace(overrideFile))
        {
            if (_appDirectory is null)
                _appDirectory = await _appFolderBridge.ResolveAsync(_id, ct).ConfigureAwait(false);
            backgroundStamp = PanelImageFiles.Stamp(_appDirectory, overrideFile);
            // Cache local images by file stamp to avoid rereading large files
            // on every AMP metrics refresh.
            if (_prettyOverrideStamp != backgroundStamp)
            {
                _prettyOverrideBytes = PanelImageFiles.Read(_appDirectory, overrideFile)?.Bytes;
                if (!PrettyPanelArtwork.SafeImage(_prettyOverrideBytes)) _prettyOverrideBytes = null;
                _prettyOverrideStamp = backgroundStamp;
                if (_prettyOverrideBytes is null)
                    BotLog.Write($"Shard {_id:D}: Pretty Panel override missing or invalid; trying AMP artwork.");
            }
            background = _prettyOverrideBytes;
        }
        if (background is null)
        {
            if (DateTimeOffset.UtcNow >= _ampArtworkRetryAt)
            {
                _ampArtworkRetryAt = DateTimeOffset.UtcNow.AddMinutes(30);
                try
                {
                    string? source = await _artworkBridge.ReadSourceAsync(_id, snapshot.Name, ct).ConfigureAwait(false);
                    if (!string.Equals(source, _ampArtworkSource, StringComparison.Ordinal))
                    {
                        _ampArtwork = null;
                        _ampArtworkSource = source;
                    }
                    if (_ampArtwork is null)
                        _ampArtwork = await PrettyPanelArtwork.DownloadAsync(source, ct).ConfigureAwait(false);
                    if (_ampArtwork is not null)
                    {
                        _ampArtworkRetryAt = DateTimeOffset.UtcNow.AddHours(6);
                        BotLog.Write($"Shard {_id:D}: Pretty Panel loaded AMP template artwork (cached in memory).");
                    }
                    else if (source?.StartsWith("amp-icon:", StringComparison.Ordinal) == true)
                        BotLog.Write($"Shard {_id:D}: active AMP Web API Minecraft artwork file unavailable; Pretty Panel will use a neutral background.");
                    else if (source is null || PrettyPanelArtwork.ToUri(source) is null)
                        BotLog.Write($"Shard {_id:D}: AMP template image source unavailable; Pretty Panel will use a neutral background.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    BotLog.Write($"Shard {_id:D}: AMP template artwork unavailable (neutral background used): " +
                        AmpFacade.CleanError(ex));
                }
            }
            background = _ampArtwork;
            backgroundStamp += "|amp:" + _ampArtworkSource + "|" + (_ampArtwork?.Length ?? 0);
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string leaderboard = _config.DisplayPlaytimeLeaderboard
            ? _playtime.Leaderboard(5, now) : "";
        string onlineNames = _config.DisplayOnlinePlayerList
            ? string.Join("|", snapshot.OnlinePlayers?.Select(p => p.Name) ?? []) : "";
        string key = string.Join("|", _configFingerprint, snapshot.Name, snapshot.Status, snapshot.IsRunning,
            snapshot.Players, snapshot.MaxPlayers, snapshot.CpuPercent is double cpu ? Math.Round(cpu / 5) * 5 : -1,
            snapshot.MemoryMb is double memory ? Math.Round(memory / 32) * 32 : -1,
            snapshot.MemoryLimitMb, snapshot.StartedAt, now.ToUnixTimeSeconds() / 60,
            leaderboard, onlineNames, _relayState.ValheimJoinCode, backgroundStamp);
        if (_prettyCachedImage is not null && _prettyCacheKey == key)
        {
            _imageStamp = _prettyCachedImage.Hash;
            return _prettyCachedImage;
        }
        // The image contains all visible sections, so do not serve an old
        // bitmap when roster, leaderboard, join code or presentation changes.
        PanelImage image = PrettyPanelRenderer.Render(_config, snapshot, background, now,
            leaderboard, _relayState.ValheimJoinCode);
        _prettyCachedImage = image;
        _prettyCacheKey = key;
        _imageStamp = image.Hash;
        _imageError = null;
        return image;
    }

    private async Task<PanelImage?> CurrentClassicPanelImageAsync(CancellationToken ct)
    {
        string configured = _config.InfoPanelImageFile;
        if (string.IsNullOrWhiteSpace(configured))
        {
            _cachedImage = null;
            _imageStamp = "";
            return null;
        }
        try
        {
            if (_appDirectory is null)
                _appDirectory = await _appFolderBridge.ResolveAsync(_id, ct).ConfigureAwait(false);
            string stamp = PanelImageFiles.Stamp(_appDirectory, configured);
            if (_imageStamp != stamp)
            {
                _cachedImage = PanelImageFiles.Read(_appDirectory, configured);
                _imageStamp = stamp;
            }
            _imageError = null;
            return _cachedImage;
        }
        catch (Exception ex)
        {
            string reason = AmpFacade.CleanError(ex);
            if (_imageError != reason)
                BotLog.Write($"Shard {_id:D} information-panel image unavailable: {reason}");
            _imageError = reason;
            return null;
        }
    }

    private static bool HasPanelAttachment(IUserMessage message, string filename) =>
        message.Attachments.Any(a => a.Filename.Equals(filename, StringComparison.OrdinalIgnoreCase));

    // Edit the existing message; never post a replacement panel as a side effect.
    private static async Task<bool> UpdatePanelMessageAsync(IUserMessage message,
        PanelLocation panel, InfoPanelRenderer.PanelView view, PanelImage? image)
    {
        bool upload = image is not null && (panel.ImageHash != image.Hash ||
            panel.ImageFileName != image.FileName || !HasPanelAttachment(message, image.FileName));
        bool clear = image is null && (panel.ImageHash is not null ||
            message.Attachments.Any(a => a.Filename.StartsWith("splinter-info.", StringComparison.OrdinalIgnoreCase)));
        if (upload)
        {
            using var stream = new MemoryStream(image!.Bytes, writable: false);
            using var uploadFile = new FileAttachment(stream, image.FileName);
            await message.ModifyAsync(x =>
            {
                x.Embeds = InfoPanelRenderer.MessageEmbeds(view);
                x.Components = view.Components ?? new ComponentBuilder().Build();
                x.Attachments = new[] { uploadFile };
            }).ConfigureAwait(false);
        }
        else
        {
            await message.ModifyAsync(x =>
            {
                x.Embeds = InfoPanelRenderer.MessageEmbeds(view);
                x.Components = view.Components ?? new ComponentBuilder().Build();
                if (clear) x.Attachments = Array.Empty<FileAttachment>();
            }).ConfigureAwait(false);
        }
        bool changed = panel.ImageHash != image?.Hash || panel.ImageFileName != image?.FileName;
        panel.ImageHash = image?.Hash;
        panel.ImageFileName = image?.FileName;
        return changed;
    }

    private async Task UpdatePanelsAsync(ShardSnapshot snapshot)
    {
        string contentKey = $"{snapshot.Name}|{snapshot.Status}|{snapshot.IsRunning}|{snapshot.Players}|{snapshot.MaxPlayers}|{snapshot.CpuPercent}|{snapshot.MemoryMb}|{snapshot.MemoryLimitMb}|{(_config.DisplayPlaytimeLeaderboard ? _playtime.Leaderboard(5, DateTimeOffset.UtcNow) : "")}|{(_config.ValheimJoinCode ? _relayState.ValheimJoinCode : "")}|{(_config.DisplayOnlinePlayerList ? string.Join(",", snapshot.OnlinePlayers?.Select(p => p.Name) ?? Array.Empty<string>()) : "")}";
        PanelImage? candidate = await CurrentPanelImageAsync(snapshot, _sessionStop.Token).ConfigureAwait(false);
        string imageFingerprint = _imageStamp ?? "";
        bool imageChanged = imageFingerprint != _lastPanelImageStamp;
        if (contentKey == _lastPanelContent && !imageChanged) return;
        // Preserve immediate status transitions, even when the normal panel
        // refresh interval has not elapsed. Metrics-only changes are throttled.
        if (_lastPanelContent is not null &&
            string.Equals(snapshot.Status, _lastPanelStatus, StringComparison.OrdinalIgnoreCase) &&
            DateTimeOffset.UtcNow - _panelAt < TimeSpan.FromSeconds(_presenceSeconds) && !imageChanged) return;

        await _panelGate.WaitAsync().ConfigureAwait(false);
        try
        {
            PanelRegistryState state = PanelRegistry.Load(_id, _config);
            if (state.Panels.Count == 0)
            {
                if (state.RequiresSave) PanelRegistry.Save(_id, state);
                _lastPanelContent = contentKey;
                _lastPanelImageStamp = imageFingerprint;
                _lastPanelStatus = snapshot.Status;
                _panelAt = DateTimeOffset.UtcNow;
                return;
            }
            bool allUpdated = true;
            bool stateChanged = state.RequiresSave;
            foreach (PanelLocation panel in state.Panels.ToArray())
            {
                try
                {
                    if (_client.GetChannel(panel.ChannelId) is not IMessageChannel channel)
                    {
                        allUpdated = false;
                        continue;
                    }

                    // NEVER post a new Discord message from background updates.
                    // Deleted panels are untracked; users may recreate them with /info.
                    if (await channel.GetMessageAsync(panel.MessageId).ConfigureAwait(false) is IUserMessage message)
                    {
                        PanelImage? image = candidate;
                        if (image is not null && _client.GetGuild(panel.GuildId) is { } guild &&
                            (ulong)image.Bytes.Length > guild.MaxUploadLimit)
                            image = null;
                        var view = InfoPanelRenderer.Render(_config, snapshot, _controls.Available,
                            _playtime.Leaderboard(5, DateTimeOffset.UtcNow), _relayState.ValheimJoinCode,
                            image?.FileName);
                        if (await UpdatePanelMessageAsync(message, panel, view, image).ConfigureAwait(false))
                            stateChanged = true;
                    }
                    else
                    {
                        state.Panels.Remove(panel);
                        stateChanged = true;
                        BotLog.Write($"Shard {_id:D}: removed missing/unedittable panel reference {panel.MessageId}.");
                    }
                }
                catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
                {
                    state.Panels.Remove(panel);
                    stateChanged = true;
                    BotLog.Write($"Shard {_id:D}: panel {panel.MessageId} was deleted; use /info to recreate it.");
                }
                catch (Exception ex)
                {
                    allUpdated = false;
                    BotLog.Write($"Shard {_id:D}: updating panel in channel {panel.ChannelId} failed: {AmpFacade.CleanError(ex)}");
                }
            }
            if (stateChanged) PanelRegistry.Save(_id, state);
            if (allUpdated)
            {
                _lastPanelContent = contentKey;
                _lastPanelImageStamp = imageFingerprint;
                _lastPanelStatus = snapshot.Status;
                _panelAt = DateTimeOffset.UtcNow;
            }
        }
        finally { _panelGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _ready = false;
        _sessionStop.Cancel();
        Task? worker;
        lock (_queueSync) worker = _updateWorker;
        if (_liveConsoleWorker is not null)
        {
            try { await _liveConsoleWorker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { BotLog.Write($"Shard {_id:D} live console shutdown exceeded five seconds."); }
            catch (OperationCanceledException) { }
        }
        if (_snapshotFollowUpWorker is not null)
        {
            try { await _snapshotFollowUpWorker.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (TimeoutException) { BotLog.Write($"Shard {_id:D} snapshot notification drain exceeded timeout."); }
            catch (OperationCanceledException) { }
        }
        if (worker is not null)
        {
            try { await worker.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (TimeoutException) { BotLog.Write($"Shard {_id:D} update drain exceeded timeout."); }
            catch (OperationCanceledException) { }
        }
        try { await _client.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception ex) { BotLog.Write($"Shard {_id:D} gateway stop: {ex.GetType().Name}"); }
        try { await _client.LogoutAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch { }
        _client.Dispose();
        if (worker is null || worker.IsCompleted)
        {
            _updateGate.Dispose();
            _panelGate.Dispose();
            if (_liveConsoleWorker is null || _liveConsoleWorker.IsCompleted) _relayGate.Dispose();
        }
        // Gateway registration tasks can be unwinding after cancellation;
        // leave their semaphore to GC rather than disposing it under a waiter.
        _controlGate.Dispose();
        _consoleGate.Dispose();
        _whitelistGate.Dispose();
        _sessionStop.Dispose();
        BotLog.Write($"Discord bot disconnected for Shard {_id:D}.");
    }
}
