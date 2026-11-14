namespace Splinter.Installer;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Options options = Options.Parse(args);
            if (options.Command == "help")
            {
                Options.PrintHelp();
                return 0;
            }
            if (options.Command == "package")
            {
                ReleasePackager.Package(options);
                return 0;
            }
            new Installer(options).Execute();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Splinter setup failed: {ex.Message}");
            return 1;
        }
    }
}

internal sealed record Options(
    string Command,
    string? DataRoot,
    string? Service,
    string? Payload,
    string? Output,
    string Rids,
    bool NoStart,
    bool Yes)
{
    public static Options Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help")
            return new("help", null, null, null, null, "win-x64,linux-x64", false, false);
        string command = args[0].ToLowerInvariant();
        if (command is not ("install" or "update" or "uninstall" or "doctor" or "package"))
            throw new ArgumentException($"Unrecognised operation '{command}'. Run 'splinter-setup help'.");
        string? root = null, service = null, payload = null, output = null;
        string rids = "win-x64,linux-x64";
        bool noStart = false, yes = false;
        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "--no-start") { noStart = true; continue; }
            if (arg is "--yes") { yes = true; continue; }
            if (arg is not ("--data" or "--service" or "--payload" or "--output" or "--rids") || ++i == args.Length)
                throw new ArgumentException($"Unknown option or missing value: {arg}");
            string value = args[i];
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"Empty value for {arg}");
            switch (arg)
            {
                case "--data": root = value; break;
                case "--service": service = value; break;
                case "--payload": payload = value; break;
                case "--output": output = value; break;
                case "--rids": rids = value; break;
            }
        }
        if (command != "package" && (output is not null || rids != "win-x64,linux-x64"))
            throw new ArgumentException("--output and --rids are package-only options.");
        if (command == "package" && (root is not null || service is not null || payload is not null || noStart || yes))
            throw new ArgumentException("package accepts only --output and --rids.");
        if (command == "uninstall" && !yes)
            throw new ArgumentException("Uninstall removes the hook and staged plugin. Re-run with --yes. Shard data is retained.");
        return new(command, root, service, payload, output, rids, noStart, yes);
    }

    public static void PrintHelp() => Console.WriteLine("""
        Splinter setup — AMP 3 Discord integration

        Windows (Administrator):  .\splinter-setup.exe install
        Linux (root):             sudo ./splinter-setup install

        install                    Install Splinter and activate it in AMP
        update                     Replace Splinter binaries, preserving data and settings
        doctor                     Read-only installation diagnostics
        uninstall --yes            Remove Splinter's hook and binaries; keep Shard data

        Options for install/update/doctor/uninstall:
          --data <directory>       AMP 3 persistent data directory (auto-detected if possible)
          --service <name>         AMP Web API service (default AMP3 on Windows)
          --payload <directory>    Plugin files (default ./payload next to setup)
          --no-start               Leave AMP stopped after installation/update

        For maintainers with the .NET 10 SDK:
          dotnet run --project Splinter.Installer -- package [--output dist]
          --rids win-x64,linux-x64  Target platforms (also supports arm64)

        Installer packages are self-contained; users need no SDK. AMP 3 must already
        be installed and able to run its own .NET 10 Web API.
        """);
}
