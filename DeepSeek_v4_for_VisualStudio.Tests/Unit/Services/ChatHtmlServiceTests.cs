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
