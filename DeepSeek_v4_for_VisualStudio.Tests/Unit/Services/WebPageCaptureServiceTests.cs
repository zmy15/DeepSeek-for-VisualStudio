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
        parametersJson.Should().Contain("\"start_y\"");
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

    #region Band planning (full-page legibility policy)

    [Theory]
    // Short page: one band, nothing truncated.
    [InlineData(800, 0, 0, 1, false)]
    [InlineData(2048, 0, 0, 1, false)]
    // Just past one band -> two bands.
    [InlineData(2049, 0, 0, 2, false)]
    [InlineData(3000, 0, 0, 2, false)]
    // The reported regression: 11500px used to collapse into one 228x2048 strip.
    [InlineData(11500, 0, 0, 6, false)]
    // Exactly at the cap, then just past it.
    [InlineData(16384, 0, 0, 8, false)]
    [InlineData(16385, 0, 0, 8, true)]
    [InlineData(32971, 0, 0, 8, true)]
    // start_y walks past the first window (page continues).
    [InlineData(32971, 16384, 16384, 8, true)]
    [InlineData(20000, 19000, 19000, 1, false)]
    // start_y past the end is clamped, not rejected.
    [InlineData(100, 500, 99, 1, false)]
    // Unknown height degrades to a single band.
    [InlineData(0, 0, 0, 1, false)]
    public void PlanBands_CapsBandCountAndReportsTruncation(
        int documentHeight, int requestedStartY,
        int expectedStartY, int expectedBandCount, bool expectedTruncated)
    {
        var (startY, bandCount, truncated) =
            WebPageCaptureService.PlanBands(documentHeight, requestedStartY);

        startY.Should().Be(expectedStartY);
        bandCount.Should().Be(expectedBandCount);
        truncated.Should().Be(expectedTruncated);
    }

    /// <summary>
    /// Bands must never exceed the long-edge cap, otherwise a band would itself be the
    /// kind of oversized single image the banding exists to avoid.
    /// </summary>
    [Fact]
    public void FullPageBandHeight_StaysWithinTheLegibilityCap()
    {
        WebPageCaptureService.FullPageBandHeightPx
            .Should().BeLessThanOrEqualTo(WebPageCaptureService.DefaultMaxLongEdgePx);
    }

    #endregion
}
