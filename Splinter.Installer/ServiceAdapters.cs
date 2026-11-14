using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace Splinter.Installer;

internal interface IServiceAdapter
{
    string Name { get; }
    bool IsRunning();
    void Stop();
    void Start();
    void RequireAdministrativeAccess();
    object CaptureConfiguration();
    string? ExistingDataRoot();
    bool HasHook(string hook);
    bool HasAnySplinterHook();
    bool WillAddDataRoot(string root);
    void Configure(string root, string hook);
    void Unconfigure(string root, string hook, bool removeDataRoot);
    void RestoreConfiguration(object previous);
    void SecureStorage(string root);
    void SecurePlugin(string path);
    void VerifyKeyAccess(string root);
}

internal static class ServiceFactory
{
    public static IServiceAdapter Create(string? name)
    {
        if (OperatingSystem.IsWindows()) return new WindowsAmpService(name ?? "AMP3");
        if (OperatingSystem.IsLinux()) return new LinuxAmpService(name ?? DiscoverLinuxService());
        throw new PlatformNotSupportedException("Splinter setup supports Windows and Linux only.");
    }

    private static string DiscoverLinuxService()
    {
        foreach (string candidate in new[] { "amp3.service", "AMP3.service", "amp3-webapi.service" })
            if (ProcessCommand.Run("systemctl", ["cat", candidate]).ExitCode == 0) return candidate;
        ProcessResult units = ProcessCommand.Run("systemctl", ["list-unit-files", "--type=service", "--no-legend", "--no-pager"]);
        var candidates = units.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(unit => unit is not null && Regex.IsMatch(unit, @"^(?:amp3|amp.*webapi)[\w@.-]*\.service$", RegexOptions.IgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (candidates.Length == 1) return candidates[0]!;
        if (!Console.IsInputRedirected)
        {
            Console.WriteLine("Unable to identify one AMP 3 Web API unit automatically.");
            if (candidates.Length > 0) Console.WriteLine("Candidates: " + string.Join(", ", candidates));
            Console.Write("AMP 3 Web API systemd unit (e.g. amp3.service): ");
            string? entered = Console.ReadLine()?.Trim();
            if (!string.IsNullOrWhiteSpace(entered))
                return entered.EndsWith(".service", StringComparison.Ordinal) ? entered : entered + ".service";
        }
        throw new InvalidOperationException("Could not uniquely identify AMP 3's Web API systemd unit. Pass --service <name>.");
    }
}

internal sealed class WindowsAmpService : IServiceAdapter
{
    public string Name { get; }
    private readonly string _registryPath;
    private readonly string _startName;

    public WindowsAmpService(string service)
    {
        if (!Regex.IsMatch(service, @"^[a-zA-Z0-9_.-]{1,100}$"))
            throw new ArgumentException("Invalid Windows service name.");
        Name = service;
        _registryPath = @"SYSTEM\CurrentControlSet\Services\" + service;
        using RegistryKey key = OpenKey(writable: false);
        string image = (string?)key.GetValue("ImagePath") ?? "";
        if (!image.Contains("amp3-host", StringComparison.OrdinalIgnoreCase) &&
            !image.Contains("webapi", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{service} does not appear to be an AMP 3 Web API service.");
        _startName = (string?)key.GetValue("ObjectName") ?? "LocalSystem";
    }

    private RegistryKey OpenKey(bool writable) => Registry.LocalMachine.OpenSubKey(_registryPath, writable)
        ?? throw new InvalidOperationException($"AMP 3 Windows service '{Name}' was not found.");

    public bool IsRunning()
    {
        var query = ProcessCommand.Required("sc.exe", "query", Name).Output;
        if (Regex.IsMatch(query, @"STATE\s*:\s*\d+\s+RUNNING", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(query, @"STATE\s*:\s*\d+\s+STOPPED", RegexOptions.IgnoreCase)) return false;
        throw new InvalidOperationException("AMP service is transitioning or has an unsupported state. Try again after it settles.");
    }

    public void Stop()
    {
        if (!IsRunning()) return;
        ProcessCommand.Required("sc.exe", "stop", Name);
        WaitForState(expectedRunning: false);
    }

    public void Start()
    {
        if (IsRunning()) return;
        ProcessCommand.Required("sc.exe", "start", Name);
        WaitForState(expectedRunning: true);
    }

    private void WaitForState(bool expectedRunning)
    {
        for (int i = 0; i < 45; i++)
        {
            Thread.Sleep(1000);
            try { if (IsRunning() == expectedRunning) return; }
            catch (InvalidOperationException) { /* START_PENDING/STOP_PENDING */ }
        }
        throw new TimeoutException($"AMP service did not reach {(expectedRunning ? "Running" : "Stopped")} within 45 seconds.");
    }

    public void RequireAdministrativeAccess()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Run splinter-setup.exe from an elevated Administrator terminal.");
    }

    public object CaptureConfiguration()
    {
        using RegistryKey key = OpenKey(writable: false);
        return (key.GetValue("Environment") as string[])?.ToArray() ?? Array.Empty<string>();
    }

    private string[] CurrentEntries() => (string[])CaptureConfiguration();

    private static string? MachineValue(string key) =>
        Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Machine);

    public string? ExistingDataRoot()
    {
        var vars = InstallEnvironment.ParseServiceAssignments(CurrentEntries());
        return vars.GetValueOrDefault(InstallEnvironment.DataVariable) ?? MachineValue(InstallEnvironment.DataVariable);
    }

    private string? EffectiveHooks()
    {
        var vars = InstallEnvironment.ParseServiceAssignments(CurrentEntries());
        return vars.GetValueOrDefault(InstallEnvironment.HookVariable) ?? MachineValue(InstallEnvironment.HookVariable);
    }

    public bool HasHook(string hook) => InstallEnvironment.SplitHooks(EffectiveHooks(), ';')
        .Any(h => InstallEnvironment.PathEquals(h, hook));

    public bool HasAnySplinterHook() => InstallEnvironment.SplitHooks(EffectiveHooks(), ';')
        .Any(h => h.EndsWith("Splinter.Host.dll", StringComparison.OrdinalIgnoreCase));

    public bool WillAddDataRoot(string root)
    {
        var vars = InstallEnvironment.ParseServiceAssignments(CurrentEntries());
        string? global = MachineValue(InstallEnvironment.DataVariable);
        if (global is not null && !InstallEnvironment.PathEquals(global, root))
            throw new InvalidOperationException("Machine-wide AMP3_ROOT does not match the selected data directory.");
        if (vars.TryGetValue(InstallEnvironment.DataVariable, out string? local))
        {
            if (!InstallEnvironment.PathEquals(local, root))
                throw new InvalidOperationException("AMP3_ROOT in the AMP service does not match the selected data directory.");
            return false;
        }
        return true;
    }

    public void Configure(string root, string hook)
    {
        string[] original = CurrentEntries();
        var vars = InstallEnvironment.ParseServiceAssignments(original);
        if (!vars.ContainsKey(InstallEnvironment.HookVariable) &&
            MachineValue(InstallEnvironment.HookVariable) is string machineHooks && machineHooks.Length > 0)
            original = [.. original, InstallEnvironment.HookVariable + "=" + machineHooks];
        WriteEntries(InstallEnvironment.ReplaceWindowsVariables(original, hook, root,
            remove: false, removeDataRoot: false));
    }

    public void Unconfigure(string root, string hook, bool removeDataRoot) => WriteEntries(
        InstallEnvironment.ReplaceWindowsVariables(CurrentEntries(), hook, root, remove: true, removeDataRoot));

    public void RestoreConfiguration(object previous) => WriteEntries((string[])previous);

    private void WriteEntries(string[] entries)
    {
        using RegistryKey key = OpenKey(writable: true);
        if (entries.Length == 0) key.DeleteValue("Environment", throwOnMissingValue: false);
        else key.SetValue("Environment", entries, RegistryValueKind.MultiString);
    }

    public void SecureStorage(string root)
    {
        string splinter = Path.Combine(root, "splinter");
        string secrets = Path.Combine(splinter, "secrets");
        Directory.CreateDirectory(splinter);
        Directory.CreateDirectory(secrets);
        var allowed = new[]
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            ServiceSid()
        }.DistinctBy(s => s.Value).ToArray();
        SecureDirectory(splinter, allowed);
        SecureDirectory(secrets, allowed);
        // Existing AMP-protected token records must not keep liberal explicit ACLs.
        foreach (string file in Directory.EnumerateFiles(secrets, "*.amp", SearchOption.TopDirectoryOnly))
            SecureFile(file, allowed);
    }

    private SecurityIdentifier ServiceSid()
    {
        string account = _startName.Trim();
        if (account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) ||
            account.Equals("NT AUTHORITY\\SYSTEM", StringComparison.OrdinalIgnoreCase))
            return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (account.Equals("NT AUTHORITY\\NetworkService", StringComparison.OrdinalIgnoreCase) ||
            account.Equals("NetworkService", StringComparison.OrdinalIgnoreCase))
            return new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
        if (account.Equals("NT AUTHORITY\\LocalService", StringComparison.OrdinalIgnoreCase) ||
            account.Equals("LocalService", StringComparison.OrdinalIgnoreCase))
            return new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null);
        return (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
    }

    private static void SecureDirectory(string path, SecurityIdentifier[] allowed)
    {
        if (new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Refusing to secure a symlink/junction in Splinter storage.");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (SecurityIdentifier sid in allowed)
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        System.IO.FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), acl);
    }

    private static void SecureFile(string path, SecurityIdentifier[] allowed)
    {
        if (new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Refusing to secure a symlink in Splinter credentials.");
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (SecurityIdentifier sid in allowed)
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                AccessControlType.Allow));
        System.IO.FileSystemAclExtensions.SetAccessControl(new FileInfo(path), acl);
    }

    public void SecurePlugin(string path)
    {
        SecurityIdentifier[] allowed = new[]
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            ServiceSid()
        }.DistinctBy(s => s.Value).ToArray();
        SecureDirectory(path, allowed);
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            SecureFile(file, allowed);
    }

    public void VerifyKeyAccess(string root)
    {
        string path = Path.Combine(root, "secret-key");
        if (File.Exists(path))
        {
            if (new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("AMP's secret-key is a symbolic link.");
            Console.WriteLine("AMP-managed secret key: present (not read or modified).");
        }
        else Console.WriteLine("AMP-managed secret key: not yet created by AMP.");
    }
}

internal sealed class LinuxAmpService : IServiceAdapter
{
    public string Name { get; }
    private readonly string _dropInPath;

    public LinuxAmpService(string service)
    {
        if (!Regex.IsMatch(service, @"^[a-zA-Z0-9_.@-]{1,120}\.service$"))
            throw new ArgumentException("Invalid systemd service name. Specify a .service unit.");
        Name = service;
        if (ProcessCommand.Run("systemctl", ["cat", Name]).ExitCode != 0)
            throw new InvalidOperationException($"AMP systemd unit '{Name}' was not found.");
        _dropInPath = Path.Combine("/etc/systemd/system", Name + ".d", "50-splinter.conf");
        if (File.Exists(_dropInPath)) ValidateExistingDropIn(File.ReadAllText(_dropInPath));
    }

    private static void ValidateExistingDropIn(string data)
    {
        if (data.Contains("# Managed by Splinter setup.", StringComparison.Ordinal)) return;
        // Adopt the simple drop-in created by the previous Install-Linux.sh.
        string[] lines = data.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.All(s => s == "[Service]" || s.StartsWith("Environment=\"AMP3_ROOT=", StringComparison.Ordinal) ||
            s.StartsWith("Environment=\"DOTNET_STARTUP_HOOKS=", StringComparison.Ordinal)) &&
            lines.Any(s => s.Contains("Splinter.Host.dll", StringComparison.Ordinal))) return;
        throw new InvalidOperationException("The Splinter drop-in contains unrecognised configuration. Inspect it manually before installation.");
    }

    public bool IsRunning() => ProcessCommand.Run("systemctl", ["is-active", "--quiet", Name]).ExitCode == 0;
    public void Stop() => ProcessCommand.Required("systemctl", "stop", Name);
    public void Start() => ProcessCommand.Required("systemctl", "start", Name);
    public void RequireAdministrativeAccess()
    {
        if (Environment.UserName != "root" && ProcessCommand.Run("id", ["-u"]).Output.Trim() != "0")
            throw new UnauthorizedAccessException("Run setup with sudo/root: sudo ./splinter-setup install");
    }
    public object CaptureConfiguration() => File.Exists(_dropInPath) ? File.ReadAllText(_dropInPath) : new MissingDropIn();
    private sealed record MissingDropIn;

    private IReadOnlyDictionary<string, string> EnvironmentValues() =>
        InstallEnvironment.ParseSystemdEnvironment(ProcessCommand.Required("systemctl", "show", "-p", "Environment", "--value", Name).Output);

    private Dictionary<string, string> EnvironmentFileValues()
    {
        string paths = ProcessCommand.Required("systemctl", "show", "-p", "EnvironmentFiles", "--value", Name).Output.Trim();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (paths.Length == 0) return values;
        // Only inspect variable names relevant to startup hooks; do not print
        // contents of EnvironmentFiles (which can contain AMP credentials).
        var matches = Regex.Matches(paths, @"(?<file>/[^\s()]+)(?:\s*\(ignore_errors=(?:yes|no)\))?");
        if (matches.Count == 0) throw new InvalidOperationException("Cannot safely inspect AMP's systemd EnvironmentFiles.");
        string unparsed = paths;
        foreach (Match match in matches.Cast<Match>().Reverse())
            unparsed = unparsed.Remove(match.Index, match.Length);
        if (!string.IsNullOrWhiteSpace(unparsed))
            throw new InvalidOperationException("AMP uses an EnvironmentFile path format this installer cannot safely inspect. Merge startup hooks manually.");
        foreach (Match match in matches)
        {
            string path = match.Groups["file"].Value;
            if (!File.Exists(path)) continue;
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string name = line[..i].Trim();
                if (name is not (InstallEnvironment.HookVariable or InstallEnvironment.DataVariable)) continue;
                values[name] = line[(i + 1)..].Trim().Trim('"', '\'');
            }
        }
        return values;
    }

    public string? ExistingDataRoot()
    {
        var fileEnv = EnvironmentFileValues();
        if (fileEnv.TryGetValue(InstallEnvironment.DataVariable, out string? fromFile)) return fromFile;
        return EnvironmentValues().GetValueOrDefault(InstallEnvironment.DataVariable);
    }

    public bool HasHook(string hook)
    {
        var env = EnvironmentValues();
        return env.TryGetValue(InstallEnvironment.HookVariable, out string? hooks) &&
            InstallEnvironment.SplitHooks(hooks, ':').Any(p => InstallEnvironment.PathEquals(p, hook));
    }

    public bool HasAnySplinterHook() => EnvironmentValues()
        .TryGetValue(InstallEnvironment.HookVariable, out string? hooks) &&
        InstallEnvironment.SplitHooks(hooks, ':').Any(p => p.EndsWith("Splinter.Host.dll", StringComparison.Ordinal));

    public bool WillAddDataRoot(string root)
    {
        string? current = ExistingDataRoot();
        if (current is not null && !InstallEnvironment.PathEquals(current, root))
            throw new InvalidOperationException("AMP3_ROOT in systemd does not match the selected data directory.");
        return current is null;
    }

    public void Configure(string root, string hook)
    {
        var fileEnv = EnvironmentFileValues();
        if (fileEnv.ContainsKey(InstallEnvironment.HookVariable))
            throw new InvalidOperationException("AMP sets DOTNET_STARTUP_HOOKS in an EnvironmentFile. Merge the hook manually; setup will not overwrite it.");
        WillAddDataRoot(root);
        var env = EnvironmentValues();
        env.TryGetValue(InstallEnvironment.HookVariable, out string? existing);
        string hooks = InstallEnvironment.JoinHooks(existing, hook, ':');
        string content = InstallEnvironment.SystemdDropIn(root, hooks);
        Directory.CreateDirectory(Path.GetDirectoryName(_dropInPath)!);
        string temp = _dropInPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content);
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.Move(temp, _dropInPath, overwrite: true);
            ProcessCommand.Required("systemctl", "daemon-reload");
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public void Unconfigure(string root, string hook, bool removeDataRoot)
    {
        if (File.Exists(_dropInPath))
        {
            ValidateExistingDropIn(File.ReadAllText(_dropInPath));
            File.Delete(_dropInPath);
            ProcessCommand.Required("systemctl", "daemon-reload");
        }
    }

    public void RestoreConfiguration(object previous)
    {
        if (previous is MissingDropIn) { if (File.Exists(_dropInPath)) File.Delete(_dropInPath); }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dropInPath)!);
            File.WriteAllText(_dropInPath, (string)previous);
        }
        ProcessCommand.Required("systemctl", "daemon-reload");
    }

    private (string User, string Group) Owner()
    {
        string user = ProcessCommand.Required("systemctl", "show", "-p", "User", "--value", Name).Output.Trim();
        if (string.IsNullOrWhiteSpace(user)) user = "root";
        string group = ProcessCommand.Required("systemctl", "show", "-p", "Group", "--value", Name).Output.Trim();
        if (string.IsNullOrWhiteSpace(group)) group = ProcessCommand.Required("id", "-gn", user).Output.Trim();
        if (string.IsNullOrWhiteSpace(group)) throw new InvalidOperationException("Cannot resolve AMP service group.");
        return (user, group);
    }

    public void SecureStorage(string root)
    {
        string splinter = Path.Combine(root, "splinter");
        string secrets = Path.Combine(splinter, "secrets");
        Directory.CreateDirectory(splinter);
        Directory.CreateDirectory(secrets);
        foreach (string path in new[] { splinter, secrets })
        {
            if (new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Refusing to follow a symlink in Splinter storage.");
        }
        var owner = Owner();
        foreach (string path in new[] { splinter, secrets })
        {
            ProcessCommand.Required("chown", owner.User + ":" + owner.Group, path);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void SecurePlugin(string path)
    {
        var owner = Owner();
        ProcessCommand.Required("chown", "-R", owner.User + ":" + owner.Group, path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
    }

    public void VerifyKeyAccess(string root)
    {
        string key = Path.Combine(root, "secret-key");
        if (!File.Exists(key)) { Console.WriteLine("AMP-managed secret key: not yet created by AMP."); return; }
        if (new FileInfo(key).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("AMP's secret-key is a symbolic link.");
        UnixFileMode mode = File.GetUnixFileMode(key);
        if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                     UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new InvalidOperationException("AMP secret-key permissions are too broad. Repair with AMP's tooling, not Splinter.");
        string user = Owner().User;
        if (ProcessCommand.Run("runuser", ["-u", user, "--", "test", "-r", key]).ExitCode != 0)
            throw new InvalidOperationException("AMP's systemd service account cannot read its native encryption key.");
        Console.WriteLine("AMP-managed secret key: permissions and service-account access verified (contents not read).");
    }
}
