using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 回归（线上 26/27 号请求）：
///   · `[3]..[15]` 与 `[21]..[33]` 逐字节相同（整个 Ask 阶段被复制一份）
///   · `[16]..[18]` 与 `[34]..[36]` 相同（Edit 交接块同样被复制）
///   · `[38]` 与 `[39]` 相同（身份边界提示相邻重复）
///   · `[19]` 是一个 282 字的动态上下文块，出现在历史中间
///
/// 根因假设：对账的结构性前缀识别依赖"与当前动态块内容完全相等"。
/// 动态块的内容会在请求期间漂移（记忆刷新、语言守卫、压缩摘要、新用户消息落库都会重建它），
/// 一旦漂移，发送列表里的旧动态块就匹配不上当前动态块，于是：
///   1. 该 system 消息被当成"脱节内容"补写进 _entries；
///   2. 补写后游标跳到 _entries 末尾；
///   3. 发送列表里后续的所有历史消息都落在游标之前，全部匹配不到 → 整段历史被复制一遍，
///      紧随其后的身份边界提示也一并被复制。
/// 这解释了上面四条现象为何总是同时出现。
/// </summary>
public class SyncCursorCascadeTests
{
    [Fact]
    public void Sync_WhenDynamicBlockDrifts_DoesNotDuplicateHistory()
    {
        var mgr = new ConversationContextManager();

        // 1) 旧动态块版本（含 MEM-OLD）
        mgr.SetMemoryContext("MEM-OLD");
        mgr.AddUserMessage("q1");
        mgr.AddAssistantMessage("a1");

        // 2) 按当时的动态块构建出的"已发送列表"
        var sent = mgr.BuildApiMessages();
        sent.Count(m => m.Role == "system").Should().Be(1,
            "记忆已迁到易变尾部，稳定前缀只应包含固定 system prompt");

        // 3) 动态块漂移：记忆变化 + 新用户消息落库都会重建 _cachedDynamicBlock
        mgr.SetMemoryContext("MEM-NEW");
        mgr.AddUserMessage("q2");

        // 4) 用"旧动态块"的发送列表对账（真实场景：发送后 _cachedDynamicBlock 已被重建）
        mgr.SyncSentMessagesToHistory(sent);

        // 断言：历史不得被复制，旧动态块也不得被当成历史条目
        var full = mgr.GetFullContext();
        full.Count(m => (m.Content ?? "").Contains("q1")).Should().Be(1, "历史用户消息不得被复制");
        full.Count(m => m.Content == "a1").Should().Be(1, "历史助手消息不得被复制");
        full.Should().NotContain(m => (m.Content ?? "").Contains("MEM-OLD"),
            "动态上下文块由字段重建，不应被写进历史");
    }

    [Fact]
    public void Sync_WhenDynamicBlockDrifts_HandoffHintIsNotDuplicated()
    {
        var mgr = new ConversationContextManager();
        const string hint = ">  **身份边界提示**：上方历史来自前一个 Agent。";

        mgr.SetMemoryContext("MEM-OLD");
        mgr.AddUserMessage("q1");
        mgr.AddAssistantMessage("a1");

        // Handoff 分支：固化身份边界提示 + 组装发送列表
        mgr.AddCustomMessage("system", hint);

        var sent = mgr.BuildApiMessages();
        sent = new List<ChatApiMessage>(sent)
        {
            new() { Role = "user", Content = "[当前任务目标] 你是一个 Edit Agent。" },
            new() { Role = "system", Content = "尾部行为指令" },
        };

        // 动态块漂移后对账
        mgr.SetMemoryContext("MEM-NEW");
        mgr.SyncSentMessagesToHistory(sent);

        mgr.GetFullContext().Count(m => m.Content == hint).Should().Be(1,
            "身份边界提示不得相邻重复（26 号请求 [38][39]）");
        mgr.GetFullContext().Count(m => m.Content == "a1").Should().Be(1);
    }

    [Fact]
    public void Sync_WhenDynamicBlockDrifts_DoesNotLoseMainFlowMessages()
    {
        var mgr = new ConversationContextManager();
        mgr.SetMemoryContext("MEM-OLD");
        mgr.AddUserMessage("q1");
        mgr.AddAssistantMessage("a1");

        var sent = mgr.BuildApiMessages();
        sent = new List<ChatApiMessage>(sent)
        {
            new() { Role = "user", Content = "[当前任务目标] Edit 步骤提示" },
        };

        mgr.SetMemoryContext("MEM-NEW");
        mgr.AddUserMessage("q2");

        mgr.SyncSentMessagesToHistory(sent);

        // 隔离漂移不能以"丢主流程消息"为代价：任务提示仍必须被记录并重放
        var rebuilt = mgr.BuildApiMessages();
        rebuilt.Should().Contain(m => m.Content == "[当前任务目标] Edit 步骤提示",
            "主流程的 Handoff 任务提示不得因隔离前缀漂移而丢失");
    }
}
