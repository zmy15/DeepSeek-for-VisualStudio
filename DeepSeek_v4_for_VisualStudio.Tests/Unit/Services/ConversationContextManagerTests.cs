namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class ConversationContextManagerTests
{
    private readonly ConversationContextManager _manager;

    public ConversationContextManagerTests()
    {
        _manager = new ConversationContextManager();
    }

    [Fact]
    public void NewManager_HasEmptyState()
    {
        _manager.IsEmpty.Should().BeTrue();
        _manager.TurnCount.Should().Be(0);
        _manager.MessageCount.Should().Be(0);
        _manager.EstimatedTokens.Should().Be(0);
    }

    [Fact]
    public void AddUserMessage_IncrementsTurnCount()
    {
        _manager.AddUserMessage("Hello");

        _manager.TurnCount.Should().Be(1);
        _manager.MessageCount.Should().Be(1);
        _manager.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void AddUserMessage_IncrementsTokenEstimate()
    {
        _manager.AddUserMessage("Hello, this is a test message");

        _manager.EstimatedTokens.Should().BeGreaterThan(0);
    }

    [Fact]
    public void AddAssistantMessage_WithContent_StoresCorrectly()
    {
        _manager.AddUserMessage("Question");
        _manager.AddAssistantMessage("Answer");

        _manager.MessageCount.Should().Be(2);
    }

    [Fact]
    public void SetSystemPrompt_StoresPrompt()
    {
        _manager.SetSystemPrompt("You are a helpful assistant.");

        // System prompt doesn't increase message count
        _manager.MessageCount.Should().Be(0);
    }

    [Fact]
    public void BuildApiMessages_WithSystemPrompt_IncludesItAsFirstMessage()
    {
        _manager.SetSystemPrompt("You are helpful.");
        _manager.AddUserMessage("Hi");

        var messages = _manager.BuildApiMessages();

        // 稳定系统提示词合并为一条；空动态块不再注入。
        messages.Should().HaveCount(2);
        messages[0].Role.Should().Be("system");
        messages[0].Content.Should().StartWith("You are helpful.");
        messages[0].Content.Should().Contain("You are helpful.");
        // 共享不可变前缀仍须追加在 fixedPrompt 之后；文件读取规则已下沉到 read_file 工具描述。
        messages[0].Content.Should().Contain("PowerShell 语法");
        messages[1].Role.Should().Be("user");
        // 用户文本落库时带「本轮用户需求」前缀（持久化事实）
        messages[1].Content.Should().Be(
            LocalizationService.Instance["system.agent.currentUserQuestionPrefix"] + "Hi");
    }

    [Fact]
    public void SetRagContext_UpdatesTokenCount()
    {
        var initialTokens = _manager.EstimatedTokens;

        _manager.SetRagContext("RAG context: relevant document content here.");

        _manager.EstimatedTokens.Should().BeGreaterThan(initialTokens);
        _manager.RagContext.Should().NotBeNull();
    }

    [Fact]
    public void Clear_ResetsAllState()
    {
        _manager.SetSystemPrompt("prompt");
        _manager.AddUserMessage("hello");
        _manager.AddAssistantMessage("hi");
        _manager.SetRagContext("rag");

        _manager.Clear();

        _manager.IsEmpty.Should().BeTrue();
        _manager.TurnCount.Should().Be(0);
        _manager.MessageCount.Should().Be(0);
        _manager.EstimatedTokens.Should().Be(0);
        _manager.RagContext.Should().BeNull();
    }

    [Fact]
    public void AddToolResult_StoresCorrectly()
    {
        _manager.AddUserMessage("search for something");
        _manager.AddToolResult("call_1", "grep_search", "Found 5 results");

        var messages = _manager.BuildApiMessages();

        messages.Should().Contain(m => m.Role == "tool" && m.ToolCallId == "call_1");
    }

    [Fact]
    public void ClearCacheSnapshot_IncludesMessagesAddedAfterHandoff()
    {
        _manager.AddUserMessage("handoff task");
        _manager.SnapshotForCache();
        _manager.AddAssistantMessage("step 1 completed");

        var frozenMessages = _manager.BuildApiMessages();
        frozenMessages.Should().NotContain(m =>
            m.Role == "assistant" && m.Content == "step 1 completed");

        _manager.ClearCacheSnapshot();

        var messages = _manager.BuildApiMessages();
        messages.Should().Contain(m =>
            m.Role == "assistant" && m.Content == "step 1 completed");
    }

    [Fact]
    public void EstimateMessageTokens_IncludesToolCallArguments()
    {
        var messages = new List<ChatApiMessage>
        {
            new()
            {
                Role = "assistant",
                Content = null,
                ToolCalls = new List<ToolCall>
                {
                    new()
                    {
                        Id = "call_1",
                        Type = "function",
                        Function = new ToolCallFunction
                        {
                            Name = "read_file",
                            Arguments = """{"filePath":"C:\\test.cs","startLine":1}""",
                        },
                    },
                },
            },
        };

        var tokens = ConversationContextManager.EstimateMessageTokens(messages);

        tokens.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TokenBudget_DefaultIs900K()
    {
        _manager.TokenBudget.Should().Be(900_000);
    }

    [Fact]
    public void UsageRatio_CalculatesCorrectly()
    {
        _manager.TokenBudget = 1000;

        // Add enough content to estimate ~100 tokens
        var longText = new string('x', 400);
        _manager.AddUserMessage(longText);

        _manager.UsageRatio.Should().BeGreaterThan(0);
    }

    [Fact]
    public void MultipleTurns_CountsCorrectly()
    {
        _manager.AddUserMessage("Q1");
        _manager.AddAssistantMessage("A1");
        _manager.AddUserMessage("Q2");
        _manager.AddAssistantMessage("A2");
        _manager.AddUserMessage("Q3");

        _manager.TurnCount.Should().Be(3);
        _manager.MessageCount.Should().Be(5);
    }

    [Fact]
    public void SetSearchContext_IsInjectedIntoMessages()
    {
        _manager.SetSearchContext("Search results: ...");
        _manager.AddUserMessage("query");

        var volatileBlock = _manager.BuildVolatileContextBlock();

        volatileBlock.Should().NotBeNull();
        volatileBlock!.Should().Contain("Search results");
    }

    /// <summary>
    /// 回归：用户消息落库即带「本轮用户需求」前缀，中途切换 Agent（Handoff）
    /// 后前缀仍必须存在于请求中——此前前缀只在请求时临时加到副本上，
    /// Handoff 分支不经过该逻辑，导致历史 user 丢失标记。
    /// </summary>
    [Fact]
    public void AddUserMessage_PersistsRequirementPrefix_SurvivesHandoff()
    {
        string prefix = LocalizationService.Instance["system.agent.currentUserQuestionPrefix"];

        _manager.AddUserMessage("加到TODO里");
        _manager.AddAssistantMessage("移交 Edit Agent");
        _manager.AddToolResult("call_1", "request_handoff", "HANDOFF_REQUESTED");

        // 移交后目标 Agent 重建请求：源轮次的 user 仍带前缀
        var messages = _manager.BuildApiMessages();
        messages.Should().Contain(m => m.Role == "user" && m.Content == prefix + "加到TODO里");
    }

    /// <summary>
    /// 重复落库不得叠加前缀（会话恢复 / 重放同一文本时）。
    /// </summary>
    [Fact]
    public void AddUserMessage_SameTextTwice_DoesNotStackPrefix()
    {
        string prefix = LocalizationService.Instance["system.agent.currentUserQuestionPrefix"];

        _manager.AddUserMessage(prefix + "重复文本");

        _manager.GetFullContext()
            .Should().Contain(m => m.Content == prefix + "重复文本");
    }
}
