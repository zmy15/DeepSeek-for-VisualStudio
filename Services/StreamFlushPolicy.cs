namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 一次流式刷新的决策结果：是否刷新，以及本次刷新需要携带哪些快照。
    /// 由 <see cref="StreamFlushPolicy.Decide"/> 产出，供 UI 层直接消费。
    /// </summary>
    internal readonly struct StreamFlushPlan
    {
        public StreamFlushPlan(
            bool flush,
            bool includeContentSnapshot,
            bool includeContentDelta,
            bool includeReasoningSnapshot,
            bool includeReasoningDelta)
        {
            Flush = flush;
            IncludeContentSnapshot = includeContentSnapshot;
            IncludeContentDelta = includeContentDelta;
            IncludeReasoningSnapshot = includeReasoningSnapshot;
            IncludeReasoningDelta = includeReasoningDelta;
        }

        /// <summary>本次是否真的需要推送一帧。</summary>
        public bool Flush { get; }

        /// <summary>是否携带整段正文快照（<c>c</c>）。</summary>
        public bool IncludeContentSnapshot { get; }

        /// <summary>是否携带正文增量（<c>cd</c>）。仅在整段正文为空时使用。</summary>
        public bool IncludeContentDelta { get; }

        /// <summary>是否携带整段思考快照（<c>r</c>）。</summary>
        public bool IncludeReasoningSnapshot { get; }

        /// <summary>是否携带思考增量（<c>rd</c>）。</summary>
        public bool IncludeReasoningDelta { get; }
    }

    /// <summary>
    /// 流式批次刷新策略（从 UI 批处理中拆出，便于单测）。
    ///
    /// <para>
    /// 旧实现只要「整段正文非空」且时间窗到达，就把整段正文随包重发。于是纯思考增量
    /// 也会触发一次 O(n) 的 <c>ToString</c> + 跨进程传输 + JS 侧整块 <c>textContent</c>
    /// 重排版；而工具轮次的时间线会随轮次线性增长，长会话里表现为「思考越流越卡」。
    /// </para>
    ///
    /// <para>
    /// 这里把「何时刷新」与「刷新携带什么」分开：
    /// </para>
    /// <list type="bullet">
    /// <item>正文快照仅在自上次推送后确实变化过（<c>contentDirty</c>）时才携带，
    ///       思考增量不再连坐重发整段正文；</item>
    /// <item>思考是纯追加的小文本，用更短的时间窗（<see cref="ReasoningIntervalMs"/>）
    ///       刷新，观感更连续；</item>
    /// <item>「有待推送」的判定不再把「正文非空」当成变化，没有实际变化时不产生空刷新。</item>
    /// </list>
    /// </summary>
    internal static class StreamFlushPolicy
    {
        /// <summary>正文刷新的时间下限。</summary>
        public const int ContentIntervalMs = 60;

        /// <summary>思考刷新的时间下限：纯追加小文本，可以比正文更密。</summary>
        public const int ReasoningIntervalMs = 30;

        /// <summary>正文增长达到该字符数时立即刷新，不等时间窗。</summary>
        public const int ContentGrowthThreshold = 100;

        /// <summary>思考增量达到该字符数时立即刷新。</summary>
        public const int ReasoningGrowthThreshold = 50;

        /// <summary>
        /// 决定本帧是否刷新，以及刷新时各通道是否需要携带快照。
        /// </summary>
        /// <param name="isComplete">本条消息是否已收尾（收尾必须刷新）。</param>
        /// <param name="contentDirty">整段正文自上次推送后是否变化过（含整体替换）。</param>
        /// <param name="contentLength">整段正文当前长度。</param>
        /// <param name="lastFlushedContentLength">上次推送时的整段正文长度。</param>
        /// <param name="contentDeltaLength">待推送的正文增量长度（整段正文为空时使用）。</param>
        /// <param name="reasoningLength">整段思考当前长度（流式期间通常为 0，仅收尾时非空）。</param>
        /// <param name="lastFlushedReasoningLength">上次推送时的整段思考长度。</param>
        /// <param name="reasoningDeltaLength">待推送的思考增量长度。</param>
        /// <param name="elapsedMs">距上次推送的毫秒数。</param>
        public static StreamFlushPlan Decide(
            bool isComplete,
            bool contentDirty,
            int contentLength,
            int lastFlushedContentLength,
            int contentDeltaLength,
            int reasoningLength,
            int lastFlushedReasoningLength,
            int reasoningDeltaLength,
            long elapsedMs)
        {
            bool contentGrew = StreamBatchContentRules.GrewEnough(
                contentLength, lastFlushedContentLength, ContentGrowthThreshold);
            // 纯问答的 cd 增量也按同一阈值立即推送，保留旧实现「攒够 100 字符即刷新」的行为。
            bool contentDeltaGrew = contentDeltaLength >= ContentGrowthThreshold;
            bool reasoningGrew = reasoningDeltaLength >= ReasoningGrowthThreshold;
            bool reasoningSnapshotDirty = reasoningLength > 0
                && reasoningLength != lastFlushedReasoningLength;

            bool pendingContent = contentDirty || contentDeltaLength > 0;
            bool pendingReasoning = reasoningDeltaLength > 0 || reasoningSnapshotDirty;

            bool flush = isComplete
                || contentGrew
                || contentDeltaGrew
                || reasoningGrew
                || (elapsedMs >= ContentIntervalMs && (pendingContent || pendingReasoning))
                || (elapsedMs >= ReasoningIntervalMs && pendingReasoning);

            if (!flush)
                return default;

            // 正文快照只在确实变化过时携带：这是「思考不再重发整段正文」的关键。
            bool includeContentSnapshot = contentDirty;
            bool includeContentDelta = !includeContentSnapshot && contentDeltaLength > 0;
            // 整段思考快照优先于增量：收尾时用它覆盖可能被合并帧丢掉的部分。
            bool includeReasoningSnapshot = reasoningSnapshotDirty;
            bool includeReasoningDelta = !includeReasoningSnapshot && reasoningDeltaLength > 0;

            return new StreamFlushPlan(
                flush: true,
                includeContentSnapshot: includeContentSnapshot,
                includeContentDelta: includeContentDelta,
                includeReasoningSnapshot: includeReasoningSnapshot,
                includeReasoningDelta: includeReasoningDelta);
        }
    }
}
