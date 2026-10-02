using System.Collections.Generic;
using System.Reflection;
using DeepSeek_v4_for_VisualStudio.Models;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class ChatHtmlServiceTests
{
    [Fact]
    public void RenderMarkdownToHtml_MarkdownContent_ReturnsHtml()
    {
        var result = ChatHtmlService.RenderMarkdownToHtml("# DeepSeek\n\nHello **world**");

        result.Should().Contain("<h1");
        result.Should().Contain("DeepSeek</h1>");
        result.Should().Contain("<strong>world</strong>");
    }

    [Fact]
    public void RenderMarkdownToHtml_ThinkBlock_RendersReasoningAndAnswer()
    {
        var result = ChatHtmlService.RenderMarkdownToHtml("<think>reason here</think>answer here");

        result.Should().Contain("reasoning-panel");
        result.Should().Contain("reason here");
        result.Should().Contain("answer here");
    }

    [Fact]
    public void RenderMarkdownToHtml_SoftLineBreak_RendersLineBreak()
    {
        var result = ChatHtmlService.RenderMarkdownToHtml("first line\nsecond line");

        result.Should().Contain("<br />");
    }

    [Fact]
    public void BuildAssistantDisplayContent_CombinesTimelineAndFinalAnswer()
    {
        string result = ChatHtmlService.BuildAssistantDisplayContent(
            "正在读取文件\n**工具调用**：read_file",
            "最终答复");

        result.Should().Be("正在读取文件\n**工具调用**：read_file\n最终答复");
    }

    [Fact]
    public void BuildAssistantMessageHtml_RendersTimelineAndFinalAnswerInOneBubble()
    {
        var message = new ChatMessage
        {
            Role = "assistant",
            TimelineContent = "**工具调用**：read_file",
            Content = "最终答复",
        };

        string result = ChatHtmlService.BuildAssistantMessageHtml(message, 1);

        result.Should().Contain("read_file");
        result.Should().Contain("最终答复");
    }

    [Fact]
    public void BuildStreamEndJson_IncludesTimelineAndFinalAnswer()
    {
        string json = ChatHtmlService.BuildStreamEndJson(
            1,
            "最终答复",
            string.Empty,
            extraFooterHtml: null,
            timelineContent: "**工具调用**：read_file");

        json.Should().Contain("read_file");
        json.Should().Contain("最终答复");
    }

    [Fact]
    public void EscapeJsString_SpecialCharacters_ReturnsJsonStringLiteral()
    {
        var method = typeof(ChatHtmlService).GetMethod(
            "EscapeJsString",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var result = (string?)method!.Invoke(null, new object[] { "a\"b\nc中文" });

        result.Should().Be("\"a\\\"b\\nc中文\"");
    }

    // ── 过程折叠块：程序化收起不得被误判为用户手动开合 ──
    // details 的 toggle 事件对 JS 赋值引发的 open 变化同样会触发，
    // 若不加抑制标记，自动收回会把自己伪装成用户操作并永久锁定该块。
    [Fact]
    public void BuildTurnProcessJsFunction_GuardsProgrammaticCollapseFromUserToggle()
    {
        string js = InvokePrivateStaticString("BuildTurnProcessJsFunction");

        // 必须存在抑制机制，且在收起前被置位
        js.Should().Contain("__suppressTurnToggle");
        js.Should().Contain("__beginTurnToggleSuppress");
        int suppressIdx = js.IndexOf("__beginTurnToggleSuppress();", StringComparison.Ordinal);
        int openIdx = js.IndexOf("el.open=false", StringComparison.Ordinal);
        suppressIdx.Should().BeGreaterThanOrEqualTo(0);
        openIdx.Should().BeGreaterThan(suppressIdx,
            "抑制必须在 el.open=false 之前置位，否则排队的 toggle 回调会漏判");

        // 抑制标记须为计数器：重绘等场景可能连续多次收起，
        // 布尔量会被后一次覆盖，导致前一次排队的 toggle 回调漏判并被误报为用户操作
        js.Should().Contain("window.__suppressTurnToggle++");
        js.Should().Contain("window.__suppressTurnToggle--");

        // 已收起视为成功（幂等）：默认收起渲染下应直接命中，不产生多余 DOM 变更
        int idempotentIdx = js.IndexOf("if(el.open===false)return true;", StringComparison.Ordinal);
        idempotentIdx.Should().BeGreaterThanOrEqualTo(0);
        idempotentIdx.Should().BeLessThan(suppressIdx,
            "幂等判断必须早于抑制置位，否则每次兜底收起都会触发一次无谓的 toggle");

        // toggle 处理器必须检查抑制标记，且该分支不得上报用户意图
        int handlerIdx = js.IndexOf("document.addEventListener('toggle'", StringComparison.Ordinal);
        handlerIdx.Should().BeGreaterThan(openIdx);
        // 注意：测试项目目标为 net472，不可使用 C# 范围语法（System.Range 不可用）
        string handlerBody = js.Substring(handlerIdx);
        handlerBody.Should().Contain("__suppressTurnToggle");
        handlerBody.IndexOf("__suppressTurnToggle", StringComparison.Ordinal)
            .Should().BeLessThan(
                handlerBody.IndexOf("turnProcessToggled", StringComparison.Ordinal),
                "抑制检查必须早于上报，否则程序化收起仍会被当成用户操作回写");
    }

    [Fact]
    public void BuildStreamEndJson_ProcessTurn_RendersBlockCollapsedByDefault()
    {
        // 回归：streamEnd 是本气泡内容的最终覆盖，必须以「收起态」渲染过程块。
        // 此前按展开渲染再依赖宿主随后下发收起指令，会与 innerHTML 覆盖竞态，
        // 实测出现「找不到节点」以及界面停留在展开态（用户要求默认不展开）。
        // 摘要文案取工具调用次数，与时间线行数无关：
        // 这里 3 行时间线，但只声明了 2 次工具调用，摘要应显示 2
        string json = ChatHtmlService.BuildStreamEndJson(
            1,
            "最终总结",
            string.Empty,
            extraFooterHtml: null,
            timelineContent: "移交 Edit\n创建文件 leetcode.cpp\n构建解决方案",
            turnId: "4e9831ea",
            isProcessMessage: true,
            toolCallCount: 2);

        // 取出 html 字段后检查 details 开标签不含 open
        json.Should().Contain("turn-process");
        int detailsStart = json.IndexOf("<details class='turn-process'", StringComparison.Ordinal);
        detailsStart.Should().BeGreaterThanOrEqualTo(0);
        int detailsTagEnd = json.IndexOf(">", detailsStart, StringComparison.Ordinal);
        string openTag = json.Substring(detailsStart, detailsTagEnd - detailsStart + 1);
        openTag.Should().NotContain("open", "过程块默认应为收起态");
        openTag.Should().Contain("data-turn-id");
        json.Should().Contain("最终总结");
        // 摘要应使用工具调用口径（2），而非时间线非空行数（3）
        json.Should().Contain("2", "摘要应展示工具调用次数 2");
        json.Should().NotContain("3 步", "不得再按时间线行数统计步骤");
    }

    [Fact]
    public void BuildStreamEndJson_SummaryCountsToolCalls_NotTimelineLines()
    {
        // 回归：此前摘要按时间线非空行数统计，把步骤预告、工具返回和模型中间文本
        // 一并计入，得到的数字与「工具调用」并非同一口径。现改为使用 ToolCallCount。
        string manyLines = string.Join("\n", new[]
        {
            "移交 Edit",
            "步骤 1: 读取文件",
            "🔧 read_file (xxx)",
            "读取完成 120 行",
            "🔧 apply_patch (xxx)",
            "补丁已应用",
        });

        string json = ChatHtmlService.BuildStreamEndJson(
            1,
            "总结",
            string.Empty,
            timelineContent: manyLines,
            turnId: "aabbccdd",
            isProcessMessage: true,
            toolCallCount: 2);

        // 6 个非空行，但只有 2 次工具调用
        json.Should().Contain("2", "摘要必须反映真实工具调用次数");
        json.Should().NotContain("6", "不得再统计时间线行数");
    }

    [Fact]
    public void BuildStreamEndJson_ZeroToolCalls_RendersZeroCount()
    {
        // 边界：过程块存在（有中间文本）但一次工具都没调用时，应如实显示 0。
        string json = ChatHtmlService.BuildStreamEndJson(
            1,
            "总结",
            string.Empty,
            timelineContent: "只是中间文本，没有工具调用",
            turnId: "00112233",
            isProcessMessage: true,
            toolCallCount: 0);

        json.Should().Contain("turn-process");
        json.Should().Contain("0", "未调用工具时应显示 0 次");
    }

    [Fact]
    public void BuildTurnProcessProbeJs_TargetsTurnSpecificElement()
    {
        string js = ChatHtmlService.BuildTurnProcessProbeJs("turn123");

        js.Should().Contain("details.turn-process");
        js.Should().Contain("turn123");
        // 探测需返回布尔值，供宿主判定节点是否已挂上 DOM
        js.Should().Contain("!!document.querySelector");
    }

    [Fact]
    public void BuildCollapseTurnProcessJs_EscapesTurnId()
    {
        string js = ChatHtmlService.BuildCollapseTurnProcessJs("a'b");

        js.Should().Contain("__collapseTurnProcess");
        // 轮次标识中的引号必须转义，否则会截断选择器字符串
        js.Should().NotContain("'a'b'");
    }

    [Fact]
    public void AppendAssistantMessageHtml_HandoffChainTurn_WithProcess_RendersCollapsibleBlock()
    {
        // 回归：Handoff 链（Ask→Edit→Ask）全程共用一个气泡，末尾由 Ask 收尾，
        // 因此 msg.AgentType 可能是 Ask。若用「AgentType != Ask」判定是否可折叠，
        // 这种「Ask 收尾但确实跑过工具」的轮次就会被整轮漏掉（实测时间线长达 5790 字符仍不折叠）。
        // 判定必须依据轮次是否产生过过程输出（IsProcessMessage），而非收尾 Agent 类型。
        var message = new ChatMessage
        {
            Role = "assistant",
            AgentType = AgentType.Ask,          // 末尾 Ask 收尾（Handoff 链第 2 棒）
            TurnId = "81361469",
            IsProcessMessage = true,            // 本轮确实产生过过程
            TimelineContent = "移交 Edit\n创建文件 leetcode.cpp\n构建解决方案",  // 过程
            Content = "LeetCode 22「括号生成」已完成",                        // 最终总结
        };

        string html = ChatHtmlService.BuildAssistantMessageHtml(message, 1);

        html.Should().Contain("details class='turn-process'",
            "Ask 收尾但有过程的轮次同样应渲染折叠块");
        html.Should().Contain("data-turn-id='81361469'");
        // 过程在折叠块内，最终总结必须留在折叠块之外
        int detailsEnd = html.IndexOf("</details>", StringComparison.Ordinal);
        int summaryIdx = html.IndexOf("已完成", StringComparison.Ordinal);
        detailsEnd.Should().BeGreaterThanOrEqualTo(0);
        summaryIdx.Should().BeGreaterThan(detailsEnd, "最终总结必须在折叠块之外，才是「只保留总结」");
    }

    [Fact]
    public void AppendAssistantMessageHtml_AskPureQa_NoProcess_RendersNoCollapsibleBlock()
    {
        // Ask 纯问答（从未调用工具）不得出现折叠块，界面与折叠功能上线前一致。
        var message = new ChatMessage
        {
            Role = "assistant",
            AgentType = AgentType.Ask,
            IsProcessMessage = false,           // 从未产生过程
            TimelineContent = string.Empty,
            Content = "这是纯问答回答",
        };

        string html = ChatHtmlService.BuildAssistantMessageHtml(message, 1);

        html.Should().NotContain("turn-process", "Ask 纯问答不应产生折叠块");
        html.Should().Contain("这是纯问答回答");
    }

    [Fact]
    public void AppendAssistantMessageHtml_CollapsibleBlock_CollapsesWhenTurnFinished()
    {
        var message = new ChatMessage
        {
            Role = "assistant",
            AgentType = AgentType.Ask,
            TurnId = "abc12345",
            IsProcessMessage = true,
            TimelineContent = "步骤 1: 写入文件",
            Content = "最终总结",
            IsStreaming = false,
        };

        string html = ChatHtmlService.BuildAssistantMessageHtml(message, 1);

        // 已结束的轮次默认收起：details 不输出 open 属性
        int detailsStart = html.IndexOf("<details class='turn-process'", StringComparison.Ordinal);
        detailsStart.Should().BeGreaterThanOrEqualTo(0);
        int detailsTagEnd = html.IndexOf('>', detailsStart);
        string openTag = html.Substring(detailsStart, detailsTagEnd - detailsStart + 1);
        openTag.Should().NotContain("open", "已结束的轮次应以收起态渲染");
    }

    private static string InvokePrivateStaticString(string methodName)
    {
        var method = typeof(ChatHtmlService).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull($"{methodName} 应存在");
        return (string)method!.Invoke(null, null)!;
    }

    [Fact]
    public void BuildInitialPage_EditFork_RendersBranchNavInsideAssistantActionsRow()
    {
        var messages = new List<ChatMessage>
        {
            new ChatMessage
            {
                Role = "user",
                Content = "帮我修改代码",
                NodeId = "user-1",
                SiblingCount = 2,
                SiblingIndex = 1,
                ForkReason = "edit",
            },
            new ChatMessage
            {
                Role = "assistant",
                Content = "已修改完成",
                NodeId = "assistant-1",
            },
        };

        string html = ChatHtmlService.BuildInitialPage(messages);

        CountOccurrences(html, "<div class='branch-nav'>").Should().Be(1);
        int actionsRowIdx = html.IndexOf("<div class='msg-actions-row'>", StringComparison.Ordinal);
        int branchNavIdx = html.IndexOf("<div class='branch-nav'>", StringComparison.Ordinal);
        actionsRowIdx.Should().BeGreaterThanOrEqualTo(0);
        branchNavIdx.Should().BeGreaterThan(actionsRowIdx);
    }

    [Fact]
    public void BuildInitialPage_RetryFork_RendersBranchNavInsideAssistantActionsRow()
    {
        var messages = new List<ChatMessage>
        {
            new ChatMessage { Role = "user", Content = "请重新回答", NodeId = "user-1" },
            new ChatMessage
            {
                Role = "assistant",
                Content = "第一版回答",
                NodeId = "assistant-1",
                SiblingCount = 2,
                SiblingIndex = 1,
                ForkReason = "retry",
            },
        };

        string html = ChatHtmlService.BuildInitialPage(messages);

        CountOccurrences(html, "<div class='branch-nav'>").Should().Be(1);
        int actionsRowIdx = html.IndexOf("<div class='msg-actions-row'>", StringComparison.Ordinal);
        int branchNavIdx = html.IndexOf("<div class='branch-nav'>", StringComparison.Ordinal);
        actionsRowIdx.Should().BeGreaterThanOrEqualTo(0);
        branchNavIdx.Should().BeGreaterThan(actionsRowIdx);
    }

    [Fact]
    public void BuildInitialPage_RegistersF5RefreshGuardAndDebugForwarding()
    {
        string html = ChatHtmlService.BuildInitialPage(new List<ChatMessage>());

        html.Should().Contain("e.key==='F5'||e.code==='F5'");
        html.Should().Contain("e.preventDefault();");
        html.Should().Contain("e.stopPropagation();");
        html.Should().Contain("type:'debugShortcut'");
        html.Should().Contain("if(e.repeat)return;");
        html.Should().Contain("if(!plain)return;");
    }

    [Fact]
    public void BuildInitialPage_RegistersF7ViewCodeForwarding()
    {
        string html = ChatHtmlService.BuildInitialPage(new List<ChatMessage>());

        html.Should().Contain("e.key==='F7'||e.code==='F7'");
        html.Should().Contain("type:'viewCodeShortcut'");
        html.Should().Contain("designer:e.shiftKey");
    }

    [Fact]
    public void BuildInitialPageFromMessagesHtml_UsesProvidedWindow()
    {
        const string windowHtml = "<div id='render-window'>only recent messages</div>";

        string html = ChatHtmlService.BuildInitialPageFromMessagesHtml(windowHtml);

        html.Should().Contain(windowHtml);
        html.Should().Contain("window.__appendMessageHtml");
        html.Should().Contain("__pageReady__");
    }

    [Theory]
    [InlineData("terminal_command", "terminal-approval-")]
    [InlineData("file_delete", "file-delete-confirm-")]
    [InlineData("file_write", "agent-permission-")]
    [InlineData("command", "agent-permission-")]
    public void GetApprovalCardId_MapsActionTypeToCardIdPrefix(string actionType, string expectedPrefix)
    {
        string cardId = ChatHtmlService.GetApprovalCardId(actionType, "req-1");

        cardId.Should().Be(expectedPrefix + "req-1");
    }

    [Fact]
    public void GetApprovalCardId_MatchesCardIdUsedByInjectionBuilders()
    {
        // 注入（Builder）与移除（审批队列推进 / 看门狗）必须落在同一个 DOM id 上，
        // 否则卡片会在聊天区残留或永远无法移除。
        var terminal = new AgentPermissionRequest { ActionType = "terminal_command", Command = "dotnet build" };
        var delete = new AgentPermissionRequest { ActionType = "file_delete", Title = "确认删除" };
        var permission = new AgentPermissionRequest { ActionType = "file_write", Title = "确认修改" };

        ChatHtmlService.BuildTerminalApprovalJs(terminal)
            .Should().Contain($"div.id=\"{ChatHtmlService.GetApprovalCardId(terminal)}\"");
        ChatHtmlService.BuildFileDeleteConfirmationJs(delete)
            .Should().Contain($"div.id=\"{ChatHtmlService.GetApprovalCardId(delete)}\"");
        ChatHtmlService.BuildPermissionRequestJs(permission)
            .Should().Contain($"div.id=\"{ChatHtmlService.GetApprovalCardId(permission)}\"");

        // 移除脚本使用同一 id
        foreach (var request in new[] { terminal, delete, permission })
        {
            string cardId = ChatHtmlService.GetApprovalCardId(request);
            ChatHtmlService.BuildRemoveElementJs(cardId)
                .Should().Contain(cardId);
        }
    }

    [Fact]
    public void BuildAskQuestionsJs_MultipleQuestions_MergesIntoPaginatedCardWithSubmitOnLastPage()
    {
        var request = new AgentQuestionRequest
        {
            RequestId = "q-1",
            Questions = new List<AgentQuestion>
            {
                new AgentQuestion
                {
                    Header = "范围",
                    Question = "要改哪些文件？",
                    Options = new List<QuestionOption>
                    {
                        new QuestionOption { Label = "全部" },
                        new QuestionOption { Label = "仅当前" },
                    },
                },
                new AgentQuestion { Header = "风格", Question = "注释用什么语言？", AllowFreeformInput = true },
                new AgentQuestion
                {
                    Header = "确认",
                    Question = "可以开始吗？",
                    Options = new List<QuestionOption> { new QuestionOption { Label = "可以" } },
                },
            },
        };

        string js = ChatHtmlService.BuildAskQuestionsJs(request);

        // 三道题合并进同一个卡片，并渲染为三个分页（每页一题）
        CountOccurrences(js, "class='aq-page'").Should().Be(3);
        js.Should().Contain("id='agent-questions-pages'");
        js.Should().Contain("data-index='0'");
        js.Should().Contain("data-index='2'");
        js.Should().Contain("要改哪些文件？");
        js.Should().Contain("注释用什么语言？");
        js.Should().Contain("可以开始吗？");

        // 进度提示 + 切题箭头
        js.Should().Contain("id='agent-questions-progress'");
        CountOccurrences(js, "window.__askQuestionsNav('q-1',").Should().Be(2);
        js.Should().Contain("window.__askQuestionsNav('q-1',-1)");
        js.Should().Contain("window.__askQuestionsNav('q-1',1)");

        // 提交按钮只在最后一题显示：初始 display:none，由 __askQuestionsShowPage 在末页切为可见
        js.Should().Contain("id='agent-questions-submit'");
        js.Should().Contain("display:none;background:#0e639c");
        js.Should().Contain("window.__askQuestionsShowPage(div,0)");
    }

    [Fact]
    public void BuildAskQuestionsJs_SingleQuestion_HidesPagingAndShowsSubmitImmediately()
    {
        var request = new AgentQuestionRequest
        {
            RequestId = "q-2",
            Questions = new List<AgentQuestion>
            {
                new AgentQuestion { Header = "继续？", Question = "是否继续？", AllowFreeformInput = true },
            },
        };

        string js = ChatHtmlService.BuildAskQuestionsJs(request);

        CountOccurrences(js, "class='aq-page'").Should().Be(1);
        js.Should().NotContain("agent-questions-prev", "单题不需要切题箭头");
        js.Should().NotContain("agent-questions-progress", "单题不需要进度提示");
        js.Should().Contain("display:inline-block;background:#0e639c");
    }

    [Fact]
    public void BuildInitialPage_DefinesAskQuestionsPagingFunctionsAndCollectsAnswersPerPage()
    {
        string html = ChatHtmlService.BuildInitialPage(new List<ChatMessage>());

        // 卡片注入脚本会调用这些函数，必须存在于初始页面脚本中
        html.Should().Contain("window.__askQuestionsNav=function");
        html.Should().Contain("window.__askQuestionsShowPage=function");
        html.Should().Contain("window.__answerQuestions=function");

        // 提交时按分页顺序统一收集答案（未显示的页也要计入，避免答案与题目错位）
        html.Should().Contain("card.querySelectorAll('.aq-page')");
    }

    [Fact]
    public void BuildAgentTaskPanelCreateJs_RendersCollapseArrowSharingHeaderToggleLogic()
    {
        var plan = new AgentTaskPlan { PlanId = "p1", Title = "任务面板" };
        plan.Steps.Add(new AgentStep { Title = "步骤一", Status = AgentStepStatus.Completed });

        string js = ChatHtmlService.BuildAgentTaskPanelCreateJs(plan);

        // 头部点击与向下箭头必须走同一个折叠函数，保证「点箭头」与「点面板」行为完全一致
        js.Should().Contain(@"onclick=""window.__toggleTaskPanel(\'p1\')""");
        js.Should().Contain("class=\"task-collapse-arrow\"");
        js.Should().Contain(@"event.stopPropagation();window.__toggleTaskPanel(\'p1\');return false;",
            "箭头点击需阻止冒泡，否则会与头部点击叠加成两次切换（等于没切换）");
        js.Should().Contain("data-title-expanded=");
        js.Should().Contain("data-title-collapsed=");
        js.Should().Contain("&#9662;", "展开状态显示向下箭头");
        js.Should().Contain("window.__syncTaskPanelArrow(panel)", "创建后需初始化箭头方向与提示");
    }

    [Fact]
    public void BuildInitialPage_DefinesTaskPanelToggleFunctions()
    {
        string html = ChatHtmlService.BuildInitialPage(new List<ChatMessage>());

        // 面板注入脚本会调用这两个函数，必须存在于初始页面脚本中
        html.Should().Contain("window.__toggleTaskPanel=function");
        html.Should().Contain("window.__syncTaskPanelArrow=function");
    }

    [Fact]
    public void BuildTerminalApprovalJs_GeneratesCardInjectionScript()
    {
        var request = new AgentPermissionRequest
        {
            Title = "Run command?",
            Command = "dotnet --version",
            ActionType = "terminal_command",
            Purpose = "Check the installed SDK version.",
            FilePaths = new List<string> { "Read the local .NET SDK version." },
        };

        string js = ChatHtmlService.BuildTerminalApprovalJs(request);
        string cardId = "terminal-approval-" + request.RequestId;

        js.Should().Contain($"document.getElementById(\"{cardId}\")");
        js.Should().Contain($"div.id=\"{cardId}\"");
        js.Should().Contain("window.__scrollToBottom('smooth');");
        js.Should().Contain("window.__terminalApprove('" + request.RequestId + "')");
        js.Should().Contain("window.__terminalSkip('" + request.RequestId + "')");
        js.Should().NotContain("#region");
        js.Should().NotContain("#endregion");
        js.Should().NotContain("wind#");
    }

    [Fact]
    public void BuildPermissionRequestJs_GeneratesRequestScopedCardInjectionScript()
    {
        var request = new AgentPermissionRequest
        {
            RequestId = "request-1",
            Title = "<Modify project>",
            Command = "Update <ProjectReference>",
            ActionType = "file_write",
            Purpose = "Keep the project file valid.",
            Detail = "<Project><PropertyGroup /></Project>",
        };

        string js = ChatHtmlService.BuildPermissionRequestJs(request);
        string cardId = "agent-permission-" + request.RequestId;

        js.Should().Contain($"document.getElementById(\"{cardId}\")");
        js.Should().Contain($"div.id=\"{cardId}\"");
        js.Should().Contain("window.__agentApprove('request-1')");
        js.Should().Contain("window.__agentDeny('request-1')");
        js.Should().Contain("&lt;Modify project&gt;");
        js.Should().Contain("&lt;ProjectReference&gt;");
        js.Should().NotContain("#region");
    }

    [Fact]
    public void BuildFileDeleteConfirmationJs_GeneratesRequestScopedCardInjectionScript()
    {
        var request = new AgentPermissionRequest
        {
            RequestId = "delete-1",
            Title = "Delete file",
            ActionType = "file_delete",
            Purpose = "Remove obsolete source.",
            FilePaths = new List<string> { @"C:\repo\obsolete & legacy.cs" },
        };

        string js = ChatHtmlService.BuildFileDeleteConfirmationJs(request);
        string cardId = "file-delete-confirm-" + request.RequestId;

        js.Should().Contain($"document.getElementById(\"{cardId}\")");
        js.Should().Contain($"div.id=\"{cardId}\"");
        js.Should().Contain("window.__fileDeleteConfirm('delete-1')");
        js.Should().Contain("window.__fileDeleteCancel('delete-1')");
        js.Should().Contain("obsolete &amp; legacy.cs");
        js.Should().NotContain("#region");
    }

    [Fact]
    public void BuildRemoveElementJs_UsesEscapedElementId()
    {
        string js = ChatHtmlService.BuildRemoveElementJs("terminal-approval-abc");

        js.Should().Be("var p=document.getElementById(\"terminal-approval-abc\");if(p)p.remove();");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
