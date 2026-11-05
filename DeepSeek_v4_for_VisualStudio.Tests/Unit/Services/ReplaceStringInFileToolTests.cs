using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using DeepSeek_v4_for_VisualStudio.Services.Editing;
using System.Text;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class ReplaceStringInFileToolTests
{
    [Fact]
    public void ReplaceTools_DoNotRequireExpectedInSchema()
    {
        var replaceParameters = JsonSerializer.SerializeToElement(
            new ReplaceStringInFileTool().GetDefinition().Function.Parameters);
        var multiParameters = JsonSerializer.SerializeToElement(
            new MultiReplaceStringInFileTool().GetDefinition().Function.Parameters);

        replaceParameters.GetProperty("required")
            .EnumerateArray().Select(item => item.GetString())
            .Should().NotContain("expected");
        multiParameters.GetProperty("required")
            .EnumerateArray().Select(item => item.GetString())
            .Should().NotContain("expected");
    }

    [Fact]
    public async Task ReplaceStringInFile_PerformsUniqueReplacement()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"replace-string-content-anchor-{Guid.NewGuid():N}.cs");
        File.WriteAllText(tempPath, "class Sample\n{\n    oldCall();\n}\n", Encoding.UTF8);

        try
        {
            var args = ParseArgs(JsonSerializer.Serialize(new
            {
                filePath = tempPath,
                oldString = "oldCall();",
                newString = "newCall();",
            }));

            string result = await new ReplaceStringInFileTool().ExecuteAsync(args, workspaceRoot: null);

            result.Should().NotStartWith("Error: ");
            File.ReadAllText(tempPath).Should().Contain("newCall();").And.NotContain("oldCall();");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task ReplaceStringInFile_LineRangeRestrictsUniquenessCheck()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"replace-string-range-{Guid.NewGuid():N}.txt");
        File.WriteAllText(tempPath, "old\nmiddle\nold\n", Encoding.UTF8);

        try
        {
            var args = ParseArgs(JsonSerializer.Serialize(new
            {
                filePath = tempPath,
                oldString = "old",
                newString = "new",
                lineRange = "1-1",
            }));

            string result = await new ReplaceStringInFileTool().ExecuteAsync(args, workspaceRoot: null);

            result.Should().NotStartWith("Error: ");
            File.ReadAllText(tempPath).Should().Be("new\nmiddle\nold\n");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task ReplaceStringInFile_LineRangeNoMatchDoesNotWrite()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"replace-string-range-miss-{Guid.NewGuid():N}.txt");
        string original = "old\nmiddle\nlast\n";
        File.WriteAllText(tempPath, original, Encoding.UTF8);

        try
        {
            var args = ParseArgs(JsonSerializer.Serialize(new
            {
                filePath = tempPath,
                oldString = "old",
                newString = "new",
                lineRange = "2-3",
            }));

            string result = await new ReplaceStringInFileTool().ExecuteAsync(args, workspaceRoot: null);

            result.Should().StartWith("Error: ");
            File.ReadAllText(tempPath).Should().Be(original);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task ReplaceStringInFile_RejectsInvalidLineRange()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"replace-string-range-invalid-{Guid.NewGuid():N}.txt");
        File.WriteAllText(tempPath, "old\n", Encoding.UTF8);

        try
        {
            var args = ParseArgs(JsonSerializer.Serialize(new
            {
                filePath = tempPath,
                oldString = "old",
                newString = "new",
                lineRange = "3-1",
            }));

            string result = await new ReplaceStringInFileTool().ExecuteAsync(args, workspaceRoot: null);

            result.Should().StartWith("Error: ");
            File.ReadAllText(tempPath).Should().Be("old\n");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task ReplaceStringInFile_ReplacesTextAfterLineOne()
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
    public async Task MultiReplaceStringInFile_ReplacesMultipleStrings()
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
    public async Task MultiReplaceStringInFile_UsesPerReplacementLineRanges()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"multi-replace-range-{Guid.NewGuid():N}.txt");
        File.WriteAllText(tempPath, "old\nmiddle\nold\n", Encoding.UTF8);

        try
        {
            var args = ParseArgs(JsonSerializer.Serialize(new
            {
                replacements = new[]
                {
                    new
                    {
                        filePath = tempPath,
                        oldString = "old",
                        newString = "first",
                        lineRange = "1-1",
                    },
                    new
                    {
                        filePath = tempPath,
                        oldString = "old",
                        newString = "third",
                        lineRange = "3-3",
                    },
                },
            }));

            string result = await new MultiReplaceStringInFileTool().ExecuteAsync(args, workspaceRoot: null);

            result.Should().NotStartWith("Error: ");
            File.ReadAllText(tempPath).Should().Be("first\nmiddle\nthird\n");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task MultiReplaceStringInFile_PrevalidatesAllLineRangesBeforeWriting()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"multi-replace-preflight-{Guid.NewGuid():N}.txt");
        string original = "first\nsecond\n";
        File.WriteAllText(tempPath, original, Encoding.UTF8);

        try
        {
            var args = ParseArgs(JsonSerializer.Serialize(new
            {
                replacements = new object[]
                {
                    new
                    {
                        filePath = tempPath,
                        oldString = "first",
                        newString = "changed",
                    },
                    new
                    {
                        filePath = tempPath,
                        oldString = "second",
                        newString = "changed second",
                        lineRange = "999-1000",
                    },
                },
            }));

            string result = await new MultiReplaceStringInFileTool().ExecuteAsync(args, workspaceRoot: null);

            result.Should().StartWith("Error: ");
            File.ReadAllText(tempPath).Should().Be(original);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task MultiReplaceStringInFile_PrevalidatesSharedLineRange()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"multi-replace-shared-range-{Guid.NewGuid():N}.txt");
        string original = "first\nsecond\n";
        File.WriteAllText(tempPath, original, Encoding.UTF8);

        try
        {
            var args = ParseArgs(JsonSerializer.Serialize(new
            {
                lineRange = "999-1000",
                replacements = new[]
                {
                    new
                    {
                        filePath = tempPath,
                        oldString = "first",
                        newString = "changed",
                    },
                    new
                    {
                        filePath = tempPath,
                        oldString = "second",
                        newString = "changed second",
                    },
                },
            }));

            string result = await new MultiReplaceStringInFileTool().ExecuteAsync(args, workspaceRoot: null);

            result.Should().StartWith("Error: ");
            File.ReadAllText(tempPath).Should().Be(original);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task MultiReplaceStringInFile_ContentAnchorsAreNotRequired()
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), $"multi-replace-content-anchor-{Guid.NewGuid():N}.cs");
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
            }));

            string result = await new MultiReplaceStringInFileTool().ExecuteAsync(args, workspaceRoot: null);

            result.Should().NotStartWith("Error: ");
            File.ReadAllText(tempPath).Should()
                .Contain("new title").And.Contain("new body")
                .And.NotContain("old title").And.NotContain("old body");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task ReplaceStringInFile_PreservesRawMarkdownBars()
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
