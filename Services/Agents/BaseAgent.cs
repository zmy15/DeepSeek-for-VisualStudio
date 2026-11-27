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
    /// 所有 Agent 的抽象基类。
    /// 提供：AI 调用、日志、权限请求、文件解析等共享能力。
    /// </summary>
    public abstract partial class BaseAgent : IDisposable
    {
        protected readonly DeepSeekApiService _apiService;
        protected readonly List<AgentLogEntry> _logs = new();

        /// <summary>Agent 元数据定义</summary>
        public AgentDefinition Definition { get; protected set; }

        /// <summary>当前执行上下文</summary>
        public AgentContext? Context { get; set; }

        /// <summary>内置工具服务引用（由 AgentFactory 注入）</summary>
        public BuiltInToolService? BuiltInTools { get; set; }

        /// <summary>MCP 管理器引用（由 AgentFactory 注入，用于执行 MCP 工具）</summary>
        public McpManagerService? McpManager { get; set; }

        /// <summary>记忆服务引用（由 AgentFactory 注入，用于程序化读写持久化记忆）</summary>
        public IMemoryService? MemoryService { get; set; }

        /// <summary>ExploreAgent 引用（由 AgentFactory 注入，用于 runSubagent 委派探索任务）</summary>
        public ExploreAgent? ExploreAgent { get; set; }

        /// <summary>日志事件</summary>
        public event Action<AgentLogEntry>? LogEntryAdded;

        /// <summary>权限请求事件</summary>
        public event Action<AgentPermissionRequest>? PermissionRequested;

        /// <summary>向用户提问事件（VisualStudio_askQuestions 工具使用）</summary>
        public event Action<AgentQuestionRequest>? QuestionsRequested;

        /// <summary>
        /// QuestionsRequested 事件的当前订阅者数量（诊断用）。
        /// 供外部代码（如 DeepSeekChatControl）在绑定/解绑后验证订阅状态。
        /// </summary>
        public int QuestionsRequestedHandlerCount => QuestionsRequested?.GetInvocationList().Length ?? 0;

        /// <summary>文件变更实时通知事件（编辑阶段逐文件推送）</summary>
        public event Action<AgentFileChangeEventArgs>? FileChangeNotified;

        /// <summary>
        /// 当前待确认的权限请求字典（RequestId → AgentPermissionRequest）。
        /// 支持多个并发权限请求（工具调用并行执行时每个工具可能独立请求权限）。
        /// 请使用 TryGetPendingPermission(requestId) 按 RequestId 精确查找。
        /// </summary>
        private readonly ConcurrentDictionary<string, AgentPermissionRequest> _pendingPermissions = new();

        /// <summary>
        /// 当前待回答的提问请求字典（RequestId → AgentQuestionRequest）。
        /// 支持多个并发提问请求。
        /// 请使用 TryGetPendingQuestion(requestId) 按 RequestId 精确查找。
        /// </summary>
        private readonly ConcurrentDictionary<string, AgentQuestionRequest> _pendingQuestions = new();

        /// <summary>
        /// 文件修改类工具全局串行锁。读工具仍可并行执行，但写文件必须按顺序落盘，
        /// 避免同一批工具调用同时写盘触发 IOException。
        /// </summary>
        private static readonly SemaphoreSlim FileMutationGate = new(1, 1);

        /// <summary>AI 通过 request_handoff 工具发起的待处理移交请求</summary>
        public HandoffRequest? PendingHandoffRequest { get; protected set; }

        /// <summary>
        /// 并行子代理执行信号量 — 限制同时运行的 Explore 子代理数量（默认 3）。
        /// 避免过多子代理同时读取文件导致 token 浪费和缓存冲突。
        /// </summary>
        private static readonly SemaphoreSlim SubagentConcurrencyLimiter = new(3, 3);

        /// <summary>
        /// 当前 Agent 最近一次可观测活动，用于子代理看门狗超时时报告卡住阶段。
        /// </summary>
        internal string CurrentActivity { get; private set; } =
            LocalizationService.Instance["agent.currentActivity.notStarted"];

        protected BaseAgent(DeepSeekApiService apiService, AgentType agentType)
        {
            _apiService = apiService ?? throw new ArgumentNullException(nameof(apiService));
            Definition = CreateDefinition(agentType);
        }

        #region Abstract

        /// <summary>
        /// 子类实现：定义 Agent 的元数据（名称、描述、工具、系统提示词等）。
        /// </summary>
        protected abstract AgentDefinition CreateDefinition(AgentType agentType);

        /// <summary>
        /// 子类实现：Agent 核心执行逻辑。
        /// </summary>
        /// <param name="userMessage">用户消息</param>
        /// <param name="context">执行上下文</param>
        /// <returns>执行结果</returns>
        public abstract Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context);

        #endregion

        #region Shared AI Call Methods

        /// <summary>
        /// 使用预构建消息列表调用 AI（支持 toolChoice 和 temperature 参数）。
        ///
        /// 缓存关键：此方法直接使用传入的 messages，不通过 BuildContextAwareMessages 重建。
        /// 这使得跨阶段的对话延续成为可能——
        /// 对齐阶段的 tool call 历史可以直接传递给设计阶段，DeepSeek Prefix Cache
        /// 可以匹配整个对齐对话前缀，而非仅匹配 system prompt。
        /// </summary>
        /// <param name="messages">预构建的完整消息列表（含 system + 历史 + user）</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="maxTokens">最大输出 token 数；传 null 表示不限制（不发送 max_tokens 字段）。</param>
        /// <param name="toolChoice">工具调用策略（"none" 禁用工具调用）</param>
        /// <param name="temperature">采样温度（0.0 = 确定性输出）</param>
        /// <param name="model">临时覆盖模型；null 使用当前端点模型</param>
        /// <param name="thinkingEnabled">临时覆盖思考模式；null 使用默认设置</param>
        /// <param name="onThinking">思考内容流式回调；null 时使用当前 Agent 回调</param>
        /// <param name="includeTools">是否随请求发送工具定义；路由等纯文本调用应设为 false</param>
        public async Task<string> CallAiWithMessagesAsync(
            List<ChatApiMessage> messages,
            CancellationToken ct,
            int? maxTokens = null,
            string? toolChoice = null,
            double? temperature = null,
            string? model = null,
            bool? thinkingEnabled = null,
            Action<string>? onThinking = null,
            bool includeTools = true)
        {
            // 常规调用传入完整工具集以保持 Prefix Cache 稳定；
            // 路由等明确不需要工具的调用必须完全省略 tools 和 tool_choice。
            var fullTools = includeTools ? TryGetFullToolSet() : null;
            var effectiveToolChoice = includeTools ? toolChoice : null;
            var sb = new StringBuilder();
            var effectiveOnThinking = onThinking ?? Context?.OnThinkingChunk;
            await foreach (var chunk in _apiService.ChatStreamAsync(messages, fullTools, ct, maxTokens, effectiveToolChoice, temperature, model, thinkingEnabled))
            {
                if (chunk.StartsWith("[THINKING]"))
                    effectiveOnThinking?.Invoke(chunk.Substring(10));
                else if (IsContentChunk(chunk))
                    sb.Append(chunk);
            }
            // ── 单次工具无关调用（路由/分类等）：不属于主流程对话内容，不写历史 ──
            CacheUsageLogger.Log(_apiService?.LastUsage);
            return sb.ToString().Trim();
        }

        /// <summary>
        /// 文本收尾阶段的公共入口：以 toolChoice:"auto" + 只读白名单走完整工具循环。
        ///
        /// 与 CallAiWithMessagesAsync 的区别：后者是单次调用，模型误发起工具调用时不会执行、
        /// 只会拿到空的 content（静默失败）。本方法复用 Plan/Ask 文本阶段的成熟策略——
        /// 保持 tool_choice 与主对话一致以命中 Prefix Cache，同时让误调用能在只读白名单内
        /// 正常收尾（读文件/搜索），并通过 reminderAfterFirstToolRound 提示模型停止调用。
        /// </summary>
        /// <param name="messages">预构建的完整消息列表</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="reminderAfterFirstToolRound">首次工具调用后追加的"禁止继续调用工具"提示</param>
        /// <param name="temperature">采样温度</param>
        /// <param name="persistToHistory">
        /// 是否把本轮 assistant/tool 消息与对账结果写入历史。
        /// 主流程调用保持默认 true；记忆判定、总结润色等一次性子任务必须传 false，
        /// 否则子任务的提示词与返回结果会进入 <c>_entries</c> 并污染下一轮请求。
        /// </param>
        public async Task<string> CallAiWithReadOnlyToolLoopAsync(
            List<ChatApiMessage> messages,
            CancellationToken ct,
            string? reminderAfterFirstToolRound = null,
            double? temperature = null,
            bool persistToHistory = true)
        {
            return await CallAiWithToolLoopAsync(
                messages,
                Context != null ? GetWorkspaceRoot(Context) : null,
                ct,
                toolWhitelist: CreateReadOnlyFinalTaskToolWhitelist(),
                toolChoiceOverride: "auto",
                temperature: temperature,
                noToolsReminderAfterFirstToolRound: reminderAfterFirstToolRound,
                persistToHistory: persistToHistory);
        }

        /// <summary>
        /// 安全获取完整工具集（Agent 未初始化时返回 null 降级）。
        /// </summary>
        private List<ToolDefinition>? TryGetFullToolSet()
        {
            try
            {
                if (BuiltInTools != null || (McpManager != null && McpManager.AllTools.Count > 0))
                    return GetFullToolSet();
            }
            catch { }
            return null;
        }

        /// <summary>一轮工具调用中观察到的验证结果。</summary>
        [Flags]
        internal enum VerificationRoundStatus
        {
            None = 0,
            TestsPassed = 1,
            TestsFailed = 2,
            BuildPassed = 4,
            BuildFailed = 8,
        }

        /// <summary>
        /// 汇总一轮工具调用中的测试/构建结果。
        /// 只有实际执行了测试且 failed=0 才算 TestsPassed。
        /// </summary>
        internal static VerificationRoundStatus ClassifyVerificationRound(
            IReadOnlyList<ToolCall> toolCalls,
            IReadOnlyList<string?> toolResults)
            => VerificationLoopDecider.ClassifyVerificationRound(toolCalls, toolResults);

        /// <summary>
        /// 根据上一轮未解决的测试失败与本轮验证结果，决定是否立即终止工具循环。
        /// 测试通过优先于构建；明确失败的测试不能被后续编译成功覆盖。
        /// </summary>
        internal static (bool ShouldTerminate, bool HasUnresolvedTestFailure, VerificationRoundStatus PreferredStatus)
            DecideVerificationLoopTermination(
                VerificationRoundStatus currentRound,
                bool hasUnresolvedTestFailure)
            => VerificationLoopDecider.DecideVerificationLoopTermination(currentRound, hasUnresolvedTestFailure);

        /// <summary>
        /// 找到与验证结论对应的工具结果索引，用于把完整结果作为本轮最终正文。
        /// </summary>
        private static int FindVerificationResultIndex(
            IReadOnlyList<ToolCall> toolCalls,
            VerificationRoundStatus preferredStatus)
            => VerificationLoopDecider.FindVerificationResultIndex(toolCalls, preferredStatus);

        internal static string BuildReasoningLoopRetryPrompt(string? originalUserQuestion)
        {
            string instruction =
                LocalizationService.Instance["agent.reasoningLoopInstruction"];

            if (string.IsNullOrWhiteSpace(originalUserQuestion))
                return instruction;

            return LocalizationService.Instance.Format(
                "agent.reasoningLoopWithQuestion", instruction, originalUserQuestion);
        }

        /// <summary>
        /// 记录本次工作流的 Cache 命中率汇总到日志（与 UI 显示一致）。
        /// 在所有轮次结束后调用，输出本次工作流（自上次 TakeCacheSnapshot 以来）的增量总计。
        /// </summary>
        private void LogTotalCacheHitRate(int finalRound)
        {
            try
            {
                var delta = _apiService?.GetCacheDelta() ?? (0, 0, 0, 0);
                long totalHit = delta.Hit;
                long totalMiss = delta.Miss;
                long totalCacheable = totalHit + totalMiss;
                if (totalCacheable == 0) return;

                double aggregateRate = (double)totalHit / totalCacheable;
                string level = aggregateRate >= 0.90 ? "🟢" : aggregateRate >= 0.50 ? "🟡" : aggregateRate >= 0.20 ? "🟠" : "🔴";

                Logger.Info(
                    $"[Cache] ═══════════════════════════════════════\n" +
                    $"[Cache] {level} 本次工作流汇总 ({finalRound} 轮)\n" +
                    $"[Cache]   Cache 命中率: {aggregateRate * 100:F1}%\n" +
                    $"[Cache]   命中: {totalHit:N0} tokens\n" +
                    $"[Cache]   未命中: {totalMiss:N0} tokens\n" +
                    $"[Cache]   Prompt: {delta.Prompt:N0} tokens\n" +
                    $"[Cache]   Completion: {delta.Completion:N0} tokens\n" +
                    $"[Cache] ═══════════════════════════════════════");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Cache] 记录汇总命中率异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 带超时保护的工具执行包装。
        /// 每个工具调用单独计时，超时则返回错误信息而非阻塞整个循环。
        /// 对于需要用户交互的命令（run_in_terminal、delete_file、VisualStudio_askQuestions），不设超时。
        /// </summary>
        /// <param name="tc">工具调用定义</param>
        /// <param name="workspaceRoot">工作区根目录</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="timeout">超时时间（用户交互命令忽略此参数）</param>
        /// <returns>工具执行结果字符串</returns>
        private async Task<string> ExecuteToolWithTimeoutAsync(
            ToolCall tc, string? workspaceRoot, CancellationToken ct, TimeSpan timeout)
        {
            // 需要用户交互的命令不设超时，直接执行
            if (IsInteractiveTool(tc.Function.Name))
            {
                try
                {
                    return await ExecuteToolAsync(tc.Function.Name, tc.Function.Arguments, workspaceRoot, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[Agent:{Definition.Name}] 工具 {tc.Function.Name} 执行异常: {ex.Message}", ex);
                    return LocalizationService.Instance.Format("agent.toolExecutionFailed", ex.Message);
                }
            }

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout);

                var execTask = ExecuteToolAsync(tc.Function.Name, tc.Function.Arguments, workspaceRoot, timeoutCts.Token);
                var completed = await Task.WhenAny(execTask, Task.Delay(timeout, ct)).ConfigureAwait(false);

                if (completed == execTask)
                {
                    return await execTask.ConfigureAwait(false);
                }
                else
                {
                    // 超时 — 取消工具执行
                    timeoutCts.Cancel();
                    Logger.Warn($"[Agent:{Definition.Name}] Timeout: 工具 {tc.Function.Name} 执行超时 ({timeout.TotalSeconds:F0}s)，已终止");
                    return LocalizationService.Instance.Format(
                        "agent.toolTimeoutSkipped", tc.Function.Name, timeout.TotalSeconds.ToString("F0"));
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 仅由 timeoutCts 触发（非外部 ct）
                Logger.Warn($"[Agent:{Definition.Name}] Timeout: 工具 {tc.Function.Name} 执行超时 ({timeout.TotalSeconds:F0}s)，已终止");
                return LocalizationService.Instance.Format(
                        "agent.toolTimeoutSkipped", tc.Function.Name, timeout.TotalSeconds.ToString("F0"));
            }
            catch (Exception ex)
            {
                Logger.Error($"[Agent:{Definition.Name}] 工具 {tc.Function.Name} 执行异常: {ex.Message}", ex);
                return LocalizationService.Instance.Format("agent.toolExecutionFailed", ex.Message);
            }
        }

        #endregion

        #region Shared Utility Methods

        /// <summary>
        /// 需要用户确认才能修改的项目文件扩展名 / 文件名集合。
        /// 修改这些文件可能影响项目结构，需要用户明确许可。
        /// 实现已下沉到 <see cref="AgentPathResolver"/>，此处保留 protected 转发以维持子类契约。
        /// </summary>
        protected static IReadOnlyCollection<string> ProjectFileExtensions => AgentPathResolver.ProjectFileExtensions;

        /// <summary>
        /// 检查文件是否为项目配置文件（需要用户确认才能修改）。
        /// EditAgent / BuildAgent 共享使用。
        /// </summary>
        protected static bool IsProjectFile(string? filePath)
            => AgentPathResolver.IsProjectFile(filePath);

        /// <summary>
        /// 从 AgentContext 解析工作区根目录。
        /// BuildAgent / EditAgent / ExploreAgent 共享使用。
        /// </summary>
        /// <remarks>
        /// 签名必须与下沉前逐字一致：<c>BuildAgent</c> 用 <c>new</c> 显式隐藏了同名方法，
        /// 改变参数或可见性会触发 CS0109，并使 BuildAgent 内部调用静默切到基类实现。
        /// </remarks>
        protected static string GetWorkspaceRoot(AgentContext context)
            => AgentPathResolver.GetWorkspaceRoot(context);

        /// <summary>
        /// 清理消息列表中不完整的工具调用链。
        /// 与 ChatStreamAsync Rule 5 使用相同算法：对每个 assistant(tool_calls)，
        /// 向前扫描匹配的 tool 结果；遇到非 tool 消息停止。
        /// 必须收齐全部 tool_call_id，缺失的 tool_calls 单独剥离；全部缺失时清空整组。
        /// 
        /// 在 Handoff (ForwardedMessages) 时调用，确保传递给目标 Agent 的消息列表
        /// 在后续每轮 API 调用中产生一致的清洗结果，避免缓存前缀断裂。
        /// </summary>
        internal static List<ChatApiMessage> CleanIncompleteToolChains(List<ChatApiMessage> messages)
        {
            // 浅克隆避免修改原始消息（与 ChatStreamAsync 保持一致）
            var result = ChatMessageCloner.CloneAll(messages);

            int strippedCount = 0;
            for (int i = 0; i < result.Count; i++)
            {
                var m = result[i];
                if (m.Role != "assistant" || m.ToolCalls == null || m.ToolCalls.Count == 0)
                    continue;

                var expectedIds = new HashSet<string>(
                    m.ToolCalls
                        .Where(tc => !string.IsNullOrEmpty(tc.Id))
                        .Select(tc => tc.Id!),
                    StringComparer.Ordinal);
                var resolvedIds = new HashSet<string>(StringComparer.Ordinal);
                for (int j = i + 1; j < result.Count; j++)
                {
                    var next = result[j];
                    if (next.Role != "tool") break;

                    if (!string.IsNullOrEmpty(next.ToolCallId)
                        && expectedIds.Contains(next.ToolCallId))
                    {
                        resolvedIds.Add(next.ToolCallId);
                    }

                    if (resolvedIds.Count == expectedIds.Count)
                        break;
                }

                bool hasCompleteToolChain =
                    expectedIds.Count > 0 && resolvedIds.Count == expectedIds.Count;
                if (!hasCompleteToolChain)
                {
                    var tcNames = string.Join(", ", m.ToolCalls.Select(tc => tc.Function?.Name ?? "?"));
                    var missingTools = string.Join(", ", m.ToolCalls
                        .Where(tc => string.IsNullOrEmpty(tc.Id) || !resolvedIds.Contains(tc.Id))
                        .Select(tc => $"{tc.Function?.Name ?? "?"}({tc.Id ?? "?"})"));
                    int originalToolCount = m.ToolCalls.Count;

                    if (resolvedIds.Count == 0)
                    {
                        Logger.Info(
                            $"[Agent] CleanIncompleteToolChains: 剥离 assistant[{i}] " +
                            $"tool_calls=[{tcNames}] missing=[{missingTools}] — 无匹配 tool 结果");
                        m.ToolCalls = null;
                        m.ReasoningContent = null;
                        strippedCount += originalToolCount;
                    }
                    else
                    {
                        m.ToolCalls = m.ToolCalls
                            .Where(tc => !string.IsNullOrEmpty(tc.Id) && resolvedIds.Contains(tc.Id))
                            .ToList();
                        strippedCount += originalToolCount - m.ToolCalls.Count;
                        Logger.Info(
                            $"[Agent] CleanIncompleteToolChains: assistant[{i}] " +
                            $"保留 {m.ToolCalls.Count}/{originalToolCount} 个已配对 tool_calls, " +
                            $"missing=[{missingTools}]");
                    }
                }
            }

            // 移除剥离后为空的 assistant
            int beforeRemove = result.Count;
            result.RemoveAll(m =>
                m.Role == "assistant"
                && string.IsNullOrEmpty(m.Content)
                && (m.ToolCalls == null || m.ToolCalls.Count == 0));
            int removedEmpty = beforeRemove - result.Count;

            if (strippedCount > 0 || removedEmpty > 0)
            {
                Logger.Info($"[Agent] CleanIncompleteToolChains: stripped={strippedCount} removedEmpty={removedEmpty} finalCount={result.Count}");
            }

            return result;
        }

        /// <summary>
        /// 检测 AI 回复中是否表明编译仍存在错误。
        /// 使用多层策略：Error: 前缀 → 错误代码正则 → MSBuild 摘要 → 关键词匹配。
        /// BuildAgent / EditAgent 共享使用。
        /// </summary>
        protected static bool HasBuildFailure(string aiResponse)
        {
            if (string.IsNullOrWhiteSpace(aiResponse))
                return false;

            // ── 策略1：工具级失败标志 Error: ──
            if (aiResponse.Contains("Error: 构建失败") || aiResponse.Contains("Error: 编译失败")
                || aiResponse.Contains("Error: build") || aiResponse.Contains("Error: Build"))
                return true;

            // ── 策略2：MSBuild / 编译器错误代码 ──
            if (System.Text.RegularExpressions.Regex.IsMatch(aiResponse,
                @"\berror\s+(CS|C|LNK|MSB|BC|FS|TS|RUST)\d+\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return true;

            // ── 策略3：MSBuild 摘要失败模式（Singleline 支持跨行匹配）──
            if (aiResponse.Contains("Build FAILED")
                || System.Text.RegularExpressions.Regex.IsMatch(aiResponse,
                    @"\b0\s+succeeded.*\b[1-9]\d*\s+failed\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    | System.Text.RegularExpressions.RegexOptions.Singleline))
                return true;

            // ── 策略4：关键词匹配（排除误报）──
            string lower = aiResponse.ToLowerInvariant();
            bool hasFailure = lower.Contains("build failed")
                || lower.Contains("构建失败")
                || lower.Contains("编译失败")
                || lower.Contains("error cs")
                || lower.Contains("error lnk")
                || lower.Contains("error msb");

            bool hasSuccess = lower.Contains("build succeeded")
                || lower.Contains("0 个错误")
                || lower.Contains("0 errors")
                || lower.Contains("编译通过")
                || lower.Contains("构建成功");

            return hasFailure && !hasSuccess;
        }

        /// <summary>
        /// 剥离 DeepSeek 泄露到 content 中的 DSML/工具调用 XML 标签。
        /// PlanAgent / BaseAgent 共享使用。
        /// </summary>
        protected static string NormalizeDsmlMarkers(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 模型/网关可能返回全角竖线，而不是 ASCII '|'。
            var normalized = text
                .Replace('\uFF5C', '|')
                .Replace('\uFE15', '|')
                .Replace('\u2502', '|');

            // 同时兼容 <||DSML|| invoke> 与 <|DSML| invoke>。
            normalized = Regex.Replace(
                normalized,
                @"<\s*\|{1,2}\s*DSML\s*\|{1,2}\s*",
                "<",
                RegexOptions.IgnoreCase);
            normalized = Regex.Replace(
                normalized,
                @"</\s*\|{1,2}\s*DSML\s*\|{1,2}\s*",
                "</",
                RegexOptions.IgnoreCase);

            return normalized;
        }

        protected static string StripDsmlContent(string text, bool removeResidualAttributes = true)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            // ── 移除完整的 DSML/工具调用 XML 块（含嵌套内容）──
            // 支持两种格式：
            //   1. 标准 XML: <tagname>...</tagname>
            //   2. DeepSeek 管道分隔: <|tagname|>...</|tagname|>
            string[] blockTags = {
                "DSML", "calls", "function_calls?", "tool_calls?", "invoke", "parameter",
                "VisualStudio_askQuestions", "runSubagent", "tool_result",
                "file_search", "grep_search", "list_dir", "read_file",
                "semantic_search", "fetch_webpage", "web_search", "run_in_terminal",
                "create_file", "replace_string_in_file",
                "create_directory", "runSubagent", "mcp_\\w+", "github_\\w+",
                // DeepSeek DSML wrapper tags
                "response", "result", "output", "answer"
            };

            string result = NormalizeDsmlMarkers(text);
            foreach (var tag in blockTags)
            {
                // ── 格式1：标准 XML 自闭合标签 <tag ... /> ──
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    @"<\s*" + tag + @"(\s+[^>]*)?\s*/\s*>",
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

                // ── 格式1：标准 XML 配对标签 <tag>...</tag> ──
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    @"<\s*" + tag + @"(\s+[^>]*)?\s*>.*?</\s*" + tag + @"\s*>",
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

                // ── 格式2：管道分隔自闭合标签 <|tag| ... /> ──
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    @"<\|\s*" + tag + @"\s*\|(\s+[^>]*)?\s*/\s*>",
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

                // ── 格式2：管道分隔配对标签 <|tag|>...</|tag|> ──
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    @"<\|\s*" + tag + @"\s*\|(\s+[^>]*)?\s*>.*?</\|\s*" + tag + @"\s*\|?\s*>",
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            }

            // ── 移除残留的独立开标签/闭标签（标准 XML）──
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"</?\s*(?:DSML|calls|function_calls?|tool_calls?|invoke|parameter|VisualStudio_askQuestions|runSubagent|tool_result|response|result|output|answer)[^>]*>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // ── 移除残留的独立开标签/闭标签（管道分隔格式）──
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"</?<|\|\s*(?:DSML|function_calls?|tool_calls?|invoke|parameter|VisualStudio_askQuestions|runSubagent|tool_result|response|result|output|answer)[^>]*>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // 更精确的管道分隔残留标签
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"<\|?\s*(?:DSML|calls|function_calls?|tool_calls?|invoke|parameter|response|result|output|answer)[^>]*\|?>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // ── 移除  response / thinking / analysis / reasoning / plan / reflection 伪标签 ──
            // 支持标准XML和管道分隔两种格式
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"</?\s*(?:response|thinking|analysis|reasoning|plan|reflection)[^>]*>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"<\|?\s*(?:response|thinking|analysis|reasoning|plan|reflection)[^>]*\|?>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (removeResidualAttributes)
            {
                // ── 额外清理：移除 DSML 属性格式的标签残留 ──
                // 例如: name="tool_name" 或 type="function" 孤立属性
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    @"\b(?:name|type|arguments|function)\s*=\s*""[^""]*""",
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            return result.Trim();
        }

        /// <summary>
        /// 判断工具是否为文件修改类工具（可能修改用户文件）。
        /// </summary>
        private static bool IsFileModifyingTool(string toolName)
            => ToolClassifier.IsFileModifyingTool(toolName);

        /// <summary>
        /// 判断工具是否为文件访问类工具（读/写/搜索/列表，涉及文件路径）。
        /// 用于越权检测——AI 访问项目外路径时需用户审批。
        /// </summary>
        private static bool IsFileAccessingTool(string toolName)
            => ToolClassifier.IsFileAccessingTool(toolName);

        /// <summary>
        /// 获取工具操作的中文名称（用于审批提示）。
        /// </summary>
        private static string GetToolOperationName(string toolName)
            => ToolClassifier.GetToolOperationName(toolName);

        /// <summary>
        /// 判断目标路径是否在工作区之外。
        /// 支持绝对路径和相对路径判断。
        /// </summary>
        private static bool IsPathOutsideWorkspace(string targetPath, string workspaceRoot)
            => ToolClassifier.IsPathOutsideWorkspace(targetPath, workspaceRoot);

        /// <summary>
        /// 从各种工具参数中提取文件/目录路径。
        /// 比 ExtractFilePathFromToolArgs 更通用，覆盖 file_search/grep_search 的 includePattern 等。
        /// </summary>
        private static string? ExtractAnyPathFromToolArgs(string toolName, string argumentsJson)
            => ToolClassifier.ExtractAnyPathFromToolArgs(toolName, argumentsJson);

        /// <summary>
        /// 判断工具是否需要用户交互（不设工具层超时）。
        /// 构建类工具也在此列，因为其执行时间不可预测，超时由 BuildService 内部自行管理。
        /// read_file / list_dir 等只读工具也包含在内，因为它们可能触发项目外路径权限弹窗，
        /// 用户响应时间不可预测，硬超时会导致权限拒绝结果丢失。
        /// </summary>
        private static bool IsInteractiveTool(string toolName)
            => ToolClassifier.IsInteractiveTool(toolName);

        internal static string NormalizeToolName(string toolName)
            => ToolClassifier.NormalizeToolName(toolName);

        /// <summary>
        /// 根据工具类型返回合适的超时时间（仅对非交互式、非构建类工具生效）。
        /// 交互式工具和构建类工具由 IsInteractiveTool 直接跳过超时。
        /// 分档策略见 <see cref="Services.Tools.ToolTimeoutPolicy"/>（P2，序号 22）。
        /// </summary>
        private static TimeSpan GetToolTimeout(string toolName)
            => ToolClassifier.GetToolTimeout(toolName);

        /// <summary>
        /// 判断工具是否为写操作（编辑/创建/删除文件）。
        /// </summary>
        private static bool IsWriteTool(string toolName)
            => ToolClassifier.IsWriteTool(toolName);

        /// <summary>判断工具是否属于 Ask Agent 的直接代码库探索工具。</summary>
        internal static bool IsDirectExplorationTool(string toolName)
            => ToolClassifier.IsDirectExplorationTool(toolName);

        /// <summary>
        /// 对同一批次中的工具调用进行去重。
        /// 相同函数名 + 相同参数（规范化 JSON）的调用只保留第一个，
        /// 后续重复调用映射到第一个的结果。
        /// </summary>
        /// <returns>
        /// dedupedIndices: 去重后需要实际执行的 toolCalls 索引列表。
        /// dedupMapping: 源索引 → 重复索引列表的映射（用于将结果复制到重复调用）。
        /// </returns>
        private static (List<int> dedupedIndices, Dictionary<int, List<int>> dedupMapping) DeduplicateToolCalls(
            List<ToolCall> toolCalls)
            => ToolCallDeduplicator.DeduplicateToolCalls(toolCalls);

        /// <summary>
        /// 规范化工具参数 JSON：移除空格/换行等无意义差异，
        /// 确保相同语义的参数能匹配到同一个去重 key。
        /// </summary>
        private static string NormalizeToolArguments(string argumentsJson)
            => ToolCallDeduplicator.NormalizeToolArguments(argumentsJson);

        /// <summary>
        /// 从工具参数 JSON 中提取目标文件路径（支持多种字段名和集合类型）。
        /// 覆盖所有文件相关工具的参数模式。
        /// </summary>
        private static string? ExtractFilePathFromToolArgs(string toolName, string argumentsJson)
            => ToolCallDeduplicator.ExtractFilePathFromToolArgs(toolName, argumentsJson);

        /// <summary>
        /// Resolves the per-invocation tool-loop limit. This value intentionally
        /// does not include the global cache round counter.
        /// </summary>
        internal static int ResolveEffectiveToolRoundLimit(
            int configuredSafetyLimit,
            int? maxToolRounds)
            => ExecutionLimits.ResolveEffectiveToolRoundLimit(configuredSafetyLimit, maxToolRounds);

        internal static TimeSpan ResolveSubagentWatchdogTimeout(int configuredSeconds)
            => ExecutionLimits.ResolveSubagentWatchdogTimeout(configuredSeconds);

        /// <summary>
        /// Applies a configured execution budget with a deterministic fallback
        /// and bounds. A minimum of zero allows nullable or unlimited budgets.
        /// </summary>
        internal static int NormalizeExecutionSetting(
            int configuredValue,
            int fallbackValue,
            int minimum,
            int maximum)
            => ExecutionLimits.NormalizeExecutionSetting(configuredValue, fallbackValue, minimum, maximum);

        /// <summary>
        /// Appends a deterministic execution-guard stop message to the current
        /// response without throwing away already produced content.
        /// </summary>
        private static void AppendExecutionStop(
            StringBuilder contentBuilder,
            AgentExecutionDecision decision)
        {
            if (contentBuilder.Length > 0)
                contentBuilder.AppendLine();

            contentBuilder.Append("> ");
            contentBuilder.Append(decision.Message);
        }

        /// <summary>
        /// 在将 reasoning 写入缓冲区之前检查循环和长度上限。
        /// </summary>
        private static void ThrowIfReasoningLoopDetected(string chunk, ReasoningLoopGuard guard)
        {
            if (!chunk.StartsWith("[THINKING]", StringComparison.Ordinal))
                return;

            var result = guard.Inspect(chunk.Substring(10));
            if (result.ShouldBreak)
                throw new ReasoningLoopDetectedException(result);
        }

        /// <summary>
        /// 处理流式响应的单个 chunk，分派到 reasoning/content/tool_call 缓冲区。
        /// 与 DeepSeekChatControl.Messaging.cs 中的逻辑一致。
        /// </summary>
        private static void ProcessStreamChunk(
            string chunk,
            StringBuilder reasoningBuilder,
            StringBuilder contentBuilder,
            Dictionary<int, Models.ToolCallAccumulator> toolCallAccumulator,
            Action<string>? onThinking,
            Action<string>? onContent)
        {
            if (chunk.StartsWith("[THINKING]"))
            {
                var thinking = chunk.Substring(10);
                reasoningBuilder.Append(thinking);
                onThinking?.Invoke(thinking);
            }
            else if (chunk.StartsWith("[TOOL_CALL]"))
            {
                var tcJson = chunk.Substring(11);
                try
                {
                    var deltas = System.Text.Json.JsonSerializer.Deserialize<List<ToolCallDelta>>(tcJson);
                    if (deltas != null)
                    {
                        foreach (var delta in deltas)
                        {
                            if (!toolCallAccumulator.ContainsKey(delta.Index))
                                toolCallAccumulator[delta.Index] = new Models.ToolCallAccumulator();
                            var acc = toolCallAccumulator[delta.Index];
                            if (!string.IsNullOrEmpty(delta.Id)) acc.Id = delta.Id!;
                            if (!string.IsNullOrEmpty(delta.Type)) acc.Type = delta.Type;
                            if (delta.Function != null)
                            {
                                if (!string.IsNullOrEmpty(delta.Function.Name)) acc.FunctionName = delta.Function.Name;
                                if (!string.IsNullOrEmpty(delta.Function.Arguments)) acc.ArgumentsBuilder.Append(delta.Function.Arguments);
                            }
                        }
                    }
                }
                catch (System.Text.Json.JsonException) { }
            }
            else if (chunk.StartsWith("[CACHE]"))
            {
                // Cache 统计信息 — 过滤，不混入正文
            }
            else if (chunk.StartsWith("[TITLE]"))
            {
                // 官方会话标题元数据 — 由 CallAiWithToolLoopAsync 单独分派，不混入正文
            }
            else
            {
                contentBuilder.Append(chunk);
                onContent?.Invoke(chunk);
            }
        }

        /// <summary>
        /// 判断 chunk 是否为正文内容（过滤 [THINKING]/[TOOL_CALL]/[CACHE] 等控制前缀）。
        /// </summary>
        private static bool IsContentChunk(string chunk)
        {
            return !chunk.StartsWith("[THINKING]")
                && !chunk.StartsWith("[TOOL_CALL]")
                && !chunk.StartsWith("[CACHE]")
                && !chunk.StartsWith("[TITLE]");
        }

        /// <summary>
        /// 从 AI 返回结果中提取 JSON（可能被 markdown 代码块包裹）。
        /// 同时剥离 DeepSeek 可能在 content 中输出的 XML 风格标签。
        /// 当 tools=null 时，DeepSeek 会将工具调用意图以 DSML/function_call 等标签泄露到 content。
        /// </summary>
        protected static string ExtractJsonFromMarkdown(string text)
            => AgentJsonHelper.ExtractJsonFromMarkdown(text);

        /// <summary>
        /// 尝试修复因 max_tokens 不足而被截断的 JSON。
        /// 策略：计数字符串外的括号不平衡数，在末尾补充缺失的引号/方括号/花括号。
        /// </summary>
        /// <param name="json">可能截断的 JSON 字符串</param>
        /// <returns>修复后的 JSON 字符串。如果无法安全修复则返回原字符串。</returns>
        protected static string TryRepairTruncatedJson(string json)
            => AgentJsonHelper.TryRepairTruncatedJson(json);

        /// <summary>
        /// 快速检查 JSON 的花括号和方括号是否平衡。
        /// </summary>
        private static bool IsJsonBracketBalanced(string json)
            => AgentJsonHelper.IsJsonBracketBalanced(json);

        /// <summary>
        /// 计算文本行数。
        /// </summary>
        protected static int CountLines(string text)
            => AgentJsonHelper.CountLines(text);

        /// <summary>
        /// 使用 CodeDiffService 精确计算新增行数和删除行数。
        /// 替代之前仅用净行数差值的错误算法。
        /// </summary>
        protected static void CountDiffLines(string oldText, string newText, out int added, out int removed)
            => AgentJsonHelper.CountDiffLines(oldText, newText, out added, out removed);

        /// <summary>
        /// 解析 AI 返回的文件路径（支持相对路径和 Unix 风格路径）。
        /// </summary>
        protected static string ResolveFilePath(string filePath, string? solutionPath)
        {
            // 实现已下沉：路径解析规则（绝对路径直返 / 方案目录拼接 / 同名文件搜索）见 AgentPathResolver.ResolveFilePath。
            return AgentPathResolver.ResolveFilePath(filePath, solutionPath);
        }

        #endregion

        #region IDisposable

        public virtual void Dispose()
        {
            _logs.Clear();
        }

        #endregion

        #region Shared Helpers

        /// <summary>
        /// 规范化文件路径（统一分隔符、去除尾部空格），用于 GroupBy 合并。
        /// </summary>
        protected static string NormalizePath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return filePath;
            return AgentPathResolver.NormalizePath(filePath);
        }

        /// <summary>
        /// 判断两次工具调用结果是否实质相同（用于循环检测）。
        /// 对错误类工具（get_errors、build_solution）提取核心错误行比较，
        /// 避免因时间戳等噪声导致误判。
        /// </summary>
        private static bool AreResultsSubstantiallySame(string prevResult, string currentResult)
        {
            if (string.IsNullOrEmpty(prevResult) && string.IsNullOrEmpty(currentResult))
                return true;
            if (string.IsNullOrEmpty(prevResult) || string.IsNullOrEmpty(currentResult))
                return false;

            // ── 快速路径：完全一致 ──
            string a = prevResult.Trim();
            string b = currentResult.Trim();
            if (a == b) return true;

            // ── 提取错误行（以 "error " 或 "错误" 开头的行）进行比较 ──
            var prevErrors = ExtractErrorLines(a);
            var currErrors = ExtractErrorLines(b);

            if (prevErrors.Count == 0 && currErrors.Count == 0)
            {
                // 无错误行时，比较长度相似度（容忍 10% 差异）
                int lenDiff = Math.Abs(a.Length - b.Length);
                int maxLen = Math.Max(a.Length, b.Length);
                return maxLen > 0 && (double)lenDiff / maxLen < 0.1;
            }

            if (prevErrors.Count != currErrors.Count)
                return false;

            // 逐行比较错误内容
            for (int i = 0; i < prevErrors.Count; i++)
            {
                if (!string.Equals(prevErrors[i], currErrors[i], StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// 从工具结果中提取核心错误行（忽略时间戳、构建配置等可变前缀）。
        /// </summary>
        private static List<string> ExtractErrorLines(string result)
        {
            var errors = new List<string>();
            foreach (var line in result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim();
                // 匹配典型的编译错误行格式: "error CS1234:" 或 "错误 CS1234:" 或 "error :" 等
                if (trimmed.Contains("error", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.StartsWith("0 Error", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.StartsWith("0 错误", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.Contains("0 Error", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.Contains("0 错误", StringComparison.OrdinalIgnoreCase))
                {
                    // 去掉行号前缀（如 "  (123,45): " 格式），保留核心错误描述
                    errors.Add(trimmed);
                }
            }
            return errors;
        }

        #endregion
    }
}
