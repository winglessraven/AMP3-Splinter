using System.Text;

namespace Splinter.Installer;

// Pure helpers are shared by the Windows registry and systemd adapters.
internal static class InstallEnvironment
{
    internal const string HookVariable = "DOTNET_STARTUP_HOOKS";
    internal const string DataVariable = "AMP3_ROOT";

    public static string[] SplitHooks(string? value, char separator)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        return value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string JoinHooks(string? existing, string own, char separator)
    {
        GuardHookPath(own, separator);
        foreach (string hook in SplitHooks(existing, separator))
        {
            string file = Path.GetFileName(hook);
            if ((file.Equals("Splinter.Host.dll", StringComparison.OrdinalIgnoreCase) && !PathEquals(hook, own)) ||
                file.Equals("Amp3Discord.Host.dll", StringComparison.OrdinalIgnoreCase) ||
                file.Equals("SpireLink.Host.dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("An older or differently located Discord startup hook is already active. Remove it before installing Splinter.");
        }
        string[] hooks = SplitHooks(existing, separator)
            .Where(h => !PathEquals(h, own)).Append(own)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return string.Join(separator, hooks);
    }

    public static string RemoveHook(string? existing, string own, char separator) =>
        string.Join(separator, SplitHooks(existing, separator).Where(h => !PathEquals(h, own)));

    public static bool PathEquals(string a, string b) => string.Equals(
        a.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        b.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static void GuardHookPath(string path, char separator)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains(separator) || path.Contains('\r') ||
            path.Contains('\n') || path.Contains('\0') || path.Contains('"'))
            throw new InvalidOperationException("AMP data path cannot be represented safely in DOTNET_STARTUP_HOOKS.");
    }

    public static Dictionary<string, string> ParseServiceAssignments(IEnumerable<string> entries)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in entries)
        {
            int split = entry.IndexOf('=');
            if (split <= 0) continue;
            string key = entry[..split];
            if (result.ContainsKey(key))
                throw new InvalidOperationException($"Duplicate service environment variable: {key}.");
            result.Add(key, entry[(split + 1)..]);
        }
        return result;
    }

    public static string[] ReplaceWindowsVariables(string[] existing, string hook, string root,
        bool remove, bool removeDataRoot)
    {
        var vars = ParseServiceAssignments(existing);
        vars.TryGetValue(HookVariable, out string? oldHooks);
        string newHooks = remove ? RemoveHook(oldHooks, hook, ';') : JoinHooks(oldHooks, hook, ';');
        if (newHooks.Length == 0) vars.Remove(HookVariable);
        else vars[HookVariable] = newHooks;
        if (remove)
        {
            if (removeDataRoot && vars.TryGetValue(DataVariable, out string? currentRoot) && PathEquals(currentRoot, root))
                vars.Remove(DataVariable);
        }
        else if (vars.TryGetValue(DataVariable, out string? currentRoot))
        {
            if (!PathEquals(currentRoot, root))
                throw new InvalidOperationException("AMP3_ROOT already points to a different directory in the AMP service.");
        }
        else vars[DataVariable] = root;
        return vars.Select(v => v.Key + "=" + v.Value).ToArray();
    }

    public static IReadOnlyDictionary<string, string> ParseSystemdEnvironment(string input)
    {
        // systemctl show -p Environment --value emits shell-like space-separated
        // assignments. Do not use a shell or evaluate any service environment data.
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string token in Tokenize(input))
        {
            int i = token.IndexOf('=');
            if (i <= 0) continue;
            string key = token[..i];
            if (values.ContainsKey(key)) throw new InvalidOperationException($"Duplicate systemd environment variable: {key}.");
            values[key] = token[(i + 1)..];
        }
        return values;
    }

    private static IEnumerable<string> Tokenize(string input)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, escaped = false;
        foreach (char c in input)
        {
            if (escaped) { current.Append(c); escaped = false; continue; }
            if (c == '\\') { escaped = true; continue; }
            if (c == '"') { quoted = !quoted; continue; }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (escaped || quoted) throw new InvalidOperationException("Cannot parse the AMP systemd environment safely.");
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    public static string SystemdValue(string text)
    {
        if (text.Contains('\n') || text.Contains('\r') || text.Contains('\0'))
            throw new InvalidOperationException("Invalid newline in systemd environment value.");
        // systemd expands % specifiers even inside quoted Environment= assignments.
        return text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal);
    }

    public static string SystemdDropIn(string root, string hooks) =>
        "# Managed by Splinter setup. Do not put credentials here.\n" +
        "[Service]\n" +
        $"Environment=\"{DataVariable}={SystemdValue(root)}\"\n" +
        $"Environment=\"{HookVariable}={SystemdValue(hooks)}\"\n";
}
