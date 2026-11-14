using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

[assembly: HostingStartup(typeof(Splinter.Host.DiscordHostingStartup))]

// The .NET runtime looks for exactly this global type and public static method.
internal static class StartupHook
{
    public static void Initialize()
    {
        try
        {
            // Avoid injecting Discord clients into any child .NET processes that inherit the hook.
            string executable = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
            if (!executable.Equals("webapi", StringComparison.OrdinalIgnoreCase))
                return;

            string folder = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location)!;
            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                // Splinter runs in AMP's single-file Web API; its own .deps.json is
                // not in the Web API's default dependency search graph.
                string? candidate = PluginDependencyResolver.ManagedAssemblyPath(folder, name);
                return candidate is null ? null : context.LoadFromAssemblyPath(candidate);
            };
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += (assembly, name) =>
            {
                // Only service SkiaSharp's own native import. Never redirect AMP's
                // native or managed dependencies into our plugin directory.
                string? candidate = PluginDependencyResolver.NativeLibraryPath(
                    folder, assembly.GetName().Name, name);
                return candidate is null ? IntPtr.Zero : NativeLibrary.Load(candidate);
            };

            const string key = "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES";
            string ownName = typeof(StartupHook).Assembly.GetName().Name!;
            string current = Environment.GetEnvironmentVariable(key) ?? "";
            string[] existing = current.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (!existing.Contains(ownName, StringComparer.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable(key, string.IsNullOrEmpty(current)
                    ? ownName : current.TrimEnd(';') + ";" + ownName);
            Console.Error.WriteLine("[Splinter] startup hook attached to webapi");
        }
        catch (Exception ex)
        {
            // A bot extension must not prevent the host from starting.
            try { Console.Error.WriteLine("[Splinter] startup hook error: " + ex); } catch { }
        }
    }
}


// This is intentionally a strict allowlist: AMP dependencies must continue to
// resolve in AMP's own load context, and only our packaged plugin dependencies
// may be loaded from splinter/plugin.
internal static class PluginDependencyResolver
{
    internal static bool IsBundledAssembly(string? name) =>
        name is not null && (name.StartsWith("Discord.", StringComparison.Ordinal) ||
            name is "Newtonsoft.Json" or "System.Reactive" or "System.Linq.Async" or
                "System.Interactive.Async" or "WebSocket4Net" or
                "SuperSocket.ClientEngine" or "Microsoft.Bcl.AsyncInterfaces" or
                "SkiaSharp");

    internal static string? ManagedAssemblyPath(string folder, AssemblyName name)
    {
        if (!IsBundledAssembly(name.Name)) return null;
        string candidate = Path.Combine(folder, name.Name + ".dll");
        return File.Exists(candidate) ? candidate : null;
    }

    internal static string? NativeLibraryPath(string folder, string? requester, string name)
    {
        if (requester != "SkiaSharp" ||
            name is not ("libSkiaSharp" or "SkiaSharp" or "libSkiaSharp.dll" or "libSkiaSharp.so"))
            return null;

        string? nativeFile = OperatingSystem.IsWindows() ? "libSkiaSharp.dll" :
            OperatingSystem.IsLinux() ? "libSkiaSharp.so" : null;
        if (nativeFile is null) return null;
        string candidate = Path.Combine(folder, nativeFile);
        return File.Exists(candidate) ? candidate : null;
    }
}

namespace Splinter.Host
{
    public sealed class DiscordHostingStartup : IHostingStartup
    {
        public void Configure(IWebHostBuilder builder)
        {
            // No HTTP route or authentication bypass is installed.
            builder.ConfigureServices(services =>
            {
                services.AddHostedService<ShardBotSupervisor>();

                // Explicitly opt in on a development installation. Do not change
                // AMP's controller routes, authentication middleware or frontend assets.
                // Disabling the flag and restarting AMP removes this integration.
                if (NativeSettingsPreview.Enabled)
                {
                    services.AddScoped<NativeSettingsActionFilter>();
                    services.Configure<MvcOptions>(options =>
                        options.Filters.AddService<NativeSettingsActionFilter>());
                }
            });
        }
    }
}
