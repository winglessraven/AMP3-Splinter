using System.IO.Compression;

namespace Splinter.Installer;

internal static class ReleasePackager
{
    private static readonly HashSet<string> SupportedRids = new(StringComparer.Ordinal)
        { "win-x64", "linux-x64", "win-arm64", "linux-arm64" };

    public static void Package(Options options)
    {
        string root = SourceRoot();
        string output = Path.GetFullPath(options.Output ?? Path.Combine(root, "dist"));
        string[] rids = options.Rids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (rids.Length == 0 || rids.Any(r => !SupportedRids.Contains(r)))
            throw new ArgumentException("Supported RIDs: win-x64, linux-x64, win-arm64, linux-arm64.");
        Directory.CreateDirectory(output);
        string staging = Path.Combine(Path.GetTempPath(), "splinter-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            Console.WriteLine("Building the release source solution...");
            Dotnet(root, "build", Path.Combine(root, "Splinter.sln"), "-c", "Release");
            // Maintainer regression suites are intentionally kept outside the public
            // repository. Run both when present, but allow a clean public checkout
            // to package releases without missing-project failures.
            string[] testProjects = ["Splinter.NativeSettings.Tests", "Splinter.Installer.Tests"];
            string[] testFiles = testProjects
                .Select(project => Path.Combine(root, "tests", project, project + ".csproj"))
                .ToArray();
            int availableTests = testFiles.Count(File.Exists);
            if (availableTests == testFiles.Length)
            {
                Console.WriteLine("Running local maintainer regression suites...");
                foreach (string file in testFiles)
                    Dotnet(root, "run", "--project", file, "-c", "Release");
            }
            else if (availableTests == 0)
            {
                Console.WriteLine("Local regression suites are not included in this checkout; packaging without tests.");
            }
            else
            {
                throw new InvalidOperationException(
                    "Only one local regression suite is present. Restore both suites or remove both before packaging.");
            }
            foreach (string rid in rids)
            {
                string setupFolder = Path.Combine(staging, rid);
                // The plugin executes inside AMP's Web API, but SkiaSharp needs
                // native binaries matched to the destination OS and CPU architecture.
                // Publish an independent plugin payload for each release RID.
                string payload = Path.Combine(setupFolder, "payload");
                Dotnet(root, "publish", Path.Combine(root, "Splinter.Host", "Splinter.Host.csproj"),
                    "-c", "Release", "-r", rid, "--self-contained", "false",
                    "-p:UseAppHost=false", "-p:DebugType=None", "-o", payload);
                if (!File.Exists(Path.Combine(payload, "Splinter.Host.dll")))
                    throw new InvalidOperationException($"Missing Splinter.Host.dll in {rid} release payload.");
                ValidateRendererPayload(payload, rid);
                string setupOutput = Path.Combine(setupFolder, "setup");
                Dotnet(root, "publish", Path.Combine(root, "Splinter.Installer", "Splinter.Installer.csproj"),
                    "-c", "Release", "-r", rid, "--self-contained", "true", "-p:PublishSingleFile=true",
                    "-p:PublishTrimmed=false", "-p:EnableCompressionInSingleFile=true", "-p:DebugType=None",
                    "-o", setupOutput);
                string executable = Path.Combine(setupOutput, rid.StartsWith("win-", StringComparison.Ordinal)
                    ? "splinter-setup.exe" : "splinter-setup");
                if (!File.Exists(executable)) throw new InvalidOperationException($"Missing setup binary for {rid}.");
                string archive = Path.Combine(output, $"Splinter-{rid}.zip");
                if (File.Exists(archive)) File.Delete(archive);
                using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
                ZipArchiveEntry setupEntry = zip.CreateEntryFromFile(executable,
                    Path.GetFileName(executable), CompressionLevel.Optimal);
                if (rid.StartsWith("linux-", StringComparison.Ordinal))
                    setupEntry.ExternalAttributes = unchecked((int)(0x81EDu << 16)); // 0100755

                zip.CreateEntryFromFile(Path.Combine(root, "README.md"), "README.md", CompressionLevel.Optimal);
                foreach (string file in Directory.EnumerateFiles(payload, "*", SearchOption.TopDirectoryOnly))
                {
                    if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
                        file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                    zip.CreateEntryFromFile(file, "payload/" + Path.GetFileName(file), CompressionLevel.Optimal);
                }
                Console.WriteLine("Created " + archive);
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    internal static void ValidateRendererPayload(string payload, string rid)
    {
        // Class-library plugins loaded through DOTNET_STARTUP_HOOKS need both
        // the managed SkiaSharp assembly and the matching native runtime asset.
        // AMP does not consume Splinter.Host.deps.json during normal startup.
        foreach (string required in new[] { "SkiaSharp.dll", NativeLibraryNameForRid(rid) })
            if (!File.Exists(Path.Combine(payload, required)))
                throw new InvalidOperationException(
                    $"Missing {required} in {rid} release payload. " +
                    "Check that the Pretty Panel dependencies were published for the target RID.");
    }

    internal static string NativeLibraryNameForRid(string rid) => rid switch
    {
        "win-x64" or "win-arm64" => "libSkiaSharp.dll",
        "linux-x64" or "linux-arm64" => "libSkiaSharp.so",
        _ => throw new ArgumentOutOfRangeException(nameof(rid), rid, "Unsupported plugin runtime")
    };

    private static string SourceRoot()
    {
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(Path.GetFullPath(start));
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Splinter.sln"))) return current.FullName;
                current = current.Parent;
            }
        }
        throw new InvalidOperationException("Run package from within the Splinter source checkout.");
    }

    private static void Dotnet(string root, params string[] args)
    {
        var result = ProcessCommand.Run("dotnet", args, timeoutSeconds: 300, workingDirectory: root);
        if (result.ExitCode != 0)
        {
            string output = result.Combined;
            string tail = output.Length > 3500 ? output[^3500..] : output;
            throw new InvalidOperationException($"dotnet {args[0]} failed:\n{tail}");
        }
    }
}
