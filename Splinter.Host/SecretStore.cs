using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;

namespace Splinter.Host;

// AMP owns cryptography and the key ring. Splinter only owns the per-Shard record.
// All interaction with AMP's unsupported secret API is confined to this file.
public static class SecretStore
{
    private const string Binding = "splinter:discord-token:v1:";
    private const int MaxTokenLength = 512;

    public static bool HasToken(Guid shardId) => File.Exists(ConfigStore.SecretPath(shardId));

    public static void WriteToken(Guid shardId, string token, IServiceProvider services)
    {
        if (shardId == Guid.Empty) throw new ArgumentException("A Shard ID is required.", nameof(shardId));
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength || token.Any(char.IsControl))
            throw new ArgumentException("A valid Discord bot token is required.", nameof(token));
        ArgumentNullException.ThrowIfNull(services);

        string cleanToken = token.Trim();
        var protector = new AmpSecretBridge(services);
        string encrypted = protector.Protect(Binding + shardId.ToString("D") + ":" + cleanToken);
        if (!protector.IsProtected(encrypted))
            throw new InvalidOperationException("AMP did not return a protected credential.");

        string path = ConfigStore.SecretPath(shardId);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        SecureDirectory(directory);
        string pending = Path.Combine(directory, "." + shardId.ToString("D") + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            // AMP creates and secures the file. Never write a plaintext token to disk.
            protector.WriteProtectedFile(pending, encrypted);
            string check = ReadBoundToken(shardId, pending, protector);
            if (!string.Equals(check, cleanToken, StringComparison.Ordinal))
                throw new InvalidDataException("Protected Discord token verification failed.");
            File.Move(pending, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(pending)) File.Delete(pending);
        }
    }

    public static string? ReadToken(Guid shardId, IServiceProvider services)
    {
        if (shardId == Guid.Empty) throw new ArgumentException("A Shard ID is required.", nameof(shardId));
        string path = ConfigStore.SecretPath(shardId);
        if (!File.Exists(path)) return null;
        return ReadBoundToken(shardId, path, new AmpSecretBridge(services));
    }

    public static void DeleteToken(Guid shardId) => File.Delete(ConfigStore.SecretPath(shardId));

    public static string FileVersion(Guid shardId)
    {
        string path = ConfigStore.SecretPath(shardId);
        return File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "missing";
    }

    private static string ReadBoundToken(Guid shardId, string path, AmpSecretBridge protector)
    {
        // Never follow a symlink into another Shard's file or outside Splinter storage.
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Discord credential path is a symbolic link.");
        if (new FileInfo(path).Length > 4096)
            throw new InvalidDataException("Discord credential record is too large.");
        string ciphertext = protector.ReadProtectedFile(path).Trim();
        if (!protector.IsProtected(ciphertext))
            throw new InvalidDataException("Discord credential was not protected by AMP.");
        string plaintext = protector.Unprotect(ciphertext);
        string prefix = Binding + shardId.ToString("D") + ":";
        if (!plaintext.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("Discord credential belongs to a different Shard or is invalid.");
        string token = plaintext[prefix.Length..];
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength || token.Any(char.IsControl))
            throw new InvalidDataException("Discord credential payload is invalid.");
        return token;
    }

    private static void SecureDirectory(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Discord credential directory must not be a symbolic link.");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            UnixFileMode actual = File.GetUnixFileMode(path);
            if ((actual & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                           UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new InvalidOperationException("Discord credential directory is accessible by other users.");
        }
        // Windows directory DACLs are provisioned by Splinter.Installer;
        // the native AMP file writer additionally verifies each file's ACL.
    }
}

internal sealed class AmpSecretBridge
{
    private readonly object _protector;
    private readonly MethodInfo _protect;
    private readonly MethodInfo _unprotect;
    private readonly MethodInfo _isProtected;
    private readonly MethodInfo _write;
    private readonly MethodInfo _read;
    private readonly string _keyPath;

    public AmpSecretBridge(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Type protectorType = FindType("AmpShared.Security.SecretProtector");
        Type fileType = FindType("AmpShared.Security.SecretFileProtection");
        Type pathsType = FindType("AmpShared.Platform.AmpDataPaths");
        object paths = services.GetService(pathsType)
            ?? throw new InvalidOperationException("AMP's registered data paths are unavailable.");
        _keyPath = pathsType.GetProperty("SecretKeyFile")?.GetValue(paths) as string
            ?? throw new MissingMemberException("AMP's native secret-key path is unavailable.");
        _protector = services.GetService(protectorType) ?? ActivatorUtilities.CreateInstance(services, protectorType);
        _protect = Required(protectorType, "Protect", BindingFlags.Public | BindingFlags.Instance, typeof(string));
        _unprotect = Required(protectorType, "Unprotect", BindingFlags.Public | BindingFlags.Instance, typeof(string));
        _isProtected = Required(protectorType, "IsProtected", BindingFlags.Public | BindingFlags.Static, typeof(string));
        _write = fileType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(m => m.Name == "Write" && m.GetParameters().Length == 6 &&
                m.GetParameters()[0].ParameterType == typeof(string) &&
                m.GetParameters()[1].ParameterType == typeof(string) &&
                m.GetParameters()[2].ParameterType == typeof(bool))
            ?? throw new MissingMethodException("AMP SecretFileProtection.Write signature changed.");
        _read = Required(fileType, "ReadAllText", BindingFlags.Public | BindingFlags.Static, typeof(string));
    }

    private static Type FindType(string name) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(name, throwOnError: false))
            .FirstOrDefault(t => t is not null)
        ?? Type.GetType(name + ", AmpShared", throwOnError: false)
        ?? throw new TypeLoadException("AMP native secret infrastructure is unavailable: " + name);

    private static MethodInfo Required(Type type, string name, BindingFlags flags, params Type[] signature) =>
        type.GetMethod(name, flags, binder: null, types: signature, modifiers: null)
        ?? throw new MissingMethodException(type.FullName, name);

    private static object Invoke(MethodInfo method, object? target, params object?[] args)
    {
        try { return method.Invoke(target, args) ?? throw new InvalidOperationException("AMP secret operation returned null."); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new InvalidOperationException("AMP native secret operation failed.", ex.InnerException);
        }
    }

    public bool IsProtected(string value) => (bool)Invoke(_isProtected, null, value);

    public string Protect(string value)
    {
        ValidateNativeKey(requireExisting: false);
        string result = (string)Invoke(_protect, _protector, value);
        ValidateNativeKey(requireExisting: true);
        return result;
    }

    public string Unprotect(string value)
    {
        ValidateNativeKey(requireExisting: true);
        return (string)Invoke(_unprotect, _protector, value);
    }

    private void ValidateNativeKey(bool requireExisting)
    {
        if (!File.Exists(_keyPath))
        {
            if (requireExisting) throw new InvalidOperationException("AMP's native secret key is missing.");
            return;
        }
        if ((File.GetAttributes(_keyPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("AMP's native secret key must not be a symbolic link.");
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(_keyPath);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                         UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new InvalidOperationException("AMP's native secret key has unsafe filesystem permissions.");
        }
    }
    public string ReadProtectedFile(string path) => (string)Invoke(_read, null, path);

    public void WriteProtectedFile(string path, string ciphertext)
    {
        object outcome = Invoke(_write, null, path, ciphertext, false, null, null, null);
        Type resultType = outcome.GetType();
        bool written = resultType.GetProperty("Written")?.GetValue(outcome) is true;
        string? failure = resultType.GetProperty("ProtectionFailure")?.GetValue(outcome) as string;
        if (!written || failure is not null)
            throw new InvalidOperationException("AMP could not securely persist the encrypted Discord credential.");
    }
}
