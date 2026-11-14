using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Splinter.Host;

// AMP 3 has no public plugin SDK. The control adapter deliberately recognises
// only inspected signatures; it never invokes a controller outside HTTP or
// impersonates an AMP user. Start/Update use AMP's actual system principal.
public enum ShardControlAction { Start, Stop, Restart, Kill, Update, Backup }

public sealed class ShardControlBridge
{
    private const string Tenant = "_root";
    private const string Reason = "Splinter Discord server control";
    private readonly IServiceScopeFactory _scopes;
    private readonly Type? _launcherType;
    private readonly AmpShardLifecycle? _lifecycle;
    private readonly AmpSnapshotBridge? _snapshots;
    private readonly Dictionary<ShardControlAction, MethodInfo> _actions = new();

    // explicitLauncherType exists for isolated, synthetic-AMP regression tests.
    // Production always resolves webapi's actual ShardAgentLauncher.
    public ShardControlBridge(IServiceScopeFactory scopes, Type? explicitLauncherType = null)
    {
        _scopes = scopes;
        // Synthetic launcher tests deliberately exercise only the launcher adapter.
        _lifecycle = explicitLauncherType is null ? new AmpShardLifecycle(scopes) : null;
        _snapshots = explicitLauncherType is null ? new AmpSnapshotBridge(scopes) : null;
        _launcherType = explicitLauncherType ?? AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "webapi")?
            .GetType("webapi.Services.ShardAgentLauncher", throwOnError: false);

        if (_launcherType is not null &&
            _launcherType.FullName == "webapi.Services.ShardAgentLauncher")
        {
            Bind(ShardControlAction.Stop, "StopAsync", typeof(Task<bool>),
                typeof(string), typeof(Guid), typeof(string), typeof(TimeSpan?), typeof(CancellationToken));
            Bind(ShardControlAction.Kill, "KillAsync", typeof(Task<bool>),
                typeof(string), typeof(Guid), typeof(string));
            Bind(ShardControlAction.Restart, "SendRestartCommandAsync", typeof(Task),
                typeof(string), typeof(Guid), typeof(CancellationToken));
        }

        // Start/Update use AMP lifecycle services; Backup uses the native
        // SnapshotService, including its safety and retention checks.
        BotLog.Write("AMP server-control bridge: " + string.Join(", ",
            Enum.GetValues<ShardControlAction>().Select(action =>
                $"{action}={(Available(action) ? "bound" : "unavailable")}")));
    }

    private void Bind(ShardControlAction action, string name, Type result, params Type[] parameters)
    {
        if (_launcherType is null) return;
        MethodInfo[] matches = _launcherType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name == name && !m.IsGenericMethod && m.ReturnType == result &&
                m.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters))
            .ToArray();
        if (matches.Length == 1) _actions[action] = matches[0];
        else if (matches.Length > 1)
            BotLog.Write($"AMP {action} binding is ambiguous; action disabled.");
    }

    public bool Available(ShardControlAction action) => action switch
    {
        ShardControlAction.Start => _lifecycle?.StartAvailable == true,
        ShardControlAction.Update => _lifecycle?.UpdateAvailable == true,
        ShardControlAction.Backup => _snapshots?.Available == true,
        _ => _actions.ContainsKey(action)
    };

    public Task<Guid> TakeSnapshotAsync(Guid shardId, CancellationToken ct) =>
        _snapshots?.TakeAsync(shardId, ct) ??
        throw new NotSupportedException("AMP 3 snapshot service is unavailable.");

    public Task<AmpSnapshotProgress?> ReadSnapshotProgressAsync(Guid shardId, Guid snapshotId,
        CancellationToken ct) =>
        _snapshots?.ReadProgressAsync(shardId, snapshotId, ct) ??
        throw new NotSupportedException("AMP 3 snapshot service is unavailable.");

    public async Task ExecuteAsync(ShardControlAction action, Guid shardId, CancellationToken ct)
    {
        if (shardId == Guid.Empty) throw new ArgumentException("A valid Shard ID is required.", nameof(shardId));
        if (action == ShardControlAction.Start)
        {
            if (_lifecycle is null) throw new NotSupportedException("AMP 3's Shard start lifecycle is unavailable.");
            await _lifecycle.StartAsync(shardId, ct).ConfigureAwait(false);
            return;
        }
        if (action == ShardControlAction.Update)
        {
            if (_lifecycle is null) throw new NotSupportedException("AMP 3's Shard update lifecycle is unavailable.");
            await _lifecycle.UpdateAsync(shardId, ct).ConfigureAwait(false);
            return;
        }
        if (action == ShardControlAction.Backup)
        {
            if (_snapshots is null) throw new NotSupportedException("AMP 3 snapshot service is unavailable.");
            await _snapshots.TakeAsync(shardId, ct).ConfigureAwait(false);
            return;
        }
        if (_launcherType is null)
            throw new NotSupportedException("AMP 3's Shard launcher is unavailable.");
        if (!_actions.TryGetValue(action, out MethodInfo? method))
            throw new NotSupportedException($"AMP 3 does not expose a verified {action} action.");

        using IServiceScope scope = _scopes.CreateScope();
        object? service = scope.ServiceProvider.GetService(_launcherType);
        if (service is null)
            throw new NotSupportedException("AMP 3's ShardAgentLauncher service is not registered.");

        object?[] args = action switch
        {
            ShardControlAction.Stop => [Tenant, shardId, Reason, (TimeSpan?)null, ct],
            ShardControlAction.Kill => [Tenant, shardId, Reason],
            ShardControlAction.Restart => [Tenant, shardId, ct],
            _ => throw new NotSupportedException($"{action} is not verified in this AMP build.")
        };
        try
        {
            if (method.Invoke(service, args) is not Task task)
                throw new NotSupportedException("AMP action did not return a Task.");
            await task.WaitAsync(ct).ConfigureAwait(false);
            if (task is Task<bool> outcome && !outcome.Result)
                throw new InvalidOperationException("AMP refused the requested Shard action.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new InvalidOperationException("AMP rejected the requested Shard action.", ex.InnerException);
        }
    }
}

public static class ServerControlPermissions
{
    public static bool RoleMatches(string configured, IEnumerable<(ulong Id, string Name)> assignedRoles)
    {
        if (string.IsNullOrWhiteSpace(configured)) return false;
        string expected = configured.Trim();
        bool isId = ulong.TryParse(expected, out ulong roleId);
        return assignedRoles.Any(role => isId ? role.Id == roleId :
            role.Name.Equals(expected, StringComparison.OrdinalIgnoreCase));
    }

    public static bool Allowed(ShardBotConfig config, bool guildMember,
        IEnumerable<(ulong Id, string Name)> assignedRoles)
    {
        if (!guildMember) return false;
        return !config.RestrictFunctionsToDiscordRole || RoleMatches(config.DiscordRoleName, assignedRoles);
    }

    public static bool ShowButton(ShardBotConfig config, ShardControlAction action) => action switch
    {
        ShardControlAction.Start => config.DisplayStartButton,
        ShardControlAction.Stop => config.DisplayStopButton,
        ShardControlAction.Restart => config.DisplayRestartButton,
        ShardControlAction.Kill => config.DisplayKillButton,
        ShardControlAction.Update => config.DisplayUpdateButton,
        ShardControlAction.Backup => config.DisplayBackupButton,
        _ => false
    };
}
