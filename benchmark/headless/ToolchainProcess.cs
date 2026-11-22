using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace BenchmarkRunner
{
    internal sealed class ToolchainResult
    {
        public int ExitCode { get; set; }

        public string Output { get; set; } = string.Empty;

        public TimeSpan Duration { get; set; }
    }

    internal static class ToolchainProcess
    {
        public static async Task<ToolchainResult> RunAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            TimeSpan? timeout = null)
        {
            var started = Stopwatch.StartNew();
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                throw new InvalidOperationException($"Failed to start {fileName}.");

            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromMinutes(10);
            var timeoutAt = DateTime.UtcNow + effectiveTimeout;

            while (!process.HasExited)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    TryKill(process);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (DateTime.UtcNow >= timeoutAt)
                {
                    TryKill(process);
                    string timedOut = (await stdout) + Environment.NewLine + (await stderr);
                    return new ToolchainResult
                    {
                        ExitCode = 124,
                        Output = $"Timeout: {effectiveTimeout.TotalMinutes:F0} minutes."
                            + Environment.NewLine
                            + timedOut,
                        Duration = started.Elapsed,
                    };
                }

                await Task.Delay(100);
            }

            string output = (await stdout) + Environment.NewLine + (await stderr);
            return new ToolchainResult
            {
                ExitCode = process.ExitCode,
                Output = output.Trim(),
                Duration = started.Elapsed,
            };
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch
            {
                // Best effort only; the caller reports timeout/cancellation.
            }
        }
    }
}
