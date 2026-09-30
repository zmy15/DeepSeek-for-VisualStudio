using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// WebPageCaptureService / CaptureWebpageTool 的纯逻辑单元测试。
///
/// 刻意不启动浏览器：这些用例只覆盖 URL 规范化、工具定义与参数校验分支，
/// 真实离屏渲染链路（WebView2 + CDP）由集成/手工验证覆盖。
/// </summary>
public class WebPageCaptureServiceTests
{
    #region URL normalization

    [Theory]
    [InlineData("example.com", "https")]
    [InlineData("https://example.com", "https")]
    [InlineData("http://example.com/a?b=1", "http")]
    [InlineData("  https://example.com/x  ", "https")]
    public void TryNormalizeUrl_AcceptsHttpAndHttps(string input, string expectedScheme)
    {
        WebPageCaptureService.TryNormalizeUrl(input, out string normalized).Should().BeTrue();

        Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri).Should().BeTrue();
        uri!.Scheme.Should().Be(expectedScheme);
    }

    [Fact]
    public void TryNormalizeUrl_BareHost_DefaultsToHttps()
    {
        WebPageCaptureService.TryNormalizeUrl("example.com", out string normalized).Should().BeTrue();

        normalized.Should().StartWith("https://");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("file:///C:/temp/page.html")]
    [InlineData("ftp://example.com/file")]
    public void TryNormalizeUrl_RejectsEmptyAndNonHttpSchemes(string input)
    {
        WebPageCaptureService.TryNormalizeUrl(input, out string normalized).Should().BeFalse();

        normalized.Should().BeEmpty();
    }

    #endregion

    #region Tool definition

    [Fact]
    public void CaptureWebpageTool_HasCorrectName()
    {
        new CaptureWebpageTool().Name.Should().Be("capture_webpage");
    }

    [Fact]
    public void CaptureWebpageTool_Definition_DeclaresUrlAndOptionalParameters()
    {
        var definition = new CaptureWebpageTool().GetDefinition();

        definition.Type.Should().Be("function");
        definition.Function.Name.Should().Be("capture_webpage");
        definition.Function.Description.Should().NotBeNullOrWhiteSpace();

        string parametersJson = JsonSerializer.Serialize(definition.Function.Parameters);
        parametersJson.Should().Contain("\"url\"");
        parametersJson.Should().Contain("\"required\"");
        parametersJson.Should().Contain("\"full_page\"");
        parametersJson.Should().Contain("\"viewport_width\"");
        parametersJson.Should().Contain("\"max_width\"");
        parametersJson.Should().Contain("\"save_path\"");
    }

    [Fact]
    public void CaptureWebpageTool_IsRegisteredAsBuiltIn()
    {
        BuiltInToolService.IsBuiltInTool("capture_webpage").Should().BeTrue();
    }

    #endregion

    #region Argument validation (no browser involved)

    [Fact]
    public async Task ExecuteAsync_MissingUrl_ReturnsError()
    {
        string result = await new CaptureWebpageTool()
            .ExecuteAsync(new Dictionary<string, JsonElement>(), null);

        result.Should().Contain("Error:");
        result.Should().Contain("capture_webpage");
    }

    [Fact]
    public async Task ExecuteAsync_NonHttpUrl_ReturnsErrorWithoutLaunchingBrowser()
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["url"] = JsonSerializer.SerializeToElement("file:///C:/temp/page.html"),
        };

        string result = await new CaptureWebpageTool().ExecuteAsync(args, null);

        result.Should().Contain("Error:");
        result.Should().Contain("capture_webpage");
    }

    [Fact]
    public void GetDisplayText_MentionsTargetUrl()
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["url"] = JsonSerializer.SerializeToElement("https://example.com"),
        };

        new CaptureWebpageTool().GetDisplayText(args).Should().Contain("example.com");
    }

    [Fact]
    public void GetResultSummary_PassesThroughErrorText()
    {
        const string errorText = "Error: capture_webpage: boom";

        new CaptureWebpageTool().GetResultSummary(errorText).Should().Be(errorText);
    }

    #endregion
}
