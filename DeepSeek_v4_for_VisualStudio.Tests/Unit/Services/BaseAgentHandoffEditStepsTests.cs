using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.Agents;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// BaseAgent 移交链路的 editSteps 语义测试：
/// ConvertHandoffRequestToHandoff 透传、BuildLightweightPlanFromHandoff 构造规则、
/// ExecuteHandoffAsync 的有效计划优先级（Edit 目标 / 非 Edit 目标 / 原计划优先）。
/// 说明：ExecuteHandoffAsync 会调用目标 Agent 的 ExecuteAsync，
/// 测试通过反射向 AgentFactory 注入 NoOp 目标 Agent，避免触发真实 AI 调用链。
/// </summary>
public class BaseAgentHandoffEditStepsTests
{
    #region Helpers

    /// <summary>创建测试用 agent 实例（NoOp 目标路径下 DeepSeekApiService 不会发起真实请求）。</summary>
    private static EditStepsTestAgent CreateAgent()
        => new(new DeepSeekApiService("test-api-key"));

    /// <summary>
    /// 反射注入 AgentFactory 的懒加载缓存字段（如 "_editAgent"），
    /// 使 GetAgent 直接返回受控的 NoOp 目标 Agent。
    /// </summary>
    private static void InjectPrivateField(AgentFactory factory, string fieldName, BaseAgent agent)
    {
        var field = typeof(AgentFactory).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        field.Should().NotBeNull($"AgentFactory 应包含私有字段 {fieldName}");
        field!.SetValue(factory, agent);
    }

    /// <summary>构造带步骤的标准移交定义（步骤描述默认空字符串，用于验证回退规则）。</summary>
    private static AgentHandoff BuildHandoff(AgentType target, params string[] stepTitles)
    {
        var steps = new List<AgentStep>();
        foreach (var title in stepTitles)
            steps.Add(new AgentStep { Title = title });

        return new AgentHandoff
        {
            Label = "测试移交",
            TargetAgent = target,
            Prompt = "测试移交提示",
            AutoSend = true,
            ShowContinueOn = false,
            EditSteps = steps,
        };
    }

    #endregion

    #region ConvertHandoffRequestToHandoff 透传

    /// <summary>HandoffRequest.EditSteps 原样透传到 AgentHandoff.EditSteps。</summary>
    [Fact]
    public void ConvertHandoffRequestToHandoff_TransfersEditSteps()
    {
        var agent = CreateAgent();
        var request = new HandoffRequest
        {
            SourceAgent = AgentType.Ask,
            TargetAgent = AgentType.Edit,
            Reason = "测试原因",
            TaskDescription = "测试任务",
            EditSteps = new List<AgentStep>
            {
                new() { Index = 1, Title = "步骤一", Description = "描述一" },
                new() { Index = 2, Title = "步骤二", Description = "描述二" },
            },
        };

        var handoff = agent.ExposeConvert(request);

        handoff.TargetAgent.Should().Be(AgentType.Edit);
        var steps = handoff.EditSteps;
        steps.Should().NotBeNull();
        var parsed = steps!;
        parsed.Should().HaveCount(2);
        parsed[0].Title.Should().Be("步骤一");
        parsed[0].Description.Should().Be("描述一");
        parsed[1].Title.Should().Be("步骤二");
        parsed[1].Description.Should().Be("描述二");
    }

    /// <summary>HandoffRequest.GitState 原样透传到 AgentHandoff.GitState。</summary>
    [Fact]
    public void ConvertHandoffRequestToHandoff_TransfersGitState()
    {
        var agent = CreateAgent();
        var request = new HandoffRequest
        {
            SourceAgent = AgentType.Ask,
            TargetAgent = AgentType.Edit,
            Reason = "测试原因",
            TaskDescription = "测试任务",
            GitState = new AgentGitStateSnapshot
            {
                Branch = "master",
                HeadSha = "4c22e6e",
                IsClean = true,
                Refs = new Dictionary<string, string>
                {
                    ["origin/master"] = "4c22e6e",
                },
            },
        };

        var handoff = agent.ExposeConvert(request);

        var gitState = handoff.GitState;
        gitState.Should().NotBeNull();
        gitState!.Branch.Should().Be("master");
        gitState.HeadSha.Should().Be("4c22e6e");
        gitState.IsClean.Should().BeTrue();
        gitState.Refs.Should().HaveCount(1);
        gitState.Refs!["origin/master"].Should().Be("4c22e6e");
    }

    #endregion

    #region BuildLightweightPlanFromHandoff

    /// <summary>
    /// 轻量计划构造：字段映射、Index 重排、空描述回退标题、超 4 项兜底截断。
    /// </summary>
    [Fact]
    public void BuildLightweightPlanFromHandoff_MapsFieldsAndTruncates()
    {
        var handoff = new AgentHandoff
        {
            TargetAgent = AgentType.Edit,
            EditSteps = new List<AgentStep>
            {
                new() { Title = "步骤一", Description = "" },
                new() { Title = "步骤二", Description = "   " },
                new() { Title = "步骤三", Description = "描述三" },
                new() { Title = "步骤四", Description = "描述四" },
                new() { Title = "步骤五", Description = "描述五" },
            },
        };

        var plan = BaseAgent.BuildLightweightPlanFromHandoff(handoff);

        plan.Intent.Should().Be(AgentIntent.CodeChange);
        plan.Title.Should().Be(LocalizationService.Instance["plan.lightweightTitle"]);
        plan.Source.Should().Be(PlanSource.None);
        plan.IsCompleted.Should().BeFalse();
        plan.IsCancelled.Should().BeFalse();
        plan.PlanFilePath.Should().BeNull();
        plan.IsFromPlanAgent.Should().BeFalse();

        plan.Steps.Should().HaveCount(4);
        plan.Steps.Select(s => s.Index).Should().Equal(1, 2, 3, 4);
        plan.Steps.Should().OnlyContain(s => s.Status == AgentStepStatus.Pending && !s.RequiresApproval);
        plan.Steps[0].Description.Should().Be("步骤一");
        plan.Steps[1].Description.Should().Be("步骤二");
        plan.Steps[2].Description.Should().Be("描述三");
    }

    #endregion

    #region ExecuteHandoffAsync 优先级与注入

    /// <summary>Edit 目标 + 无原计划：EditSteps 构造轻量计划写入 context.ActivePlan。</summary>
    [Fact]
    public async Task ExecuteHandoffAsync_EditTargetWithEditSteps_InjectsLightweightPlan()
    {
        var apiService = new DeepSeekApiService("test-api-key");
        var agent = new EditStepsTestAgent(apiService);
        var factory = new AgentFactory(apiService);
        InjectPrivateField(factory, "_editAgent", new NoOpEditAgent(apiService));

        var handoff = BuildHandoff(AgentType.Edit, "步骤一", "步骤二");

        var context = new AgentContext();
        await agent.ExecuteHandoffAsync(handoff, context, null, factory);

        var plan = context.ActivePlan;
        plan.Should().NotBeNull();
        var injected = plan!;
        injected.Intent.Should().Be(AgentIntent.CodeChange);
        injected.Source.Should().Be(PlanSource.None);
        injected.PlanFilePath.Should().BeNull();
        injected.Steps.Should().HaveCount(2);
        injected.Steps.Select(s => s.Index).Should().Equal(1, 2);
        injected.Steps[0].Description.Should().Be("步骤一");
        injected.Title.Should().Be(LocalizationService.Instance["plan.lightweightTitle"]);
        context.IsPlanningMode.Should().BeTrue();
    }

    /// <summary>非 Edit 目标（Ask）+ EditSteps 非空：不构造轻量计划，context.ActivePlan 保持 null。</summary>
    [Fact]
    public async Task ExecuteHandoffAsync_NonEditTarget_DoesNotInjectPlan()
    {
        var apiService = new DeepSeekApiService("test-api-key");
        var agent = new EditStepsTestAgent(apiService);
        var factory = new AgentFactory(apiService);
        InjectPrivateField(factory, "_askAgent", new NoOpAskAgent(apiService));

        var handoff = BuildHandoff(AgentType.Ask, "步骤一");

        var context = new AgentContext();
        await agent.ExecuteHandoffAsync(handoff, context, null, factory);

        context.ActivePlan.Should().BeNull();
        context.IsPlanningMode.Should().BeFalse();
    }

    /// <summary>原计划非 null 且未完成：原计划优先，EditSteps 被忽略。</summary>
    [Fact]
    public async Task ExecuteHandoffAsync_ExistingIncompletePlan_TakesPrecedenceOverEditSteps()
    {
        var apiService = new DeepSeekApiService("test-api-key");
        var agent = new EditStepsTestAgent(apiService);
        var factory = new AgentFactory(apiService);
        InjectPrivateField(factory, "_editAgent", new NoOpEditAgent(apiService));

        var existingPlan = new AgentTaskPlan
        {
            Title = "既有计划",
            Steps = new List<AgentStep> { new() { Index = 1, Title = "既有步骤" } },
        };

        var handoff = BuildHandoff(AgentType.Edit, "轻量步骤");

        var context = new AgentContext();
        await agent.ExecuteHandoffAsync(handoff, context, existingPlan, factory);

        context.ActivePlan.Should().BeSameAs(existingPlan);
    }

    /// <summary>原计划已完成：视为不可用，EditSteps 构造的轻量计划接管 context.ActivePlan。</summary>
    [Fact]
    public async Task ExecuteHandoffAsync_CompletedPlan_ReplacedByLightweightPlan()
    {
        var apiService = new DeepSeekApiService("test-api-key");
        var agent = new EditStepsTestAgent(apiService);
        var factory = new AgentFactory(apiService);
        InjectPrivateField(factory, "_editAgent", new NoOpEditAgent(apiService));

        var completedPlan = new AgentTaskPlan
        {
            Title = "已完成计划",
            IsCompleted = true,
            Steps = new List<AgentStep> { new() { Index = 1, Title = "旧步骤" } },
        };

        var handoff = BuildHandoff(AgentType.Edit, "轻量步骤");

        var context = new AgentContext();
        await agent.ExecuteHandoffAsync(handoff, context, completedPlan, factory);

        var replaced = context.ActivePlan;
        replaced.Should().NotBeNull();
        var current = replaced!;
        current.Should().NotBeSameAs(completedPlan);
        current.Source.Should().Be(PlanSource.None);
        current.Steps.Should().ContainSingle().Which.Title.Should().Be("轻量步骤");
    }

    #endregion

    #region Test doubles

    /// <summary>暴露 BaseAgent 受保护转换方法的测试 Agent（用于透传用例）。</summary>
    private sealed class EditStepsTestAgent : BaseAgent
    {
        public EditStepsTestAgent(DeepSeekApiService apiService)
            : base(apiService, AgentType.Ask)
        {
        }

        /// <summary>暴露 ConvertHandoffRequestToHandoff 供测试调用。</summary>
        public AgentHandoff ExposeConvert(HandoffRequest request)
            => ConvertHandoffRequestToHandoff(request);

        protected override AgentDefinition CreateDefinition(AgentType agentType)
        {
            return new AgentDefinition
            {
                Type = agentType,
                Name = "EditStepsTest",
                AllowedTools = new List<string>(),
                SystemPrompt = "test",
            };
        }

        public override Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context)
            => Task.FromResult(new AgentResult { Success = true, Content = userMessage });
    }

    /// <summary>Ask 目标 NoOp：直接返回结果，避免测试触发真实 AI 调用链。</summary>
    private sealed class NoOpAskAgent : AskAgent
    {
        public NoOpAskAgent(DeepSeekApiService apiService) : base(apiService)
        {
        }

        public override Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context)
            => Task.FromResult(new AgentResult { Success = true, Content = userMessage });
    }

    /// <summary>Edit 目标 NoOp：直接返回结果，避免测试触发真实 AI 调用链。</summary>
    private sealed class NoOpEditAgent : EditAgent
    {
        public NoOpEditAgent(DeepSeekApiService apiService) : base(apiService)
        {
        }

        public override Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context)
            => Task.FromResult(new AgentResult { Success = true, Content = userMessage });
    }

    #endregion
}
