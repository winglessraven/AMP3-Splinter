using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Splinter.Installer;

internal sealed class InstallationState
{
    public int SchemaVersion { get; set; } = 1;
    public string Service { get; set; } = "";
    public string Hook { get; set; } = "";
    public bool AddedDataRoot { get; set; }
}

internal sealed class Installer
{
    private readonly Options _options;
    private readonly IServiceAdapter _service;
    private readonly string _root;
    private readonly string _splinter;
    private readonly string _plugin;
    private readonly string _hook;
    private readonly string _stateFile;

    public Installer(Options options)
    {
        _options = options;
        _service = ServiceFactory.Create(options.Service);
        _root = FindDataRoot(options.DataRoot, _service.ExistingDataRoot(), _service.Name);
        _splinter = Path.Combine(_root, "splinter");
        _plugin = Path.Combine(_splinter, "plugin");
        _hook = Path.Combine(_plugin, "Splinter.Host.dll");
        _stateFile = Path.Combine(_splinter, "installer-state.json");
        InstallEnvironment.GuardHookPath(_hook, Path.PathSeparator);
        ValidateAmpRoot();
    }

    public void Execute()
    {
        Console.WriteLine($"Splinter setup — {_options.Command}");
        Console.WriteLine($"AMP data: {_root}");
        Console.WriteLine($"AMP service: {_service.Name}");
        if (_options.Command == "doctor") { Doctor(); return; }
        _service.RequireAdministrativeAccess();
        _service.VerifyKeyAccess(_root);
        if (_options.Command == "uninstall") { Uninstall(); return; }
        InstallOrUpdate();
    }

    private static string FindDataRoot(string? requested, string? configured, string serviceName)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return Path.GetFullPath(requested);
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        string? env = Environment.GetEnvironmentVariable("AMP3_ROOT");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);

        // On Linux the AMP host executable is the most reliable clue. Its
        // installation directory contains the persistent `data/` directory.
        // Derive this from systemd's metadata; never execute or source the unit.
        if (OperatingSystem.IsLinux())
        {
            ProcessResult info = ProcessCommand.Run("systemctl", ["show", "-p", "ExecStart", "--value", serviceName]);
            if (info.ExitCode == 0)
            {
                string[] serviceRoots = LinuxDataDiscovery.FromExecStart(info.Output)
                    .Where(IsAmpDataRoot).Distinct(StringComparer.Ordinal).ToArray();
                if (serviceRoots.Length == 1) return serviceRoots[0];
                if (serviceRoots.Length > 1)
                    throw new InvalidOperationException("AMP service references multiple valid data directories. Pass --data <path>.");
            }
        }

        string[] candidates = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CubeCoders", "AMP3", "data")]
            : ["/opt/cubecoders/amp3/data", "/var/lib/amp3/data", "/var/lib/amp3", "/opt/amp3/data"];
        string[] valid = candidates.Where(IsAmpDataRoot).Distinct().ToArray();
        if (valid.Length == 1) return valid[0];
        if (!Console.IsInputRedirected)
        {
            Console.WriteLine("Unable to identify one AMP 3 data directory automatically.");
            if (valid.Length > 0) Console.WriteLine("Candidates: " + string.Join(", ", valid));
            Console.Write("AMP 3 persistent data directory: ");
            string? entered = Console.ReadLine()?.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(entered)) return Path.GetFullPath(entered);
        }
        throw new InvalidOperationException("Could not locate the AMP 3 data directory uniquely. Pass --data <path>.");
    }

    internal static bool IsAmpDataRoot(string root)
    {
        try
        {
            string versions = Path.Combine(root, "versions");
            return Directory.Exists(versions) && Directory.EnumerateDirectories(versions)
                .Any(dir => Path.GetFileName(dir).StartsWith("3.", StringComparison.OrdinalIgnoreCase) &&
                    (File.Exists(Path.Combine(dir, "webapi", "webapi")) ||
                     File.Exists(Path.Combine(dir, "webapi", "webapi.exe"))));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void ValidateAmpRoot()
    {
        if (!IsAmpDataRoot(_root))
            throw new InvalidOperationException("The selected data root does not contain an AMP 3 versions/<version>/webapi executable.");
        if (new DirectoryInfo(_root).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("The AMP data root is a symbolic link/junction. Pass the actual directory.");
        foreach (string path in new[] { _splinter, _plugin, Path.Combine(_splinter, "secrets") })
        {
            if ((File.Exists(path) || Directory.Exists(path)) &&
                File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException($"Splinter path is a symbolic link/junction: {path}");
        }
    }

    private InstallationState? State()
    {
        if (!File.Exists(_stateFile)) return null;
        try
        {
            InstallationState? state = JsonSerializer.Deserialize<InstallationState>(File.ReadAllText(_stateFile));
            if (state?.SchemaVersion != 1 || state.Hook.Length == 0 || state.Service.Length == 0)
                throw new InvalidDataException("Invalid installer state.");
            return state;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new InvalidOperationException("Cannot read the existing installation state. Repair it before making changes.", ex);
        }
    }

    private void Doctor()
    {
        Console.WriteLine($"Service running: {_service.IsRunning()}");
        Console.WriteLine($"Plugin DLL: {(File.Exists(_hook) ? "present" : "missing")}");
        Console.WriteLine($"Startup hook: {(_service.HasHook(_hook) ? "configured" : "not configured")}");
        Console.WriteLine($"Native settings flag: {(File.Exists(Path.Combine(_splinter, "native-settings-preview.enabled")) ? "enabled" : "missing")}");
        string? data = _service.ExistingDataRoot();
        Console.WriteLine($"AMP3_ROOT matches: {(data is null ? "not configured" : InstallEnvironment.PathEquals(data, _root) ? "yes" : "NO")}");
        _service.VerifyKeyAccess(_root);
        Console.WriteLine("Doctor is read-only. It cannot confirm the AMP startup hook loaded or that Discord connected.");
    }

    private void InstallOrUpdate()
    {
        string payload = Path.GetFullPath(_options.Payload ?? Path.Combine(AppContext.BaseDirectory, "payload"));
        ValidatePayload(payload);
        InstallationState? previousState = State();
        if (previousState is not null && (!previousState.Service.Equals(_service.Name, StringComparison.OrdinalIgnoreCase) ||
            !InstallEnvironment.PathEquals(previousState.Hook, _hook)))
            throw new InvalidOperationException("Existing installation state belongs to another service or plugin path.");
        bool addedRoot = previousState?.AddedDataRoot ?? _service.WillAddDataRoot(_root);
        object oldConfiguration = _service.CaptureConfiguration();
        bool wasRunning = _service.IsRunning();
        string pending = Path.Combine(_splinter, "plugin.pending-" + Guid.NewGuid().ToString("N"));
        string oldPlugin = Path.Combine(_splinter, "plugin.previous-" + Guid.NewGuid().ToString("N"));
        string flag = Path.Combine(_splinter, "native-settings-preview.enabled");
        bool oldPluginMoved = false, newPluginPlaced = false, flagCreated = false, configured = false;
        bool oldStateExisted = File.Exists(_stateFile);
        string? oldStateData = oldStateExisted ? File.ReadAllText(_stateFile) : null;
        try
        {
            if (wasRunning) { Console.WriteLine("Stopping AMP Web API service..."); _service.Stop(); }
            EnsureNoRunningWebApi();
            _service.SecureStorage(_root);
            Directory.CreateDirectory(pending);
            CopyPayload(payload, pending);
            _service.SecurePlugin(pending);
            if (Directory.Exists(_plugin)) { Directory.Move(_plugin, oldPlugin); oldPluginMoved = true; }
            Directory.Move(pending, _plugin);
            newPluginPlaced = true;
            if (!File.Exists(flag)) { File.WriteAllText(flag, string.Empty); flagCreated = true; }
            configured = true; // Restore even if writing/reloading service config fails midway.
            _service.Configure(_root, _hook);
            var state = new InstallationState { Service = _service.Name, Hook = _hook, AddedDataRoot = addedRoot };
            AtomicText(_stateFile, JsonSerializer.Serialize(state));
            if (!_options.NoStart)
            {
                Console.WriteLine("Starting AMP Web API service...");
                _service.Start();
            }
            if (oldPluginMoved) BestEffortCleanup(oldPlugin);
            Console.WriteLine("Splinter installed. Persistent Shard configuration and AMP-protected tokens were preserved.");
            Console.WriteLine("Open Shard → App Settings → Splinter to configure or enable Discord bots.");
            if (_options.NoStart) Console.WriteLine("AMP remains stopped (--no-start). Start it when ready.");
            Console.WriteLine("Run 'splinter-setup doctor' for installation checks.");
        }
        catch
        {
            Console.Error.WriteLine("Install/update failed. Attempting to restore the previous plugin and service configuration...");
            try
            {
                if (_service.IsRunning()) _service.Stop();
                if (configured) _service.RestoreConfiguration(oldConfiguration);
                if (newPluginPlaced && Directory.Exists(_plugin)) Directory.Delete(_plugin, recursive: true);
                if (oldPluginMoved && Directory.Exists(oldPlugin)) Directory.Move(oldPlugin, _plugin);
                if (flagCreated && File.Exists(flag)) File.Delete(flag);
                if (oldStateExisted) AtomicText(_stateFile, oldStateData!);
                else if (File.Exists(_stateFile)) File.Delete(_stateFile);
                if (wasRunning) _service.Start();
                Console.Error.WriteLine("Previous installation restored.");
            }
            catch (Exception rollbackError)
            {
                Console.Error.WriteLine("ROLLBACK NEEDS ATTENTION: " + rollbackError.Message);
                Console.Error.WriteLine("Check the AMP service and splinter/plugin directory before attempting another install.");
            }
            throw;
        }
        finally
        {
            BestEffortCleanup(pending);
        }
    }

    private void Uninstall()
    {
        InstallationState? state = State();
        if (state is not null && (!state.Service.Equals(_service.Name, StringComparison.OrdinalIgnoreCase) ||
            !InstallEnvironment.PathEquals(state.Hook, _hook)))
            throw new InvalidOperationException("Installer state differs from the requested AMP installation.");
        object previous = _service.CaptureConfiguration();
        bool wasRunning = _service.IsRunning();
        string flag = Path.Combine(_splinter, "native-settings-preview.enabled");
        string previousPlugin = Path.Combine(_splinter, "plugin.uninstall-" + Guid.NewGuid().ToString("N"));
        bool moved = false, configChanged = false;
        try
        {
            if (wasRunning) _service.Stop();
            EnsureNoRunningWebApi();
            if (Directory.Exists(_plugin)) { Directory.Move(_plugin, previousPlugin); moved = true; }
            configChanged = true; // Restore even if removing/reloading service config fails midway.
            _service.Unconfigure(_root, _hook, state?.AddedDataRoot == true);
            if (!_options.NoStart && wasRunning) _service.Start();
            if (moved) BestEffortCleanup(previousPlugin);
            try
            {
                if (File.Exists(flag)) File.Delete(flag);
                if (File.Exists(_stateFile)) File.Delete(_stateFile);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine("Warning: Splinter was unhooked, but optional local cleanup failed: " + ex.Message);
            }
            Console.WriteLine("Splinter removed from the AMP service. Shard settings, encrypted tokens and panels were retained.");
        }
        catch
        {
            Console.Error.WriteLine("Uninstall failed; restoring the previous service configuration and plugin...");
            try
            {
                if (_service.IsRunning()) _service.Stop();
                if (configChanged) _service.RestoreConfiguration(previous);
                if (moved && Directory.Exists(previousPlugin)) Directory.Move(previousPlugin, _plugin);
                if (wasRunning) _service.Start();
            }
            catch (Exception ex) { Console.Error.WriteLine("ROLLBACK NEEDS ATTENTION: " + ex.Message); }
            throw;
        }
    }

    private static void ValidatePayload(string payload)
    {
        if (!Directory.Exists(payload))
            throw new InvalidOperationException($"Plugin payload not found: {payload}. Use the release archive, or specify --payload.");
        string? nativeSkia = OperatingSystem.IsWindows() ? "libSkiaSharp.dll" :
            OperatingSystem.IsLinux() ? "libSkiaSharp.so" : null;
        string[] requiredFiles = nativeSkia is null
            ? ["Splinter.Host.dll", "Discord.Net.WebSocket.dll", "Splinter.Host.deps.json", "SkiaSharp.dll"]
            : ["Splinter.Host.dll", "Discord.Net.WebSocket.dll", "Splinter.Host.deps.json", "SkiaSharp.dll", nativeSkia];
        foreach (string required in requiredFiles)
            if (!File.Exists(Path.Combine(payload, required)))
                throw new InvalidOperationException($"Plugin payload is incomplete (missing {required}). Publish Splinter.Host first.");
        foreach (string path in Directory.EnumerateFileSystemEntries(payload))
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Plugin payload contains a symlink/junction; refusing to stage it.");
    }

    private static void CopyPayload(string source, string target)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                continue;
            File.Copy(file, Path.Combine(target, name), overwrite: false);
        }
        // Splinter.Host is a class library; deployment should contain only the
        // published flat runtime dependencies, not build intermediates or directories.
        if (Directory.EnumerateDirectories(source).Any())
            throw new InvalidOperationException("Plugin payload contains unexpected directories. Use a clean dotnet publish output.");
    }

    private static void AtomicText(string path, string content)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void BestEffortCleanup(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (IOException ex) { Console.Error.WriteLine("Warning: stale plugin directory needs manual cleanup: " + ex.Message); }
        catch (UnauthorizedAccessException ex) { Console.Error.WriteLine("Warning: stale plugin directory needs manual cleanup: " + ex.Message); }
    }

    private static void EnsureNoRunningWebApi()
    {
        if (!OperatingSystem.IsWindows()) return;
        using Process current = Process.GetCurrentProcess();
        Process[] running = Process.GetProcessesByName("webapi");
        try
        {
            if (running.Any(p => p.Id != current.Id))
                throw new InvalidOperationException("A webapi.exe process is still running. Stop AMP fully before replacing Splinter binaries.");
        }
        finally { foreach (Process process in running) process.Dispose(); }
    }
}

// Interpret systemd's printed ExecStart metadata, not a shell command line.
// Only the verified AMP 3 host executable may establish the installation base.
internal static class LinuxDataDiscovery
{
    internal static IReadOnlyList<string> FromExecStart(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];
        var match = Regex.Match(output, @"(?:^|[\s{;])path=(?<exe>/[^;\r\n]+?)(?=\s*[;}]|$)");
        if (!match.Success) return [];
        string executable = match.Groups["exe"].Value.Trim();
        if (!Path.GetFileName(executable).Equals("amp3-host", StringComparison.OrdinalIgnoreCase))
            return [];
        string? directory = Path.GetDirectoryName(executable);
        if (string.IsNullOrWhiteSpace(directory)) return [];
        return [Path.Combine(directory, "data")];
    }
}
