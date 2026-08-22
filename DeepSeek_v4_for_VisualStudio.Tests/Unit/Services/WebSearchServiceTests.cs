namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// WebSearchService 静态工具方法测试，重点覆盖 fetch_webpage 图片 URL 的
/// 视觉模型格式过滤（防止 SVG 等不支持格式直传导致 HTTP 400）。
/// </summary>
public class WebSearchServiceTests
{
    private const string BilibiliSvg =
        "https://i0.hdslb.com/bfs/activity-plat/static/20221018/df3e2ff90b315fca2f8d24a29cb68a47/2g46SzaanE.svg";

    #region FilterVisionImageUrls

    [Fact]
    public void FilterVisionImageUrls_SvgUrl_IsFilteredOut()
    {
        var result = WebSearchService.FilterVisionImageUrls(new[] { BilibiliSvg });

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("https://example.com/a.png")]
    [InlineData("https://example.com/a.jpg")]
    [InlineData("https://example.com/a.jpeg")]
    [InlineData("https://example.com/a.gif")]
    [InlineData("https://example.com/a.webp")]
    public void FilterVisionImageUrls_SupportedExtensions_AreKept(string url)
    {
        var result = WebSearchService.FilterVisionImageUrls(new[] { url });

        result.Should().NotBeNull();
        result.Should().ContainSingle().Which.Should().Be(url);
    }

    [Theory]
    [InlineData("https://example.com/a.PNG")]
    [InlineData("https://example.com/a.Jpg")]
    [InlineData("https://example.com/a.WEBP")]
    public void FilterVisionImageUrls_CaseInsensitive_MatchesExtension(string url)
    {
        var result = WebSearchService.FilterVisionImageUrls(new[] { url });

        result.Should().NotBeNull();
        result.Should().ContainSingle().Which.Should().Be(url);
    }

    [Fact]
    public void FilterVisionImageUrls_QueryString_StillMatchesExtension()
    {
        const string url = "https://example.com/image.png?width=100&height=200";

        var result = WebSearchService.FilterVisionImageUrls(new[] { url });

        result.Should().NotBeNull();
        result.Should().ContainSingle().Which.Should().Be(url);
    }

    [Theory]
    [InlineData("https://example.com/image.svg")]
    [InlineData("https://example.com/image.bmp")]
    [InlineData("https://example.com/image.tiff")]
    [InlineData("https://example.com/image.ico")]
    public void FilterVisionImageUrls_UnsupportedExtensions_AreFilteredOut(string url)
    {
        var result = WebSearchService.FilterVisionImageUrls(new[] { url });

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("https://example.com/image")]               // 无扩展名
    [InlineData("https://example.com/image/")]              // 目录结尾
    [InlineData("data:image/png;base64,AAAA")]              // data URI
    [InlineData("javascript:void(0)")]                      // 非 http(s)
    public void FilterVisionImageUrls_NoRecognizableExtension_IsFilteredOut(string url)
    {
        var result = WebSearchService.FilterVisionImageUrls(new[] { url });

        result.Should().BeNull();
    }

    [Fact]
    public void FilterVisionImageUrls_MixedList_ReturnsOnlySupported()
    {
        const string png = "https://example.com/keep.png";
        var input = new[] { png, BilibiliSvg, "https://example.com/drop.gif.extra" };

        var result = WebSearchService.FilterVisionImageUrls(input);

        result.Should().NotBeNull();
        result.Should().ContainSingle().Which.Should().Be(png);
    }

    [Fact]
    public void FilterVisionImageUrls_Null_ReturnsNull()
    {
        WebSearchService.FilterVisionImageUrls(null).Should().BeNull();
    }

    [Fact]
    public void FilterVisionImageUrls_EmptyList_ReturnsNull()
    {
        WebSearchService.FilterVisionImageUrls(new List<string>()).Should().BeNull();
    }

    #endregion
}
