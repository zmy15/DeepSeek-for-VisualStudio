using DeepSeek_v4_for_VisualStudio.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace BenchmarkRunner
{
    /// <summary>
    /// CLI-only test runner for .NET test projects, standalone console test
    /// harnesses, and CMake/CTest projects. No VS services are used.
    /// </summary>
    internal sealed class HeadlessTestService : ITestService
    {
        private static readonly string[] DotNetTestMarkers =
        {
            "<istestproject>true</istestproject>",
            "microsoft.net.test.sdk",
            "xunit",
            "nunit",
            "mstest",
        };

        private readonly string _workspaceRoot;
        private readonly HeadlessBuildService _buildService;

        public HeadlessTestService(
            string workspaceRoot,
            HeadlessBuildService buildService)
        {
            _workspaceRoot = Path.GetFullPath(workspaceRoot);
            _buildService = buildService;
        }

        public IReadOnlyList<string> FindTestProjects(string? solutionPath)
        {
            return DiscoverDotNetTests(_workspaceRoot);
        }

        public IReadOnlyList<string> FindCppTestProjects(string? solutionPath)
        {
            string cmake = Path.Combine(_workspaceRoot, "CMakeLists.txt");
            if (!File.Exists(cmake))
                return Array.Empty<string>();
            string text = File.ReadAllText(cmake);
            return text.IndexOf("enable_testing", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("add_test", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("include(CTest)", StringComparison.OrdinalIgnoreCase) >= 0
                ? new[] { _workspaceRoot }
                : Array.Empty<string>();
        }

        public async Task<TestRunResult> RunTestsAsync(
            string? solutionPath,
            string? projectPath,
            string? filter,
            string? configuration,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<string> cppProjects = FindCppTestProjects(solutionPath);
            if (cppProjects.Count > 0)
                return await RunCmakeTestsAsync(filter, cancellationToken);

            IReadOnlyList<string> dotNetProjects = FindTestProjects(solutionPath);
            string? project = ResolveProject(dotNetProjects, projectPath, out TestRunErrorKind error);
            if (project == null)
            {
                return new TestRunResult
                {
                    Error = error,
                    Kind = TestProjectKind.DotNet,
                };
            }

            return IsStandaloneConsoleTest(project)
                ? await RunConsoleTestsAsync(project, configuration, cancellationToken)
                : await RunDotNetTestsAsync(project, filter, configuration, cancellationToken);
        }

        private async Task<TestRunResult> RunCmakeTestsAsync(
            string? filter,
            CancellationToken cancellationToken)
        {
            BuildRunResult build = await _buildService.BuildCoreAsync(
                null,
                cancellationToken);
            if (!build.Success)
            {
                return new TestRunResult
                {
                    Error = TestRunErrorKind.BuildFailed,
                    Kind = TestProjectKind.Cpp,
                    ProjectPath = _workspaceRoot,
                    Filter = filter ?? string.Empty,
                    OutputTail = build.Output,
                };
            }

            string buildDirectory = Path.Combine(_workspaceRoot, "build");
            string arguments =
                $"--test-dir \"{buildDirectory}\" -C Debug --output-on-failure";
            if (!string.IsNullOrWhiteSpace(filter))
                arguments += $" -R \"{filter}\"";

            ToolchainResult ctest = await ToolchainProcess.RunAsync(
                "ctest",
                arguments,
                _workspaceRoot,
                cancellationToken);
            var result = new TestRunResult
            {
                Error = TestRunErrorKind.None,
                Kind = TestProjectKind.Cpp,
                ProjectPath = _workspaceRoot,
                Filter = filter ?? string.Empty,
                OutputTail = (build.Output + Environment.NewLine + ctest.Output).Trim(),
            };
            ParseCtestSummary(ctest.Output, result);
            if (result.Total == 0)
            {
                result.Error = TestRunErrorKind.NoResultProduced;
                result.Passed = 0;
                result.Failed = 0;
            }
            return result;
        }

        private async Task<TestRunResult> RunDotNetTestsAsync(
            string project,
            string? filter,
            string? configuration,
            CancellationToken cancellationToken)
        {
            string resultsDirectory = Path.Combine(
                Path.GetTempPath(),
                "deepseek-headless-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(resultsDirectory);
            string arguments =
                $"test \"{project}\" --nologo "
                + $"--logger \"trx;LogFileName=results.trx\" "
                + $"--results-directory \"{resultsDirectory}\"";
            if (!string.IsNullOrWhiteSpace(filter))
                arguments += $" --filter \"{filter}\"";
            if (!string.IsNullOrWhiteSpace(configuration))
                arguments += $" --configuration \"{configuration}\"";

            try
            {
                ToolchainResult process = await ToolchainProcess.RunAsync(
                    "dotnet",
                    arguments,
                    Path.GetDirectoryName(project) ?? _workspaceRoot,
                    cancellationToken);
                var result = new TestRunResult
                {
                    Kind = TestProjectKind.DotNet,
                    ProjectPath = project,
                    Filter = filter ?? string.Empty,
                    OutputTail = process.Output,
                };

                string? trx = Directory
                    .GetFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (trx != null)
                    ParseTrx(trx, result);
                if (result.Total == 0)
                    ParseDotNetTestSummary(process.Output, result);
                if (process.ExitCode != 0 && result.Failed == 0)
                    result.Error = TestRunErrorKind.BuildFailed;
                else if (result.Total == 0)
                    result.Error = TestRunErrorKind.NoResultProduced;
                return result;
            }
            finally
            {
                try { Directory.Delete(resultsDirectory, recursive: true); }
                catch { }
            }
        }

        private async Task<TestRunResult> RunConsoleTestsAsync(
            string project,
            string? configuration,
            CancellationToken cancellationToken)
        {
            string arguments = $"run --project \"{project}\"";
            if (!string.IsNullOrWhiteSpace(configuration))
                arguments += $" --configuration \"{configuration}\"";

            ToolchainResult process = await ToolchainProcess.RunAsync(
                "dotnet",
                arguments,
                Path.GetDirectoryName(project) ?? _workspaceRoot,
                cancellationToken);
            var result = new TestRunResult
            {
                Kind = TestProjectKind.DotNet,
                ProjectPath = project,
                OutputTail = process.Output,
            };
            Match summary = Regex.Match(
                process.Output,
                @"(?<passed>\d+)\s*/\s*(?<total>\d+)\s+checks passed",
                RegexOptions.IgnoreCase);
            if (summary.Success)
            {
                result.Passed = int.Parse(summary.Groups["passed"].Value);
                result.Total = int.Parse(summary.Groups["total"].Value);
                result.Failed = Math.Max(0, result.Total - result.Passed);
            }
            else
            {
                result.Error = TestRunErrorKind.NoResultProduced;
            }
            if (process.ExitCode != 0 && result.Failed == 0)
                result.Error = TestRunErrorKind.LaunchFailed;
            return result;
        }

        private List<string> DiscoverDotNetTests(string root)
        {
            var projects = Directory
                .GetFiles(root, "*.csproj", SearchOption.AllDirectories)
                .Where(path => !IsBuildOutput(path))
                .Where(path => IsDotNetTestProject(path) || IsStandaloneConsoleTest(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return projects;
        }

        private static bool IsDotNetTestProject(string projectPath)
        {
            string content = File.ReadAllText(projectPath).ToLowerInvariant();
            return DotNetTestMarkers.Any(content.Contains);
        }

        private static bool IsStandaloneConsoleTest(string projectPath)
        {
            string normalized = projectPath.Replace('\\', '/');
            if (!normalized.Contains("/tests/", StringComparison.OrdinalIgnoreCase))
                return false;
            string directory = Path.GetDirectoryName(projectPath) ?? string.Empty;
            if (!File.Exists(Path.Combine(directory, "Program.cs")))
                return false;
            return File.ReadAllText(projectPath)
                .IndexOf("<OutputType>Exe</OutputType>", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsBuildOutput(string path)
        {
            string normalized = path.Replace('\\', '/');
            return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
        }

        private static string? ResolveProject(
            IReadOnlyList<string> projects,
            string? projectPath,
            out TestRunErrorKind error)
        {
            error = TestRunErrorKind.None;
            if (!string.IsNullOrWhiteSpace(projectPath))
                return Path.GetFullPath(projectPath);
            if (projects.Count == 1)
                return projects[0];
            error = projects.Count == 0
                ? TestRunErrorKind.NoTestProject
                : TestRunErrorKind.MultipleProjects;
            return null;
        }

        private static void ParseCtestSummary(string output, TestRunResult result)
        {
            Match percent = Regex.Match(
                output,
                @"(\d+)%\s+tests\s+passed,\s+(\d+)\s+tests?\s+failed\s+out\s+of\s+(\d+)",
                RegexOptions.IgnoreCase);
            Match plain = Regex.Match(
                output,
                @"(\d+)\s+out\s+of\s+(\d+)\s+tests?\s+passed",
                RegexOptions.IgnoreCase);
            if (percent.Success)
            {
                result.Failed = int.Parse(percent.Groups[2].Value);
                result.Total = int.Parse(percent.Groups[3].Value);
                result.Passed = Math.Max(0, result.Total - result.Failed);
            }
            else if (plain.Success)
            {
                result.Passed = int.Parse(plain.Groups[1].Value);
                result.Total = int.Parse(plain.Groups[2].Value);
                result.Failed = Math.Max(0, result.Total - result.Passed);
            }
        }

        private static void ParseDotNetTestSummary(string output, TestRunResult result)
        {
            Match passed = Regex.Match(
                output,
                @"Passed:\s*(\d+)",
                RegexOptions.IgnoreCase);
            Match failed = Regex.Match(
                output,
                @"Failed:\s*(\d+)",
                RegexOptions.IgnoreCase);
            Match skipped = Regex.Match(
                output,
                @"Skipped:\s*(\d+)",
                RegexOptions.IgnoreCase);
            if (!passed.Success && !failed.Success)
                return;
            result.Passed = passed.Success ? int.Parse(passed.Groups[1].Value) : 0;
            result.Failed = failed.Success ? int.Parse(failed.Groups[1].Value) : 0;
            result.Skipped = skipped.Success ? int.Parse(skipped.Groups[1].Value) : 0;
            result.Total = result.Passed + result.Failed + result.Skipped;
        }

        private static void ParseTrx(string path, TestRunResult result)
        {
            XDocument document = XDocument.Load(path);
            XNamespace ns = document.Root?.Name.Namespace ?? XNamespace.None;
            XElement? counters = document.Descendants(ns + "Counters").FirstOrDefault();
            if (counters != null)
            {
                result.Total = ReadInt(counters, "total");
                result.Passed = ReadInt(counters, "passed");
                result.Failed = ReadInt(counters, "failed");
                result.Skipped = ReadInt(counters, "notExecuted");
            }

            foreach (XElement item in document
                .Descendants(ns + "UnitTestResult")
                .Where(element => string.Equals(
                    (string?)element.Attribute("outcome"),
                    "Failed",
                    StringComparison.OrdinalIgnoreCase))
                .Take(20))
            {
                result.FailedTests.Add(
                    new FailedTestInfo
                    {
                        Name = (string?)item.Attribute("testName") ?? "(unknown)",
                        Message = item
                            .Descendants(ns + "Message")
                            .FirstOrDefault()?
                            .Value
                            .Trim() ?? "Test failed.",
                    });
            }
        }

        private static int ReadInt(XElement element, string name)
        {
            return int.TryParse((string?)element.Attribute(name), out int value)
                ? value
                : 0;
        }
    }
}
