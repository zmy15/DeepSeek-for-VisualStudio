using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.View;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.View;

public class HandoffResultFinalizationTests
{
    [Fact]
    public void AskSummaryWithoutPlan_RequiresFinalization()
    {
        var result = new AgentResult
        {
            Success = true,
            Content = "变更总结正文",
            Plan = null,
        };

        DeepSeekChatControl.ShouldFinalizeHandoffContent(result).Should().BeTrue();
    }

    [Fact]
    public void EmptyOrFailedResult_DoesNotRequireFinalization()
    {
        DeepSeekChatControl.ShouldFinalizeHandoffContent(new AgentResult
        {
            Success = true,
            Content = " ",
        }).Should().BeFalse();

        DeepSeekChatControl.ShouldFinalizeHandoffContent(new AgentResult
        {
            Success = false,
            Content = "失败说明",
        }).Should().BeFalse();
    }
}
