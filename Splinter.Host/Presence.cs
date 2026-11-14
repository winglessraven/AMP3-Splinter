using System.Text.Json;
using System.Text.RegularExpressions;

namespace Splinter.Host;

// The AMP 3 Web API is persistent and hosts many Shards. Unlike AMP 2, there is
// no single 'instance folder' for the plugin; each Shard gets its own data file.
public static class PresenceVariables
{
    public const int MaxFileBytes = 8192;
    public static IReadOnlyDictionary<string, string> Read(Guid shardId)
    {
        string path = ConfigStore.VariablesPath(shardId);
        if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxFileBytes)
            throw new InvalidDataException("BotVariables.json exceeds the 8 KiB safety limit.");
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("BotVariables.json must contain one JSON object.");

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (values.Count >= 64)
                throw new InvalidDataException("BotVariables.json supports at most 64 variables.");
            string key = property.Name;
            if (!PresenceRenderer.ValidVariableName(key) || PresenceRenderer.IsBuiltIn(key))
                throw new InvalidDataException("BotVariables.json contains an invalid or reserved variable name.");
            if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or
                                                 JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("BotVariables.json values must be strings, numbers or booleans.");
            string value = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? ""
                : property.Value.ToString();
            if (value.Length > 120 || value.Any(char.IsControl))
                throw new InvalidDataException("BotVariables.json contains an oversized or multiline value.");
            if (!values.TryAdd(key, value))
                throw new InvalidDataException("BotVariables.json contains duplicate variable names.");
        }
        return values;
    }
}

// Pure formatting function: no Discord calls and no AMP reflection.
public static partial class PresenceRenderer
{
    private static readonly HashSet<string> BuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        "ServerName", "State", "OnlinePlayers", "MaximumPlayers"
    };

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex VariablePattern();

    public static bool ValidVariableName(string name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 64 &&
        (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
        name.Skip(1).All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    public static bool IsBuiltIn(string name) => BuiltIns.Contains(name);

    public static string Render(ShardBotConfig config, ShardSnapshot snapshot,
        IReadOnlyDictionary<string, string>? additionalVariables = null)
    {
        string state = snapshot.IsRunning ? "Online" : snapshot.Status;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (additionalVariables is not null)
            foreach ((string key, string value) in additionalVariables)
                if (ValidVariableName(key) && !IsBuiltIn(key)) values[key] = value;

        values["ServerName"] = snapshot.Name;
        values["State"] = state;
        values["OnlinePlayers"] = snapshot.Players?.ToString() ?? "?";
        values["MaximumPlayers"] = snapshot.MaxPlayers?.ToString() ?? "?";

        string template = string.IsNullOrWhiteSpace(config.PresenceTemplate)
            ? "{ServerName}: {State}" : config.PresenceTemplate;
        string result = VariablePattern().Replace(template, match =>
            values.TryGetValue(match.Groups[1].Value, out string? value) ? value : match.Value);
        // Discord's activity fields are limited; don't split a UTF-16 surrogate pair.
        if (result.Length > 120)
        {
            result = result[..120];
            if (char.IsHighSurrogate(result[^1])) result = result[..^1];
        }
        return result;
    }
}
