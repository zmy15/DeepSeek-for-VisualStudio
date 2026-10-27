using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 流式刷新策略：锁住「思考增量不再重发整段正文」与「思考 30ms / 正文 60ms」两条规则，
/// 防止长会话里思考越流越卡、以及空闲时产生空刷新。
/// </summary>
public class StreamFlushPolicyTests
{
    private static StreamFlushPlan Decide(
        bool isComplete = false,
        bool contentDirty = false,
        int contentLength = 0,
        int lastFlushedContentLength = 0,
        int contentDeltaLength = 0,
        int reasoningLength = 0,
        int lastFlushedReasoningLength = 0,
        int reasoningDeltaLength = 0,
        long elapsedMs = 0)
        => StreamFlushPolicy.Decide(
            isComplete,
            contentDirty,
            contentLength,
            lastFlushedContentLength,
            contentDeltaLength,
            reasoningLength,
            lastFlushedReasoningLength,
            reasoningDeltaLength,
            elapsedMs);

    [Fact]
    public void ReasoningOnly_AfterLongTimeline_DoesNotResendContentSnapshot()
    {
        // 回归：工具轮次把时间线当整段正文推过之后，纯思考增量曾会连带重发整段正文，
        // 导致每次刷新都做一次 O(n) 序列化 + JS 整块文本重排版。
        var plan = Decide(
            contentDirty: false,
            contentLength: 50_000,
            lastFlushedContentLength: 50_000,
            reasoningDeltaLength: 12,
            elapsedMs: StreamFlushPolicy.ReasoningIntervalMs);

        plan.Flush.Should().BeTrue();
        plan.IncludeContentSnapshot.Should().BeFalse();
        plan.IncludeContentDelta.Should().BeFalse();
        plan.IncludeReasoningDelta.Should().BeTrue();
        plan.IncludeReasoningSnapshot.Should().BeFalse();
    }

    [Fact]
    public void DirtyContent_IsCarriedWithTheFrame()
    {
        // 时间线新增一行 / 正文增量并入整段正文后，本帧必须带 c，否则文本会延迟出现。
        var plan = Decide(
            contentDirty: true,
            contentLength: 120,
            lastFlushedContentLength: 100,
            reasoningDeltaLength: 12,
            elapsedMs: StreamFlushPolicy.ContentIntervalMs);

        plan.Flush.Should().BeTrue();
        plan.IncludeContentSnapshot.Should().BeTrue();
        plan.IncludeContentDelta.Should().BeFalse();
        plan.IncludeReasoningDelta.Should().BeTrue();
    }

    [Fact]
    public void NoChange_DoesNotProduceEmptyRefresh()
    {
        // 旧实现把「正文非空」当成有待推送：每次 60ms 定时器都会空推一帧整段正文。
        var plan = Decide(
            contentLength: 50_000,
            lastFlushedContentLength: 50_000,
            elapsedMs: 10_000);

        plan.Flush.Should().BeFalse();
        plan.IncludeContentSnapshot.Should().BeFalse();
    }

    [Fact]
    public void ReasoningFlushesAt30ms_WhileContentStillWaitsFor60ms()
    {
        // 思考：30ms 即可推送。
        Decide(reasoningDeltaLength: 10, elapsedMs: 30).Flush.Should().BeTrue();

        // 正文：同样时刻（未达到 100 字符增长）不应推送，避免把正文刷新频率翻倍。
        Decide(
            contentDirty: true,
            contentLength: 110,
            lastFlushedContentLength: 100,
            elapsedMs: 30).Flush.Should().BeFalse();

        // 到 60ms 才推送正文。
        Decide(
            contentDirty: true,
            contentLength: 110,
            lastFlushedContentLength: 100,
            elapsedMs: 60).Flush.Should().BeTrue();
    }

    [Fact]
    public void PureChat_WithEmptyBody_UsesContentDeltaPath()
    {
        // 纯问答：整段正文为空，正文只能走 cd 增量；60ms 时间窗到达即推送。
        Decide(contentDeltaLength: 15, elapsedMs: 30).Flush.Should().BeFalse();

        var plan = Decide(contentDeltaLength: 15, elapsedMs: 60);
        plan.Flush.Should().BeTrue();
        plan.IncludeContentDelta.Should().BeTrue();
        plan.IncludeContentSnapshot.Should().BeFalse();
    }

    [Fact]
    public void PureChat_ContentDeltaReachingThreshold_FlushesImmediately()
    {
        // 保留旧行为：cd 增量攒够 100 字符不必等时间窗（高速流下更跟手）。
        var plan = Decide(
            contentDeltaLength: StreamFlushPolicy.ContentGrowthThreshold,
            elapsedMs: 0);

        plan.Flush.Should().BeTrue();
        plan.IncludeContentDelta.Should().BeTrue();
        plan.IncludeContentSnapshot.Should().BeFalse();
    }

    [Theory]
    [InlineData(100)] // 恰好达到阈值
    [InlineData(180)]
    public void ContentGrowthReachingThreshold_FlushesImmediately(int growth)
    {
        var plan = Decide(
            contentDirty: true,
            contentLength: growth,
            lastFlushedContentLength: 0,
            elapsedMs: 0);

        plan.Flush.Should().BeTrue();
        plan.IncludeContentSnapshot.Should().BeTrue();
    }

    [Fact]
    public void ReasoningGrowthReachingThreshold_FlushesImmediately()
    {
        var plan = Decide(
            reasoningDeltaLength: StreamFlushPolicy.ReasoningGrowthThreshold,
            elapsedMs: 0);

        plan.Flush.Should().BeTrue();
        plan.IncludeReasoningDelta.Should().BeTrue();
    }

    [Fact]
    public void Complete_SendsReasoningSnapshotInsteadOfDelta()
    {
        // 收尾必须以整段 r 覆盖：delta 帧可能被 rAF 合并丢弃，快照才能保证终态完整。
        var plan = Decide(
            isComplete: true,
            contentDirty: true,
            contentLength: 100,
            lastFlushedContentLength: 40,
            reasoningLength: 500,
            lastFlushedReasoningLength: 0,
            reasoningDeltaLength: 50,
            elapsedMs: 0);

        plan.Flush.Should().BeTrue();
        plan.IncludeContentSnapshot.Should().BeTrue();
        plan.IncludeReasoningSnapshot.Should().BeTrue();
        plan.IncludeReasoningDelta.Should().BeFalse();
    }

    [Fact]
    public void Complete_WithNothingPending_StillFlushesToClearCursor()
    {
        var plan = Decide(isComplete: true, elapsedMs: 0);

        plan.Flush.Should().BeTrue();
        plan.IncludeContentSnapshot.Should().BeFalse();
        plan.IncludeContentDelta.Should().BeFalse();
        plan.IncludeReasoningSnapshot.Should().BeFalse();
        plan.IncludeReasoningDelta.Should().BeFalse();
    }

    [Fact]
    public void ReplacedWithShorterContent_IsStillIncluded()
    {
        // 时间线重排后正文可能变短：不能靠「增长量」判定，必须看 dirty 标记。
        var plan = Decide(
            contentDirty: true,
            contentLength: 80,
            lastFlushedContentLength: 10_000,
            elapsedMs: StreamFlushPolicy.ContentIntervalMs);

        plan.Flush.Should().BeTrue();
        plan.IncludeContentSnapshot.Should().BeTrue();
    }

    [Fact]
    public void Constants_KeepReasoningWindowShorterThanContent()
    {
        // 思考窗必须短于正文窗，否则 Agent 层 30ms 的聚合没有意义。
        StreamFlushPolicy.ReasoningIntervalMs.Should().BeLessThan(StreamFlushPolicy.ContentIntervalMs);
    }
}
