using System.IO;
using DeepSeek_v4_for_VisualStudio.Services.Agents;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class AgentPathResolverTests
{
    [Theory]
    [InlineData("app.csproj", true)]
    [InlineData("CMakeLists.txt", true)]
    [InlineData("app.slnx", true)]
    [InlineData("Program.cs", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsProjectFile_RecognizesProjectFiles(string? filePath, bool expected)
    {
        AgentPathResolver.IsProjectFile(filePath).Should().Be(expected);
    }

    [Fact]
    public void ProjectFileExtensions_ExposesReadOnlyCollection()
    {
        AgentPathResolver.ProjectFileExtensions.Should().Contain(".csproj");
        AgentPathResolver.ProjectFileExtensions.Should().Contain(".slnx");
    }

    [Theory]
    [InlineData("src/file.cs", "src\\file.cs")]
    [InlineData("src\\file.cs\\", "src\\file.cs")]
    [InlineData("", "")]
    public void NormalizePath_UnifiesSeparatorAndTrimsTrailingSlash(string input, string expected)
    {
        AgentPathResolver.NormalizePath(input).Should().Be(expected);
    }

    [Fact]
    public void ResolveToAbsolutePath_KeepsRootedPath()
    {
        string rooted = Path.Combine(Path.GetTempPath(), "sample.txt");

        AgentPathResolver.ResolveToAbsolutePath(rooted, null).Should().Be(Path.GetFullPath(rooted));
    }

    [Fact]
    public void ResolveToAbsolutePath_CombinesWithWorkspaceRoot()
    {
        string root = Path.GetTempPath();

        string resolved = AgentPathResolver.ResolveToAbsolutePath("src/file.cs", root);

        resolved.Should().Be(Path.GetFullPath(Path.Combine(root, "src\\file.cs")));
    }

    [Fact]
    public void ResolveToAbsolutePath_ReturnsRelativePathWhenNoWorkspaceRoot()
    {
        AgentPathResolver.ResolveToAbsolutePath("src/file.cs", null).Should().Be("src/file.cs");
    }
}
