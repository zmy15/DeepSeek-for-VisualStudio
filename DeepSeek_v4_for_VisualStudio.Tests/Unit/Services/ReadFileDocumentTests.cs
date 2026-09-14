using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using NPOI.XSSF.UserModel;
using NPOI.XWPF.UserModel;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class ReadFileDocumentTests
{
    [Fact]
    public void CreateFileReferences_PreservesPathWithoutEmbeddingContent()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "do-not-embed-this");

            var references = FileParserService.CreateFileReferences(new[] { path });
            string context = FileParserService.FormatParseResultsForContext(references);

            references.Should().ContainSingle();
            references[0].FilePath.Should().Be(path);
            references[0].Content.Should().BeNull();
            context.Should().Contain(path);
            context.Should().Contain("read_file");
            context.Should().NotContain("do-not-embed-this");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadFileTool_ReadsXlsxThroughDocumentParser()
    {
        string path = Path.Combine(Path.GetTempPath(), $"deepseek-read-{Guid.NewGuid():N}.xlsx");
        try
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var workbook = new XSSFWorkbook())
            {
                var sheet = workbook.CreateSheet("Data");
                var row = sheet.CreateRow(0);
                row.CreateCell(0).SetCellValue("Header");
                row.CreateCell(1).SetCellValue(42);
                workbook.Write(stream);
            }

            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                JsonSerializer.Serialize(new { filePath = path }))!;

            string result = await new ReadFileTool(new()).ExecuteAsync(args, null);

            result.Should().Contain("Header");
            result.Should().Contain("42");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadFileTool_ReadsDocxThroughDocumentParser()
    {
        string path = Path.Combine(Path.GetTempPath(), $"deepseek-read-{Guid.NewGuid():N}.docx");
        try
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var document = new XWPFDocument())
            {
                document.CreateParagraph().CreateRun().SetText("Word document content");
                document.Write(stream);
            }

            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                JsonSerializer.Serialize(new { filePath = path }))!;

            string result = await new ReadFileTool(new()).ExecuteAsync(args, null);

            result.Should().Contain("Word document content");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
