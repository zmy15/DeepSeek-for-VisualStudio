using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using DeepSeek_v4_for_VisualStudio.Utils;
using DeepSeek_v4_for_VisualStudio.View;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// <see cref="BaseAgent"/> 的 工具派发 分部实现。
    /// 承载该职责域的成员，使主文件聚焦于 Agent 执行编排。
    /// </summary>
    public abstract partial class BaseAgent
    {
        /// <summary>
        /// 执行单个工具调用（优先内置工具，其次 MCP 工具）。
        /// 对于 run_in_terminal 命令，需要用户审批后才能执行。
        /// </summary>
        private async Task<string> ExecuteToolAsync(string toolName, string argumentsJson, string? workspaceRoot, CancellationToken ct)
        {
            toolName = NormalizeToolName(toolName);

            if (BuiltInTools != null)
                BuiltInTools.CurrentSolutionPath = Context?.SolutionPath;

            // ── OCR 参数预处理：将文件路径自动转为 base64 ──
            argumentsJson = DeepSeekChatControl.SanitizeOcrToolArguments(toolName, argumentsJson);

            // ── 注入 ExploreHandler 到 BuiltInToolService（桥接 Agent.ExploreAgent）──
            //  修复：确保即使 BaseAgent.ExploreAgent 为 null 也能找到 ExploreAgent。
            // 场景：AskAgent 的 ExploreAgent 可能由 AgentFactory 通过不同类型引用设置。
            var effectiveExploreAgent = ExploreAgent
                ?? (this as AskAgent)?.ExploreAgent
                ?? (this as EditAgent)?.ExploreAgent
                ?? (this as PlanAgent)?.ExploreAgent
                ?? (this as BuildAgent)?.ExploreAgent;

            if (BuiltInTools != null && effectiveExploreAgent != null)
            {
                if (ExploreAgent == null)
                {
                    // 回退：将派生类的 ExploreAgent 同步到基类属性
                    ExploreAgent = effectiveExploreAgent;
                    Logger.Info($"[Agent:{Definition.Name}] 从派生类回退同步 ExploreAgent 到基类");
                }
                BuiltInTools.ExploreHandler = async (ctx) =>
                {
                    string traceId = string.IsNullOrWhiteSpace(ctx.TraceId)
                        ? Guid.NewGuid().ToString("N").Substring(0, 8)
                        : ctx.TraceId;
                    int configuredSubagentTimeoutSeconds =
                        Settings.DeepSeekOptionsPage.Instance?.AgentSubagentTimeoutSeconds ?? 900;
                    TimeSpan watchdogTimeout =
                        ResolveSubagentWatchdogTimeout(configuredSubagentTimeoutSeconds);
                    using var subagentCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    if (watchdogTimeout != Timeout.InfiniteTimeSpan)
                        subagentCts.CancelAfter(watchdogTimeout);
                    ExploreAgent? activeExploreAgent = null;

                    try
                    {
                        // ──  子Agent缓存优化：消费父Agent保存的ForwardedMessages，
                        //     使ExploreAgent复用父Agent的API缓存前缀。──
                        ctx.ForwardedMessages = Context?.ForwardedMessages;
                        if (Context != null)
                            Context.ForwardedMessages = null; // 消费后清空

                        // 每次子代理执行使用独立实例，避免并行任务共享 ExploreAgent.Context。
                        var exploreAgent = new ExploreAgent(_apiService)
                        {
                            BuiltInTools = BuiltInTools ?? effectiveExploreAgent.BuiltInTools,
                            McpManager = McpManager ?? effectiveExploreAgent.McpManager,
                        };
                        activeExploreAgent = exploreAgent;

                        // 并行子代理使用独立 ExploreAgent 实例，必须显式桥接交互事件；
                        // 否则 run_in_terminal 等审批会停在子代理内部，父 Agent/UI 永远收不到。
                        exploreAgent.PermissionRequested += OnExplorePermissionRequested;
                        exploreAgent.QuestionsRequested += OnExploreQuestionsRequested;

                        // ── 构建 ExploreAgent 上下文 ──
                        // 为 ExploreAgent 创建 FileReadCache 副本，避免共享同一实例导致
                        // "集合已修改；可能无法执行枚举操作" 异常。
                        var exploreFileCache = Context?.FileReadCache != null
                            ? new Dictionary<string, string>(Context.FileReadCache, StringComparer.OrdinalIgnoreCase)
                            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                        var exploreCtx = new AgentContext
                        {
                            SolutionPath = ctx.WorkspaceRoot ?? Context?.SolutionPath,
                            CancellationToken = subagentCts.Token,
                            // 子代理内部消息必须与父对话隔离。共享 ContextManager 会把
                            // Explore 的 assistant/tool 链插入父 assistant(tool_calls) 与
                            // 对应 tool 结果之间，破坏 API 要求的严格配对顺序。
                            ContextManager = new ConversationContextManager(),
                            ExecutionDepth = (Context?.ExecutionDepth ?? 0) + 1,
                            FileReadCache = exploreFileCache,
                            //  子Agent缓存优化：继承父Agent当前消息列表作为前缀，
                            //    使ExploreAgent的首轮API调用可复用父Agent的缓存前缀。
                            ForwardedMessages = ctx.ForwardedMessages,
                            //  子Agent派发属于跨 Agent 边界，允许复用移交前缀。
                            AllowForwardedMessageReuse = ctx.ForwardedMessages is { Count: > 0 },
                        };
                        exploreAgent.Context = exploreCtx;

                        AddLog("INFO", $"[{Definition.Name}][{traceId}] → ExploreAgent: {ctx.Description}");
                        Logger.Info($"[Subagent:{traceId}] 开始: {ctx.Description.Truncate(120)}");

                        // ── 并行子代理限流：获取信号量，限制同时运行的 Explore 子代理数量 ──
                        bool limiterAcquired = false;

                        // ── 不再通过 LogEntryAdded 事件转发 Explore 日志（并行 runSubagent 会导致重复订阅）
                        //    改为在 Explore 完成后从 exploreResult.Logs 批量导入。 ──
                        AgentResult? exploreResult = null;
                        try
                        {
                            await SubagentConcurrencyLimiter.WaitAsync(subagentCts.Token);
                            limiterAcquired = true;

                            // ── 获取信号量后再次检查取消（防止在排队等待期间用户点了停止）──
                            subagentCts.Token.ThrowIfCancellationRequested();

                            var executionTask = exploreAgent.ExecuteAsync(ctx.Prompt, exploreCtx);
                            if (watchdogTimeout != Timeout.InfiniteTimeSpan)
                            {
                                var watchdogTask = Task.Delay(watchdogTimeout, CancellationToken.None);
                                var completed = await Task.WhenAny(executionTask, watchdogTask).ConfigureAwait(false);
                                if (completed != executionTask)
                                {
                                    subagentCts.Cancel();
                                    string timeoutMessage = LocalizationService.Instance.Format(
                                        "agent.subagentWatchdogTimeout",
                                        watchdogTimeout.TotalMinutes.ToString("F0"), exploreAgent.CurrentActivity);
                                    Logger.Warn($"[Subagent:{traceId}] {timeoutMessage}; task={ctx.Description.Truncate(120)}");
                                    AddLog("WARN", $"[{Definition.Type}][{traceId}] {timeoutMessage}");
                                    _ = ObserveSubagentCompletionAsync(executionTask, traceId);
                                    return LocalizationService.Instance.Format("agent.subagentWatchdogRetry", timeoutMessage);
                                }
                            }

                            exploreResult = await executionTask.ConfigureAwait(false);
                        }
                        finally
                        {
                            if (limiterAcquired)
                                SubagentConcurrencyLimiter.Release();
                            // ── 导入 ExploreAgent 的日志到父 Agent 的 _logs ──
                            if (exploreResult != null && exploreResult.Logs.Count > 0)
                            {
                                foreach (var log in exploreResult.Logs)
                                {
                                    _logs.Add(new AgentLogEntry
                                    {
                                        Level = log.Level,
                                        Message = $"[Explore] {log.Message}"
                                    });
                                }
                                // ── 通过 AddLog 触发 LogEntryAdded 事件，使 UI 实时更新执行进度 ──
                                //     修复：之前仅用 _logs.Add() 导入日志，不触发事件，
                                //     导致 @agent 路由时 UI 思考面板不显示 ExploreAgent 执行流程。
                                AddLog("INFO", "[Explore] " + LocalizationService.Instance.Format(
                                    "agent.log.exploreDoneWithLogs",
                                    exploreResult.Logs.Count, exploreResult.Content?.Length ?? 0));
                                Logger.Info($"[{Definition.Name}][Explore] ExploreAgent 完成: {exploreResult.Logs.Count} 条日志, {exploreResult.Content?.Length ?? 0} 字符结果");
                            }
                        }

                        // ── 回收 ExploreAgent 的缓存到当前 Context ──
                        if (Context != null && exploreResult.Success)
                        {
                            // 文件读缓存
                            if (exploreCtx.FileReadCache.Count > 0)
                            {
                                foreach (var kvp in exploreCtx.FileReadCache)
                                {
                                    Context.FileReadCache[kvp.Key] = kvp.Value;
                                }
                            }
                        }

                        if (exploreResult.Success && !string.IsNullOrEmpty(exploreResult.Content))
                        {
                            Logger.Info($"[Subagent:{traceId}] 完成: {exploreResult.Content!.Length} 字符");
                            AddLog("INFO", $"[{Definition.Name}][{traceId}] ExploreAgent 完成: {exploreResult.Content!.Length} 字符");
                            return exploreResult.Content;
                        }

                        return exploreResult.Success
                            ? LocalizationService.Instance["agent.exploreNoContent"]
                            : LocalizationService.Instance.Format("agent.exploreFailed",
                                exploreResult.ErrorMessage ?? LocalizationService.Instance["agent.unknownError"]);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested && subagentCts.IsCancellationRequested)
                    {
                        string timeoutMessage = LocalizationService.Instance.Format(
                            "agent.subagentWatchdogTimeout",
                            watchdogTimeout.TotalMinutes.ToString("F0"),
                            activeExploreAgent?.CurrentActivity ?? LocalizationService.Instance["agent.subagentNotStarted"]);
                        Logger.Warn($"[Subagent:{traceId}] {timeoutMessage}; task={ctx.Description.Truncate(120)}");
                        AddLog("WARN", $"[{Definition.Type}][{traceId}] {timeoutMessage}");
                        return LocalizationService.Instance.Format("agent.subagentWatchdogRetry", timeoutMessage);
                    }
                    catch (OperationCanceledException)
                    {
                        Logger.Info($"[Subagent:{traceId}] runSubagent 被取消 (ExploreAgent: {ctx.Description.Truncate(80)})");
                        throw; // 让取消信号向上传播，停止整个 Agent 流程
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[Subagent:{traceId}] runSubagent 异常: {ex.Message}", ex);
                        return LocalizationService.Instance.Format("agent.runSubagentException", ex.Message);
                    }
                };
            }

            // ── 注入 HandoffHandler 到 BuiltInToolService（处理 request_handoff 工具）──
            if (BuiltInTools != null)
            {
                BuiltInTools.HandoffHandler = async (request) =>
                {
                    request.SourceAgent = Definition.Type;

                    PendingHandoffRequest = request;
                    AddLog("INFO", $"[{Definition.Name}]  移交请求: → {request.TargetAgent} (原因: {request.Reason})");
                    await Task.CompletedTask;
                };
            }

            // ── Git 工具审批（危险操作需审批；只读 Agent 的写操作交由 GitTool 拒绝）──
            if (toolName == "git" && BuiltInTools != null)
            {
                string operation = string.Empty;
                string purpose = string.Empty;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
                    if (doc.RootElement.TryGetProperty("operation", out var opProp))
                        operation = opProp.GetString()?.ToLowerInvariant() ?? string.Empty;
                    if (doc.RootElement.TryGetProperty("purpose", out var purProp))
                        purpose = purProp.GetString() ?? string.Empty;
                }
                catch { }

                // 设置 Agent 类型供 GitTool 运行时校验（Ask/Explore 只能执行只读操作）
                GitTool.CurrentAgentType = Definition.Type;

                // 解析额外参数以细化操作分类
                string branch = string.Empty;
                string stashMode = string.Empty;
                string resetPath = string.Empty;
                bool delete = false;
                bool force = false;
                var requestedFlags = new List<string>();
                try
                {
                    using var doc2 = System.Text.Json.JsonDocument.Parse(argumentsJson);
                    if (doc2.RootElement.TryGetProperty("branch", out var bProp))
                        branch = bProp.GetString() ?? string.Empty;
                    if (doc2.RootElement.TryGetProperty("mode", out var mProp))
                        stashMode = mProp.GetString() ?? string.Empty;
                    if (doc2.RootElement.TryGetProperty("path", out var pProp))
                        resetPath = pProp.GetString() ?? string.Empty;
                    if (doc2.RootElement.TryGetProperty("delete", out var dProp) && dProp.ValueKind == System.Text.Json.JsonValueKind.True)
                        delete = true;
                    if (doc2.RootElement.TryGetProperty("force", out var forceProp) && forceProp.ValueKind == System.Text.Json.JsonValueKind.True)
                        force = true;
                    if (doc2.RootElement.TryGetProperty("flags", out var flagsProp)
                        && flagsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var flagProp in flagsProp.EnumerateArray())
                        {
                            if (flagProp.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                string flag = flagProp.GetString() ?? string.Empty;
                                if (!string.IsNullOrWhiteSpace(flag))
                                    requestedFlags.Add(flag);
                            }
                        }
                    }
                }
                catch { }

                bool isReadOnly = GitTool.IsReadOnlyOperation(
                    operation, branch, stashMode, resetPath, delete);
                bool flagsRequireApproval = GitTool.FlagsRequireApproval(
                    operation, requestedFlags, out string flagsReason);
                bool requiresApproval = GitTool.RequiresApproval(
                    Definition.Type,
                    operation,
                    isReadOnly,
                    stashMode,
                    delete,
                    force,
                    requestedFlags,
                    out string approvalReason);

                if (requiresApproval && !string.IsNullOrWhiteSpace(operation))
                {
                    var L = LocalizationService.Instance;
                    string gitOpDesc = operation switch
                    {
                        "add" => L["agent.approval.gitOp.add"],
                        "commit" => L["agent.approval.gitOp.commit"],
                        "branch" => L["agent.approval.gitOp.branch"],
                        "checkout" => L["agent.approval.gitOp.checkout"],
                        "pull" => L["agent.approval.gitOp.pull"],
                        "push" => L["agent.approval.gitOp.push"],
                        "stash" => L["agent.approval.gitOp.stash"],
                        "reset" => L["agent.approval.gitOp.reset"],
                        _ => $"git {operation}"
                    };

                    string approvalCmd = string.IsNullOrEmpty(approvalReason)
                        || string.Equals(approvalReason, operation, StringComparison.OrdinalIgnoreCase)
                            ? $"git {operation}"
                            : $"git {operation} {approvalReason}";
                    string approvalTitle = flagsRequireApproval
                        ? L.Format("agent.approval.confirmGitFlags", operation)
                        : operation == "push"
                            ? L["agent.approval.confirmGitPush"]
                            : L.Format("agent.approval.confirmGitDanger", operation, gitOpDesc);
                    string approvalDetail = string.IsNullOrEmpty(purpose)
                        ? L.Format("agent.approval.gitRequest", operation)
                        : purpose;
                    if (!string.IsNullOrEmpty(approvalReason))
                    {
                        approvalDetail += "\n" + LocalizationService.Instance.Format(
                            flagsRequireApproval
                                ? "tool.git.approvalFlagsReason"
                                : "tool.git.approvalDangerReason",
                            approvalReason);
                    }

                    bool approved = await RequestPermissionAsync(
                        approvalTitle,
                        approvalCmd,
                        "git_operation",
                        purpose,
                        approvalDetail,
                        ct);

                    if (!approved)
                    {
                        AddLog("WARN", LocalizationService.Instance.Format("agent.log.gitOperationDenied", operation));
                        return LocalizationService.Instance.Format("agent.approval.skipGitOperation", operation);
                    }
                    AddLog("INFO", LocalizationService.Instance.Format("agent.log.gitOperationApproved", operation));
                }
            }

            // ── 终端命令需要用户审批 ──
            if (toolName == "run_in_terminal" && BuiltInTools != null)
            {
                // 设置 Agent 类型供 RunInTerminalTool 运行时校验（Ask/Explore 禁止修改文件的终端命令）
                RunInTerminalTool.CurrentAgentType = Definition.Type;

                string command = string.Empty;
                string explanation = string.Empty;
                string purpose = string.Empty;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
                    if (doc.RootElement.TryGetProperty("command", out var cmdProp))
                        command = cmdProp.GetString() ?? string.Empty;
                    if (doc.RootElement.TryGetProperty("explanation", out var explProp))
                        explanation = explProp.GetString() ?? string.Empty;
                    if (doc.RootElement.TryGetProperty("purpose", out var purProp))
                        purpose = purProp.GetString() ?? string.Empty;
                    // goal 是 DeepSeek 模型原生使用的参数名，作为 purpose 的 fallback
                    if (string.IsNullOrWhiteSpace(purpose) && doc.RootElement.TryGetProperty("goal", out var goalProp))
                        purpose = goalProp.GetString() ?? string.Empty;
                }
                catch { }

                if (!string.IsNullOrWhiteSpace(command)
                    && RunInTerminalTool.CurrentAgentType is AgentType.Ask or AgentType.Explore
                    && RunInTerminalTool.DetectFileEditingCommand(command))
                {
                    // 只读 Agent：直接拒绝会修改文件的终端命令，不进入审批流程
                    AddLog("WARN", LocalizationService.Instance.Format("agent.log.readOnlyCommandBlocked", command));
                    return RunInTerminalTool.FormatFileEditBlocked(command);
                }

                if (!string.IsNullOrWhiteSpace(command))
                {
                    bool approved = await RequestTerminalApprovalAsync(command, explanation, purpose, ct);
                    if (!approved)
                        return LocalizationService.Instance.Format("agent.approval.skipTerminalCommand", command);
                }
            }

            // ── 文件删除需要用户审批 ──
            if (toolName == "delete_file" && BuiltInTools != null)
            {
                string filePath = string.Empty;
                string explanation = string.Empty;
                string purpose = string.Empty;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
                    if (doc.RootElement.TryGetProperty("filePath", out var fpProp))
                        filePath = fpProp.GetString() ?? string.Empty;
                    if (doc.RootElement.TryGetProperty("explanation", out var explProp))
                        explanation = explProp.GetString() ?? string.Empty;
                    if (doc.RootElement.TryGetProperty("purpose", out var purProp))
                        purpose = purProp.GetString() ?? string.Empty;
                }
                catch { }

                if (!string.IsNullOrWhiteSpace(filePath))
                {
                    var paths = new List<string> { filePath };
                    bool approved = await RequestFileDeleteConfirmationAsync(paths, explanation, purpose, ct);
                    if (!approved)
                        return LocalizationService.Instance.Format(
                    "agent.approval.cancelFileDeletion", System.IO.Path.GetFileName(filePath));
                }
            }

            // ── VisualStudio_askQuestions：向用户提问并等待回答 ──
            if (toolName == "VisualStudio_askQuestions")
            {
                string questionsJson = string.Empty;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
                    if (doc.RootElement.TryGetProperty("questions", out var qProp))
                        questionsJson = qProp.GetRawText();
                }
                catch { }

                if (!string.IsNullOrWhiteSpace(questionsJson))
                {
                    return await RequestAskQuestionsAsync(questionsJson, ct);
                }
                return LocalizationService.Instance["service.baseAgent.missingQuestions"];
            }

            // ── 越权文件访问检查：AI 访问项目外路径需要用户审批 ──
            if (BuiltInTools != null && IsFileAccessingTool(toolName))
            {
                string? targetPath = ExtractAnyPathFromToolArgs(toolName, argumentsJson);
                if (!string.IsNullOrWhiteSpace(targetPath)
                    && !string.IsNullOrWhiteSpace(workspaceRoot)
                    && IsPathOutsideWorkspace(targetPath!, workspaceRoot!))
                {
                    string operation = GetToolOperationName(toolName);
                    string fileName = System.IO.Path.GetFileName(targetPath!.TrimEnd('/', '\\'));
                    string displayName = string.IsNullOrEmpty(fileName) ? targetPath : fileName;

                    var L = LocalizationService.Instance;
                    bool approved = await RequestPermissionAsync(
                        L.Format("agent.approval.confirmOutOfWorkspace", operation, displayName),
                        L.Format("agent.approval.outOfWorkspaceDetail", operation, targetPath, workspaceRoot),
                        "file_access_outside_workspace",
                        "",
                        L.Format("agent.approval.outOfWorkspacePurpose", operation, targetPath),
                        ct);
                    if (!approved)
                    {
                        AddLog("WARN", LocalizationService.Instance.Format("agent.log.permissionDenied", targetPath));
                        return L.Format("agent.approval.outOfWorkspaceDenied", operation, targetPath)
                            + string.Format(AiPrompts.OutOfWorkspaceWarning, targetPath, workspaceRoot);
                    }
                    AddLog("INFO", LocalizationService.Instance.Format("agent.log.permissionGranted", targetPath));
                }
            }

            // ── 修改项目文件（.vcxproj/.csproj/.slnx 等）需要用户确认 ──
            if (BuiltInTools != null && IsFileModifyingTool(toolName))
            {
                string? targetPath = ExtractFilePathFromToolArgs(toolName, argumentsJson);
                if (!string.IsNullOrWhiteSpace(targetPath) && IsProjectFile(targetPath))
                {
                    string fileName = System.IO.Path.GetFileName(targetPath);
                    var Lp = LocalizationService.Instance;
                    string operation = toolName switch
                    {
                        "replace_string_in_file" => Lp["agent.op.modify"],
                        "multi_replace_string_in_file" => Lp["agent.op.batchModify"],
                        "create_file" => Lp["agent.op.create"],
                        "apply_patch" => Lp["agent.op.applyPatch"],
                        _ => Lp["agent.op.generic"]
                    };
                    // 根据工具类型自动推断操作目的
                    string filePurpose = toolName switch
                    {
                        "create_file" => Lp.Format("agent.purpose.createFile", fileName),
                        "replace_string_in_file" => Lp.Format("agent.purpose.replaceString", fileName),
                        "multi_replace_string_in_file" => Lp.Format("agent.purpose.multiReplace", fileName),
                        "apply_patch" => Lp.Format("agent.purpose.applyPatch", fileName),
                        _ => Lp.Format("agent.purpose.generic", fileName)
                    };
                    bool approved = await RequestPermissionAsync(
                        Lp.Format("agent.approval.confirmProjectFile", operation, fileName),
                        Lp.Format("agent.approval.projectFileDetail", operation, fileName, targetPath),
                        "file_write",
                        "",
                        filePurpose,
                        ct);
                    if (!approved)
                    {
                        AddLog("WARN", LocalizationService.Instance.Format("agent.log.projectModDenied", fileName));
                        return Lp.Format("agent.approval.projectFileCancelled", operation, fileName);
                    }
                    AddLog("INFO", LocalizationService.Instance.Format("agent.log.projectModGranted", fileName));
                }
            }

            // ── 1. MCP 工具优先（同名时覆盖内置）──
            if (McpManager != null)
            {
                var mcpTools = McpManager.AllTools;
                bool hasMcpTool = mcpTools.Any(t => string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase));
                if (hasMcpTool)
                {
                    try
                    {
                        return await McpManager.CallToolAsync(toolName, argumentsJson, ct);
                    }
                    catch (Exception ex)
                    {
                        return LocalizationService.Instance.Format("agent.mcpToolFailed", toolName, ex.Message);
                    }
                }
            }

            // ── 2. 内置工具（回退）──
            if (BuiltInTools != null && BuiltInToolService.IsBuiltInTool(toolName))
            {
                // 注入当前轮用户原始问题（AsyncLocal）：web_search 会把它作为
                // 官方 Search 的 ori_question，帮助引擎理解搜索意图。
                BuiltInToolBase.CurrentUserQuestion = Context?.CurrentUserContent;

                string? result = await BuiltInTools.ExecuteBuiltInToolAsync(toolName, argumentsJson, workspaceRoot, ct);
                if (result != null)
                    return result;
            }

            return LocalizationService.Instance.Format("agent.unknownTool", toolName);
        }

        // ── 阶段 3 分解用的私有类型容器 ──
        // 说明：这些类型用于把 ExecuteToolAsync 内的上下文与结果集中承载，
        // 使后续 E1~E4 阶段方法可通过单一参数传递状态。

        /// <summary>
        /// <see cref="ExecuteToolAsync"/> 单次派发内的不可变上下文（工具名、参数、工作区与取消令牌）。
        /// </summary>
        private sealed class ToolInvocationContext
        {
        }

        // 注：ToolExecutionOutcome 在项目中已是既有公共类型（提供 Classify/FromRaw/StartsWithContractMarker），
        // 此处不得再声明同名嵌套类型，否则会遮蔽该类型并导致 CS0117。

        /// <summary>审批子链的判定结果（是否放行以及拒绝时返回给模型的结果文本）。</summary>
        private sealed class AgentPermissionDecision
        {
        }
    }
}
