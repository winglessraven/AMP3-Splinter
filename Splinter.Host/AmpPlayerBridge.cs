using System.Collections;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Splinter.Host;

// Read-only adapter for AMP 3's ShardUserStore.Get(tenantId, shardId), which
// also backs ShardsController.GetUsers. No console parsing or HTTP credentials.
public sealed record OnlinePlayer(string Id, string Name, DateTimeOffset? ConnectedAt = null);

public sealed class AmpPlayerBridge
{
    private readonly IServiceScopeFactory _scopes;
    private readonly Type? _storeType;
    private readonly MethodInfo? _get;

    public AmpPlayerBridge(IServiceScopeFactory scopes, Type? storeType = null)
    {
        _scopes = scopes;
        var webapi = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "webapi");
        var shared = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "AmpShared");
        _storeType = storeType ??
            webapi?.GetType("webapi.Hubs.ShardUserStore", throwOnError: false) ??
            webapi?.GetType("webapi.Services.ShardUserStore", throwOnError: false) ??
            shared?.GetType("AmpShared.Shards.ShardUserStore", throwOnError: false);
        _get = _storeType?.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Get" && m.GetParameters().Length == 2 &&
                m.GetParameters()[0].ParameterType == typeof(string) &&
                m.GetParameters()[1].ParameterType == typeof(Guid) &&
                typeof(IEnumerable).IsAssignableFrom(m.ReturnType));
    }

    public bool Available => _get is not null;

    // Null means unavailable/unknown, never an empty server. No side effects.
    public IReadOnlyList<OnlinePlayer>? Read(string tenantId, Guid shardId)
    {
        if (_get is null)
            throw new NotSupportedException("AMP ShardUserStore.Get(String, Guid) is unavailable.");
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("AMP tenant ID is required.", nameof(tenantId));
        using var scope = _scopes.CreateScope();
        object? store = scope.ServiceProvider.GetService(_storeType!);
        if (store is null)
            throw new NotSupportedException("AMP ShardUserStore is not registered in dependency injection.");
        try
        {
            object? response = _get.Invoke(store, [tenantId, shardId]);
            return ParseUsers(response);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new InvalidOperationException("AMP connected-user lookup failed.", ex.InnerException);
        }
    }

    public static IReadOnlyList<OnlinePlayer> ParseUsers(object? response)
    {
        if (response is not IEnumerable list || response is string)
            throw new NotSupportedException("Unexpected AMP connected-user list format.");
        var players = new List<OnlinePlayer>();
        foreach (object? item in list)
        {
            if (item is null) continue;
            // Some builds include an explicit disconnected flag, others return
            // connected users only. Respect an explicit false before tracking.
            if (Read(item, "IsConnected", "Connected", "Online", "IsOnline") is bool online && !online)
                continue;
            string? name = Read(item, "Username", "UserName", "PlayerName", "DisplayName", "Name")?.ToString();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl))
                throw new NotSupportedException("AMP connected-user record has no recognised player name. " +
                    item.GetType().Name + " fields=[" + string.Join(",", item.GetType()
                        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                        .Select(p => p.Name)) + "]");
            string id = Read(item, "UserId", "PlayerId", "Id", "Identifier", "SteamId", "Uuid")?.ToString() ?? name;
            if (id.Length == 0 || id.Length > 128 || id.Any(char.IsControl)) id = name;
            DateTimeOffset? connected = DateValue(Read(item, "ConnectedAt", "JoinedAt", "SessionStartedAt", "SessionStart", "ConnectedSince"));
            players.Add(new OnlinePlayer(id, name.Trim(), connected));
        }
        return players.DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static object? Read(object obj, params string[] names)
    {
        foreach (string name in names)
        {
            var property = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property?.GetIndexParameters().Length == 0)
            {
                object? candidate = property.GetValue(obj);
                if (candidate is not null && (candidate is not string text || !string.IsNullOrWhiteSpace(text)))
                    return candidate;
            }
        }
        return null;
    }

    private static DateTimeOffset? DateValue(object? value) => value switch
    {
        DateTimeOffset dto when dto.Year > 2000 => dto,
        DateTime dt when dt.Year > 2000 => new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero),
        string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var parsed) && parsed.Year > 2000 => parsed,
        _ => null
    };
}
