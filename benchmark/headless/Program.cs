using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.Agents;
using DeepSeek_v4_for_VisualStudio.Services.IdeContext;
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
                if (args.Length > 0
                    && string.Equals(
                        args[0],
                        "--audit-tools",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return await HeadlessToolAudit.RunAsync(
                        args.Length > 1
                            ? args.Skip(1).ToArray()
                            : Array.Empty<string>());
                }

                if (args.Length > 0
                    && string.Equals(
                        args[0],
                        "--self-test-toolchain",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return await HeadlessToolchainSelfTest.RunAsync(
                        args.Length > 1
                            ? args.Skip(1).ToArray()
                            : Array.Empty<string>());
                }

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
            HarnessProfile harnessProfile = HarnessProfile.Load(options.HarnessProfilePath);
            UserSimulationConfig userSimulation = UserSimulationConfig.FromCase(
                caseElement,
                options.MaxTurns);
            WorkspaceSnapshot initialSnapshot = WorkspaceSnapshot.Capture(caseDir);

            using var apiService = new DeepSeekApiService(
                apiKey,
                options.Model,
                requestTimeoutSeconds: 300,
                baseUrl: options.BaseUrl);
            apiService.ConfigureThinking(
                harnessProfile.ThinkingEnabled,
                harnessProfile.ThinkingEffort);
            apiService.PrefixCache = new PrefixCacheManager();

            var buildService = new HeadlessBuildService(caseDir);
            var testService = new HeadlessTestService(caseDir, buildService);
            using HeadlessToolProxyHost proxyHost =
                HeadlessToolProxyHost.Create(caseDir, buildService);
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
            builtInTools.CurrentSolutionPath = solutionPath;
            builtInTools.WebSearchEnabled = false;

            using var factory = new AgentFactory(
                apiService,
                builtInTools,
                proxyHost.Manager);
            using UserSimulator? userSimulator = userSimulation.Enabled
                ? new UserSimulator(
                    apiKey,
                    options.UserSimModel ?? options.Model,
                    options.BaseUrl)
                : null;
            var questionPolicy = new AgentQuestionPolicy(
                userSimulation,
                userSimulator);
            BindAgentCallbacks(factory, caseDir, questionPolicy);

            var contextManager = new ConversationContextManager
            {
                CurrentModel = options.Model,
            };
            contextManager.SetSolutionPath(solutionPath);
            var gitContextProvider = new GitContextProvider();
            string? gitContext = gitContextProvider.BuildPromptBlock(
                gitContextProvider.Capture(solutionPath));
            string ideContext =
                "[Headless benchmark]\n"
                + $"Workspace: {caseDir}\n"
                + $"Solution: {solutionPath}\n"
                + "The filesystem is the source of truth. Use tools to inspect and modify it.";
            if (!string.IsNullOrWhiteSpace(gitContext))
                ideContext += Environment.NewLine + Environment.NewLine + gitContext;
            contextManager.SetIdeContext(ideContext);

            var turnDocuments = new List<Dictionary<string, object?>>();
            var manifestTurns = new List<Dictionary<string, object?>>();
            int totalToolCalls = 0;
            long totalDurationMs = 0;
            AgentTaskPlan? carriedPlan = null;
            var sessionStarted = Stopwatch.StartNew();
            List<(int Turn, string Prompt)> fixedTurns = turns
                .EnumerateArray()
                .Select((turnElement, index) => (
                    turnElement.TryGetProperty("turn", out JsonElement turnNumber)
                        ? turnNumber.GetInt32()
                        : index + 1,
                    turnElement.TryGetProperty("user_prompt", out _)
                        ? GetRequiredString(turnElement, "user_prompt")
                        : GetRequiredString(turnElement, "prompt")))
                .ToList();
            var simulatedHistory = new List<(string User, string Assistant)>();
            int maxTurns = userSimulation.Enabled
                ? userSimulation.MaxTurns
                : fixedTurns.Count;

            for (int turnPosition = 0; turnPosition < maxTurns; turnPosition++)
            {
                int turnIndex = userSimulation.Enabled
                    ? turnPosition + 1
                    : fixedTurns[turnPosition].Turn;
                string? prompt;
                if (userSimulation.Enabled && turnPosition > 0)
                {
                    if (userSimulator == null)
                        throw new InvalidOperationException("User simulator was not initialized.");
                    prompt = await userSimulator.NextMessageAsync(
                        userSimulation,
                        simulatedHistory,
                        CancellationToken.None);
                    if (string.IsNullOrWhiteSpace(prompt))
                        break;
                }
                else
                {
                    prompt = turnPosition < fixedTurns.Count
                        ? fixedTurns[turnPosition].Prompt
                        : null;
                }

                if (string.IsNullOrWhiteSpace(prompt))
                    break;
                string turnPrompt = prompt!;

                List<ChatApiMessage> beforeTurn = contextManager.GetFullContext();
                long promptBefore = apiService.TotalPromptTokens;
                long completionBefore = apiService.TotalCompletionTokens;
                long hitBefore = apiService.TotalCacheHitTokens;
                long missBefore = apiService.TotalCacheMissTokens;
                var turnStopwatch = Stopwatch.StartNew();

                contextManager.AddUserMessage(turnPrompt);
                var context = new AgentContext
                {
                    SolutionPath = solutionPath,
                    ContextManager = contextManager,
                    CurrentUserContent = turnPrompt,
                    ConversationHistory = contextManager.GetConversationHistory(),
                    CancellationToken = CancellationToken.None,
                    PreClassifiedTaskSize = harnessProfile.PreClassifiedTaskSize,
                    ActivePlan = carriedPlan,
                    ReadFileAsync = ReadFileAsync,
                };

                BaseAgent activeAgent = factory.GetAgent(harnessProfile.InitialAgent);
                activeAgent.Context = context;
                questionPolicy.BeginTurn();
                Console.WriteLine($"[{caseId}] turn {turnIndex}: {turnPrompt}");
                AgentResult result = await activeAgent.ExecuteAsync(turnPrompt, context);
                if (questionPolicy.ShouldBlockHandoff)
                {
                    if (result.Handoff != null)
                    {
                        Console.WriteLine(
                            "  handoff blocked: clarification was not answered");
                    }
                }
                else if (harnessProfile.AutoFollowHandoffs)
                {
                    result = await FollowAutoHandoffsAsync(
                        activeAgent,
                        result,
                        context,
                        factory,
                        maxDepth: harnessProfile.MaxHandoffDepth);
                }
                else if (result.Handoff != null)
                {
                    Console.WriteLine(
                        $"  handoff paused by profile: {result.Handoff.TargetAgent}");
                }

                turnStopwatch.Stop();
                carriedPlan = result.Plan ?? context.ActivePlan;
                simulatedHistory.Add((turnPrompt, result.Content ?? string.Empty));
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
                    ["user_prompt"] = turnPrompt,
                });

                turnDocuments.Add(new Dictionary<string, object?>
                {
                    ["schema"] = "deepseek-vs.turn-evaluation/1",
                    ["export_id"] = $"headless-{caseId}-{turnIndex:000}",
                    ["exported_at_utc"] = DateTime.UtcNow.ToString("O"),
                    ["source"] = BuildSource(
                        caseDir,
                        solutionPath,
                        options.Model,
                        harnessProfile),
                    ["turn"] = new Dictionary<string, object?>
                    {
                        ["turn_index"] = turnIndex,
                        ["user_simulated"] = userSimulation.Enabled && turnIndex > 1,
                        ["clarification"] = questionPolicy.TurnSummary(),
                        ["user"] = new Dictionary<string, object?>
                        {
                            ["content"] = turnPrompt,
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
            WorkspaceSnapshot finalSnapshot = WorkspaceSnapshot.Capture(caseDir);
            WorkspaceSnapshotDiff workspaceDiff = initialSnapshot.Diff(finalSnapshot);
            var manifest = new Dictionary<string, object?>
            {
                ["schema"] = "deepseek-vs.session-evaluation/1",
                ["export_id"] = $"headless-{caseId}-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
                ["exported_at_utc"] = DateTime.UtcNow.ToString("O"),
                ["turn_count"] = turnDocuments.Count,
                ["source"] = BuildSource(
                    caseDir,
                    solutionPath,
                    options.Model,
                    harnessProfile),
                ["harness_profile"] = new
                {
                    id = harnessProfile.Id,
                    description = harnessProfile.Description,
                    initial_agent = harnessProfile.InitialAgent.ToString(),
                    auto_follow_handoffs = harnessProfile.AutoFollowHandoffs,
                    max_handoff_depth = harnessProfile.MaxHandoffDepth,
                    pre_classified_task_size = harnessProfile.PreClassifiedTaskSize.ToString(),
                },
                ["user_simulation"] = userSimulation.Enabled
                    ? new
                    {
                        enabled = true,
                        model = userSimulator?.Model,
                        max_turns = userSimulation.MaxTurns,
                    }
                    : null,
                ["question_policy"] = new
                {
                    mode = userSimulation.Enabled
                        ? "user_simulator"
                        : "skip_unanswered",
                    fixed_case_questions_are_never_invented = true,
                },
                ["workspace_snapshot"] = new
                {
                    schema = "deepseek-vs.workspace-snapshot/1",
                    file_count = finalSnapshot.FileCount,
                    changes = workspaceDiff,
                },
                ["execution_environment"] = ExecutionEnvironment.Describe(harnessProfile),
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
                    ["user_simulation_usage"] = userSimulator == null
                        ? null
                        : new
                        {
                            prompt_tokens = userSimulator.PromptTokens,
                            completion_tokens = userSimulator.CompletionTokens,
                            cache_hit_tokens = userSimulator.CacheHitTokens,
                            cache_miss_tokens = userSimulator.CacheMissTokens,
                            cache_hit_rate = userSimulator.CacheHitRate,
                        },
                },
            };

            string archivePath = Path.GetFullPath(options.ArchivePath);
            WriteSessionArchive(
                archivePath,
                manifest,
                turnDocuments,
                finalSnapshot,
                caseDir);
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

        private static void BindAgentCallbacks(
            AgentFactory factory,
            string workspaceRoot,
            AgentQuestionPolicy questionPolicy)
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
                    string answers = questionPolicy.Answer(request);
                    var summary = questionPolicy.TurnSummary();
                    Console.WriteLine(
                        $"  question policy: requests={summary["requests"]} "
                        + $"simulated={summary["simulated_answers"]} "
                        + $"skipped={summary["skipped_answers"]}");
                    captured.RespondToQuestions(
                        request.RequestId,
                        answers);
                };
            }
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
                "git status", "git diff", "git log", "git show",
                "git rev-parse", "git branch --show-current",
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
            if (result.StartsWith("用户跳过", StringComparison.OrdinalIgnoreCase)
                || result.StartsWith("User skipped", StringComparison.OrdinalIgnoreCase))
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
            string model,
            HarnessProfile harnessProfile)
        {
            return new Dictionary<string, object?>
            {
                ["extension_version"] = typeof(DeepSeekApiService).Assembly.GetName().Version?.ToString(),
                ["session_id"] = $"headless-{Path.GetFileName(caseDir)}",
                ["session_title"] = Path.GetFileName(caseDir),
                ["model"] = model,
                ["workspace_name"] = Path.GetFileName(caseDir),
                ["solution_path"] = solutionPath,
                ["harness_profile"] = harnessProfile.Id,
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
            List<Dictionary<string, object?>> turns,
            WorkspaceSnapshot workspaceSnapshot,
            string workspaceRoot)
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
            workspaceSnapshot.WriteToArchive(archive, workspaceRoot);
        }

        private static void WriteZipEntry(ZipArchive archive, string name, string content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        private sealed class RunnerOptions
        {
            public string CaseDirectory { get; private set; } = string.Empty;
            public string ArchivePath { get; private set; } = string.Empty;
            public string? TranscriptPath { get; private set; }
            public string Model { get; private set; } = "deepseek-flash";
            public string BaseUrl { get; private set; } = "https://api.deepseek.com";
            public string? HarnessProfilePath { get; private set; }
            public string? UserSimModel { get; private set; }
            public int? MaxTurns { get; private set; }

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
                    HarnessProfilePath = GetValue(values, "harness-profile"),
                    UserSimModel = GetValue(values, "user-sim-model"),
                    MaxTurns = ParseOptionalInteger(values, "max-turns"),
                };
            }

            private static string? GetValue(
                Dictionary<string, string> values,
                string key)
            {
                return values.TryGetValue(key, out string? value) ? value : null;
            }

            private static int? ParseOptionalInteger(
                Dictionary<string, string> values,
                string key)
            {
                string? value = GetValue(values, key);
                if (string.IsNullOrWhiteSpace(value))
                    return null;
                return int.TryParse(value, out int parsed)
                    ? parsed
                    : throw new ArgumentException($"--{key} must be an integer.");
            }
        }
    }
}
