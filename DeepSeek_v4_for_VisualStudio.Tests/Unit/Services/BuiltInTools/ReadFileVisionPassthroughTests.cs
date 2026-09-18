using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services.BuiltInTools;

public class ReadFileVisionPassthroughTests : IDisposable
{
    private readonly string _tempDir;

    public ReadFileVisionPassthroughTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rf_vision_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task ExecuteAsync_VisionModel_ReturnsImageBlockWithoutOcr()
    {
        string imagePath = Path.Combine(_tempDir, "sample.png");
        byte[] imageBytes = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        File.WriteAllBytes(imagePath, imageBytes);

        var tool = new ReadFileTool(new ConcurrentDictionary<string, FileReadCacheEntry>())
        {
            IsVisionModelProvider = () => true,
        };

        string result = await tool.ExecuteAsync(CreateArgs(imagePath), null);

        result.Should().Contain(ReadFileTool.VisionImageBlockStart);
        result.Should().Contain(imagePath);

        var (cleanText, imageDataUris) = ReadFileTool.ParseImageBlock(result);
        cleanText.Should().NotContain(ReadFileTool.VisionImageBlockStart);
        cleanText.Should().Contain("vision_passthrough=\"true\"");
        imageDataUris.Should().ContainSingle();
        imageDataUris[0].Should().Be(
            "data:image/png;base64," + Convert.ToBase64String(imageBytes));
    }

    [Fact]
    public void ParseImageBlock_DataUriLine_IsReturnedWithoutFileRead()
    {
        const string dataUri = "data:image/webp;base64,AAAA";
        string raw =
            "<file path=\"sample.webp\" vision_passthrough=\"true\">\n" +
            ReadFileTool.VisionImageBlockStart + "\n" +
            dataUri + "\n" +
            ReadFileTool.VisionImageBlockEnd + "\n" +
            "</file>";

        var (cleanText, imageDataUris) = ReadFileTool.ParseImageBlock(raw);

        cleanText.Should().NotContain(ReadFileTool.VisionImageBlockStart);
        imageDataUris.Should().Equal(new[] { dataUri });
    }

    private static Dictionary<string, JsonElement> CreateArgs(string filePath)
    {
        var args = new Dictionary<string, JsonElement>();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { filePath }));
        foreach (var prop in doc.RootElement.EnumerateObject())
            args[prop.Name] = prop.Value.Clone();
        return args;
    }
}
