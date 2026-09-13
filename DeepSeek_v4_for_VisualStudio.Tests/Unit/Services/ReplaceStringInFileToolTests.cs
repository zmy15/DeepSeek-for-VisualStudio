using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
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

    private static Dictionary<string, JsonElement> ParseArgs(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
           ?? new Dictionary<string, JsonElement>();
}
