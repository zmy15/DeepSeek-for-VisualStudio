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
        var handoffPrompt = global::DeepSeek_v4_for_VisualStudio.Services.LocalizationService.Instance["agent.summary.handoffUserPrompt"];
        var summaryOnlyPrompt = global::DeepSeek_v4_for_VisualStudio.Services.LocalizationService.Instance["agent.summaryOnlySystemPrompt"];

        handoffPrompt.Should().Contain("Markdown");
        handoffPrompt.Should().Contain("Mermaid");
        handoffPrompt.Should().Contain("LaTeX");
        handoffPrompt.Should().Contain("不要求固定结构");

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

        // 执行类工具同样不能在 Ask 工具集里：
        // Ask 无法跑测试/构建，缺失的 run_tests 曾让模型在"能不能调用"上反复摇摆。
        AskAgent.AskTools.Should().NotContain("run_tests");
        AskAgent.AskTools.Should().NotContain("build_solution");
    }

    #endregion

    #region Execution Boundary Prompt

    /// <summary>
    /// 回归：Ask 系统提示词必须显式说明"没有 run_tests / build_solution，遇到执行类请求直接移交"。
    /// 否则模型会看到用户要求跑测试、又找不到对应工具，进而在思考里反复自我说服。
    /// </summary>
    [Fact]
    public void SystemPrompt_StatesExecutionToolsAreUnavailableAndHandoffIsRequired()
    {
        string prompt = LocalizationService.Instance["agent.ask.systemPromptFragment"]
            + LocalizationService.Instance["system.agent.askPromptFragment"];

        // 只断言语言无关的标识符：工具名与移交目标，避免在非中文语言环境下误报。
        prompt.Should().Contain("run_tests");
        prompt.Should().Contain("build_solution");
        prompt.Should().Contain("request_handoff");
        prompt.Should().Contain("Edit");
        // 明确禁止用终端顶替专用工具（否则模型会用 run_in_terminal 绕一圈）
        prompt.Should().Contain("run_in_terminal");
    }

    /// <summary>
    /// 回归：单纯的构建/测试请求要交给 Build Agent（它同时持有 build_solution 与 run_tests），
    /// 而不是 Edit —— 前者只需一个结果，后者才是改代码的场景。
    /// </summary>
    [Fact]
    public void SystemPrompt_RoutesPlainBuildAndTestRequestsToBuildAgent()
    {
        string prompt = LocalizationService.Instance["system.agent.askPromptFragment"];

        // 移交清单里，构建/测试两项的目标必须是 Build
        prompt.Should().Contain("Build");
        prompt.Should().Contain("run_tests");
        prompt.Should().Contain("build_solution");

        // 移交清单里「运行测试」那一条必须指向 Build（断言语言无关：只找含 run_tests 的清单项）
        var testLine = prompt
            .Split('\n')
            .FirstOrDefault(l => l.TrimStart().StartsWith("- ") && l.Contains("run_tests"));
        testLine.Should().NotBeNull("移交清单必须给出运行测试的目标 Agent");
        testLine!.Should().Contain("Build", "运行测试应移交给 Build Agent");
        testLine.Should().NotContain("Edit", "运行测试不应再移交给 Edit Agent");
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
        // 移交提示带「当前任务目标」标记
        messages[4].Content.Should().Be("[当前任务目标] handoff user");
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
                // 快照来自真实管线：用户文本落库时已带「本轮用户需求」前缀
                new() { Role = "user", Content = "[本轮用户需求] 原始用户提问" },
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
        // 源轮次的用户需求标记在移交后必须保留
        messages[2].Content.Should().Be("[本轮用户需求] 原始用户提问");

        // 身份边界提示紧随用户提问之后（[3]），而非直接跟在主 system 之后
        messages[3].Role.Should().Be("system");
        messages[3].Content.Should().NotBeNullOrWhiteSpace();

        // Handoff 分支不再重复注入 volatile 块，改为紧接 [新任务 user][Edit 提示词]
        messages[4].Role.Should().Be("user");
        // 移交提示带「当前任务目标」标记
        messages[4].Content.Should().Be("[当前任务目标] handoff user");
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
    public void SnapshotHandoffCacheMessages_WithCompletedResult_AppendsFinalAssistantBeforeTargetPrompt()
    {
        var sentMessages = new List<ChatApiMessage>
        {
            new() { Role = "system", Content = "stable system" },
            new() { Role = "user", Content = "补齐执行器测试" },
            new()
            {
                Role = "assistant",
                ToolCalls = new List<ToolCall>
                {
                    new()
                    {
                        Id = "call_1",
                        Type = "function",
                        Function = new ToolCallFunction { Name = "replace_string_in_file", Arguments = "{}" },
                    },
                },
            },
            new() { Role = "tool", ToolCallId = "call_1", Name = "replace_string_in_file", Content = "编辑应用并验证成功。" },
            new() { Role = "system", Content = "source agent prompt" },
        };

        typeof(DeepSeek_v4_for_VisualStudio.Services.Providers.OpenAiCompatibleProvider)
            .GetField("<LastSentMessages>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(_apiService, sentMessages);

        var agent = new AskAgent(_apiService);
        var result = new AgentResult
        {
            Content = "执行器单元测试已补齐并验证。",
            ReasoningContent = "完成测试和验证。",
        };

        var snapshot = agent.SnapshotHandoffCacheMessages(result);

        snapshot.Should().NotBeNull();
        snapshot.Should().HaveCount(5);
        snapshot![snapshot.Count - 1].Role.Should().Be("assistant");
        snapshot[snapshot.Count - 1].Content.Should().Be(result.Content);
        snapshot[snapshot.Count - 1].ReasoningContent.Should().Be(result.ReasoningContent);
        snapshot.Should().Contain(m => m.Role == "tool" && m.ToolCallId == "call_1");
        snapshot.Should().NotContain(m => m.Content == "source agent prompt");
    }

    [Fact]
    public void SnapshotHandoffCacheMessages_RequestHandoffToolChain_DoesNotAppendSyntheticNotice()
    {
        var sentMessages = new List<ChatApiMessage>
        {
            new() { Role = "system", Content = "stable system" },
            new() { Role = "user", Content = "排查测试失败" },
            new()
            {
                Role = "assistant",
                Content = "遵照选择，我把排查任务移交 Build Agent。",
                ToolCalls = new List<ToolCall>
                {
                    new()
                    {
                        Id = "call_handoff",
                        Type = "function",
                        Function = new ToolCallFunction { Name = "request_handoff", Arguments = "{}" },
                    },
                },
            },
            new() { Role = "tool", ToolCallId = "call_handoff", Name = "request_handoff", Content = "HANDOFF_REQUESTED" },
            new() { Role = "system", Content = "source agent prompt" },
        };

        typeof(DeepSeek_v4_for_VisualStudio.Services.Providers.OpenAiCompatibleProvider)
            .GetField("<LastSentMessages>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(_apiService, sentMessages);

        var agent = new AskAgent(_apiService);
        var snapshot = agent.SnapshotHandoffCacheMessages(new AgentResult
        {
            Content = "任务已移交给 Build Agent。",
        });

        snapshot.Should().NotBeNull();
        snapshot.Should().HaveCount(4);
        snapshot![snapshot.Count - 1].Role.Should().Be("tool");
        snapshot[snapshot.Count - 1].Name.Should().Be("request_handoff");
    }

    [Fact]
    public void EnsureCompletedAssistantInHandoffPrefix_AppendsToExistingSnapshot()
    {
        var snapshot = new List<ChatApiMessage>
        {
            new() { Role = "system", Content = "stable system" },
            new() { Role = "user", Content = "补齐执行器测试" },
            new()
            {
                Role = "assistant",
                Content = "构建验证中",
                ToolCalls = new List<ToolCall>
                {
                    new()
                    {
                        Id = "call_build",
                        Type = "function",
                        Function = new ToolCallFunction { Name = "build_solution", Arguments = "{}" },
                    },
                },
            },
            new() { Role = "tool", ToolCallId = "call_build", Name = "build_solution", Content = "构建成功" },
        };

        bool appended = BaseAgent.EnsureCompletedAssistantInHandoffPrefix(
            snapshot,
            new AgentResult { Content = "源 Agent 最终回答", ReasoningContent = "完成验证。" });

        appended.Should().BeTrue();
        snapshot.Should().HaveCount(5);
        snapshot[snapshot.Count - 1].Role.Should().Be("assistant");
        snapshot[snapshot.Count - 1].Content.Should().Be("源 Agent 最终回答");
    }

    [Fact]
    public void BuildMemoryExtractionMessages_ReusesSummaryPrefixAndAppendsMemoryTail()
    {
        var handoff = new AgentHandoff
        {
            IsSummaryOnly = true,
            ForwardedMessages = new List<ChatApiMessage>
            {
                new() { Role = "system", Content = "stable system" },
                new() { Role = "user", Content = "原用户请求" },
                new()
                {
                    Role = "assistant",
                    Content = "构建验证中",
                    ToolCalls = new List<ToolCall>
                    {
                        new()
                        {
                            Id = "call_build",
                            Type = "function",
                            Function = new ToolCallFunction { Name = "build_solution", Arguments = "{}" },
                        },
                    },
                },
                new() { Role = "tool", ToolCallId = "call_build", Name = "build_solution", Content = "构建成功" },
                new() { Role = "system", Content = "Edit 模式行为提示" },
            },
        };

        var messages = DeepSeek_v4_for_VisualStudio.View.DeepSeekChatControl
            .BuildMemoryExtractionMessages(handoff, new AgentResult
            {
                Content = "源 Agent 最终回答",
                ReasoningContent = "完成执行。",
            });

        messages.Should().HaveCount(7);
        messages[4].Role.Should().Be("assistant");
        messages[4].Content.Should().Be("源 Agent 最终回答");
        messages[4].ReasoningContent.Should().Be("完成执行。");
        messages[5].Role.Should().Be("user");
        messages[5].Content.Should().Be(AiPrompts.MemoryAutoRecordUserPrompt);
        messages[6].Role.Should().Be("system");
        messages[6].Content.Should().Be(AiPrompts.MemoryAutoRecordSystemPrompt);
        messages.Should().NotContain(m => m.Content == "Edit 模式行为提示");
    }

    [Fact]
    public void SummaryHandoffUserPrompt_IsInstructionOnly()
    {
        string prompt = LocalizationService.Instance["agent.summary.handoffUserPrompt"];

        prompt.Should().NotContain("步骤执行情况");
        prompt.Should().NotContain("修改的文件");
        prompt.Should().NotContain("链回原始请求");
        prompt.Should().NotContain("以下资料仅供参考");
        prompt.Should().NotContain("不是输出模板");
        prompt.Should().Contain("最终总结");
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


    // ──────────── Reflection helpers for testing private methods ────────────

    private static string SummarizeForLogPublic(string? message)
    {
        // SummarizeForLog 已提升到 BaseAgent，供 Ask/Build/Plan/Explore 共用
        var method = typeof(BaseAgent).GetMethod("SummarizeForLog",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, new object?[] { message })!;
    }

    private static string BuildContextualPromptPublic(string userMessage, AgentContext context)
    {
        var method = typeof(AskAgent).GetMethod("BuildContextualPrompt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, new object[] { userMessage, context })!;
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

    /// <summary>
    /// 回归：Agent 启动行必须走不带 {0} 的 *Short 键写时间线。
    ///
    /// 原实现把整段 Handoff prompt 经 AddLog("INFO", ...) 广播 → 渲染进 UI 过程折叠块，
    /// 用户会看到本该给 AI 的指令（例如 Build 收到测试请求时那条
    /// 「优先复用对话历史中已读取的文件内容…」）。
    /// 现在长文案只写日志文件，时间线固定用短句。
    /// </summary>
    [Theory]
    [InlineData("agent.log.buildStartedShort")]
    [InlineData("agent.log.planStartedShort")]
    [InlineData("agent.log.exploreStartedShort")]
    [InlineData("agent.log.askStartedShort")]
    public void AgentStartTimelineKeys_AreStaticAndHaveNoPromptPlaceholder(string key)
    {
        string text = LocalizationService.Instance[key];

        // 有值（键存在），且不含 {0} 占位符——否则仍会把输入拼进 UI 时间线
        text.Should().NotStartWith("[", "本地化键必须存在");
        text.Should().NotContain("{0}", "时间线文案不得包含提示词占位符");
        text.Length.Should().BeLessThan(120);
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
