using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Splinter.Admin;

/// <summary>
/// Reads only CLR metadata. Does not load or execute AMP assemblies, instantiate
/// AMP services, inspect configuration data, or access credential values.
/// </summary>
public static class AmpMetadataInspector
{
    private static readonly string[] TargetTypeWords =
    [
        "Setting", "Secret", "Credential", "Crypt", "Protect", "Password", "Vault",
        "PermissionContext", "ShardSettingsWriter"
    ];

    private static readonly string[] TargetMemberWords =
    [
        "Secret", "Credential", "Crypt", "Protect", "Password", "Value", "Permission",
        "Save", "Write", "Read", "Load", "Set", "Get", "Update", "Patch", "Scope", "Store", "Remove"
    ];

    public static int Run(string path, TextWriter output, TextWriter errors)
    {
        string[] files;
        if (File.Exists(path))
        {
            files = [Path.GetFullPath(path)];
        }
        else if (Directory.Exists(path))
        {
            // Skip third-party dependencies; examining their metadata would drown out
            // the relevant AMP API. The user can pass an explicit DLL to override.
            files = Directory.EnumerateFiles(path, "*.dll", SearchOption.AllDirectories)
                .Where(f =>
                {
                    string n = Path.GetFileName(f);
                    return n.Equals("webapi.dll", StringComparison.OrdinalIgnoreCase) ||
                           n.StartsWith("AmpShared", StringComparison.OrdinalIgnoreCase) ||
                           n.StartsWith("CubeCoders", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        else
        {
            errors.WriteLine("No such directory or assembly file.");
            return 2;
        }

        if (files.Length == 0)
        {
            errors.WriteLine("No AMP assembly files found. Supply the AMP version directory or the full path to a DLL.");
            return 2;
        }

        output.WriteLine("Splinter AMP 3 protected-settings metadata survey");
        output.WriteLine("Metadata only: no assemblies executed and no values or credentials read.");
        output.WriteLine("IMPORTANT: IsSecret metadata and cryptography references DO NOT prove encrypted storage.");
        output.WriteLine();

        int examined = 0;
        foreach (string file in files)
        {
            try
            {
                if (InspectAssembly(file, output)) examined++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
            {
                errors.WriteLine($"Skipped {Path.GetFileName(file)} ({e.GetType().Name}).");
            }
        }
        output.WriteLine($"Examined {examined} managed AMP assembly file(s).");
        return examined > 0 ? 0 : 2;
    }

    // Narrow metadata-only survey for the AMP 3 game-control bridge. No code is
    // loaded or executed and no persistent AMP data or token files are opened.
    public static int RunActions(string path, TextWriter output, TextWriter errors)
    {
        string[] files = File.Exists(path) ? [Path.GetFullPath(path)] :
            Directory.Exists(path) ? Directory.GetFiles(path, "*.dll", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f) is "AmpShared.dll" or "webapi.dll")
                .OrderBy(x => x).ToArray() : [];
        if (files.Length == 0)
        {
            errors.WriteLine("No AMP assemblies found; supply the extracted AMP DLL directory.");
            return 2;
        }
        output.WriteLine("AMP 3 Shard action signatures (metadata only; no AMP code executed)");
        string[] verbs = ["Start", "Stop", "Restart", "Kill", "Update", "Backup", "Console", "Command", "Execute"];
        int matched = 0;
        foreach (string file in files)
        {
            try
            {
                using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
                if (!pe.HasMetadata) continue;
                MetadataReader reader = pe.GetMetadataReader();
                var formatter = new SignatureFormatter();
                foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
                {
                    TypeDefinition type = reader.GetTypeDefinition(handle);
                    string name = FullName(reader.GetString(type.Namespace), reader.GetString(type.Name));
                    if (!(name.StartsWith("AmpShared.Shards.", StringComparison.Ordinal) ||
                          name.StartsWith("webapi.Controllers.Shards", StringComparison.Ordinal) ||
                          name.StartsWith("webapi.Services.Shard", StringComparison.Ordinal))) continue;
                    foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                    {
                        MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                        string methodName = reader.GetString(method.Name);
                        if (!HasAny(methodName, verbs)) continue;
                        try
                        {
                            MethodSignature<string> signature = method.DecodeSignature(formatter, genericContext: null);
                            output.WriteLine($"{name}.{methodName}(" +
                                string.Join(", ", signature.ParameterTypes) + $") -> {signature.ReturnType}");
                            matched++;
                        }
                        catch (BadImageFormatException) { }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
            {
                errors.WriteLine("Skipped a non-readable AMP assembly.");
            }
        }
        output.WriteLine($"Discovered {matched} relevant method signatures.");
        return matched > 0 ? 0 : 2;
    }

    // Discover the AMP 3 player-event and snapshot contracts without running
    // AMP code or reading its persistent data. This is deliberately a survey:
    // method names alone do not establish safe invocation semantics.
    public static int RunSnapshotEvents(string path, TextWriter output, TextWriter errors)
    {
        string[] files;
        if (File.Exists(path))
            files = [Path.GetFullPath(path)];
        else if (Directory.Exists(path))
            files = Directory.EnumerateFiles(path, "*.dll", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f).Equals("webapi.dll", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(f).Equals("AmpShared.dll", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        else
            files = [];

        if (files.Length == 0)
        {
            errors.WriteLine("No managed AMP DLLs found. Use the extracted AMP 3 webapi.dll and AmpShared.dll directory.");
            return 2;
        }

        output.WriteLine("AMP 3 snapshot and player-event API survey");
        output.WriteLine("Metadata only: does not execute AMP, read settings, or access credentials.");
        output.WriteLine("Signatures are not evidence of a supported or authorised call path.");
        int scanned = 0;
        int matching = 0;
        string[] typeWords =
        [
            "Snapshot", "Backup", "ShardUserStore", "ConnectedUser",
            "UserSession", "PlayerEvent", "UserEvent", "PlayerTracker",
            "ConnectionEvent", "SessionEvent", "UserTracker"
        ];
        foreach (string file in files)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
                if (!pe.HasMetadata) continue;
                MetadataReader reader = pe.GetMetadataReader();
                if (!reader.IsAssembly) continue;
                scanned++;
                output.WriteLine();
                output.WriteLine("ASSEMBLY " + Path.GetFileName(file));
                var formatter = new SignatureFormatter();
                foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
                {
                    TypeDefinition type = reader.GetTypeDefinition(typeHandle);
                    string fullName = FullName(reader.GetString(type.Namespace), reader.GetString(type.Name));
                    if (!HasAny(fullName, typeWords) || fullName.Contains('<')) continue;
                    if (!(fullName.StartsWith("webapi.", StringComparison.Ordinal) ||
                          fullName.StartsWith("AmpShared.", StringComparison.Ordinal) ||
                          files.Length == 1)) continue;
                    matching++;
                    output.WriteLine("  TYPE " + fullName);
                    foreach (EventDefinitionHandle eventHandle in type.GetEvents())
                    {
                        EventDefinition item = reader.GetEventDefinition(eventHandle);
                        output.WriteLine("    EVENT " + reader.GetString(item.Name));
                    }
                    foreach (PropertyDefinitionHandle propertyHandle in type.GetProperties())
                    {
                        PropertyDefinition item = reader.GetPropertyDefinition(propertyHandle);
                        try
                        {
                            output.WriteLine("    PROPERTY " +
                                item.DecodeSignature(formatter, genericContext: null).ReturnType + " " +
                                reader.GetString(item.Name));
                        }
                        catch (BadImageFormatException) { }
                    }
                    foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                    {
                        MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                        string name = reader.GetString(method.Name);
                        // Accessor methods are already represented by property/event names.
                        if (name is ".ctor" or ".cctor" || name.StartsWith("get_", StringComparison.Ordinal) ||
                            name.StartsWith("set_", StringComparison.Ordinal) ||
                            name.StartsWith("add_", StringComparison.Ordinal) ||
                            name.StartsWith("remove_", StringComparison.Ordinal)) continue;
                        try
                        {
                            MethodSignature<string> signature = method.DecodeSignature(formatter, genericContext: null);
                            output.WriteLine("    METHOD " + signature.ReturnType + " " + name + "(" +
                                string.Join(", ", signature.ParameterTypes) + ")");
                        }
                        catch (BadImageFormatException) { }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
            {
                errors.WriteLine("Skipped " + Path.GetFileName(file) + " (" + ex.GetType().Name + ").");
            }
        }
        output.WriteLine();
        output.WriteLine($"Examined {scanned} managed assemblies; found {matching} matching types.");
        return scanned > 0 && matching > 0 ? 0 : 2;
    }

    // Narrow read-only inventory for possible AMP 3 game-chat/event interfaces.
    // This intentionally does not infer NATS subjects from type names: only
    // AMP's publisher/consumer implementation can establish the actual contract.
    public static int RunChatEvents(string path, TextWriter output, TextWriter errors)
    {
        string[] files = File.Exists(path) ? [Path.GetFullPath(path)] :
            Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.dll", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f).Equals("webapi.dll", StringComparison.OrdinalIgnoreCase) ||
                            Path.GetFileName(f).Equals("AmpShared.dll", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray() : [];
        if (files.Length == 0)
        {
            errors.WriteLine("No AMP assemblies found. Supply extracted webapi.dll and AmpShared.dll, or a managed DLL.");
            return 2;
        }

        output.WriteLine("AMP 3 game-chat and message-event API survey");
        output.WriteLine("Metadata only: no AMP code executed, no console/chat contents or secrets read.");
        output.WriteLine("Type names do not establish that AMP publishes a native chat event.");
        int scanned = 0, matching = 0;
        foreach (string file in files)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
                if (!pe.HasMetadata) continue;
                MetadataReader reader = pe.GetMetadataReader();
                if (!reader.IsAssembly) continue;
                scanned++;
                output.WriteLine();
                output.WriteLine("ASSEMBLY " + Path.GetFileName(file));
                var formatter = new SignatureFormatter();
                foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
                {
                    TypeDefinition type = reader.GetTypeDefinition(handle);
                    string fullName = FullName(reader.GetString(type.Namespace), reader.GetString(type.Name));
                    if (fullName.Contains('<')) continue;
                    if (files.Length != 1 && !fullName.StartsWith("webapi.", StringComparison.Ordinal) &&
                        !fullName.StartsWith("AmpShared.", StringComparison.Ordinal)) continue;

                    bool relevantType = fullName.Contains("Chat", StringComparison.OrdinalIgnoreCase) ||
                        fullName.EndsWith(".Ns", StringComparison.Ordinal) ||
                        (fullName.Contains("Message", StringComparison.OrdinalIgnoreCase) &&
                         (fullName.Contains("Shard", StringComparison.OrdinalIgnoreCase) ||
                          fullName.Contains("Game", StringComparison.OrdinalIgnoreCase) ||
                          fullName.Contains("Hub", StringComparison.OrdinalIgnoreCase))) ||
                        fullName.Contains("MessageBus", StringComparison.OrdinalIgnoreCase);
                    var methods = new List<string>();
                    foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                    {
                        MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                        string name = reader.GetString(method.Name);
                        if (name is ".ctor" or ".cctor" || name.StartsWith("get_", StringComparison.Ordinal) ||
                            name.StartsWith("set_", StringComparison.Ordinal) ||
                            name.StartsWith("add_", StringComparison.Ordinal) ||
                            name.StartsWith("remove_", StringComparison.Ordinal)) continue;
                        if (!relevantType && !name.Contains("Chat", StringComparison.OrdinalIgnoreCase)) continue;
                        if (fullName.EndsWith(".Ns", StringComparison.Ordinal) &&
                            !HasAny(name, ["Chat", "Message", "Shard", "Console", "Event"])) continue;
                        try
                        {
                            MethodSignature<string> signature = method.DecodeSignature(formatter, genericContext: null);
                            methods.Add("    METHOD " + signature.ReturnType + " " + name + "(" +
                                string.Join(", ", signature.ParameterTypes) + ")");
                        }
                        catch (BadImageFormatException) { }
                        if (methods.Count >= 60) break;
                    }
                    if (!relevantType && methods.Count == 0) continue;
                    matching++;
                    output.WriteLine("  TYPE " + fullName);
                    if (relevantType)
                    {
                        foreach (EventDefinitionHandle eventHandle in type.GetEvents())
                            output.WriteLine("    EVENT " + reader.GetString(reader.GetEventDefinition(eventHandle).Name));
                        foreach (PropertyDefinitionHandle propertyHandle in type.GetProperties())
                        {
                            var property = reader.GetPropertyDefinition(propertyHandle);
                            string name = reader.GetString(property.Name);
                            if (!HasAny(name, ["Chat", "Message", "Text", "Content", "Sender", "Shard", "Event", "Player", "User"])) continue;
                            try
                            {
                                output.WriteLine("    PROPERTY " +
                                    property.DecodeSignature(formatter, genericContext: null).ReturnType + " " + name);
                            }
                            catch (BadImageFormatException) { }
                        }
                    }
                    foreach (string line in methods) output.WriteLine(line);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
            {
                errors.WriteLine("Skipped " + Path.GetFileName(file) + " (" + ex.GetType().Name + ").");
            }
        }
        output.WriteLine();
        output.WriteLine($"Examined {scanned} managed assemblies; found {matching} candidate types.");
        return scanned > 0 ? 0 : 2;
    }

    private static bool InspectAssembly(string path, TextWriter output)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (!pe.HasMetadata) return false;
        MetadataReader reader = pe.GetMetadataReader();
        if (!reader.IsAssembly) return false;

        output.WriteLine($"ASSEMBLY {Path.GetFileName(path)}");
        var formatter = new SignatureFormatter();
        int typesPrinted = 0;
        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            string name = FullName(reader.GetString(type.Namespace), reader.GetString(type.Name));
            if (!HasAny(name, TargetTypeWords)) continue;
            if (typesPrinted++ >= 160)
            {
                output.WriteLine("  (remaining matching types omitted; inspect a narrower assembly)");
                break;
            }
            output.WriteLine($"  TYPE {name}");
            foreach (PropertyDefinitionHandle propertyHandle in type.GetProperties())
            {
                string propertyName = reader.GetString(reader.GetPropertyDefinition(propertyHandle).Name);
                if (HasAny(propertyName, TargetMemberWords) || name.EndsWith("SettingSpec", StringComparison.Ordinal))
                    output.WriteLine($"    PROPERTY {propertyName}");
            }
            int methodsPrinted = 0;
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                string methodName = reader.GetString(method.Name);
                if (!HasAny(methodName, TargetMemberWords) || methodName.StartsWith("get_", StringComparison.Ordinal) ||
                    methodName.StartsWith("set_", StringComparison.Ordinal)) continue;
                if (methodsPrinted++ >= 75)
                {
                    output.WriteLine("    (remaining matching methods omitted)");
                    break;
                }
                try
                {
                    MethodSignature<string> signature = method.DecodeSignature(formatter, genericContext: null);
                    output.WriteLine($"    METHOD {signature.ReturnType} {methodName}(" +
                        string.Join(", ", signature.ParameterTypes) + ")");
                }
                catch (BadImageFormatException)
                {
                    output.WriteLine($"    METHOD {methodName} (signature unavailable)");
                }
            }
        }
        string[] relevantReferences = reader.AssemblyReferences
            .Select(h => reader.GetString(reader.GetAssemblyReference(h).Name))
            .Where(n => n.Contains("Cryptography", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("DataProtection", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (relevantReferences.Length != 0)
            output.WriteLine("  CRYPTOGRAPHY REFERENCES " + string.Join(", ", relevantReferences));
        output.WriteLine();
        return true;
    }

    private static bool HasAny(string text, string[] needles) =>
        needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string FullName(string ns, string name) =>
        string.IsNullOrEmpty(ns) ? name : ns + "." + name;

    private sealed class SignatureFormatter : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
        public string GetByReferenceType(string elementType) => "ref " + elementType;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            genericType + "<" + string.Join(", ", typeArguments) + ">";
        // Compatibility with metadata interface versions that still expose the old name.
        public string GetGenericInstance(string genericType, ImmutableArray<string> typeArguments) =>
            GetGenericInstantiation(genericType, typeArguments);
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            return FullName(reader.GetString(type.Namespace), reader.GetString(type.Name));
        }
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            TypeReference type = reader.GetTypeReference(handle);
            return FullName(reader.GetString(type.Namespace), reader.GetString(type.Name));
        }
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext,
            TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}
