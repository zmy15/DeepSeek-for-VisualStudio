using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using DeepSeek_v4_for_VisualStudio.Services.Editing;
using System.Text;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class ReplaceStringInFileToolTests
{
    [Fact]
    public async Task ReplaceStringInFile_AllowsExpectedFragmentStartingAfterLineOne()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"replace-string-fragment-{Guid.NewGuid():N}.md");
        File.WriteAllText(tempPath, "line 1\nold title\nline 3\n", Encoding.UTF8);

        try
        {
            var args = ParseArgs(JsonSerializer.Serialize(new
            {
                filePath = tempPath,
                oldString = "old title",
                newString = "new title",
                expected = "2|new title",
            }));

            string result = await new ReplaceStringInFileTool().ExecuteAsync(args, workspaceRoot: null);

            result.Should().NotStartWith("Error: ");
            File.ReadAllText(tempPath).Should().Contain("new title");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task MultiReplaceStringInFile_AllowsExpectedFragmentStartingAfterLineOne()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"multi-replace-fragment-{Guid.NewGuid():N}.md");
        File.WriteAllText(tempPath, "line 1\nold title\nold body\n", Encoding.UTF8);

        try
        {
            var args = ParseArgs(JsonSerializer.Serialize(new
            {
                replacements = new[]
                {
                    new
                    {
                        filePath = tempPath,
                        oldString = "old title",
                        newString = "new title",
                    },
                    new
                    {
                        filePath = tempPath,
                        oldString = "old body",
                        newString = "new body",
                    },
                },
                expected = "2|new title\n3|new body",
            }));

            string result = await new MultiReplaceStringInFileTool().ExecuteAsync(args, workspaceRoot: null);

            result.Should().NotStartWith("Error: ");
            File.ReadAllText(tempPath).Should().Contain("new title").And.Contain("new body");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task ReplaceStringInFile_ExpectedKeepsRawMarkdownBarsAndEmptyLine()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"replace-string-raw-lines-{Guid.NewGuid():N}.md");
        File.WriteAllText(tempPath, "line 1\n| a | b |\n\nline 4\n", Encoding.UTF8);

        try
        {
            var args = ParseArgs(JsonSerializer.Serialize(new
            {
                filePath = tempPath,
                oldString = "| a | b |",
                newString = "| a | c |",
                expected = "2|| a | c |\n3|",
            }));

            string result = await new ReplaceStringInFileTool().ExecuteAsync(args, workspaceRoot: null);

            result.Should().NotStartWith("Error: ");
            File.ReadAllText(tempPath).Should().Contain("| a | c |");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task MultiReplaceStringInFile_ExpectedFragmentMayCoverOnlyOneChangedRegion()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"multi-replace-fragment-subset-{Guid.NewGuid():N}.md");
        File.WriteAllText(tempPath, "old title\nmiddle\nold body\n", Encoding.UTF8);

        try
        {
            var args = ParseArgs(JsonSerializer.Serialize(new
            {
                replacements = new[]
                {
                    new
                    {
                        filePath = tempPath,
                        oldString = "old title",
                        newString = "new title",
                    },
                    new
                    {
                        filePath = tempPath,
                        oldString = "old body",
                        newString = "new body",
                    },
                },
                expected = "3|new body",
            }));

            string result = await new MultiReplaceStringInFileTool().ExecuteAsync(args, workspaceRoot: null);

            result.Should().NotStartWith("Error: ");
            File.ReadAllText(tempPath).Should().Contain("new title").And.Contain("new body");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task MultiReplaceStringInFile_RollsBackWhenLaterReplacementFails()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"multi-replace-rollback-{Guid.NewGuid():N}.cs");
        File.WriteAllText(tempPath, "first\nsecond\n", Encoding.UTF8);

        try
        {
            var args = ParseArgs(JsonSerializer.Serialize(new
            {
                replacements = new[]
                {
                    new { filePath = tempPath, oldString = "first", newString = "new first" },
                    new { filePath = tempPath, oldString = "missing", newString = "x" },
                },
                expected = "2|second",
            }));

            string result = await new MultiReplaceStringInFileTool().ExecuteAsync(args, workspaceRoot: null);

            result.Should().Contain("success 1, fail 1");
            File.ReadAllText(tempPath).Should().Contain("first").And.NotContain("new first");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task MultiReplaceStringInFile_RejectsMultipleFilesBeforeApplyingAnyChange()
    {
        string temp1 = Path.Combine(
            Path.GetTempPath(), $"multi-replace-a-{Guid.NewGuid():N}.cs");
        string temp2 = Path.Combine(
            Path.GetTempPath(), $"multi-replace-b-{Guid.NewGuid():N}.cs");
        File.WriteAllText(temp1, "one\n", Encoding.UTF8);
        File.WriteAllText(temp2, "two\n", Encoding.UTF8);

        try
        {
            var args = ParseArgs(JsonSerializer.Serialize(new
            {
                replacements = new[]
                {
                    new { filePath = temp1, oldString = "one", newString = "new one" },
                    new { filePath = temp2, oldString = "two", newString = "new two" },
                },
                expected = "1|new one",
            }));

            string result = await new MultiReplaceStringInFileTool().ExecuteAsync(args, workspaceRoot: null);

            result.Should().StartWith("Error: ");
            File.ReadAllText(temp1).Should().Contain("one").And.NotContain("new one");
            File.ReadAllText(temp2).Should().Contain("two").And.NotContain("new two");
        }
        finally
        {
            if (File.Exists(temp1)) File.Delete(temp1);
            if (File.Exists(temp2)) File.Delete(temp2);
        }
    }

    [Fact]
    public async Task MultiReplaceStringInFile_RollsBackInWorkspaceMode()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"multi-replace-workspace-{Guid.NewGuid():N}.cs");
        File.WriteAllText(tempPath, "first\nsecond\n", Encoding.UTF8);
        var workspace = new StagedEditWorkspace();

        try
        {
            var tool = new MultiReplaceStringInFileTool { Workspace = workspace };
            var args = ParseArgs(JsonSerializer.Serialize(new
            {
                replacements = new[]
                {
                    new { filePath = tempPath, oldString = "first", newString = "new first" },
                    new { filePath = tempPath, oldString = "missing", newString = "x" },
                },
                expected = "2|second",
            }));

            string result = await tool.ExecuteAsync(args, workspaceRoot: null);

            result.Should().Contain("success 1, fail 1");
            File.ReadAllText(tempPath).Should().Contain("first").And.NotContain("new first");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static Dictionary<string, JsonElement> ParseArgs(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
           ?? new Dictionary<string, JsonElement>();
}
