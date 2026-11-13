using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.Agents;
using FluentAssertions;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 验证工具循环的测试/构建结果分类与终止判定。
/// </summary>
public class BaseAgentVerificationTests
{
    [Fact]
    public void ClassifyVerificationRound_RecognizesExecutedPassingTests()
    {
        var status = BaseAgent.ClassifyVerificationRound(
            new[] { ToolCallNamed("run_tests") },
            new[] { "total=3 passed=3 failed=0 skipped=0 elapsed=1.0s" });

        status.Should().HaveFlag(BaseAgent.VerificationRoundStatus.TestsPassed);
        status.Should().NotHaveFlag(BaseAgent.VerificationRoundStatus.TestsFailed);
    }

    [Fact]
    public void ClassifyVerificationRound_ZeroTestsIsNotSuccess()
    {
        var status = BaseAgent.ClassifyVerificationRound(
            new[] { ToolCallNamed("run_tests") },
            new[] { "total=0 passed=0 failed=0 skipped=0 elapsed=0.1s" });

        status.Should().Be(BaseAgent.VerificationRoundStatus.None);
    }

    [Fact]
    public void ClassifyVerificationRound_TracksFailedTestsAndPassingBuild()
    {
        var status = BaseAgent.ClassifyVerificationRound(
            new[] { ToolCallNamed("run_tests"), ToolCallNamed("build_solution") },
            new[]
            {
                "total=2 passed=1 failed=1 skipped=0 elapsed=1.0s",
                "Build succeeded",
            });

        status.Should().HaveFlag(BaseAgent.VerificationRoundStatus.TestsFailed);
        status.Should().HaveFlag(BaseAgent.VerificationRoundStatus.BuildPassed);
    }

    [Fact]
    public void DecideVerificationLoopTermination_TestSuccessClearsEarlierFailure()
    {
        var decision = BaseAgent.DecideVerificationLoopTermination(
            BaseAgent.VerificationRoundStatus.TestsPassed,
            hasUnresolvedTestFailure: true);

        decision.ShouldTerminate.Should().BeTrue();
        decision.HasUnresolvedTestFailure.Should().BeFalse();
        decision.PreferredStatus.Should().Be(BaseAgent.VerificationRoundStatus.TestsPassed);
    }

    [Fact]
    public void DecideVerificationLoopTermination_BuildSuccessCannotOverrideFailedTests()
    {
        var decision = BaseAgent.DecideVerificationLoopTermination(
            BaseAgent.VerificationRoundStatus.TestsFailed
                | BaseAgent.VerificationRoundStatus.BuildPassed,
            hasUnresolvedTestFailure: false);

        decision.ShouldTerminate.Should().BeFalse();
        decision.HasUnresolvedTestFailure.Should().BeTrue();
    }

    [Fact]
    public void DecideVerificationLoopTermination_LaterBuildStillCannotOverrideUnresolvedTests()
    {
        var decision = BaseAgent.DecideVerificationLoopTermination(
            BaseAgent.VerificationRoundStatus.BuildPassed,
            hasUnresolvedTestFailure: true);

        decision.ShouldTerminate.Should().BeFalse();
        decision.HasUnresolvedTestFailure.Should().BeTrue();
    }

    [Fact]
    public void DecideVerificationLoopTermination_TestSuccessWithFailedBuildDoesNotTerminate()
    {
        var decision = BaseAgent.DecideVerificationLoopTermination(
            BaseAgent.VerificationRoundStatus.TestsPassed
                | BaseAgent.VerificationRoundStatus.BuildFailed,
            hasUnresolvedTestFailure: false);

        decision.ShouldTerminate.Should().BeFalse();
    }

    private static ToolCall ToolCallNamed(string name)
    {
        return new ToolCall
        {
            Id = "call_" + name,
            Function = new ToolCallFunction { Name = name },
        };
    }
}
