using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class ReasoningGuardTests
{
    [Fact]
    public void Inspect_RepeatedBlock_DetectsLoop()
    {
        var guard = new ReasoningLoopGuard(
            maxCharacters: 10_000,
            blockCharacters: 32,
            repeatThreshold: 3,
            searchWindowCharacters: 256);
        string block = new string('a', 32);

        guard.Inspect(block).ShouldBreak.Should().BeFalse();
        guard.Inspect(block).ShouldBreak.Should().BeFalse();

        var result = guard.Inspect(block);

        result.Action.Should().Be(ReasoningGuardAction.LoopDetected);
        result.RepetitionCount.Should().BeGreaterOrEqualTo(3);
    }

    [Fact]
    public void Inspect_RepeatedBlockWithWhitespaceAndCase_DetectsLoop()
    {
        var guard = new ReasoningLoopGuard(
            maxCharacters: 10_000,
            blockCharacters: 32,
            repeatThreshold: 3,
            searchWindowCharacters: 256);
        string block = "ABCDEFGHIJKLMNOPQRSTUVWXYZ123456";

        guard.Inspect(block).ShouldBreak.Should().BeFalse();
        guard.Inspect("  " + block.ToLowerInvariant() + "\r\n").ShouldBreak.Should().BeFalse();

        var result = guard.Inspect(block.Insert(8, " "));

        result.Action.Should().Be(ReasoningGuardAction.LoopDetected);
    }

    [Fact]
    public void Inspect_DistinctReasoning_Continues()
    {
        var guard = new ReasoningLoopGuard(
            maxCharacters: 10_000,
            blockCharacters: 16,
            repeatThreshold: 3,
            searchWindowCharacters: 128);

        guard.Inspect("first distinct reasoning block").ShouldBreak.Should().BeFalse();
        guard.Inspect("second completely different thought").ShouldBreak.Should().BeFalse();
        guard.Inspect("third conclusion with another shape").ShouldBreak.Should().BeFalse();
    }

    [Fact]
    public void Inspect_RepeatedBlockAfterRingWrap_DetectsLoop()
    {
        var guard = new ReasoningLoopGuard(
            maxCharacters: 10_000,
            blockCharacters: 8,
            repeatThreshold: 3,
            searchWindowCharacters: 32);

        foreach (char c in "0123456789abcdefghij0123456789abcdefghij")
            guard.Inspect(c.ToString()).ShouldBreak.Should().BeFalse();

        bool detected = false;
        foreach (char c in "abcdefghabcdefghabcdefgh")
        {
            var result = guard.Inspect(c.ToString());
            detected |= result.ShouldBreak;
        }

        detected.Should().BeTrue();
    }

    [Fact]
    public void Inspect_ExceedsCharacterLimit_Stops()
    {
        var guard = new ReasoningLoopGuard(maxCharacters: 10);

        guard.Inspect("12345").Action.Should().Be(ReasoningGuardAction.Continue);
        guard.Inspect("67890").Action.Should().Be(ReasoningGuardAction.LengthLimitExceeded);
    }

    [Fact]
    public void ClampForCompression_LongText_IsBoundedAndOnlyUsedOffWire()
    {
        // Clamp/ClampForCompression 仅服务压缩摘要这类本地用途；
        // 其插入的截断标记绝不能出现在回传给 API 的 reasoning_content 中
        // （见 BuildApiMessages_* 系列测试与 TrimForDisplay 的移除原因）。
        string input = new string('a', 20_000) + "MIDDLE" + new string('z', 20_000);

        string? result = ReasoningTextPolicy.ClampForCompression(input);

        result.Should().NotBeNull();
        result!.Length.Should().BeLessOrEqualTo(ReasoningTextPolicy.CompressionReasoningMaxChars);
        result.Should().Contain("reasoning truncated");
    }

    [Fact]
    public void AddAssistantMessage_LongReasoning_IsStoredVerbatim()
    {
        var manager = new ConversationContextManager();
        manager.AddUserMessage("question");

        string longReasoning = new string('r', 60_000);
        manager.AddAssistantMessage("answer", longReasoning);

        var history = manager.GetConversationHistory();
        var assistant = history.Single(message => message.Role == "assistant");
        assistant.ReasoningContent.Should().NotBeNull();
        // 存储阶段不裁剪：当前轮仍完整回传，历史轮保留供 UI 回看
        assistant.ReasoningContent.Should().Be(longReasoning);
    }

    [Fact]
    public void AddAssistantMessage_UiSummary_DoesNotOverwritePerTurnReasoning()
    {
        var manager = new ConversationContextManager();
        manager.AddUserMessage("execute task");

        manager.AddAssistantMessage(
            "calling tool",
            "tool-round reasoning",
            new List<ToolCall>
            {
                new()
                {
                    Id = "call_1",
                    Type = "function",
                    Function = new ToolCallFunction { Name = "edit_file", Arguments = "{}" }
                }
            });
        manager.AddToolResult("call_1", "edit_file", "ok");
        manager.AddAssistantMessage("API final answer", "final-round reasoning");

        // UI 收尾会把多个工具轮次/Handoff 的思考聚合成一个展示值。
        // 该聚合值不得覆盖 API 最后一轮自己的 reasoning_content。
        manager.AddAssistantMessage("programmatic summary");

        var assistants = manager.BuildApiMessages()
            .Where(m => m.Role == "assistant")
            .ToList();

        assistants.Should().HaveCount(2);
        assistants[0].ReasoningContent.Should().Be("tool-round reasoning");
        assistants[1].ReasoningContent.Should().Be("final-round reasoning");
        assistants.Should().NotContain(m =>
            (m.ReasoningContent ?? string.Empty).Contains("programmatic summary"));
    }

    [Fact]
    public void BuildApiMessages_HistoricalToolCallReasoning_IsOmitted()
    {
        var manager = new ConversationContextManager();
        manager.AddUserMessage("first");
        string longReasoning = new string('r', 40_000);
        manager.AddAssistantMessage(
            "calling tool",
            longReasoning,
            new List<ToolCall>
            {
                new()
                {
                    Id = "call_1",
                    Type = "function",
                    Function = new ToolCallFunction
                    {
                        Name = "read_file",
                        Arguments = "{}"
                    }
                }
            });
        manager.AddToolResult("call_1", "read_file", "ok");
        manager.AddUserMessage("second");

        var messages = manager.BuildApiMessages();

        var historicalAssistant = messages.First(message =>
            message.Role == "assistant"
            && message.ToolCalls != null
            && message.ToolCalls.Count > 0);
        historicalAssistant.ReasoningContent.Should().BeNull(
            "历史轮次的思考只用于本地回看，不再回传以节省 token");
    }

    /// <summary>
    /// 当前轮保留思考，进入下一轮后同一 assistant 的 reasoning_content 不再回传。
    /// </summary>
    [Fact]
    public void BuildApiMessages_OnlyCurrentTurnReasoning_IsReplayed()
    {
        var manager = new ConversationContextManager();
        manager.AddUserMessage("first");
        string reasoning = new string('r', 12_345);
        manager.AddAssistantMessage(
            "calling tool",
            reasoning,
            new List<ToolCall>
            {
                new()
                {
                    Id = "call_1",
                    Type = "function",
                    Function = new ToolCallFunction { Name = "read_file", Arguments = "{}" }
                }
            });

        // 仍是当前轮（TurnIndex == TurnCount）
        var asCurrentTurn = manager.BuildApiMessages()
            .First(m => m.Role == "assistant" && m.ToolCalls != null)
            .ReasoningContent;

        manager.AddToolResult("call_1", "read_file", "ok");
        manager.AddUserMessage("second"); // 开新一轮 → 上一条变历史轮

        var asHistoricalTurn = manager.BuildApiMessages()
            .First(m => m.Role == "assistant" && m.ToolCalls != null)
            .ReasoningContent;

        asCurrentTurn.Should().Be(reasoning);
        asHistoricalTurn.Should().BeNull(
            "新用户轮次开始后，上一轮思考不再进入 API 请求");
    }

    [Fact]
    public void BuildApiMessages_CurrentTurnToolLoop_RetainsEachRoundReasoning()
    {
        var manager = new ConversationContextManager();
        manager.AddUserMessage("current turn");

        manager.AddAssistantMessage(
            "first tool call",
            "first-round reasoning",
            new List<ToolCall>
            {
                new()
                {
                    Id = "call_1",
                    Type = "function",
                    Function = new ToolCallFunction { Name = "read_file", Arguments = "{}" }
                }
            });
        manager.AddToolResult("call_1", "read_file", "ok");
        manager.AddAssistantMessage("final answer", "final-round reasoning");

        var assistants = manager.BuildApiMessages()
            .Where(m => m.Role == "assistant")
            .ToList();

        assistants.Should().HaveCount(2);
        assistants[0].ReasoningContent.Should().Be("first-round reasoning");
        assistants[1].ReasoningContent.Should().Be("final-round reasoning");
    }

    [Fact]
    public void BuildStreamUpdateJson_ReasoningDelta_UsesDeltaField()
    {
        string json = ChatHtmlService.BuildStreamUpdateJson(
            messageIndex: 3,
            streamingContent: "answer",
            reasoningContent: string.Empty,
            isComplete: false,
            reasoningDelta: "new thought");

        json.Should().Contain("\"rd\":\"new thought\"");
        json.Should().NotContain("\"r\":");
    }

    [Fact]
    public void BuildStreamUpdateJson_ContentDelta_UsesDeltaField()
    {
        string json = ChatHtmlService.BuildStreamUpdateJson(
            messageIndex: 3,
            streamingContent: null,
            reasoningContent: string.Empty,
            isComplete: false,
            contentDelta: "next chunk");

        json.Should().Contain("\"cd\":\"next chunk\"");
        json.Should().NotContain("\"c\":");
    }
}
