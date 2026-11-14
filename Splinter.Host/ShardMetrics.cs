using System.Collections;
using System.Reflection;
using System.Text.Json;

namespace Splinter.Host;

// Read-only, bounded extraction of AMP 3's beta Shard state/metric shapes.
// Never assume missing player data means zero. Keep reflection out of the bot.
public static class ShardMetrics
{
    private static readonly HashSet<string> OnlineNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Players", "PlayersOnline", "OnlinePlayers", "PlayerCount", "CurrentPlayerCount",
        "CurrentPlayers", "ActivePlayers", "ActiveUsers", "CurrentUsers", "OnlineUsers",
        "ConnectedPlayers", "ConnectedUsers", "NumberOfPlayers", "ActiveConnections"
    };
    private static readonly HashSet<string> MaxNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "MaxPlayers", "MaxPlayerCount", "MaximumPlayers", "PlayerLimit", "MaxUsers",
        "MaximumUsers", "MaximumPlayerCount", "PlayerSlots", "MaxSlots",
        "ServerMaxPlayers"
    };
    // Only read explicitly named limits. AMP's graph axis maxima are not
    // necessarily game limits or memory allocations.
    private static readonly string[] ConfigurationScopes =
    [
        "AppSettings", "GameSettings", "Configuration", "Settings", "ResourceLimits",
        "Resources", "Limits", "RuntimeConfig"
    ];
    private static readonly string[] MemoryCapNames =
    [
        "MemoryLimitMB", "MemoryLimitMb", "MaxMemoryMB", "MaximumMemoryMB",
        "MaxRamMB", "RamLimitMB", "MemoryAllocationMB", "AllocatedMemoryMB"
    ];
    private static readonly string[] Children =
    [
        "Metrics", "MetricValues", "Statistics", "PlayerMetrics", "PlayerCounts",
        "GameMetrics", "ServerMetrics", "ServerState", "Runtime", "StatusDetails",
        "State", "Status", "GameState", "ServerStatus"
    ];

    // AMP 3's live Shard state exposes Metrics.ActiveConnections,
    // CpuUsagePercent, MemoryUsageMB and MemoryLimitMB (verified from the
    // 3.0.0 beta). A metrics-history sample instead exposes ActiveUsers,
    // MaxUsers, CpuPercent, MemoryMb and MemoryLimitMb. Both are read-only.
    public sealed record RuntimeReading(
        int? Online, int? Maximum, double? CpuPercent, double? MemoryMb, double? MemoryLimitMb);

    public static RuntimeReading ResolveRuntime(object? state, object? shard = null, object? historySample = null)
    {
        (int? online, int? maximum) = Resolve(state, shard);
        double? cpu = null, memory = null, memoryLimit = null;

        // Prefer live state metrics; use a recent history sample only to fill gaps.
        foreach (object? candidate in new[] { Member(state, "Metrics"), state,
                                               Member(shard, "Metrics"), shard })
        {
            if (candidate is null) continue;
            object? native = Member(candidate, "Sample") ?? candidate;
            online ??= ToNonNegativeInt(Member(native, "ActiveConnections"))
                ?? ToNonNegativeInt(Member(native, "ActiveUsers"));
            maximum ??= ToPositiveInt(Member(native, "MaxUsers"));
            cpu ??= ToNonNegativeDouble(Member(native, "CpuUsagePercent"))
                ?? ToNonNegativeDouble(Member(native, "CpuPercent"));
            memory ??= ToNonNegativeDouble(Member(native, "MemoryUsageMB"))
                ?? ToNonNegativeDouble(Member(native, "MemoryMb"));
            memoryLimit ??= ToPositiveDouble(Member(native, "MemoryLimitMB"))
                ?? ToPositiveDouble(Member(native, "MemoryLimitMb"));

            // AMP's older/JSON metric envelope uses named metric entries.
            foreach (object entry in Enumerate(native, 64))
            {
                string key = Normalize(Member(entry, "Name")?.ToString() ??
                    Member(entry, "Key")?.ToString() ?? "");
                object? value = Member(entry, "Value");
                if (value is null) continue;
                if (key is "CPU" or "CPUUSAGE" or "CPUPERCENT")
                    cpu ??= ToNonNegativeDouble(Member(value, "Percent") ?? Member(value, "RawValue") ?? value);
                if (key is "MEMORY" or "MEMORYUSAGE" or "RAM" or "RAMUSAGE")
                {
                    string units = Member(value, "Units")?.ToString() ?? "MB";
                    double factor = units.Equals("B", StringComparison.OrdinalIgnoreCase) ? 1.0 / 1_048_576 :
                        units.Equals("KB", StringComparison.OrdinalIgnoreCase) ? 1.0 / 1024 :
                        units.Equals("GB", StringComparison.OrdinalIgnoreCase) ? 1024 : 1;
                    memory ??= ToNonNegativeDouble(Member(value, "RawValue") ?? Member(value, "Value") ?? value) * factor;
                    if (ToPositiveDouble(Member(value, "MaxValue") ?? Member(value, "Maximum")) is double cap)
                        memoryLimit ??= cap * factor;
                }
            }
        }
        // Shard listings may carry configured resource/game limits
        // separately from the live Metrics object. Only known positive values
        // are accepted: 0 means "not supplied / unlimited", never a literal
        // 0 MB capacity or a zero-player server.
        foreach (object? root in new[] { shard, state })
        {
            if (root is null) continue;
            maximum ??= FindPlayerCapacity(root);
            memoryLimit ??= FindMemoryCapacity(root);
            foreach (string scopeName in ConfigurationScopes)
            {
                object? scope = Member(root, scopeName);
                if (scope is null) continue;
                maximum ??= FindPlayerCapacity(scope);
                memoryLimit ??= FindMemoryCapacity(scope);
            }
        }

        // History is the last resort. A stale / empty history sample cannot
        // replace live readings or a configured capacity.
        if (historySample is not null)
        {
            object sample = Member(historySample, "Sample") ?? historySample;
            online ??= ToNonNegativeInt(Member(sample, "ActiveUsers"));
            maximum ??= ToPositiveInt(Member(sample, "MaxUsers"));
            cpu ??= ToNonNegativeDouble(Member(sample, "CpuPercent"));
            memory ??= ToNonNegativeDouble(Member(sample, "MemoryMb"));
            memoryLimit ??= ToPositiveDouble(Member(sample, "MemoryLimitMb"));
        }
        return new RuntimeReading(online, maximum, cpu, memory, memoryLimit);
    }

    private static int? FindPlayerCapacity(object source)
    {
        foreach (string name in MaxNames)
        {
            int? value = ToPositiveInt(Member(source, name));
            if (value.HasValue) return value;
        }
        return null;
    }

    private static double? FindMemoryCapacity(object source)
    {
        foreach (string name in MemoryCapNames)
        {
            double? value = ToPositiveDouble(Member(source, name));
            if (value.HasValue) return value;
        }
        // An explicitly named bytes value is converted to MB. Do not
        // interpret arbitrary memory numbers as megabytes.
        double? bytes = ToPositiveDouble(Member(source, "MemoryLimitBytes")) ??
            ToPositiveDouble(Member(source, "MaxMemoryBytes"));
        return bytes / 1048576.0;
    }

    // Log property names, never configuration values or tokens.
    public static string CapacityShape(object? shard)
    {
        var parts = new List<string> { "Shard=[" + Shape(shard) + "]" };
        foreach (string name in ConfigurationScopes)
        {
            object? scope = Member(shard, name);
            if (scope is not null) parts.Add(name + "=[" + Shape(scope) + "]");
        }
        return string.Join("; ", parts);
    }

    private static int? ToPositiveInt(object? value) =>
        ToNonNegativeInt(value) is int number && number > 0 ? number : null;

    private static double? ToPositiveDouble(object? value) =>
        ToNonNegativeDouble(value) is double number && number > 0 ? number : null;

    // Use only fresh history entries; an old resource sample must not be
    // presented as current data after a game/server restart.
    public static object? LatestRecentSample(object? result, DateTimeOffset now)
    {
        if (result is not IEnumerable sequence || result is string) return null;
        object? latest = null;
        DateTimeOffset latestTime = DateTimeOffset.MinValue;
        int visited = 0;
        foreach (object? item in sequence)
        {
            if (++visited > 256) break;
            if (item is null) continue;
            object sample = Member(item, "Sample") ?? item;
            object? stamp = Member(sample, "Timestamp") ?? Member(item, "Timestamp");
            DateTimeOffset time = stamp switch
            {
                DateTimeOffset dto => dto,
                DateTime dt => new DateTimeOffset(dt.ToUniversalTime()),
                JsonElement json when json.ValueKind == JsonValueKind.String &&
                    json.TryGetDateTimeOffset(out DateTimeOffset parsed) => parsed,
                _ => DateTimeOffset.MinValue
            };
            if (time > now.AddSeconds(30) || time < now.AddMinutes(-3)) continue;
            if (time >= latestTime) { latestTime = time; latest = sample; }
        }
        return latest;
    }

    private static int? ToNonNegativeInt(object? value)
    {
        double? parsed = ToNonNegativeDouble(value);
        return parsed is >= 0 and <= int.MaxValue && Math.Truncate(parsed.Value) == parsed.Value
            ? (int)parsed.Value : null;
    }

    private static double? ToNonNegativeDouble(object? value)
    {
        if (value is null) return null;
        if (value is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Number && json.TryGetDouble(out double number) && double.IsFinite(number) && number >= 0)
                return number;
            if (json.ValueKind == JsonValueKind.String) value = json.GetString();
            else return null;
        }
        if (!double.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double result))
            return null;
        return double.IsFinite(result) && result >= 0 ? result : null;
    }

    public static (int? Online, int? Maximum) Resolve(object? state, object? shard = null)
    {
        int? online = null;
        int? maximum = null;
        var queue = new Queue<(object Value, int Depth)>();
        if (state is not null) queue.Enqueue((state, 0));
        if (shard is not null) queue.Enqueue((shard, 0));

        // Bound reads so an unexpected SDK object graph cannot stall AMP.
        int examined = 0;
        while (queue.Count != 0 && ++examined <= 96)
        {
            (object scope, int depth) = queue.Dequeue();
            foreach (string key in OnlineNames)
            {
                object? value = Member(scope, key);
                if (value is null) continue;
                (int? a, int? b) = NumberOrPair(value);
                online ??= a;
                maximum ??= b is > 0 ? b : null;
            }
            foreach (string key in MaxNames)
            {
                int? cap = NumberOrPair(Member(scope, key)).Item1;
                maximum ??= cap is > 0 ? cap : null;
            }
            if (online.HasValue && maximum.HasValue) break;

            // Common AMP metric shape: [{ Name: "Players", Value: "2/10" }].
            // Also supports dictionaries such as { "Players": { Value: "2/10" } }.
            foreach (object item in Enumerate(scope, 64))
            {
                string? name = Member(item, "Name")?.ToString() ??
                               Member(item, "Key")?.ToString() ??
                               Member(item, "MetricName")?.ToString() ??
                               Member(item, "Id")?.ToString();
                if (name is null) continue;
                name = Normalize(name);
                object? value = Member(item, "Value") ?? Member(item, "CurrentValue") ??
                    Member(item, "Current") ?? Member(item, "NumericValue");
                (int? a, int? b) = NumberOrPair(value);
                if (OnlineNames.Contains(name)) { online ??= a; maximum ??= b is > 0 ? b : null; }
                if (MaxNames.Contains(name)) maximum ??= a is > 0 ? a : null;
            }
            if (depth >= 3) continue;
            foreach (string child in Children)
            {
                object? nested = Member(scope, child);
                if (nested is not null && nested is not string && !ReferenceEquals(nested, scope))
                    queue.Enqueue((nested, depth + 1));
            }
        }
        return (online, maximum);
    }

    // Diagnostic emits schema names only, not any potentially sensitive values.
    public static string Shape(object? value)
    {
        if (value is null) return "(null)";
        if (value is JsonElement json && json.ValueKind == JsonValueKind.Object)
            return string.Join(",", json.EnumerateObject().Take(30).Select(p => p.Name));
        if (value is IDictionary dict)
            return string.Join(",", dict.Keys.Cast<object?>().Take(30).Select(k => k?.ToString()));
        return string.Join(",", value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.GetIndexParameters().Length == 0).Take(30).Select(p => p.Name));
    }

    private static object? Member(object? source, string key)
    {
        if (source is null) return null;
        if (source is JsonElement json && json.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in json.EnumerateObject())
                if (prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) return prop.Value;
            return null;
        }
        if (source is IDictionary dictionary)
        {
            foreach (object? dictKey in dictionary.Keys)
                if (dictKey is not null && Normalize(dictKey.ToString() ?? "") == Normalize(key))
                    return dictionary[dictKey];
            return null;
        }
        try
        {
            return source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.GetIndexParameters().Length == 0 &&
                    Normalize(p.Name) == Normalize(key))?.GetValue(source);
        }
        catch { return null; }
    }

    private static IEnumerable<object> Enumerate(object scope, int limit)
    {
        if (scope is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Array)
                return json.EnumerateArray().Take(limit).Cast<object>().ToArray();
            if (json.ValueKind == JsonValueKind.Object)
                return json.EnumerateObject().Take(limit)
                    .Select(p => (object)new MetricEntry(p.Name, p.Value)).ToArray();
        }
        if (scope is IDictionary dict)
        {
            var results = new List<object>();
            foreach (DictionaryEntry entry in dict)
            {
                if (entry.Key is not null)
                    results.Add(new MetricEntry(entry.Key.ToString() ?? "", entry.Value));
                if (results.Count == limit) break;
            }
            return results;
        }
        if (scope is IEnumerable sequence && scope is not string)
            return sequence.Cast<object?>().Where(x => x is not null).Take(limit).Cast<object>().ToArray();
        return [];
    }

    private sealed record MetricEntry(string Name, object? Value);

    private static (int?, int?) NumberOrPair(object? source)
    {
        if (source is null) return (null, null);
        if (source is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out int number) && number >= 0)
                return (number, null);
            if (json.ValueKind == JsonValueKind.String) return ParseString(json.GetString());
            if (json.ValueKind == JsonValueKind.Array) return (json.GetArrayLength(), null);
        }
        if (source is string text) return ParseString(text);
        if (source is int i && i >= 0) return (i, null);
        if (source is long l && l >= 0 && l <= int.MaxValue) return ((int)l, null);
        if (source is ICollection collection && source is not IDictionary) return (collection.Count, null);
        object? current = Member(source, "Current") ?? Member(source, "Online") ??
            Member(source, "CurrentValue") ?? Member(source, "RawValue") ?? Member(source, "Value") ?? Member(source, "Count");
        object? max = Member(source, "Maximum") ?? Member(source, "Max") ??
            Member(source, "MaxValue") ?? Member(source, "Limit");
        if (ReferenceEquals(source, current)) return (null, null);
        (int? a, int? b) = current is null ? (null, null) : NumberOrPair(current);
        int? m = max is null || ReferenceEquals(source, max) ? null : NumberOrPair(max).Item1;
        return (a, m ?? b);
    }

    private static (int?, int?) ParseString(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        string[] halves = text.Split('/', 2);
        if (!int.TryParse(halves[0].Trim(), out int online) || online < 0) return (null, null);
        if (halves.Length == 1) return (online, null);
        return int.TryParse(halves[1].Trim(), out int max) && max >= 0
            ? (online, max) : (online, null);
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
