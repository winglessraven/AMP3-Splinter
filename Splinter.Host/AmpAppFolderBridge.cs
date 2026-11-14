using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Splinter.Host;

// AMP 3 compatibility boundary: resolve exactly the same application directory
// as its snapshot engine, never guess a location from a Shard display name.
public sealed class AmpAppFolderBridge
{
    private readonly IServiceScopeFactory _scopes;
    public AmpAppFolderBridge(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task<string> ResolveAsync(Guid shardId, CancellationToken ct)
    {
        Assembly shared = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
            a => a.GetName().Name == "AmpShared") ?? throw new NotSupportedException("AMP shared assembly unavailable.");
        Assembly api = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
            a => a.GetName().Name == "webapi") ?? throw new NotSupportedException("AMP Web API assembly unavailable.");
        Type shardsType = shared.GetType("AmpShared.Shards.ShardService", true)!;
        Type launcherType = api.GetType("webapi.Services.ShardAgentLauncher", true)!;
        Type pathsType = shared.GetTypes().FirstOrDefault(t => t.Name == "AppPaths")
            ?? throw new NotSupportedException("AMP application-path resolver unavailable.");
        var resolve = pathsType.GetMethod("ResolveAppDir", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            binder: null, types: [typeof(string), typeof(string)], modifiers: null)
            ?? throw new NotSupportedException("AMP application-path signature changed.");
        using var scope = _scopes.CreateScope();
        object shards = scope.ServiceProvider.GetRequiredService(shardsType);
        object launcher = scope.ServiceProvider.GetRequiredService(launcherType);
        MethodInfo get = shardsType.GetMethod("GetAsync", [typeof(string), typeof(Guid)])
            ?? throw new NotSupportedException("AMP Shard lookup signature changed.");
        MethodInfo spec = shardsType.GetMethod("GetSpecAsync", [typeof(string), typeof(Guid), typeof(CancellationToken)])
            ?? throw new NotSupportedException("AMP template lookup signature changed.");
        object? shard = await ResultAsync(get.Invoke(shards, ["_root", shardId]), ct).ConfigureAwait(false)
            ?? throw new DirectoryNotFoundException("Shard unavailable.");
        object? appSpec = await ResultAsync(spec.Invoke(shards, ["_root", shardId, ct]), ct).ConfigureAwait(false);
        object? lifecycle = appSpec?.GetType().GetProperty("Lifecycle")?.GetValue(appSpec);
        string baseDirectory = lifecycle?.GetType().GetProperty("BaseDirectory")?.GetValue(lifecycle)?.ToString() ?? "";
        MethodInfo working = launcherType.GetMethod("ResolveWorkingDirAsync", [shard.GetType()])
            ?? throw new NotSupportedException("AMP Shard working-directory signature changed.");
        string workingDir = (await ResultAsync(working.Invoke(launcher, [shard]), ct).ConfigureAwait(false))?.ToString()
            ?? throw new DirectoryNotFoundException("Shard working directory unavailable.");
        string root = resolve.Invoke(null, [workingDir, baseDirectory])?.ToString()
            ?? throw new NotSupportedException("AMP application path unavailable.");
        return Path.GetFullPath(root);
    }

    private static async Task<object?> ResultAsync(object? value, CancellationToken ct)
    {
        if (value is not Task task) throw new NotSupportedException("AMP method no longer returns Task.");
        await task.WaitAsync(ct).ConfigureAwait(false);
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }
}
