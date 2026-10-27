using System;
using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Utils;

/// <summary>
/// <see cref="MentionTokenizer"/> 的单元测试：覆盖 @ / token 识别、边界与围栏排除规则。
/// </summary>
public class MentionTokenizerTests
{
    #region 空输入

    [Fact]
    public void FindTokens_WithNull_ReturnsEmptyList()
    {
        var tokens = MentionTokenizer.FindTokens(null);

        tokens.Should().NotBeNull();
        tokens.Should().BeEmpty();
    }

    [Fact]
    public void FindTokens_WithEmptyString_ReturnsEmptyList()
    {
        var tokens = MentionTokenizer.FindTokens(string.Empty);

        tokens.Should().BeEmpty();
    }

    [Fact]
    public void FindTokens_WithPlainText_ReturnsEmptyList()
    {
        var tokens = MentionTokenizer.FindTokens("帮我看看这段代码");

        tokens.Should().BeEmpty();
    }

    #endregion

    #region 单 token 识别

    [Fact]
    public void FindTokens_WithAgentToken_ReturnsAgentKindAndRange()
    {
        var tokens = MentionTokenizer.FindTokens("@ask");

        tokens.Should().HaveCount(1);
        tokens[0].Kind.Should().Be(MentionTokenKind.Agent);
        tokens[0].Start.Should().Be(0);
        tokens[0].Length.Should().Be(4);
        tokens[0].End.Should().Be(4);
    }

    [Fact]
    public void FindTokens_WithSkillToken_ReturnsSkillKindAndRange()
    {
        var tokens = MentionTokenizer.FindTokens("/review");

        tokens.Should().HaveCount(1);
        tokens[0].Kind.Should().Be(MentionTokenKind.Skill);
        tokens[0].Start.Should().Be(0);
        tokens[0].Length.Should().Be(7);
    }

    [Fact]
    public void FindTokens_WithOnlyTriggerChar_ReturnsEmptyList()
    {
        // 孤立的触发字符不足以构成提及，长度必须大于 1
        MentionTokenizer.FindTokens("@").Should().BeEmpty();
        MentionTokenizer.FindTokens("/").Should().BeEmpty();
        MentionTokenizer.FindTokens("a @ b / c").Should().BeEmpty();
    }

    #endregion

    #region 组合与顺序

    [Fact]
    public void FindTokens_WithAgentThenSkill_ReturnsBothInOrder()
    {
        var tokens = MentionTokenizer.FindTokens("@ask /review 参数");

        tokens.Should().HaveCount(2);

        tokens[0].Kind.Should().Be(MentionTokenKind.Agent);
        tokens[0].Start.Should().Be(0);
        tokens[0].Length.Should().Be(4);

        // "/review" 位于 "@ask " 之后，起算 5
        tokens[1].Kind.Should().Be(MentionTokenKind.Skill);
        tokens[1].Start.Should().Be(5);
        tokens[1].Length.Should().Be(7);
    }

    [Fact]
    public void FindTokens_WithTabSeparator_RecognizesFollowingToken()
    {
        var tokens = MentionTokenizer.FindTokens("@ask\t/review");

        tokens.Should().HaveCount(2);
        tokens[1].Start.Should().Be(5);
    }

    [Fact]
    public void FindTokens_ReturnsAscendingStartOrder()
    {
        var tokens = MentionTokenizer.FindTokens("/a @b /c @d");

        tokens.Should().HaveCount(4);
        for (var i = 1; i < tokens.Count; i++)
        {
            tokens[i].Start.Should().BeGreaterThan(tokens[i - 1].Start);
        }
    }

    #endregion

    #region 词边界误判防护

    [Theory]
    [InlineData("mail me at a@b.com")]
    [InlineData("see https://example.com/path")]
    [InlineData("路径 src/utils/helper.cs")]
    [InlineData("100% 完成")]
    public void FindTokens_WithPunctuationInsideWord_DoesNotMatch(string text)
    {
        // 只有 token 首字符是 @ 或 / 才算提及，避免邮箱、URL、路径被误着色
        MentionTokenizer.FindTokens(text).Should().BeEmpty();
    }

    [Fact]
    public void FindTokens_WithSkillFollowedByArguments_MatchesOnlySkillName()
    {
        var tokens = MentionTokenizer.FindTokens("/help 一些参数");

        tokens.Should().HaveCount(1);
        tokens[0].Kind.Should().Be(MentionTokenKind.Skill);
        tokens[0].Length.Should().Be(5);
    }

    #endregion

    #region 多行文本

    [Fact]
    public void FindTokens_OnSecondLine_ReportsAbsoluteStartIndex()
    {
        var tokens = MentionTokenizer.FindTokens("第一行\n@ask");

        tokens.Should().HaveCount(1);
        // "第一行" 3 字符 + 换行符 1 个
        tokens[0].Start.Should().Be(4);
        tokens[0].Length.Should().Be(4);
    }

    [Fact]
    public void FindTokens_WithCrLf_DoesNotShiftLineStart()
    {
        var tokens = MentionTokenizer.FindTokens("abc\r\n/review");

        tokens.Should().HaveCount(1);
        tokens[0].Start.Should().Be(5);
        tokens[0].Kind.Should().Be(MentionTokenKind.Skill);
    }

    #endregion

    #region 代码围栏排除

    [Fact]
    public void FindTokens_InsideCodeFence_IsIgnored()
    {
        var text = "看这里 @ask\n```\n/inside\n@hidden\n```\n@after";

        var tokens = MentionTokenizer.FindTokens(text);

        // 围栏内的 /inside 与 @hidden 被跳过，只保留围栏外的两个
        tokens.Should().HaveCount(2);
        tokens[0].Kind.Should().Be(MentionTokenKind.Agent);
        tokens[0].Start.Should().Be(4);
        tokens[1].Kind.Should().Be(MentionTokenKind.Agent);
        tokens[1].Start.Should().Be(text.IndexOf("@after", StringComparison.Ordinal));
    }

    [Fact]
    public void FindTokens_WithIndentedFence_IsIgnored()
    {
        var text = "```\n@hidden\n  ```\n@visible";

        var tokens = MentionTokenizer.FindTokens(text);

        tokens.Should().HaveCount(1);
        tokens[0].Start.Should().Be(text.IndexOf("@visible", StringComparison.Ordinal));
    }

    [Fact]
    public void FindTokens_WithUnclosedFence_SkipsRemainder()
    {
        var text = "@before\n```\n@hidden1\n@hidden2";

        var tokens = MentionTokenizer.FindTokens(text);

        tokens.Should().HaveCount(1);
        tokens[0].Start.Should().Be(0);
    }

    #endregion

    #region 值类型契约

    [Fact]
    public void Constructor_WithNegativeStart_Throws()
    {
        var act = () => new MentionToken(-1, 3, MentionTokenKind.Agent);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Constructor_WithNonPositiveLength_Throws(int length)
    {
        var act = () => new MentionToken(0, length, MentionTokenKind.Skill);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void End_AlwaysEqualsStartPlusLength()
    {
        var token = new MentionToken(7, 4, MentionTokenKind.Agent);

        token.End.Should().Be(token.Start + token.Length);
        token.End.Should().Be(11);
    }

    #endregion

    #region 高亮片段切分（输入框叠加层必须逐字符复现原文）

    [Fact]
    public void SplitForHighlight_AgentAndSkill_KeepsSpaceBetweenThem()
    {
        // 回归：叠加层曾只渲染 token，丢掉 @agent 与 /skill 之间的空格，
        // 导致两个 token 在视觉上粘连、末尾露出未被覆盖的底层字符。
        const string text = "@explore /improve-codebase-architecture";

        var segments = MentionTokenizer.SplitForHighlight(text);

        segments.Select(s => s.Text)
            .Should().Equal("@explore", " ", "/improve-codebase-architecture");
        segments.Select(s => s.Kind)
            .Should().Equal(MentionTokenKind.Agent, (MentionTokenKind?)null, MentionTokenKind.Skill);
        string.Concat(segments.Select(s => s.Text)).Should().Be(text);
    }

    [Fact]
    public void SplitForHighlight_PlainTextAroundToken_KeepsAllText()
    {
        const string text = "hello @ask world";

        var segments = MentionTokenizer.SplitForHighlight(text);

        string.Concat(segments.Select(s => s.Text)).Should().Be(text);
        segments.Select(s => s.Text).Should().Equal("hello ", "@ask", " world");
        segments[1].Kind.Should().Be(MentionTokenKind.Agent);
    }

    [Fact]
    public void SplitForHighlight_NoToken_ReturnsWholeTextAsPlain()
    {
        var segments = MentionTokenizer.SplitForHighlight("普通文本");

        segments.Should().ContainSingle();
        segments[0].Kind.Should().BeNull();
        segments[0].Text.Should().Be("普通文本");
    }

    [Fact]
    public void SplitForHighlight_Empty_ReturnsEmpty()
    {
        MentionTokenizer.SplitForHighlight(null).Should().BeEmpty();
        MentionTokenizer.SplitForHighlight(string.Empty).Should().BeEmpty();
    }

    #endregion
}
