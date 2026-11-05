using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Models;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 计划执行按钮的渲染位置回归测试。
/// </summary>
public class ChatHtmlServiceHandoffButtonTests
{
    /// <summary>
    /// 执行按钮必须位于计划面板内，而不是消息气泡下方。
    /// </summary>
    [Fact]
    public void BuildHandoffButtonJs_InjectsButtonIntoTaskPanel()
    {
        string js = ChatHtmlService.BuildHandoffButtonJs(7, "Edit", "执行当前计划");

        js.Should().Contain("agent-task-panel-");
        js.Should().Contain("agent-task-panel-actions");
        js.Should().Contain("actions.appendChild(btn)");
        js.Should().Contain("data-plan-source");
        js.Should().Contain("!=='PlanAgent'");
        js.Should().Contain("btn.textContent=\"执行当前计划\"");
        js.Should().NotContain("msg-body-7");
    }

    /// <summary>
    /// 计划面板必须记录来源，供执行按钮判断是否只属于 Plan Agent。
    /// </summary>
    [Fact]
    public void BuildAgentTaskPanelCreateJs_RecordsPlanSource()
    {
        var plan = new AgentTaskPlan
        {
            PlanId = "edit-plan",
            Title = "Edit 自动拆分计划",
            Source = PlanSource.EditAgent,
        };
        plan.Steps.Add(new AgentStep { Index = 1, Title = "步骤一" });

        string js = ChatHtmlService.BuildAgentTaskPanelCreateJs(plan);

        js.Should().Contain("panel.setAttribute('data-plan-source',\"EditAgent\")");
    }

    /// <summary>
    /// 输入“开始”时，计划面板按钮也必须切换为执行中状态。
    /// </summary>
    [Fact]
    public void BuildSetHandoffButtonsExecutingJs_DisablesButtonsAndUpdatesLabel()
    {
        string js = ChatHtmlService.BuildSetHandoffButtonsExecutingJs("执行中...");

        js.Should().Contain("querySelectorAll('.handoff-btn')");
        js.Should().Contain("disabled=true");
        js.Should().Contain("textContent=\"执行中...\"");
    }

    /// <summary>
    /// 任务结束后应移除按钮及空的操作区，避免面板残留“执行中...”状态。
    /// </summary>
    [Fact]
    public void BuildRemoveHandoffButtonsJs_RemovesButtonsAndEmptyActionArea()
    {
        string js = ChatHtmlService.BuildRemoveHandoffButtonsJs();

        js.Should().Contain("querySelectorAll('.handoff-btn')");
        js.Should().Contain("querySelectorAll('.agent-task-panel-actions')");
        js.Should().Contain("actions[j].parentNode.removeChild(actions[j])");
    }
}
