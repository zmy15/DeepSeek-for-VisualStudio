using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.Agents;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class ToolCallDeduplicatorTests
{
    [Fact]
    public void NormalizeToolArguments_CompactsWhitespace()
    {
        ToolCallDeduplicator.NormalizeToolArguments("{ \"a\" : 1 }").Should().Be("{\"a\":1}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeToolArguments_ReturnsEmptyForBlankInput(string input)
    {
        ToolCallDeduplicator.NormalizeToolArguments(input).Should().BeEmpty();
    }

    [Fact]
    public void NormalizeToolArguments_FallsBackToTrimForInvalidJson()
    {
        ToolCallDeduplicator.NormalizeToolArguments("  not-json  ").Should().Be("not-json");
    }

    [Fact]
    public void ExtractFilePathFromToolArgs_ReadsFilePathProperty()
    {
        ToolCallDeduplicator.ExtractFilePathFromToolArgs("read_file", "{\"filePath\":\"src/a.cs\"}")
            .Should().Be("src/a.cs");
    }

    [Fact]
    public void ExtractFilePathFromToolArgs_ReadsFirstArrayElement()
    {
        ToolCallDeduplicator.ExtractFilePathFromToolArgs("read_file", "{\"paths\":[\"a.cs\",\"b.cs\"]}")
            .Should().Be("a.cs");
    }

    [Fact]
    public void ExtractFilePathFromToolArgs_ReturnsNullWhenAbsent()
    {
        ToolCallDeduplicator.ExtractFilePathFromToolArgs("read_file", "{\"other\":1}")
            .Should().BeNull();
    }

    [Fact]
    public void DeduplicateToolCalls_KeepsFirstAndMapsDuplicates()
    {
        var calls = new List<ToolCall>
        {
            CreateCall("read_file", "{\"filePath\":\"a.cs\"}"),
            CreateCall("read_file", "{ \"filePath\" : \"a.cs\" }"),
            CreateCall("read_file", "{\"filePath\":\"b.cs\"}"),
        };

        var (dedupedIndices, dedupMapping) = ToolCallDeduplicator.DeduplicateToolCalls(calls);

        dedupedIndices.Should().Equal(0, 2);
        dedupMapping.Should().ContainKey(0);
        dedupMapping[0].Should().Equal(1);
    }

    private static ToolCall CreateCall(string name, string arguments)
        => new()
        {
            Function = new ToolCallFunction { Name = name, Arguments = arguments },
        };
}
