using DeepSeek_v4_for_VisualStudio.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BenchmarkRunner
{
    /// <summary>
    /// Builds solely through the CLI toolchain. It never uses DTE, VS Shell,
    /// VS Error List, or the VS installation environment.
    /// </summary>
    internal sealed class HeadlessBuildService : IBuildService
    {
        private readonly string _workspaceRoot;
        private readonly bool _isCmake;
        private string _lastOutput = string.Empty;

        public HeadlessBuildService(string workspaceRoot)
        {
            _workspaceRoot = Path.GetFullPath(workspaceRoot);
            _isCmake = File.Exists(Path.Combine(_workspaceRoot, "CMakeLists.txt"));
        }

        public async Task<string> BuildAsync(
            string? solutionPath,
            CancellationToken cancellationToken)
        {
            BuildRunResult result = await BuildCoreAsync(solutionPath, cancellationToken);
            _lastOutput = result.Output;
            return result.Output;
        }

        public Task<List<ErrorListItem>> GetSelectedErrorsAsync(
            CancellationToken cancellationToken)
        {
            return Task.FromResult(ParseErrors(_lastOutput));
        }

        public Task<List<ErrorListItem>> GetAllErrorsAsync(
            CancellationToken cancellationToken)
        {
            return Task.FromResult(ParseErrors(_lastOutput));
        }

        internal async Task<BuildRunResult> BuildCoreAsync(
            string? solutionPath,
            CancellationToken cancellationToken)
        {
            if (_isCmake)
                return await BuildCmakeAsync(cancellationToken);

            string target = string.IsNullOrWhiteSpace(solutionPath)
                ? FindDotNetBuildTarget()
                : Path.GetFullPath(solutionPath);
            var result = await ToolchainProcess.RunAsync(
                "dotnet",
                $"build \"{target}\" --nologo",
                _workspaceRoot,
                cancellationToken);
            return new BuildRunResult
            {
                Success = result.ExitCode == 0,
                Command = $"dotnet build \"{target}\" --nologo",
                Output = (result.ExitCode == 0 ? "Build succeeded." : "Error: Build FAILED.")
                    + Environment.NewLine
                    + result.Output,
            };
        }

        private async Task<BuildRunResult> BuildCmakeAsync(
            CancellationToken cancellationToken)
        {
            string buildDirectory = Path.Combine(_workspaceRoot, "build");
            string cachePath = Path.Combine(buildDirectory, "CMakeCache.txt");
            var commands = new List<string>();
            string output = string.Empty;

            if (!File.Exists(cachePath))
            {
                string configureArguments =
                    $"-S \"{_workspaceRoot}\" -B \"{buildDirectory}\" "
                    + "-G Ninja -DCMAKE_BUILD_TYPE=Debug";
                commands.Add("cmake " + configureArguments);
                ToolchainResult configure = await ToolchainProcess.RunAsync(
                    "cmake",
                    configureArguments,
                    _workspaceRoot,
                    cancellationToken);
                output = configure.Output;
                if (configure.ExitCode != 0)
                {
                    return new BuildRunResult
                    {
                        Success = false,
                        Command = string.Join(Environment.NewLine, commands),
                        Output = "Error: CMake configure FAILED."
                            + Environment.NewLine
                            + output,
                    };
                }
            }

            string buildArguments = $"--build \"{buildDirectory}\"";
            commands.Add("cmake " + buildArguments);
            ToolchainResult build = await ToolchainProcess.RunAsync(
                "cmake",
                buildArguments,
                _workspaceRoot,
                cancellationToken);
            output = string.Join(
                Environment.NewLine,
                new[] { output, build.Output }.Where(value => !string.IsNullOrWhiteSpace(value)));
            return new BuildRunResult
            {
                Success = build.ExitCode == 0,
                Command = string.Join(Environment.NewLine, commands),
                Output = (build.ExitCode == 0 ? "Build succeeded." : "Error: Build FAILED.")
                    + Environment.NewLine
                    + output,
            };
        }

        private string FindDotNetBuildTarget()
        {
            string? solution = Directory
                .GetFiles(_workspaceRoot, "*.slnx", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetFiles(
                    _workspaceRoot,
                    "*.sln",
                    SearchOption.TopDirectoryOnly))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            return solution ?? _workspaceRoot;
        }

        private static List<ErrorListItem> ParseErrors(string output)
        {
            var errors = new List<ErrorListItem>();
            foreach (Match match in Regex.Matches(
                output ?? string.Empty,
                @"^(?<file>[^\r\n:]+)\((?<line>\d+),(?<column>\d+)\):\s*error\s+(?<code>[^:]+):\s*(?<message>.+)$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                errors.Add(
                    new ErrorListItem
                    {
                        FileName = match.Groups["file"].Value.Trim(),
                        Line = int.TryParse(match.Groups["line"].Value, out int line)
                            ? line
                            : 0,
                        Column = int.TryParse(match.Groups["column"].Value, out int column)
                            ? column
                            : 0,
                        ErrorCode = match.Groups["code"].Value.Trim(),
                        Description = match.Groups["message"].Value.Trim(),
                        Category = "error",
                    });
            }
            return errors;
        }
    }

    internal sealed class BuildRunResult
    {
        public bool Success { get; set; }

        public string Command { get; set; } = string.Empty;

        public string Output { get; set; } = string.Empty;
    }
}
