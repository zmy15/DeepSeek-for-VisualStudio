using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.Agents;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BenchmarkRunner
{
    internal static class Program
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static async Task<int> Main(string[] args)
        {
            try
            {
                RegisterVisualStudioAssemblyResolver();
                var options = RunnerOptions.Parse(args);
                string apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
                    ?? Environment.GetEnvironmentVariable("JUDGE_API_KEY")
                    ?? throw new InvalidOperationException(
                        "Set DEEPSEEK_API_KEY or JUDGE_API_KEY before running the headless benchmark.");

                return await RunAsync(options, apiKey);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static void RegisterVisualStudioAssemblyResolver()
        {
            string ideRoot = Environment.GetEnvironmentVariable("VSINSTALLDIR")
                ?? @"D:\Visual Studio\IDE\Common7\IDE";
            string[] probeDirectories =
            {
                Path.Combine(ideRoot, "PublicAssemblies", "Microsoft.VisualStudio.Threading.17.x"),
                Path.Combine(ideRoot, "CommonExtensions", "Microsoft", "Editor"),
                Path.Combine(
                    ideRoot,
                    "CommonExtensions",
                    "Microsoft",
                    "VBCSharp",
                    "LanguageServices",
                    "Core"),
                ideRoot,
                Path.Combine(ideRoot, "PublicAssemblies"),
                Path.Combine(ideRoot, "PrivateAssemblies"),
                Path.Combine(ideRoot, "CommonExtensions", "Microsoft", "VSSDK"),
            };

            AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
            {
                string assemblyName = new System.Reflection.AssemblyName(
                    eventArgs.Name).Name;
                foreach (string directory in probeDirectories)
                {
                    string candidate = Path.Combine(directory, assemblyName + ".dll");
                    if (File.Exists(candidate))
                        return System.Reflection.Assembly.LoadFrom(candidate);
                }
                return null;
            };
        }

        private static async Task<int> RunAsync(RunnerOptions options, string apiKey)
        {
            string caseDir = Path.GetFullPath(options.CaseDirectory);
            string metadataPath = caseDir.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + ".benchmark-meta"
                + Path.DirectorySeparatorChar + "case.json";
            if (!File.Exists(metadataPath))
            {
                metadataPath = Path.Combine(caseDir, "_benchmark", "case.json");
            }
            if (!File.Exists(metadataPath))
                throw new FileNotFoundException("Prepared case metadata not found.", metadataPath);

            using JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
            JsonElement caseElement = metadata.RootElement.GetProperty("case");
            string caseId = GetRequiredString(caseElement, "id");
            JsonElement turns = caseElement.GetProperty("turns");
            string solutionPath = FindSolution(caseDir);

            using var apiService = new DeepSeekApiService(
                apiKey,
                options.Model,
                requestTimeoutSeconds: 300,
                baseUrl: options.BaseUrl);
            apiService.ConfigureThinking(false);

            var buildService = new WorkspaceBuildService(caseDir);
            var testService = new WorkspaceTestService(caseDir);
            var builtInTools = new BuiltInToolService(
                mcpManager: null,
                webSearchService: null,
                buildService: buildService,
                memoryService: null,
                skillService: null,
                testService: testService)
            {
                ApiService = apiService,
            };

            using var factory = new AgentFactory(apiService, builtInTools);
            BindAgentCallbacks(factory, caseDir);

            var contextManager = new ConversationContextManager
            {
                CurrentModel = options.Model,
            };
            contextManager.SetSolutionPath(solutionPath);
            contextManager.SetIdeContext(
                "[Headless benchmark]\n"
                + $"Workspace: {caseDir}\n"
                + $"Solution: {solutionPath}\n"
                + "The filesystem is the source of truth. Use tools to inspect and modify it.");

            var turnDocuments = new List<Dictionary<string, object?>>();
            var manifestTurns = new List<Dictionary<string, object?>>();
            int totalToolCalls = 0;
            long totalDurationMs = 0;
            AgentTaskPlan? carriedPlan = null;
            var sessionStarted = Stopwatch.StartNew();

            foreach (JsonElement turnElement in turns.EnumerateArray())
            {
                int turnIndex = turnElement.TryGetProperty("turn", out JsonElement turnNumber)
                    ? turnNumber.GetInt32()
                    : turnDocuments.Count + 1;
                string prompt = turnElement.TryGetProperty("user_prompt", out _)
                    ? GetRequiredString(turnElement, "user_prompt")
                    : GetRequiredString(turnElement, "prompt");
                List<ChatApiMessage> beforeTurn = contextManager.GetFullContext();
                long promptBefore = apiService.TotalPromptTokens;
                long completionBefore = apiService.TotalCompletionTokens;
                long hitBefore = apiService.TotalCacheHitTokens;
                long missBefore = apiService.TotalCacheMissTokens;
                var turnStopwatch = Stopwatch.StartNew();

                contextManager.AddUserMessage(prompt);
                var context = new AgentContext
                {
                    SolutionPath = solutionPath,
                    ContextManager = contextManager,
                    CurrentUserContent = prompt,
                    ConversationHistory = contextManager.GetConversationHistory(),
                    CancellationToken = CancellationToken.None,
                    PreClassifiedTaskSize = TaskSize.Small,
                    ActivePlan = carriedPlan,
                    ReadFileAsync = ReadFileAsync,
                };

                BaseAgent activeAgent = factory.GetAgent(AgentType.Ask);
                activeAgent.Context = context;
                Console.WriteLine($"[{caseId}] turn {turnIndex}: {prompt}");
                AgentResult result = await activeAgent.ExecuteAsync(prompt, context);
                result = await FollowAutoHandoffsAsync(
                    activeAgent,
                    result,
                    context,
                    factory,
                    maxDepth: 10);

                turnStopwatch.Stop();
                carriedPlan = result.Plan ?? context.ActivePlan;
                List<ChatApiMessage> afterTurn = contextManager.GetFullContext();
                List<ChatApiMessage> turnMessages = afterTurn
                    .Skip(beforeTurn.Count)
                    .ToList();
                List<Dictionary<string, object?>> toolTrace = BuildToolTrace(turnMessages);
                totalToolCalls += toolTrace.Count;
                totalDurationMs += turnStopwatch.ElapsedMilliseconds;

                long promptDelta = Math.Max(0, apiService.TotalPromptTokens - promptBefore);
                long completionDelta = Math.Max(0, apiService.TotalCompletionTokens - completionBefore);
                long hitDelta = Math.Max(0, apiService.TotalCacheHitTokens - hitBefore);
                long missDelta = Math.Max(0, apiService.TotalCacheMissTokens - missBefore);
                string turnFileName = $"turns/turn-{turnIndex:000}.json";
                manifestTurns.Add(new Dictionary<string, object?>
                {
                    ["turn_index"] = turnIndex,
                    ["turn_id"] = $"headless-{caseId}-{turnIndex:000}",
                    ["file"] = turnFileName,
                    ["user_prompt"] = prompt,
                });

                turnDocuments.Add(new Dictionary<string, object?>
                {
                    ["schema"] = "deepseek-vs.turn-evaluation/1",
                    ["export_id"] = $"headless-{caseId}-{turnIndex:000}",
                    ["exported_at_utc"] = DateTime.UtcNow.ToString("O"),
                    ["source"] = BuildSource(caseDir, solutionPath, options.Model),
                    ["turn"] = new Dictionary<string, object?>
                    {
                        ["turn_index"] = turnIndex,
                        ["user"] = new Dictionary<string, object?>
                        {
                            ["content"] = prompt,
                            ["attachments"] = Array.Empty<object>(),
                        },
                        ["assistant"] = new Dictionary<string, object?>
                        {
                            ["content"] = result.Content,
                            ["reasoning_content"] = result.ReasoningContent,
                            ["incomplete"] = !result.Success,
                            ["error_message"] = result.ErrorMessage,
                            ["changed_files"] = result.FileChanges.Select(file => new
                            {
                                file_path = file.FilePath,
                                lines_added = file.LinesAdded,
                                lines_removed = file.LinesRemoved,
                                description = file.BriefDescription,
                            }).ToArray(),
                            ["handoff"] = result.Handoff == null ? null : new
                            {
                                target_agent = result.Handoff.TargetAgent.ToString(),
                                label = result.Handoff.Label,
                                auto_send = result.Handoff.AutoSend,
                            },
                        },
                        ["branch"] = new Dictionary<string, object?>
                        {
                            ["id"] = $"headless-{caseId}",
                            ["title"] = caseElement.TryGetProperty("title", out JsonElement title)
                                ? title.GetString()
                                : caseId,
                        },
                    },
                    ["conversation"] = new Dictionary<string, object?>
                    {
                        ["full_api_history"] = afterTurn,
                        ["before_turn"] = beforeTurn,
                        ["turn_messages"] = turnMessages,
                        ["tool_trace"] = toolTrace,
                        ["context_debug"] = new
                        {
                            estimated_tokens = contextManager.GetStats().EstimatedTokens,
                            message_count = afterTurn.Count,
                        },
                    },
                    ["metrics"] = new Dictionary<string, object?>
                    {
                        ["scope"] = "latest_execution",
                        ["duration_ms"] = turnStopwatch.ElapsedMilliseconds,
                        ["prompt_tokens"] = promptDelta,
                        ["completion_tokens"] = completionDelta,
                        ["input_tokens"] = promptDelta,
                        ["output_tokens"] = completionDelta,
                        ["cache_hit_tokens"] = hitDelta,
                        ["cache_miss_tokens"] = missDelta,
                        ["terminated_reason"] = result.Success ? "completed" : "error",
                    },
                    ["judge_contract"] = new
                    {
                        schema = "deepseek-vs.judge-contract/1",
                        environment = "headless-existing-agent-services",
                    },
                });
            }

            sessionStarted.Stop();
            var manifest = new Dictionary<string, object?>
            {
                ["schema"] = "deepseek-vs.session-evaluation/1",
                ["export_id"] = $"headless-{caseId}-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
                ["exported_at_utc"] = DateTime.UtcNow.ToString("O"),
                ["turn_count"] = turnDocuments.Count,
                ["source"] = BuildSource(caseDir, solutionPath, options.Model),
                ["turns"] = manifestTurns,
                ["session_telemetry"] = new Dictionary<string, object?>
                {
                    ["turn_count"] = turnDocuments.Count,
                    ["user_turn_count"] = turnDocuments.Count,
                    ["model_turn_count"] = CountModelTurns(contextManager.GetFullContext()),
                    ["tool_call_count"] = totalToolCalls,
                    ["input_tokens"] = apiService.TotalPromptTokens,
                    ["output_tokens"] = apiService.TotalCompletionTokens,
                    ["cache_hit_tokens"] = apiService.TotalCacheHitTokens,
                    ["cache_miss_tokens"] = apiService.TotalCacheMissTokens,
                    ["cache_hit_rate"] = apiService.TotalCacheHitRate,
                    ["duration_ms"] = sessionStarted.ElapsedMilliseconds,
                },
            };

            string archivePath = Path.GetFullPath(options.ArchivePath);
            WriteSessionArchive(archivePath, manifest, turnDocuments);
            if (!string.IsNullOrWhiteSpace(options.TranscriptPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.TranscriptPath))!);
                File.WriteAllText(
                    Path.GetFullPath(options.TranscriptPath),
                    JsonSerializer.Serialize(new
                    {
                        schema = "deepseek-vs.headless-transcript/1",
                        case_id = caseId,
                        model = options.Model,
                        manifest,
                        turns = turnDocuments,
                    }, JsonOptions),
                    new UTF8Encoding(false));
            }

            Console.WriteLine(
                $"[{caseId}] completed: turns={turnDocuments.Count}, "
                + $"tools={totalToolCalls}, duration_ms={totalDurationMs}");
            Console.WriteLine($"Archive: {archivePath}");
            return 0;
        }

        private static async Task<AgentResult> FollowAutoHandoffsAsync(
            BaseAgent sourceAgent,
            AgentResult result,
            AgentContext context,
            AgentFactory factory,
            int maxDepth)
        {
            BaseAgent currentAgent = sourceAgent;
            AgentTaskPlan? activePlan = result.Plan ?? context.ActivePlan;
            for (int depth = 0; result.Handoff != null && depth < maxDepth; depth++)
            {
                AgentHandoff handoff = result.Handoff;
                if (!handoff.AutoSend || handoff.ShowContinueOn)
                {
                    Console.WriteLine($"  handoff paused: {handoff.TargetAgent} ({handoff.Label})");
                    break;
                }

                Console.WriteLine($"  handoff: {currentAgent.Definition.Type} -> {handoff.TargetAgent}");
                currentAgent = factory.GetAgent(handoff.TargetAgent);
                currentAgent.Context = context;
                result = await currentAgent.ExecuteHandoffAsync(
                    handoff,
                    context,
                    activePlan,
                    factory);
                activePlan = result.Plan ?? context.ActivePlan;
            }

            return result;
        }

        private static void BindAgentCallbacks(AgentFactory factory, string workspaceRoot)
        {
            BaseAgent[] agents =
            {
                factory.AskAgent,
                factory.ExploreAgent,
                factory.PlanAgent,
                factory.EditAgent,
                factory.BuildAgent,
            };

            foreach (BaseAgent agent in agents)
            {
                BaseAgent captured = agent;
                captured.PermissionRequested += request =>
                {
                    bool approved = IsSafePermission(request, workspaceRoot);
                    Console.WriteLine(
                        $"  permission {(approved ? "approved" : "denied")}: "
                        + $"{request.ActionType} {request.Title}");
                    captured.RespondToPermission(request.RequestId, approved);
                };
                captured.QuestionsRequested += request =>
                {
                    Console.WriteLine($"  question auto-answer: {request.RequestId}");
                    captured.RespondToQuestions(
                        request.RequestId,
                        BuildQuestionAnswers(request));
                };
            }
        }

        private static string BuildQuestionAnswers(AgentQuestionRequest request)
        {
            var answers = new List<Dictionary<string, object?>>();
            foreach (AgentQuestion question in request.Questions)
            {
                QuestionOption? firstOption = question.Options?.FirstOrDefault();
                answers.Add(new Dictionary<string, object?>
                {
                    ["selectedOptions"] = firstOption == null
                        ? Array.Empty<string>()
                        : new[] { firstOption.Label },
                    ["freeText"] = firstOption == null
                        ? "按当前任务和已给出的约束继续执行。"
                        : string.Empty,
                    ["skipped"] = false,
                });
            }
            return JsonSerializer.Serialize(answers, JsonOptions);
        }

        private static bool IsSafePermission(
            AgentPermissionRequest request,
            string workspaceRoot)
        {
            if (string.Equals(request.ActionType, "file_write", StringComparison.OrdinalIgnoreCase))
            {
                if (request.FilePaths.Count > 0)
                {
                    return request.FilePaths.All(path =>
                        IsWithinWorkspace(path, workspaceRoot));
                }

                string evidence = (request.Command ?? string.Empty)
                    + "\n"
                    + (request.Detail ?? string.Empty);
                System.Text.RegularExpressions.MatchCollection absolutePaths =
                    System.Text.RegularExpressions.Regex.Matches(
                        evidence,
                        @"[A-Za-z]:\\[^""'\r\n;]+");
                return absolutePaths
                    .Cast<System.Text.RegularExpressions.Match>()
                    .All(match => IsWithinWorkspace(match.Value.Trim(), workspaceRoot));
            }

            if (string.Equals(request.ActionType, "web_access", StringComparison.OrdinalIgnoreCase))
                return false;

            if (string.Equals(request.ActionType, "terminal_command", StringComparison.OrdinalIgnoreCase))
                return IsSafeTerminalCommand(request.Command);

            return false;
        }

        private static bool IsSafeTerminalCommand(string command)
        {
            string value = (command ?? string.Empty).Trim();
            if (value.Length == 0)
                return false;

            string[] deniedTokens =
            {
                "remove-item", "rm -rf", " rm ", "del ", "rmdir", "format ",
                "shutdown", "restart-computer", "set-executionpolicy",
                "git push", "git reset", "git clean", "git checkout", "git switch",
                "invoke-webrequest", "curl ", "wget ",
            };
            if (deniedTokens.Any(token =>
                value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return false;
            }

            string[] allowedPrefixes =
            {
                "dotnet build", "dotnet run", "dotnet test",
                "cmake -s", "cmake --build", "ctest ",
                "git status", "git diff",
                "get-childitem", "test-path", "rg ", "findstr ", "where.exe ",
            };
            return allowedPrefixes.Any(prefix =>
                value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsWithinWorkspace(string path, string workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            string fullPath = Path.GetFullPath(path);
            string root = Path.GetFullPath(workspaceRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    fullPath.TrimEnd(Path.DirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }

        private static List<Dictionary<string, object?>> BuildToolTrace(
            List<ChatApiMessage> turnMessages)
        {
            var trace = new List<Dictionary<string, object?>>();
            foreach (ChatApiMessage message in turnMessages)
            {
                if (!string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    || message.ToolCalls == null)
                {
                    continue;
                }

                foreach (ToolCall call in message.ToolCalls)
                {
                    ChatApiMessage? resultMessage = turnMessages.FirstOrDefault(item =>
                        string.Equals(item.Role, "tool", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(item.ToolCallId, call.Id, StringComparison.Ordinal));
                    string result = resultMessage?.Content ?? string.Empty;
                    trace.Add(new Dictionary<string, object?>
                    {
                        ["tool_name"] = call.Function.Name,
                        ["arguments"] = call.Function.Arguments,
                        ["result"] = result,
                        ["result_classification"] = ClassifyToolResult(result),
                    });
                }
            }
            return trace;
        }

        private static string ClassifyToolResult(string result)
        {
            if (result.StartsWith("[BLOCKED]", StringComparison.OrdinalIgnoreCase))
                return "Blocked";
            if (result.StartsWith("Timeout:", StringComparison.OrdinalIgnoreCase))
                return "Timeout";
            if (result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                return "ToolError";
            return "Success";
        }

        private static int CountModelTurns(List<ChatApiMessage> messages)
        {
            return messages.Count(message =>
                string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, object?> BuildSource(
            string caseDir,
            string solutionPath,
            string model)
        {
            return new Dictionary<string, object?>
            {
                ["extension_version"] = typeof(DeepSeekApiService).Assembly.GetName().Version?.ToString(),
                ["session_id"] = $"headless-{Path.GetFileName(caseDir)}",
                ["session_title"] = Path.GetFileName(caseDir),
                ["model"] = model,
                ["workspace_name"] = Path.GetFileName(caseDir),
                ["solution_path"] = solutionPath,
            };
        }

        private static Task<string?> ReadFileAsync(string path)
        {
            if (!File.Exists(path))
                return Task.FromResult<string?>(null);
            return Task.Run(() => (string?)File.ReadAllText(path));
        }

        private static string FindSolution(string caseDir)
        {
            string[] candidates = Directory
                .GetFiles(caseDir, "*.slnx", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetFiles(caseDir, "*.sln", SearchOption.TopDirectoryOnly))
                .Concat(Directory.GetFiles(caseDir, "CMakeLists.txt", SearchOption.TopDirectoryOnly))
                .ToArray();
            if (candidates.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Expected exactly one solution file in {caseDir}, found {candidates.Length}.");
            }
            return candidates[0];
        }

        private static string GetRequiredString(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out JsonElement value)
                || string.IsNullOrWhiteSpace(value.GetString()))
            {
                throw new InvalidOperationException($"Missing required string '{property}'.");
            }
            return value.GetString()!;
        }

        private static void WriteSessionArchive(
            string archivePath,
            Dictionary<string, object?> manifest,
            List<Dictionary<string, object?>> turns)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            if (File.Exists(archivePath))
                File.Delete(archivePath);

            using FileStream stream = File.Create(archivePath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            WriteZipEntry(archive, "manifest.json", JsonSerializer.Serialize(manifest, JsonOptions));
            for (int index = 0; index < turns.Count; index++)
            {
                WriteZipEntry(
                    archive,
                    $"turns/turn-{index + 1:000}.json",
                    JsonSerializer.Serialize(turns[index], JsonOptions));
            }
        }

        private static void WriteZipEntry(ZipArchive archive, string name, string content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        private sealed class WorkspaceBuildService : IBuildService
        {
            private readonly string _workspaceRoot;
            private readonly bool _isCmake;

            public WorkspaceBuildService(string workspaceRoot)
            {
                _workspaceRoot = workspaceRoot;
                _isCmake = File.Exists(Path.Combine(workspaceRoot, "CMakeLists.txt"));
            }

            public async Task<string> BuildAsync(
                string? solutionPath,
                CancellationToken ct)
            {
                string target = solutionPath ?? _workspaceRoot;
                if (_isCmake
                    || string.Equals(
                        Path.GetFileName(target),
                        "CMakeLists.txt",
                        StringComparison.OrdinalIgnoreCase))
                {
                    string buildDir = Path.Combine(_workspaceRoot, "build");
                    ProcessResult configure = await ProcessRunner.RunAsync(
                        "cmake",
                        $"-S \"{_workspaceRoot}\" -B \"{buildDir}\" -G Ninja -DCMAKE_BUILD_TYPE=Debug",
                        _workspaceRoot,
                        ct);
                    if (configure.ExitCode != 0)
                    {
                        return "Error: CMake configure FAILED."
                            + Environment.NewLine
                            + configure.Output;
                    }

                    ProcessResult build = await ProcessRunner.RunAsync(
                        "cmake",
                        $"--build \"{buildDir}\"",
                        _workspaceRoot,
                        ct);
                    string cmakeSummary = build.ExitCode == 0
                        ? "Build succeeded."
                        : "Error: Build FAILED.";
                    return cmakeSummary + Environment.NewLine + build.Output;
                }

                ProcessResult result = await ProcessRunner.RunAsync(
                    "dotnet",
                    $"build \"{target}\" --nologo",
                    _workspaceRoot,
                    ct);
                string summary = result.ExitCode == 0
                    ? "Build succeeded."
                    : "Error: Build FAILED.";
                return summary + Environment.NewLine + result.Output;
            }

            public Task<List<ErrorListItem>> GetSelectedErrorsAsync(CancellationToken ct)
            {
                return Task.FromResult(new List<ErrorListItem>());
            }

            public Task<List<ErrorListItem>> GetAllErrorsAsync(CancellationToken ct)
            {
                return Task.FromResult(new List<ErrorListItem>());
            }
        }

        private sealed class WorkspaceTestService : ITestService
        {
            private readonly string _workspaceRoot;
            private readonly bool _isCmake;
            private readonly string? _dotNetTestProject;

            public WorkspaceTestService(string workspaceRoot)
            {
                _workspaceRoot = workspaceRoot;
                _isCmake = File.Exists(Path.Combine(workspaceRoot, "CMakeLists.txt"));
                _dotNetTestProject = Directory
                    .GetFiles(workspaceRoot, "*.csproj", SearchOption.AllDirectories)
                    .FirstOrDefault(path =>
                        path.Replace('\\', '/').EndsWith(
                            "tests/OrderDesk.Tests/OrderDesk.Tests.csproj",
                            StringComparison.OrdinalIgnoreCase));
            }

            public IReadOnlyList<string> FindTestProjects(string? solutionPath)
            {
                return _dotNetTestProject == null
                    ? Array.Empty<string>()
                    : new[] { _dotNetTestProject };
            }

            public IReadOnlyList<string> FindCppTestProjects(string? solutionPath)
            {
                return _isCmake
                    ? new[] { _workspaceRoot }
                    : Array.Empty<string>();
            }

            public async Task<TestRunResult> RunTestsAsync(
                string? solutionPath,
                string? projectPath,
                string? filter,
                string? configuration,
                CancellationToken ct)
            {
                if (_isCmake)
                {
                    string buildDir = Path.Combine(_workspaceRoot, "build");
                    ProcessResult ctest = await ProcessRunner.RunAsync(
                        "ctest",
                        $"--test-dir \"{buildDir}\" -C Debug --output-on-failure",
                        _workspaceRoot,
                        ct);
                    var cppResult = new TestRunResult
                    {
                        Error = TestRunErrorKind.None,
                        Kind = TestProjectKind.Cpp,
                        ProjectPath = _workspaceRoot,
                        Filter = filter ?? string.Empty,
                        Total = 1,
                        Passed = ctest.ExitCode == 0 ? 1 : 0,
                        Failed = ctest.ExitCode == 0 ? 0 : 1,
                        OutputTail = ctest.Output,
                    };
                    System.Text.RegularExpressions.Match ctestCount =
                        System.Text.RegularExpressions.Regex.Match(
                            ctest.Output,
                            @"out of\s+(\d+)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (ctestCount.Success)
                    {
                        cppResult.Total = int.Parse(ctestCount.Groups[1].Value);
                        cppResult.Failed = ctest.ExitCode == 0 ? 0 : 1;
                        cppResult.Passed = cppResult.Total - cppResult.Failed;
                    }
                    if (ctest.ExitCode != 0)
                    {
                        cppResult.FailedTests.Add(new FailedTestInfo
                        {
                            Name = "line_controller_tests",
                            Message = ctest.Output,
                        });
                    }
                    return cppResult;
                }

                if (_dotNetTestProject == null)
                {
                    return new TestRunResult
                    {
                        Error = TestRunErrorKind.NoTestProject,
                        Kind = TestProjectKind.DotNet,
                    };
                }

                string project = string.IsNullOrWhiteSpace(projectPath)
                    ? _dotNetTestProject
                    : projectPath!;
                ProcessResult process = await ProcessRunner.RunAsync(
                    "dotnet",
                    $"run --project \"{project}\"",
                    _workspaceRoot,
                    ct);
                var result = new TestRunResult
                {
                    Error = TestRunErrorKind.None,
                    Kind = TestProjectKind.DotNet,
                    ProjectPath = project,
                    Filter = filter ?? string.Empty,
                    Total = 1,
                    Passed = process.ExitCode == 0 ? 1 : 0,
                    Failed = process.ExitCode == 0 ? 0 : 1,
                    OutputTail = process.Output,
                };
                System.Text.RegularExpressions.Match countMatch =
                    System.Text.RegularExpressions.Regex.Match(
                        process.Output,
                        @"(\d+)\s*/\s*(\d+)\s+checks passed",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (countMatch.Success)
                {
                    result.Passed = int.Parse(countMatch.Groups[1].Value);
                    result.Total = int.Parse(countMatch.Groups[2].Value);
                    result.Failed = Math.Max(0, result.Total - result.Passed);
                }
                if (process.ExitCode != 0)
                {
                    result.FailedTests.Add(new FailedTestInfo
                    {
                        Name = "OrderDesk.Tests.Program",
                        Message = process.Output,
                    });
                }
                return result;
            }
        }

        private sealed class ProcessResult
        {
            public int ExitCode { get; set; }

            public string Output { get; set; } = string.Empty;
        }

        private static class ProcessRunner
        {
            public static async Task<ProcessResult> RunAsync(
                string fileName,
                string arguments,
                string workingDirectory,
                CancellationToken ct)
            {
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
                while (!process.HasExited)
                {
                    if (ct.IsCancellationRequested)
                    {
                        try { process.Kill(); } catch { }
                        ct.ThrowIfCancellationRequested();
                    }
                    await Task.Delay(100);
                }

                string output = (await stdout) + Environment.NewLine + (await stderr);
                return new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    Output = output.Trim(),
                };
            }
        }

        private sealed class RunnerOptions
        {
            public string CaseDirectory { get; private set; } = string.Empty;
            public string ArchivePath { get; private set; } = string.Empty;
            public string? TranscriptPath { get; private set; }
            public string Model { get; private set; } = "deepseek-flash";
            public string BaseUrl { get; private set; } = "https://api.deepseek.com";

            public static RunnerOptions Parse(string[] args)
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < args.Length; index++)
                {
                    string arg = args[index];
                    if (!arg.StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"Unexpected argument: {arg}");
                    if (index + 1 >= args.Length)
                        throw new ArgumentException($"Missing value for {arg}");
                    values[arg.Substring(2)] = args[++index];
                }

                if (!values.TryGetValue("case-dir", out string? caseDir))
                    throw new ArgumentException("--case-dir is required.");
                if (!values.TryGetValue("archive", out string? archive))
                    throw new ArgumentException("--archive is required.");

                return new RunnerOptions
                {
                    CaseDirectory = caseDir,
                    ArchivePath = archive,
                    TranscriptPath = GetValue(values, "transcript"),
                    Model = GetValue(values, "model") ?? "deepseek-flash",
                    BaseUrl = GetValue(values, "base-url") ?? "https://api.deepseek.com",
                };
            }

            private static string? GetValue(
                Dictionary<string, string> values,
                string key)
            {
                return values.TryGetValue(key, out string? value) ? value : null;
            }
        }
    }
}
