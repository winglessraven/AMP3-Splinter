using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Splinter.Host;

// AMP 3.0.0 internal lifecycle service adapter. The binding is intentionally narrow:
// no controller invocation, no HTTP impersonation, no shell execution, and no guessed
// command names. A future AMP change disables the affected action until reviewed.
internal sealed class AmpShardLifecycle
{
    private const string TenantId = "_root";
    private const string Source = "Splinter Discord";
    private readonly IServiceScopeFactory _scopes;
    private readonly Type? _shards;
    private readonly Type? _starter;
    private readonly Type? _tasks;
    private readonly Type? _launcher;
    private readonly Type? _principal;
    private readonly Type? _commandKind;
    private readonly MethodInfo? _getShard;
    private readonly MethodInfo? _preflight;
    private readonly MethodInfo? _dispatch;
    private readonly MethodInfo? _systemPrincipal;
    private readonly MethodInfo? _getState;
    private readonly MethodInfo? _refreshSpec;
    private readonly MethodInfo? _rebuildEffective;
    private readonly MethodInfo? _underway;
    private readonly MethodInfo? _createUpdate;
    private readonly MethodInfo? _sendCommand;
    private readonly MethodInfo? _offline;
    private readonly MethodInfo? _failTask;
    private readonly MethodInfo? _agentResponding;
    private readonly MethodInfo? _sendInPlaceUpdate;

    public bool StartAvailable { get; }
    public bool UpdateAvailable { get; }

    public AmpShardLifecycle(IServiceScopeFactory scopes) : this(scopes,
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "AmpShared")?.GetType("AmpShared.Shards.ShardService", false),
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "webapi")?.GetType("webapi.Services.ShardStarter", false),
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "webapi")?.GetType("webapi.Services.ShardTaskService", false),
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "webapi")?.GetType("webapi.Services.ShardAgentLauncher", false),
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "AmpShared")?.GetType("AmpShared.Models.Tasks.TaskPrincipal", false),
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "AmpShared")?.GetType("AmpShared.Models.Spire.SpireCommandKind", false)) { }

    // Type injection is for regression tests only; production resolves real AMP assemblies.
    internal AmpShardLifecycle(IServiceScopeFactory scopes, Type? shards, Type? starter,
        Type? tasks, Type? launcher, Type? principal, Type? commandKind)
    {
        _scopes = scopes;
        _shards = shards;
        _starter = starter;
        _tasks = tasks;
        _launcher = launcher;
        _principal = principal;
        _commandKind = commandKind;
        _getShard = Find(_shards, "GetAsync", typeof(string), typeof(Guid));
        _preflight = Find(_starter, "PreflightAsync", typeof(string), "AmpShared.Models.Shard.DeployedShard", typeof(bool), typeof(string), typeof(CancellationToken));
        // AMP 3 has shipped both the five-argument dispatch and a variant with an
        // additional string immediately before CancellationToken (confirmed in the
        // Linux 3.0.0 single-file Web API). Match the complete signature, never an
        // arbitrary extra parameter, so incompatible AMP changes still fail closed.
        _dispatch = Exactly(Find(_starter, "DispatchAsync", typeof(string),
            "AmpShared.Models.Shard.DeployedShard", "AmpShared.Models.AppSpecification.AppSpec",
            "AmpShared.Models.Tasks.TaskPrincipal", typeof(CancellationToken)), 5)
            ?? Exactly(Find(_starter, "DispatchAsync", typeof(string),
                "AmpShared.Models.Shard.DeployedShard", "AmpShared.Models.AppSpecification.AppSpec",
                "AmpShared.Models.Tasks.TaskPrincipal", typeof(string), typeof(CancellationToken)), 6);
        _systemPrincipal = _principal?.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(m => m.Name == "System" && m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType == typeof(string) && m.ReturnType == _principal);
        StartAvailable = _getShard is not null && _preflight is not null && _dispatch is not null && _systemPrincipal is not null;

        _getState = Find(_shards, "GetStateAsync", typeof(string), typeof(Guid));
        _refreshSpec = Find(_shards, "RefreshSpecAsync", typeof(string), typeof(Guid), typeof(CancellationToken));
        _rebuildEffective = Find(_shards, "RebuildEffectiveAsync", typeof(string), typeof(Guid), "AmpShared.Models.AppSpecification.AppSpec", null, null, typeof(bool), typeof(CancellationToken));
        _underway = Find(_tasks, "FindUnderwayUpdateAsync", typeof(string), typeof(Guid), "System.Nullable`1<AmpShared.Models.AppSpecification.ApplicationState>", typeof(CancellationToken));
        _createUpdate = Find(_tasks, "CreateUpdateTaskAsync", "AmpShared.Models.Shard.DeployedShard", "AmpShared.Models.AppSpecification.AppSpec", "AmpShared.Models.Tasks.TaskPrincipal", typeof(CancellationToken));
        _sendCommand = Find(_starter, "SendCommandAsync", typeof(string), "AmpShared.Models.Shard.DeployedShard", "AmpShared.Models.Spire.SpireCommandKind", "AmpShared.Models.Tasks.TaskPrincipal", typeof(Guid?), typeof(CancellationToken), "AmpShared.Shards.LoginCredential");
        _offline = Find(_starter, "SpireOfflineRefusalAsync", typeof(string), "AmpShared.Models.Shard.DeployedShard");
        _failTask = Find(_tasks, "FailAsync", typeof(string), typeof(Guid), typeof(string), typeof(CancellationToken));
        _agentResponding = Find(_launcher, "IsAgentRespondingAsync", typeof(string), typeof(Guid), typeof(CancellationToken));
        _sendInPlaceUpdate = Find(_launcher, "SendUpdateCommandAsync", typeof(string), typeof(Guid), typeof(Guid), typeof(CancellationToken));
        // Updating never calls PreflightAsync or DispatchAsync. A start-only signature
        // mismatch must not disable an otherwise fully verified update lifecycle.
        UpdateAvailable = _getShard is not null && _systemPrincipal is not null &&
            _getState is not null && _refreshSpec is not null && _rebuildEffective is not null &&
            _underway is not null && _createUpdate is not null && _sendCommand is not null &&
            _offline is not null && _failTask is not null && _agentResponding is not null &&
            _sendInPlaceUpdate is not null && _commandKind is not null;

        if (!StartAvailable || !UpdateAvailable)
        {
            // On Linux, AMP's single-file Web API does not expose loose DLLs for
            // the offline metadata inspector. Report *types and signatures only*
            // from the already loaded AMP assemblies; never dump values or secrets.
            // Diagnostics must not interfere with startup or any bound actions.
            try { LogUnavailableBindings(); }
            catch (Exception ex)
            {
                BotLog.Write("AMP lifecycle metadata diagnostic unavailable: " + ex.GetType().Name);
            }
        }
    }

    private void LogUnavailableBindings()
    {
        BotLog.Write($"AMP lifecycle metadata: Start={(StartAvailable ? "bound" : "unavailable")}, " +
            $"Update={(UpdateAvailable ? "bound" : "unavailable")}; listing missing bindings only.");

        var bindings = new (string Label, Type? Owner, MethodInfo? Bound, string Method)[]
        {
            ("ShardService.GetAsync", _shards, _getShard, "GetAsync"),
            ("ShardStarter.PreflightAsync", _starter, _preflight, "PreflightAsync"),
            ("ShardStarter.DispatchAsync", _starter, _dispatch, "DispatchAsync"),
            ("TaskPrincipal.System", _principal, _systemPrincipal, "System"),
            ("ShardService.GetStateAsync", _shards, _getState, "GetStateAsync"),
            ("ShardService.RefreshSpecAsync", _shards, _refreshSpec, "RefreshSpecAsync"),
            ("ShardService.RebuildEffectiveAsync", _shards, _rebuildEffective, "RebuildEffectiveAsync"),
            ("ShardTaskService.FindUnderwayUpdateAsync", _tasks, _underway, "FindUnderwayUpdateAsync"),
            ("ShardTaskService.CreateUpdateTaskAsync", _tasks, _createUpdate, "CreateUpdateTaskAsync"),
            ("ShardStarter.SendCommandAsync", _starter, _sendCommand, "SendCommandAsync"),
            ("ShardStarter.SpireOfflineRefusalAsync", _starter, _offline, "SpireOfflineRefusalAsync"),
            ("ShardTaskService.FailAsync", _tasks, _failTask, "FailAsync"),
            ("ShardAgentLauncher.IsAgentRespondingAsync", _launcher, _agentResponding, "IsAgentRespondingAsync"),
            ("ShardAgentLauncher.SendUpdateCommandAsync", _launcher, _sendInPlaceUpdate, "SendUpdateCommandAsync")
        };

        foreach (var binding in bindings)
        {
            if (binding.Bound is not null) continue;
            if (binding.Owner is null)
            {
                BotLog.Write($"AMP lifecycle missing: {binding.Label} (declaring type not loaded).");
                continue;
            }

            MethodInfo[] candidates = binding.Owner.GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == binding.Method)
                .Take(5)
                .ToArray();
            string signatures = candidates.Length == 0 ? "no methods with this name" :
                string.Join(" | ", candidates.Select(method =>
                    $"{method.ReturnType.FullName} {method.Name}(" +
                    string.Join(", ", method.GetParameters().Select(parameter =>
                        parameter.ParameterType.FullName + (parameter.IsOptional ? " optional" : ""))) + ")"));
            BotLog.Write($"AMP lifecycle missing: {binding.Label}; loaded type={binding.Owner.FullName}; candidates: {signatures}");
        }

        if (_commandKind is null)
            BotLog.Write("AMP lifecycle missing: AmpShared.Models.Spire.SpireCommandKind (type not loaded).");
    }

    private static MethodInfo? Exactly(MethodInfo? method, int parameterCount) =>
        method?.GetParameters().Length == parameterCount ? method : null;

    private static MethodInfo? Find(Type? type, string name, params object?[] parameters)
    {
        if (type is null) return null;
        MethodInfo[] found = type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name == name && !m.IsGenericMethod && typeof(Task).IsAssignableFrom(m.ReturnType))
            .Where(m =>
            {
                ParameterInfo[] actual = m.GetParameters();
                if (actual.Length < parameters.Length) return false;
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i] is null) continue;
                    Type t = actual[i].ParameterType;
                    if (parameters[i] is Type expected && t != expected) return false;
                    if (parameters[i] is string fullName && t.FullName != fullName &&
                        !(Nullable.GetUnderlyingType(t)?.FullName == "AmpShared.Models.AppSpecification.ApplicationState" &&
                          fullName == "System.Nullable`1<AmpShared.Models.AppSpecification.ApplicationState>")) return false;
                }
                return actual.Skip(parameters.Length).All(p => p.IsOptional);
            }).ToArray();
        return found.Length == 1 ? found[0] : null;
    }

    private static object? Property(object? value, string name) =>
        value?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value);

    private static object?[] Arguments(MethodInfo method, params object?[] supplied)
    {
        ParameterInfo[] parameters = method.GetParameters();
        object?[] args = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
            args[i] = i < supplied.Length ? supplied[i] : parameters[i].DefaultValue;
        return args;
    }

    private static async Task<object?> InvokeAsync(object service, MethodInfo method, CancellationToken ct, params object?[] args)
    {
        object? result;
        try { result = method.Invoke(service, Arguments(method, args)); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { throw new InvalidOperationException($"AMP {method.Name} rejected the request.", ex.InnerException); }
        if (result is not Task task) throw new NotSupportedException($"AMP {method.Name} returned an unexpected result.");
        await task.WaitAsync(ct).ConfigureAwait(false);
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private static bool Accepted(object? ack)
    {
        // Both preflight and Spire command acknowledgement must explicitly succeed.
        return Property(ack, "Accepted") is true;
    }

    private object CreatePrincipal()
    {
        if (_systemPrincipal?.Invoke(null, [Source]) is not object principal)
            throw new NotSupportedException("AMP's system principal cannot be created.");
        return principal;
    }

    private static object Required(IServiceProvider provider, Type? type) =>
        type is null ? throw new NotSupportedException("Required AMP lifecycle type is missing.") :
        provider.GetService(type) ?? throw new NotSupportedException($"AMP service {type.Name} is not registered.");

    public async Task StartAsync(Guid shardId, CancellationToken ct)
    {
        if (!StartAvailable) throw new NotSupportedException("AMP's Shard start lifecycle is unavailable.");
        using var scope = _scopes.CreateScope();
        object shards = Required(scope.ServiceProvider, _shards);
        object starter = Required(scope.ServiceProvider, _starter);
        object? shard = await InvokeAsync(shards, _getShard!, ct, TenantId, shardId);
        if (shard is null) throw new InvalidOperationException("AMP could not find this Shard.");
        // Never request AMP's privileged suspension bypass from Discord.
        object? preflight = await InvokeAsync(starter, _preflight!, ct, TenantId, shard, false, null, ct);
        if (preflight is null || Property(preflight, "Refusal") is not null)
            throw new InvalidOperationException("AMP refused to start this Shard; review its status and required settings.");
        object? spec = Property(preflight, "Spec") ?? throw new InvalidOperationException("AMP returned no application specification.");
        // The newer dispatch accepts an additional string argument before the
        // cancellation token. No user-supplied value is required here; pass null,
        // just as we do for AMP's preflight string parameter. Any AMP rejection
        // is propagated rather than bypassed.
        object?[] dispatchArgs = _dispatch!.GetParameters().Length == 6
            ? [TenantId, shard, spec, CreatePrincipal(), null, ct]
            : [TenantId, shard, spec, CreatePrincipal(), ct];
        object? ack = await InvokeAsync(starter, _dispatch, ct, dispatchArgs);
        if (!Accepted(ack)) throw new InvalidOperationException("The owning AMP Spire refused to start the Shard.");
    }

    public async Task UpdateAsync(Guid shardId, CancellationToken ct)
    {
        if (!UpdateAvailable) throw new NotSupportedException("AMP's Shard update lifecycle is unavailable.");
        using var scope = _scopes.CreateScope();
        object shards = Required(scope.ServiceProvider, _shards);
        object starter = Required(scope.ServiceProvider, _starter);
        object tasks = Required(scope.ServiceProvider, _tasks);
        object launcher = Required(scope.ServiceProvider, _launcher);
        object? shard = await InvokeAsync(shards, _getShard!, ct, TenantId, shardId);
        if (shard is null) throw new InvalidOperationException("AMP could not find this Shard.");
        if (Property(shard, "Suspended") is true)
            throw new InvalidOperationException("A suspended Shard cannot be updated from Discord.");
        object? state = await InvokeAsync(shards, _getState!, ct, TenantId, shardId);
        object? status = Property(state, "Status");
        if (await InvokeAsync(tasks, _underway!, ct, TenantId, shardId, status, ct) is not null)
            throw new InvalidOperationException("AMP already has an update underway for this Shard.");
        object? spec = await InvokeAsync(shards, _refreshSpec!, ct, TenantId, shardId, ct);
        if (spec is null) throw new InvalidOperationException("AMP could not refresh this Shard's application specification.");
        string? anonymous = Property(Property(spec, "Lifecycle"), "SteamUpdateAnonymousLogin")?.ToString();
        if (anonymous is null)
            throw new NotSupportedException("AMP's Steam update credential requirements could not be verified.");
        if (string.Equals(anonymous, "False", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("This update requires Steam credentials; configure and run it through AMP.");
        if (await InvokeAsync(starter, _offline!, ct, TenantId, shard) is not null)
            throw new InvalidOperationException("The owning AMP Spire is offline.");
        // Determine whether AMP can run this update in place before creating a tracked task.
        bool inPlace = status?.ToString() is "Running" or "RunningReady" or "AwaitingUserAction";
        if (inPlace)
            inPlace = await InvokeAsync(launcher, _agentResponding!, ct, TenantId, shardId, ct) is true;
        await InvokeAsync(shards, _rebuildEffective!, ct, TenantId, shardId, spec, null, null, true, ct);
        object principal = CreatePrincipal();
        object? task = await InvokeAsync(tasks, _createUpdate!, ct, shard, spec, principal, ct);
        if (Property(task, "Id") is not Guid taskId || taskId == Guid.Empty)
            throw new InvalidOperationException("AMP did not return a tracked update task.");
        try
        {
            if (inPlace)
            {
                await InvokeAsync(launcher, _sendInPlaceUpdate!, ct, TenantId, shardId, taskId, ct);
            }
            else
            {
                object kind = Enum.Parse(_commandKind!, "Update", ignoreCase: false);
                object? ack = await InvokeAsync(starter, _sendCommand!, ct,
                    TenantId, shard, kind, principal, (Guid?)taskId, ct, null);
                if (!Accepted(ack)) throw new InvalidOperationException("The owning AMP Spire refused the update.");
            }
        }
        catch
        {
            // An update task must not remain Pending when dispatch fails.
            try { await InvokeAsync(tasks, _failTask!, CancellationToken.None,
                TenantId, taskId, "Splinter could not dispatch the update.", CancellationToken.None); }
            catch (Exception ex) { BotLog.Write("AMP update task failure could not be recorded: " + AmpFacade.CleanError(ex)); }
            throw;
        }
    }
}
