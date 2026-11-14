using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Splinter.Host;

// AMP's shard agent publishes ConnectedUserEvent on the documented NATS
// subject. No game console parsing and no additional AMP HTTP endpoints.
public sealed record AmpPlayerEvent(Guid ShardId, string Kind, OnlinePlayer Player,
    DateTimeOffset Timestamp, Guid EventId);

public sealed class AmpPlayerEvents
{
    private const string Tenant = "_root";
    private const string Subject = "amp.tn._root.sr.*.ev.shard.user";
    private readonly IServiceScopeFactory _scopes;
    private readonly Type? _providerType;
    private readonly MethodInfo? _getClient;

    public AmpPlayerEvents(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
        _providerType = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "AmpShared")?
            .GetType("AmpShared.MessageBus.NatsConnectionProvider", false);
        _getClient = _providerType?.GetMethod("GetClientAsync", [typeof(CancellationToken)]);
    }

    // Deliberately rejects mismatched tenants, invalid identities and unknown
    // event kinds. Never deserialize/log Endpoint (a player's IP address).
    public static bool TryParse(string? json, out AmpPlayerEvent? evt)
    {
        evt = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 64 * 1024) return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryString(root, "TenantId", out string tenant) || tenant != Tenant ||
                !TryString(root, "Kind", out string kind) || kind is not ("Joined" or "Left") ||
                !TryString(root, "ShardId", out string shardText) ||
                !Guid.TryParse(shardText, out Guid shard) || shard == Guid.Empty ||
                !root.TryGetProperty("User", out JsonElement user) || user.ValueKind != JsonValueKind.Object ||
                !TryString(user, "Name", out string name) || name.Length > 128 ||
                name.Any(char.IsControl) ||
                !TryPlayerId(user, name, out string id) ||
                !TryString(root, "Timestamp", out string timestamp) ||
                !DateTimeOffset.TryParse(timestamp, out DateTimeOffset at) ||
                at.Year < 2000 || at > DateTimeOffset.UtcNow.AddMinutes(5)) return false;
            DateTimeOffset? joined = null;
            if (TryString(user, "JoinedAt", out string joinedText) &&
                DateTimeOffset.TryParse(joinedText, out DateTimeOffset joinedAt) && joinedAt.Year >= 2000)
                joined = joinedAt;
            Guid eventId = Guid.Empty;
            if (TryString(root, "EventId", out string eventText))
                Guid.TryParse(eventText, out eventId);
            evt = new AmpPlayerEvent(shard, kind, new OnlinePlayer(id, name, joined), at, eventId);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryPlayerId(JsonElement user, string name, out string id)
    {
        // AMP can report a game-specific name before obtaining a stable ID.
        // Use the same identity fallback as ShardUserStore's roster adapter.
        id = TryString(user, "Id", out string candidate) ? candidate : name;
        return id.Length <= 128 && !id.Any(char.IsControl);
    }

    private static bool TryString(JsonElement obj, string property, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(property, out JsonElement element)) return false;
        value = element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : "";
        return !string.IsNullOrWhiteSpace(value);
    }

    public async Task ListenAsync(Func<AmpPlayerEvent, CancellationToken, Task> handle, CancellationToken ct)
    {
        if (_providerType is null || _getClient is null)
            throw new NotSupportedException("AMP NATS connection provider is unavailable.");
        using IServiceScope scope = _scopes.CreateScope();
        object provider = scope.ServiceProvider.GetService(_providerType) ??
            throw new NotSupportedException("AMP NATS connection provider is not registered.");
        object pending = _getClient.Invoke(provider, [ct]) ??
            throw new NotSupportedException("AMP NATS connection was not returned.");
        object client = await AwaitValueAsync(pending, ct).ConfigureAwait(false) ??
            throw new NotSupportedException("AMP NATS connection is unavailable.");
        MethodInfo subscribe = ResolveSubscribe(client.GetType());
        ParameterInfo[] pars = subscribe.GetParameters();
        object?[] arguments = new object?[pars.Length];
        arguments[0] = Subject;
        for (int i = 1; i < arguments.Length; i++)
            arguments[i] = pars[i].ParameterType == typeof(CancellationToken) ? ct :
                pars[i].HasDefaultValue ? pars[i].DefaultValue : null;
        object stream = subscribe.Invoke(client, arguments) ??
            throw new NotSupportedException("AMP NATS subscription returned no stream.");
        // NATS.Client.Core may expose an async enumerable directly or in an
        // awaitable wrapper. Both are handled without a compile-time NATS dependency.
        if (!stream.GetType().GetInterfaces().Any(t => t.IsGenericType &&
            t.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>)))
            stream = await AwaitValueAsync(stream, ct).ConfigureAwait(false) ??
                throw new NotSupportedException("AMP returned no NATS event stream.");
        Type streamInterface = stream.GetType().GetInterfaces().Single(t => t.IsGenericType &&
            t.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));
        Type itemType = streamInterface.GenericTypeArguments[0];
        Type enumeratorInterface = typeof(IAsyncEnumerator<>).MakeGenericType(itemType);
        object enumerator = streamInterface.GetMethod("GetAsyncEnumerator")!
            .Invoke(stream, [ct])!;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                object moving = enumeratorInterface.GetMethod("MoveNextAsync")!.Invoke(enumerator, null)!;
                if (await AwaitValueAsync(moving, ct).ConfigureAwait(false) is not true) break;
                object? msg = enumeratorInterface.GetProperty("Current")!.GetValue(enumerator);
                // NatsMsg<T>.Data holds the already-decoded payload; its Subject
                // is validated separately to prevent cross-Shard event injection.
                string? subject = msg?.GetType().GetProperty("Subject")?.GetValue(msg) as string;
                string? data = msg?.GetType().GetProperty("Data")?.GetValue(msg) as string;
                if (data is not null && TryParse(data, out AmpPlayerEvent? evt) && evt is not null &&
                    SubjectMatches(subject, evt.ShardId))
                {
                    try { await handle(evt, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { BotLog.Write("AMP player event processing failed: " + AmpFacade.CleanError(ex)); }
                }
            }
        }
        finally
        {
            if (enumerator is IAsyncDisposable disposable)
                await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }

    public static bool SubjectMatches(string? subject, Guid shardId)
    {
        string[] tokens = subject?.Split('.') ?? [];
        return tokens.Length == 8 && tokens[0] == "amp" && tokens[1] == "tn" &&
            tokens[2] == Tenant && tokens[3] == "sr" &&
            Guid.TryParse(tokens[4], out Guid parsed) && parsed == shardId &&
            tokens[5] == "ev" && tokens[6] == "shard" && tokens[7] == "user";
    }

    private static MethodInfo ResolveSubscribe(Type clientType)
    {
        var methods = clientType.GetInterfaces().Append(clientType)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(m => m.Name == "SubscribeAsync" && m.IsGenericMethodDefinition &&
                m.GetGenericArguments().Length == 1 &&
                m.GetParameters().Length >= 2 &&
                m.GetParameters()[0].ParameterType == typeof(string) &&
                m.GetParameters()[^1].ParameterType == typeof(CancellationToken) &&
                m.GetParameters().Skip(1).SkipLast(1).All(p => p.IsOptional))
            .DistinctBy(m => m.ToString())
            .ToArray();
        if (methods.Length != 1)
            throw new NotSupportedException("AMP NATS SubscribeAsync signature is unavailable or ambiguous.");
        return methods[0].MakeGenericMethod(typeof(string));
    }

    private static async Task<object?> AwaitValueAsync(object awaited, CancellationToken ct)
    {
        if (awaited is Task task)
        {
            await task.WaitAsync(ct).ConfigureAwait(false);
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }
        if (awaited is ValueTask<bool> vbool) return await vbool.AsTask().WaitAsync(ct).ConfigureAwait(false);
        MethodInfo? asTask = awaited.GetType().GetMethod("AsTask", Type.EmptyTypes);
        if (asTask?.Invoke(awaited, null) is Task converted)
        {
            await converted.WaitAsync(ct).ConfigureAwait(false);
            return converted.GetType().GetProperty("Result")?.GetValue(converted);
        }
        throw new NotSupportedException("AMP NATS returned an unsupported async operation.");
    }
}
