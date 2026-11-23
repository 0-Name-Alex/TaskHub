using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using TaskHub.Core.Automation;

namespace TaskHub.Automation;

/// <summary>
/// <see cref="IShellLauncher"/> for Windows: <c>open</c> goes through ShellExecute (like double-clicking),
/// <c>run</c> starts the program directly with an argument list — no cmd.exe in between.
/// </summary>
public sealed class ShellLauncher(ILogger<ShellLauncher> log) : IShellLauncher
{
    private const int MaxOutput = 4000;

    public void Open(string target, string? arguments = null, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(target)
        {
            UseShellExecute = true,
            Arguments = arguments ?? "",
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory)) psi.WorkingDirectory = workingDirectory;
        log.LogInformation("Opening {Target} {Args}", target, arguments);
        using var _ = Process.Start(psi);
    }

    public async Task<ProcessOutcome> RunAsync(ProcessStart start, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(start.Program)
        {
            UseShellExecute = false,
            CreateNoWindow = start.Hidden,
            RedirectStandardOutput = start.Wait,
            RedirectStandardError = start.Wait,
            StandardOutputEncoding = start.Wait ? Encoding.UTF8 : null,
            StandardErrorEncoding = start.Wait ? Encoding.UTF8 : null,
        };
        foreach (var arg in start.Arguments) psi.ArgumentList.Add(arg);
        if (!string.IsNullOrWhiteSpace(start.WorkingDirectory)) psi.WorkingDirectory = start.WorkingDirectory;
        foreach (var (key, value) in start.Environment) psi.Environment[key] = value;

        log.LogInformation("Starting {Program} with {Count} argument(s){Wait}", start.Program, start.Arguments.Count, start.Wait ? " and waiting" : "");
        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Windows didn't start '{start.Program}'.");
        if (!start.Wait)
        {
            process.Dispose();
            return new ProcessOutcome(null, "", false);
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(start.Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* already exited */ }
                ct.ThrowIfCancellationRequested();
                return new ProcessOutcome(null, "", true);
            }

            string output;
            try
            {
                output = ((await stdout.ConfigureAwait(false)) + "\n" + (await stderr.ConfigureAwait(false))).Trim();
            }
            catch (OperationCanceledException)
            {
                output = "";
            }
            if (output.Length > MaxOutput) output = output[..MaxOutput];
            return new ProcessOutcome(process.ExitCode, output, false);
        }
    }
}
