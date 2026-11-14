using System.Reflection;
using System.Collections;
using Microsoft.Extensions.DependencyInjection;

namespace Splinter.Host;

// Uses AMP 3's own managed snapshot task and its normal retention/quiesce
// checks. The reflection boundary is intentionally limited to the verified
// 3.0.0 signature; no filesystem copying or private HTTP controller calls.
public sealed class AmpSnapshotBridge
{
    private const string Tenant = "_root";
    private const string Source = "Splinter Discord";
    private readonly IServiceScopeFactory _scopes;
    private readonly Type? _snapshotType;
    private readonly MethodInfo? _take;
    private readonly MethodInfo? _getGraph;
    private readonly MethodInfo? _principalFactory;
    private readonly Type? _kindType;
    private readonly Type? _contentType;
    private readonly Type? _triggerType;

    public AmpSnapshotBridge(IServiceScopeFactory scopes) : this(scopes,
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "webapi")?
            .GetType("webapi.Services.SnapshotService", false),
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "AmpShared")?
            .GetType("AmpShared.Models.Tasks.TaskPrincipal", false)) { }

    // Used by the isolated regression suite; production always resolves AMP.
    internal AmpSnapshotBridge(IServiceScopeFactory scopes, Type? snapshotType, Type? principalType)
    {
        _scopes = scopes;
        _snapshotType = snapshotType;
        _getGraph = snapshotType?.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(m => m.Name == "GetGraphAsync" &&
                m.GetParameters().Select(p => p.ParameterType).SequenceEqual([typeof(string), typeof(Guid)]) &&
                m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(Task<>) &&
                m.ReturnType.GenericTypeArguments[0].Name == "ShardSnapshotGraph");
        _principalFactory = principalType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(m => m.Name == "System" && m.ReturnType == principalType &&
                m.GetParameters().Select(p => p.ParameterType).SequenceEqual([typeof(string)]));
        var candidates = snapshotType?.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "TakeAsync" && m.GetParameters().Length == 10 &&
                m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(Task<>) &&
                m.ReturnType.GenericTypeArguments[0].Name == "ShardSnapshot")
            .ToArray() ?? [];
        foreach (MethodInfo method in candidates)
        {
            Type[] types = method.GetParameters().Select(p => p.ParameterType).ToArray();
            if (types[0] != typeof(string) || types[1] != typeof(Guid) ||
                types[2].FullName != "AmpShared.Models.Shard.SnapshotKind" ||
                types[3].FullName != "AmpShared.Models.Shard.SnapshotContent" ||
                types[4] != typeof(string) || types[5] != typeof(string) ||
                types[6] != typeof(bool) ||
                types[7].FullName != "AmpShared.Models.Shard.SnapshotTrigger" ||
                types[8] != principalType || types[9] != typeof(CancellationToken)) continue;
            if (_take is not null) { _take = null; return; } // Ambiguous: fail closed.
            _take = method;
            _kindType = types[2];
            _contentType = types[3];
            _triggerType = types[7];
        }
    }

    public bool Available => _take is not null && _getGraph is not null && _principalFactory is not null &&
        _kindType is not null && _contentType is not null && _triggerType is not null;

    // Read AMP's persisted graph to follow the exact capture requested by a
    // Discord user. Unknown statuses and missing nodes are not completion.
    public async Task<AmpSnapshotProgress?> ReadProgressAsync(Guid shardId, Guid snapshotId,
        CancellationToken ct)
    {
        if (shardId == Guid.Empty || snapshotId == Guid.Empty)
            throw new ArgumentException("Valid Shard and snapshot IDs are required.");
        if (_getGraph is null || _snapshotType is null)
            throw new NotSupportedException("AMP snapshot status lookup is unavailable.");
        using IServiceScope scope = _scopes.CreateScope();
        object service = scope.ServiceProvider.GetService(_snapshotType) ??
            throw new NotSupportedException("AMP SnapshotService is not registered.");
        object? pending = _getGraph.Invoke(service, [Tenant, shardId]);
        if (pending is not Task task)
            throw new NotSupportedException("AMP returned an unexpected snapshot graph operation.");
        await task.WaitAsync(ct).ConfigureAwait(false);
        object graph = task.GetType().GetProperty("Result")?.GetValue(task) ??
            throw new NotSupportedException("AMP snapshot graph is unavailable.");
        if (graph.GetType().GetProperty("Nodes")?.GetValue(graph) is not IEnumerable nodes)
            throw new NotSupportedException("AMP snapshot graph has no recognised nodes.");
        foreach (object? node in nodes)
        {
            if (node is null || node.GetType().GetProperty("Id")?.GetValue(node) is not Guid id ||
                id != snapshotId) continue;
            string status = node.GetType().GetProperty("Status")?.GetValue(node)?.ToString() ?? "";
            if (status is not ("Pending" or "Running" or "Complete" or "Failed"))
                throw new NotSupportedException("Unrecognised AMP snapshot status.");
            string? failureReason = status == "Failed"
                ? node.GetType().GetProperty("FailureReason")?.GetValue(node) as string : null;
            return new AmpSnapshotProgress(status, failureReason);
        }
        return null; // AMP has not published the capture record yet.
    }

    public async Task<Guid> TakeAsync(Guid shardId, CancellationToken ct)
    {
        if (shardId == Guid.Empty) throw new ArgumentException("A valid Shard ID is required.", nameof(shardId));
        if (!Available) throw new NotSupportedException("AMP 3 native snapshot creation is unavailable.");
        using IServiceScope scope = _scopes.CreateScope();
        object instance = scope.ServiceProvider.GetService(_snapshotType!) ??
            throw new NotSupportedException("AMP SnapshotService is not registered.");
        // AMP's graph is authoritative. Do not allow Discord button spam to
        // queue another capture while this Shard already has an active one.
        object graphPending = _getGraph!.Invoke(instance, [Tenant, shardId]) ??
            throw new NotSupportedException("AMP snapshot graph lookup returned no task.");
        if (graphPending is not Task graphTask)
            throw new NotSupportedException("AMP snapshot graph lookup returned an unexpected task.");
        await graphTask.WaitAsync(ct).ConfigureAwait(false);
        object graph = graphTask.GetType().GetProperty("Result")?.GetValue(graphTask) ??
            throw new NotSupportedException("AMP snapshot graph is unavailable.");
        if (graph.GetType().GetProperty("Nodes")?.GetValue(graph) is not IEnumerable nodes)
            throw new NotSupportedException("AMP snapshot graph contains no recognised node list.");
        foreach (object? node in nodes)
        {
            string? status = node?.GetType().GetProperty("Status")?.GetValue(node)?.ToString();
            if (status is "Pending" or "Running")
                throw new InvalidOperationException("An AMP snapshot is already in progress for this Shard.");
        }
        object principal = _principalFactory!.Invoke(null, [Source]) ??
            throw new NotSupportedException("AMP did not create a system task principal.");
        object kind = Enum.Parse(_kindType!, "Full", false);
        object flags = Enum.ToObject(_contentType!,
            Convert.ToInt64(Enum.Parse(_contentType!, "GameState")) |
            Convert.ToInt64(Enum.Parse(_contentType!, "BaseData")) |
            Convert.ToInt64(Enum.Parse(_contentType!, "AmpConfig")));
        object trigger = Enum.Parse(_triggerType!, "Manual", false);
        Task pending;
        try
        {
            pending = _take!.Invoke(instance, [Tenant, shardId, kind, flags, "", "", false,
                trigger, principal, ct]) as Task ??
                throw new NotSupportedException("AMP SnapshotService returned an unexpected task.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new InvalidOperationException("AMP refused to create a snapshot.", ex.InnerException);
        }
        await pending.WaitAsync(ct).ConfigureAwait(false);
        object? result = pending.GetType().GetProperty("Result")?.GetValue(pending);
        if (result?.GetType().GetProperty("Id")?.GetValue(result) is not Guid id || id == Guid.Empty)
            throw new InvalidOperationException("AMP did not return a snapshot ID.");
        // AMP's TakeAsync schedules capture; this confirms acceptance, NOT completion.
        return id;
    }
}
