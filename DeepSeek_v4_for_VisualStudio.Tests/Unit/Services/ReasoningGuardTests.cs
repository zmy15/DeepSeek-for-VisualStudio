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
    public void ClampStored_LongText_ReturnsBoundedHeadAndTail()
    {
        string input = new string('a', 20_000) + "MIDDLE" + new string('z', 20_000);

        string? result = ReasoningTextPolicy.ClampStored(input);

        result.Should().NotBeNull();
        result!.Length.Should().BeLessOrEqualTo(ReasoningTextPolicy.StoredReasoningMaxChars);
        result.Should().Contain("reasoning truncated");
        result.Should().StartWith("a");
        result.Should().EndWith("z");
    }

    [Fact]
    public void AddAssistantMessage_LongReasoning_IsStoredBounded()
    {
        var manager = new ConversationContextManager();
        manager.AddUserMessage("question");

        manager.AddAssistantMessage("answer", new string('r', 60_000));

        var history = manager.GetConversationHistory();
        var assistant = history.Single(message => message.Role == "assistant");
        assistant.ReasoningContent.Should().NotBeNull();
        assistant.ReasoningContent!.Length.Should()
            .BeLessOrEqualTo(ReasoningTextPolicy.StoredReasoningMaxChars);
    }

    [Fact]
    public void BuildApiMessages_HistoricalToolCallReasoning_IsCompacted()
    {
        var manager = new ConversationContextManager();
        manager.AddUserMessage("first");
        manager.AddAssistantMessage(
            "calling tool",
            new string('r', 40_000),
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
        historicalAssistant.ReasoningContent.Should().NotBeNull();
        historicalAssistant.ReasoningContent!.Length.Should()
            .BeLessOrEqualTo(ReasoningTextPolicy.HistoricalToolCallMaxChars);
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
