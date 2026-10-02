using DeepSeek_v4_for_VisualStudio.Services.Agents;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// AskAgent 单元测试 — 测试 Agent 定义、工具集和纯逻辑方法。
/// </summary>
public class AskAgentTests
{
    private readonly DeepSeekApiService _apiService;

    public AskAgentTests()
    {
        _apiService = new DeepSeekApiService("test-api-key");
    }

    #region Constructor

    [Fact]
    public void Constructor_WithApiService_CreatesSuccessfully()
    {
        var agent = new AskAgent(_apiService);

        agent.Should().NotBeNull();
        agent.Definition.Should().NotBeNull();
        agent.Definition.Type.Should().Be(AgentType.Ask);
    }

    [Fact]
    public void Constructor_WithNullApiService_ThrowsArgumentNullException()
    {
        Action act = () => new AskAgent(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Agent Definition

    [Fact]
    public void Definition_Name_IsAsk()
    {
        var agent = new AskAgent(_apiService);

        agent.Definition.Name.Should().Be("Ask");
    }

    [Fact]
    public void Definition_SystemPrompt_IsNotEmpty()
    {
        var agent = new AskAgent(_apiService);

        agent.Definition.SystemPrompt.Should().NotBeNullOrEmpty();
        agent.Definition.SystemPrompt.Should().Contain("Ask");
        // 只读边界与 Git/终端能力说明已下沉到各工具的 description，
        // Agent 专属提示词只保留角色定位与移交规则（见 tool.git.desc / tool.run_in_terminal.desc）。
        agent.Definition.SystemPrompt.Should().Contain("VisualStudio_askQuestions");
        agent.Definition.SystemPrompt.Should().Contain("必须直接调用");
        agent.Definition.SystemPrompt.Should().Contain(
            global::DeepSeek_v4_for_VisualStudio.Services.AiPrompts.AgentConclusionStopRule);
        agent.Definition.SystemPrompt.Should().Contain("直接复用上述结论");
    }

    [Fact]
    public void SummaryPrompts_AllowFreeMarkdownOutput()
    {
        var handoffPrompt = global::DeepSeek_v4_for_VisualStudio.Services.LocalizationService.Instance["agent.edit.handoffAskPrompt"];
        var polishPrompt = global::DeepSeek_v4_for_VisualStudio.Services.AiPrompts.SummaryPolishSystemPrompt;
        var summaryOnlyPrompt = global::DeepSeek_v4_for_VisualStudio.Services.LocalizationService.Instance["agent.summaryOnlySystemPrompt"];

        handoffPrompt.Should().Contain("Markdown");
        handoffPrompt.Should().Contain("Mermaid");
        handoffPrompt.Should().Contain("LaTeX");
        handoffPrompt.Should().Contain("不要求固定结构");

        polishPrompt.Should().Contain("Markdown");
        polishPrompt.Should().Contain("Mermaid");
        polishPrompt.Should().Contain("LaTeX");
        polishPrompt.Should().Contain("不要求固定结构");

        summaryOnlyPrompt.Should().Contain("禁止调用任何工具");
        summaryOnlyPrompt.Should().Contain("DSML");
        summaryOnlyPrompt.Should().Contain("系统如检测到误调用会返回工具结果并继续");
    }

    [Fact]
    public void StripToolCallMarkers_RemovesFullWidthDsmlToolCall()
    {
        const string prefix = "最终总结：构建成功，1 个项目通过。";
        const string bars = "\uFF5C\uFF5C";
        string input = prefix
            + $"\n\n<{bars}DSML{bars} calls>"
            + $"\n<{bars}DSML{bars} invoke name=\"read_file\">"
            + $"\n<{bars}DSML{bars} parameter name=\"path\" string=\"true\">"
            + @"F:\VSCode\DeepSeek_v4_for_VisualStudio\DeepSeek_v4_for_VisualStudio.slnx"
            + $"</{bars}DSML{bars} parameter>"
            + $"\n</{bars}DSML{bars} invoke>"
            + $"\n</{bars}DSML{bars} calls>";

        string result = StripToolCallMarkersPublic(input);

        result.Should().Be(prefix);
    }

    [Fact]
    public void Definition_AllowedTools_ContainsDelegationAndUtilityTools()
    {
        var agent = new AskAgent(_apiService);

        // Ask agent can delegate via runSubagent and handoff to other agents
        agent.Definition.AllowedTools.Should().Contain("runSubagent");
        agent.Definition.AllowedTools.Should().Contain("request_handoff");
        agent.Definition.AllowedTools.Should().Contain("fetch_webpage");
        agent.Definition.AllowedTools.Should().Contain("memory");
        agent.Definition.AllowedTools.Should().Contain("git");
        agent.Definition.AllowedTools.Should().Contain("run_in_terminal");
        agent.Definition.AllowedTools.Should().Contain("get_terminal_output");
        // Ask agent has built-in search/read tools for self-service code lookup
        agent.Definition.AllowedTools.Should().Contain("symbol_search");
        agent.Definition.AllowedTools.Should().Contain("get_file_symbols");
        agent.Definition.AllowedTools.Should().Contain("file_search");
        agent.Definition.AllowedTools.Should().Contain("grep_search");
        agent.Definition.AllowedTools.Should().Contain("read_file");
        agent.Definition.AllowedTools.Should().Contain("list_dir");
        agent.Definition.AllowedTools.Should().Contain("get_errors");
    }

    [Fact]
    public void Definition_AllowedTools_DoesNotContainModifyTools()
    {
        var agent = new AskAgent(_apiService);

        agent.Definition.AllowedTools.Should().NotContain("replace_string_in_file");
        agent.Definition.AllowedTools.Should().NotContain("create_file");
        agent.Definition.AllowedTools.Should().NotContain("delete_file");
    }

    #endregion

    #region AskTools Static Array

    [Fact]
    public void AskTools_ContainsDelegationAndUtilityTools()
    {
        AskAgent.AskTools.Should().Contain("runSubagent");
        AskAgent.AskTools.Should().Contain("request_handoff");
        AskAgent.AskTools.Should().Contain("fetch_webpage");
        AskAgent.AskTools.Should().Contain("capture_window");
        AskAgent.AskTools.Should().Contain("capture_webpage");
        AskAgent.AskTools.Should().Contain("memory");
        AskAgent.AskTools.Should().Contain("git");
        AskAgent.AskTools.Should().Contain("run_in_terminal");
        AskAgent.AskTools.Should().Contain("get_terminal_output");
        // Ask agent has built-in search/read tools
        AskAgent.AskTools.Should().Contain("symbol_search");
        AskAgent.AskTools.Should().Contain("get_file_symbols");
        AskAgent.AskTools.Should().Contain("file_search");
        AskAgent.AskTools.Should().Contain("grep_search");
        AskAgent.AskTools.Should().Contain("read_file");
        AskAgent.AskTools.Should().Contain("list_dir");
        AskAgent.AskTools.Should().Contain("get_errors");
    }

    [Fact]
    public void AskTools_DoesNotContainModifyTools()
    {
        AskAgent.AskTools.Should().NotContain("replace_string_in_file");
        AskAgent.AskTools.Should().NotContain("create_file");
        AskAgent.AskTools.Should().NotContain("create_directory");
        AskAgent.AskTools.Should().NotContain("delete_file");
        AskAgent.AskTools.Should().NotContain("apply_patch");
    }

    #endregion

    #region BuildContextualPrompt

    [Fact]
    public void BuildContextAwareMessages_UsesSessionCurrentUser_AsStandardTurn()
    {
        var contextManager = new ConversationContextManager();
        contextManager.AddUserMessage("你好");

        var context = new AgentContext
        {
            ContextManager = contextManager,
            CurrentUserContent = "你好",
        };
        var agent = new AskAgent(_apiService)
        {
            Context = context,
        };

        var method = typeof(BaseAgent).GetMethod(
            "BuildContextAwareMessages",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(string), typeof(string), typeof(int), typeof(bool) },
            modifiers: null);
        method.Should().NotBeNull();

        var messages = (List<ChatApiMessage>)method!.Invoke(
            agent,
            new object[] { "AskAgent system prompt", string.Empty, int.MaxValue, true })!;

        messages.Count(m => m.Role == "user").Should().Be(1);
        string prefix = DeepSeek_v4_for_VisualStudio.Services.LocalizationService.Instance[
            "system.agent.currentUserQuestionPrefix"];
        messages.Last(m => m.Role == "user").Content.Should().Be(prefix + "你好");
        messages.Last().Role.Should().Be("system");
        messages.Last().Content.Should().Be("AskAgent system prompt");
        // 文件读取规则已从共享前缀下沉到 read_file 工具描述；
        // 共享前缀仍必须作为稳定 messages[0] 注入，并保留 Windows 终端红线。
        messages.Count(m => m.Role == "system" && m.Content!.Contains("PowerShell 语法"))
            .Should().Be(1);
    }

    [Fact]
    public void BuildContextAwareMessages_MaxTurnsZero_PreservesStableContextPrefix()
    {
        var contextManager = new ConversationContextManager();
        contextManager.SetSystemPrompt("Custom system prefix");
        contextManager.SetMemoryContext("Repository memory");
        contextManager.FreezeSystemPrompt();
        contextManager.AddUserMessage("你好");

        var context = new AgentContext
        {
            ContextManager = contextManager,
            CurrentUserContent = "你好",
        };
        var agent = new AskAgent(_apiService)
        {
            Context = context,
        };

        var method = typeof(BaseAgent).GetMethod(
            "BuildContextAwareMessages",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(string), typeof(string), typeof(int), typeof(bool) },
            modifiers: null);
        method.Should().NotBeNull();

        var messages = (List<ChatApiMessage>)method!.Invoke(
            agent,
            new object[] { "Design system prompt", "design user prompt", 0, false })!;
        var expectedPrefix = contextManager.BuildContextPrefix();

        messages.Should().HaveCountGreaterThanOrEqualTo(3);
        messages[0].Content.Should().Be(expectedPrefix[0].Content);
        messages[0].Content.Should().Contain("Custom system prefix");
        messages[1].Content.Should().Be(expectedPrefix[1].Content);
        messages[1].Content.Should().Contain("Repository memory");
    }

    [Fact]
    public void BuildContextAwareMessages_ExplicitRoute_AppendsOverrideAsLastSystemMessage()
    {
        var context = new AgentContext
        {
            IsExplicitRoute = true,
            ExplicitRouteTarget = AgentType.Ask,
        };
        var agent = new AskAgent(_apiService)
        {
            Context = context,
        };

        var method = typeof(BaseAgent).GetMethod(
            "BuildContextAwareMessages",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(string), typeof(string), typeof(int), typeof(bool) },
            modifiers: null);

        var messages = (List<ChatApiMessage>)method!.Invoke(
            agent,
            new object[] { "AskAgent system prompt", "user prompt", int.MaxValue, false })!;

        messages.Last().Role.Should().Be("system");
        messages.Last().Content.Should().Contain("@Ask");
        messages.Last().Content.Should().Contain("重新分类");
    }

    [Fact]
    public void BuildContextAwareMessages_ExplicitRouteForDifferentAgent_DoesNotAppendOverride()
    {
        var context = new AgentContext
        {
            IsExplicitRoute = true,
            ExplicitRouteTarget = AgentType.Edit,
        };
        var agent = new AskAgent(_apiService)
        {
            Context = context,
        };

        var method = typeof(BaseAgent).GetMethod(
            "BuildContextAwareMessages",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(string), typeof(string), typeof(int), typeof(bool) },
            modifiers: null);

        var messages = (List<ChatApiMessage>)method!.Invoke(
            agent,
            new object[] { "AskAgent system prompt", "user prompt", int.MaxValue, false })!;

        messages.Last().Role.Should().Be("system");
        messages.Last().Content.Should().Be("AskAgent system prompt");
    }

    [Fact]
    public void BuildContextAwareMessages_HandoffPrefix_PlacesBoundaryToolsAndUserCorrectly()
    {
        var contextManager = new ConversationContextManager();
        contextManager.SetIdeContext("[IDE Context] Active File: Test.cs");

        var context = new AgentContext
        {
            ContextManager = contextManager,
            // 真移交：允许复用移交前缀（走 Handoff 消息复用分支）
            AllowForwardedMessageReuse = true,
            ForwardedMessages = new List<ChatApiMessage>
            {
                new() { Role = "system", Content = "stable system" },
                new() { Role = "assistant", Content = "explore" },
                new() { Role = "tool", Content = "result" },
            },
        };
        var agent = new AskAgent(_apiService)
        {
            Context = context,
        };

        var method = typeof(BaseAgent).GetMethod(
            "BuildContextAwareMessages",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(string), typeof(string), typeof(int), typeof(bool) },
            modifiers: null);
        method.Should().NotBeNull();

        var messages = (List<ChatApiMessage>)method!.Invoke(
            agent,
            new object[] { "Edit agent prompt", "handoff user", int.MaxValue, false })!;

        // Handoff 分支不再注入易变块，故尾部结构紧凑：[边界提示][user][Agent 提示词]
        context.ToolHistoryInsertIndex.Should().Be(5);
        messages[3].Role.Should().Be("system");
        messages[3].Content.Should().NotBeNullOrWhiteSpace();
        messages[4].Role.Should().Be("user");
        messages[4].Content.Should().Be("handoff user");
        messages[5].Role.Should().Be("system");
        messages[5].Content.Should().Be("Edit agent prompt");

        // 易变上下文块（IDE Context）不得在 Handoff 分支重复注入
        messages.Should().NotContain(m => m.Content != null && m.Content.Contains("[IDE Context]"));
    }

    [Fact]
    public void BuildContextAwareMessages_HandoffPrefix_PreservesVolatileAndUserBeforeBoundary()
    {
        var contextManager = new ConversationContextManager();
        contextManager.SetIdeContext("[IDE Context] Active File: Test.cs");

        var context = new AgentContext
        {
            ContextManager = contextManager,
            // 真移交：允许复用移交前缀（走 Handoff 消息复用分支）
            AllowForwardedMessageReuse = true,
            // 回归场景：快照末尾为 [上下文块(system), 用户提问(user)]。
            // 修复前该组合会被误判为"旧结构 [agent] + [user]"一并删除，导致用户提问丢失。
            ForwardedMessages = new List<ChatApiMessage>
            {
                new() { Role = "system", Content = "stable system" },
                new() { Role = "system", Content = "volatile 上下文块" },
                new() { Role = "user", Content = "原始用户提问" },
            },
        };
        var agent = new AskAgent(_apiService)
        {
            Context = context,
        };

        var method = typeof(BaseAgent).GetMethod(
            "BuildContextAwareMessages",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(string), typeof(string), typeof(int), typeof(bool) },
            modifiers: null);
        method.Should().NotBeNull();

        var messages = (List<ChatApiMessage>)method!.Invoke(
            agent,
            new object[] { "Edit agent prompt", "handoff user", int.MaxValue, false })!;

        // 快照前缀原样保留：volatile 上下文块与用户提问均未被删除
        messages[0].Role.Should().Be("system");
        messages[0].Content.Should().Be("stable system");
        messages[1].Role.Should().Be("system");
        messages[1].Content.Should().Be("volatile 上下文块");
        messages[2].Role.Should().Be("user");
        messages[2].Content.Should().Be("原始用户提问");

        // 身份边界提示紧随用户提问之后（[3]），而非直接跟在主 system 之后
        messages[3].Role.Should().Be("system");
        messages[3].Content.Should().NotBeNullOrWhiteSpace();

        // Handoff 分支不再重复注入 volatile 块，改为紧接 [新任务 user][Edit 提示词]
        messages[4].Role.Should().Be("user");
        messages[4].Content.Should().Be("handoff user");
        messages[5].Role.Should().Be("system");
        messages[5].Content.Should().Be("Edit agent prompt");

        // 易变上下文块只应保留快照中那一份（[1]），不得在此处再次注入
        messages.Count(m => m.Content != null && m.Content.Contains("[IDE Context]"))
            .Should().Be(0, "Handoff 分支不得重复注入 IDE Context");

        context.ToolHistoryInsertIndex.Should().Be(5);
    }

    /// <summary>
    /// 回归：@agent 显式路由等不经 AskAgent 的路径，其 BuildContextAwareMessages 调用
    /// 不传 persistVolatileToHistory（默认 false）。修复前易变上下文块只临时进入本次请求，
    /// 不落 _entries，重启后 IDE Context 丢失。现在内部默认尝试固化，修复该缺口。
    /// </summary>
    [Fact]
    public void BuildContextAwareMessages_NonHandoff_PersistsVolatileToEntriesByDefault()
    {
        var contextManager = new ConversationContextManager();
        contextManager.SetIdeContext("[IDE Context] Active File: Test.cs");
        contextManager.AddUserMessage("@Edit 修复这个 bug");

        var context = new AgentContext
        {
            ContextManager = contextManager,
        };
        var agent = new AskAgent(_apiService)
        {
            Context = context,
        };

        var method = typeof(BaseAgent).GetMethod(
            "BuildContextAwareMessages",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(string), typeof(string), typeof(int), typeof(bool) },
            modifiers: null);
        method.Should().NotBeNull();

        // 默认重载：persistVolatileToHistory 隐式为 false（模拟 @agent / 子 Agent 调用）
        method!.Invoke(agent, new object[] { "Edit agent prompt", "@Edit 修复这个 bug", int.MaxValue, false });

        // 易变块应已固化进 _entries → 可随会话持久化
        contextManager.GetFullContext()
            .Should().Contain(m => m.Role == "system" && m.Content!.Contains("[IDE Context]"),
                "非 Handoff 且未显式要求固化时，也应变易变块写入 _entries");
    }

    /// <summary>
    /// 回归：同一轮内连续多次构建（主对话 → @agent 切换 → Handoff）不得产生多份 IDE Context。
    /// </summary>
    [Fact]
    public void BuildContextAwareMessages_RepeatedCallsInSameTurn_PersistOnlyOneSnapshot()
    {
        var contextManager = new ConversationContextManager();
        contextManager.SetIdeContext("[IDE Context] Active File: Test.cs");
        contextManager.AddUserMessage("修复这个 bug");

        var context = new AgentContext { ContextManager = contextManager };
        var agent = new AskAgent(_apiService) { Context = context };

        var method = typeof(BaseAgent).GetMethod(
            "BuildContextAwareMessages",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(string), typeof(string), typeof(int), typeof(bool) },
            modifiers: null);
        method.Should().NotBeNull();

        for (int i = 0; i < 3; i++)
            method!.Invoke(agent, new object[] { "prompt", "user", int.MaxValue, false });

        contextManager.GetFullContext()
            .Count(m => m.Content != null && m.Content.Contains("[IDE Context]"))
            .Should().Be(1, "同一轮内多次构建应受幂等保护，只保留一份 IDE Context");
    }

    /// <summary>
    /// 回归：身份边界提示必须固化进 _entries（可持久化），而不只是存在于「发射即焚」的
    /// 本次请求消息列表里。修复前 BuildContextAwareMessages 只在局部 result 中添加边界提示，
    /// 从不回写 ContextManager，导致 GetFullContext()/ApiHistory 都导不出它；重启或切换会话后，
    /// 历史中仍留有来源 Agent 身份声明却没有边界提示中和，模型可能沿用旧身份。
    /// </summary>
    [Fact]
    public void BuildContextAwareMessages_HandoffPrefix_PersistsBoundaryPromptToContextManager()
    {
        var contextManager = new ConversationContextManager();

        var context = new AgentContext
        {
            ContextManager = contextManager,
            AllowForwardedMessageReuse = true,
            ForwardedMessages = new List<ChatApiMessage>
            {
                new() { Role = "system", Content = "stable system" },
                new() { Role = "assistant", Content = "explore" },
                new() { Role = "tool", Content = "result" },
            },
        };
        var agent = new AskAgent(_apiService)
        {
            Context = context,
        };

        var method = typeof(BaseAgent).GetMethod(
            "BuildContextAwareMessages",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(string), typeof(string), typeof(int), typeof(bool) },
            modifiers: null);
        method.Should().NotBeNull();

        var messages = (List<ChatApiMessage>)method!.Invoke(
            agent,
            new object[] { "Edit agent prompt", "handoff user", int.MaxValue, false })!;

        string boundaryPrompt = messages[3].Content!;

        // 持久化路径：GetFullContext() 从 _entries 导出，模拟 ApiHistory 落盘
        var persisted = contextManager.GetFullContext();
        persisted.Should().ContainSingle(
            m => m.Role == "system" && m.Content == boundaryPrompt,
            "身份边界提示必须进入 _entries，才能随会话持久化并在重启后生效");

        // 往返验证：序列化再恢复后边界提示仍在（模拟重启 / 会话切换）
        var restored = new ConversationContextManager();
        restored.RestoreFullContext(persisted);
        restored.GetFullContext().Should().ContainSingle(
            m => m.Role == "system" && m.Content == boundaryPrompt,
            "重启恢复后身份边界提示必须依然存在");
    }

    [Fact]
    public void SnapshotHandoffCacheMessages_PreservesCompletedToolHistory()
    {
        var sentMessages = new List<ChatApiMessage>
        {
            new() { Role = "system", Content = "stable system" },
            new() { Role = "user", Content = "inspect the project" },
            new()
            {
                Role = "assistant",
                ToolCalls = new List<ToolCall>
                {
                    new()
                    {
                        Id = "call_1",
                        Type = "function",
                        Function = new ToolCallFunction { Name = "read_file", Arguments = "{}" },
                    },
                },
            },
            new() { Role = "tool", ToolCallId = "call_1", Name = "read_file", Content = "file content" },
            new() { Role = "system", Content = "source agent prompt" },
        };

        typeof(DeepSeek_v4_for_VisualStudio.Services.Providers.OpenAiCompatibleProvider)
            .GetField("<LastSentMessages>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(_apiService, sentMessages);

        var agent = new AskAgent(_apiService);
        var snapshot = agent.SnapshotHandoffCacheMessages();

        snapshot.Should().NotBeNull();
        snapshot.Should().HaveCount(4);
        snapshot.Should().Contain(m => m.Role == "tool" && m.ToolCallId == "call_1");
        snapshot.Should().NotContain(m => m.Content == "source agent prompt");
    }

    [Fact]
    public void BuildContextualPrompt_WithFileContext_IncludesItWithoutQuestionWrapper()
    {
        var context = new AgentContext
        {
            FileContext = "File content context",
        };

        var result = BuildContextualPromptPublic("帮我分析项目结构", context);

        result.Should().Contain("File content context");
        result.Should().Contain("帮我分析项目结构");
        result.Should().NotContain("[用户问题]");
    }

    [Fact]
    public void BuildContextualPrompt_WithoutFileContext_ExcludesSolutionMetadata()
    {
        var context = new AgentContext
        {
            FileContext = null,
        };

        var result = BuildContextualPromptPublic("帮我分析项目结构", context);

        result.Should().NotContain("当前解决方案");
        result.Should().NotContain("[用户问题]");
    }

    [Fact]
    public void BuildContextualPrompt_AlwaysIncludesUserMessage()
    {
        var context = new AgentContext();

        var result = BuildContextualPromptPublic("我的问题是这个", context);

        result.Should().Contain("我的问题是这个");
    }

    #endregion

    #region ParseCodeChangesFromResult (inherited from BaseAgent)

    [Fact]
    public void ParseCodeChangesFromResult_NullOrEmpty_ReturnsEmpty()
    {
        var result1 = ParseCodeChangesPublic(null!);
        var result2 = ParseCodeChangesPublic("");
        var result3 = ParseCodeChangesPublic("   ");

        result1.Should().BeEmpty();
        result2.Should().BeEmpty();
        result3.Should().BeEmpty();
    }

    [Fact]
    public void ParseCodeChangesFromResult_FileFormat_ParsesPathAndContent()
    {
        var input = @"```file: src/app.ts
export const App = () => <div>Hello</div>;
```";

        var changes = ParseCodeChangesPublic(input);

        changes.Should().HaveCount(1);
        changes[0].FilePath.Should().Be("src/app.ts");
        changes[0].NewContent.Should().Contain("Hello");
    }

    [Fact]
    public void ParseCodeChangesFromResult_MultipleFiles_ParsesAll()
    {
        var input = @"```file: src/a.ts
content a
```
```file: src/b.ts
content b
```";

        var changes = ParseCodeChangesPublic(input);

        changes.Should().HaveCount(2);
        changes[0].FilePath.Should().Be("src/a.ts");
        changes[1].FilePath.Should().Be("src/b.ts");
    }

    [Fact]
    public void ParseCodeChangesFromResult_InsertEditFormat_Parsed()
    {
        var input = @"```insert_edit_into_file: src/utils.ts
const x = 1;
// ...existing code...
const y = 2;
```";

        var changes = ParseCodeChangesPublic(input);

        changes.Should().HaveCount(1);
        changes[0].FilePath.Should().Be("src/utils.ts");
        changes[0].BriefDescription.Should().Contain("insert_edit");
    }

    [Fact]
    public void ParseCodeChangesFromResult_PatchFormat_Parsed()
    {
        var input = @"*** Begin Patch
*** Update File: src/config.json
+  ""debug"": true,
*** End Patch";

        var changes = ParseCodeChangesPublic(input);

        changes.Should().HaveCount(1);
        changes[0].FilePath.Should().Be("src/config.json");
        changes[0].BriefDescription.Should().Contain("patch");
    }

    #endregion

    #region BuildSummaryMarkdown

    [Fact]
    public void BuildSummaryMarkdown_WithChanges_UsesAiSummaryDirectly()
    {
        var plan = new AgentTaskPlan
        {
            Title = "测试计划",
            ChangedFiles =
            {
                new FileChangeSummary { FilePath = "src/a.ts", LinesAdded = 10, LinesRemoved = 2 },
                new FileChangeSummary { FilePath = "src/b.ts", LinesAdded = 5, LinesRemoved = 0 },
            },
        };

        var result = BuildSummaryMarkdownPublic(plan, "AI 生成的变更摘要");

        result.Should().Contain("AI 生成的变更摘要");
        result.Should().NotContain("测试计划");
        result.Should().NotContain("a.ts");
        result.Should().NotContain("b.ts");
    }

    [Fact]
    public void BuildSummaryMarkdown_WithAiSummary_UsesAiSummaryDirectly()
    {
        var plan = new AgentTaskPlan
        {
            Title = "测试计划",
            ChangedFiles =
            {
                new FileChangeSummary { FilePath = "src/a.ts", LinesAdded = 10, LinesRemoved = 2 },
            },
        };

        string aiSummary = """
            ## 自由总结

            本次实现了完整登录流程。

            ```mermaid
            flowchart LR
                A[登录] --> B[令牌]
            ```
            """;

        var result = BuildSummaryMarkdownPublic(plan, aiSummary);

        result.Should().Be(aiSummary);
        result.Should().NotContain("测试计划");
        result.Should().NotContain("a.ts");
        result.Should().NotContain("步骤执行详情");
    }

    [Fact]
    public void BuildSummaryMarkdown_NoChanges_ShowsEmptyMessage()
    {
        var plan = new AgentTaskPlan { Title = "空计划" };

        var result = BuildSummaryMarkdownPublic(plan, null);

        result.Should().Contain("空计划");
    }

    #endregion

    // ──────────── Reflection helpers for testing private methods ────────────

    private static string SummarizeForLogPublic(string? message)
    {
        var method = typeof(AskAgent).GetMethod("SummarizeForLog",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, new object?[] { message })!;
    }

    private static string BuildSummaryMarkdownPublic(AgentTaskPlan plan, string? aiSummary)
    {
        var method = typeof(AskAgent).GetMethod("BuildSummaryMarkdown",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, new object?[] { plan, aiSummary })!;
    }

    private static string BuildContextualPromptPublic(string userMessage, AgentContext context)
    {
        var method = typeof(AskAgent).GetMethod("BuildContextualPrompt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, new object[] { userMessage, context })!;
    }

    private static List<FileChangeSummary> ParseCodeChangesPublic(string aiResult)
    {
        var method = typeof(BaseAgent).GetMethod("ParseCodeChangesFromResult",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (List<FileChangeSummary>)method!.Invoke(null, new object[] { aiResult })!;
    }

    private static string StripToolCallMarkersPublic(string text)
    {
        var method = typeof(AskAgent).GetMethod("StripToolCallMarkers",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, new object[] { text })!;
    }

    // ──────────── 提示词回显压缩（不进 UI 时间线） ────────────

    /// <summary>
    /// 回归：askStarted 的占位符是整段提示词，最长数 KB。
    /// 它以 INFO 级别广播给 UI 后会被渲染进过程折叠块，泄漏内部脚手架文案
    /// （如「自由生成面向用户的最终总结」）并撑大折叠块。
    /// 现改为只写日志文件，且日志内容压缩为首行 + 长度统计。
    /// </summary>
    [Fact]
    public void SummarizeForLog_MultiLinePrompt_KeepsFirstLineAndStats()
    {
        string prompt = string.Join("\n", new[]
        {
            "代码修改已完成。请自由生成面向用户的最终总结：不要求固定结构。",
            "",
            "**任务**: 输入框与聊天记录中 @ / 蓝色渲染实现计划",
            "## 步骤执行情况",
            "- ✅ 步骤 1: 抽取纯函数分词器 + 单元测试 — 修改 2 个文件",
        });

        string result = SummarizeForLogPublic(prompt);

        result.Should().StartWith("代码修改已完成。");
        result.Should().Contain("字符");
        result.Should().Contain("行");
        // 关键：只保留首行，后续脚手架内容（任务标题、步骤清单）不得带入
        result.Should().NotContain("步骤执行情况");
        result.Should().NotContain("步骤 1: 抽取纯函数分词器");
        result.Should().NotContain("**任务**");
        // 首行本身被保留（它是提示词的入口句），但整段不得原样透出
        result.Length.Should().BeLessThan(prompt.Length);
    }

    [Fact]
    public void SummarizeForLog_SingleLineShortMessage_ReturnsAsIs()
    {
        SummarizeForLogPublic("把@和/都加上蓝色渲染").Should().Be("把@和/都加上蓝色渲染");
    }

    [Fact]
    public void SummarizeForLog_VeryLongFirstLine_IsTruncated()
    {
        string longLine = new string('长', 500);

        string result = SummarizeForLogPublic(longLine);

        result.Length.Should().BeLessThan(200, "首行过长必须截断，避免日志刷屏");
        result.Should().Contain("…");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SummarizeForLog_EmptyInput_ReturnsPlaceholder(string? input)
    {
        SummarizeForLogPublic(input).Should().Be("(empty)");
    }
}
