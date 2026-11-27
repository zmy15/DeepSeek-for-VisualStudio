using DeepSeek_v4_for_VisualStudio.Services.Agents;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class ToolClassifierTests
{
    [Theory]
    [InlineData("create_file", true)]
    [InlineData("apply_patch", true)]
    [InlineData("read_file", false)]
    [InlineData("grep_search", false)]
    public void IsWriteTool_ClassifiesWriteOperations(string toolName, bool expected)
    {
        ToolClassifier.IsWriteTool(toolName).Should().Be(expected);
    }

    [Theory]
    [InlineData("replace_string_in_file", true)]
    [InlineData("apply_patch", true)]
    [InlineData("read_file", false)]
    [InlineData("git", false)]
    public void IsFileModifyingTool_ClassifiesFileMutations(string toolName, bool expected)
    {
        ToolClassifier.IsFileModifyingTool(toolName).Should().Be(expected);
    }

    [Theory]
    [InlineData("grep_search", true)]
    [InlineData("create_directory", true)]
    [InlineData("run_in_terminal", false)]
    public void IsFileAccessingTool_ClassifiesPathAccess(string toolName, bool expected)
    {
        ToolClassifier.IsFileAccessingTool(toolName).Should().Be(expected);
    }

    [Theory]
    [InlineData("run_in_terminal", true)]
    [InlineData("build_solution", true)]
    [InlineData("capture_window", false)]
    public void IsInteractiveTool_ClassifiesInteractionRequirement(string toolName, bool expected)
    {
        ToolClassifier.IsInteractiveTool(toolName).Should().Be(expected);
    }

    [Theory]
    [InlineData("askQuestions", "VisualStudio_askQuestions")]
    [InlineData("read_file", "read_file")]
    public void NormalizeToolName_MapsLegacyAlias(string toolName, string expected)
    {
        ToolClassifier.NormalizeToolName(toolName).Should().Be(expected);
    }

    [Theory]
    [InlineData("read_file", true)]
    [InlineData("symbol_search", true)]
    [InlineData("run_in_terminal", false)]
    public void IsDirectExplorationTool_ClassifiesExplorationTools(string toolName, bool expected)
    {
        ToolClassifier.IsDirectExplorationTool(toolName).Should().Be(expected);
    }

    [Theory]
    [InlineData("..\\outside\\a.cs", "C:\\workspace", true)]
    [InlineData("src\\a.cs", "C:\\workspace", false)]
    public void IsPathOutsideWorkspace_DetectsEscapingRelativePath(string target, string root, bool expected)
    {
        ToolClassifier.IsPathOutsideWorkspace(target, root).Should().Be(expected);
    }

    [Fact]
    public void IsPathOutsideWorkspace_ReturnsFalseForBlankInput()
    {
        ToolClassifier.IsPathOutsideWorkspace("", "C:\\workspace").Should().BeFalse();
        ToolClassifier.IsPathOutsideWorkspace("src\\a.cs", "").Should().BeFalse();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ShouldSuppressOcrTools_FollowsVisionModelFlag(bool isVisionModel, bool expected)
    {
        ToolClassifier.ShouldSuppressOcrTools(isVisionModel, "读取图片").Should().Be(expected);
    }

    [Fact]
    public void ExtractAnyPathFromToolArgs_ReadsIncludePatternDirectory()
    {
        // glob 片段被截断后再取目录，因此 C:\ws\src\** 解析为上层的 C:\ws
        ToolClassifier.ExtractAnyPathFromToolArgs(
                "file_search", "{\"includePattern\":\"C:\\\\ws\\\\src\\\\**\"}")
            .Should().Be("C:\\ws");
    }

    [Fact]
    public void ExtractAnyPathFromToolArgs_ReturnsNullForRelativePattern()
    {
        ToolClassifier.ExtractAnyPathFromToolArgs(
                "file_search", "{\"includePattern\":\"src/**\"}")
            .Should().BeNull();
    }
}
