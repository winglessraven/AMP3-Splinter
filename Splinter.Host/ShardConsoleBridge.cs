using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Splinter.Host;

// AMP 3.0.0 has no plugin SDK. This adapter follows the inspected
// ShardsController.SendConsoleInput route: NATS ShardConsole(..., "input").
// Only the identified signatures are accepted; no shell is ever involved.
public sealed class ShardConsoleBridge
{
    private const string Tenant = "_root";
    private readonly IServiceScopeFactory _scopes;
    private readonly Type? _providerType;
    private readonly MethodInfo? _getClient;
    private readonly MethodInfo? _subject;
    private readonly MethodInfo? _publish;

    public ShardConsoleBridge(IServiceScopeFactory scopes, Type? providerType = null, Type? namespaceType = null)
    {
        _scopes = scopes;
        Assembly? shared = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "AmpShared");
        _providerType = providerType ?? shared?.GetType("AmpShared.MessageBus.NatsConnectionProvider", false);
        Type? ns = namespaceType ?? shared?.GetType("AmpShared.Namespaces.Ns", false);
        _subject = ns?.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "ShardConsole" && m.ReturnType == typeof(string) &&
                m.GetParameters().Select(p => p.ParameterType).SequenceEqual(
                    new[] { typeof(string), typeof(Guid), typeof(string) }));
        _getClient = _providerType?.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "GetClientAsync" &&
                m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(CancellationToken) }) &&
                AsyncResultType(m.ReturnType) is not null);
        Type? clientType = _getClient is null ? null : AsyncResultType(_getClient.ReturnType);
        _publish = clientType?.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(TryBindPublish)
            .Where(m => m is not null)
            .Cast<MethodInfo>()
            .FirstOrDefault();
    }

    private static Type? AsyncResultType(Type type) =>
        type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) ||
                               type.GetGenericTypeDefinition() == typeof(ValueTask<>))
            ? type.GenericTypeArguments[0] : null;

    private static MethodInfo? TryBindPublish(MethodInfo method)
    {
        if (method.Name != "PublishAsync") return null;
        if (method.IsGenericMethodDefinition && method.GetGenericArguments().Length == 1)
        {
            try { method = method.MakeGenericMethod(typeof(string)); }
            catch (ArgumentException) { return null; }
        }
        if (method.ContainsGenericParameters) return null;
        var args = method.GetParameters();
        if (args.Length != 7 || args[0].ParameterType != typeof(string) ||
            args[1].ParameterType != typeof(string) ||
            args[6].ParameterType != typeof(CancellationToken) ||
            args.Skip(2).Take(4).Any(p => p.ParameterType.IsValueType &&
                Nullable.GetUnderlyingType(p.ParameterType) is null))
            return null;
        return method.ReturnType == typeof(Task) || method.ReturnType == typeof(ValueTask)
            ? method : null;
    }

    public bool Available => _providerType is not null && _getClient is not null &&
                             _subject is not null && _publish is not null;

    public static bool IsValidCommand(string? command) =>
        command is { Length: > 0 and <= 1024 } &&
        !string.IsNullOrWhiteSpace(command) &&
        !command.Any(char.IsControl);

    public async Task SendAsync(Guid shardId, string command, CancellationToken ct)
    {
        if (shardId == Guid.Empty) throw new ArgumentException("A Shard ID is required.", nameof(shardId));
        if (!IsValidCommand(command))
            throw new ArgumentException("Enter a single-line console command (1–1024 characters).", nameof(command));
        if (!Available)
            throw new NotSupportedException("AMP's Shard console input interface is unavailable.");
        using IServiceScope scope = _scopes.CreateScope();
        object provider = scope.ServiceProvider.GetService(_providerType!) ??
            throw new NotSupportedException("AMP's NATS connection provider is not registered.");
        try
        {
            object pendingClient = _getClient!.Invoke(provider, new object?[] { ct })!;
            object client = (await AwaitResultAsync(pendingClient, ct).ConfigureAwait(false)) ??
                throw new InvalidOperationException("AMP did not return a NATS client.");
            string subject = (string)_subject!.Invoke(null, new object?[] { Tenant, shardId, "input" })!;
            if (string.IsNullOrWhiteSpace(subject))
                throw new InvalidOperationException("AMP did not return a Shard console subject.");
            object? pending = _publish!.Invoke(client,
                new object?[] { subject, command, null, null, null, null, ct });
            await AwaitAsync(pending, ct).ConfigureAwait(false);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new InvalidOperationException("AMP rejected the console command.", ex.InnerException);
        }
    }

    private static async Task<object?> AwaitResultAsync(object pending, CancellationToken ct)
    {
        if (pending is Task task)
        {
            await task.WaitAsync(ct).ConfigureAwait(false);
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }
        MethodInfo? asTask = pending.GetType().GetMethod("AsTask", Type.EmptyTypes);
        if (asTask?.Invoke(pending, null) is Task converted)
        {
            await converted.WaitAsync(ct).ConfigureAwait(false);
            return converted.GetType().GetProperty("Result")?.GetValue(converted);
        }
        throw new NotSupportedException("AMP returned an unrecognised NATS client awaitable.");
    }

    private static async Task AwaitAsync(object? pending, CancellationToken ct)
    {
        switch (pending)
        {
            case Task task:
                await task.WaitAsync(ct).ConfigureAwait(false);
                return;
            case ValueTask valueTask:
                await valueTask.AsTask().WaitAsync(ct).ConfigureAwait(false);
                return;
            default:
                throw new NotSupportedException("AMP returned an unrecognised console publish awaitable.");
        }
    }
}
