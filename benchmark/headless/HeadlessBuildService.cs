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
            BuildRunResult result;
            if (_isCmake)
            {
                result = await BuildCmakeAsync(cancellationToken);
            }
            else
            {
                string target = string.IsNullOrWhiteSpace(solutionPath)
                    ? FindDotNetBuildTarget()
                    : Path.GetFullPath(solutionPath);
                ToolchainResult process = await ToolchainProcess.RunAsync(
                    "dotnet",
                    $"build \"{target}\" --nologo",
                    _workspaceRoot,
                    cancellationToken);
                result = new BuildRunResult
                {
                    Success = process.ExitCode == 0,
                    Command = $"dotnet build \"{target}\" --nologo",
                    Output = (process.ExitCode == 0 ? "Build succeeded." : "Error: Build FAILED.")
                        + Environment.NewLine
                        + process.Output,
                };
            }

            // get_errors reads this snapshot. Test runs build through BuildCoreAsync
            // as well, so refresh it here rather than only in BuildAsync.
            _lastOutput = result.Output;
            return result;
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
            string text = output ?? string.Empty;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // MSBuild/Roslyn: file(line,column): error CSxxxx: message
            foreach (Match match in Regex.Matches(
                text,
                @"^(?<file>[^\r\n]+?)\((?<line>\d+),(?<column>\d+)\):\s*error\s+(?<code>[^:\r\n]+):\s*(?<message>.+)$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                AddError(
                    errors,
                    seen,
                    match.Groups["file"].Value,
                    match.Groups["line"].Value,
                    match.Groups["column"].Value,
                    match.Groups["code"].Value,
                    match.Groups["message"].Value);
            }

            // GCC/Clang: file:line:column: error: message
            foreach (Match match in Regex.Matches(
                text,
                @"^(?<file>(?:[A-Za-z]:[\\/])?(?:[^\r\n:]+?)):(?<line>\d+):(?<column>\d+):\s*(?:fatal\s+)?error:\s*(?<message>.+)$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                AddError(
                    errors,
                    seen,
                    match.Groups["file"].Value,
                    match.Groups["line"].Value,
                    match.Groups["column"].Value,
                    string.Empty,
                    match.Groups["message"].Value);
            }
            return errors;
        }

        private static void AddError(
            List<ErrorListItem> errors,
            HashSet<string> seen,
            string file,
            string lineText,
            string columnText,
            string code,
            string message)
        {
            int line = int.TryParse(lineText, out int parsedLine) ? parsedLine : 0;
            int column = int.TryParse(columnText, out int parsedColumn) ? parsedColumn : 0;
            string normalizedFile = file.Trim();
            string normalizedMessage = message.Trim();
            string key = $"{normalizedFile}|{line}|{column}|{code}|{normalizedMessage}";
            if (!seen.Add(key))
                return;

            errors.Add(
                new ErrorListItem
                {
                    FileName = normalizedFile,
                    Line = line,
                    Column = column,
                    ErrorCode = string.IsNullOrWhiteSpace(code) ? null : code.Trim(),
                    Description = normalizedMessage,
                    Category = "error",
                });
        }
    }

    internal sealed class BuildRunResult
    {
        public bool Success { get; set; }

        public string Command { get; set; } = string.Empty;

        public string Output { get; set; } = string.Empty;
    }
}
