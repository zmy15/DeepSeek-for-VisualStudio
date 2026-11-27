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
    /// <see cref="BaseAgent"/> 的 工具循环 分部实现。
    /// 承载该职责域的成员，使主文件聚焦于 Agent 执行编排。
    /// </summary>
    public abstract partial class BaseAgent
    {
        /// <summary>
        /// 带工具调用的 AI 对话循环（支持多轮工具调用）。
        /// 
        /// 这是 Agent 执行工具增强型任务的核心方法。
        /// 与主聊天流程 (DeepSeekChatControl.Messaging.cs) 中的工具调用循环一致。
        /// </summary>
        /// <param name="messages">消息列表（system + 历史 + user）</param>
        /// <param name="workspaceRoot">工作区根目录，用于内置工具（如 file_search, list_dir）</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="toolWhitelist">自定义工具白名单（null = 使用 Definition.AllowedTools）</param>
        /// <param name="onThinking">思考内容回调（用于 UI 实时更新）</param>
        /// <param name="onContent">内容回调（用于 UI 实时更新）</param>
        /// <param name="onToolCall">工具调用回调（用于 UI 通知）</param>
        /// <param name="maxToolRounds">本次工具循环的最大轮数（null = 仅使用全局安全上限）</param>
        /// <param name="maxAskQuestionsCalls">本次工具循环最多允许执行的 askQuestions 次数（null = 不限制）</param>
        /// <param name="temperature">采样温度</param>
        /// <param name="toolChoiceOverride">覆盖本轮工具选择策略（例如 auto / none）</param>
        /// <param name="noToolsReminderAfterFirstToolRound">首次完成工具调用后追加的“禁止继续调用工具”提示</param>
        /// <param name="persistToHistory">
        /// 是否把本轮的 assistant / tool / 对账结果写入 <c>_entries</c>。
        /// 主流程（Ask/Plan/Edit/Build 的对话推进）传 true；
        /// 一次性子任务（记忆判定、总结润色等）必须传 false —— 它们的提示词与返回结果
        /// 不属于主流程对话，落库会污染下一轮请求（模型会把「记忆管理助手」的问答当成本轮内容）。
        /// </param>
        /// <returns>AI 最终生成的文本内容</returns>
        protected async Task<string> CallAiWithToolLoopAsync(
            List<ChatApiMessage> messages,
            string? workspaceRoot,
            CancellationToken ct,
            List<string>? toolWhitelist = null,
            Action<string>? onThinking = null,
            Action<string>? onContent = null,
            Action<string>? onToolCall = null,
            int? maxToolRounds = null,
            int? maxAskQuestionsCalls = null,
            double? temperature = null,
            string? toolChoiceOverride = null,
            string? noToolsReminderAfterFirstToolRound = null,
            bool persistToHistory = true)
        {
            string? originalUserQuestion = messages
                .LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                ?.Content;
            var reasoningBuilder = new StringBuilder();
            var contentBuilder = new StringBuilder();
            var toolCallAccumulator = new Dictionary<int, Models.ToolCallAccumulator>();

            // ── P0 Telemetry：会话指标采集（非侵入：Metrics 为 null 时零开销）──
            // 包装流式回调以捕获首个 token 到达时间（TTFT），原回调语义保持不变。
            var metrics = Context?.Metrics;
            if (metrics != null)
            {
                var origThinking = onThinking;
                var origContent = onContent;
                onThinking = s => { metrics.RecordFirstToken(); origThinking?.Invoke(s); };
                onContent = s => { metrics.RecordFirstToken(); origContent?.Invoke(s); };
            }

            // ── 循环检测状态 ──
            var callSignatureHistory = new List<string>();
            var toolCallHistory = new List<(int Round, string Summary)>();
            var lastResultBySignature = new Dictionary<string, string>();  // 跟踪每次调用的结果
            var rejectedToolNames = new List<string>();
            int rejectedToolRounds = 0;
            int consecutiveErrorRounds = 0;
            int askQuestionsCallCount = 0;
            int askDirectExplorationCallCount = 0;
            bool hasUnresolvedTestFailure = false;
            // 本次工具循环内"考虑探索深度"提示只发一次，避免反复刷屏。
            bool askDirectExplorationNoticeSent = false;
            bool noToolsReminderAppended = false;
            int maxRepeatedSameCall = Settings.DeepSeekOptionsPage.Instance?.MaxRepeatedSameCall ?? 5;
            if (maxRepeatedSameCall < 1) maxRepeatedSameCall = 5;
            int maxConsecutiveErrors = Settings.DeepSeekOptionsPage.Instance?.MaxConsecutiveErrors ?? 5;
            if (maxConsecutiveErrors < 1) maxConsecutiveErrors = 5;
            int maxConsecutiveWhitelistRejections = Math.Max(5, maxConsecutiveErrors);
            int configuredSafetyLimit = Settings.DeepSeekOptionsPage.Instance?.MaxToolCallRounds ?? 200;
            if (configuredSafetyLimit < 1) configuredSafetyLimit = 200;
            bool loopDetected = false;
            bool conversationTitleRequestSent = false;
            bool conversationTitleReceived = false;
            bool conversationTitleRequestCompleted = false;

            // MaxToolCallRounds is a per-Agent-invocation limit. The global
            // CurrentRound is only a cache-age offset and must not consume the
            // next request's safety budget.
            int round = 0;
            // 本轮工具循环中直传图片的临时文件批次令牌；每次请求发送成功后清理。
            var visionBatchTokens = new List<string>();
            int cacheRoundOffset = BuiltInTools?.CurrentRound ?? 0;
            int effectiveRoundLimit = ResolveEffectiveToolRoundLimit(
                configuredSafetyLimit,
                maxToolRounds);
            int contextBudget = Context?.ContextManager?.TokenBudget ?? 900_000;
            var options = Settings.DeepSeekOptionsPage.Instance;
            int maxWallTimeSeconds = NormalizeExecutionSetting(
                options?.AgentMaxWallTimeSeconds ?? 0,
                0,
                0,
                7200);
            int maxTotalTokens = NormalizeExecutionSetting(
                options?.AgentMaxTotalTokens ?? 0,
                0,
                0,
                2_000_000);
            int maxToolCalls = NormalizeExecutionSetting(
                options?.AgentMaxToolCalls ?? Math.Max(100, effectiveRoundLimit * 2),
                Math.Max(100, effectiveRoundLimit * 2),
                1,
                10_000);
            int maxExecutionDepth = NormalizeExecutionSetting(
                options?.AgentMaxDepth ?? 3,
                3,
                1,
                10);
            int maxNoProgressRounds = NormalizeExecutionSetting(
                options?.AgentNoProgressRounds ?? Math.Max(3, maxRepeatedSameCall),
                Math.Max(3, maxRepeatedSameCall),
                1,
                50);
            var executionPolicy = new AgentExecutionPolicy
            {
                MaxSteps = effectiveRoundLimit,
                MaxWallTime = TimeSpan.FromSeconds(maxWallTimeSeconds),
                MaxTotalTokens = maxTotalTokens,
                MaxToolCalls = maxToolCalls,
                MaxExecutionDepth = maxExecutionDepth,
                MaxNoProgressRounds = maxNoProgressRounds
            };
            var executionGuard = new AgentExecutionGuard(
                executionPolicy,
                Context?.ExecutionDepth ?? 0);

            // ──  v1.1.11：固定后缀插入点 ──
            // 消息结构：[prefix][稳定历史][tool_calls...][volatile][user][agent]
            // assistant/tool 插入到 volatile + user 之前，下一轮只替换 volatile + user 尾部。
            int persistedToolIndex = Context?.ToolHistoryInsertIndex ?? Math.Max(0, messages.Count - 1);
            int toolInsertPos = Math.Max(0, Math.Min(persistedToolIndex, messages.Count));
            while (!loopDetected)
            {
                round++;
                toolInsertPos = await ApplyPendingGuidanceMessageAsync(
                    messages,
                    toolInsertPos).ConfigureAwait(false);
                var executionDecision = executionGuard.CheckBeforeStep(round);
                if (executionDecision.ShouldStop)
                {
                    Logger.Warn($"[Agent:{Definition.Name}] {executionDecision.Message}");
                    AppendExecutionStop(contentBuilder, executionDecision);
                    metrics?.MarkTerminated(executionDecision.Reason.ToString());
                    break;
                }

                string? budgetWarning = executionGuard.GetBudgetWarning(round);
                if (budgetWarning != null)
                    Logger.Warn($"[Agent:{Definition.Name}] {budgetWarning}");

                // ── P0 Telemetry：本轮 LLM 请求计时开始（TTFT/耗时基准）──
                metrics?.BeginTurn(round);
                CurrentActivity = LocalizationService.Instance.Format("agent.currentActivity.waitingModel", round);

                // ── 同步当前轮次到文件读取缓存，用于轮数过期策略 ──
                if (BuiltInTools != null)
                {
                    int cacheRound = cacheRoundOffset > int.MaxValue - round
                        ? int.MaxValue
                        : cacheRoundOffset + round;
                    BuiltInTools.CurrentRound = cacheRound;
                }

                toolCallAccumulator.Clear();
                reasoningBuilder.Clear();
                contentBuilder.Clear();

                // ── 获取工具定义 ──
                //  Prefix Cache 优化：始终发送完整工具集（所有内置 + MCP 工具），
                //    保持 tools JSON 跨 Agent/阶段不变，最大化 DeepSeek Prefix Cache 命中率。
                //    工具调用由客户端按白名单拦截（见下方拦截逻辑）。
                //    白名单为空表示明确"不允许任何工具"：此时显式传 tool_choice:"none"，
                //    避免模型看到 read_file 等定义后误调用，被拦截后污染最终输出。
                // ── 获取工具定义（实现见 PrepareToolDefinitions）──
                //  白名单只用于客户端拦截；工具集始终完整发送以保持 Prefix Cache 稳定。
                var preparedTools = PrepareToolDefinitions(toolWhitelist, toolChoiceOverride);
                List<ToolDefinition>? toolDefs = preparedTools.ToolDefs;
                List<string>? effectiveWhitelist = preparedTools.EffectiveWhitelist;
                string? toolChoice = preparedTools.ToolChoice;

                // ── 流式调用 AI（带断点续传重试）──
                bool streamSuccess = false;
                int streamAttempt = 0;
                int maxStreamAttempts = ApiRetryPolicy.GetMaxAttempts();
                int maxStreamRetries = maxStreamAttempts - 1;
                const int maxReasoningLoopRetries = 1;
                int reasoningLoopRetryCount = 0;
                bool reasoningLoopRetryPending = false;
                string savedPartialContent = "";
                string savedPartialReasoning = "";
                var reasoningGuard = new ReasoningLoopGuard();

                async Task PrepareAndWaitForStreamRetryAsync(
                    Func<TimeSpan, string> createLogMessage)
                {
                    streamAttempt++;
                    savedPartialContent = contentBuilder.ToString();
                    savedPartialReasoning = reasoningBuilder.ToString();
                    TimeSpan backoff = ApiRetryPolicy.GetBackoffDelay(streamAttempt);
                    Logger.Warn(createLogMessage(backoff));
                    metrics?.RecordStreamRetry();
                    contentBuilder.Clear();
                    reasoningBuilder.Clear();
                    toolCallAccumulator.Clear();
                    await Task.Delay(backoff, ct);
                }

                bool TryHandleConversationTitleChunk(string chunk)
                {
                    if (!chunk.StartsWith("[TITLE]", StringComparison.Ordinal))
                        return false;

                    conversationTitleReceived = true;
                    try
                    {
                        Context?.OnConversationTitleReceived?.Invoke(chunk.Substring(7));
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[AI标题] 处理官方 title 回调失败: {ex.Message}");
                    }
                    return true;
                }

                void CompleteConversationTitleRequestIfNeeded()
                {
                    if (!conversationTitleRequestSent || conversationTitleRequestCompleted)
                        return;

                    conversationTitleRequestCompleted = true;
                    if (Context != null)
                        Context.RequestConversationTitle = false;
                    if (conversationTitleReceived)
                        return;

                    try
                    {
                        Context?.OnConversationTitleRequestCompleted?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[AI标题] 官方 title 未返回时执行兜底失败: {ex.Message}");
                    }
                }

                while (!streamSuccess && streamAttempt < maxStreamAttempts)
                {
                    try
                    {
                        bool requestConversationTitle =
                            Context?.RequestConversationTitle == true &&
                            !conversationTitleRequestSent;

                        // 如果是重试，将已接收的部分内容注入对话上下文
                        if (streamAttempt > 0 || reasoningLoopRetryPending)
                        {
                            // 在消息列表中追加部分 AI 回复 + 继续指令
                            var resumeMessages = new List<ChatApiMessage>(messages);
                            resumeMessages.Add(new ChatApiMessage
                            {
                                Role = "assistant",
                                Content = string.IsNullOrEmpty(savedPartialContent) ? null : savedPartialContent,
                                ReasoningContent = string.IsNullOrEmpty(savedPartialReasoning) ? null : savedPartialReasoning
                            });

                            string tailContent = savedPartialContent.Length > 300
                                ? LocalizationService.Instance["agent.resume.truncatedMarker"] + savedPartialContent.Substring(savedPartialContent.Length - 300)
                                : savedPartialContent;
                            string resumeInstruction = reasoningLoopRetryPending
                                ? BuildReasoningLoopRetryPrompt(originalUserQuestion)
                                : LocalizationService.Instance.Format("agent.resume.truncated", tailContent);
                            resumeMessages.Add(new ChatApiMessage
                            {
                                Role = "user",
                                Content = resumeInstruction
                            });
                            reasoningLoopRetryPending = false;

                            // 将部分内容预置到缓冲区
                            contentBuilder.Append(savedPartialContent);
                            reasoningBuilder.Append(savedPartialReasoning);
                            Logger.Info($"[Agent:{Definition.Name}] 流断点续传：第 {streamAttempt + 1}/{maxStreamAttempts} 次，已注入 {savedPartialContent.Length} 字符部分内容");

                            // 使用 resume 消息而不是原始消息
                            reasoningGuard.Reset();
                            await foreach (var chunk in _apiService.ChatStreamAsync(
                                resumeMessages, toolDefs, ct,
                                toolChoice: toolChoice,
                                temperature: temperature,
                                requestConversationTitle: requestConversationTitle))
                            {
                                if (requestConversationTitle)
                                    conversationTitleRequestSent = true;
                                if (!TryHandleConversationTitleChunk(chunk))
                                {
                                    ThrowIfReasoningLoopDetected(chunk, reasoningGuard);
                                    ProcessStreamChunk(chunk, reasoningBuilder, contentBuilder, toolCallAccumulator, onThinking, onContent);
                                }
                            }
                        }
                        else
                        {
                            reasoningGuard.Reset();
                            await foreach (var chunk in _apiService.ChatStreamAsync(
                                messages, toolDefs, ct,
                                toolChoice: toolChoice,
                                temperature: temperature,
                                requestConversationTitle: requestConversationTitle))
                            {
                                if (requestConversationTitle)
                                    conversationTitleRequestSent = true;
                                if (!TryHandleConversationTitleChunk(chunk))
                                {
                                    ThrowIfReasoningLoopDetected(chunk, reasoningGuard);
                                    ProcessStreamChunk(chunk, reasoningBuilder, contentBuilder, toolCallAccumulator, onThinking, onContent);
                                }
                            }
                        }

                        if (requestConversationTitle)
                            conversationTitleRequestSent = true;
                        CompleteConversationTitleRequestIfNeeded();
                        streamSuccess = true;
                        if (streamAttempt > 0)
                        {
                            Logger.Info($"[Agent:{Definition.Name}] 流断点续传成功 (尝试 {streamAttempt + 1}/{maxStreamAttempts})");
                        }

                        // ── 主流程发送内容对账 ──
                        //   把本次真正发出去的主流程消息（Handoff 的 volatile 块与任务提示、
                        //   路由边界指令、Agent 行为指令）补齐进 _entries，保证重建历史不缺消息。
                        //   子任务调用（persistToHistory=false）不走这里。
                        if (persistToHistory)
                            SyncSentMessagesToHistory(sentMessages: messages);

                        // ── 传输成功：删除本轮直传的临时图片文件 ──
                        //   图片已转为 data URI 随请求发出，临时文件无需保留。
                        foreach (string token in visionBatchTokens)
                            VisionImageFetchService.CleanupBatch(token);
                        visionBatchTokens.Clear();
                    }
                    catch (HttpRequestException ex) when (
                        !ApiRequestFailure.IsProviderHandled(ex) &&
                        streamAttempt < maxStreamAttempts - 1)
                    {
                        await PrepareAndWaitForStreamRetryAsync(backoff =>
                            $"[Agent:{Definition.Name}] 流中断 (尝试 {streamAttempt}/{maxStreamRetries})，已收到 {savedPartialContent.Length} 字符，{backoff.TotalSeconds:0}s 后恢复…");
                    }
                    catch (TaskCanceledException) when (!ct.IsCancellationRequested && streamAttempt < maxStreamAttempts - 1)
                    {
                        // 超时（非用户取消）
                        await PrepareAndWaitForStreamRetryAsync(backoff =>
                            $"[Agent:{Definition.Name}] 流超时 (尝试 {streamAttempt}/{maxStreamRetries})，{backoff.TotalSeconds:0}s 后恢复…");
                    }
                    catch (ObjectDisposedException) when (ct.IsCancellationRequested)
                    {
                        // 用户取消导致的流释放，不重试
                        Logger.Info($"[Agent:{Definition.Name}] 流式调用被取消令牌中断");
                        if (contentBuilder.Length == 0)
                            contentBuilder.Append(LocalizationService.Instance["agent.cancelledNotice"]);
                        streamSuccess = true; // 不视为失败，正常退出
                    }
                    catch (ObjectDisposedException) when (!ct.IsCancellationRequested && streamAttempt < maxStreamAttempts - 1)
                    {
                        // SSE 读取超时（v1.1.10）：120s 无数据 → 流被 Dispose → ReadLineAsync 抛 ObjectDisposedException
                        await PrepareAndWaitForStreamRetryAsync(backoff =>
                            $"[Agent:{Definition.Name}] SSE 流读取超时 (尝试 {streamAttempt}/{maxStreamRetries})，已收到 {savedPartialContent.Length} 字符，{backoff.TotalSeconds:0}s 后恢复…");
                    }
                    catch (IOException) when (ct.IsCancellationRequested)
                    {
                        // 用户取消导致的 IO 异常，不重试
                        Logger.Info($"[Agent:{Definition.Name}] 流式调用被取消令牌中断 (IO)");
                        if (contentBuilder.Length == 0)
                            contentBuilder.Append(LocalizationService.Instance["agent.cancelledNotice"]);
                        streamSuccess = true;
                    }
                    catch (IOException) when (!ct.IsCancellationRequested && streamAttempt < maxStreamAttempts - 1)
                    {
                        // 网络错误导致的 IO 异常（非用户取消），尝试重试
                        await PrepareAndWaitForStreamRetryAsync(backoff =>
                            $"[Agent:{Definition.Name}] 流网络错误 (尝试 {streamAttempt}/{maxStreamRetries})，已收到 {savedPartialContent.Length} 字符，{backoff.TotalSeconds:0}s 后恢复…");
                    }
                    catch (ReasoningLoopDetectedException ex) when (reasoningLoopRetryCount < maxReasoningLoopRetries)
                    {
                        reasoningLoopRetryCount++;
                        savedPartialContent = contentBuilder.ToString();
                        savedPartialReasoning = reasoningBuilder.ToString();
                        contentBuilder.Clear();
                        reasoningBuilder.Clear();
                        toolCallAccumulator.Clear();
                        reasoningGuard.Reset();
                        reasoningLoopRetryPending = true;
                        metrics?.RecordStreamRetry();
                        Logger.Warn($"[Agent:{Definition.Name}] 检测到思考循环，已中断并准备重试: " +
                            $"reason={ex.Result.Action}, chars={ex.Result.CharacterCount}, repeats={ex.Result.RepetitionCount}");
                        await Task.Delay(200, ct);
                    }
                    catch (ReasoningLoopDetectedException ex)
                    {
                        Logger.Error($"[Agent:{Definition.Name}] 思考循环在重试后仍然存在，已停止当前回复: " +
                            $"reason={ex.Result.Action}, chars={ex.Result.CharacterCount}, repeats={ex.Result.RepetitionCount}");
                        reasoningBuilder.Clear();
                        toolCallAccumulator.Clear();
                        if (contentBuilder.Length > 0)
                            contentBuilder.AppendLine();
                        contentBuilder.Append(LocalizationService.Instance["agent.thinkingLoopStopped"]);
                        streamSuccess = true;
                        metrics?.MarkTerminated("reasoning_loop");
                    }
                    catch (OperationCanceledException)
                    {
                        // 用户取消，不重试
                        Logger.Info($"[Agent:{Definition.Name}] 流式调用被取消");
                        if (contentBuilder.Length == 0)
                            contentBuilder.Append(LocalizationService.Instance["agent.cancelledNotice"]);
                        streamSuccess = true;
                    }
                }

                // ── 记录本轮 Cache 命中率 ──
                CacheUsageLogger.Log(_apiService?.LastUsage, round);

                // ── 每轮 API usage 返回后立即校准，工具循环中也能及时反映真实 token。──
                var turnUsage = _apiService?.LastUsage;
                if (turnUsage != null && turnUsage.PromptTokens > 0 && Context?.ContextManager != null)
                    Context.ContextManager.CalibrateFromApiUsage(turnUsage.PromptTokens);

                // ── P0 Telemetry：记录本轮 usage 与总耗时（usage 取自最后一帧 SSE）──
                if (metrics != null)
                {
                    metrics.EndTurn(round,
                        turnUsage?.PromptTokens ?? 0,
                        turnUsage?.CompletionTokens ?? 0,
                        turnUsage?.EffectiveHitTokens ?? 0,
                        turnUsage?.EffectiveMissTokens ?? 0);
                }

                var usageDecision = executionGuard.RecordUsage(
                    turnUsage?.PromptTokens ?? 0,
                    turnUsage?.CompletionTokens ?? 0);
                if (usageDecision.ShouldStop)
                {
                    Logger.Warn($"[Agent:{Definition.Name}] {usageDecision.Message}");
                    AppendExecutionStop(contentBuilder, usageDecision);
                    metrics?.MarkTerminated(usageDecision.Reason.ToString());
                    break;
                }

                // ── 处理工具调用 ──
                var toolCalls = new List<ToolCall>();
                if (toolCallAccumulator.Count > 0)
                {
                    toolCalls = toolCallAccumulator.Values
                        .Where(a => !string.IsNullOrEmpty(a.FunctionName))
                        .Select(a => new ToolCall
                        {
                            Id = a.Id,
                            Type = a.Type ?? "function",
                            Function = new ToolCallFunction
                            {
                                Name = a.FunctionName!,
                                Arguments = a.ArgumentsBuilder.ToString()
                            }
                        }).ToList();
                }

                var toolCallDecision = executionGuard.RecordToolCalls(toolCalls.Count);
                if (toolCallDecision.ShouldStop)
                {
                    Logger.Warn($"[Agent:{Definition.Name}] {toolCallDecision.Message}");
                    AppendExecutionStop(contentBuilder, toolCallDecision);
                    metrics?.MarkTerminated(toolCallDecision.Reason.ToString());
                    break;
                }

                if (toolCalls.Count == 0 && contentBuilder.Length > 0)
                {
                    var contentText = NormalizeDsmlMarkers(contentBuilder.ToString());
                    
                    // Parse leaked DSML / XML format
                    var xmlMatches = Regex.Matches(contentText, @"(?:<｜｜DSML｜｜|<)invoke\s+name=""([^""]+)""[^>]*>(.*?)(?:</｜｜DSML｜｜|</)invoke>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    foreach (Match m in xmlMatches)
                    {
                        string name = m.Groups[1].Value;
                        string inner = m.Groups[2].Value;
                        var paramMatches = Regex.Matches(inner, @"(?:<｜｜DSML｜｜|<)parameter\s+name=""([^""]+)""[^>]*>(.*?)(?:</｜｜DSML｜｜|</)parameter>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                        var dict = new Dictionary<string, object>();
                        foreach (Match pm in paramMatches)
                        {
                            dict[pm.Groups[1].Value] = pm.Groups[2].Value;
                        }
                        toolCalls.Add(new ToolCall
                        {
                            Id = "call_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                            Type = "function",
                            Function = new ToolCallFunction
                            {
                                Name = name,
                                Arguments = JsonSerializer.Serialize(dict)
                            }
                        });
                    }

                    // Parse leaked ReAct format
                    var reactMatches = Regex.Matches(contentText, @"Action:\s*(?<name>\w+)\s*Action Input:\s*(?<args>\{.*?\})", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    foreach (Match m in reactMatches)
                    {
                        toolCalls.Add(new ToolCall
                        {
                            Id = "call_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                            Type = "function",
                            Function = new ToolCallFunction
                            {
                                Name = m.Groups["name"].Value,
                                Arguments = m.Groups["args"].Value
                            }
                        });
                    }

                    if (toolCalls.Count > 0)
                    {
                        Logger.Info($"[Agent:{Definition.Name}] (Fallback) 从内容中解析到 {toolCalls.Count} 个工具调用");
                    }
                }

                // ──  客户端白名单拦截：标记不在 effectiveWhitelist 中的工具调用 ──
                //    因为 API 请求始终发送完整工具集（Prefix Cache 优化），
                //    AI 可能调用不在当前 Agent/阶段白名单中的工具。
                //    标记后统一在执行阶段返回拒绝消息，并终止本轮工具循环。
                HashSet<int>? blockedToolIndices = null;
                bool rejectedToolThisRound = false;
                if (toolCalls.Count > 0 && effectiveWhitelist != null)
                {
                    var whitelistSet = new HashSet<string>(effectiveWhitelist, StringComparer.OrdinalIgnoreCase);
                    blockedToolIndices = new HashSet<int>();
                    for (int i = 0; i < toolCalls.Count; i++)
                    {
                        if (!whitelistSet.Contains(NormalizeToolName(toolCalls[i].Function.Name)))
                        {
                            blockedToolIndices.Add(i);
                        }
                    }
                    if (blockedToolIndices.Count > 0)
                    {
                        var blockedNames = string.Join(", ", blockedToolIndices.Select(i => toolCalls[i].Function.Name));
                        rejectedToolNames.AddRange(toolCalls
                            .Where((tc, i) => blockedToolIndices.Contains(i))
                            .Select(tc => tc.Function.Name));
                        rejectedToolRounds++;
                        rejectedToolThisRound = true;
                        Logger.Warn($"[Agent:{Definition.Name}]  白名单拦截 {blockedToolIndices.Count} 个工具: {blockedNames}（白名单: {string.Join(", ", effectiveWhitelist)}）");
                    }
                }

                if (toolCalls.Count == 0)
                {
                    bool hasInterimAssistant = contentBuilder.Length > 0
                        || reasoningBuilder.Length > 0;
                    if (hasInterimAssistant)
                    {
                        string interimContent = contentBuilder.ToString();
                        string interimReasoning = reasoningBuilder.ToString();
                        messages.Insert(toolInsertPos, new ChatApiMessage
                        {
                            Role = "assistant",
                            Content = string.IsNullOrWhiteSpace(interimContent) ? null : interimContent,
                            ReasoningContent = string.IsNullOrWhiteSpace(interimReasoning) ? null : interimReasoning,
                        });
                        toolInsertPos++;
                        if (persistToHistory)
                        {
                            Context?.ContextManager?.AddAssistantMessage(
                                string.IsNullOrWhiteSpace(interimContent) ? null : interimContent,
                                string.IsNullOrWhiteSpace(interimReasoning) ? null : interimReasoning);
                        }
                    }

                    int newToolInsertPos = await ApplyPendingGuidanceMessageAsync(
                        messages,
                        toolInsertPos).ConfigureAwait(false);
                    bool guidanceInserted = newToolInsertPos != toolInsertPos;
                    toolInsertPos = newToolInsertPos;
                    if (guidanceInserted)
                        continue;

                    if (hasInterimAssistant)
                    {
                        // ── 仅回收本次请求的局部消息，绝不回滚持久化历史 ──
                        //   局部 messages 中的这条 assistant 必须移除：调用方会把最终正文
                        //   作为本次请求的返回值再渲染一次，留着会与工具循环尾部重复。
                        //   但 ContextManager 里的同一条必须保留 —— 它就是「上一轮 AI 的最终输出」，
                        //   下一次用户提问时由 BuildApiMessagesRecentTurns 重放。
                        //   此前这里连 _entries 一起回滚（RemoveLastAssistantMessage），
                        //   导致下一轮请求只剩 tool_calls / tool 结果、没有 assistant 答复，
                        //   模型因此看不到自己上一轮回答过什么，表现为「上轮结论丢失」。
                        toolInsertPos--;
                        messages.RemoveAt(toolInsertPos);
                    }
                    break;
                }

                if (toolCalls.Count > 0)
                {
                    Logger.Info($"[Agent:{Definition.Name}] 检测到 {toolCalls.Count} 个工具调用: {string.Join(", ", toolCalls.Select(t => t.Function.Name))}");
                    CurrentActivity = LocalizationService.Instance.Format("agent.currentActivity.executingTool", round) +
                        string.Join(", ", toolCalls.Select(t => t.Function.Name));

                    // ── 去重：检测同一批次中完全相同的工具调用（同函数名+同参数），避免重复执行 ──
                    // 例如 AI 可能误调用 3 次相同的 runSubagent，去重后只执行 1 次，结果复用
                    var (dedupedIndices, dedupMapping) = DeduplicateToolCalls(toolCalls);
                    bool hasDedup = dedupedIndices.Count < toolCalls.Count;
                    if (hasDedup)
                    {
                        int skipped = toolCalls.Count - dedupedIndices.Count;
                        Logger.Info($"[Agent:{Definition.Name}]  去重: 跳过 {skipped} 个重复工具调用（{toolCalls.Count} → {dedupedIndices.Count} 个唯一调用）");
                    }

                    // ── 限制同一阶段内的用户提问次数，避免模型收到回答后继续重复提问 ──
                    HashSet<int>? suppressedAskQuestionIndices = null;
                    if (maxAskQuestionsCalls is int askQuestionLimit)
                    {
                        int effectiveLimit = Math.Max(0, askQuestionLimit);
                        foreach (int idx in dedupedIndices)
                        {
                            if (!string.Equals(
                                NormalizeToolName(toolCalls[idx].Function.Name),
                                "VisualStudio_askQuestions",
                                StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (askQuestionsCallCount >= effectiveLimit)
                                (suppressedAskQuestionIndices ??= new HashSet<int>()).Add(idx);
                            else
                                askQuestionsCallCount++;
                        }

                        if (suppressedAskQuestionIndices?.Count > 0)
                        {
                            Logger.Warn(
                                $"[Agent:{Definition.Name}] 已拦截重复的 VisualStudio_askQuestions 调用" +
                                $"（本阶段上限 {effectiveLimit} 次），不再向用户弹出重复问题。");
                        }
                    }

                    // ── Ask Agent 直接探索提示：所有调用始终正常执行；
                    //    达到阈值后，仅在本次真实工具结果末尾追加一次效率建议。──
                    HashSet<int>? directExplorationHintIndices = null;
                    bool canDelegateAskExploration = Definition.Type == AgentType.Ask
                        && ExploreAgent != null
                        && effectiveWhitelist?.Contains("runSubagent", StringComparer.OrdinalIgnoreCase) == true;
                    if (canDelegateAskExploration)
                    {
                        foreach (int idx in dedupedIndices)
                        {
                            if (!IsDirectExplorationTool(toolCalls[idx].Function.Name))
                                continue;

                            askDirectExplorationCallCount++;
                            if (!askDirectExplorationNoticeSent
                                && askDirectExplorationCallCount > AskDirectExplorationHintThreshold)
                            {
                                askDirectExplorationNoticeSent = true;
                                (directExplorationHintIndices ??= new HashSet<int>()).Add(idx);
                            }
                        }

                        if (directExplorationHintIndices?.Count > 0)
                        {
                            Logger.Info(
                                $"[Agent:{Definition.Name}] 直接探索已执行 {askDirectExplorationCallCount} 次，"
                                + "将在工具结果后追加一次非强制 Explore 建议");
                        }
                    }

                    // ── 通知工具调用（含详细信息，每轮仅一次，去重后只通知唯一调用）──
                    foreach (var idx in dedupedIndices)
                    {
                        var tc = toolCalls[idx];
                        string summary = BuiltInToolService.GetToolCallDisplayText(tc.Function.Name, tc.Function.Arguments);
                        // MCP 工具标注：即使与内置工具同名，也标注来源
                        if (McpManager != null && McpManager.AllTools.Any(t => string.Equals(t.Name, tc.Function.Name, StringComparison.OrdinalIgnoreCase)))
                            summary += " [MCP]";
                        onToolCall?.Invoke(summary);
                    }

                    // ── 收集本轮工具调用摘要到历史（去重后，用于循环终止时的上下文总结）──
                    foreach (var idx in dedupedIndices)
                    {
                        var tc = toolCalls[idx];
                        string summary = BuiltInToolService.GetToolCallDisplayText(tc.Function.Name, tc.Function.Arguments);
                        if (McpManager != null && McpManager.AllTools.Any(t => string.Equals(t.Name, tc.Function.Name, StringComparison.OrdinalIgnoreCase)))
                            summary += " [MCP]";
                        toolCallHistory.Add((round, summary));
                    }
                    // 保留最近 20 条记录防止内存增长
                    while (toolCallHistory.Count > 20)
                        toolCallHistory.RemoveAt(0);

                    // ── 子 Agent 缓存优化：在插入 assistant(tool_calls) 前捕获干净前缀 ──
                    //     runSubagent 只需要本轮请求已经缓存过的前缀，因此仍在 Insert 前捕获。
                    //     request_handoff 不能在这里捕获：它必须等 request_handoff 的 assistant/tool
                    //     结果写入 messages 后再生成转发快照，否则目标 Agent 的历史会比
                    //     ContextManager 的 canonical history 少两条消息。后续记忆提取从
                    //     canonical history 重建时就会在该位置分叉，造成整段 Prompt Cache miss。
                    bool isRequestHandoff = toolCalls.Any(tc => tc.Function.Name == "request_handoff");
                    if (Context != null && !isRequestHandoff && toolCalls.Any(tc =>
                        tc.Function.Name == "runSubagent"))
                    {
                        Context.ForwardedMessages =
                            CaptureLastSentMessagesForCache()
                            ?? CloneApiMessages(messages);
                    }

                    // ── 添加 assistant 消息（含工具调用）──
                    // 插入到 volatile + user 之前，使工具历史进入稳定前缀区。
                    messages.Insert(toolInsertPos, new ChatApiMessage
                    {
                        Role = "assistant",
                        Content = contentBuilder.Length > 0 ? contentBuilder.ToString() : null,
                        ReasoningContent = reasoningBuilder.Length > 0 ? reasoningBuilder.ToString() : null,
                        ToolCalls = toolCalls
                    });
                    toolInsertPos++;

                    // ── 同步 assistant 消息（含 tool_calls）到全局 ContextManager，确保重启后可恢复完整对话 ──
                    //     子任务（persistToHistory=false）不落库：其提示词/返回结果不属于主流程对话。
                    if (persistToHistory && Context?.ContextManager != null)
                    {
                        Context.ContextManager.AddAssistantMessage(
                            contentBuilder.Length > 0 ? contentBuilder.ToString() : null,
                            reasoningBuilder.Length > 0 ? reasoningBuilder.ToString() : null,
                            toolCalls);
                    }

                    // ── 并行执行工具调用（带超时保护，长时工具使用更长超时，已去重）──
                    //    被白名单拦截的工具跳过执行，直接返回拒绝消息。
                    var toolTasks = dedupedIndices.Select(idx =>
                    {
                        var tc = toolCalls[idx];
                        if (suppressedAskQuestionIndices?.Contains(idx) == true)
                        {
                            return Task.FromResult(
                                LocalizationService.Instance["agent.ask.answerAlreadyReceived"]);
                        }

                        // ── 视觉模型：OCR 工具禁用（仍随工具集发送，保持前缀稳定）──
                        if (ShouldBlockOcrToolExecution(
                                tc.Function.Name,
                                _apiService?.CurrentIsVision == true,
                                Context?.CurrentUserContent))
                        {
                            Logger.Info($"[Agent:{Definition.Name}] 视觉模型下禁用 OCR 工具调用: {tc.Function.Name}");
                            return Task.FromResult(
                                LocalizationService.Instance["agent.ocrToolDisabledForVision"]);
                        }

                        // ── 白名单拦截：不在白名单中的工具不执行，返回拒绝消息 ──
                        if (blockedToolIndices != null && blockedToolIndices.Contains(idx))
                        {
                            string allowedList = effectiveWhitelist != null
                                ? string.Join(", ", effectiveWhitelist)
                                : LocalizationService.Instance["agent.none"];
                            var L = LocalizationService.Instance;
                            return Task.FromResult(
                                L.Format("agent.toolNotAvailable", tc.Function.Name, allowedList)
                                + (effectiveWhitelist!.Contains("request_handoff", StringComparer.OrdinalIgnoreCase)
                                    ? L["agent.toolNotAvailableHandoff"]
                                    : L["agent.toolNotAvailableExplain"])
                                + L["agent.toolNotAvailableTerminate"]);
                        }
                        var timeout = GetToolTimeout(NormalizeToolName(tc.Function.Name));
                        return ExecuteToolWithTelemetryAsync(metrics, round, tc, workspaceRoot, ct, timeout);
                    }).ToList();
                    var dedupedResults = await Task.WhenAll(toolTasks).ConfigureAwait(false);
                    CurrentActivity = LocalizationService.Instance.Format("agent.currentActivity.toolDone", round);

                    if (canDelegateAskExploration
                        && dedupedIndices
                            .Select((idx, resultIndex) => (Tool: toolCalls[idx], Result: dedupedResults[resultIndex]))
                            .Any(item => NormalizeToolName(item.Tool.Function.Name) == "runSubagent"
                                && ToolExecutionOutcome.Classify(item.Result) == ToolResultKind.Success))
                    {
                        askDirectExplorationCallCount = 0;
                    }

                    // ── 将去重后的结果映射回原始 toolCalls 数组 ──
                    var toolResults = new string[toolCalls.Count];
                    for (int i = 0; i < dedupedIndices.Count; i++)
                    {
                        int originalIdx = dedupedIndices[i];
                        toolResults[originalIdx] = dedupedResults[i];

                        // ── 活跃文件追踪：记录工具访问的文件 ──
                        TryTrackActiveFileAccess(toolCalls[originalIdx], workspaceRoot);
                    }
                    // 将去重结果复制到所有重复调用
                    foreach (var mapping in dedupMapping)
                    {
                        int sourceIdx = mapping.Key;       // 实际执行的索引
                        foreach (int dupIdx in mapping.Value) // 重复调用的索引列表
                        {
                            toolResults[dupIdx] = dedupedResults[dedupedIndices.IndexOf(sourceIdx)];
                        }
                    }

                    if (directExplorationHintIndices?.Count > 0)
                    {
                        string explorationHint = LocalizationService.Instance["tool.ask.explorationHint"];
                        foreach (int hintIdx in directExplorationHintIndices)
                        {
                            string actualResult = toolResults[hintIdx] ?? string.Empty;
                            toolResults[hintIdx] = string.IsNullOrWhiteSpace(actualResult)
                                ? explorationHint
                                : actualResult.TrimEnd() + "\n\n" + explorationHint;
                        }
                    }

                    for (int i = 0; i < toolCalls.Count; i++)
                    {
                        var tc = toolCalls[i];
                        string toolResult = toolResults[i];

                        // ── 视觉工具：剥离图片块，视觉模型时提取图片直传给视觉模型 ──
                        // fetch_webpage 提取网页图片 URL（**下载为 data URI** 后直传）；
                        // capture_window（窗口截图）/ capture_webpage（网页截图）/ read_file 提取本地图片 data URI。
                        List<string>? imageUrls = null;
                        string? visionBatchToken = null;
                        string resultText = toolResult;
                        if (tc.Function.Name == "fetch_webpage")
                        {
                            var (cleanText, fetchedImageUrls) = WebSearchService.ParseWebImagesBlock(toolResult);
                            resultText = cleanText;
                            if (fetchedImageUrls.Count > 0
                                && _apiService.CurrentIsVision)
                            {
                                // ── 过滤视觉模型不支持的图片格式（如 SVG），并按内容嗅探兜底 ──
                                var filtered = WebSearchService.FilterVisionImageUrls(fetchedImageUrls);
                                if (filtered is { Count: > 0 })
                                {
                                    // ── 关键修复 ──
                                    // 过去把网页图片的**远程 URL** 直接作为 image_url 上传，由服务端自行下载；
                                    // 服务端网络/代理与客户端不一致时会失败并返回不可重试的 HTTP 400
                                    //（"An exception occurred while loading IMAGE data at index N"），
                                    // 导致整轮对话直接中断。
                                    // 现改为客户端下载 → data URI：服务端无需联网取图，从根上消除该类 400。
                                    // 下载失败（超时/非 2xx/格式不符/超限）的图片被静默丢弃，绝不断整轮对话。
                                    visionBatchToken = VisionImageFetchService.NewBatchToken();
                                    List<string> dataUris = await VisionImageFetchService
                                        .ToDataUrisAsync(
                                            filtered,
                                            httpClient: null,
                                            visionBatchToken,
                                            ct)
                                        .ConfigureAwait(false);

                                    if (dataUris.Count > 0)
                                    {
                                        imageUrls = dataUris;
                                        visionBatchTokens.Add(visionBatchToken);
                                        Logger.Info(
                                            $"[Agent:{Definition.Name}] fetch_webpage 图片已下载为 data URI 直传: " +
                                            $"{dataUris.Count}/{filtered.Count} 张（失败的图片已丢弃）");
                                    }
                                    else
                                    {
                                        // 全部图片下载失败：不留空批次目录，直接清理。
                                        VisionImageFetchService.CleanupBatch(visionBatchToken);
                                        visionBatchToken = null;
                                        Logger.Info(
                                            $"[Agent:{Definition.Name}] fetch_webpage 的 {filtered.Count} 张图片全部下载失败，" +
                                            "已按纯文本继续（不打断对话）");
                                    }
                                }
                            }
                        }
                        else if (tc.Function.Name == "capture_window"
                              || tc.Function.Name == "capture_webpage")
                        {
                            var (cleanText, imageUris) = CaptureWindowTool.ParseImageBlock(toolResult);
                            resultText = cleanText;
                            if (imageUris.Count > 0
                                && _apiService.CurrentIsVision)
                            {
                                imageUrls = imageUris;
                            }
                        }
                        else if (tc.Function.Name == "read_file")
                        {
                            var (cleanText, imageUris) = ReadFileTool.ParseImageBlock(toolResult);
                            resultText = cleanText;
                            if (imageUris.Count > 0
                                && _apiService.CurrentIsVision)
                            {
                                imageUrls = imageUris;
                            }
                        }

                        // ── 裁剪工具结果以保护上下文（与 ContextManager.AddToolResult 保持一致）──
                        string contextResult = CompactToolResultForAgent(tc.Function.Name, resultText);

                        // 构建一次并复用同一对象：本次发送与历史落库必须保持完全一致的 content 形态。
                        var toolResultMessage = BuildToolResultMessage(
                            tc.Id, tc.Function.Name, contextResult, imageUrls);
                        messages.Insert(toolInsertPos, toolResultMessage);
                        toolInsertPos++;

                        // ── 同步 tool 结果到全局 ContextManager，确保重启后可恢复完整对话 ──
                        //     图片等多模态块必须一并落库，否则下一次请求会把内容数组降级为字符串。
                        //     子任务（persistToHistory=false）不落库。
                        if (persistToHistory && Context?.ContextManager != null)
                        {
                            Context.ContextManager.AddToolResult(
                                tc.Id,
                                tc.Function.Name,
                                contextResult,
                                toolResultMessage.MultimodalContent);
                        }

                        Logger.Info($"[Agent:{Definition.Name}] 工具 {tc.Function.Name} 返回: {(toolResult.Length > 200 ? toolResult.Substring(0, 200) + "..." : toolResult)}");

                        // ── 过程区展示工具结果摘要：目前仅 web_search 提供可读的网页列表 ──
                        // 以独立日志级别标记，UI 侧将其作为过程行展示且不计入工具调用次数。
                        if (string.Equals(tc.Function.Name, "web_search", StringComparison.OrdinalIgnoreCase))
                        {
                            string resultSummary = BuiltInToolService.GetToolResultSummary(tc.Function.Name, resultText);
                            if (!string.IsNullOrWhiteSpace(resultSummary))
                                AddLog("TOOLRESULT", resultSummary);
                        }
                    }

                    if (!noToolsReminderAppended
                        && !string.IsNullOrWhiteSpace(noToolsReminderAfterFirstToolRound))
                    {
                        messages.Insert(toolInsertPos, new ChatApiMessage
                        {
                            Role = "system",
                            Content = noToolsReminderAfterFirstToolRound
                        });
                        toolInsertPos++;
                        noToolsReminderAppended = true;
                        Logger.Info($"[Agent:{Definition.Name}] 首次工具调用已处理，已追加禁止继续调用工具的提示");
                    }

                    // ── 工具结果写回后立即检查：同一用户请求内超限也触发压缩。──
                    TryCompressToolLoopContext(ref messages, ref toolInsertPos);

                    // ── Build/Edit 验证：测试或构建已经等待完整结果。明确成功后立即结束，
                    //    不再进入下一轮让模型调用 get_errors 或重复验证。──
                    if ((Definition.Type == AgentType.Build || Definition.Type == AgentType.Edit)
                        && PendingHandoffRequest == null)
                    {
                        var verificationStatus = ClassifyVerificationRound(toolCalls, toolResults);
                        var verificationDecision = DecideVerificationLoopTermination(
                            verificationStatus,
                            hasUnresolvedTestFailure);
                        hasUnresolvedTestFailure = verificationDecision.HasUnresolvedTestFailure;

                        if (verificationDecision.ShouldTerminate)
                        {
                            int resultIndex = FindVerificationResultIndex(
                                toolCalls,
                                verificationDecision.PreferredStatus);
                            if (resultIndex >= 0)
                            {
                                contentBuilder.Clear();
                                contentBuilder.Append(toolResults[resultIndex]);
                            }

                            // 成功验证会直接结束工具循环，之后不会再有 API 请求更新
                            // _apiService.LastSentMessages。如果后续程序化 Handoff 仍从最近
                            // 请求快照取上下文，就会丢失本次 build_solution 的 tool result。
                            // 因此显式保存当前完整消息列表，确保 Handoff 能看到最终构建结果。
                            if (Context != null)
                            {
                                Context.ForwardedMessages = CloneApiMessages(messages);
                            }
                            string terminationReason =
                                verificationDecision.PreferredStatus == VerificationRoundStatus.TestsPassed
                                    ? "test_success"
                                    : "build_success";
                            Logger.Info(
                                $"[Agent:{Definition.Name}] {terminationReason} 已明确完成，终止工具循环并返回结果。");
                            metrics?.MarkTerminated(terminationReason);
                            break;
                        }
                    }

                    // 合法工具轮次说明模型已纠正，重置连续白名单拒绝计数。
                    if (!rejectedToolThisRound)
                    {
                        rejectedToolRounds = 0;
                    }

                    // ── 连续多轮白名单拒绝才终止，避免大型任务偶发误调用被过早中断 ──
                    if (rejectedToolRounds >= maxConsecutiveWhitelistRejections && PendingHandoffRequest == null)
                    {
                        loopDetected = true;
                        PendingHandoffRequest = null;
                        var distinctRejectedTools = string.Join(", ", rejectedToolNames.Distinct(StringComparer.OrdinalIgnoreCase));
                        Logger.Warn($"[Agent:{Definition.Name}]  连续 {rejectedToolRounds} 轮白名单拒绝，终止工具循环: {distinctRejectedTools}");
                        metrics?.MarkTerminated("whitelist_rejection");

                        string whitelistSummary = BuildLoopTerminationSummary(
                            contentBuilder, toolCalls, toolResults, toolCallHistory, round,
                            includeHistory: false, includeToolResults: false,
                            trailingText: LocalizationService.Instance.Format(
                                "agent.noToolCallsTerminated", rejectedToolRounds, distinctRejectedTools)
                                + LocalizationService.Instance["agent.noToolCallsTerminatedHint"]);
                        contentBuilder.Clear();
                        contentBuilder.Append(whitelistSummary);
                        break;
                    }

                    // ── 移交检测：如果 AI 调用了 request_handoff，立即终止循环 ──
                    if (PendingHandoffRequest != null)
                    {
                        Logger.Info($"[Agent:{Definition.Name}]  检测到移交请求 → {PendingHandoffRequest.TargetAgent}，终止工具循环");
                        // ── 保存完整的 handoff 工具链，供目标 Agent 复用并保持 canonical 前缀 ──
                        //     request_handoff 的 assistant(tool_calls) 与 tool 结果已经是主流程
                        //     历史的一部分；若转发时省略它们，目标 Agent 第一轮请求与
                        //     ContextManager 后续重建的历史就会永久错位。这里深克隆当前消息，
                        //     并移除末尾 Agent 行为指令，交给目标 Agent 重新生成身份边界和任务。
                        if (Context != null)
                        {
                            Context.ForwardedMessages =
                                PrepareHandoffCacheMessages(CloneApiMessages(messages));
                        }
                        contentBuilder.Append(LocalizationService.Instance.Format(
                    "agent.handoffNotice", PendingHandoffRequest.TargetAgent));
                        break;
                    }

                    // ── 循环检测 ──
                    // 收集本轮签名
                    var roundSignatures = new List<string>();
                    foreach (var tc in toolCalls)
                    {
                        string sig = BuildToolCallSignature(tc);
                        callSignatureHistory.Add(sig);
                        roundSignatures.Add(sig);
                    }

                    var stateParts = new List<string>(roundSignatures);
                    for (int i = 0; i < toolResults.Length; i++)
                        stateParts.Add(toolResults[i] ?? string.Empty);
                    var progressDecision = executionGuard.RecordState(stateParts);
                    if (progressDecision.ShouldStop)
                    {
                        loopDetected = true;
                        Logger.Warn($"[Agent:{Definition.Name}] {progressDecision.Message}");
                        AppendExecutionStop(contentBuilder, progressDecision);
                        metrics?.MarkTerminated(progressDecision.Reason.ToString());
                        break;
                    }

                    // 检测同一调用重复（带同结果判断：只有每次返回相同结果才终止）
                    foreach (var sig in roundSignatures)
                    {
                        int repeatCount = callSignatureHistory.Count(s => s == sig);
                        if (repeatCount >= maxRepeatedSameCall)
                        {
                            string toolName = sig.Split('|')[0];

                            // ── 同结果检测：找到本轮该签名对应的工具执行结果 ──
                            string? currentResult = null;
                            for (int i = 0; i < toolResults.Length && i < toolCalls.Count; i++)
                            {
                                string tcSig = BuildToolCallSignature(toolCalls[i]);
                                if (tcSig == sig) { currentResult = toolResults[i]; break; }
                            }

                            // 检查与上次结果是否相同
                            bool sameResult = currentResult != null
                                && lastResultBySignature.TryGetValue(sig, out string? prevResult)
                                && AreResultsSubstantiallySame(prevResult, currentResult);

                            if (sameResult)
                            {
                                // 结果相同 → 真正的死循环，终止
                                loopDetected = true;
                                PendingHandoffRequest = null;
                                Logger.Warn($"[Agent:{Definition.Name}]  检测到循环调用: {toolName} 已重复 {repeatCount} 次且每次返回相同结果");
                                metrics?.MarkTerminated("loop_detected");

                                string loopSummary = BuildLoopTerminationSummary(
                                    contentBuilder, toolCalls, toolResults, toolCallHistory, round,
                                    includeHistory: true, includeToolResults: true,
                                    trailingText: LocalizationService.Instance.Format(
                                        "agent.repeatedResultTerminated", toolName, repeatCount));
                                contentBuilder.Clear();
                                contentBuilder.Append(loopSummary);
                                break;
                            }
                            else
                            {
                                // 结果不同 → 用户正在修复问题，重置该签名的计数并记录本次结果
                                if (currentResult != null)
                                    lastResultBySignature[sig] = currentResult;
                                callSignatureHistory.RemoveAll(s => s == sig);
                                Logger.Info($"[Agent:{Definition.Name}]  {toolName} 重复 {repeatCount} 次但结果不同，可能是用户在修复问题，继续执行");
                            }
                        }
                        else
                        {
                            // 正常记录结果（用于后续比较）
                            for (int i = 0; i < toolResults.Length && i < toolCalls.Count; i++)
                            {
                                string tcSig = BuildToolCallSignature(toolCalls[i]);
                                if (tcSig == sig && !string.IsNullOrWhiteSpace(toolResults[i]))
                                    lastResultBySignature[sig] = toolResults[i];
                            }
                        }
                    }

                    // 保留最近 30 条签名
                    while (callSignatureHistory.Count > 30)
                        callSignatureHistory.RemoveAt(0);

                    // 检测连续错误：检查本轮 tool 消息是否全部以 Error: 开头
                    if (!loopDetected)
                    {
                        // toolInsertPos 在插入 assistant + N 条 tool 后指向 agent 消息，
                        // 故第一个 tool 消息位于 toolInsertPos - toolCalls.Count
                        int toolMsgStart = toolInsertPos - toolCalls.Count;
                        bool allErrors = toolCalls.Count > 0;
                        for (int i = toolMsgStart; i < messages.Count && allErrors; i++)
                        {
                            if (messages[i].Role == "tool" && !(messages[i].Content ?? "").StartsWith("Error: "))
                                allErrors = false;
                        }

                        if (allErrors)
                            consecutiveErrorRounds++;
                        else
                            consecutiveErrorRounds = 0;

                        if (consecutiveErrorRounds >= maxConsecutiveErrors)
                        {
                            loopDetected = true;
                            PendingHandoffRequest = null;
                            Logger.Warn($"[Agent:{Definition.Name}]  连续 {consecutiveErrorRounds} 轮工具调用全部返回错误，强制结束");
                            metrics?.MarkTerminated("consecutive_errors");

                            string errorSummary = BuildLoopTerminationSummary(
                                contentBuilder, toolCalls, toolResults, toolCallHistory, round,
                                includeHistory: true, includeToolResults: true,
                                trailingText: LocalizationService.Instance.Format(
                                    "agent.consecutiveErrorsTerminated", consecutiveErrorRounds));
                            contentBuilder.Clear();
                            contentBuilder.Append(errorSummary);
                        }
                    }

                    // ── 继续下一轮 ──
                    continue;
                }

                // ── 无工具调用，结束循环 ──
                break;
            }

            // ── 汇总累计 Cache 统计（日志）──
            LogTotalCacheHitRate(round);

            return contentBuilder.ToString().Trim();
        }

        /// <summary>
        /// 准备本轮的完整工具集、客户端拦截白名单与 tool_choice（P1 请求准备的一部分）。
        /// 始终发送完整工具集以保持 tools JSON 稳定（Prefix Cache），白名单只用于客户端拦截：
        /// 追加 skill 只读工具、按 Agent 类型剔除修改类工具、自动注入未重叠的 MCP 工具。
        /// </summary>
        /// <param name="toolWhitelist">调用方显式指定的白名单；<c>null</c> 表示使用 Agent 定义的默认白名单。</param>
        /// <param name="toolChoiceOverride">显式覆盖 tool_choice；白名单为空时仍强制 <c>none</c>。</param>
        /// <returns>工具定义全集、生效白名单与 tool_choice。</returns>
        private (List<ToolDefinition>? ToolDefs, List<string>? EffectiveWhitelist, string? ToolChoice)
            PrepareToolDefinitions(List<string>? toolWhitelist, string? toolChoiceOverride)
        {
            List<ToolDefinition>? toolDefs = null;
            List<string>? effectiveWhitelist = null;
            string? toolChoice = null;
            if (BuiltInTools != null || McpManager != null)
            {
                // ── 始终发送完整工具集（不对 tools JSON 做白名单过滤）──
                toolDefs = GetFullToolSet();

                // ── 构建白名单（仅用于客户端拦截，不影响 tools JSON）──
                effectiveWhitelist = toolWhitelist ?? Definition.AllowedTools;
                if (effectiveWhitelist != null && effectiveWhitelist.Count > 0)
                {
                    // Skill discovery/loading is a read-only capability shared by every Agent.
                    // The auto-routing setting only controls the pre-request routing pass.
                    effectiveWhitelist = effectiveWhitelist
                        .Concat(new[] { "list_skills", "load_skill", "read_skill_resource" })
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                if (Definition.Type != AgentType.Edit && Definition.Type != AgentType.Build && effectiveWhitelist != null)
                {
                    // run_in_terminal 不在本列表中：AskAgent/ExploreAgent 通过白名单显式启用，
                    // 但由 RunInTerminalTool / BaseAgent 在运行时拦截任何修改文件的命令（只读终端）。
                    var modifyingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "replace_string_in_file", "create_file", "create_directory",
                        "delete_file", "apply_patch",
                        "write_file", "edit_file"
                    };
                    effectiveWhitelist = effectiveWhitelist.Where(t => !modifyingTools.Contains(t)).ToList();
                }

                // ── 自动注入未重叠的 MCP 工具（按 Agent 类型分类）──
                if (McpManager != null && McpManager.AllTools.Count > 0 && effectiveWhitelist != null)
                {
                    var autoMcpNames = GetAutoMcpToolNames(McpManager, Definition.Type);
                    if (autoMcpNames.Count > 0)
                    {
                        effectiveWhitelist = new List<string>(effectiveWhitelist);
                        effectiveWhitelist.AddRange(autoMcpNames);
                    }
                }

                if (effectiveWhitelist != null && effectiveWhitelist.Count == 0)
                {
                    toolChoice = "none";
                }
                else if (!string.IsNullOrWhiteSpace(toolChoiceOverride))
                {
                    toolChoice = toolChoiceOverride;
                }

                Logger.Info($"[Agent:{Definition.Name}] 本轮携带 {toolDefs.Count} 个工具定义(完整集)" +
                    (toolWhitelist != null ? $", 白名单={effectiveWhitelist?.Count ?? 0}个" : "") +
                    (toolChoice == "none" ? ", toolChoice=none" : ""));
            }

            return (toolDefs, effectiveWhitelist, toolChoice);
        }

        /// <summary>
        /// 构建工具调用签名：函数名 + 参数前缀（超过 200 字符截断）。
        /// 用于循环检测的去重计数与「同结果」比对；P3/P4/P5 共用同一口径，
        /// 避免各处自行拼接产生不一致的签名。
        /// </summary>
        /// <param name="tc">工具调用对象。</param>
        /// <returns>形如 <c>toolName|argumentPrefix</c> 的签名。</returns>
        private static string BuildToolCallSignature(ToolCall tc)
        {
            string arguments = tc.Function.Arguments ?? string.Empty;
            string prefix = arguments.Length > 200 ? arguments.Substring(0, 200) : arguments;
            return tc.Function.Name + "|" + prefix;
        }

        /// <summary>
        /// 构建工具循环终止时回填给用户的摘要正文（P5 终止决策的纯逻辑部分）。
        /// 把原有三处重复的「正文 + 历史操作 + 最后工具结果 + 终止原因」拼装收敛到一处；
        /// 各终止分支只在调用点决定是否附加历史/结果与尾部文案，跳转（break）仍留在外层循环。
        /// </summary>
        /// <param name="contentBuilder">本轮已产出的正文内容。</param>
        /// <param name="toolCalls">本轮工具调用列表。</param>
        /// <param name="toolResults">与 <paramref name="toolCalls"/> 对应的工具结果。</param>
        /// <param name="toolCallHistory">整轮循环内的历史操作摘要。</param>
        /// <param name="round">当前轮次，用于省略历史中本轮的轮次标签。</param>
        /// <param name="includeHistory">是否附加历史操作摘要。</param>
        /// <param name="includeToolResults">是否附加最后一批工具结果。</param>
        /// <param name="trailingText">终止原因文案（已本地化）。</param>
        /// <returns>可整体替换正文的终止摘要文本。</returns>
        private string BuildLoopTerminationSummary(
            StringBuilder contentBuilder,
            List<ToolCall> toolCalls,
            string[] toolResults,
            List<(int Round, string Summary)> toolCallHistory,
            int round,
            bool includeHistory,
            bool includeToolResults,
            string trailingText)
        {
            var summaryBuilder = new StringBuilder();
            summaryBuilder.Append(contentBuilder);

            if (includeHistory && toolCallHistory.Count > 0)
            {
                summaryBuilder.Append(LocalizationService.Instance["agent.priorOperationsHeader"]);
                int startRound = toolCallHistory[0].Round;
                foreach (var (r, summary) in toolCallHistory)
                {
                    string prefix = r == round ? "" : LocalizationService.Instance.Format("agent.roundLabel", r - startRound + 1);
                    summaryBuilder.AppendLine($"- {prefix} {summary}");
                }
            }

            if (includeToolResults)
            {
                for (int i = 0; i < toolResults.Length && i < toolCalls.Count; i++)
                {
                    string result = toolResults[i];
                    if (!string.IsNullOrWhiteSpace(result))
                    {
                        summaryBuilder.Append(LocalizationService.Instance.Format(
                            "agent.lastToolResultHeader", toolCalls[i].Function.Name, result));
                    }
                }
            }

            summaryBuilder.Append(trailingText);
            return summaryBuilder.ToString();
        }

        // ── 阶段 3 分解用的私有类型容器 ──
        // 说明：这些类型用于把 CallAiWithToolLoopAsync 内的局部变量集中承载，
        // 使后续 P1~P5 阶段方法可通过单一参数传递状态，避免长参数列表。

        /// <summary>
        /// <see cref="CallAiWithToolLoopAsync"/> 单次调用内的可变状态容器。
        /// 生命周期严格等于一次调用，禁止存为字段或 static。
        /// </summary>
        private sealed class ToolLoopState
        {
        }

        /// <summary>单轮流式调用的返回载体（正文、工具调用增量、用量与缓存统计）。</summary>
        private sealed class ToolLoopRoundResponse
        {
        }

        /// <summary>单轮工具调用的解析结果（通过白名单的工具集合与需要回写的消息）。</summary>
        private sealed class ToolInvocationBatch
        {
        }

        /// <summary>单轮工具批量执行的汇总（结果、去重映射与展示摘要）。</summary>
        private sealed class ToolExecutionSummary
        {
        }

        /// <summary>循环终止决策（继续、结束或触发移交）。</summary>
        private sealed class ToolLoopDecision
        {
        }
    }
}
