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
}
