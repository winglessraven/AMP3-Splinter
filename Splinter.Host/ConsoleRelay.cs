using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace Splinter.Host;

// The AMP 3 controller uses ConsoleHistoryService.GetPageAsync(tenant, shard, beforeSeq,
// limit, ct). Keep the unversioned AMP contract entirely within this adapter.
// AMP 3's ConsoleLineDto may include richer metadata. Keep it in the
// compatibility adapter rather than throwing it away at the Discord boundary.
// Null means AMP did not provide that field, NOT that the line is non-chat.
public sealed record ConsoleEntry(long Sequence, string Text,
    string? Level = null, string? Kind = null, string? Origin = null, string? StreamId = null);

public sealed class ConsoleHistoryBridge
{
    private const string Tenant = "_root";
    private readonly IServiceScopeFactory _scopes;
    private readonly Type? _serviceType;
    private readonly MethodInfo? _getPage;

    public ConsoleHistoryBridge(IServiceScopeFactory scopes, Type? serviceType = null)
    {
        _scopes = scopes;
        _serviceType = serviceType ?? AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "webapi")?
            .GetType("webapi.Hubs.ConsoleHistoryService", throwOnError: false);
        _getPage = _serviceType?.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "GetPageAsync" &&
                m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[]
                { typeof(string), typeof(Guid), typeof(long?), typeof(int), typeof(CancellationToken) }));
    }

    public bool Available => _getPage is not null;

    public async Task<IReadOnlyList<ConsoleEntry>> LatestAsync(Guid shardId, CancellationToken ct)
    {
        if (!Available) throw new NotSupportedException("AMP console history is unavailable.");
        using var scope = _scopes.CreateScope();
        object instance = scope.ServiceProvider.GetService(_serviceType!) ??
            throw new NotSupportedException("AMP console history is not registered.");
        try
        {
            object pending = _getPage!.Invoke(instance, new object?[] { Tenant, shardId, null, 200, ct })!;
            object? page;
            if (pending is Task task)
            {
                await task.WaitAsync(ct).ConfigureAwait(false);
                page = task.GetType().GetProperty("Result")?.GetValue(task);
            }
            else
            {
                var asTask = pending.GetType().GetMethod("AsTask", Type.EmptyTypes);
                if (asTask?.Invoke(pending, null) is not Task converted)
                    throw new NotSupportedException("AMP console history returned an unsupported awaitable.");
                await converted.WaitAsync(ct).ConfigureAwait(false);
                page = converted.GetType().GetProperty("Result")?.GetValue(converted);
            }
            try { return ParsePage(page); }
            catch (NotSupportedException ex)
            {
                // Names and CLR types only: no player chat, console text or secrets.
                throw new NotSupportedException(ex.Message + " " + DescribeShape(page), ex);
            }
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new InvalidOperationException("AMP console history request failed.", ex.InnerException);
        }
    }

    // AMP 3 can return console records as CLR models, JSON nodes or transport
    // envelopes. Inspect only named console fields; never stringify an unknown
    // object, since its ToString() may expose non-console information.
    private static readonly string[] PageFields =
        ["Entries", "Items", "Lines", "Messages", "Records", "Events", "History"];
    private static readonly string[] SequenceFields =
        ["Seq", "Sequence", "SequenceNumber", "SequenceId", "StreamSequence", "StreamSeq", "Cursor", "Position", "Index", "Offset", "Id", "Key", "Item1"];
    private static readonly string[] TextFields =
        ["Text", "Line", "Message", "Content", "Output", "RenderedText", "PlainText", "RawText", "ConsoleText", "OutputText", "Rendered", "Data", "Payload", "Body", "Value", "Item2"];
    private static readonly string[] EnvelopeFields =
        ["Entry", "Record", "Envelope", "Event", "Console", "Metadata", "Meta", "Header"];
    private static readonly System.Text.UTF8Encoding Utf8Strict = new(false, true);

    public static IReadOnlyList<ConsoleEntry> ParsePage(object? page)
    {
        if (page is null) throw new NotSupportedException("AMP returned no console history page.");
        object? records = Get(page, PageFields) ?? page;
        IEnumerable sequence = records is JsonElement json && json.ValueKind == JsonValueKind.Array
            ? json.EnumerateArray().ToArray()
            : records as IEnumerable ?? throw new NotSupportedException(
                "Unrecognised AMP console history page format.");
        if (records is string || records is byte[])
            throw new NotSupportedException("Unrecognised AMP console history page format.");

        var result = new List<ConsoleEntry>();
        foreach (object? item in sequence)
        {
            if (item is null) continue;
            object? body = FindNamed(item, TextFields, EnvelopeFields, 0);
            if (!TryFindSequence(item, out long seq) || seq < 0 ||
                !TryText(body, out string text))
                throw new NotSupportedException("Unrecognised AMP console history entry format.");
            if (!string.IsNullOrWhiteSpace(text))
                result.Add(new ConsoleEntry(seq, text,
                    ReadMetadata(item, "Level", "LogLevel"),
                    ReadMetadata(item, "Kind"),
                    ReadMetadata(item, "Origin"),
                    ReadMetadata(item, "StreamId")));
        }
        return result.OrderBy(entry => entry.Sequence).DistinctBy(entry => entry.Sequence).ToArray();
    }

    private static object? FindNamed(object item, string[] fields, string[] wrappers, int depth)
    {
        object? direct = Get(item, fields);
        if (direct is not null) return direct;
        if (depth >= 3) return null;
        foreach (string name in wrappers)
        {
            object? child = Get(item, name);
            if (child is null || child is string || child is byte[]) continue;
            object? nested = FindNamed(child, fields, wrappers, depth + 1);
            if (nested is not null) return nested;
        }
        return null;
    }

    // Only inspect named fields on recognised console envelopes. AMP may return
    // CLR DTOs, JsonElement values or metadata nested within an envelope.
    // Unknown objects are never stringified, particularly for diagnostics.
    private static string? ReadMetadata(object item, params string[] names) =>
        FindMetadata(item, names, 0);

    private static string? FindMetadata(object item, string[] names, int depth)
    {
        string? direct = AsMetadataScalar(Get(item, names));
        if (direct is not null) return direct;
        if (depth >= 3) return null;
        foreach (string wrapper in EnvelopeFields)
        {
            object? nested = Get(item, wrapper);
            if (nested is null || nested is string || nested is byte[]) continue;
            string? value = FindMetadata(nested, names, depth + 1);
            if (value is not null) return value;
        }
        return null;
    }

    private static string? AsMetadataScalar(object? value)
    {
        if (value is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.String) return json.GetString();
            if (json.ValueKind == JsonValueKind.Number) return json.GetRawText();
            return null;
        }
        // Only explicitly typed primitive/enum values. An arbitrary AMP DTO's
        // ToString() could reveal content, credentials or other private data.
        return value switch
        {
            string text => text,
            Guid id => id.ToString("D"),
            Enum named => named.ToString(),
            byte or sbyte or short or ushort or int or uint or long or ulong =>
                Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            _ => null
        };
    }

    private static bool TryFindSequence(object item, out long sequence, int depth = 0)
    {
        foreach (string name in SequenceFields)
        {
            if (TrySequence(Get(item, name), out sequence) && sequence >= 0)
                return true;
        }
        if (depth < 3)
        {
            foreach (string wrapper in EnvelopeFields)
            {
                object? nested = Get(item, wrapper);
                if (nested is not null && nested is not string &&
                    TryFindSequence(nested, out sequence, depth + 1))
                    return true;
            }
        }
        sequence = 0;
        return false;
    }

    private static bool TrySequence(object? source, out long sequence)
    {
        sequence = 0;
        if (source is null) return false;
        if (source is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Number) return json.TryGetInt64(out sequence);
            if (json.ValueKind != JsonValueKind.String) return false;
            source = json.GetString();
        }
        return source switch
        {
            long value => Assign(value, out sequence),
            int value => Assign(value, out sequence),
            short value => Assign(value, out sequence),
            uint value => Assign(value, out sequence),
            ulong value when value <= long.MaxValue => Assign((long)value, out sequence),
            string value => long.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out sequence),
            _ => false
        };
    }

    private static bool Assign(long value, out long result)
    {
        result = value;
        return true;
    }

    private static bool TryText(object? source, out string text, int depth = 0)
    {
        text = "";
        if (source is null || depth > 4) return false;
        switch (source)
        {
            case string value:
                text = value;
                return true;
            case JsonDocument document:
                return TryText(document.RootElement, out text, depth + 1);
            case JsonElement json when json.ValueKind == JsonValueKind.String:
                text = json.GetString() ?? "";
                return true;
            case JsonElement json when json.ValueKind == JsonValueKind.Object:
                return TryText(Get(json, TextFields), out text, depth + 1);
            case byte[] bytes:
                return DecodeUtf8(bytes, out text);
            case ReadOnlyMemory<byte> readOnlyMemory:
                return DecodeUtf8(readOnlyMemory.Span, out text);
            case Memory<byte> writableMemory:
                return DecodeUtf8(writableMemory.Span, out text);
        }
        // This is restricted to explicitly named text/payload fields. Never
        // call ToString(), deserialize arbitrary objects or traverse all fields.
        object? inner = Get(source, TextFields);
        return inner is not null && !ReferenceEquals(source, inner) &&
            TryText(inner, out text, depth + 1);
    }

    private static bool DecodeUtf8(ReadOnlySpan<byte> bytes, out string text)
    {
        text = "";
        if (bytes.Length is 0 or > 65536 || bytes.Contains((byte)0)) return false;
        try
        {
            text = Utf8Strict.GetString(bytes).TrimStart('\uFEFF');
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }

    // Diagnostics contain CLR type and property/field names ONLY. Do not log
    // actual console lines, embedded credentials, JSON values or exception data.
    public static string DescribeShape(object? page)
    {
        if (page is null) return "page=(null)";
        static string Summary(object item)
        {
            if (item is JsonElement json)
                return json.ValueKind == JsonValueKind.Object
                    ? "JsonObject[" + string.Join(",", json.EnumerateObject().Take(24)
                        .Select(prop => prop.Name + ":" + prop.Value.ValueKind)) + "]"
                    : "JsonElement[" + json.ValueKind + "]";
            Type type = item.GetType();
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(prop => prop.GetIndexParameters().Length == 0)
                .Select(prop => prop.Name + ":" + prop.PropertyType.Name);
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(field => field.Name + ":" + field.FieldType.Name);
            return type.Name + "[" + string.Join(",", props.Concat(fields).Take(24)) + "]";
        }
        object? records = Get(page, PageFields);
        object? first = records is JsonElement json && json.ValueKind == JsonValueKind.Array
            ? json.EnumerateArray().Cast<object>().FirstOrDefault()
            : records is IEnumerable sequence && records is not string
                ? sequence.Cast<object?>().FirstOrDefault(item => item is not null) : null;
        var result = "page=" + Summary(page) + "; entry=" +
            (first is null ? "(none)" : Summary(first));
        if (first is not null)
        {
            foreach (string name in new[] { "Data", "Payload", "Message", "Value", "Entry", "Metadata" })
            {
                object? nested = Get(first, name);
                if (nested is not null && nested is not string && nested is not byte[])
                    result += "; " + name + "=" + Summary(nested);
            }
        }
        return result;
    }

    private static object? Get(object item, params string[] names)
    {
        if (item is JsonDocument document) item = document.RootElement;
        if (item is JsonElement json)
        {
            if (json.ValueKind != JsonValueKind.Object) return null;
            foreach (string name in names)
                foreach (JsonProperty property in json.EnumerateObject())
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                        return property.Value;
            return null;
        }
        Type type = item.GetType();
        foreach (string name in names)
        {
            PropertyInfo? prop = type.GetProperty(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop is not null && prop.GetIndexParameters().Length == 0)
            {
                object? value = prop.GetValue(item);
                if (value is not null) return value;
            }
            FieldInfo? field = type.GetField(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (field is not null)
            {
                object? value = field.GetValue(item);
                if (value is not null) return value;
            }
        }
        return null;
    }

}

public sealed class ConsoleRelayState
{
    public bool Initialized { get; set; }
    public long LastSequence { get; set; }
    public string ValheimJoinCode { get; set; } = "";
}

// Per-session, opt-in diagnostics. No line text is ever logged. A fixed budget
// prevents even very busy game consoles from flooding the AMP/Splinter logs.
public sealed class ConsoleMetadataProbe
{
    public const int MaximumSamples = 120;
    private bool _enabled;
    private int _samples;

    public IReadOnlyList<string> Observe(bool enabled, Guid shardId, IReadOnlyList<ConsoleEntry> entries,
        string source = "history")
    {
        if (!enabled)
        {
            _enabled = false;
            _samples = 0;
            return Array.Empty<string>();
        }
        var report = new List<string>();
        if (!_enabled)
        {
            _enabled = true;
            _samples = 0;
            report.Add($"Shard {shardId:D} AMP console metadata probe enabled: " +
                $"source={(source == "jetstream" ? "jetstream" : "history")}; line content omitted; sample limit=120");
        }
        foreach (ConsoleEntry entry in entries)
        {
            if (_samples >= MaximumSamples) break;
            report.Add($"Shard {shardId:D} AMP console metadata: " +
                $"source={(source == "jetstream" ? "jetstream" : "history")}; " +
                $"seq={entry.Sequence}; level={SafeValue(entry.Level)}; " +
                $"kind={SafeValue(entry.Kind)}; origin={SafeValue(entry.Origin)}; " +
                $"streamId={SafeValue(entry.StreamId)}");
            _samples++;
            if (_samples == MaximumSamples)
                report.Add($"Shard {shardId:D} AMP console metadata probe limit reached; " +
                    "disable and re-enable the setting to capture more lines.");
        }
        return report;
    }

    // Preserve simple AMP metadata labels but refuse content-shaped values,
    // addresses, paths, URLs, control characters and unbounded identifiers.
    public static string SafeValue(string? value)
    {
        if (value is null) return "(null)";
        if (value.Length == 0) return "(empty)";
        if (value.Length > 64 || !Regex.IsMatch(value, @"^[A-Za-z0-9_][A-Za-z0-9_. -]*$",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) ||
            Regex.IsMatch(value, @"\b\d{1,3}(?:\.\d{1,3}){3}\b",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            return "(redacted)";
        return value;
    }
}

// Default to the anticipated CHAT level, with the legacy Minecraft fallback
// retained for current-beta INFO lines when no explicit level is configured.
public static class ConsoleChatRelay
{
    private static readonly Regex LegacyMinecraftChat = new(
        @"<(?<user>[A-Za-z0-9_]{1,16})>\s+(?<text>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static bool TryFormat(ShardBotConfig config, ConsoleEntry entry, out string message)
    {
        message = "";
        if (!config.SendChatToDiscord ||
            !LifecycleNotifications.IsValidChannel(config.ChatDiscordChannel) ||
            string.IsNullOrWhiteSpace(entry.Text)) return false;

        // Assume AMP will expose CHAT as a structured console level in a
        // future beta. An explicit level overrides this assumption. On the
        // current beta, preserve the existing Minecraft relay only when no
        // explicit level has been configured.
        string expectedLevel = string.IsNullOrWhiteSpace(config.ChatConsoleLevel)
            ? "CHAT" : config.ChatConsoleLevel;
        if (string.Equals(entry.Level, expectedLevel, StringComparison.OrdinalIgnoreCase))
        {
            message = SafeChatText(entry.Text);
        }
        else if (string.IsNullOrWhiteSpace(config.ChatConsoleLevel))
        {
            Match match = LegacyMinecraftChat.Match(entry.Text);
            if (!match.Success) return false;
            message = $"**{SafeChatText(match.Groups["user"].Value)}:** " +
                SafeChatText(match.Groups["text"].Value);
        }
        else return false;
        return !string.IsNullOrWhiteSpace(message);
    }

    // AMP lines are untrusted: prevent arbitrary Markdown, mass mentions and
    // formatting from console or in-game chat reaching Discord.
    public static string SafeChatText(string input)
    {
        string cleaned = ConsoleRelay.SafeLine(input);
        var result = new StringBuilder(cleaned.Length);
        foreach (char c in cleaned)
        {
            if (c is '\\' or '*' or '_' or '~' or '|' or '[' or ']' or '(' or ')' or '>' or '#')
                result.Append('\\');
            result.Append(c);
            if (result.Length >= 1800) break;
        }
        return result.ToString();
    }
}

// Suppress echoes of successfully sent Discord messages, without suppressing
// unrelated players or leaking content to disk. Each Shard has its own queue.
public sealed class ConsoleChatEchoFilter
{
    private readonly object _sync = new();
    private readonly Queue<(string Content, DateTimeOffset Time)> _outgoing = new();
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(3);

    public void Record(string content, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        lock (_sync)
        {
            Prune(now);
            _outgoing.Enqueue((content, now));
            while (_outgoing.Count > 32) _outgoing.Dequeue();
        }
    }

    public void Forget(string content)
    {
        lock (_sync)
        {
            var remaining = _outgoing.Where(entry => entry.Content != content).ToArray();
            _outgoing.Clear();
            foreach (var entry in remaining) _outgoing.Enqueue(entry);
        }
    }

    public bool IsEcho(string line, DateTimeOffset now)
    {
        lock (_sync)
        {
            Prune(now);
            return _outgoing.Any(entry => line.Contains(entry.Content, StringComparison.Ordinal));
        }
    }

    private void Prune(DateTimeOffset now)
    {
        while (_outgoing.Count > 0 && now - _outgoing.Peek().Time > Lifetime)
            _outgoing.Dequeue();
    }
}

public static class ConsoleRelay
{
    public static string PathFor(Guid id) => Path.Combine(ConfigStore.Root, "console", id.ToString("D") + ".json");

    // Poll history periodically regardless of live subscription status: the
    // listener can be connected yet silent, filtered or stalled. Both paths
    // share a cursor, so a recovery poll does not duplicate live deliveries.
    public static bool ShouldReconcile(DateTimeOffset lastPoll, DateTimeOffset now,
        int configuredSeconds) =>
        now - lastPoll >= TimeSpan.FromSeconds(Math.Clamp(configuredSeconds, 10, 300));

    // First poll establishes a baseline; a reconnect never dumps old history.
    public static IReadOnlyList<ConsoleEntry> Consume(ConsoleRelayState state,
        IReadOnlyList<ConsoleEntry> received, bool allowSequenceReset = true)
    {
        if (received.Count == 0) return Array.Empty<ConsoleEntry>();
        long newest = received.Max(x => x.Sequence);
        if (!state.Initialized)
        {
            state.Initialized = true;
            state.LastSequence = newest;
            return Array.Empty<ConsoleEntry>();
        }
        if (newest < state.LastSequence)
        {
            // History indexing can lag behind an active live consumer. Never
            // rewind that consumer's cursor merely because a poll is older.
            // In polling-only mode AMP stream recreation can rebase safely.
            if (allowSequenceReset) state.LastSequence = newest;
            return Array.Empty<ConsoleEntry>();
        }
        var unread = received.Where(x => x.Sequence > state.LastSequence)
            .OrderBy(x => x.Sequence).DistinctBy(x => x.Sequence).ToArray();
        state.LastSequence = Math.Max(state.LastSequence, newest);
        return unread;
    }

    // A live consumer can deliver an older record after a concurrent history
    // reconciliation. Never interpret that as a JetStream sequence reset: it
    // would rewind the cursor and create duplicate Discord messages.
    public static IReadOnlyList<ConsoleEntry> ConsumeLive(ConsoleRelayState state, ConsoleEntry entry)
    {
        if (entry.Sequence <= 0) return Array.Empty<ConsoleEntry>();
        if (!state.Initialized)
        {
            state.Initialized = true;
            state.LastSequence = entry.Sequence;
            // A new live consumer starts at DeliverPolicy.New when history
            // cannot be read, so its first entry is genuinely new.
            return [entry];
        }
        if (entry.Sequence <= state.LastSequence) return Array.Empty<ConsoleEntry>();
        state.LastSequence = entry.Sequence;
        return [entry];
    }

    public static bool Excluded(string line, string? expressions)
    {
        if (string.IsNullOrWhiteSpace(expressions)) return false;
        foreach (string entry in expressions.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.Length is 0 or > 200) continue;
            string pattern = Regex.Escape(entry).Replace("\\*", ".*");
            if (Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100))) return true;
        }
        return false;
    }

    public static string? ValheimCode(string input)
    {
        Match m = Regex.Match(input, @"\bjoin\s*code\s*[:=]\s*(?<code>\d{6})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return m.Success ? m.Groups["code"].Value : null;
    }

    // Never allow mass mentions or Markdown formatting from raw game output.
    public static string SafeLine(string line)
    {
        string normalized = line.Replace('\r', ' ').Replace('\n', ' ')
            .Replace("@", "@\u200b", StringComparison.Ordinal).Replace('`', 'ˋ');
        return normalized.Length > 1800 ? normalized[..1800] : normalized;
    }

    public static IReadOnlyList<string> Batches(IEnumerable<string> lines, int maxCharacters = 1750)
    {
        var batches = new List<string>();
        var builder = new StringBuilder();
        foreach (string raw in lines)
        {
            string line = SafeLine(raw);
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (builder.Length > 0 && builder.Length + line.Length + 1 > maxCharacters)
            {
                batches.Add(builder.ToString());
                builder.Clear();
            }
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(line.Length > maxCharacters ? line[..maxCharacters] : line);
        }
        if (builder.Length > 0) batches.Add(builder.ToString());
        return batches;
    }
}

