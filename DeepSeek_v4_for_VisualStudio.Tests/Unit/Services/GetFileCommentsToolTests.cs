using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using FluentAssertions;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services
{
    public class GetFileCommentsToolTests
    {
        [Fact]
        public void ExtractAttachedComment_CSharpXmlDoc_StripsCommentMarkers()
        {
            string[] lines =
            {
                "public class Sample",
                "{",
                "    /// <summary>",
                "    /// Performs work.",
                "    /// </summary>",
                "    public void Work() { }",
                "}",
            };

            string? comment = GetFileCommentsTool.ExtractAttachedComment(lines, 6);

            comment.Should().Be("<summary>\nPerforms work.\n</summary>".Replace("\n", System.Environment.NewLine));
        }

        [Fact]
        public void ExtractAttachedComment_BlockComment_StripsMarkers()
        {
            string[] lines =
            {
                "/*",
                " * Performs work.",
                " */",
                "public void Work() { }",
            };

            string? comment = GetFileCommentsTool.ExtractAttachedComment(lines, 4);

            comment.Should().Be("Performs work.");
        }

        [Fact]
        public void ExtractAttachedComment_BlankLineBeforeDeclaration_ReturnsNull()
        {
            string[] lines =
            {
                "/// Detached comment",
                string.Empty,
                "public void Work() { }",
            };

            GetFileCommentsTool.ExtractAttachedComment(lines, 3).Should().BeNull();
        }

        [Fact]
        public void FormatCommentsOutput_GroupsBySymbolAndLine()
        {
            var entries = new List<GetFileCommentsTool.FileCommentEntry>
            {
                new("class", "Sample.Widget", 10, "Widget summary."),
                new("method", "Sample.Widget.Run", 25, "Run summary."),
            };

            string result = GetFileCommentsTool.FormatCommentsOutput("src/Widget.cs", entries);

            result.Should().Contain("class `Sample.Widget` - line 10");
            result.Should().Contain("Widget summary.");
            result.Should().Contain("method `Sample.Widget.Run` - line 25");
            result.Should().Contain("Run summary.");
        }

        [Fact]
        public void BuildNoCommentsResult_UsesReadFileFallback()
        {
            string result = GetFileCommentsTool.BuildNoCommentsResult("src/Widget.cs");

            result.Should().Contain("read_file");
        }

        [Fact]
        public void ResultSummary_PreservesErrorPrefix()
        {
            var tool = new GetFileCommentsTool();

            tool.GetResultSummary("Error: failed").Should().Be("Error: failed");
        }

    [Fact]
    public void ResultSummary_NoComments_UsesReadFileHint()
    {
        var tool = new GetFileCommentsTool();

        string noComments = tool.GetResultSummary("No comments. Use read_file to inspect.");
        string withComments = tool.GetResultSummary("### class `Sample`");

        noComments.Should().NotBeNullOrWhiteSpace();
        noComments.Should().NotBe(withComments);
    }

        [Fact]
        public void GetDisplayText_IncludesFileName()
        {
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                "{\"filePath\":\"C:\\\\src\\\\Widget.cs\"}")!;

            string result = new GetFileCommentsTool().GetDisplayText(args);

            result.Should().Contain("Widget.cs");
        }
    }
}
