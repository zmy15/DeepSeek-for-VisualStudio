using DeepSeek_v4_for_VisualStudio.Services.Agents;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class AgentExecutionGuardTests
{
    [Fact]
    public void CheckBeforeStep_MaxSteps_Stops()
    {
        var guard = new AgentExecutionGuard(
            new AgentExecutionPolicy { MaxSteps = 2 },
            executionDepth: 0);

        guard.CheckBeforeStep(2).ShouldStop.Should().BeFalse();

        var result = guard.CheckBeforeStep(3);

        result.ShouldStop.Should().BeTrue();
        result.Reason.Should().Be(AgentExecutionStopReason.MaxSteps);
    }

    [Fact]
    public void CheckBeforeStep_MaxDepth_Stops()
    {
        var guard = new AgentExecutionGuard(
            new AgentExecutionPolicy { MaxExecutionDepth = 2 },
            executionDepth: 3);

        var result = guard.CheckBeforeStep(1);

        result.ShouldStop.Should().BeTrue();
        result.Reason.Should().Be(AgentExecutionStopReason.MaxDepth);
    }

    [Fact]
    public void RecordUsage_TokenBudget_Stops()
    {
        var guard = new AgentExecutionGuard(
            new AgentExecutionPolicy { MaxTotalTokens = 10 },
            executionDepth: 0);

        guard.RecordUsage(5, 4).ShouldStop.Should().BeFalse();

        var result = guard.RecordUsage(2, 0);

        result.ShouldStop.Should().BeTrue();
        result.Reason.Should().Be(AgentExecutionStopReason.TokenBudget);
    }

    [Fact]
    public void RecordUsage_DefaultTokenBudget_IsUnlimited()
    {
        var guard = new AgentExecutionGuard(
            new AgentExecutionPolicy(),
            executionDepth: 0);

        guard.RecordUsage(1_000_000, 1_000_000).ShouldStop.Should().BeFalse();
    }

    [Fact]
    public void RecordToolCalls_ToolBudget_Stops()
    {
        var guard = new AgentExecutionGuard(
            new AgentExecutionPolicy { MaxToolCalls = 3 },
            executionDepth: 0);

        guard.RecordToolCalls(2).ShouldStop.Should().BeFalse();

        var result = guard.RecordToolCalls(2);

        result.ShouldStop.Should().BeTrue();
        result.Reason.Should().Be(AgentExecutionStopReason.ToolCallBudget);
    }

    [Fact]
    public void RecordState_RepeatedFingerprint_StopsForNoProgress()
    {
        var guard = new AgentExecutionGuard(
            new AgentExecutionPolicy { MaxNoProgressRounds = 3 },
            executionDepth: 0);

        guard.RecordState(new[] { "read_file", "same result" }).ShouldStop.Should().BeFalse();
        guard.RecordState(new[] { "read_file", "same result" }).ShouldStop.Should().BeFalse();

        var result = guard.RecordState(new[] { "read_file", "same result" });

        result.ShouldStop.Should().BeTrue();
        result.Reason.Should().Be(AgentExecutionStopReason.NoProgress);
    }

    [Fact]
    public void GetBudgetWarning_AtEightyPercent_ReturnsWarningOnce()
    {
        var guard = new AgentExecutionGuard(
            new AgentExecutionPolicy { MaxSteps = 10 },
            executionDepth: 0);

        guard.GetBudgetWarning(7).Should().BeNull();
        guard.GetBudgetWarning(8).Should().NotBeNull();
        guard.GetBudgetWarning(9).Should().BeNull();
    }
}
