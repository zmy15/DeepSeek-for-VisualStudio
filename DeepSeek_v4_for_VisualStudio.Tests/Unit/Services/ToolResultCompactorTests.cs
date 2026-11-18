using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// ToolResultCompactor 的截断策略测试。
/// 重点：web_search / fetch_webpage 的结果必须完整保留，不能被 head-tail 静默截断，
/// 否则模型会拿到断裂的网页正文或残缺的搜索结果。
/// </summary>
public class ToolResultCompactorTests
{
    private readonly ToolResultCompactor _compactor = new();

    [Theory]
    [InlineData("web_search")]
    [InlineData("fetch_webpage")]
    [InlineData("web_fetch")]
    public void WebTools_AreNeverTruncated(string toolName)
    {
        // 长度远超噪音工具软上限（4000）与硬上限（12000），确保命中截断路径。
        string content = new string('x', 50_000);

        var result = _compactor.CompactToolResultForContext(toolName, content, "deepseek-flash");

        result.Should().Be(content);
        result.Length.Should().Be(50_000);
    }

    [Fact]
    public void WebTools_KeepFullDocumentEvenWithSmallContextModel()
    {
        // deepseek-flash 不属于大上下文启发式（无 v4/reasoner/1m 关键字），
        // 旧实现会按 4000 字符软上限截断；现在必须原样返回。
        string document = "# DeepSeek Search/Fetch API 文档\n" + new string('字', 20_000);

        var fetched = _compactor.CompactToolResultForContext("fetch_webpage", document, "deepseek-flash");
        var searched = _compactor.CompactToolResultForContext("web_search", document, "deepseek-flash");

        fetched.Should().Be(document);
        searched.Should().Be(document);
    }

    [Fact]
    public void OtherNoisyTools_AreStillCompacted()
    {
        // 回归保护：web 工具豁免不应顺带放开其它噪音工具的裁剪。
        string content = new string('y', 50_000);

        var result = _compactor.CompactToolResultForContext("grep_search", content, "gpt-4.1");

        result.Length.Should().BeLessThan(content.Length);
        result.Should().Contain("内容已截断");
    }

    [Fact]
    public void ShortWebToolResult_IsReturnedUnchanged()
    {
        const string shortResult = "1. 标题\n   URL: https://example.com/a\n   摘要";

        _compactor.CompactToolResultForContext("web_search", shortResult, "deepseek-v4")
            .Should().Be(shortResult);
    }

    [Fact]
    public void DeepSeekFlash_IsTreatedAsLargeContextModel()
    {
        ToolResultCompactor.IsLargeContextModel("deepseek-flash").Should().BeTrue();
    }

    [Fact]
    public void RunSubagent_18KExploreReport_IsPreservedForDeepSeekFlash()
    {
        string report = "Explore 汇报\n" + new string('字', 18_000);

        string result = _compactor.CompactToolResultForContext(
            "runSubagent",
            report,
            "deepseek-flash");

        result.Should().Be(report);
    }

    [Fact]
    public void RunSubagent_ModerateReport_IsPreservedOnSmallContextModel()
    {
        string report = new string('x', 18_000);

        string result = _compactor.CompactToolResultForContext(
            "runSubagent",
            report,
            "gpt-4.1");

        result.Should().Be(report, "runSubagent must not have a tool-level truncation limit");
    }

    [Fact]
    public void RunSubagent_OversizedReport_IsPreserved()
    {
        string report = new string('z', 100_000);

        string result = _compactor.CompactToolResultForContext(
            "runSubagent",
            report,
            "gpt-4.1");

        result.Should().Be(report);
    }
}
