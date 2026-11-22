using DeepSeek_v4_for_VisualStudio.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BenchmarkRunner
{
    internal static class HeadlessToolAudit
    {
        private static readonly string[] KnownTools =
        {
            "list_dir",
            "read_file",
            "file_search",
            "grep_search",
            "symbol_search",
            "get_file_symbols",
            "get_file_comments",
            "list_skills",
            "load_skill",
            "read_skill_resource",
            "get_errors",
            "get_current_time",
            "fetch_webpage",
            "web_search",
            "capture_window",
            "capture_webpage",
            "build_solution",
            "run_tests",
            "replace_string_in_file",
            "multi_replace_string_in_file",
            "create_file",
            "delete_file",
            "apply_patch",
            "create_directory",
            "run_in_terminal",
            "get_terminal_output",
            "VisualStudio_askQuestions",
            "runSubagent",
            "request_handoff",
            "memory",
            "git",
        };

        public static async Task<int> RunAsync(string[] args)
        {
            AuditOptions options = AuditOptions.Parse(args);
            string sourceWorkspace = Path.GetFullPath(options.CaseDirectory);
            ToolAuditWorkspace workspace = await ToolAuditWorkspace.CreateAsync(
                sourceWorkspace,
                CancellationToken.None);
            var results = new List<ToolAuditResult>();

            var buildService = new HeadlessBuildService(workspace.Root);
            var testService = new HeadlessTestService(workspace.Root, buildService);
            using HeadlessToolProxyHost proxyHost =
                HeadlessToolProxyHost.Create(workspace.Root, buildService);
            var builtIn = new BuiltInToolService(
                mcpManager: null,
                webSearchService: null,
                buildService: buildService,
                memoryService: null,
                skillService: null,
                testService: testService);
            builtIn.WebSearchEnabled = false;
            var tools = new HeadlessToolRouter(builtIn, proxyHost.Bridge);
            string proxyProbe = await proxyHost.Manager.CallToolAsync(
                "read_file",
                JsonSerializer.Serialize(new
                {
                    filePath = "audit-fixtures/AuditSample.cs",
                }),
                CancellationToken.None);
            if (!proxyProbe.Contains("AuditSample", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The in-process benchmark tool proxy was not reachable.");
            }

            bool handoffHandled = false;
            tools.BuiltIn.ExploreHandler = _ => Task.FromResult(
                "audit subagent handler reached");
            tools.BuiltIn.HandoffHandler = _ =>
            {
                handoffHandled = true;
                return Task.CompletedTask;
            };

            await AuditReadAndSearchAsync(tools, workspace, results);
            await AuditSkillsAsync(tools, workspace, results);
            await AuditTerminalAndGitAsync(tools, workspace, results);
            await AuditEditingAsync(tools, workspace, results);
            await AuditBuildAndDiagnosticsAsync(
                tools,
                buildService,
                workspace,
                results);
            await AuditInteractiveAsync(tools, workspace, results, () => handoffHandled);
            ToolExecution fetch = await ExecuteAsync(
                tools,
                workspace.Root,
                "fetch_webpage",
                new { url = "https://example.com" });
            AddResult(
                results,
                "fetch_webpage",
                fetch.Text?.Contains("disabled", StringComparison.OrdinalIgnoreCase) == true
                    ? ToolAuditStatus.Disabled
                    : ToolAuditStatus.Failed,
                "Headless web access is intentionally disabled.",
                fetch.Text,
                fetch.DurationMs);
            AddConditionalAndUnclassifiedResults(tools, results);

            string markdown = ToolAuditReport.RenderMarkdown(
                sourceWorkspace,
                workspace.Root,
                results);
            Console.WriteLine(markdown);
            if (!string.IsNullOrWhiteSpace(options.OutputPath))
            {
                ToolAuditReport.WriteJson(
                    options.OutputPath,
                    sourceWorkspace,
                    workspace.Root,
                    results);
                Console.WriteLine(
                    $"JSON audit report: {Path.GetFullPath(options.OutputPath)}");
            }
            return ToolAuditReport.HasFailures(results) ? 1 : 0;
        }

        private static async Task AuditReadAndSearchAsync(
            HeadlessToolRouter tools,
            ToolAuditWorkspace workspace,
            List<ToolAuditResult> results)
        {
            await RunToolAsync(
                tools,
                workspace,
                results,
                "list_dir",
                new { path = "." },
                ToolAuditStatus.Works,
                text => text.Contains("audit-fixtures", StringComparison.OrdinalIgnoreCase),
                "Listed the isolated workspace.");
            await RunToolAsync(
                tools,
                workspace,
                results,
                "read_file",
                new { filePath = "audit-fixtures/AuditSample.cs" },
                ToolAuditStatus.Works,
                text => text.Contains("AuditSample", StringComparison.Ordinal),
                "Read the audit source file.");
            await RunToolAsync(
                tools,
                workspace,
                results,
                "file_search",
                new { query = "**/AuditSample.cs" },
                ToolAuditStatus.Works,
                text => text.Contains("AuditSample", StringComparison.Ordinal)
                    && !text.Contains("找到 0", StringComparison.Ordinal),
                "Found the audit source file.");
            await RunToolAsync(
                tools,
                workspace,
                results,
                "grep_search",
                new { query = "AuditSample", path = "audit-fixtures" },
                ToolAuditStatus.Works,
                text => text.Contains("AuditSample", StringComparison.Ordinal),
                "Found source text.");
            await RunToolAsync(
                tools,
                workspace,
                results,
                "symbol_search",
                new { query = "AuditSample", maxResults = 10 },
                ToolAuditStatus.HeadlessAdapted,
                text => text.Contains("AuditSample", StringComparison.Ordinal),
                "Used the source-text symbol fallback.");
            await RunToolAsync(
                tools,
                workspace,
                results,
                "get_file_symbols",
                new { filePath = "audit-fixtures/AuditSample.cs" },
                ToolAuditStatus.HeadlessAdapted,
                text => text.Contains("AuditSample", StringComparison.Ordinal)
                    && text.Contains("Run", StringComparison.Ordinal),
                "Used the source outline fallback.");
            await RunToolAsync(
                tools,
                workspace,
                results,
                "get_file_comments",
                new { filePath = "audit-fixtures/AuditSample.cs" },
                ToolAuditStatus.HeadlessAdapted,
                text => text.Contains("Audit sample widget", StringComparison.Ordinal),
                "Used the source comment fallback.");
            await RunToolAsync(
                tools,
                workspace,
                results,
                "get_current_time",
                new { },
                ToolAuditStatus.Works,
                text => !string.IsNullOrWhiteSpace(text),
                "Returned the current time.");
        }

        private static async Task AuditSkillsAsync(
            HeadlessToolRouter tools,
            ToolAuditWorkspace workspace,
            List<ToolAuditResult> results)
        {
            await RunToolAsync(
                tools,
                workspace,
                results,
                "list_skills",
                new { },
                ToolAuditStatus.Works,
                text => text.Contains("audit-skill", StringComparison.OrdinalIgnoreCase),
                "Discovered the fixture skill.");
            await RunToolAsync(
                tools,
                workspace,
                results,
                "load_skill",
                new { name = "audit-skill" },
                ToolAuditStatus.Works,
                text => text.Contains("Audit Skill", StringComparison.OrdinalIgnoreCase),
                "Loaded the fixture skill.");
            await RunToolAsync(
                tools,
                workspace,
                results,
                "read_skill_resource",
                new { skillName = "audit-skill", relativePath = "references/reference.txt" },
                ToolAuditStatus.Works,
                text => text.Contains("audit-skill-resource", StringComparison.Ordinal),
                "Read a skill resource.");
        }

        private static async Task AuditTerminalAndGitAsync(
            HeadlessToolRouter tools,
            ToolAuditWorkspace workspace,
            List<ToolAuditResult> results)
        {
            await RunToolAsync(
                tools,
                workspace,
                results,
                "git",
                new { operation = "status" },
                ToolAuditStatus.Works,
                text => text.Contains("exit code: 0", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("退出码: 0", StringComparison.OrdinalIgnoreCase),
                "Ran git status in the temporary repository.");

            ToolExecution terminal = await ExecuteAsync(
                tools,
                workspace.Root,
                "run_in_terminal",
                new { command = "Write-Output 'audit-terminal-ok'", mode = "sync" });
            ToolExecution buildBlocked = await ExecuteAsync(
                tools,
                workspace.Root,
                "run_in_terminal",
                new { command = "dotnet build", mode = "sync" });
            ToolExecution testBlocked = await ExecuteAsync(
                tools,
                workspace.Root,
                "run_in_terminal",
                new { command = "dotnet test", mode = "sync" });
            bool terminalWorks = terminal.Text?.Contains(
                "audit-terminal-ok",
                StringComparison.Ordinal) == true
                && buildBlocked.Text?.Contains(
                    "build_solution",
                    StringComparison.OrdinalIgnoreCase) == true
                && testBlocked.Text?.Contains(
                    "run_tests",
                    StringComparison.OrdinalIgnoreCase) == true;
            AddResult(
                results,
                "run_in_terminal",
                terminalWorks ? ToolAuditStatus.Works : ToolAuditStatus.Failed,
                terminalWorks
                    ? "Ran a safe command and blocked build/test commands."
                    : "Terminal smoke test did not complete as expected.",
                Combine(terminal.Text, buildBlocked.Text, testBlocked.Text),
                terminal.DurationMs + buildBlocked.DurationMs + testBlocked.DurationMs);

            ToolExecution asyncStart = await ExecuteAsync(
                tools,
                workspace.Root,
                "run_in_terminal",
                new { command = "Write-Output 'audit-async-ok'", mode = "async" });
            Match idMatch = Regex.Match(
                asyncStart.Text ?? string.Empty,
                @"PID:\s*(?<id>[A-Za-z0-9_-]+)",
                RegexOptions.IgnoreCase);
            ToolExecution terminalOutput = idMatch.Success
                ? await ExecuteAsync(
                    tools,
                    workspace.Root,
                    "get_terminal_output",
                    new { id = idMatch.Groups["id"].Value })
                : new ToolExecution
                {
                    Text = asyncStart.Text,
                    DurationMs = 0,
                };
            bool outputWorks = idMatch.Success
                && terminalOutput.Text?.Contains(
                    "audit-async-ok",
                    StringComparison.Ordinal) == true;
            AddResult(
                results,
                "get_terminal_output",
                outputWorks ? ToolAuditStatus.Works : ToolAuditStatus.Failed,
                outputWorks
                    ? "Consumed an async terminal job and returned its output."
                    : "Could not consume the async terminal job.",
                Combine(asyncStart.Text, terminalOutput.Text),
                asyncStart.DurationMs + terminalOutput.DurationMs);
        }

        private static async Task AuditEditingAsync(
            HeadlessToolRouter tools,
            ToolAuditWorkspace workspace,
            List<ToolAuditResult> results)
        {
            await RunToolAsync(
                tools,
                workspace,
                results,
                "create_directory",
                new { dirPath = "audit-fixtures/created-directory" },
                ToolAuditStatus.Works,
                _ => Directory.Exists(workspace.EditDirectoryPath),
                "Created a directory inside the workspace.");
            await RunToolAsync(
                tools,
                workspace,
                results,
                "create_file",
                new
                {
                    filePath = "audit-fixtures/AuditEdit.cs",
                    content = """
                        public static class AuditEdit
                        {
                            public const string Value = "replace-me";
                        }
                        """,
                },
                ToolAuditStatus.Works,
                _ => File.Exists(workspace.EditFilePath),
                "Created a source file.");
            await RunToolAsync(
                tools,
                workspace,
                results,
                "replace_string_in_file",
                new
                {
                    filePath = "audit-fixtures/AuditEdit.cs",
                    oldString = "\"replace-me\"",
                    newString = "\"replace-ok\"",
                },
                ToolAuditStatus.Works,
                _ => ReadAllText(workspace.EditFilePath)
                    .Contains("replace-ok", StringComparison.Ordinal),
                "Applied a single replacement.");
            await RunToolAsync(
                tools,
                workspace,
                results,
                "multi_replace_string_in_file",
                new
                {
                    replacements = new[]
                    {
                        new
                        {
                            filePath = "audit-fixtures/AuditEdit.cs",
                            oldString = "\"replace-ok\"",
                            newString = "\"multi-ok\"",
                        },
                    },
                },
                ToolAuditStatus.Works,
                _ => ReadAllText(workspace.EditFilePath)
                    .Contains("multi-ok", StringComparison.Ordinal),
                "Applied a batched replacement.");
            await RunToolAsync(
                tools,
                workspace,
                results,
                "apply_patch",
                new
                {
                    patch = """
                        *** Begin Patch
                        *** Update File: audit-fixtures/AuditEdit.cs
                        @@ public static class AuditEdit
                        -    public const string Value = "multi-ok";
                        +    public const string Value = "patch-ok";
                        *** End Patch
                        """,
                    expected = "@public static class AuditEdit",
                },
                ToolAuditStatus.Works,
                _ => ReadAllText(workspace.EditFilePath)
                    .Contains("patch-ok", StringComparison.Ordinal),
                "Applied a structured patch.");
            await RunToolAsync(
                tools,
                workspace,
                results,
                "delete_file",
                new { filePath = "audit-fixtures/AuditEdit.cs" },
                ToolAuditStatus.Works,
                _ => !File.Exists(workspace.EditFilePath),
                "Deleted the audit file without VS project services.");
        }

        private static async Task AuditBuildAndDiagnosticsAsync(
            HeadlessToolRouter tools,
            HeadlessBuildService buildService,
            ToolAuditWorkspace workspace,
            List<ToolAuditResult> results)
        {
            ToolExecution build = await ExecuteAsync(
                tools,
                workspace.Root,
                "build_solution",
                new { });
            bool buildExecuted = !string.IsNullOrWhiteSpace(build.Text);
            AddResult(
                results,
                "build_solution",
                buildExecuted ? ToolAuditStatus.HeadlessAdapted : ToolAuditStatus.Failed,
                buildExecuted
                    ? "Executed through dotnet/CMake CLI."
                    : "Build service returned no output.",
                build.Text,
                build.DurationMs);

            ToolExecution tests = await ExecuteAsync(
                tools,
                workspace.Root,
                "run_tests",
                new { });
            bool testsExecuted = !LooksLikeInfrastructureError(tests.Text);
            AddResult(
                results,
                "run_tests",
                testsExecuted ? ToolAuditStatus.HeadlessAdapted : ToolAuditStatus.Failed,
                testsExecuted
                    ? "Executed through dotnet test/runner or CTest."
                    : "Test service returned an infrastructure error.",
                tests.Text,
                tests.DurationMs);

            workspace.BreakPrimarySource();
            ToolExecution brokenBuild = await ExecuteAsync(
                tools,
                workspace.Root,
                "build_solution",
                new { });
            ToolExecution errors = await ExecuteAsync(
                tools,
                workspace.Root,
                "get_errors",
                new { });
            List<ErrorListItem> parsedErrors =
                await buildService.GetAllErrorsAsync(CancellationToken.None);
            bool errorsDetected = !LooksLikeInfrastructureError(errors.Text)
                && !string.IsNullOrWhiteSpace(errors.Text)
                && parsedErrors.Count > 0
                && !Regex.IsMatch(
                    errors.Text ?? string.Empty,
                    @"no errors|未检测到编译错误",
                    RegexOptions.IgnoreCase)
                && parsedErrors.Any(error =>
                    !string.IsNullOrWhiteSpace(error.FileName)
                    && errors.Text!.Contains(
                        Path.GetFileName(error.FileName),
                        StringComparison.OrdinalIgnoreCase));
            AddResult(
                results,
                "get_errors",
                errorsDetected
                    ? ToolAuditStatus.HeadlessAdapted
                    : ToolAuditStatus.Failed,
                errorsDetected
                    ? "Read CLI compiler errors after an intentional failure."
                    : "CLI compiler errors were not surfaced.",
                $"parsed={parsedErrors.Count} / tool={errors.Text} / build_tail={Tail(brokenBuild.Text, 1200)}",
                brokenBuild.DurationMs + errors.DurationMs);
        }

        private static async Task AuditInteractiveAsync(
            HeadlessToolRouter tools,
            ToolAuditWorkspace workspace,
            List<ToolAuditResult> results,
            Func<bool> handoffWasHandled)
        {
            await RunToolAsync(
                tools,
                workspace,
                results,
                "VisualStudio_askQuestions",
                new
                {
                    questions = new[]
                    {
                        new
                        {
                            header = "Audit",
                            question = "Can the question backend be reached?",
                            options = new[]
                            {
                                new { label = "Yes", description = "Audit option" },
                            },
                        },
                    },
                },
                ToolAuditStatus.HeadlessAdapted,
                text => !string.IsNullOrWhiteSpace(text),
                "Reached the headless question-interception contract.");
            await RunToolAsync(
                tools,
                workspace,
                results,
                "request_handoff",
                new
                {
                    targetAgent = "Edit",
                    reason = "Audit handoff transport.",
                    taskDescription = "Verify the handoff callback.",
                },
                ToolAuditStatus.Works,
                _ => handoffWasHandled(),
                "Invoked the handoff callback.");
            await RunToolAsync(
                tools,
                workspace,
                results,
                "runSubagent",
                new
                {
                    agentName = "Explore",
                    prompt = "Inspect the audit workspace.",
                    description = "Audit subagent transport.",
                },
                ToolAuditStatus.RequiresModel,
                text => text.Contains("audit subagent handler", StringComparison.Ordinal),
                "Handler dispatch works; a real ExploreAgent needs the model.");
        }

        private static void AddConditionalAndUnclassifiedResults(
            HeadlessToolRouter tools,
            List<ToolAuditResult> results)
        {
            var completed = new HashSet<string>(
                results.Select(result => result.Tool),
                StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<string> registered = tools.RegisteredToolNames;
            var registeredSet = new HashSet<string>(
                registered,
                StringComparer.OrdinalIgnoreCase);

            AddDisabledIfMissing(
                results,
                completed,
                registeredSet,
                "fetch_webpage",
                "Headless web-search backend is not configured.");
            AddDisabledIfMissing(
                results,
                completed,
                registeredSet,
                "web_search",
                "Web search is disabled by the headless profile.");
            AddDisabledIfMissing(
                results,
                completed,
                registeredSet,
                "memory",
                "Memory service is disabled by the headless profile.");

            if (registeredSet.Contains("capture_window"))
            {
                AddResult(
                    results,
                    "capture_window",
                    ToolAuditStatus.RequiresInteractiveDesktop,
                    "Capture code exists but requires an interactive Windows desktop.",
                    "Not invoked to avoid touching the user's foreground window.");
            }
            if (registeredSet.Contains("capture_webpage"))
            {
                AddResult(
                    results,
                    "capture_webpage",
                    ToolAuditStatus.RequiresInteractiveDesktop,
                    "Browser capture requires a desktop browser session.",
                    "Not invoked in the non-visual headless audit.");
            }

            foreach (string name in KnownTools)
            {
                if (!completed.Contains(name)
                    && !results.Any(result => string.Equals(
                        result.Tool,
                        name,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    AddResult(
                        results,
                        name,
                        ToolAuditStatus.Disabled,
                        "Tool is not registered in this headless configuration.",
                        registeredSet.Contains(name)
                            ? "Registered but not exercised by the audit."
                            : "Not present in BuiltInToolService.");
                }
            }

            foreach (string name in registered.Where(name => !KnownTools.Contains(
                name,
                StringComparer.OrdinalIgnoreCase)))
            {
                AddResult(
                    results,
                    name,
                    ToolAuditStatus.Failed,
                    "Registered tool has no audit classification.",
                    "Add it to HeadlessToolAudit.KnownTools.");
            }
        }

        private static void AddDisabledIfMissing(
            List<ToolAuditResult> results,
            HashSet<string> completed,
            HashSet<string> registered,
            string tool,
            string reason)
        {
            if (completed.Contains(tool) || registered.Contains(tool))
                return;
            AddResult(
                results,
                tool,
                ToolAuditStatus.Disabled,
                reason,
                "Not registered for this service instance.");
            completed.Add(tool);
        }

        private static async Task RunToolAsync(
            HeadlessToolRouter tools,
            ToolAuditWorkspace workspace,
            List<ToolAuditResult> results,
            string tool,
            object arguments,
            ToolAuditStatus successStatus,
            Func<string, bool> validate,
            string successSummary)
        {
            ToolExecution execution = await ExecuteAsync(
                tools,
                workspace.Root,
                tool,
                arguments);
            bool worked = !LooksLikeInfrastructureError(execution.Text)
                && validate(execution.Text ?? string.Empty);
            AddResult(
                results,
                tool,
                worked ? successStatus : ToolAuditStatus.Failed,
                worked
                    ? successSummary
                    : "Tool execution did not satisfy its smoke-test contract.",
                execution.Text,
                execution.DurationMs);
        }

        private static async Task<ToolExecution> ExecuteAsync(
            HeadlessToolRouter tools,
            string workspaceRoot,
            string tool,
            object arguments)
        {
            var stopwatch = Stopwatch.StartNew();
            string json = JsonSerializer.Serialize(arguments);
            string? result = await tools.ExecuteBuiltInToolAsync(
                tool,
                json,
                workspaceRoot,
                CancellationToken.None);
            stopwatch.Stop();
            return new ToolExecution
            {
                Text = result,
                DurationMs = stopwatch.ElapsedMilliseconds,
            };
        }

        private static bool LooksLikeInfrastructureError(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return true;
            return text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("Timeout:", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("[BLOCKED]", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddResult(
            List<ToolAuditResult> results,
            string tool,
            ToolAuditStatus status,
            string summary,
            string? evidence,
            long durationMs = 0)
        {
            results.RemoveAll(item => string.Equals(
                item.Tool,
                tool,
                StringComparison.OrdinalIgnoreCase));
            results.Add(
                new ToolAuditResult
                {
                    Tool = tool,
                    Status = status,
                    Summary = summary,
                    Evidence = TruncateEvidence(evidence),
                    DurationMs = durationMs,
                });
        }

        private static string Combine(params string?[] values)
        {
            return string.Join(
                " / ",
                values
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!.Trim()));
        }

        private static string TruncateEvidence(string? value)
        {
            const int maxLength = 1800;
            string text = (value ?? string.Empty)
                .Replace("\r\n", " ")
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
            return text.Length <= maxLength
                ? text
                : text.Substring(0, maxLength) + "...";
        }

        private static string Tail(string? value, int maxLength)
        {
            string text = (value ?? string.Empty)
                .Replace("\r\n", " ")
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
            return text.Length <= maxLength
                ? text
                : "..." + text.Substring(text.Length - maxLength);
        }

        private static string ReadAllText(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private sealed class ToolExecution
        {
            public string? Text { get; set; }
            public long DurationMs { get; set; }
        }

        private sealed class AuditOptions
        {
            public string CaseDirectory { get; private set; } = string.Empty;
            public string? OutputPath { get; private set; }

            public static AuditOptions Parse(string[] args)
            {
                var values = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < args.Length; index++)
                {
                    string key = args[index];
                    if (!key.StartsWith("--", StringComparison.Ordinal)
                        || index + 1 >= args.Length)
                    {
                        throw new ArgumentException(
                            "Usage: BenchmarkRunner --audit-tools "
                            + "--case-dir <workspace> [--output <json>]");
                    }
                    values[key.Substring(2)] = args[++index];
                }

                if (!values.TryGetValue("case-dir", out string? caseDirectory)
                    || string.IsNullOrWhiteSpace(caseDirectory))
                {
                    throw new ArgumentException("--case-dir is required.");
                }

                return new AuditOptions
                {
                    CaseDirectory = caseDirectory,
                    OutputPath = values.TryGetValue("output", out string? output)
                        ? output
                        : null,
                };
            }
        }
    }
}
