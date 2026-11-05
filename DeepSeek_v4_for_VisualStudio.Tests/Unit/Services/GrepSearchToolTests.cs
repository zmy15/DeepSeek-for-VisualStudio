using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class GrepSearchToolTests
{
    [Fact]
    public async Task ExecuteAsync_PathDirectory_RestrictsSearchScope()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            Directory.CreateDirectory(Path.Combine(workspace, "src"));
            Directory.CreateDirectory(Path.Combine(workspace, "other"));
            File.WriteAllText(Path.Combine(workspace, "src", "a.cs"), "TOKEN in src");
            File.WriteAllText(Path.Combine(workspace, "other", "b.cs"), "TOKEN in other");

            var tool = new GrepSearchTool();
            var args = CreateArgs(new Dictionary<string, object?>
            {
                ["query"] = "TOKEN",
                ["isRegexp"] = false,
                ["path"] = "src",
            });

            var result = await tool.ExecuteAsync(args, workspace);

            result.Should().Contain("src\\a.cs");
            result.Should().NotContain("other\\b.cs");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_IncludePattern_CannotEscapePathScope()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            Directory.CreateDirectory(Path.Combine(workspace, "src"));
            Directory.CreateDirectory(Path.Combine(workspace, "other"));
            File.WriteAllText(Path.Combine(workspace, "other", "b.cs"), "TOKEN outside scope");

            var tool = new GrepSearchTool();
            var args = CreateArgs(new Dictionary<string, object?>
            {
                ["query"] = "TOKEN",
                ["isRegexp"] = false,
                ["path"] = "src",
                ["includePattern"] = "../other/*.cs",
            });

            var result = await tool.ExecuteAsync(args, workspace);

            result.Should().NotContain("other\\b.cs");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PathFile_RestrictsSearchToOneFile()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            Directory.CreateDirectory(Path.Combine(workspace, "src"));
            File.WriteAllText(Path.Combine(workspace, "src", "a.cs"), "TOKEN in a");
            File.WriteAllText(Path.Combine(workspace, "src", "b.cs"), "TOKEN in b");

            var tool = new GrepSearchTool();
            var args = CreateArgs(new Dictionary<string, object?>
            {
                ["query"] = "TOKEN",
                ["isRegexp"] = false,
                ["path"] = Path.Combine("src", "a.cs"),
            });

            var result = await tool.ExecuteAsync(args, workspace);

            result.Should().Contain("src\\a.cs");
            result.Should().NotContain("src\\b.cs");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PathOutsideWorkspace_SearchesAbsolutePath()
    {
        var workspace = CreateTempWorkspace();
        var outside = CreateTempWorkspace();
        try
        {
            string outsideFile = Path.Combine(outside, "outside.txt");
            File.WriteAllText(outsideFile, "TOKEN outside workspace");

            var tool = new GrepSearchTool();
            var args = CreateArgs(new Dictionary<string, object?>
            {
                ["query"] = "TOKEN",
                ["isRegexp"] = false,
                ["path"] = outside,
            });

            var result = await tool.ExecuteAsync(args, workspace);

            // 工具层不再直接拒绝外部路径；是否放行由 BaseAgent 的统一越界审批决定。
            result.Should().NotStartWith("Error:");
            result.Should().Contain("outside.txt");
            result.Should().Contain("TOKEN outside workspace");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_BraceExpandedIncludePattern_SearchesMultipleExtensions()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            Directory.CreateDirectory(Path.Combine(workspace, "nested"));
            File.WriteAllText(Path.Combine(workspace, "a.cs"), "BaseUrl in C#");
            File.WriteAllText(Path.Combine(workspace, "nested", "b.csproj"), "<BaseUrl />");
            File.WriteAllText(Path.Combine(workspace, "nested", "c.txt"), "BaseUrl in text");

            var tool = new GrepSearchTool();
            var args = CreateArgs(new Dictionary<string, object?>
            {
                ["query"] = "BaseUrl",
                ["isRegexp"] = false,
                ["includePattern"] = "**/*.{cs,csproj}",
            });

            var result = await tool.ExecuteAsync(args, workspace);

            result.Should().Contain("a.cs");
            result.Should().Contain("b.csproj");
            result.Should().NotContain("c.txt");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_MaxResults_IsExplicitlyMarkedAsTruncated()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            for (int i = 0; i < 5; i++)
                File.WriteAllText(Path.Combine(workspace, $"file{i}.cs"), "TOKEN value " + i);

            var tool = new GrepSearchTool();
            var args = CreateArgs(new Dictionary<string, object?>
            {
                ["query"] = "TOKEN",
                ["isRegexp"] = false,
                ["maxResults"] = 3,
            });

            var result = await tool.ExecuteAsync(args, workspace);

            result.Should().Contain(">3", "header must make truncation visible");
            result.Should().Contain("maxResults=3");
            result.Split('\n')
                .Count(line => line.TrimStart().StartsWith("- `", StringComparison.Ordinal))
                .Should().Be(3);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_InvalidBracePattern_ReturnsExplicitError()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(workspace, "a.cs"), "TOKEN");

            var tool = new GrepSearchTool();
            var args = CreateArgs(new Dictionary<string, object?>
            {
                ["query"] = "TOKEN",
                ["isRegexp"] = false,
                ["includePattern"] = "**/*.{cs",
            });

            var result = await tool.ExecuteAsync(args, workspace);

            result.Should().StartWith("Error:");
            result.Should().Contain("includePattern");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static string CreateTempWorkspace()
    {
        string path = Path.Combine(Path.GetTempPath(), $"grep-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static Dictionary<string, JsonElement> CreateArgs(Dictionary<string, object?> values)
    {
        return values.ToDictionary(
            kvp => kvp.Key,
            kvp => JsonSerializer.SerializeToElement(kvp.Value));
    }
}
