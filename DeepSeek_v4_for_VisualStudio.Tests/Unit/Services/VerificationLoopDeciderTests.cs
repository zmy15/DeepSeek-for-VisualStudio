using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.Agents;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class VerificationLoopDeciderTests
{
    [Fact]
    public void FindVerificationResultIndex_PrefersTestsWhenTestsPassed()
    {
        var calls = new List<ToolCall>
        {
            CreateCall("build_solution"),
            CreateCall("run_tests"),
        };

        int index = VerificationLoopDecider.FindVerificationResultIndex(
            calls, BaseAgent.VerificationRoundStatus.TestsPassed);

        index.Should().Be(1);
    }

    [Fact]
    public void FindVerificationResultIndex_FallsBackToBuildTool()
    {
        var calls = new List<ToolCall>
        {
            CreateCall("run_tests"),
            CreateCall("build_solution"),
        };

        int index = VerificationLoopDecider.FindVerificationResultIndex(
            calls, BaseAgent.VerificationRoundStatus.BuildPassed);

        index.Should().Be(1);
    }

    [Fact]
    public void FindVerificationResultIndex_ReturnsMinusOneWhenAbsent()
    {
        var calls = new List<ToolCall> { CreateCall("read_file") };

        VerificationLoopDecider.FindVerificationResultIndex(
            calls, BaseAgent.VerificationRoundStatus.TestsPassed).Should().Be(-1);
    }

    [Fact]
    public void DecideVerificationLoopTermination_TerminatesOnCleanTestSuccess()
    {
        var (shouldTerminate, hasUnresolvedTestFailure, preferred) =
            VerificationLoopDecider.DecideVerificationLoopTermination(
                BaseAgent.VerificationRoundStatus.TestsPassed, false);

        shouldTerminate.Should().BeTrue();
        hasUnresolvedTestFailure.Should().BeFalse();
        preferred.Should().Be(BaseAgent.VerificationRoundStatus.TestsPassed);
    }

    [Fact]
    public void DecideVerificationLoopTermination_KeepsRunningAfterTestFailure()
    {
        var (shouldTerminate, hasUnresolvedTestFailure, _) =
            VerificationLoopDecider.DecideVerificationLoopTermination(
                BaseAgent.VerificationRoundStatus.TestsFailed, false);

        shouldTerminate.Should().BeFalse();
        hasUnresolvedTestFailure.Should().BeTrue();
    }

    [Fact]
    public void DecideVerificationLoopTermination_BuildSuccessDoesNotOverrideUnresolvedTestFailure()
    {
        var (shouldTerminate, hasUnresolvedTestFailure, _) =
            VerificationLoopDecider.DecideVerificationLoopTermination(
                BaseAgent.VerificationRoundStatus.BuildPassed, true);

        shouldTerminate.Should().BeFalse();
        hasUnresolvedTestFailure.Should().BeTrue();
    }

    private static ToolCall CreateCall(string name)
        => new() { Function = new ToolCallFunction { Name = name, Arguments = "{}" } };
}
