using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 测试服务实现 — 运行 .NET（dotnet test / VSTest + TRX）与 C++（CMake / ctest + JUnit）单元测试。
    ///
    /// 设计取舍：
    /// - .NET 走 dotnet test；C++ 走 ctest（见 TestService.CMake.cs）。
    /// - 自带构建：dotnet test 会先编译测试项目（含其依赖项目）再运行用例，
    ///   C++ 侧在 ctest 前先做一次增量 cmake --build；这样"跑测试"本身就是一次完整验证，
    ///   不需要额外先调 build_solution。
    /// - 不解析控制台文本，而是用结构化日志（TRX / JUnit XML）取结果，避免被本地化输出影响。
    /// </summary>
    public partial class TestService : ITestService
    {
        /// <summary>单次测试运行的超时上限。</summary>
        private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(10);

        /// <summary>失败用例明细最多保留的条数。</summary>
        private const int MaxFailedTests = 20;

        /// <summary>失败消息单条最大长度。</summary>
        private const int MaxMessageLength = 400;

        /// <summary>诊断输出尾部保留的行数。</summary>
        private const int OutputTailLines = 30;

        #region Test Project Discovery

        /// <summary>
        /// 判定 .NET 测试项目的项目文件特征。
        /// 命中任一即认为是测试项目：显式标记 IsTestProject、
        /// 引用 Microsoft.NET.Test.Sdk，或引用主流测试框架适配器/框架。
        /// </summary>
        private static readonly string[] TestProjectMarkers =
        {
            "<istestproject>true</istestproject>",
            "microsoft.net.test.sdk",
            "xunit",
            "nunit",
            "mstest",
        };

        /// <inheritdoc />
        public IReadOnlyList<string> FindTestProjects(string? solutionPath)
        {
            string? root = StoragePaths.NormalizeToDirectory(solutionPath);
            if (root == null || !Directory.Exists(root))
                return Array.Empty<string>();

            var results = new List<string>();
            try
            {
                foreach (string pattern in new[] { "*.csproj", "*.fsproj", "*.vbproj" })
                {
                    foreach (string projectFile in Directory.GetFiles(
                        root, pattern, SearchOption.AllDirectories))
                    {
                        if (IsTestProjectFile(projectFile))
                            results.Add(projectFile);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[TestService] Scanning test projects failed: {ex.Message}");
                return results;
            }

            // 跳过构建产物目录，避免把 obj/ 下的中间项目文件误判为测试项目
            results = results
                .Where(p => !IsUnderBuildOutput(p, root))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Logger.Info($"[TestService] Found {results.Count} test project(s) under {root}");
            return results;
        }

        /// <summary>
        /// 判断项目文件是否为测试项目。按内容特征匹配，不解析 MSBuild。
        /// </summary>
        internal static bool IsTestProjectFile(string projectFilePath)
        {
            try
            {
                string content = File.ReadAllText(projectFilePath);
                string lowered = content.ToLowerInvariant();
                return TestProjectMarkers.Any(marker => lowered.Contains(marker));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 判断路径是否位于 bin/obj 等构建产物目录下（扫描项目文件时排除）。
        /// </summary>
        private static bool IsUnderBuildOutput(string projectFilePath, string root)
        {
            try
            {
                // net472 没有 Path.GetRelativePath，用 Uri 做路径前缀裁剪
                string rootFull = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                string fileFull = Path.GetFullPath(projectFilePath);

                string relative = fileFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
                    ? fileFull.Substring(rootFull.Length)
                    : fileFull;

                string[] segments = relative.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries);

                // 中间目录命名与 MSBuild 属性相关，但 bin/obj 是默认且最常见的约定。
                return segments.Any(s =>
                    s.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    s.Equals("obj", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Run

        /// <inheritdoc />
        public async Task<TestRunResult> RunTestsAsync(
            string? solutionPath,
            string? projectPath,
            string? filter,
            string? configuration,
            CancellationToken ct)
        {
            var result = new TestRunResult();

            // ── 0. 技术栈分派：显式传入 CMakeLists.txt / CMake 工程目录时直接按 C++ 处理 ──
            if (IsCppProjectRequest(projectPath))
            {
                string cppDir = ResolveCppProjectDirectory(projectPath!, solutionPath);
                if (!Directory.Exists(cppDir))
                {
                    result.Error = TestRunErrorKind.NoTestProject;
                    return result;
                }

                await RunCmakeTestsAsync(cppDir, filter, configuration, result, ct);
                return result;
            }

            // ── 1. 解析要运行的测试项目 ──
            string? project = ResolveProject(solutionPath, projectPath, result);
            if (project == null)
            {
                // .NET 侧已经给出明确结论（多项目 / 显式路径无效）时不再改走 C++，
                // 只有「没找到 .NET 测试项目」才继续尝试 CMake/CTest。
                if (result.Error != TestRunErrorKind.NoTestProject)
                    return result;

                // 没有 .NET 测试项目时，退而检查 CMake/CTest 测试工程。
                // NO_TESTS 的稳定标记只在两类工程都不存在时返回，
                // 这样 EditAgent 的「测试优先、构建兜底」流程对 C++ 工作区同样成立。
                string? cppProject = ResolveCppTestProject(solutionPath, result);
                if (cppProject == null)
                    return result;

                await RunCmakeTestsAsync(cppProject, filter, configuration, result, ct);
                return result;
            }
            result.ProjectPath = project;
            result.Filter = filter ?? string.Empty;

            // ── 2. 定位 dotnet 运行器 ──
            string? dotnet = FindDotnetExecutable();
            if (dotnet == null)
            {
                Logger.Warn("[TestService] dotnet executable not found");
                result.Error = TestRunErrorKind.RunnerNotFound;
                return result;
            }

            // ── 3. 执行测试 ──
            string? trxPath = null;
            try
            {
                trxPath = await ExecuteDotnetTestAsync(
                    dotnet, project, filter, configuration, result, ct);
            }
            catch (OperationCanceledException)
            {
                result.Error = TestRunErrorKind.Timeout;
                return result;
            }

            // ── 4. 解析 TRX 结果 ──
            if (!string.IsNullOrEmpty(trxPath) && File.Exists(trxPath))
            {
                try
                {
                    ParseTrxResult(trxPath, result);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[TestService] Parsing TRX failed: {ex.Message}");
                    result.Error = TestRunErrorKind.NoResultProduced;
                }
                finally
                {
                    TryDeleteFile(trxPath);
                }
            }
            else if (result.Error == TestRunErrorKind.None)
            {
                // 进程跑完但没有产出日志：通常是测试程序集未构建。
                result.Error = TestRunErrorKind.NoResultProduced;
            }

            return result;
        }

        /// <summary>
        /// 决定本次运行哪个测试项目。优先显式指定，其次工作区内唯一测试项目，
        /// 多个候选时返回 MultipleProjects 交由调用方决定。
        /// </summary>
        private string? ResolveProject(string? solutionPath, string? projectPath, TestRunResult result)
        {
            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                string resolved = Path.GetFullPath(projectPath!);
                if (!File.Exists(resolved))
                {
                    result.Error = TestRunErrorKind.NoTestProject;
                    return null;
                }
                return resolved;
            }

            var projects = FindTestProjects(solutionPath);
            if (projects.Count == 0)
            {
                Logger.Warn("[TestService] No test project found in the workspace");
                result.Error = TestRunErrorKind.NoTestProject;
                return null;
            }

            if (projects.Count > 1)
            {
                Logger.Info($"[TestService] Multiple test projects found, project must be specified: {projects.Count}");
                result.Error = TestRunErrorKind.MultipleProjects;
                result.CandidateProjects.AddRange(projects);
                return null;
            }

            return projects[0];
        }

        /// <summary>
        /// 调用 dotnet test 执行测试，返回 TRX 日志路径（未产出时为 null）。
        /// </summary>
        private async Task<string?> ExecuteDotnetTestAsync(
            string dotnetPath,
            string projectPath,
            string? filter,
            string? configuration,
            TestRunResult result,
            CancellationToken ct)
        {
            string trxPath = Path.Combine(
                Path.GetTempPath(),
                $"deepseek-test-{Guid.NewGuid():N}.trx");

            var args = new StringBuilder();
            args.Append("test \"").Append(projectPath).Append('"');

            // 自带构建：dotnet test 默认先编译测试项目（含其依赖项目）再运行，
            // 这样"跑测试"是一个自洽动作，不需要在计划里单列"构建"步骤。
            args.Append(" --nologo");
            args.Append(" --logger \"trx;LogFileName=").Append(trxPath).Append('"');

            if (!string.IsNullOrWhiteSpace(filter))
                args.Append(" --filter \"").Append(filter).Append('"');

            if (!string.IsNullOrWhiteSpace(configuration))
                args.Append(" --configuration ").Append(configuration);

            var psi = new ProcessStartInfo
            {
                FileName = dotnetPath,
                Arguments = args.ToString(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // dotnet CLI 在输出被重定向时按 UTF-8 写出，显式指定编码避免诊断输出乱码
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(projectPath) ?? Environment.CurrentDirectory,
            };

            Logger.Info($"[TestService] Executing: {dotnetPath} {psi.Arguments}");
            var started = Stopwatch.StartNew();

            using var process = Process.Start(psi);
            if (process == null)
            {
                result.Error = TestRunErrorKind.LaunchFailed;
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RunTimeout);

            try
            {
                await Task.Run(() => process.WaitForExit(), cts.Token);
            }
            catch (OperationCanceledException)
            {
                Logger.Info("[TestService] Test run timed out or was cancelled, killing process...");
                ProcessTreeKiller.Kill(process, "[TestService]");
                throw;
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            started.Stop();

            result.Duration = started.Elapsed;
            result.OutputTail = BuildOutputTail(stdout + "\n" + stderr);

            Logger.Info($"[TestService] dotnet test exit code: {process.ExitCode}, elapsed {started.Elapsed.TotalSeconds:F1}s");
            return trxPath;
        }

        /// <summary>
        /// 取输出的尾部若干行作为诊断信息。
        /// </summary>
        private static string BuildOutputTail(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return string.Empty;

            var lines = output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd())
                .Where(l => l.Length > 0)
                .ToList();

            if (lines.Count == 0)
                return string.Empty;

            return string.Join("\n", lines.Skip(Math.Max(0, lines.Count - OutputTailLines)));
        }

        /// <summary>
        /// 查找 dotnet 可执行文件：先看 PATH，再回退到常见安装位置，
        /// 最后尝试 VS 自带的 dotnet（Open Folder / CMake 工作区常见）。
        /// </summary>
        internal static string? FindDotnetExecutable()
        {
            const string exeName = "dotnet.exe";

            // ── 1. PATH ──
            string? pathVar = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathVar))
            {
                foreach (string dir in pathVar.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    try
                    {
                        string candidate = Path.Combine(dir.Trim(), exeName);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                    catch { }
                }
            }

            // ── 2. 默认安装位置 ──
            string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string defaultPath = Path.Combine(progFiles, "dotnet", exeName);
            if (File.Exists(defaultPath))
                return defaultPath;

            // ── 3. VS 自带 dotnet（<VS Root>\dotnet\dotnet.exe）──
            try
            {
                string vswhere = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Microsoft Visual Studio", "Installer", "vswhere.exe");

                if (File.Exists(vswhere))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = vswhere,
                        Arguments = "-latest -property installationPath",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        string installPath = proc.StandardOutput.ReadToEnd().Trim();
                        proc.WaitForExit(5000);
                        if (!string.IsNullOrEmpty(installPath))
                        {
                            string vsDotnet = Path.Combine(installPath, "dotnet", exeName);
                            if (File.Exists(vsDotnet))
                                return vsDotnet;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[TestService] Locating VS bundled dotnet failed: {ex.Message}");
            }

            return null;
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[TestService] Deleting temp TRX failed: {ex.Message}");
            }
        }

        #endregion
    }
}
