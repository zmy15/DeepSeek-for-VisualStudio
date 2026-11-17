using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class ReadFilePageRangeTests
{
    [Theory]
    [InlineData("notes.pdf", true)]
    [InlineData("slides.pptx", true)]
    [InlineData("slides.ppt", true)]
    [InlineData("notes.docx", false)]
    public void IsPagedDocument_OnlyIncludesPdfAndPowerPoint(string path, bool expected)
    {
        ReadFileTool.IsPagedDocument(path).Should().Be(expected);
    }

    [Fact]
    public void OfficialOcr_DeclaresDocumentedPageLimit()
    {
        DeepSeekOcrClient.MaxPagesPerRequest.Should().Be(1000);
    }

    [Fact]
    public void SplitPagedDocumentContent_RecognizesEnglishAndChinesePageMarkers()
    {
        const string content =
            "--- Page 1/3 ---\nfirst\n" +
            "--- 第 2/3 页 ---\nsecond\n" +
            "--- Slide 3/3 ---\nthird";

        var pages = ReadFileTool.SplitPagedDocumentContent(content);

        pages.Should().HaveCount(3);
        pages[0].Should().Contain("first");
        pages[1].Should().Contain("second");
        pages[2].Should().Contain("third");
    }

    [Fact]
    public void BuildPagedDocumentResult_DefaultReadsAll41Pages()
    {
        string content = BuildPages(41);

        string result = ReadFileTool.BuildPagedDocumentResult(
            @"C:\docs\course.pdf",
            content,
            startPage: 1,
            requestedEndPage: int.MaxValue,
            requestedMaxPages: ReadFileTool.DefaultMaxPages);

        result.Should().Contain("total_pages=\"41\"");
        result.Should().Contain("shown_pages=\"1-41\"");
        result.Should().Contain("truncated=\"false\"");
        result.Should().Contain("page 41");
    }

    [Fact]
    public void BuildPagedDocumentResult_ExplicitRangeReturnsOnlyRequestedPages()
    {
        string result = ReadFileTool.BuildPagedDocumentResult(
            @"C:\docs\course.pdf",
            BuildPages(41),
            startPage: 31,
            requestedEndPage: 38,
            requestedMaxPages: ReadFileTool.DefaultMaxPages);

        result.Should().Contain("shown_pages=\"31-38\"");
        result.Should().Contain("page 31");
        result.Should().Contain("page 38");
        result.Should().NotContain("page 30");
        result.Should().NotContain("page 39");
    }

    [Fact]
    public void BuildPagedDocumentResult_ReturnsNextPageWhenOverDefaultLimit()
    {
        string result = ReadFileTool.BuildPagedDocumentResult(
            @"C:\docs\course.pdf",
            BuildPages(60),
            startPage: 1,
            requestedEndPage: int.MaxValue,
            requestedMaxPages: ReadFileTool.DefaultMaxPages);

        result.Should().Contain("shown_pages=\"1-50\"");
        result.Should().Contain("truncated=\"true\"");
        result.Should().Contain("next_start_page=\"51\"");
    }

    [Fact]
    public void BuildVisionDocumentResult_UsesRequestedPageRange()
    {
        string path = Path.GetTempFileName();
        try
        {
            var batch = new RenderedDocumentPageBatch
            {
                TotalPages = 41,
                StartPage = 31,
                Pages = new[] { new byte[] { 1, 2 }, new byte[] { 3, 4 } },
            };

            string? result = ReadFileTool.BuildVisionDocumentResult(path, "pdf", batch);

            result.Should().NotBeNull();
            result.Should().Contain("total_pages=\"41\"");
            result.Should().Contain("shown_pages=\"31-32\"");
            result.Should().Contain("next_start_page=\"33\"");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BuildVisionDocumentResult_ReportsPageBeyondDocument()
    {
        string path = Path.GetTempFileName();
        try
        {
            var batch = new RenderedDocumentPageBatch
            {
                TotalPages = 41,
                StartPage = 42,
            };

            string? result = ReadFileTool.BuildVisionDocumentResult(path, "pdf", batch);

            result.Should().NotBeNull();
            result.Should().Contain("shown_pages=\"none\"");
            result.Should().Contain("start_page 42 is beyond total_pages 41");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string BuildPages(int count)
    {
        var builder = new System.Text.StringBuilder();
        for (int page = 1; page <= count; page++)
        {
            builder.AppendLine($"--- Page {page}/{count} ---");
            builder.AppendLine($"page {page} content");
        }
        return builder.ToString();
    }
}
