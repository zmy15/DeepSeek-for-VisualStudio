using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class FileSymbolsToolTests
{
    [Fact]
    public void Definition_RequiresFilePath()
    {
        var definition = new FileSymbolsTool().GetDefinition();

        definition.Function.Name.Should().Be("get_file_symbols");
        definition.Function.Parameters.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_MissingFile_ReturnsPathError()
    {
        var tool = new FileSymbolsTool();
        var missingPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.cs");
        var args = new Dictionary<string, JsonElement>
        {
            ["filePath"] = JsonSerializer.SerializeToElement(missingPath)
        };

        var result = await tool.ExecuteAsync(args, null);

        result.Should().StartWith("Error:");
        result.Should().Contain(Path.GetFileName(missingPath));
        ToolExecutionOutcome.Classify(result).Should().Be(ToolResultKind.ToolError);
    }

    [Fact]
    public void GetResultSummary_Error_IsPreserved()
    {
        var summary = new FileSymbolsTool().GetResultSummary("Error: file not found");

        summary.Should().Be("Error: file not found");
    }
}
