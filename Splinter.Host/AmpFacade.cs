using System.Collections;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Splinter.Host;

public sealed record ShardSnapshot(Guid Id, string Name, string Status, bool IsRunning, int? Players, int? MaxPlayers,
    double? CpuPercent = null, double? MemoryMb = null, double? MemoryLimitMb = null,
    IReadOnlyList<OnlinePlayer>? OnlinePlayers = null, DateTimeOffset? StartedAt = null);

// The beta has no SDK. Reflection isolates its unstable types from our Discord code.
// Only explicitly allowlisted READ methods are called here.
public sealed class AmpFacade
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Type? _shardServiceType;
    private readonly MethodInfo? _list;
    private readonly MethodInfo? _state;
    private readonly AmpPlayerBridge _players;
    private readonly Type? _metricsHistoryType;
    private readonly MethodInfo? _readMetricsHistory;
    private bool _metricsBridgeWarningLogged;
    private readonly HashSet<Guid> _missingPlayerMetricsLogged = [];
    private readonly HashSet<Guid> _missingRosterWarningLogged = [];

    public AmpFacade(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
        _players = new AmpPlayerBridge(scopeFactory);
        Assembly? ampShared = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
            a => a.GetName().Name == "AmpShared");
        _shardServiceType = ampShared?.GetType("AmpShared.Shards.ShardService", throwOnError: false);
        _list = FindReadMethod(_shardServiceType, "ListAsync", hasShardId: false);
        _state = FindReadMethod(_shardServiceType, "GetStateAsync", hasShardId: true);
        // Live Shard state is our primary source. The history is an optional
        // read-only fallback for fields such as maximum users. AMP 3.0.0
        // exposes Get(String, Guid, Nullable<T>) for the time window.
        Assembly? webapi = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
            a => a.GetName().Name == "webapi");
        _metricsHistoryType = webapi?.GetType("webapi.Hubs.ShardMetricsHistory", throwOnError: false);
        _readMetricsHistory = _metricsHistoryType?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Get" && m.GetParameters().Length == 3 &&
                m.GetParameters()[0].ParameterType == typeof(string) &&
                m.GetParameters()[1].ParameterType == typeof(Guid) &&
                IsSupportedHistoryWindow(m.GetParameters()[2].ParameterType));
        if (_metricsHistoryType is not null && _readMetricsHistory is null)
            BotLog.Write("AMP metrics history reader signature is not recognized: " +
                string.Join("; ", _metricsHistoryType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(m => m.Name == "Get")
                    .Select(m => "Get(" + string.Join(",", m.GetParameters().Select(p =>
                        Nullable.GetUnderlyingType(p.ParameterType) is Type inner ? inner.Name + "?" : p.ParameterType.Name)) + ")")));
        if (_shardServiceType is null || _list is null)
            BotLog.Write("ERROR: ShardService.ListAsync not available in this AMP release; discovery disabled.");
        else
            BotLog.Write($"AMP read bridge: {_list.Name}({string.Join(", ", _list.GetParameters().Select(p => p.ParameterType.Name))}); state={_state is not null}; metricsHistory={_readMetricsHistory is not null}");
    }

    // Do not broadly invoke unrelated nullable Get overloads from beta builds.
    // A nullable TimeSpan window is supported, as is the non-nullable form.
    // Some builds accept an integer number of minutes instead.
    public static bool IsSupportedHistoryWindow(Type type)
    {
        Type window = Nullable.GetUnderlyingType(type) ?? type;
        return window == typeof(TimeSpan) || window == typeof(int);
    }

    private static MethodInfo? FindReadMethod(Type? type, string name, bool hasShardId)
    {
        return type?.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == name && typeof(Task).IsAssignableFrom(m.ReturnType))
            .FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Count(x => x.ParameterType == typeof(string)) == 1 &&
                    p.Count(x => x.ParameterType == typeof(Guid)) == (hasShardId ? 1 : 0) &&
                    p.All(x => x.ParameterType == typeof(string) || x.ParameterType == typeof(Guid) ||
                               x.ParameterType == typeof(CancellationToken) ||
                               (x.ParameterType == typeof(bool) && x.Name == "includeDeleted") ||
                               (x.IsOptional && x.HasDefaultValue));
            });
    }

    private static object?[] Args(MethodInfo method, Guid? shardId, CancellationToken ct) =>
        method.GetParameters().Select(p =>
        {
            if (p.ParameterType == typeof(string)) return (object?)"_root";
            if (p.ParameterType == typeof(Guid)) return shardId ?? throw new InvalidOperationException("Missing Shard ID");
            if (p.ParameterType == typeof(CancellationToken)) return ct;
            if (p.ParameterType == typeof(bool) && p.Name == "includeDeleted") return false;
            return p.DefaultValue;
        }).ToArray();

    private static async Task<object?> RunReadAsync(object service, MethodInfo method, Guid? shardId, CancellationToken ct)
    {
        object? result = method.Invoke(service, Args(method, shardId, ct));
        if (result is not Task task) throw new InvalidOperationException("AMP read method did not return Task");
        await task.WaitAsync(ct).ConfigureAwait(false);
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    public async Task<IReadOnlyList<ShardSnapshot>> ListAsync(CancellationToken ct)
    {
        if (_shardServiceType is null || _list is null)
            throw new NotSupportedException("AMP 3 Shard listing signature is unavailable in this release.");
        using var scope = _scopeFactory.CreateScope();
        object service = scope.ServiceProvider.GetRequiredService(_shardServiceType);
        object? listResult = await RunReadAsync(service, _list, null, ct);
        // Some versions return a list, others a page with Items or Shards.
        object? items = listResult is IEnumerable ? listResult : Read(listResult, "Items") ?? Read(listResult, "Shards");
        if (items is not IEnumerable list)
            throw new InvalidOperationException("Unexpected ShardService.ListAsync result type; refusing to guess.");

        var snapshots = new List<ShardSnapshot>();
        foreach (object? entry in list)
        {
            ct.ThrowIfCancellationRequested();
            if (entry is null) continue;
            if (Read(entry, "Deleted") is bool deleted && deleted) continue;
            if (!Guid.TryParse(Read(entry, "Id")?.ToString(), out Guid id) || id == Guid.Empty) continue;
            string name = Read(entry, "DisplayName")?.ToString() ?? id.ToString("D");
            object? state = null;
            if (_state is not null)
            {
                try { state = await RunReadAsync(service, _state, id, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { BotLog.Write($"Cannot read Shard {id:D} state: {CleanError(ex)}"); }
            }
            string status = Read(state, "Status")?.ToString() ?? "Unknown";
            bool isRunning = Read(state, "IsRunning") is bool running ? running :
                status.Equals("Ready", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Running", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("RunningReady", StringComparison.OrdinalIgnoreCase);
            // Prefer the live Shard state; history can supply the capacity
            // absent from live metrics. Neither path writes to AMP.
            object? recentSample = null;
            if (isRunning)
            {
                try { recentSample = ReadRecentMetrics(scope.ServiceProvider, state, entry, id); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (!_metricsBridgeWarningLogged)
                    {
                        _metricsBridgeWarningLogged = true;
                        BotLog.Write("AMP metrics history read unavailable: " + CleanError(ex));
                    }
                }
            }
            var metrics = ShardMetrics.ResolveRuntime(state, entry, recentSample);
            // AMP's authoritative connected-user store is independent of console
            // history. Never infer player names from connection counts.
            string tenantId = Read(state, "TenantId")?.ToString() ??
                Read(entry, "TenantId")?.ToString() ?? "_root";
            IReadOnlyList<OnlinePlayer>? roster = null;
            if (isRunning)
            {
                try { roster = _players.Read(tenantId, id); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (_missingRosterWarningLogged.Add(id))
                        BotLog.Write($"Shard {id:D} player roster unavailable: {CleanError(ex)}");
                }
            }
            // Some games report an accurate numeric player count without
            // providing named-user tracking. Preserve that count without
            // incorrectly declaring every known player disconnected.
            if (roster is { Count: 0 } && metrics.Online is > 0)
                roster = null;
            int? players = roster is { Count: > 0 } ? roster.Count : metrics.Online ?? roster?.Count;
            int? max = metrics.Maximum;
            if (isRunning && (players is null || max is null || metrics.CpuPercent is null || metrics.MemoryMb is null ||
                              metrics.MemoryLimitMb is null) &&
                _missingPlayerMetricsLogged.Add(id))
            {
                BotLog.Write($"Shard {id:D}: one or more live metrics unavailable; " +
                    $"state keys=[{ShardMetrics.Shape(state)}], metrics keys=[{ShardMetrics.Shape(Read(state, "Metrics"))}], " +
                    $"history sample keys=[{ShardMetrics.Shape(recentSample)}]; " +
                    $"configured-limit scopes: {ShardMetrics.CapacityShape(entry)}.");
            }
            snapshots.Add(new ShardSnapshot(id, name, status, isRunning, players, max,
                isRunning ? metrics.CpuPercent : null,
                isRunning ? metrics.MemoryMb : null,
                isRunning ? metrics.MemoryLimitMb : null, roster,
                isRunning ? ReadStartTime(state) : null));
        }
        return snapshots;
    }

    private static DateTimeOffset? ReadStartTime(object? state)
    {
        foreach (string key in new[] { "StartedAt", "RunningSince", "ProcessStartedAt", "LastStartedAt", "StartTime" })
        {
            object? raw = Read(state, key);
            if (raw is DateTimeOffset dto) return dto;
            if (raw is DateTime dt) return new DateTimeOffset(dt.ToUniversalTime());
            if (raw is string s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
                return parsed;
        }
        return null;
    }

    private object? ReadRecentMetrics(IServiceProvider services, object? state, object? entry, Guid id)
    {
        if (_metricsHistoryType is null || _readMetricsHistory is null) return null;
        object? service = services.GetService(_metricsHistoryType);
        if (service is null) return null; // An unsupported beta registration is harmless.
        string tenant = Read(state, "TenantId")?.ToString() ??
            Read(entry, "TenantId")?.ToString() ?? "_root";
        Type windowType = Nullable.GetUnderlyingType(_readMetricsHistory.GetParameters()[2].ParameterType)
            ?? _readMetricsHistory.GetParameters()[2].ParameterType;
        object window = windowType == typeof(int) ? (object)3 : TimeSpan.FromMinutes(3);
        object? history = _readMetricsHistory.Invoke(service, [tenant, id, window]);
        return ShardMetrics.LatestRecentSample(history, DateTimeOffset.UtcNow);
    }

    private static object? Read(object? value, string property)
    {
        try { return value?.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value); }
        catch { return null; }
    }

    public static string CleanError(Exception error)
    {
        while (error is TargetInvocationException tie && tie.InnerException is not null) error = tie.InnerException;
        return error.GetType().Name + ": " + error.Message.Replace('\n', ' ').Replace('\r', ' ');
    }
}
