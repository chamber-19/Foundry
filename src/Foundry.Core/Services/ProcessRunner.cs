using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Services;

public sealed class ProcessRunner
{
    private readonly ILogger<ProcessRunner> _logger;

    public ProcessRunner(ILogger<ProcessRunner>? logger = null)
    {
        _logger = logger ?? NullLogger<ProcessRunner>.Instance;
    }

    public async Task<string> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default
    )
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            var stderrSnippet = string.IsNullOrWhiteSpace(error)
                ? "(no stderr)"
                : error.Trim().Length > 500
                    ? error.Trim()[..500] + "..."
                    : error.Trim();

            var message = $"Command '{fileName} {arguments}' failed with exit code {process.ExitCode}. stderr: {stderrSnippet}";
            _logger.LogWarning("Process failed: {Command} exit code {ExitCode}. stderr: {Stderr}",
                $"{fileName} {arguments}", process.ExitCode, stderrSnippet);
            throw new InvalidOperationException(message);
        }

        return output;
    }

    /// <summary>
    /// Checks whether Python 3 is available on this system.
    /// Returns the version string (e.g. "Python 3.12.0") or null if unavailable.
    /// </summary>
    public async Task<string?> CheckPythonAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var output = await RunAsync("python3", "--version", null, cancellationToken);
            var version = output.Trim();
            if (!string.IsNullOrWhiteSpace(version))
            {
                return version;
            }
        }
        catch
        {
            // python3 not found, try python
        }

        try
        {
            var output = await RunAsync("python", "--version", null, cancellationToken);
            var version = output.Trim();
            if (!string.IsNullOrWhiteSpace(version) && version.StartsWith("Python 3", StringComparison.OrdinalIgnoreCase))
            {
                return version;
            }
        }
        catch
        {
            // python not found either
        }

        return null;
    }
}
