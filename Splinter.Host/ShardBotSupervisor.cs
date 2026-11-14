using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Splinter.Host;

public sealed class ShardBotSupervisor : IHostedService, IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<Guid, BotSession> _bots = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenRegistration _started;
    private Task? _loop;
    private Task? _events;

    public ShardBotSupervisor(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime)
    {
        _scopes = scopes;
        _lifetime = lifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Never hold up or terminate AMP while Discord starts, connects, or fails.
        _started = _lifetime.ApplicationStarted.Register(() =>
            _loop = Task.Run(() => RunSafeAsync(_stop.Token)));
        return Task.CompletedTask;
    }

    private async Task RunSafeAsync(CancellationToken ct)
    {
        try
        {
            ConfigStore.EnsureFolders();
            if (!File.Exists(ConfigStore.GlobalPath))
                ConfigStore.Write(ConfigStore.GlobalPath, new GlobalConfig());
            BotLog.Write("Splinter bot manager started (all Shards opt-in).");
            var amp = new AmpFacade(_scopes);
            var controls = new ShardControlBridge(_scopes);
            var console = new ShardConsoleBridge(_scopes);
            var history = new ConsoleHistoryBridge(_scopes);
            var playerEvents = new AmpPlayerEvents(_scopes);
            _events = Task.Run(() => RunPlayerEventsAsync(playerEvents, ct), ct);
            while (!ct.IsCancellationRequested)
            {
                GlobalConfig global = new();
                try
                {
                    global = ConfigStore.Read(ConfigStore.GlobalPath, new GlobalConfig());
                    var snapshots = await amp.ListAsync(ct).ConfigureAwait(false);
                    await ReconcileAsync(snapshots, global, controls, console, history, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { BotLog.Write("Discovery/reconciliation failed (AMP unaffected): " + AmpFacade.CleanError(ex)); }
                await Task.Delay(TimeSpan.FromSeconds(global.DiscoverEverySeconds == 30 ? 5 : Math.Clamp(global.DiscoverEverySeconds, 5, 300)), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { BotLog.Write("Bot manager fatal error (AMP unaffected): " + AmpFacade.CleanError(ex)); }
        finally { await DisconnectAllAsync().ConfigureAwait(false); }
    }

    private async Task RunPlayerEventsAsync(AmpPlayerEvents events, CancellationToken ct)
    {
        string? lastError = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await events.ListenAsync(DispatchPlayerEventAsync, ct).ConfigureAwait(false);
                lastError = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                string error = AmpFacade.CleanError(ex);
                if (!string.Equals(lastError, error, StringComparison.Ordinal))
                    BotLog.Write("AMP player-event listener unavailable (roster recovery remains active): " + error);
                lastError = error;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private async Task DispatchPlayerEventAsync(AmpPlayerEvent evt, CancellationToken ct)
    {
        BotSession? bot;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { _bots.TryGetValue(evt.ShardId, out bot); }
        finally { _gate.Release(); }
        if (bot is not null) await bot.HandleAmpPlayerEventAsync(evt, ct).ConfigureAwait(false);
    }

    private async Task ReconcileAsync(IReadOnlyList<ShardSnapshot> shards, GlobalConfig global,
        ShardControlBridge controls, ShardConsoleBridge console, ConsoleHistoryBridge history, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var seen = new HashSet<Guid>();
            foreach (ShardSnapshot shard in shards)
            {
                seen.Add(shard.Id);
                try
                {
                    string path = ConfigStore.ConfigPath(shard.Id);
                    bool isNew = !File.Exists(path);
                    ShardBotConfig config = ConfigStore.GetOrCreate(shard.Id, shard.Name);
                    if (isNew) BotLog.Write($"Discovered Shard '{shard.Name}' ({shard.Id:D}); disabled configuration created.");
                    // Independently update the opt-in static web panel, even if Discord is disconnected.
                    // Rendering failures affect only this Shard and never AMP.
                    try { WebPanelPublisher.Update(config, shard); }
                    catch (Exception ex) { BotLog.Write($"Shard {shard.Id:D} web panel update failed: {AmpFacade.CleanError(ex)}"); }
                    if (!config.Enabled || !File.Exists(ConfigStore.SecretPath(shard.Id)))
                    {
                        if (_bots.Remove(shard.Id, out BotSession? old)) await old.DisposeAsync().ConfigureAwait(false);
                        continue;
                    }
                    // Only token changes require gateway recreation. Native presence
                    // changes are applied in-place without disconnecting the bot.
                    string version = SecretStore.FileVersion(shard.Id);
                    if (_bots.TryGetValue(shard.Id, out BotSession? session) &&
                        (session.ConfigVersion != version || session.ChatIntentEnabled != BotSession.ChatIntentRequired(config)))
                    {
                        _bots.Remove(shard.Id);
                        await session.DisposeAsync().ConfigureAwait(false);
                        session = null;
                    }
                    if (session is null)
                    {
                        using var secretScope = _scopes.CreateScope();
                        string? token = SecretStore.ReadToken(shard.Id, secretScope.ServiceProvider);
                        if (string.IsNullOrEmpty(token)) continue;
                        session = new BotSession(shard.Id, config, version, token, controls, console, history, _scopes);
                        _bots[shard.Id] = session;
                        try { await session.StartAsync(ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            BotLog.Write($"Bot for {shard.Id:D} failed to connect: {AmpFacade.CleanError(ex)}");
                            await session.DisposeAsync().ConfigureAwait(false);
                            _bots.Remove(shard.Id);
                            continue;
                        }
                    }
                    // Connected clients are updated independently for each Shard.
                    await session.ApplyConfigAsync(config, ct).ConfigureAwait(false);
                    session.QueueUpdate(shard);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { BotLog.Write($"Shard {shard.Id:D} bot error (other bots unaffected): {AmpFacade.CleanError(ex)}"); }
            }
            foreach (Guid orphanedId in _bots.Keys.Where(id => !seen.Contains(id)).ToArray())
            {
                BotSession old = _bots[orphanedId];
                _bots.Remove(orphanedId);
                await old.DisposeAsync().ConfigureAwait(false);
                try { File.Delete(ConfigStore.WebPanelPath(orphanedId)); } catch { }
                BotLog.Write($"Shard {orphanedId:D} no longer exists; bot disconnected, config preserved.");
            }
        }
        finally { _gate.Release(); }
    }

    private async Task DisconnectAllAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (BotSession bot in _bots.Values)
            {
                try { await bot.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { BotLog.Write("Bot shutdown issue: " + AmpFacade.CleanError(ex)); }
            }
            _bots.Clear();
        }
        finally { _gate.Release(); }
        BotLog.Write("Bot manager stopped.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _started.Dispose();
        _stop.Cancel();
        if (_events is not null)
        {
            try { await _events.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { BotLog.Write("Player event listener shutdown exceeded five seconds."); }
        }
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { BotLog.Write("Bot shutdown exceeded 12s; AMP shutdown continues."); }
        }
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _stop.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal static class BotLog
{
    private static readonly object Sync = new();
    public static void Write(string message)
    {
        string line = $"[{DateTimeOffset.UtcNow:O}] {message}";
        lock (Sync)
        {
            try { Console.Error.WriteLine("[Splinter] " + line); } catch { }
            try
            {
                string file = Path.Combine(ConfigStore.Root, "logs", "splinter.log");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.AppendAllText(file, line + Environment.NewLine);
            }
            catch { /* diagnostics must never crash AMP */ }
        }
    }
}
