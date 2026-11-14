using System.Diagnostics;

namespace Splinter.Installer;

internal sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public string Combined => (Output + "\n" + Error).Trim();
}

internal static class ProcessCommand
{
    public static ProcessResult Run(string executable, IEnumerable<string> arguments, int timeoutSeconds = 90,
        string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };
        foreach (string arg in arguments) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        { throw new InvalidOperationException($"Unable to launch required program: {executable}.", ex); }
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Command timed out: {Path.GetFileName(executable)}.");
        }
        Task.WaitAll(stdout, stderr);
        return new ProcessResult(process.ExitCode, stdout.Result, stderr.Result);
    }

    public static ProcessResult Required(string executable, params string[] arguments)
    {
        ProcessResult result = Run(executable, arguments);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} failed (exit {result.ExitCode}): " +
                SafeError(result.Combined));
        return result;
    }

    private static string SafeError(string error)
    {
        // Do not copy arbitrary systemd environment values into diagnostic output.
        string text = error.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > 300 ? text[..300] + "…" : text;
    }
}
