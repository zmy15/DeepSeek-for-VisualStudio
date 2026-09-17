using DeepSeek_v4_for_VisualStudio.Services.IdeContext;
using System;
using System.IO;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services.IdeContext;

public class GitContextProviderTests
{
    [Fact]
    public void BuildPromptBlock_NullState_ReturnsNull()
    {
        new GitContextProvider().BuildPromptBlock(null).Should().BeNull();
    }

    [Fact]
    public void BuildPromptBlock_WithoutRepository_InjectsNotPresent()
    {
        var provider = new GitContextProvider();
        var block = provider.BuildPromptBlock(new GitContextSnapshot
        {
            IsRepository = false,
            RootPath = @"C:\temp\no-repo",
        });

        block.Should().Contain("[Git Context]");
        block.Should().Contain("Git: repository not present");
    }

    [Fact]
    public void BuildPromptBlock_WithRepository_FormatsBranchAndHead()
    {
        var provider = new GitContextProvider();
        var block = provider.BuildPromptBlock(new GitContextSnapshot
        {
            IsRepository = true,
            Branch = "feature/test",
            HeadSummary = "4c22e6e fix: sample change",
        });

        block.Should().Contain("[Git Context]");
        block.Should().Contain("Git Branch: feature/test");
        block.Should().Contain("Git HEAD: 4c22e6e fix: sample change");
    }

    [Fact]
    public void Capture_WithoutRepository_MarksNotRepository()
    {
        string tempDir = Path.Combine(
            Path.GetTempPath(), $"git-context-no-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var state = new GitContextProvider().Capture(tempDir);

            state.IsRepository.Should().BeFalse();
            state.Branch.Should().BeNull();
            state.HeadSummary.Should().BeNull();
        }
        finally
        {
            DeleteDirectoryWithRetry(tempDir);
        }
    }

    [Fact]
    public void Capture_WithRepository_UsesProvidedGitRunner()
    {
        string repoDir = Path.Combine(
            Path.GetTempPath(), $"git-context-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));

        try
        {
            var provider = new GitContextProvider((_, arguments) => arguments switch
            {
                "symbolic-ref --short -q HEAD" => "feature/test",
                "rev-parse --short HEAD" => "abc1234",
                "log -1 --format=\"%h %s\"" => "4c22e6e initial commit",
                _ => null,
            });

            var state = provider.Capture(repoDir);

            state.IsRepository.Should().BeTrue();
            state.RootPath.Should().Be(repoDir);
            state.Branch.Should().Be("feature/test");
            state.HeadSummary.Should().Be("4c22e6e initial commit");
        }
        finally
        {
            DeleteDirectoryWithRetry(repoDir);
        }
    }

    [Fact]
    public void Capture_WithDetachedHead_ReportsDetachedSha()
    {
        string repoDir = Path.Combine(
            Path.GetTempPath(), $"git-context-detached-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));

        try
        {
            var provider = new GitContextProvider((_, arguments) => arguments switch
            {
                "symbolic-ref --short -q HEAD" => null,
                "rev-parse --short HEAD" => "abc1234",
                "log -1 --format=\"%h %s\"" => "abc1234 detached change",
                _ => null,
            });

            var state = provider.Capture(repoDir);

            state.IsRepository.Should().BeTrue();
            state.Branch.Should().Be("detached HEAD at abc1234");
            state.HeadSummary.Should().Be("abc1234 detached change");
        }
        finally
        {
            DeleteDirectoryWithRetry(repoDir);
        }
    }

    [Fact]
    public void Capture_WithSolutionPathInsideRepository_ResolvesGitRoot()
    {
        string repoDir = Path.Combine(
            Path.GetTempPath(), $"git-context-sln-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        string slnPath = Path.Combine(repoDir, "Sample.slnx");
        File.WriteAllText(slnPath, "solution");

        try
        {
            var provider = new GitContextProvider((_, arguments) => arguments switch
            {
                "symbolic-ref --short -q HEAD" => "master",
                "rev-parse --short HEAD" => "4c22e6e",
                "log -1 --format=\"%h %s\"" => "4c22e6e sln initial",
                _ => null,
            });

            var state = provider.Capture(slnPath);

            state.IsRepository.Should().BeTrue();
            state.RootPath.Should().Be(repoDir);
            state.Branch.Should().Be("master");
            state.HeadSummary.Should().Be("4c22e6e sln initial");
        }
        finally
        {
            DeleteDirectoryWithRetry(repoDir);
        }
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        for (int i = 0; i < 20; i++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
                return;
            }
            catch (IOException)
            {
                System.Threading.Thread.Sleep(100);
            }
        }

        if (Directory.Exists(path))
            Directory.Delete(path, true);
    }
}
