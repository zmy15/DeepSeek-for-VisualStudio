using DeepSeek_v4_for_VisualStudio.Services.Agents;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class AgentJsonHelperTests
{
    [Fact]
    public void ExtractJsonFromMarkdown_ReturnsJsonInsideFence()
    {
        string text = "```json\n{\"a\":1}\n```";

        AgentJsonHelper.ExtractJsonFromMarkdown(text).Should().Be("{\"a\":1}");
    }

    [Fact]
    public void ExtractJsonFromMarkdown_StripsDsmlTags()
    {
        string text = "<|thinking|>内部推理<|/thinking|>{\"name\":\"x\"}";

        AgentJsonHelper.ExtractJsonFromMarkdown(text).Should().Be("{\"name\":\"x\"}");
    }

    [Fact]
    public void ExtractJsonFromMarkdown_ReturnsEmptyObjectForBlankInput()
    {
        AgentJsonHelper.ExtractJsonFromMarkdown("   ").Should().Be("{}");
    }

    [Fact]
    public void TryRepairTruncatedJson_ClosesMissingBrace()
    {
        string repaired = AgentJsonHelper.TryRepairTruncatedJson("{\"a\":\"b\"");

        AgentJsonHelper.IsJsonBracketBalanced(repaired).Should().BeTrue();
        repaired.Should().EndWith("}");
    }

    [Fact]
    public void TryRepairTruncatedJson_LeavesCompleteJsonUnchanged()
    {
        const string json = "{\"a\":1}";

        AgentJsonHelper.TryRepairTruncatedJson(json).Should().Be(json);
    }

    [Theory]
    [InlineData("{\"a\":[1]}", true)]
    [InlineData("{\"a\":[1", false)]
    [InlineData("{\"a\":1}}", false)]
    public void IsJsonBracketBalanced_DetectsUnbalancedInput(string json, bool expected)
    {
        AgentJsonHelper.IsJsonBracketBalanced(json).Should().Be(expected);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("a\nb\nc", 3)]
    public void CountLines_CountsNewlines(string text, int expected)
    {
        AgentJsonHelper.CountLines(text).Should().Be(expected);
    }

    [Fact]
    public void CountDiffLines_ReturnsZeroForIdenticalText()
    {
        AgentJsonHelper.CountDiffLines("a\nb", "a\nb", out int added, out int removed);

        added.Should().Be(0);
        removed.Should().Be(0);
    }
}
