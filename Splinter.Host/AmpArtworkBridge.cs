using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace Splinter.Host;

// AMP 3 beta compatibility boundary. Read only public presentation metadata;
// never try to infer a game's Steam store ID from a dedicated-server update ID.
public sealed class AmpArtworkBridge
{
    private readonly IServiceScopeFactory _scopes;
    public AmpArtworkBridge(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task<string?> ReadSourceAsync(Guid shardId, string? shardType, CancellationToken ct)
    {
        // This official asset is always available in the observed AMP 3
        // Minecraft Web API installation, even when the deployed Shard's
        // runtime spec omits its presentation metadata.
        string? local = MinecraftIconSource(null, shardType);
        if (local is not null) return local;
        Assembly shared = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
            a => a.GetName().Name == "AmpShared")
            ?? throw new NotSupportedException("AMP shared assembly unavailable.");
        Type shardsType = shared.GetType("AmpShared.Shards.ShardService", true)!;
        MethodInfo get = shardsType.GetMethod("GetSpecAsync", [typeof(string), typeof(Guid), typeof(CancellationToken)])
            ?? throw new NotSupportedException("AMP Shard specification API changed.");
        using var scope = _scopes.CreateScope();
        object service = scope.ServiceProvider.GetRequiredService(shardsType);
        if (get.Invoke(service, ["_root", shardId, ct]) is not Task task)
            throw new NotSupportedException("AMP specification API no longer returns Task.");
        await task.WaitAsync(ct).ConfigureAwait(false);
        object? spec = task.GetType().GetProperty("Result")?.GetValue(task);
        // AMP has explicit local banners for Minecraft Java and Bedrock.
        // Prefer those assets even if the runtime spec reports another source.
        string? source = MinecraftIconSource(spec, shardType) ?? ExtractSource(spec);
        if (!string.IsNullOrWhiteSpace(source)) return source;

        // Some beta builds put the template metadata on the deployed Shard.
        MethodInfo? getShard = shardsType.GetMethod("GetAsync", [typeof(string), typeof(Guid)]);
        object? shardRecord = null;
        if (getShard?.Invoke(service, ["_root", shardId]) is Task shardTask)
        {
            await shardTask.WaitAsync(ct).ConfigureAwait(false);
            shardRecord = shardTask.GetType().GetProperty("Result")?.GetValue(shardTask);
            source = MinecraftIconSource(shardRecord, shardType) ?? ExtractSource(shardRecord);
        }
        if (string.IsNullOrWhiteSpace(source))
            BotLog.Write($"Shard {shardId:D}: AMP artwork not exposed by Shard/spec metadata; " +
                $"spec={ShapeHint(spec)}; shard={ShapeHint(shardRecord)}. " +
                "No image reference or Steam client App ID inferred.");
        return source;
    }

    internal static string? MinecraftIconSource(object? obj, string? name)
    {
        // Check AMP's game/template identifier and real Shard type, never the
        // user-configurable Splinter display title.
        string? app = ReadString(obj, "AppType", "ApplicationType", "TemplateName",
            "ApplicationName", "GameType", "AppName", "DisplayName");
        foreach (string? candidate in new[] { app, name })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string compact = new string(candidate.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (compact.Contains("minecraftbedrock", StringComparison.Ordinal)) return "amp-icon:MinecraftBedrock";
            if (compact.Contains("minecraftjava", StringComparison.Ordinal)) return "amp-icon:MinecraftJava";
        }
        return null;
    }

    // Do not blindly traverse AMP service objects or settings. Only known
    // display/template wrappers, with a bounded depth, can supply candidates.
    internal static string? ExtractSource(object? spec)
    {
        var candidates = new Queue<(object Obj, int Depth)>();
        if (spec is not null) candidates.Enqueue((spec, 0));
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        string[] imageFields = ["DisplayImageSource", "ImageSource", "ArtworkSource",
            "BannerImageSource", "BannerImageUrl", "ArtworkUrl", "ImageUrl",
            "CoverImageUrl", "ThumbnailUrl", "DisplayImageUrl", "HeroImageUrl",
            "HeroUrl", "BannerUrl"];
        string[] steamFields = ["SteamStoreAppId", "SteamClientAppId", "ClientAppId",
            "StoreAppId"];
        string[] holders = ["Meta", "Metadata", "Template", "TemplateInfo", "Specification", "Spec",
            "AppSpec", "Application", "Presentation", "Display", "Image", "App", "Game", "Details",
            "AppDefinition", "TemplateData", "AppConfig", "Configuration", "Source"];
        string? steam = null;
        while (candidates.Count > 0 && seen.Count < 40)
        {
            (object current, int depth) = candidates.Dequeue();
            if (!seen.Add(current)) continue;
            foreach (string field in imageFields)
            {
                string? value = SafeMember(current, field)?.ToString();
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            // Explicit store/app IDs can supply a Steam hero image when AMP's
            // DisplayImageSource has been lost from the resolved runtime spec.
            if (steam is null)
                foreach (string field in steamFields)
                {
                    string? id = SafeMember(current, field)?.ToString();
                    if (IsSteamAppId(id)) { steam = "steam:" + id; break; }
                }
            if (depth >= 4) continue;
            foreach (string holder in holders)
            {
                object? child = SafeMember(current, holder);
                if (child is not null && child is not string &&
                    (!child.GetType().IsValueType || child is JsonElement))
                    candidates.Enqueue((child, depth + 1));
            }
        }
        return steam;
    }

    // Diagnostic exposes property NAMES only, never game settings, URL data,
    // credentials or any value. It helps identify changes in future AMP betas.
    internal static string ShapeHint(object? value)
    {
        if (value is null) return "null";
        IEnumerable<string> names = value switch
        {
            JsonElement json when json.ValueKind == JsonValueKind.Object =>
                json.EnumerateObject().Select(p => p.Name).ToArray(),
            JsonObject obj => obj.Select(p => p.Key).ToArray(),
            IDictionary dictionary => dictionary.Keys.OfType<string>().ToArray(),
            _ => value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0).Select(p => p.Name).ToArray()
        };
        string[] interesting = names.Where(n => new[] { "App", "Template", "Image", "Banner", "Meta", "Steam", "Source", "Display" }
            .Any(term => n.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Take(15).ToArray();
        return value.GetType().Name + "[" + string.Join(",", interesting) + "]";
    }

    private static bool IsSteamAppId(string? value) =>
        value is { Length: >= 1 and <= 12 } && value.All(char.IsAsciiDigit) &&
        value.Any(c => c != '0');

    private static string? ReadString(object? obj, params string[] fields)
    {
        if (obj is null) return null;
        foreach (string field in fields)
        {
            string? value = SafeMember(obj, field)?.ToString();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static object? SafeMember(object value, string name)
    {
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return property.Value;
            return null;
        }
        if (value is JsonObject jsonObject)
        {
            foreach (KeyValuePair<string, JsonNode?> property in jsonObject)
                if (property.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return property.Value;
            return null;
        }
        if (value is IDictionary dictionary)
        {
            foreach (object key in dictionary.Keys)
                if (key is string s && s.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return dictionary[key];
            return null;
        }
        PropertyInfo? propertyInfo = value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                p.GetIndexParameters().Length == 0);
        try { return propertyInfo?.GetValue(value); }
        catch { return null; }
    }
}
