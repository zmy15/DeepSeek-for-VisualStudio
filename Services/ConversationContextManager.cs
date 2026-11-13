using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 统一上下文管理器 — 负责多轮对话历史的存储、拼接与 Token 预算管理。
    /// 
    /// 核心职责：
    /// 1. 单一数据源：所有 API 调用所需的对话历史由此类统一管理
    /// 2. reasoning_content 回传规则（严格遵守 DeepSeek 思考模式协议）：
    ///    - 所有历史 assistant 消息均逐条原样回传 reasoning_content
    ///    - 不做截断、压缩或跨工具轮次聚合
    ///    - 缺失该字段会让 DeepSeek 思考模式在停止、编辑重发等场景返回 HTTP 400
    /// 3. Token 预算估算与自动压缩（不再直接截断/删除旧消息）
    /// 4. 轮次（Turn）边界追踪
    /// 5. RAG 上下文注入点
    /// 6. 上下文统计与使用率监控
    /// 
    /// DeepSeek 最大上下文窗口: 1M tokens（1,000,000）
    /// 默认预算: 900K tokens（留 100K 给模型输出）
    /// 
    /// 参考：
    /// - https://api-docs.deepseek.com/zh-cn/guides/multi_round_chat
    /// - https://api-docs.deepseek.com/zh-cn/guides/thinking_mode
    /// </summary>
    public class ConversationContextManager : IConversationContextManager
    {
        /// <summary>内部对话历史存储（单一数据源）</summary>
        private readonly List<ContextEntry> _entries = new();

        /// <summary>系统提示词（独立存储，不参与 turn 管理）</summary>
        private string? _systemPrompt;

        /// <summary>
        /// 已冻结的系统提示词（session 初始化时由 SetFixedSystemPrompt() 设置）。
        /// 一旦冻结，整个会话期间不再改变，保障 messages[0] 前缀稳定性，
        /// 使 DeepSeek 的自动前缀缓存可以持续命中。
        /// </summary>
        private string? _fixedSystemPrompt;

        /// <summary>IDE 实时态上下文（P1-A：活动文件/光标/选区/符号/诊断摘要，注入 volatile 块最前）</summary>
        private string? _ideContext;

        /// <summary>当前解决方案上下文（注入 volatile 块最前）</summary>
        private string? _solutionContext;

        /// <summary>Skill 发现上下文（独立存储）</summary>
        private string? _skillContext;

        /// <summary>始终注入的技能上下文（独立存储，每次对话均注入完整指令）</summary>
        private string? _alwaysInjectSkillsContext;

        /// <summary>RAG 检索上下文（独立存储，注入为 system 消息）</summary>
        private string? _ragContext;

        /// <summary>记忆上下文（独立存储，注入为 system 消息）</summary>
        private string? _memoryContext;

        /// <summary>当前工作区根目录（用于 Working Set 摘要相对路径生成）</summary>
        private string? _workspaceRoot;

        /// <summary>当前 Token 估算计数器（字符级原始估算，未校准）</summary>
        private int _estimatedTokens;

        /// <summary>上下文条目单调递增 ID，用于判断压缩边界内是否仍有新内容。</summary>
        private long _nextEntryId;

        /// <summary>已经压缩过的最大条目 ID。</summary>
        private long _lastCompressedEntryId;

        /// <summary>是否需要在本次完整对话结束后提示用户切换新对话。</summary>
        private bool _conversationResetNoticePending;

        /// <summary>从会话持久化恢复、等待 compressor 初始化后注入的摘要。</summary>
        private readonly List<CompressedTurnSummary> _pendingCompressedSummaries = new();

        /// <summary>Token 估算校准系数（基于 API 实际 usage 的指数移动平均，1.0 = 无校准）</summary>
        private double _calibrationFactor = 1.0;

        /// <summary>
        /// 上一次已参与校准的 API prompt_tokens。
        /// 同一份 usage 会被两条链路各上报一次（BaseAgent 工具循环内按轮上报、
        /// UI 层 RefreshConsumptionDisplay 收尾上报），若不做去重，同一 ratio 会被
        /// EMA 连续叠加两次（α=0.15），导致估算因子被过快拉向单次观测值并在并发下抖动。
        /// </summary>
        private long _lastCalibratedPromptTokens;

        /// <summary>校准样本权重（EMA α 值，0.15 约等于最近 ~7 次调用的加权）</summary>
        private const double CalibrationAlpha = 0.15;

        /// <summary>上下文压缩服务（可选注入）</summary>
        private ContextCompressorService? _compressor;

        /// <summary>压缩标记：是否正在压缩中</summary>
        private bool _isCompressing;

        /// <summary>
        /// 压缩状态变化事件：参数为“是否正在压缩”和当前上下文使用率（0~100）。
        /// 供 UI 在耗时的 LLM 摘要压缩期间显示进度提示。
        /// </summary>
        public event Action<bool, double>? CompressionStateChanged;

        /// <summary>当前是否正在执行上下文压缩。</summary>
        public bool IsCompressing => _isCompressing;

        /// <summary>活跃文件追踪器（可选注入，用于 Working Set 摘要）</summary>
        private IActiveFileTracker? _activeFileTracker;

        /// <summary>当前使用的模型标识（用于裁剪器的大上下文模型检测）</summary>
        public string CurrentModel { get; set; } = "deepseek-v4";

        /// <summary>Token 预算上限（默认 900K，DeepSeek 上下文窗口为 1M，留 100K 给输出）</summary>
        public int TokenBudget { get; set; } = 900_000;

        /// <summary>当超过 Token 预算时自动压缩的最旧轮次数（此值在压缩模式下仅作用为最小保留轮次）</summary>
        public int AutoTrimTurns { get; set; } = 3;

        // ── 缓存友好窗口（v1.1.10 稳定前缀窗口优化）──
        //     将 messages 数组分为「固定前缀区」和「缓存窗口区」，
        //     超出窗口的旧轮次自动压缩为摘要，确保 messages 数组大小稳定，
        //     使 DeepSeek 服务端能快速检测公共前缀并落盘缓存。
        //     参考：DeepSeek API 文档 — 公共前缀检测需要 2-3 次请求才能触发落盘

        /// <summary>
        /// 缓存友好窗口：消息列表超过此估算 token 数时自动裁剪旧轮次并压缩为摘要。
        /// 默认 800K tokens，减少压缩频率、增大单次压缩范围，
        /// 降低前缀缓存因压缩导致的断裂次数。
        /// 设为 0 禁用缓存窗口裁剪。
        /// </summary>
        public int CacheWindowMaxTokens { get; set; } = 800_000;

        /// <summary>
        /// 缓存友好窗口：最多保留的最近轮次数。
        /// 默认 30 轮，配合 CacheWindowMaxTokens 使用（两者中限制更严格者生效）。
        /// </summary>
        public int CacheWindowMaxTurns { get; set; } = 30;

        /// <summary>
        /// 缓存友好窗口：最多保留的消息条目数（兜底保护）。
        /// 默认 500 条，防止单轮大量工具调用导致窗口失控。
        /// 设为 0 不限条目数。
        /// </summary>
        public int CacheWindowMaxEntries { get; set; } = 500;

        /// <summary>
        /// 压缩后保留轮次的比例（0.0~1.0）。
        /// 触发压缩时不只压缩到窗口边界，而是进一步压缩到窗口大小的此比例，
        /// 为后续对话增长留出余量，减少频繁压缩导致的缓存断裂。
        /// 默认 0.5：窗口 30 轮 → 压缩后保留 ~15 轮。
        /// </summary>
        public double CompressionAggressiveness { get; set; } = 0.5;

        // ──  缓存边界快照（v1.1.10）──
        //     Agent Handoff 时，在注入过渡消息前保存 _entries 的快照索引，
        //     目标 Agent 可调用 BuildApiMessagesUpToSnapshot() 仅包含边界前的历史，
        //     使前缀缓存跨 Agent 切换时仍能命中。
        //     null = 未设置快照（正常会话模式）。

        /// <summary>缓存边界快照：应在 Handoff 前由源 Agent 设置</summary>
        private int? _cacheSnapshotEntryIndex;

        /// <summary>
        ///  缓存边界快照时的 dynamicBlock 冻结副本（v1.1.10）。
        /// 快照活跃时，BuildApiMessages 使用此冻结版本替代实时 BuildDynamicContextBlock()，
        /// 防止压缩摘要/搜索/RAG/记忆等动态内容变化导致前缀缓存断裂。
        /// null = 无快照或快照时 dynamicBlock 为空。
        /// </summary>
        private string? _cachedDynamicBlock;

        /// <summary>
        /// 保存缓存边界快照：记录当前 _entries 的数量作为缓存前缀边界，
        /// 同时冻结当前的 dynamicBlock 文本，防止动态上下文变化破坏前缀缓存。
        /// Agent Handoff 前调用，确保目标 Agent 可排除过渡消息、复用缓存前缀。
        /// </summary>
        public void SnapshotForCache()
        {
            _cacheSnapshotEntryIndex = _entries.Count;
            _cachedDynamicBlock = BuildDynamicContextBlock();
            Logger.Info($"[ContextManager]  缓存边界快照已保存: entryIndex={_cacheSnapshotEntryIndex}, dynamicBlock={(_cachedDynamicBlock?.Length ?? 0)}chars");
        }

        /// <summary>
        /// 消费并清空缓存边界快照。
        /// 多步骤 Agent 在执行后续步骤前必须回到完整上下文；
        /// 否则 assistant/tool 历史会被 Handoff 边界意外排除。
        /// </summary>
        public void ClearCacheSnapshot()
        {
            if (!_cacheSnapshotEntryIndex.HasValue && _cachedDynamicBlock == null)
                return;

            Logger.Info($"[ContextManager]  缓存边界快照已清除: was at entry {_cacheSnapshotEntryIndex}");
            _cacheSnapshotEntryIndex = null;
            _cachedDynamicBlock = null;
        }

        /// <summary>获取当前对话轮次数（一个真实用户轮次 = 一轮）</summary>
        /// <remarks>
        /// 按「轮次序号去重」计数：同一轮内可能有多条 user 条目
        /// （如 Handoff 任务提示、Edit 步骤提示，它们归属当前轮但不开启新轮），
        /// 只有真实用户提问才会产生新的 TurnIndex。
        /// </remarks>
        public int TurnCount => _entries
            .Where(e => e is not null && e.Role == "user" && e.TurnIndex > 0)
            .Select(e => e.TurnIndex)
            .Distinct()
            .Count();

        /// <summary>获取消息总条数</summary>
        public int MessageCount => _entries.Count;

        /// <summary>获取当前上下文中 assistant 消息携带的工具调用总次数</summary>
        public int ToolCallCount => _entries.Sum(e => e?.ToolCalls?.Count ?? 0);

        /// <summary>获取校准后的估算 Token 数（= 原始估算 × 校准系数）</summary>
        public int EstimatedTokens => (int)(_estimatedTokens * _calibrationFactor);

        /// <summary>获取原始字符级估算 Token 数（未校准），用于内部计算和校准对比</summary>
        public int RawEstimatedTokens => _estimatedTokens;

        /// <summary>上下文是否为空（无任何用户消息）</summary>
        public bool IsEmpty => !_entries.Any(e => e is not null && e.Role == "user");

        /// <summary>获取上下文使用率（0.0 ~ 1.0，基于校准后估算）</summary>
        public double UsageRatio => TokenBudget > 0 ? (double)EstimatedTokens / TokenBudget : 0;

        /// <summary>获取上下文使用百分比</summary>
        public double UsagePercent => UsageRatio * 100;

        /// <summary>获取压缩服务实例</summary>
        public ContextCompressorService? Compressor => _compressor;

        /// <summary>获取 RAG 上下文</summary>
        public string? RagContext => _ragContext;

        #region Core API — 添加消息

        /// <summary>
        /// 设置系统提示词。
        /// 如果已通过 FreezeSystemPrompt() 冻结，则忽略此次调用并记录警告，
        /// 防止意外覆盖不可变前缀导致 DeepSeek 缓存失效。
        /// </summary>
        public void SetSystemPrompt(string? prompt)
        {
            if (_fixedSystemPrompt != null)
            {
                Logger.Warn($"[ContextManager] SetSystemPrompt() 被调用但 system prompt 已冻结 — 忽略。" +
                    $"当前冻结长度={_fixedSystemPrompt.Length} 字符, 新 prompt 长度={prompt?.Length ?? 0} 字符。" +
                    $"如需更换 system prompt，请先调用 Clear() 然后重新 FreezeSystemPrompt()。");
                return;
            }
            _systemPrompt = prompt;
        }

        /// <summary>
        /// 设置 IDE 实时态上下文（P1-A）。
        /// 由 View 在每次用户消息发送前捕获快照并格式化后传入；传 null 表示禁用或清空。
        /// Token 记账与 RAG 上下文一致。
        /// </summary>
        public void SetIdeContext(string? ideContext)
        {
            if (!string.IsNullOrEmpty(_ideContext))
                _estimatedTokens -= EstimateTokens(_ideContext);

            _ideContext = ideContext;

            if (!string.IsNullOrEmpty(_ideContext))
                _estimatedTokens += EstimateTokens(_ideContext);
        }

        /// <summary>
        /// 设置当前解决方案上下文。解决方案路径是会话元数据，
        /// 与 IDE Context 一起进入 volatile 块，不包装进当前 user 轮次。
        /// </summary>
        public void SetSolutionPath(string? solutionPath)
        {
            string? formatted = string.IsNullOrWhiteSpace(solutionPath)
                ? null
                : LocalizationService.Instance.Format("system.contextSolutionLabel", solutionPath);

            if (!string.IsNullOrEmpty(_solutionContext))
                _estimatedTokens -= EstimateTokens(_solutionContext);

            _solutionContext = formatted;

            if (!string.IsNullOrEmpty(_solutionContext))
                _estimatedTokens += EstimateTokens(_solutionContext);
        }

        // ── Context Debugger 只读探针（P2，序号 20 数据面）──

        /// <summary>本轮是否注入了 RAG 检索上下文</summary>
        [JsonIgnore]
        public bool HasRagContext => !string.IsNullOrEmpty(_ragContext);

        /// <summary>IDE 实时态上下文字符数（0 = 未注入/已禁用）</summary>
        [JsonIgnore]
        public int IdeContextChars => _ideContext?.Length ?? 0;

        /// <summary>Working Set 最近活跃文件 Top-N（P2 Context Debugger 数据面）。</summary>
        public System.Collections.Generic.IReadOnlyList<string> GetWorkingSetTopPaths(int limit = 8)
        {
            try
            {
                var root = !string.IsNullOrWhiteSpace(_workspaceRoot)
                    ? _workspaceRoot!
                    : Environment.CurrentDirectory;
                return _activeFileTracker?.GetTopPaths(root, limit)
                       ?? (System.Collections.Generic.IReadOnlyList<string>)Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// 设置 Skill 发现上下文。
        /// </summary>
        public void SetSkillContext(string? skillContext)
        {
            _skillContext = skillContext;
        }

        /// <summary>
        /// 设置始终注入的技能上下文（每次对话均加载完整技能指令）。
        /// </summary>
        public void SetAlwaysInjectSkillsContext(string? alwaysInjectSkillsContext)
        {
            _alwaysInjectSkillsContext = alwaysInjectSkillsContext;
        }

        /// <summary>
        /// <summary>
        /// 设置记忆上下文（注入为 system 消息）。
        /// 会话初始化时由 ChatControl 调用，包含用户记忆。
        /// </summary>
        public void SetMemoryContext(string? memoryContext)
        {
            _memoryContext = memoryContext;
        }

        /// <summary>
        /// 设置当前工作区根目录（用于 Working Set 摘要的相对路径生成）。
        /// </summary>
        public void SetWorkspaceRoot(string? workspaceRoot)
        {
            _workspaceRoot = workspaceRoot;
        }

        /// <summary>
        /// 设置 RAG 检索上下文（注入为 system 消息）。
        /// 由 RagService 在每次用户消息前调用。
        /// </summary>
        public void SetRagContext(string? ragContext)
        {
            // 移除旧的 RAG 上下文 token 计数
            if (!string.IsNullOrEmpty(_ragContext))
                _estimatedTokens -= EstimateTokens(_ragContext);

            _ragContext = ragContext;

            // 添加新的 RAG 上下文 token 计数
            if (!string.IsNullOrEmpty(_ragContext))
                _estimatedTokens += EstimateTokens(_ragContext);
        }

        /// <summary>
        /// 注入上下文压缩服务。
        /// 设置后，当 Token 超出预算时将自动调用压缩而非直接删除旧消息。
        /// </summary>
        public void SetCompressor(ContextCompressorService? compressor)
        {
            if (!ReferenceEquals(_compressor, compressor))
            {
                // 设置热重载会创建新的压缩器。必须先迁移旧摘要，
                // 否则换语言/换模型等无关设置变更也会导致对话历史摘要消失。
                var summaries = _compressor is { CompressedSummaries.Count: > 0 }
                    ? _compressor.CompressedSummaries.Select(CloneCompressedSummary).ToList()
                    : _pendingCompressedSummaries.Select(CloneCompressedSummary).ToList();

                _pendingCompressedSummaries.Clear();
                _pendingCompressedSummaries.AddRange(summaries);
                _compressor = compressor;
            }

            if (_compressor != null)
                _compressor.ReplaceSummaries(_pendingCompressedSummaries);
        }

        /// <summary>
        /// 获取当前压缩摘要的持久化快照。
        /// </summary>
        public List<CompressedTurnSummary> GetCompressedSummariesSnapshot()
        {
            var source = _compressor?.CompressedSummaries ?? _pendingCompressedSummaries;
            return source.Select(CloneCompressedSummary).ToList();
        }

        /// <summary>
        /// 从会话持久化恢复压缩摘要，并在 compressor 已初始化时立即注入。
        /// </summary>
        public void RestoreCompressedSummaries(IEnumerable<CompressedTurnSummary>? summaries)
        {
            _pendingCompressedSummaries.Clear();
            if (summaries != null)
                _pendingCompressedSummaries.AddRange(summaries.Select(CloneCompressedSummary));

            _compressor?.ReplaceSummaries(_pendingCompressedSummaries);
            _cachedDynamicBlock = null;
        }

        private static CompressedTurnSummary CloneCompressedSummary(CompressedTurnSummary summary)
        {
            return new CompressedTurnSummary
            {
                Summary = summary.Summary,
                FromTurn = summary.FromTurn,
                ToTurn = summary.ToTurn,
                OriginalTokens = summary.OriginalTokens,
                CompressedTokens = summary.CompressedTokens,
                CompressedAt = summary.CompressedAt,
            };
        }

        private ContextEntry CreateEntry(
            string Role,
            string? Content = null,
            List<ChatContentPart>? MultimodalContent = null,
            string? ReasoningContent = null,
            List<ToolCall>? ToolCalls = null,
            bool HasToolCalls = false,
            string? ToolCallId = null,
            string? Name = null,
            int TurnIndex = -1,
            bool IsVolatileSnapshot = false,
            bool Replay = true)
        {
            return new ContextEntry
            {
                EntryId = ++_nextEntryId,
                Role = Role,
                Content = Content,
                MultimodalContent = MultimodalContent,
                ReasoningContent = ReasoningContent,
                ToolCalls = ToolCalls,
                HasToolCalls = HasToolCalls,
                ToolCallId = ToolCallId,
                Name = Name,
                TurnIndex = TurnIndex,
                IsVolatileSnapshot = IsVolatileSnapshot,
                Replay = Replay,
            };
        }

        /// <summary>
        /// 判断待压缩边界内是否还有从未压缩过的对话条目。
        /// </summary>
        private bool HasNewCompressibleEntries(int compressionStart)
        {
            if (compressionStart <= 0)
                return false;

            for (int i = 0; i < compressionStart && i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.TurnIndex > 0 && entry.EntryId > _lastCompressedEntryId)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 判断不可再压缩的固定 system[0] 与已生成压缩摘要是否已经占满 Token 预算。
        /// 只有这部分本身达到上限时，才真正需要用户切换新对话。
        /// </summary>
        private bool IsSystemAndCompressedContextAtBudget()
        {
            if (_compressor == null || TokenBudget <= 0)
                return false;

            string? systemPrompt = _fixedSystemPrompt ?? BuildFinalSystemPrompt();
            int systemTokens = EstimateTokens(systemPrompt);
            int compressedTokens = EstimateTokens(_compressor.GetCompressedContextText());
            int irreducibleTokens = (int)((systemTokens + compressedTokens) * _calibrationFactor);
            return irreducibleTokens >= TokenBudget;
        }

        /// <summary>
        /// 标记需要在当前 Agent 工作流结束后提示用户切换新对话。
        /// </summary>
        private void MarkConversationResetNeeded(string reason)
        {
            if (_conversationResetNoticePending)
                return;

            _conversationResetNoticePending = true;
            Logger.Warn($"[ContextManager] 没有新的可压缩对话内容，已停止压缩并要求切换新对话: {reason}");
        }

        /// <summary>
        /// 消费“需要切换新对话”通知。返回 true 表示本次完整对话结束后应提示用户。
        /// </summary>
        public bool ConsumeConversationResetNotice()
        {
            bool pending = _conversationResetNoticePending;
            _conversationResetNoticePending = false;
            return pending;
        }

        /// <summary>
        /// 清除待处理的“切换新对话”通知，不修改对话历史。
        /// </summary>
        public void ClearConversationResetNotice()
        {
            _conversationResetNoticePending = false;
        }

        /// <summary>
        /// 注入前缀缓存管理器。
        /// 设置后，BuildApiMessages 时会自动检查前缀稳定性并记录漂移事件。
        /// <summary>
        /// 注入活跃文件追踪器。
        /// 设置后，BuildVolatileContextBlock 将注入 Working Set 摘要。
        /// </summary>
        public void SetActiveFileTracker(IActiveFileTracker? tracker)
        {
            _activeFileTracker = tracker;
        }

        /// <summary>
        /// 冻结系统提示词为不可变前缀。
        /// 应在会话初始化时调用一次。冻结后整个会话期间不应再调用 SetSystemPrompt()。
        /// 
        /// 这是前缀缓存优化的核心：messages[0] 的内容在冻结后永远不会改变，
        /// 确保 DeepSeek 的自动前缀缓存在每次请求时都能命中。
        /// 
        /// 冻结内容 = systemPrompt + skillContext（若存在则以换行连接）
        /// </summary>
        public void FreezeSystemPrompt()
        {
            _fixedSystemPrompt = BuildFinalSystemPrompt();
            Logger.Info($"[ContextManager] System prompt 已冻结为不可变前缀 ({_fixedSystemPrompt?.Length ?? 0} 字符)");
        }

        /// <summary>
        /// 获取已冻结的系统提示词（null = 尚未冻结）。
        /// </summary>
        public string? GetFixedSystemPrompt() => _fixedSystemPrompt;

        /// <summary>
        /// 给用户输入加上「本轮用户需求」前缀（幂等）。
        /// 前缀文案取自本地化键，未配置时原样返回。
        /// </summary>
        private static string ApplyUserRequirementPrefix(string content)
        {
            string prefix = LocalizationService.Instance["system.agent.currentUserQuestionPrefix"];
            if (string.IsNullOrWhiteSpace(prefix))
                return content;

            return content.StartsWith(prefix, StringComparison.Ordinal)
                ? content
                : prefix + content;
        }

        /// <summary>
        /// 添加用户消息。
        /// 当 Token 超出预算时触发压缩而非直接删除。
        /// </summary>
        public void AddUserMessage(string content)
            => AddUserMessage(content, null);

        /// <summary>
        /// 添加用户消息。视觉模型轮次中可以携带 <paramref name="multimodalContent"/>，
        /// 即使文本为空也仍应作为有效 user 轮次保存，避免重启后 ContextManager 被判定为空。
        /// </summary>
        public void AddUserMessage(string content, List<ChatContentPart>? multimodalContent)
        {
            bool hasText = !string.IsNullOrEmpty(content);
            bool hasMultimodal = multimodalContent is { Count: > 0 };
            if (!hasText && !hasMultimodal) return;

            // ── 安全净化：防止工具注入标记进入上下文 ──
            if (hasText)
                content = StringExtensions.SanitizeUserInput(content);

            // ── 落库时写入「本轮用户需求」前缀 ──
            //   前缀此前只在发出请求时临时加到消息副本上，_entries 里始终是裸文本。
            //   一旦同一轮内发生 Agent 移交（Handoff 走独立分支、不经过前缀逻辑），
            //   目标 Agent 就会看到一条没有任何「这是用户需求」标记的历史 user，
            //   与上一请求不一致，模型容易把中间内容或旧需求误当成本轮目标。
            //   改为落库时写入：前缀成为持久化事实，任何后续请求（含移交）自动继承，
            //   无需在每条路径上重复补写。幂等保护避免重复保存时二次叠加。
            if (hasText)
                content = ApplyUserRequirementPrefix(content);

            var normalizedMultimodalContent = CloneContentParts(multimodalContent);
            if (hasText
                && normalizedMultimodalContent is { Count: > 0 }
                && !normalizedMultimodalContent.Any(p =>
                    p.Type == "text" && string.Equals(p.Text, content, StringComparison.Ordinal)))
            {
                // 多模态消息序列化时 content 数组优先于纯文本字段。
                // 调用方通常只传图片块，因此必须把文本显式放回数组，避免文字丢失。
                normalizedMultimodalContent.Insert(0, new ChatContentPart
                {
                    Type = "text",
                    Text = content,
                });
            }

            // ──  缓存边界快照：新用户消息意味着新对话轮次，清除旧快照 ──
            if (_cacheSnapshotEntryIndex.HasValue)
            {
                Logger.Info($"[ContextManager]  新用户消息，清除缓存边界快照 (was at entry {_cacheSnapshotEntryIndex})");
                _cacheSnapshotEntryIndex = null;
            }

            _entries.Add(CreateEntry(
                Role: "user",
                Content: content,
                MultimodalContent: normalizedMultimodalContent,
                TurnIndex: TurnCount + 1)); // 新轮次

            _estimatedTokens += EstimateTokens(content);
            _estimatedTokens += EstimateMultimodalTokens(multimodalContent);

            // ──  v1.1.11：冻结动态上下文块，确保同轮次内后续API调用
            //     messages[2] 内容不变 → DeepSeek前缀缓存可持续命中。──
            _cachedDynamicBlock = BuildDynamicContextBlock();
        }

        /// <summary>
        /// 添加助手消息。
        /// </summary>
        /// <param name="content">回复内容</param>
        /// <param name="reasoningContent">思维链内容（思考模式下）</param>
        /// <param name="toolCalls">工具调用列表（可为 null）</param>
        public void AddAssistantMessage(string? content, string? reasoningContent = null, List<ToolCall>? toolCalls = null)
        {
            // 不裁剪：官方要求思考模式下后续请求继续回传历史 reasoning_content。
            // 落库/落内存时截断会违反该要求，仅做超限告警，便于观察实际体量。
            ReasoningTextPolicy.WarnIfOversized(reasoningContent);

            // ──  前缀缓存优化：写时合并连续 assistant 消息 ──
            //     如果上一条也是 assistant，合并内容而非新增条目，
            //     避免 BuildApiMessages 产生连续 assistant 消息，进而触发 ChatStreamAsync
            //     的合并逻辑修改消息内容 → 破坏 DeepSeek Prefix Cache 前缀。
            //
            //     覆盖两种场景：
            //     A. 两条纯文本 assistant（均无 tool_calls）→ 合并文本
            //     B. 前条有 tool_calls + 本条纯文本 → 合并文本到前条（保留 tool_calls）
            //        这是 Agent 工具调用循环中的常见模式：AI 先发起工具调用，再输出文本响应
            if (_entries.Count > 0)
            {
                var last = _entries[_entries.Count - 1];
                if (last.Role == "assistant")
                {
                    bool lastHasTc = last.ToolCalls != null && last.ToolCalls.Count > 0;
                    bool currHasTc = toolCalls != null && toolCalls.Count > 0;

                    // 场景 A：两条都是纯文本 assistant → 合并
                    // 场景 B：前条有 tool_calls，本条是纯文本 → 合并文本到前条（保留 tool_calls 结构）
                    if (!currHasTc)
                    {
                        // ── 幂等保护：完全相同的正文不重复追加 ──
                        //   工具循环终止时已把最终答复写入 _entries，UI 收尾
                        //   （SyncAgentResponseToTreeAndContextAsync）会再写一次同样的正文。
                        //   若不拦截，同一段回答会被拼接成 "正文\n\n---\n\n正文"，
                        //   既浪费 token 又让下一轮看到重复内容。
                        if (!string.IsNullOrWhiteSpace(content)
                            && string.Equals(last.Content, content, StringComparison.Ordinal))
                        {
                            if (!string.IsNullOrWhiteSpace(reasoningContent))
                                last.ReasoningContent = reasoningContent;
                            return;
                        }

                        // 合并文本内容到前条 assistant
                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            last.Content = string.IsNullOrWhiteSpace(last.Content)
                                ? content
                                : last.Content + "\n\n---\n\n" + content;
                        }
                        // 如果后者有 reasoning_content，保留后者
                        if (!string.IsNullOrWhiteSpace(reasoningContent))
                            last.ReasoningContent = reasoningContent;
                        _estimatedTokens += EstimateTokens(content);
                        if (!string.IsNullOrEmpty(reasoningContent))
                            _estimatedTokens += EstimateTokens(reasoningContent);

                        // ── 诊断：记录非标准合并场景 ──
                        if (lastHasTc)
                        {
                            Logger.Info($"[ContextManager] 写时合并 assistant: 前条有 tool_calls({last.ToolCalls!.Count}个), " +
                                $"本条纯文本({content?.Length ?? 0}chars) → 合并到前条");
                        }
                        return;
                    }
                    // 场景 C：前条纯文本 + 本条有 tool_calls → 不应合并（语义上 tool_calls 不应在文本之后）
                    // 保持独立条目，由 BuildApiMessages 自然处理
                }
            }

            _entries.Add(CreateEntry(
                Role: "assistant",
                Content: content,
                ReasoningContent: reasoningContent,
                ToolCalls: toolCalls,
                HasToolCalls: toolCalls != null && toolCalls.Count > 0,
                TurnIndex: TurnCount)); // 属于当前轮次
            _estimatedTokens += EstimateTokens(content);
            if (!string.IsNullOrEmpty(reasoningContent))
                _estimatedTokens += EstimateTokens(reasoningContent);
        }

        /// <summary>
        /// 添加纯文本工具调用结果（tool 角色消息）。
        /// </summary>
        public void AddToolResult(string toolCallId, string toolName, string result)
            => AddToolResult(toolCallId, toolName, result, null);

        /// <summary>
        /// 添加工具调用结果；视觉工具返回的图片必须与文本一起写入历史，
        /// 否则下一次从 ContextManager 重建请求时 content 数组会退化成字符串，
        /// 既丢失图片，也会破坏后续请求的稳定前缀缓存。
        /// </summary>
        public void AddToolResult(
            string toolCallId,
            string toolName,
            string result,
            List<ChatContentPart>? multimodalContent)
        {
            var normalizedMultimodalContent = CloneContentParts(multimodalContent);
            if (!string.IsNullOrEmpty(result)
                && normalizedMultimodalContent is { Count: > 0 }
                && !normalizedMultimodalContent.Any(p =>
                    p.Type == "text" && string.Equals(p.Text, result, StringComparison.Ordinal)))
            {
                // 调用方只传图片块时补回文本，保证工具结果在 UI、日志与 API 三处语义一致。
                normalizedMultimodalContent.Insert(0, new ChatContentPart
                {
                    Type = "text",
                    Text = result,
                });
            }

            _entries.Add(CreateEntry(
                Role: "tool",
                Content: result,
                MultimodalContent: normalizedMultimodalContent,
                ToolCallId: toolCallId,
                Name: toolName,
                TurnIndex: TurnCount)); // 工具调用属于当前轮次
            _estimatedTokens += EstimateTokens(result);
            _estimatedTokens += EstimateMultimodalTokens(normalizedMultimodalContent);
        }

        /// <summary>
        /// 添加自定义角色消息（如 skill 指令注入的 system 消息）。
        /// 这些消息不计入 turn 管理。
        /// </summary>
        /// <param name="role">消息角色。</param>
        /// <param name="content">消息内容。</param>
        /// <param name="turnIndex">
        /// 归属轮次；默认 -1（不属于任何轮次）。
        /// 传入具体轮次时该条目会随该轮一起参与压缩/裁剪，但仍不计入 <see cref="TurnCount"/>。
        /// </param>
        public void AddCustomMessage(string role, string content, int turnIndex = -1)
        {
            if (string.IsNullOrEmpty(content)) return;

            _entries.Add(CreateEntry(
                Role: role,
                Content: content,
                TurnIndex: turnIndex));
            _estimatedTokens += EstimateTokens(content);
        }

        /// <summary>
        /// 当前轮次序号（最后一条真实 user 的 TurnIndex）；无真实轮次时返回 -1。
        /// </summary>
        private int CurrentTurnIndex
        {
            get
            {
                for (int i = _entries.Count - 1; i >= 0; i--)
                {
                    if (_entries[i].Role == "user" && _entries[i].TurnIndex > 0)
                        return _entries[i].TurnIndex;
                }
                return -1;
            }
        }

        #endregion

        #region Core API — 构建 API 消息

        /// <summary>
        /// 构建发送给 DeepSeek API 的完整消息列表。
        /// 委托给 BuildApiMessagesCore(0)。
        /// </summary>
        public List<ChatApiMessage> BuildApiMessages()
        {
            return BuildApiMessagesCore(0);
        }

        /// <summary>
        /// 构建「完整 API 消息历史 + 子任务尾部」的消息数组，供记忆总结等 UI 侧子任务复用主对话完整上下文。
        /// 结构：[0] 稳定前缀 + [1] 动态块（记忆注入）+ [2..] 完整历史 + [user 子任务提示] + [system 子任务指令]。
        /// 末尾原为 system 时以新对象整体替换，否则直接追加，保证尾部签名恒为 [user][system]。
        /// </summary>
        /// <param name="userPrompt">追加到末尾的 user 提示（子任务指令正文）。</param>
        /// <param name="systemPrompt">子任务 system 指令；替换或追加到消息数组最末。</param>
        /// <returns>新构建的消息列表；不修改内部历史条目对象，也不影响后续主对话请求。</returns>
        /// <remarks>
        /// <b>子任务尾提示一律不落库</b>：记忆判定、总结润色等属于主流程之外的一次性脚手架，
        /// 其提示词与返回结果都不应进入 <c>_entries</c>，否则会污染下一轮主对话历史
        /// （模型会把「记忆管理助手」的问答当成本轮对话的一部分）。
        /// 因此本方法只构建请求，绝不写历史。
        /// </remarks>
        public List<ChatApiMessage> BuildApiMessagesWithSubTaskPrompt(string userPrompt, string systemPrompt)
        {
            // ── 复用主对话完整快照（稳定前缀 + 动态块 + 完整历史，含工具调用记录）──
            var messages = BuildApiMessages();

            // ── 尾部恒为 [user][system]，与主对话「user 在前、system 固定在最后」的签名保持一致 ──
            if (messages.Count > 0 && messages[messages.Count - 1].Role == "system")
            {
                // 末尾已是 system（如主对话的 Agent 行为指令）：在其位置前插入 user 提示，
                // 并以新对象替换原末尾 system——不修改原对象、不污染内部状态。
                int last = messages.Count - 1;
                messages.Insert(last, new ChatApiMessage { Role = "user", Content = userPrompt });
                messages[last + 1] = new ChatApiMessage { Role = "system", Content = systemPrompt };
            }
            else
            {
                // 末尾非 system（含空列表）：直接追加 user + system 两条新消息。
                messages.Add(new ChatApiMessage { Role = "user", Content = userPrompt });
                messages.Add(new ChatApiMessage { Role = "system", Content = systemPrompt });
            }

            return messages;
        }

        /// <summary>
        /// 将「本次实际发出的消息列表」与内部历史对账，把**主流程**脱节消息补写进 <c>_entries</c>。
        ///
        /// ── 不变量 ──
        /// <c>_entries</c> 必须完整包含主流程发出过的每一条消息（任何一条都不能缺），
        /// 且必须**完全不含**一次性子任务（记忆判定、总结润色、路由）的提示词与返回结果。
        ///
        /// ── 为什么需要它 ──
        /// 正常请求由 <see cref="BuildApiMessages"/> 从 <c>_entries</c> 重建，但有几类主流程内容
        /// 只在请求副本上临时存在、从不落库：
        ///   1. Handoff 分支插入的 volatile 上下文块（当前解决方案 / Git / IDE 快照）；
        ///   2. Handoff 目标 Agent 的任务提示（「[当前任务目标] 你是一个 Edit Agent…」）；
        ///   3. 显式 @Agent 路由的 doNotHandoff 边界指令；
        ///   4. Agent 专属行为指令（末尾 system）。
        /// 这些一旦不入库，从 <c>_entries</c> 重建历史的链路（子任务、会话持久化、重启恢复）
        /// 就会看到一份「缺了几条」的历史：既丢上下文，也让前缀缓存从缺失点断裂。
        ///
        /// ── 对账算法 ──
        /// 以「角色 + 内容（assistant 另比 tool_calls id 集合、tool 另比 tool_call_id）」做
        /// 贪心顺序匹配：为每条已发出消息找到匹配条目即前移；找不到匹配的即为脱节消息，补写为新条目。
        /// 结构性前缀（稳定 system / 动态块）不参与条目匹配，天然被跳过。
        ///
        /// 幂等：同一条消息重复对账不会重复写入（第二次会匹配到已补写的条目）。
        /// </summary>
        /// <param name="sentMessages">实际发送给 API 的消息列表（清洗后的最终版本）。</param>
        /// <returns>补写的条目数。</returns>
        public int SyncSentMessagesToHistory(IReadOnlyList<ChatApiMessage>? sentMessages)
        {
            if (sentMessages == null || sentMessages.Count == 0)
                return 0;

            // ── 正在压缩时不对账：压缩会重排 _entries，此时写入会与压缩边界冲突 ──
            if (_isCompressing)
                return 0;

            // ── 匹配范围必须覆盖全部条目 ──
            //     不能用 _cacheSnapshotEntryIndex 截断：缓存边界快照只约束"本次渲染哪些条目"，
            //     而 Handoff 分支恰好会在快照之后把身份边界提示等写入 _entries。
            //     若用快照下标当上界，刚写下的那条就落在窗口外、匹配不到，
            //     于是被判为"脱节内容"再补一份 —— 这正是 152 号请求 [48][49] 相邻重复的成因。
            int entryLimit = _entries.Count;
            int appended = 0;
            int multimodalUpgraded = 0;
            int cursor = 0;      // _entries 扫描游标：已匹配到的条目下标

            // ── 跳过结构性前缀（[0] 稳定系统提示词、[1] 动态上下文块）──
            //     这两条由 BuildApiMessagesSnapshot 每次从字段重新生成，不是历史条目；
            //     若把它们当脱节消息补写，下一轮就会在历史里重放一遍，与前缀重复。
            string? stablePrompt = BuildStableSystemPrompt();
            string? dynamicBlockForMatch = _cachedDynamicBlock ?? BuildDynamicContextBlock();
            int structuralSkip = 0;
            foreach (var msg in sentMessages)
            {
                if (msg?.Role != "system")
                    break;
                bool isStable = !string.IsNullOrEmpty(stablePrompt)
                    && string.Equals(msg.Content, stablePrompt, StringComparison.Ordinal);
                bool isDynamic = !string.IsNullOrEmpty(dynamicBlockForMatch)
                    && string.Equals(msg.Content, dynamicBlockForMatch, StringComparison.Ordinal);
                if (!isStable && !isDynamic)
                    break;
                structuralSkip++;
            }

            // ── 兜底：前缀已漂移时，跳过「起始处未匹配的 system」──
            //     动态块内容在本轮内可能被重建（记忆刷新、语言守卫、压缩摘要、新用户消息落库），
            //     于是发送列表里的那份与当前字段不再逐字相等。若把它当成脱节内容补写，
            //     游标会跳到 _entries 末尾，导致其后所有历史消息都匹配不到而被整段复制
            //     （线上 26/27：Ask 阶段 [3..15] 与 [21..33] 逐字节重复）。
            //     system 前缀永远由字段重建，绝不作为历史补写。
            while (structuralSkip < sentMessages.Count
                && string.Equals(sentMessages[structuralSkip]?.Role, "system", StringComparison.Ordinal))
            {
                structuralSkip++;
            }

            // 当前轮次：补写条目归属最近一条真实 user 的轮次，避免凭空造轮
            int currentTurn = CurrentTurnIndex;

            // 最后一条 user 的位置：用于区分「历史内容」与「请求尾部提示」。
            // 尾部 system（Agent 行为指令、路由边界）在每次请求都会重新生成，
            // 若参与重放会随请求数无限累积，故只登记不重放。
            int lastUserSentIndex = -1;
            for (int i = sentMessages.Count - 1; i >= 0; i--)
            {
                if (string.Equals(sentMessages[i]?.Role, "user", StringComparison.Ordinal))
                {
                    lastUserSentIndex = i;
                    break;
                }
            }

            for (int i = structuralSkip; i < sentMessages.Count; i++)
            {
                var msg = sentMessages[i];
                if (msg == null || string.IsNullOrEmpty(msg.Role))
                    continue;

                // ── 空壳消息（无内容、无工具调用）不落库，与 BuildApiMessagesSnapshot 的跳过规则一致 ──
                bool hasBody = !string.IsNullOrEmpty(msg.Content)
                    || (msg.MultimodalContent is { Count: > 0 })
                    || (msg.ToolCalls is { Count: > 0 })
                    || (msg.Role == "tool" && !string.IsNullOrEmpty(msg.ToolCallId));
                if (!hasBody)
                    continue;

                // ── 在条目中寻找匹配（限界内、顺序前移）──
                int matched = FindMatchingEntryIndex(msg, cursor, entryLimit);
                if (matched >= 0)
                {
                    // 兼容旧历史：此前工具结果只落文本，实际请求却携带了图片块。
                    // 同一 tool_call 已被匹配时不要追加重复消息，而是用发送快照补齐缺失的多模态内容。
                    var matchedEntry = _entries[matched];
                    if (string.Equals(matchedEntry.Role, "tool", StringComparison.Ordinal)
                        && msg.MultimodalContent is { Count: > 0 }
                        && matchedEntry.MultimodalContent is not { Count: > 0 })
                    {
                        matchedEntry.MultimodalContent = CloneContentParts(msg.MultimodalContent);
                        multimodalUpgraded++;
                    }

                    cursor = matched + 1;
                    continue;
                }

                // ── 未匹配 → 主流程脱节内容，补写（不变量：主流程消息一条都不能缺）──
                //     · user（Handoff 任务提示、Edit 步骤提示）归属当前轮次，不新开轮次；
                //     · volatile 快照（当前解决方案 + Git/IDE）参与重放，并标记 IsVolatileSnapshot，
                //       使 PersistCurrentVolatileSnapshot 的同轮幂等判断能识别它、不再重复固化；
                //     · 尾部 system（Agent 行为指令、路由边界）只登记不重放：
                //       它们每次请求都会在尾部重新生成，重放会随请求数无限累积。
                bool isTailSystem = string.Equals(msg.Role, "system", StringComparison.Ordinal)
                    && i > lastUserSentIndex;
                bool isVolatile = IsVolatileSnapshotContent(msg.Content);

                _entries.Add(CreateEntry(
                    Role: msg.Role,
                    Content: msg.Content,
                    MultimodalContent: CloneContentParts(msg.MultimodalContent),
                    ReasoningContent: msg.ReasoningContent,
                    ToolCalls: CloneToolCalls(msg.ToolCalls),
                    HasToolCalls: msg.ToolCalls is { Count: > 0 },
                    ToolCallId: msg.ToolCallId,
                    Name: msg.Name,
                    TurnIndex: currentTurn,
                    IsVolatileSnapshot: isVolatile,
                    Replay: !isTailSystem));

                cursor = _entries.Count;
                appended++;
            }

            if (appended > 0 || multimodalUpgraded > 0)
            {
                _estimatedTokens = RecalculateEstimatedTokens();

                if (appended > 0)
                {
                    Logger.Info($"[ContextManager] 主流程对账：补写 {appended} 条脱节消息进 _entries（当前 {MessageCount} 条 / {TurnCount} 轮）");
                }

                if (multimodalUpgraded > 0)
                {
                    Logger.Info($"[ContextManager] 主流程对账：为 {multimodalUpgraded} 条工具结果补齐多模态内容");
                }
            }

            return appended;
        }

        /// <summary>
        /// 判断给定文本是否为「volatile 上下文快照」（当前解决方案 + IDE/Git 等易变块）。
        /// 这类内容每轮由 <see cref="PersistCurrentVolatileSnapshot"/> 重新固化，
        /// 若把已固化的那份再参与重放，同一块会在历史里叠成两份。
        /// </summary>
        private static bool IsVolatileSnapshotContent(string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return false;

            // 易变块以「[当前解决方案: 」或「[Git Context]」开头（见 BuildVolatileContextBlock 的拼装顺序）
            string solutionLabel = LocalizationService.Instance["system.contextSolutionLabel"];
            string prefix = string.IsNullOrWhiteSpace(solutionLabel)
                ? "[当前解决方案"
                : solutionLabel.Split('{')[0];

            return (!string.IsNullOrEmpty(prefix) && content.StartsWith(prefix, StringComparison.Ordinal))
                || content.StartsWith("[Git Context]", StringComparison.Ordinal);
        }

        /// <summary>
        /// 在 <c>_entries</c> 的 <paramref name="fromIndex"/>~<paramref name="entryLimit"/> 区间内
        /// 顺序查找与 <paramref name="msg"/> 内容一致的条目，返回下标；未找到返回 -1。
        ///
        /// 匹配规则：角色一致 + 内容一致；assistant 还需 tool_calls 的 id 集合一致；
        /// tool 还需 tool_call_id 一致。允许跳过区间内更早的条目，
        /// 使乱序/并发写入的历史也能正确对齐。
        /// </summary>
        private int FindMatchingEntryIndex(ChatApiMessage msg, int fromIndex, int entryLimit)
        {
            int limit = Math.Min(entryLimit, _entries.Count);
            for (int i = Math.Max(0, fromIndex); i < limit; i++)
            {
                var entry = _entries[i];
                if (entry.Role != msg.Role)
                    continue;

                if (entry.Role == "tool")
                {
                    if (!string.Equals(entry.ToolCallId, msg.ToolCallId, StringComparison.Ordinal))
                        continue;
                }
                else if (entry.Role == "assistant" && msg.ToolCalls is { Count: > 0 })
                {
                    var entryIds = entry.ToolCalls?.Select(tc => tc.Id).Where(id => !string.IsNullOrEmpty(id))
                        .OrderBy(id => id, StringComparer.Ordinal).ToList()
                        ?? new List<string?>();
                    var msgIds = msg.ToolCalls.Select(tc => tc.Id).Where(id => !string.IsNullOrEmpty(id))
                        .OrderBy(id => id, StringComparer.Ordinal).ToList();
                    if (!entryIds.SequenceEqual(msgIds, StringComparer.Ordinal))
                        continue;
                }

                if (!string.Equals(entry.Content ?? string.Empty, msg.Content ?? string.Empty, StringComparison.Ordinal))
                    continue;

                return i;
            }

            return -1;
        }

        /// <summary>
        /// 重新累计 <c>_entries</c> 的估算 Token（含思维链与多模态开销）。
        /// 仅在对账补写等低频路径调用。
        /// </summary>
        /// <remarks>
        /// 排除 <c>Replay == false</c> 的条目：它们不进入请求序列，
        /// 因此不应计入上下文预算（否则会虚高使用率、提前触发压缩）。
        /// </remarks>
        private int RecalculateEstimatedTokens()
        {
            int total = 0;
            foreach (var entry in _entries)
            {
                if (!entry.Replay)
                    continue;

                total += EstimateTokens(entry.Content);
                total += EstimateMultimodalTokens(entry.MultimodalContent);
                if (!string.IsNullOrEmpty(entry.ReasoningContent))
                    total += EstimateTokens(entry.ReasoningContent);
            }
            return total;
        }

        /// <summary>
        /// 克隆工具调用列表，避免调用方（Provider 侧快照）与内部历史共享可变对象。
        /// </summary>
        private static List<ToolCall>? CloneToolCalls(List<ToolCall>? toolCalls)
            => ChatMessageCloner.CloneToolCalls(toolCalls);

        /// <summary>
        /// 判断当前轮次（最近一条真实 user 所在轮）是否已存在内容与 <paramref name="volatileBlock"/>
        /// 完全相同的 volatile 快照条目（含固化快照与对账补写）。
        ///
        /// 供请求构建器在「临时注入易变块」前做去重：同一轮内的多次请求
        /// （如 Edit 多步骤推进）不应把同一份易变块重复叠加，否则既浪费 token，
        /// 也让从 _entries 重建的链路与线上请求在这一点上前缀分叉。
        /// </summary>
        /// <param name="volatileBlock">待注入的易变块文本。</param>
        public bool HasVolatileSnapshotInCurrentTurn(string? volatileBlock)
        {
            if (string.IsNullOrWhiteSpace(volatileBlock))
                return false;

            int currentTurn = CurrentTurnIndex;
            if (currentTurn < 0)
                return false;

            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (entry.TurnIndex != currentTurn)
                    continue;
                if (string.Equals(entry.Content, volatileBlock, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 判断一条 user 消息是否代表「真实用户轮次」。
        ///
        /// 规则：带「当前任务目标」前缀的（Handoff 任务提示、Edit 步骤提示）属于当前轮的执行内容，
        /// 不是新轮次；其余一律视为真实轮次 —— 包括纯图片/附件的 multimodal 轮次（文本为空）。
        /// </summary>
        private static bool IsRealUserTurn(ChatApiMessage msg)
        {
            string? content = msg.Content;

            // 纯图片/附件轮次：文本为空但带 content 块，仍是真实用户轮次
            if (string.IsNullOrWhiteSpace(content))
                return msg.MultimodalContent is { Count: > 0 };

            string handoffPrefix = LocalizationService.Instance["system.agent.handoffTaskGoalPrefix"];
            if (!string.IsNullOrWhiteSpace(handoffPrefix)
                && content.StartsWith(handoffPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// 构建仅包含稳定缓存前缀的 API 消息列表（固定 system prompt + 动态上下文块）。
        /// 不包含任何对话历史，供需要隔离历史的 Agent 阶段复用同一前缀，避免缓存失效。
        /// </summary>
        public List<ChatApiMessage> BuildContextPrefix()
        {
            string? dynamicBlock = _cachedDynamicBlock ?? BuildDynamicContextBlock();
            return BuildApiMessagesSnapshot(
                startEntryIdx: 0,
                dynamicBlock,
                entryLimitOverride: 0);
        }

        /// <summary>
        /// 统一的消息列表构建核心。BuildApiMessages() 和 BuildApiMessagesRecentTurns()
        /// 共享相同的前缀构建、缓存窗口裁剪和条目转换逻辑，仅起始索引不同。
        /// </summary>
        /// <param name="startEntryIdx">_entries 中开始包含的起始索引</param>
        private List<ChatApiMessage> BuildApiMessagesCore(int startEntryIdx)
        {
            string? dynamicBlock = _cachedDynamicBlock ?? BuildDynamicContextBlock();

            // ── 压缩必须在构建正常请求前完成 ──
            //     压缩请求从对话开头截取到待压缩区间末尾，最后只追加压缩指令，
            //     保证待压缩内容既完整出现，又不会破坏这段缓存前缀。
            if (!_cacheSnapshotEntryIndex.HasValue)
            {
                int compressionStart = ResolveCompressionStartIndex(
                    startEntryIdx,
                    out string compressionReason,
                    out int targetSummaryTokens);

                if (compressionStart > startEntryIdx)
                {
                    if (!HasNewCompressibleEntries(compressionStart))
                    {
                        if (IsSystemAndCompressedContextAtBudget())
                            MarkConversationResetNeeded(compressionReason);
                    }
                    else
                    {
                        string? compressionDynamicBlock = BuildCompressionPrefixDynamicBlock();
                        // 压缩请求只发送到“待压缩区间末尾”为止的完整对话前缀，
                        // 不包含之后要继续保留的历史，也不重复追加待压缩内容。
                        var compressionPrefix = BuildApiMessagesSnapshot(
                            startEntryIdx: 0,
                            compressionDynamicBlock,
                            entryLimitOverride: compressionStart);
                        Logger.Info($"[CacheWindow] 触发压缩: {compressionReason}, 压缩前保留 {TurnCount} 轮");
                        CompressEntriesBeforeWindow(
                            compressionStart,
                            compressionPrefix,
                            targetSummaryTokens);

                        // 压缩摘要已生成，立即刷新动态块，确保本轮正常请求就能注入结果。
                        dynamicBlock = _cachedDynamicBlock ?? BuildDynamicContextBlock();
                        startEntryIdx = 0;
                    }
                }
                else if (startEntryIdx > 0)
                {
                    Logger.Info($"[CacheWindow] 触发压缩: 调用方限制(startEntry={startEntryIdx}), 当前{TurnCount}轮");
                }
            }

            return BuildApiMessagesSnapshot(startEntryIdx, dynamicBlock);
        }

        /// <summary>
        /// 解析当前应当压缩到哪个条目边界。
        /// 供正常请求构建和同一用户请求内的工具循环共用，确保触发条件一致。
        /// </summary>
        private int ResolveCompressionStartIndex(
            int startEntryIdx,
            out string compressionReason,
            out int targetSummaryTokens)
        {
            int compressionStart = startEntryIdx;
            compressionReason = "none";
            targetSummaryTokens = 0;

            if (_compressor != null && _compressor.Config.AutoCompressEnabled)
            {
                var config = _compressor.Config;
                double defaultTargetRatio = Math.Max(0.05, Math.Min(0.95, config.CompressionTargetRatio));
                targetSummaryTokens = (int)(TokenBudget * defaultTargetRatio);

                int tokenWindowStart = FindCacheWindowStart(out string triggerReason);
                if (tokenWindowStart > compressionStart)
                {
                    int aggressiveTurns = Math.Max(1, (int)(CacheWindowMaxTurns * CompressionAggressiveness));
                    int aggressiveStart = FindTurnStartIndex(aggressiveTurns);
                    compressionStart = Math.Max(tokenWindowStart, aggressiveStart);
                    compressionReason = triggerReason;
                }

                if (EstimatedTokens > TokenBudget * config.CompressionThreshold)
                {
                    bool severe = EstimatedTokens > TokenBudget * config.AggressiveThreshold;
                    double targetRatio = severe
                        ? config.AggressiveCompressionTargetRatio
                        : config.CompressionTargetRatio;
                    targetRatio = Math.Max(0.05, Math.Min(0.95, targetRatio));
                    int targetTokens = (int)(TokenBudget * targetRatio);
                    targetSummaryTokens = targetTokens;
                    int budgetStart = FindTokenTargetStartIndex(targetTokens);
                    if (budgetStart > compressionStart)
                    {
                        compressionStart = budgetStart;
                        compressionReason = severe
                            ? $"usage>{config.AggressiveThreshold:P0}->{targetRatio:P0}"
                            : $"usage>{config.CompressionThreshold:P0}->{targetRatio:P0}";
                    }
                }
            }
            else
            {
                AutoTrimIfNeeded();
                int tokenWindowStart = FindCacheWindowStart(out string triggerReason);
                if (tokenWindowStart > compressionStart)
                {
                    compressionStart = tokenWindowStart;
                    compressionReason = triggerReason;
                }
            }

            return compressionStart;
        }

        /// <summary>
        /// 同一用户请求的 Agent 工具循环中，在工具结果写回后检查并执行压缩。
        /// 返回的消息数量用于同步裁剪本地 messages，避免下一轮仍发送旧历史。
        /// </summary>
        public bool TryCompressForToolLoop(
            out int staticPrefixMessageCount,
            out int removedMessageCount,
            out string? dynamicBlock)
        {
            staticPrefixMessageCount = 0;
            removedMessageCount = 0;
            dynamicBlock = null;

            if (_cacheSnapshotEntryIndex.HasValue)
                return false;

            int compressionStart = ResolveCompressionStartIndex(
                0,
                out string compressionReason,
                out int targetSummaryTokens);
            if (compressionStart <= 0)
                return false;

            if (!HasNewCompressibleEntries(compressionStart))
            {
                if (IsSystemAndCompressedContextAtBudget())
                    MarkConversationResetNeeded(compressionReason);
                return false;
            }

            string? currentDynamicBlock = BuildCompressionPrefixDynamicBlock();
            var staticPrefix = BuildApiMessagesSnapshot(
                startEntryIdx: 0,
                currentDynamicBlock,
                entryLimitOverride: 0);
            var compressionPrefix = BuildApiMessagesSnapshot(
                startEntryIdx: 0,
                currentDynamicBlock,
                entryLimitOverride: compressionStart);

            staticPrefixMessageCount = staticPrefix.Count;
            removedMessageCount = Math.Max(0, compressionPrefix.Count - staticPrefix.Count);
            if (removedMessageCount <= 0)
                return false;

            Logger.Info($"[ToolLoopCompression] 触发压缩: {compressionReason}, " +
                $"待压缩消息={removedMessageCount}, 当前={EstimatedTokens:N0}/{TokenBudget:N0}");

            CompressEntriesBeforeWindow(
                compressionStart,
                compressionPrefix,
                targetSummaryTokens);
            dynamicBlock = _cachedDynamicBlock ?? BuildDynamicContextBlock();
            return true;
        }

        /// <summary>
        /// 构建压缩请求使用的动态块。始终优先使用包含最新压缩摘要的实时内容，
        /// 防止缓存的旧动态块漏掉已生成的 [对话历史摘要]。
        /// </summary>
        private string? BuildCompressionPrefixDynamicBlock()
        {
            string? liveDynamicBlock = BuildDynamicContextBlock();
            if (!string.IsNullOrWhiteSpace(liveDynamicBlock))
                return liveDynamicBlock;

            return _cachedDynamicBlock;
        }

        /// <summary>
        /// 构建未压缩的 API 消息快照，不修改任何历史条目。
        /// 该快照既是正常对话前缀，也是压缩请求复用的前缀。
        /// </summary>
        private List<ChatApiMessage> BuildApiMessagesSnapshot(
            int startEntryIdx,
            string? dynamicBlock,
            int? entryLimitOverride = null)
        {
            var messages = new List<ChatApiMessage>();

            // ── [0] 稳定系统提示词（共享前缀 + 固定提示词）──
            string stableSystemPrompt = BuildStableSystemPrompt();
            if (!string.IsNullOrWhiteSpace(stableSystemPrompt))
                messages.Add(new ChatApiMessage { Role = "system", Content = stableSystemPrompt });

            // ── [1] 动态上下文块（仅在非空时注入）──
            if (!string.IsNullOrWhiteSpace(dynamicBlock))
                messages.Add(new ChatApiMessage { Role = "system", Content = dynamicBlock });

            // ── [2..] 对话历史 ──
            int entryLimit = entryLimitOverride ?? _cacheSnapshotEntryIndex ?? _entries.Count;
            for (int i = startEntryIdx; i < _entries.Count && i < entryLimit; i++)
            {
                var entry = _entries[i];

                // ── 对账补写的「不重放」条目不进入请求序列 ──
                //     它们保留在 _entries 中供持久化 / 调试 / 子任务可见，
                //     但对应内容由构建器按需重新生成，重放会造成重复。
                if (!entry.Replay)
                    continue;

                if (string.IsNullOrEmpty(entry.Content)
                    && (entry.ToolCalls == null || entry.ToolCalls.Count == 0)
                    && (entry.MultimodalContent == null || entry.MultimodalContent.Count == 0))
                    continue;

                var apiMsg = new ChatApiMessage
                {
                    Role = entry.Role,
                    Content = entry.Content,
                    MultimodalContent = CloneContentParts(entry.MultimodalContent),
                };

                if (entry.Role == "assistant")
                {
                    // DeepSeek 思考模式要求每个后续请求都继续回传历史 reasoning_content。
                    // 必须逐条原样回放，不能截断、聚合或按轮次丢弃，否则会返回 HTTP 400。
                    apiMsg.ReasoningContent = entry.ReasoningContent;
                }

                if (entry.Role == "assistant" && entry.ToolCalls != null && entry.ToolCalls.Count > 0)
                    apiMsg.ToolCalls = entry.ToolCalls;

                if (entry.Role == "tool")
                {
                    if (!string.IsNullOrEmpty(entry.ToolCallId))
                        apiMsg.ToolCallId = entry.ToolCallId;
                    if (!string.IsNullOrEmpty(entry.Name))
                        apiMsg.Name = entry.Name;
                }

                messages.Add(apiMsg);
            }

            return messages;
        }

        /// <summary>
        /// 构建动态上下文块：合并压缩摘要、记忆上下文和语言守卫。
        /// 所有同轮次内相对稳定的内容合并为一段文本，作为单条 system 消息注入。
        /// 这样将可变内容隔离在一个位置，保护 messages[0] 和对话历史前缀的缓存稳定性。
        /// </summary>
        private string? BuildDynamicContextBlock()
        {
            var parts = new List<string>();

            // 压缩摘要
            if (_compressor != null)
            {
                string compressedText = _compressor.GetCompressedContextText();
                if (!string.IsNullOrWhiteSpace(compressedText))
                    parts.Add(compressedText);
            }

            // 记忆上下文
            if (!string.IsNullOrWhiteSpace(_memoryContext))
                parts.Add(_memoryContext!);

            if (parts.Count == 0)
                return null;

            return string.Join("\n\n", parts);
        }

        /// <summary>
        /// 构建易变上下文块：Active Working Set + 联网搜索结果 + RAG 检索上下文。
        /// 这些内容在同一会话内可能每轮都变，应从消息数组末尾注入以保护前缀缓存。
        /// </summary>
        public string? BuildVolatileContextBlock()
        {
            var parts = new List<string>();

            // ── 解决方案与 IDE 实时态快照（置于易变块最前，不进入 user 轮次）──
            if (!string.IsNullOrWhiteSpace(_solutionContext))
                parts.Add(_solutionContext!);

            if (!string.IsNullOrWhiteSpace(_ideContext))
                parts.Add(_ideContext!);

            // ── 活跃文件 Working Set ──
            if (_activeFileTracker != null)
            {
                string wsRoot = !string.IsNullOrWhiteSpace(_workspaceRoot)
                    ? _workspaceRoot!
                    : Environment.CurrentDirectory;
                string activeFileSummary = _activeFileTracker.GetActiveFileSummary(wsRoot);
                if (!string.IsNullOrWhiteSpace(activeFileSummary))
                    parts.Add(activeFileSummary);
            }

            // ── RAG 检索上下文 ──
            if (!string.IsNullOrWhiteSpace(_ragContext))
                parts.Add(_ragContext!);

            if (parts.Count == 0)
                return null;

            return string.Join("\n\n", parts);
        }

        /// <summary>
        /// 将当前 volatile 块固化到标准历史中当前 user 之前。
        /// 下一轮请求会保留上一轮快照，避免 DeepSeek 前缀缓存在旧快照位置断裂。
        /// </summary>
        public bool PersistCurrentVolatileSnapshot()
        {
            string? snapshot = BuildVolatileContextBlock();
            if (string.IsNullOrWhiteSpace(snapshot))
                return false;

            int lastUserIndex = -1;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Role == "user")
                {
                    lastUserIndex = i;
                    break;
                }
            }

            if (lastUserIndex < 0)
                return false;

            int turnIndex = _entries[lastUserIndex].TurnIndex;
            if (lastUserIndex > 0)
            {
                var previous = _entries[lastUserIndex - 1];
                if (previous.IsVolatileSnapshot && previous.TurnIndex == turnIndex)
                    return false;
            }

            var snapshotEntry = CreateEntry(
                Role: "system",
                Content: snapshot,
                TurnIndex: turnIndex,
                IsVolatileSnapshot: true);

            _entries.Insert(lastUserIndex, snapshotEntry);
            _estimatedTokens += EstimateTokens(snapshot);
            return true;
        }

        /// <summary>
        /// 查找缓存窗口的起始条目索引。
        /// 从 _entries 末尾向前遍历，累计轮次、token 数（UTF-8 字节估算）、条目数，
        /// 直到达到任一限制。返回窗口内第一个条目的索引（包含），0 表示全部保留。
        /// </summary>
        private int FindCacheWindowStart(out string triggerReason)
        {
            int maxTurns = CacheWindowMaxTurns;
            int maxTokens = CacheWindowMaxTokens;
            int maxEntries = CacheWindowMaxEntries;

            triggerReason = "none";

            // 全部禁用 → 返回 0
            if (maxTurns <= 0 && maxTokens <= 0 && maxEntries <= 0)
                return 0;

            int turnCount = 0;
            int tokenSum = 0;
            int entryCount = 0;
            int lastUserTurn = -1;

            // 从后往前扫描
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];

                // ── Token 估算：UTF-8 字节数 / 2（比 chars/4 更接近 API 实际值）──
                int contentBytes = 0;
                if (!string.IsNullOrEmpty(entry.Content))
                    contentBytes = System.Text.Encoding.UTF8.GetByteCount(entry.Content);
                if (!string.IsNullOrEmpty(entry.ReasoningContent))
                    contentBytes += System.Text.Encoding.UTF8.GetByteCount(entry.ReasoningContent);
                int entryTokens = Math.Max(1, contentBytes / 2)
                    + EstimateMultimodalTokens(entry.MultimodalContent);

                // 检测到新的 user 消息 → 轮次+1
                if (entry.Role == "user" && entry.TurnIndex > 0)
                {
                    if (lastUserTurn != entry.TurnIndex)
                    {
                        turnCount++;
                        lastUserTurn = entry.TurnIndex;
                    }
                }

                tokenSum += entryTokens;
                entryCount++;

                // 检查是否超出窗口限制
                bool turnLimitExceeded = maxTurns > 0 && turnCount >= maxTurns;
                bool tokenLimitExceeded = maxTokens > 0 && tokenSum >= maxTokens;
                bool entryLimitExceeded = maxEntries > 0 && entryCount >= maxEntries;

                if (turnLimitExceeded || tokenLimitExceeded || entryLimitExceeded)
                {
                    var reasons = new List<string>();
                    if (turnLimitExceeded) reasons.Add($"轮次数>{maxTurns}(当前{TurnCount})");
                    if (tokenLimitExceeded) reasons.Add($"Token>{maxTokens / 1000}K(当前{EstimatedTokens / 1000}K)");
                    if (entryLimitExceeded) reasons.Add($"条目数>{maxEntries}(当前{_entries.Count})");
                    triggerReason = string.Join(", ", reasons);
                    return i;
                }
            }

            // 所有条目都在窗口内
            return 0;
        }

        /// <summary>
        /// 从末尾向前扫描，找到保留最近 maxTurns 轮所需的起始条目索引。
        /// 用于激进压缩：触发裁剪时不仅移除窗口外的，还多移除一部分窗口内的旧轮次。
        /// </summary>
        private int FindTurnStartIndex(int maxTurns)
        {
            if (maxTurns <= 0) return _entries.Count;

            int turnCount = 0;
            int lastUserTurn = -1;

            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (entry.Role == "user" && entry.TurnIndex > 0)
                {
                    if (lastUserTurn != entry.TurnIndex)
                    {
                        turnCount++;
                        lastUserTurn = entry.TurnIndex;
                        if (turnCount >= maxTurns)
                            return i;
                    }
                }
            }

            return 0;
        }

        /// <summary>
        /// 按目标 Token 数选择需要保留的历史起点，并对齐到 user 轮次边界。
        /// 如果最近一轮本身已超过目标，则至少保留这一轮。
        /// </summary>
        private int FindTokenTargetStartIndex(int targetTokens)
        {
            if (_entries.Count == 0 || targetTokens <= 0)
                return 0;

            var turnStarts = new List<int>();
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Role == "user" && _entries[i].TurnIndex > 0)
                    turnStarts.Add(i);
            }

            if (turnStarts.Count == 0)
                return 0;

            int totalEntryTokens = _entries.Sum(EstimateEntryTokens);
            double calibrationFactor = _calibrationFactor > 0 ? _calibrationFactor : 1.0;
            int targetRawTokens = (int)(targetTokens / calibrationFactor);
            int overheadTokens = Math.Max(0, _estimatedTokens - totalEntryTokens);
            int allowedEntryTokens = Math.Max(0, targetRawTokens - overheadTokens);

            int candidateStart = turnStarts[turnStarts.Count - 1];
            int retainedTokens = EstimateRangeTokens(candidateStart, _entries.Count);

            for (int i = turnStarts.Count - 2; i >= 0; i--)
            {
                int previousTurnStart = turnStarts[i];
                int previousTurnTokens = EstimateRangeTokens(previousTurnStart, candidateStart);
                if (retainedTokens + previousTurnTokens > allowedEntryTokens)
                    break;

                retainedTokens += previousTurnTokens;
                candidateStart = previousTurnStart;
            }

            return candidateStart;
        }

        private static int EstimateEntryTokens(ContextEntry entry)
        {
            int tokens = EstimateTokens(entry.Content)
                + EstimateMultimodalTokens(entry.MultimodalContent);
            if (!string.IsNullOrEmpty(entry.ReasoningContent))
                tokens += EstimateTokens(entry.ReasoningContent);
            return tokens;
        }

        private int EstimateRangeTokens(int startIndex, int endExclusive)
        {
            int tokens = 0;
            for (int i = startIndex; i < endExclusive && i < _entries.Count; i++)
                tokens += EstimateEntryTokens(_entries[i]);
            return tokens;
        }

        /// <summary>
        /// 将指定索引之前的条目移出 _entries，可选压缩为摘要。
        /// 即使没有压缩服务，也会移除旧条目以控制上下文大小。
        /// </summary>
        private void CompressEntriesBeforeWindow(
            int windowStartIdx,
            IReadOnlyList<ChatApiMessage>? compressionPrefix,
            int targetSummaryTokens)
        {
            if (windowStartIdx <= 0)
                return;

            var entriesToCompress = new List<ContextEntry>();
            int removedTokens = 0;

            for (int i = 0; i < windowStartIdx; i++)
            {
                var entry = _entries[i];
                entriesToCompress.Add(entry);
                removedTokens += EstimateTokens(entry.Content);
                removedTokens += EstimateMultimodalTokens(entry.MultimodalContent);
                if (!string.IsNullOrEmpty(entry.ReasoningContent))
                    removedTokens += EstimateTokens(entry.ReasoningContent);
            }

            if (entriesToCompress.Count == 0) return;

            long lastCompressedEntryId = entriesToCompress.Max(e => e.EntryId);
            _lastCompressedEntryId = Math.Max(_lastCompressedEntryId, lastCompressedEntryId);

            // 确定压缩的轮次范围
            int fromTurn = entriesToCompress.Where(e => e.TurnIndex > 0).Select(e => e.TurnIndex).DefaultIfEmpty(1).First();
            int toTurn = entriesToCompress.Where(e => e.TurnIndex > 0).Select(e => e.TurnIndex).DefaultIfEmpty(fromTurn).Last();

            // ── 始终从 _entries 中移除（无论有无 compressor）──
            _entries.RemoveRange(0, windowStartIdx);
            _estimatedTokens -= removedTokens;
            _cachedDynamicBlock = null;

            // ── 同步完成摘要，确保同一轮后续消息构建立即包含压缩结果 ──
            if (_compressor != null)
            {
                _isCompressing = true;
                RaiseCompressionStateChanged(true);
                try
                {
                    var summary = System.Threading.Tasks.Task.Run(() =>
                        _compressor.CompressTurnsAsync(
                            entriesToCompress,
                            fromTurn,
                            toTurn,
                            compressionPrefix,
                            CancellationToken.None,
                            targetSummaryTokens)).GetAwaiter().GetResult();

                    _cachedDynamicBlock = BuildDynamicContextBlock();
                    Logger.Info($"[CacheWindow] 压缩第 {fromTurn}-{toTurn} 轮: " +
                        $"{removedTokens} → {summary.CompressedTokens} tokens (摘要)");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[CacheWindow] 压缩失败: {ex.Message}");
                    _cachedDynamicBlock = BuildDynamicContextBlock();
                }
                finally
                {
                    _isCompressing = false;
                    RaiseCompressionStateChanged(false);
                }
            }

            Logger.Info($"[CacheWindow] 裁剪 {entriesToCompress.Count} 条旧消息 " +
                $"(第 {fromTurn}-{toTurn} 轮, {removedTokens} tokens)，" +
                $"保留最近 {TurnCount} 轮在窗口内" +
                (_compressor == null ? " (无压缩器，直接丢弃)" : ""));
        }

        private void RaiseCompressionStateChanged(bool isCompressing)
        {
            try
            {
                CompressionStateChanged?.Invoke(isCompressing, UsagePercent);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[ContextManager] 压缩状态通知失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 构建仅包含最近 N 轮的 API 消息列表（用于 Agent 子调用）。
        /// 委托给 BuildApiMessagesCore()，先按轮次计算起始索引。
        /// </summary>
        /// <param name="maxTurns">保留的最大轮次数</param>
        public List<ChatApiMessage> BuildApiMessagesRecentTurns(int maxTurns)
        {
            if (TurnCount <= maxTurns)
                return BuildApiMessages();

            int turnsToSkip = TurnCount - maxTurns;
            int userCount = 0;
            int startEntryIdx = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Role == "user")
                {
                    userCount++;
                    if (userCount > turnsToSkip)
                    {
                        startEntryIdx = i;
                        break;
                    }
                }
            }

            return BuildApiMessagesCore(startEntryIdx);
        }

        /// <summary>
        /// 移除指定索引之后的所有消息（用于重试/编辑场景）。
        /// </summary>
        public void TrimAfter(int entryIndex)
        {
            if (entryIndex < 0 || entryIndex >= _entries.Count) return;

            int removeCount = _entries.Count - entryIndex;

            // 重新计算被移除部分的 token
            for (int i = entryIndex; i < _entries.Count; i++)
            {
                _estimatedTokens -= EstimateTokens(_entries[i].Content);
                _estimatedTokens -= EstimateMultimodalTokens(_entries[i].MultimodalContent);
                if (!string.IsNullOrEmpty(_entries[i].ReasoningContent))
                    _estimatedTokens -= EstimateTokens(_entries[i].ReasoningContent);
            }

            _entries.RemoveRange(entryIndex, removeCount);
        }

        /// <summary>
        /// 移除最后一个 user 消息及其之后的所有条目，同时清除不属于任何轮次的 custom 消息
        /// （如 skill 指令）。用于重试/编辑回退场景。
        /// </summary>
        /// <summary>
        /// 移除最后一条助手消息（用于流式取消时不保存不完整回复）。
        /// </summary>
        public void RemoveLastAssistantMessage()
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Role == "assistant")
                {
                    _estimatedTokens -= EstimateTokens(_entries[i].Content);
                    if (!string.IsNullOrEmpty(_entries[i].ReasoningContent))
                        _estimatedTokens -= EstimateTokens(_entries[i].ReasoningContent);
                    _entries.RemoveAt(i);
                    return;
                }
            }
        }

        #endregion

        #region Core API — Token 管理

        /// <summary>
        /// 估算文本的 Token 数。
        /// 改进版：1 英文字符 ≈ 0.3 token，1 中文字符 ≈ 0.6 token，
        /// 1 数字/符号 ≈ 0.3 token，CJK 标点 ≈ 0.6 token。
        /// </summary>
        public static int EstimateTokens(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            int chineseChars = 0;
            int otherChars = 0;
            foreach (char c in text!)
            {
                // CJK 统一表意文字 + CJK 扩展
                if ((c >= 0x4E00 && c <= 0x9FFF) ||
                    (c >= 0x3400 && c <= 0x4DBF) ||
                    (c >= 0x20000 && c <= 0x2A6DF))
                {
                    chineseChars++;
                }
                // CJK 标点符号
                else if ((c >= 0x3000 && c <= 0x303F) ||
                         (c >= 0xFF00 && c <= 0xFFEF))
                {
                    chineseChars++;
                }
                else if (!char.IsWhiteSpace(c))
                {
                    otherChars++;
                }
            }
            // 1 中文 ≈ 0.6 token, 1 英文/数字 ≈ 0.3 token
            return (int)(chineseChars * 0.6 + otherChars * 0.3) + 1;
        }

        /// <summary>
        /// 估算消息列表的 Token 数。与持久化统计不同，这里包含
        /// tool_calls 的函数名和参数，适用于 Handoff 转发快照。
        /// </summary>
        public static int EstimateMessageTokens(IEnumerable<ChatApiMessage>? messages)
        {
            if (messages == null) return 0;

            int tokens = 0;
            foreach (var message in messages)
            {
                if (message == null) continue;

                tokens += EstimateTokens(message.Content);
                tokens += EstimateMultimodalTokens(message.MultimodalContent);
                tokens += EstimateTokens(message.ReasoningContent);
                if (message.ToolCalls == null) continue;

                foreach (var toolCall in message.ToolCalls)
                {
                    tokens += EstimateTokens(toolCall.Function?.Name);
                    tokens += EstimateTokens(toolCall.Function?.Arguments);
                }
            }

            return tokens;
        }

        private static int EstimateMultimodalTokens(List<ChatContentPart>? parts)
        {
            if (parts == null || parts.Count == 0)
                return 0;

            int tokens = 0;
            foreach (var part in parts)
            {
                if (part.Type == "text")
                {
                    tokens += EstimateTokens(part.Text);
                }
                else if (part.Type == "image_url")
                {
                    // 图片按尺寸/张数估算，绝不把 base64 data URI 当文本字符计数
                    tokens += EstimateImageTokens(part.ImageUrl?.Url);
                }
                else if (part.Type == "file")
                {
                    // file_id 引用极小；file_data 是 base64 数据，同样不能当文本计数
                    tokens += EstimateTokens(part.File?.FileId);
                    if (part.File?.FileData != null)
                        tokens += EstimateImageTokens(part.File.FileData);
                    tokens += EstimateTokens(part.File?.Filename);
                }
                else
                {
                    // 未知类型兜底
                    tokens += EstimateTokens(part.Text);
                }
            }
            return tokens;
        }

        /// <summary>
        /// 估算单张图片的 token 数，依据 DeepSeek Vision 文档的尺寸换算规则：
        /// 推理前会缩放，使有效面积落在约 [384×384, 800×800] 之间，上限约 384 token/张。
        /// https://api-docs.deepseek.com/guides/vision/#token-usage
        /// </summary>
        private static int EstimateImageTokens(string? imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
                return 0;

            const int tokMin = 85;
            const int tokMax = 384;
            const double minArea = 384.0 * 384.0;
            const double maxArea = 800.0 * 800.0;
            const double pixelsPerToken = 1667.0;

            // 外链 http(s) URL：拿不到本地尺寸，按中等图片估算
            if (!imageUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return 256;

            int comma = imageUrl.IndexOf(',');
            if (comma < 0 || comma + 1 >= imageUrl.Length)
                return 256;

            try
            {
                byte[] bytes = Convert.FromBase64String(imageUrl.Substring(comma + 1));
                if (TryGetImageDimensions(bytes, out int width, out int height))
                {
                    double area = (double)width * height;
                    double effectiveArea = Math.Min(maxArea, Math.Max(minArea, area));
                    int tok = (int)Math.Round(effectiveArea / pixelsPerToken);
                    return Math.Max(tokMin, Math.Min(tokMax, tok));
                }
            }
            catch
            {
                // 解析失败按中等图片兜底，避免计数异常
            }

            return 256;
        }

        /// <summary>
        /// 从图像字节中解析宽高。支持 PNG / JPEG / GIF / WebP(VP8X/VP8L)；失败返回 false。
        /// </summary>
        private static bool TryGetImageDimensions(byte[] b, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (b == null || b.Length < 24) return false;

            // PNG：签名 + IHDR，宽高在 offset 16/20（大端）
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            {
                width = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
                height = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
                return width > 0 && height > 0;
            }

            // GIF：宽高在 offset 6/8（小端）
            if (b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b.Length >= 10)
            {
                width = b[6] | (b[7] << 8);
                height = b[8] | (b[9] << 8);
                return width > 0 && height > 0;
            }

            // JPEG：FF D8 FF，扫描 SOF 标记
            if (b[0] == 0xFF && b[1] == 0xD8)
                return TryGetJpegDimensions(b, out width, out height);

            // WebP：RIFF .... WEBP
            if (b.Length >= 30
                && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F'
                && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
                return TryGetWebPDimensions(b, out width, out height);

            return false;
        }

        private static bool TryGetJpegDimensions(byte[] b, out int width, out int height)
        {
            width = 0;
            height = 0;
            int i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF)
                {
                    i++;
                    continue;
                }

                int marker = b[i + 1];
                if (marker == 0xFF) { i++; continue; }            // 填充字节
                if (marker == 0xD9) break;                        // EOI
                if (marker == 0xD8 || marker == 0x01) { i += 2; continue; }
                if (marker >= 0xD0 && marker <= 0xD7) { i += 2; continue; } // RSTn

                int segLen = (b[i + 2] << 8) | b[i + 3];
                if (segLen < 2) break;

                // SOF0~SOF15，排除 DHT(0xC4)/JPG(0xC8)/DAC(0xCC)
                bool isSof = marker >= 0xC0 && marker <= 0xCF
                    && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (isSof)
                {
                    height = (b[i + 5] << 8) | b[i + 6];
                    width = (b[i + 7] << 8) | b[i + 8];
                    return width > 0 && height > 0;
                }

                i += 2 + segLen;
            }
            return false;
        }

        private static bool TryGetWebPDimensions(byte[] b, out int width, out int height)
        {
            width = 0;
            height = 0;

            // VP8X（扩展）：canvas 尺寸在 offset 24/27（24 位小端，值为尺寸-1）
            if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'X')
            {
                width = 1 + (b[24] | (b[25] << 8) | (b[26] << 16));
                height = 1 + (b[27] | (b[28] << 8) | (b[29] << 16));
                return width > 0 && height > 0;
            }

            // VP8L（无损）：chunk payload 首字节 0x2F，随后 4 字节小端含 14bit 宽-1 / 高-1
            if (b.Length >= 26 && b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'L')
            {
                int v = b[21] | (b[22] << 8) | (b[23] << 16) | (b[24] << 24);
                width = (v & 0x3FFF) + 1;
                height = ((v >> 14) & 0x3FFF) + 1;
                return width > 0 && height > 0;
            }

            return false;
        }

        private static List<ChatContentPart>? CloneContentParts(List<ChatContentPart>? parts)
            => ChatMessageCloner.CloneContentParts(parts);

        /// <summary>
        /// 当估算 Token 超过预算时，自动修剪最旧的轮次（旧行为，无压缩服务时使用）。
        /// 使用校准后估算值作为阈值比较，确保在 API 真实 token 消耗接近预算时触发。
        /// </summary>
        public void AutoTrimIfNeeded()
        {
            while (EstimatedTokens > TokenBudget && TurnCount > AutoTrimTurns + 1)
            {
                TrimOldestTurn();
            }
        }

        /// <summary>
        /// 移除最旧的一轮对话（一个 user 消息 + 其后续的 assistant/tool 消息）。
        /// 仅在无压缩服务时作为回退使用。
        /// </summary>
        public void TrimOldestTurn()
        {
            // 找到第一个 user 消息
            int startIdx = -1;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Role == "user" && _entries[i].TurnIndex > 0)
                {
                    startIdx = i;
                    break;
                }
            }

            if (startIdx < 0) return;

            int firstTurnIndex = _entries[startIdx].TurnIndex;

            // 移除从 startIdx 到下一个 user 消息（不含）之间的所有条目
            int endIdx = _entries.Count;
            for (int i = startIdx + 1; i < _entries.Count; i++)
            {
                if (_entries[i].Role == "user")
                {
                    endIdx = i;
                    break;
                }
            }

            // 扣除 token
            for (int i = startIdx; i < endIdx; i++)
            {
                _estimatedTokens -= EstimateTokens(_entries[i].Content);
                _estimatedTokens -= EstimateMultimodalTokens(_entries[i].MultimodalContent);
                if (!string.IsNullOrEmpty(_entries[i].ReasoningContent))
                    _estimatedTokens -= EstimateTokens(_entries[i].ReasoningContent);
            }

            _entries.RemoveRange(startIdx, endIdx - startIdx);
        }

        /// <summary>
        /// 获取上下文统计信息。
        /// </summary>
        public ContextStats GetStats()
        {
            // 后台 Agent 写入与 UI 统计读取可能并发发生。List<T>.ToArray() 不做版本枚举，
            // 但扩容瞬间仍可能复制到未初始化槽位；统计属于只读诊断数据，跳过空槽位即可。
            var entries = _entries
                .ToArray()
                .Where(e => e is not null)
                .ToArray();

            var stats = new ContextStats
            {
                EstimatedTokens = EstimatedTokens,
                TokenBudget = TokenBudget,
                MessageCount = entries.Length,
                TurnCount = entries
                    .Where(e => e.Role == "user" && e.TurnIndex > 0)
                    .Select(e => e.TurnIndex)
                    .Distinct()
                    .Count(),
                ToolCallCount = entries.Sum(e => e.ToolCalls?.Count ?? 0),
                SystemPromptTokens = EstimateTokens(_systemPrompt),
                CompressedTurns = _compressor?.CompressedSummaries.Count ?? 0,
                CompressedSummaryTokens = _compressor?.TotalCompressedTokens ?? 0,
            };

            // 统计工具调用结果 token
            stats.ToolResultTokens = entries
                .Where(e => e.Role == "tool")
                .Sum(e => EstimateTokens(e.Content));

            return stats;
        }

        /// <summary>
        /// 使用 API 返回的实际 prompt_tokens 校准本地字符级估算。
        /// 使用指数移动平均 (EMA) 平滑校准系数，避免单次波动。
        /// 应在每次 Chat API 调用完成后调用。
        ///
        /// 幂等保护：同一份 usage 常被两条链路各上报一次（BaseAgent 工具循环内按轮
        /// 上报、UI 层 RefreshConsumptionDisplay 收尾上报），二者共享 _apiService.LastUsage。
        /// 若不去重，同一 ratio 会被 EMA 连乘两次，估算因子被过度拉向单次观测值。
        /// 故此处以 prompt_tokens 作为「同一次 API 调用」的标识，重复值直接忽略。
        /// 说明：不同调用的 prompt_tokens 相同属于极小概率（上下文逐轮增长），
        /// 即使偶发碰撞也只是少校准一次，远优于重复叠加。
        /// </summary>
        /// <param name="actualPromptTokens">API usage 中的实际 prompt_tokens</param>
        public void CalibrateFromApiUsage(long actualPromptTokens)
        {
            if (actualPromptTokens <= 0 || _estimatedTokens <= 0) return;

            // ── 同一份 usage 去重（多链路重复上报）──
            if (actualPromptTokens == _lastCalibratedPromptTokens) return;

            // 计算本次调用的实际/估算比率
            double ratio = (double)actualPromptTokens / _estimatedTokens;

            // 合理性检查：比率在 0.2 ~ 10 之间才参与校准（过滤异常值）
            if (ratio < 0.2 || ratio > 10.0) return;

            // 记录已校准的 usage 标识；放在合理性检查之后，确保被过滤的异常值
            // 不会占用该标识而误挡掉后续同值的合法校准
            _lastCalibratedPromptTokens = actualPromptTokens;

            // 指数移动平均：newFactor = oldFactor * (1 - α) + ratio * α
            _calibrationFactor = _calibrationFactor * (1 - CalibrationAlpha) + ratio * CalibrationAlpha;

            Logger.Info($"[ContextCalibration] API prompt_tokens={actualPromptTokens}, " +
                        $"rawEstimate={_estimatedTokens}, ratio={ratio:F3}, " +
                        $"newFactor={_calibrationFactor:F3}");
        }

        #endregion

        #region Core API — 查询与序列化

        /// <summary>
        /// 获取所有 user/assistant 角色的原始消息列表（用于持久化和 UI）。
        /// </summary>
        /// <remarks>
        /// 排除 <c>Replay == false</c> 的对账补写条目（子任务提示等脚手架），
        /// 使 UI 展示与轮次语义保持一致；这类内容仍保留在 <c>_entries</c> 中可追溯。
        /// </remarks>
        public List<ChatApiMessage> GetConversationHistory()
        {
            return _entries
                .Where(e => e.Replay && (e.Role == "user" || e.Role == "assistant"))
                .Select(e => new ChatApiMessage
                {
                    Role = e.Role,
                    Content = e.Content,
                    MultimodalContent = CloneContentParts(e.MultimodalContent),
                    ReasoningContent = e.ReasoningContent,
                })
                .ToList();
        }

        /// <summary>
        /// 获取包含所有角色（含 tool）的完整消息列表，用于完整持久化。
        /// 恢复时使用 RestoreFullContext()。
        /// </summary>
        /// <remarks>
        /// 排除 <c>Replay == false</c> 的脚手架条目（子任务尾提示、Agent 行为指令、volatile 块）：
        /// 它们由请求构建器在每次请求时按需重新生成，语义不会丢失；
        /// 若写入持久化，恢复时无法区分其「不重放」属性，会被当成历史重放，反而污染后续请求。
        /// 这类内容在本次会话内仍完整保留在 <c>_entries</c> 中，可经 <see cref="GetDebugSummary"/> 追溯。
        /// </remarks>
        public List<ChatApiMessage> GetFullContext()
        {
            return _entries
                .Where(e => e.Replay)
                .Select(e => new ChatApiMessage
                {
                    Role = e.Role,
                    Content = e.Content,
                    MultimodalContent = CloneContentParts(e.MultimodalContent),
                    ReasoningContent = e.ReasoningContent,
                    ToolCalls = CloneToolCalls(e.ToolCalls),
                    ToolCallId = e.ToolCallId,
                    Name = e.Name,
                })
                .ToList();
        }

        /// <summary>
        /// 从完整消息列表恢复上下文（含 tool 消息）。
        /// </summary>
        public void RestoreFullContext(List<ChatApiMessage> fullHistory)
        {
            Clear();
            foreach (var msg in fullHistory)
            {
                switch (msg.Role)
                {
                    case "user":
                        // ── 区分「真实用户轮次」与「请求侧注入的 user」──
                        //     真实轮次在落库时带「本轮用户需求」前缀（AddUserMessage 写入）；
                        //     Handoff 任务提示 / Edit 步骤提示带「当前任务目标」前缀，
                        //     它们属于当前轮的执行内容，不是新轮次。
                        //     若不区分，恢复后每个步骤提示都会被算作一轮，污染轮次计数与缓存窗口。
                        if (IsRealUserTurn(msg))
                            AddUserMessage(msg.Content ?? string.Empty, msg.MultimodalContent);
                        else
                            AddCustomMessage("user", msg.Content ?? string.Empty, CurrentTurnIndex);
                        break;
                    case "assistant":
                        AddAssistantMessage(msg.Content, msg.ReasoningContent, msg.ToolCalls);
                        break;
                    case "tool":
                        if (!string.IsNullOrEmpty(msg.ToolCallId))
                            AddToolResult(msg.ToolCallId!, msg.Name ?? "unknown", msg.Content ?? string.Empty, msg.MultimodalContent);
                        break;
                    case "system":
                        AddCustomMessage("system", msg.Content ?? string.Empty);
                        break;
                }
            }
        }

        /// <summary>
        /// 从持久化的 ChatApiMessage 列表恢复上下文。
        /// 用于会话切换时重建上下文。
        /// </summary>
        public void RestoreFromHistory(List<ChatApiMessage> history)
        {
            Clear();
            foreach (var msg in history)
            {
                if (msg.Role == "user")
                {
                    if (IsRealUserTurn(msg))
                        AddUserMessage(msg.Content ?? string.Empty, msg.MultimodalContent);
                    else
                        AddCustomMessage("user", msg.Content ?? string.Empty, CurrentTurnIndex);
                }
                else if (msg.Role == "assistant")
                {
                    AddAssistantMessage(msg.Content, msg.ReasoningContent);
                }
            }
        }

        /// <summary>
        /// 清空所有上下文。
        /// </summary>
        public void Clear()
        {
            _entries.Clear();
            _estimatedTokens = 0;
            _systemPrompt = null;
            _fixedSystemPrompt = null;
            _ideContext = null;
            _solutionContext = null;
            _skillContext = null;
            _alwaysInjectSkillsContext = null;
            _ragContext = null;
            _memoryContext = null;
            _compressor?.Clear();
            _pendingCompressedSummaries.Clear();
            _cacheSnapshotEntryIndex = null;
            _cachedDynamicBlock = null;
            _nextEntryId = 0;
            _lastCompressedEntryId = 0;
            _conversationResetNoticePending = false;
            // 新会话的校准去重标记需复位，否则首个 usage 若与上一会话末次值相同会被误判重复
            _lastCalibratedPromptTokens = 0;
        }

        /// <summary>
        /// 丢弃对话历史与压缩摘要，但保留 system prompt、记忆、RAG、IDE 等系统级上下文。
        /// 用于用户忽略“切换新对话”提示后继续发送消息的场景。
        /// </summary>
        public void ClearConversationHistory()
        {
            _entries.Clear();
            _estimatedTokens = 0;
            _compressor?.Clear();
            _pendingCompressedSummaries.Clear();
            _cacheSnapshotEntryIndex = null;
            _cachedDynamicBlock = null;
            _nextEntryId = 0;
            _lastCompressedEntryId = 0;
            _conversationResetNoticePending = false;
        }

        /// <summary>
        /// 获取对话历史的调试摘要。
        /// </summary>
        public string GetDebugSummary()
        {
            var sb = new StringBuilder();
            var stats = GetStats();
            sb.AppendLine($"=== 上下文管理器状态 ===");
            sb.AppendLine($"消息总数: {MessageCount}");
            sb.AppendLine($"轮次数: {TurnCount}");
            sb.AppendLine($"估算 Token: {_estimatedTokens:N0}/{TokenBudget:N0} ({UsagePercent:F1}%)");
            sb.AppendLine($"系统提示词: {(_systemPrompt != null ? $"{_systemPrompt.Length} 字符 ({stats.SystemPromptTokens} tokens)" : "无")}");
            sb.AppendLine($"Skill 上下文: {(_skillContext != null ? $"{_skillContext.Length} 字符" : "无")}");
            sb.AppendLine($"始终注入 Skill: {(_alwaysInjectSkillsContext != null ? $"{_alwaysInjectSkillsContext.Length} 字符" : "无")}");
            sb.AppendLine($"RAG 上下文: {(_ragContext != null ? $"{_ragContext.Length} 字符" : "无")}");
            sb.AppendLine($"压缩摘要: {stats.CompressedTurns} 组 ({stats.CompressedSummaryTokens} tokens)");
            sb.AppendLine();
            foreach (var entry in _entries)
            {
                string preview = (entry.Content ?? "").Length > 80
                    ? (entry.Content ?? "").Substring(0, 80) + "..."
                    : (entry.Content ?? "");
                string reasoning = !string.IsNullOrEmpty(entry.ReasoningContent) ? " [含思维链]" : "";
                string tools = entry.HasToolCalls ? $" [工具调用:{entry.ToolCalls?.Count}]" : "";
                sb.AppendLine($"[T{entry.TurnIndex}] {entry.Role}: {preview}{reasoning}{tools}");
            }
            return sb.ToString();
        }

        #endregion

        #region Internal Helpers

        /// <summary>
        /// 构建完整稳定 system prompt（冻结的用户/Skill 提示词 + 共享前缀）。
        /// 所有 API 构建路径必须通过此方法获取 messages[0]，避免前缀漂移。
        /// </summary>
        private string BuildStableSystemPrompt()
        {
            string sharedPrefix = AiPrompts.SharedImmutablePrefix;
            string? fixedPrompt = _fixedSystemPrompt
                ?? (string.IsNullOrWhiteSpace(_systemPrompt) && string.IsNullOrWhiteSpace(_skillContext)
                    ? null
                    : BuildFinalSystemPrompt());
            return CombineSystemParts(fixedPrompt, sharedPrefix) ?? string.Empty;
        }

        /// <summary>
        /// 组装最终的系统提示词（用户自定义 + Skill 上下文）。
        /// </summary>
        private string? BuildFinalSystemPrompt()
        {
            if (string.IsNullOrWhiteSpace(_systemPrompt) && string.IsNullOrWhiteSpace(_skillContext) && string.IsNullOrWhiteSpace(_alwaysInjectSkillsContext))
                return null;

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(_systemPrompt))
                parts.Add(_systemPrompt);
            if (!string.IsNullOrWhiteSpace(_skillContext))
                parts.Add(_skillContext);
            if (!string.IsNullOrWhiteSpace(_alwaysInjectSkillsContext))
                parts.Add(_alwaysInjectSkillsContext);

            return string.Join("\n\n", parts);
        }

        private static string? CombineSystemParts(string? first, string? second)
        {
            if (string.IsNullOrWhiteSpace(first)) return second;
            if (string.IsNullOrWhiteSpace(second)) return first;
            return first + "\n\n" + second;
        }

        #endregion

        #region Inner Types

        /// <summary>
        /// 上下文条目 — 内部存储单元，比 ChatApiMessage 更丰富。
        /// 对 ContextCompressorService 可见以支持压缩操作。
        /// </summary>
        internal class ContextEntry
        {
            /// <summary>单调递增条目 ID，用于判断压缩边界中的新内容</summary>
            public long EntryId { get; set; }
            public string Role { get; set; } = "user";
            public string? Content { get; set; }
            public List<ChatContentPart>? MultimodalContent { get; set; }
            public string? ReasoningContent { get; set; }
            public List<ToolCall>? ToolCalls { get; set; }
            public bool HasToolCalls { get; set; }
            public string? ToolCallId { get; set; }
            public string? Name { get; set; }
            /// <summary>所属轮次（1-based），-1 表示不属于任何轮次</summary>
            public int TurnIndex { get; set; } = -1;
            /// <summary>是否为固化到历史中的 volatile 上下文快照</summary>
            public bool IsVolatileSnapshot { get; set; }
            /// <summary>
            /// 是否参与后续请求的历史重放。
            /// 仅「对账补写」的条目会置 false：它们只用于持久化与子任务上下文可见性，
            /// 不进入 <see cref="BuildApiMessagesSnapshot"/> 的重放序列 ——
            /// 因为这类内容（子任务尾提示、Agent 行为指令、volatile 块）由请求构建器按需重新生成，
            /// 重放会与重新生成的那份重复，反而破坏前缀。
            /// </summary>
            public bool Replay { get; set; } = true;
        }

        #endregion
    }
}
