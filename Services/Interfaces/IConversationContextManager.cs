using DeepSeek_v4_for_VisualStudio.Models;
using System.Collections.Generic;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 统一上下文管理器接口 — 负责多轮对话历史的存储、拼接与 Token 预算管理。
    /// </summary>
    public interface IConversationContextManager
    {
        // ── 属性 ──
        int TokenBudget { get; set; }
        int AutoTrimTurns { get; set; }
        int TurnCount { get; }
        int MessageCount { get; }
        int EstimatedTokens { get; }
        bool IsEmpty { get; }
        double UsageRatio { get; }
        double UsagePercent { get; }

        // ── 缓存友好窗口（v1.1.10）──
        /// <summary>缓存窗口最大 token 数（0 = 禁用）</summary>
        int CacheWindowMaxTokens { get; set; }
        /// <summary>缓存窗口最大轮次数</summary>
        int CacheWindowMaxTurns { get; set; }
        /// <summary>缓存窗口最大条目数（兜底保护，0 = 不限）</summary>
        int CacheWindowMaxEntries { get; set; }

        // ── 系统/上下文设置 ──
        void SetSystemPrompt(string? prompt);
        void SetSolutionPath(string? solutionPath);
        void SetSkillContext(string? skillContext);
        void SetMemoryContext(string? memoryContext);
        void SetCompressor(ContextCompressorService? compressor);
        void SetActiveFileTracker(IActiveFileTracker? tracker);
        void SetWorkspaceRoot(string? workspaceRoot);

        // ── 前缀冻结（v1.1.9 前缀缓存优化）──
        /// <summary>
        /// 冻结系统提示词为不可变前缀。调用后 messages[0] 在整个会话期间保持不变。
        /// </summary>
        void FreezeSystemPrompt();

        /// <summary>
        /// 获取已冻结的系统提示词（null = 尚未冻结）。
        /// </summary>
        string? GetFixedSystemPrompt();

        // ── 消息管理 ──
        void AddUserMessage(string content);
        void AddUserMessage(string content, List<ChatContentPart>? multimodalContent);
        void AddAssistantMessage(string? content, string? reasoningContent = null, List<ToolCall>? toolCalls = null);
        /// <summary>添加纯文本工具调用结果。</summary>
        void AddToolResult(string toolCallId, string toolName, string result);

        /// <summary>添加工具调用结果；视觉工具可同时保存文本与图片等内容块。</summary>
        void AddToolResult(string toolCallId, string toolName, string result, List<ChatContentPart>? multimodalContent);
        void AddCustomMessage(string role, string content, int turnIndex = -1);

        // ── 上下文获取 ──
        List<ChatApiMessage> BuildApiMessages();
        List<ChatApiMessage> BuildContextPrefix();
        List<ChatApiMessage> BuildApiMessagesRecentTurns(int maxTurns);
        string GetDebugSummary();

        /// <summary>
        /// 将「本次实际发出的消息列表」与内部历史对账，把主流程脱节消息补写进 <c>_entries</c>。
        /// 保证主流程发出过的内容不缺失；一次性子任务（记忆/润色等）不得调用本方法。
        /// </summary>
        /// <param name="sentMessages">实际发送给 API 的消息列表（清洗后的最终版本）。</param>
        /// <returns>补写的条目数。</returns>
        int SyncSentMessagesToHistory(IReadOnlyList<ChatApiMessage> sentMessages);

        /// <summary>
        /// 判断当前轮次是否已存在内容相同的 volatile 快照（供请求构建器去重，避免同轮重复注入）。
        /// </summary>
        bool HasVolatileSnapshotInCurrentTurn(string? volatileBlock);

        /// <summary>
        /// 构建易变上下文块（Working Set + IDE 实时态）。
        /// 这些内容在同一会话内可能每轮都变，应放在消息数组末尾以保护前缀缓存。
        /// 与 BuildDynamicContextBlock() 分离：后者包含稳定的压缩摘要与记忆，
        /// 始终占据 messages[2] 固定位置。
        /// </summary>
        string? BuildVolatileContextBlock();
        bool PersistCurrentVolatileSnapshot();

        // ── Token 估算校准 ──
        /// <summary>
        /// 使用 API 返回的实际 prompt_tokens 校准本地字符级估算。
        /// 应在每次 Chat API 调用完成后调用，以逐步修正估算偏差。
        /// </summary>
        /// <param name="actualPromptTokens">API usage 中的实际 prompt_tokens</param>
        void CalibrateFromApiUsage(long actualPromptTokens);

        // ── 工具方法 ──
        void Clear();
    }
}
