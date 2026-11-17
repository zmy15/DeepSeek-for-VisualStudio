using DeepSeek_v4_for_VisualStudio.Services;
using System.IO.Compression;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class PresentationRenderServiceTests
{
    [Theory]
    [InlineData("slides.pptx", true)]
    [InlineData("slides.PPT", true)]
    [InlineData("slides.pdf", false)]
    public void IsPresentationFile_RecognizesPowerPointFormats(string path, bool expected)
    {
        PresentationRenderService.IsPresentationFile(path).Should().Be(expected);
        if (expected)
            FileParserService.IsSupportedFormat(path).Should().BeTrue();
    }

    [Fact]
    public void TryExtractPptxText_ExtractsSlidesInNumericOrder()
    {
        string filePath = Path.Combine(
            Path.GetTempPath(),
            $"deepseek-pptx-{Guid.NewGuid():N}.pptx");
        try
        {
            using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
            {
                AddSlide(archive, 10, "last slide");
                AddSlide(archive, 2, "second slide");
                AddSlide(archive, 1, "first slide");
            }

            string? text = PresentationRenderService.TryExtractPptxText(filePath);

            text.Should().NotBeNull();
            text!.IndexOf("first slide", StringComparison.Ordinal)
                .Should().BeLessThan(text.IndexOf("second slide", StringComparison.Ordinal));
            text.IndexOf("second slide", StringComparison.Ordinal)
                .Should().BeLessThan(text.IndexOf("last slide", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    private static void AddSlide(ZipArchive archive, int slideNumber, string text)
    {
        var entry = archive.CreateEntry($"ppt/slides/slide{slideNumber}.xml");
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                   xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <p:cSld>
                <p:spTree>
                  <p:sp>
                    <p:txBody>
                      <a:p><a:r><a:t>{text}</a:t></a:r></a:p>
                    </p:txBody>
                  </p:sp>
                </p:spTree>
              </p:cSld>
            </p:sld>
            """);
    }
}
