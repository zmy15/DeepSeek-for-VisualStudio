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
