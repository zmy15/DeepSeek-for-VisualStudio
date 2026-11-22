using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BenchmarkRunner
{
    internal sealed partial class HeadlessToolBridge
    {
        private static readonly ConcurrentDictionary<string, HeadlessTerminalJob>
            TerminalJobs = new(StringComparer.OrdinalIgnoreCase);

        private Task<string> ExecuteTerminalAsync(
            Dictionary<string, object> args,
            CancellationToken cancellationToken)
        {
            string command = GetString(args, "command").Trim();
            if (command.Length == 0)
                return Task.FromResult("Error: run_in_terminal: missing command");
            if (IsBuildOrTestCommand(command))
            {
                return Task.FromResult(
                    "[BLOCKED] Use build_solution or run_tests instead of invoking "
                    + "build/test commands in the terminal.");
            }

            bool isAsync = string.Equals(
                GetString(args, "mode"),
                "async",
                StringComparison.OrdinalIgnoreCase);
            if (!isAsync)
            {
                return RunProcessAsync(
                    command,
                    _workspaceRoot,
                    cancellationToken);
            }

            string id = Guid.NewGuid().ToString("N").Substring(0, 12);
            var job = new HeadlessTerminalJob(
                id,
                RunProcessAsync(command, _workspaceRoot, CancellationToken.None));
            TerminalJobs[id] = job;
            return Task.FromResult(
                $"Terminal command started (PID: {id}, mode: async). "
                + "Call get_terminal_output once with this id.");
        }

        private async Task<string> ExecuteGetTerminalOutputAsync(
            Dictionary<string, object> args,
            CancellationToken cancellationToken)
        {
            string id = GetString(args, "id").Trim();
            if (id.Length == 0)
                return "Error: get_terminal_output: missing id";
            if (!TerminalJobs.TryRemove(id, out HeadlessTerminalJob? job))
                return $"Error: terminal job not found: {id}";

            Task<string> completion = await Task.WhenAny(
                job.Completion,
                Task.Delay(Timeout.Infinite, cancellationToken)) == job.Completion
                ? job.Completion
                : Task.FromResult("Terminal output wait was cancelled.");
            return await completion;
        }

        private static async Task<string> RunProcessAsync(
            string command,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand "
                    + Convert.ToBase64String(Encoding.Unicode.GetBytes(
                        "$ProgressPreference='SilentlyContinue'; "
                        + "$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8; "
                        + command)),
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                    return "Error: failed to start terminal process";
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeout.Token);
                try
                {
                    while (!process.HasExited)
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        await Task.Delay(50, linked.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(); } catch { }
                    return "Timeout: terminal command exceeded 10 minutes.";
                }
                string output = (await stdout) + Environment.NewLine + (await stderr);
                return $"exit code: {process.ExitCode}"
                    + Environment.NewLine
                    + output.Trim();
            }
            catch (Exception ex)
            {
                return $"Error: terminal command failed: {ex.Message}";
            }
        }

        private static bool IsBuildOrTestCommand(string command)
        {
            string normalized = command.Trim().ToLowerInvariant();
            string[] markers =
            {
                "dotnet build", "dotnet test", "dotnet msbuild",
                "msbuild", "cmake --build", "ctest", "ninja",
                "g++ ", "gcc ", "clang", "cl ", "make ",
            };
            return markers.Any(marker => normalized.Contains(marker));
        }

        private sealed class HeadlessTerminalJob
        {
            public HeadlessTerminalJob(string id, Task<string> completion)
            {
                Id = id;
                Completion = completion;
            }

            public string Id { get; }
            public Task<string> Completion { get; }
        }
    }
}
