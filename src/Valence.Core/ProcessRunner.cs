using System.Diagnostics;
using System.Text;

namespace Valence.Core;

internal sealed record ProcessResult(int ExitCode, string StdOut, bool TimedOut);

internal static class ProcessRunner
{
    /// <summary>
    /// Runs a process without a shell. Stderr lines are forwarded to <paramref name="onStdErr"/> as they arrive.
    /// </summary>
    public static ProcessResult Run(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        Action<string>? onStdErr = null,
        IDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        if (environment != null)
        {
            foreach (var (key, value) in environment) startInfo.Environment[key] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) onStdErr?.Invoke(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds)))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
            return new ProcessResult(-1, stdout.ToString(), TimedOut: true);
        }

        process.WaitForExit(); // flush async output handlers
        lock (stdout) return new ProcessResult(process.ExitCode, stdout.ToString(), TimedOut: false);
    }
}
