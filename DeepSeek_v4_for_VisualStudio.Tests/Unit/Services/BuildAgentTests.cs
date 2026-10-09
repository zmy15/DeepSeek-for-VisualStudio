using DeepSeek_v4_for_VisualStudio.Services.Agents;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class BuildAgentTests
{
    [Fact]
    public void AllowedTools_ContainsDirectSearchTools()
    {
        var agent = new BuildAgent(new DeepSeekApiService("test-api-key"));

        agent.Definition.AllowedTools.Should().Contain("file_search");
        agent.Definition.AllowedTools.Should().Contain("grep_search");
        agent.Definition.AllowedTools.Should().Contain("list_dir");
    }

    [Fact]
    public void SystemPrompt_UsesSingleBuildWorkflow()
    {
        var agent = new BuildAgent(new DeepSeekApiService("test-api-key"));

        agent.Definition.SystemPrompt.Should().Contain("build_solution");
        agent.Definition.SystemPrompt.Should().Contain(
            global::DeepSeek_v4_for_VisualStudio.Services.AiPrompts.AgentConclusionStopRule);
        agent.Definition.SystemPrompt.Should().NotContain("build_solution 是异步的");
        agent.Definition.SystemPrompt.Should().NotContain("build_solution is async");
        agent.Definition.SystemPrompt.Should().NotContain("Turn 2");
    }

    [Fact]
    public void SystemPrompt_ContainsNoRebuildRule()
    {
        var agent = new BuildAgent(new DeepSeekApiService("test-api-key"));

        agent.Definition.SystemPrompt.Should().Contain(
            global::DeepSeek_v4_for_VisualStudio.Services.LocalizationService.Instance[
                "system.agent.buildNoRebuildRule"]);
    }

    [Fact]
    public void CreateAskSummaryHandoff_MarksSummaryAsTerminal()
    {
        var handoff = BuildAgent.CreateAskSummaryHandoff("summary");

        handoff.TargetAgent.Should().Be(AgentType.Ask);
        handoff.IsSummaryOnly.Should().BeTrue();
        handoff.AutoSend.Should().BeTrue();
        handoff.ShowContinueOn.Should().BeFalse();
        handoff.Prompt.Should().Be("summary");
    }

    [Fact]
    public void AppendBuildResult_IncludesFinalToolResult()
    {
        var prompt = BuildAgent.AppendBuildResult("summary", "Build succeeded");

        prompt.Should().Contain("summary");
        prompt.Should().Contain("本次构建结果");
        prompt.Should().Contain("Build succeeded");
    }

    [Fact]
    public void AppendBuildResult_TruncatesLongOutput()
    {
        string longOutput = new string('E', 10000);
        var prompt = BuildAgent.AppendBuildResult("summary", longOutput);

        prompt.Should().Contain("summary");
        prompt.Should().Contain("本次构建结果");
        prompt.Should().Contain("已截断");
        prompt.Length.Should().BeLessThan(5000);
    }

    /// <summary>
    /// 构建任务是「当前任务目标」：该 user 由 BuildContextAwareMessages 追加
    /// （deduplicateCurrentUser: false），不经过 Handoff 分支的前缀逻辑，
    /// 必须由 BuildEnhancedUserMessage 自身打标。
    /// </summary>
    [Fact]
    public void BuildEnhancedUserMessage_CarriesCurrentTaskGoalPrefix()
    {
        var agent = new BuildAgent(new DeepSeekApiService("test-api-key"));
        var method = typeof(BuildAgent).GetMethod(
            "BuildEnhancedUserMessage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        string result = (string)method!.Invoke(agent, new object[] { "修复编译错误", new AgentContext() })!;

        string prefix = global::DeepSeek_v4_for_VisualStudio.Services.LocalizationService.Instance[
            "system.agent.handoffTaskGoalPrefix"];
        result.Should().Contain(prefix + "修复编译错误");
        // 前缀只应出现一次，不得叠加
        result.Split(new[] { prefix }, StringSplitOptions.None).Length.Should().Be(2);
    }
}
