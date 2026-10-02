using DeepSeek_v4_for_VisualStudio.Services.Agents;
using System.Collections.Generic;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// EditAgent 单元测试 — 测试 Agent 定义、工具集、ExploreAgent 事件转发、计划管理。
/// 不测试完整 ExecuteAsync 流程（需要 mock HTTP 流）。
/// </summary>
public class EditAgentTests
{
    private readonly DeepSeekApiService _apiService;

    public EditAgentTests()
    {
        _apiService = new DeepSeekApiService("test-api-key");
    }

    #region Constructor

    [Fact]
    public void Constructor_WithApiService_CreatesSuccessfully()
    {
        var agent = new EditAgent(_apiService);

        agent.Should().NotBeNull();
        agent.Definition.Should().NotBeNull();
        agent.Definition.Type.Should().Be(AgentType.Edit);
    }

    [Fact]
    public void Constructor_WithNullApiService_ThrowsArgumentNullException()
    {
        Action act = () => new EditAgent(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(".", false)]
    [InlineData("无需修改", true)]
    [InlineData("No changes needed", true)]
    public void IsNoChangesResponse_RequiresExplicitNoChangeText(string response, bool expected)
    {
        var method = typeof(EditAgent).GetMethod(
            "IsNoChangesResponse",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull();

        bool result = (bool)method!.Invoke(null, new object[] { response })!;

        result.Should().Be(expected);
    }

    [Fact]
    public void ExtractToolMadeEdits_ApplyPatch_UsesPatchHeaderPath()
    {
        const string patch = "*** Begin Patch\n*** Update File: README.md\n@@\n-old\n+new\n*** End Patch";
        string arguments = System.Text.Json.JsonSerializer.Serialize(new
        {
            patch,
            expected = "1: new"
        });
        var messages = new List<ChatApiMessage>
        {
            new()
            {
                Role = "assistant",
                ToolCalls = new List<ToolCall>
                {
                    new()
                    {
                        Id = "call_apply_patch",
                        Function = new ToolCallFunction
                        {
                            Name = "apply_patch",
                            Arguments = arguments
                        }
                    }
                }
            }
        };
        var method = typeof(EditAgent).GetMethod(
            "ExtractToolMadeEdits",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var edits = (List<(string FilePath, string ToolName)>)method!.Invoke(
            null, new object[] { messages })!;

        edits.Should().ContainSingle();
        edits[0].FilePath.Should().Be("README.md");
        edits[0].ToolName.Should().Be("apply_patch");
    }

    [Fact]
    public void BuildPlanProgressSnapshot_ListsStepsAndMarksCurrent()
    {
        var plan = new AgentTaskPlan
        {
            CurrentStepIndex = 2,
            Steps = new List<AgentStep>
            {
                new() { Index = 1, Title = "提交修复", Status = AgentStepStatus.Completed },
                new() { Index = 2, Title = "合并到 dev", Status = AgentStepStatus.InProgress },
                new() { Index = 3, Title = "验证报告", Status = AgentStepStatus.Pending },
            },
        };

        var snapshot = EditAgent.BuildPlanProgressSnapshot(plan);

        snapshot.Should().Contain("## 计划进度");
        snapshot.Should().Contain("步骤 1: 提交修复");
        snapshot.Should().Contain("已完成");
        snapshot.Should().Contain("步骤 2: 合并到 dev");
        snapshot.Should().Contain("▶ 当前");
        snapshot.Should().Contain("步骤 3: 验证报告");
        snapshot.Should().Contain("待执行");
    }

    [Fact]
    public void BuildPlanProgressSnapshot_EmptyPlan_ReturnsEmpty()
    {
        EditAgent.BuildPlanProgressSnapshot(new AgentTaskPlan()).Should().BeEmpty();
    }

    [Fact]
    public void TruncateBuildResultForHandoff_ShortKeepsFull()
    {
        var result = EditAgent.TruncateBuildResultForHandoff("构建成功，0 个错误");
        result.Should().Be("构建成功，0 个错误");
    }

    [Fact]
    public void TruncateBuildResultForHandoff_LongKeepsHeadTailAndNote()
    {
        string longOutput = new string('E', 10000);
        var result = EditAgent.TruncateBuildResultForHandoff(longOutput);

        result.Should().Contain("已截断");
        result.Should().StartWith(new string('E', 3000));
        result.Should().EndWith(new string('E', 5000));
        result.Length.Should().BeLessThan(9000);
    }

    [Theory]
    [InlineData(true, false, true)]   // 有编译问题且未禁用自动构建 → 显示“正在修复”
    [InlineData(false, false, false)] // 无编译问题 → 正常完成
    [InlineData(true, true, false)]   // 用户/设置禁用自动构建 → 正常完成
    public void RequiresBuildRepairToast_MatchesHandoffToBuild(bool hasBuildWarnings, bool skipAutoBuild, bool expected)
    {
        EditAgent.RequiresBuildRepairToast(hasBuildWarnings, skipAutoBuild).Should().Be(expected);
    }

    [Fact]
    public void ShouldSkipAskSummaryHandoff_CodeChangeWithoutChanges_ReturnsFalse()
    {
        // 普通代码修改任务即使没有追踪到文件变更（如纯 Git/终端操作），仍必须移交 Ask 出总结。
        var plan = new AgentTaskPlan
        {
            Intent = AgentIntent.CodeChange,
            Steps = new List<AgentStep>
            {
                new() { Index = 1, Title = "提交合并", Status = AgentStepStatus.Completed },
            },
        };

        EditAgent.ShouldSkipAskSummaryHandoff(plan, hasBuildWarnings: false).Should().BeFalse();
    }

    [Fact]
    public void ShouldSkipAskSummaryHandoff_ReadOnlyOutputWithoutChanges_ReturnsTrue()
    {
        var plan = new AgentTaskPlan
        {
            Intent = AgentIntent.QandA,
            Steps = new List<AgentStep>
            {
                new() { Index = 1, Title = "执行只读命令", Status = AgentStepStatus.Completed },
            },
        };

        EditAgent.ShouldSkipAskSummaryHandoff(plan, hasBuildWarnings: false).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]  // 有变更 → 不跳过
    [InlineData(false)] // 有构建警告 → 不跳过
    public void ShouldSkipAskSummaryHandoff_ChangesOrWarnings_ReturnsFalse(bool withChanges)
    {
        var plan = new AgentTaskPlan
        {
            Intent = AgentIntent.QandA,
            Steps = new List<AgentStep>
            {
                new() { Index = 1, Title = "任务", Status = AgentStepStatus.Completed },
            },
        };
        if (withChanges)
        {
            plan.ChangedFiles.Add(new FileChangeSummary
            {
                FilePath = "F:\\repo\\Program.cs",
                LinesAdded = 1,
            });
        }

        EditAgent.ShouldSkipAskSummaryHandoff(plan, hasBuildWarnings: !withChanges).Should().BeFalse();
    }

    #endregion

    [Theory]
    [InlineData(true, true, true, false, false)]   // 构建成功 → 不移交 Build
    [InlineData(true, true, false, false, true)]   // 构建失败 → 移交 Build
    [InlineData(true, false, null, false, true)]   // 修改文件但未构建 → 移交 Build
    [InlineData(false, false, null, false, false)] // 仅 Git/终端，无文件变更 → 不移交 Build
    [InlineData(false, true, false, false, false)] // 无文件变更即使构建失败 → 不移交 Build
    [InlineData(true, false, null, true, false)]   // 用户/设置禁用构建 → 不移交 Build
    public void ShouldHandoffToBuild_MatchesEditCompletionRules(
        bool hasFileChanges,
        bool didAttemptBuild,
        bool? lastBuildSucceeded,
        bool skipAutoBuild,
        bool expected)
    {
        bool result = EditAgent.ShouldHandoffToBuild(
            hasFileChanges,
            didAttemptBuild,
            lastBuildSucceeded,
            skipAutoBuild);

        result.Should().Be(expected);
    }

    [Fact]
    public async Task ExecutePlanAsync_WithCancelledContext_MarksPlanCancelledWithoutExecutingStep()
    {
        var agent = new EditAgent(_apiService);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var plan = new AgentTaskPlan
        {
            Title = "Cancellation test",
            Steps =
            {
                new AgentStep
                {
                    Index = 1,
                    Title = "Step 1",
                    Description = "Should not execute",
                },
            },
        };
        var context = new AgentContext
        {
            CancellationToken = cts.Token,
        };

        await agent.ExecutePlanAsync(plan, context);

        plan.IsCancelled.Should().BeTrue();
        plan.IsCompleted.Should().BeFalse();
        plan.Steps.Should().ContainSingle()
            .Which.Status.Should().Be(AgentStepStatus.Pending);
    }

    [Fact]
    public async Task ExecutePlanAsync_MultiStepPlan_DropsHandoffPrefixWhenFullHistoryExists()
    {
        var agent = new EditAgent(_apiService);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var contextManager = new ConversationContextManager();
        contextManager.AddUserMessage("用户任务");
        var context = new AgentContext
        {
            ContextManager = contextManager,
            ForwardedMessages = new List<ChatApiMessage>
            {
                new() { Role = "system", Content = "compact handoff prefix" },
            },
            CancellationToken = cts.Token,
        };

        var plan = new AgentTaskPlan
        {
            Title = "Multi-step test",
            Steps =
            {
                new AgentStep { Index = 1, Title = "步骤 1", Description = "第一项" },
                new AgentStep { Index = 2, Title = "步骤 2", Description = "第二项" },
            },
        };

        await agent.ExecutePlanAsync(plan, context);

        context.ForwardedMessages.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WithCancelledPlan_DoesNotCreateFollowUpHandoff()
    {
        var agent = new EditAgent(_apiService);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var plan = new AgentTaskPlan
        {
            Title = "Cancellation handoff test",
            Source = PlanSource.PlanAgent,
            Steps =
            {
                new AgentStep
                {
                    Index = 1,
                    Title = "Step 1",
                    Description = "Should not execute",
                },
            },
        };
        var context = new AgentContext
        {
            ActivePlan = plan,
            CancellationToken = cts.Token,
        };

        var result = await agent.ExecuteAsync("Execute the plan", context);

        result.Success.Should().BeTrue();
        result.Plan.Should().BeSameAs(plan);
        result.Handoff.Should().BeNull();
    }

    #region Agent Definition

    [Fact]
    public void Definition_Name_IsEdit()
    {
        var agent = new EditAgent(_apiService);

        agent.Definition.Name.Should().Be("Edit");
    }

    [Fact]
    public void Definition_SystemPrompt_IsNotEmpty()
    {
        var agent = new EditAgent(_apiService);

        agent.Definition.SystemPrompt.Should().NotBeNullOrEmpty();
        agent.Definition.SystemPrompt.Should().Contain("Edit");
        agent.Definition.SystemPrompt.Should().Contain(
            global::DeepSeek_v4_for_VisualStudio.Services.AiPrompts.AgentConclusionStopRule);
        // 编辑工具调用细则已下沉到各工具的 description（apply_patch / replace_string_in_file 等），
        // Edit 专属提示词只保留「必须通过真实工具调用完成修改」这一行为约束。
        agent.Definition.SystemPrompt.Should().Contain("编辑工具");
        global::DeepSeek_v4_for_VisualStudio.Services.AiPrompts.EditSystemPromptFragment
            .Should().Contain("apply_patch")
            .And.Contain("replace_string_in_file")
            .And.Contain("delete_file")
            .And.Contain("工具会返回删除结果")
            .And.NotContain("```file:");
        // 「终态」「不要再次读取」等编辑工具幂等性说明改由工具描述承载。
        global::DeepSeek_v4_for_VisualStudio.Services.LocalizationService.Instance["tool.replace_string_in_file.desc"]
            .Should().Contain("oldString");
        global::DeepSeek_v4_for_VisualStudio.Services.AiPrompts.AgentConclusionStopRule
            .Should().Contain("不要质疑用户给出的明确操作");
    }

    #endregion

    #region StepTools Whitelist

    [Fact]
    public void StepTools_AreUnifiedAcrossAllStepTypes()
    {
        var field = typeof(EditAgent).GetField(
            "StepTools",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var tools = (string[])field!.GetValue(null)!;

        tools.Should().Contain("file_search");
        tools.Should().Contain("grep_search");
        tools.Should().Contain("symbol_search");
        tools.Should().Contain("list_dir");
        tools.Should().Contain("run_in_terminal");
        tools.Should().Contain("get_terminal_output");
        tools.Should().Contain("VisualStudio_askQuestions");
        tools.Should().Contain("build_solution");
        tools.Should().Contain("create_file");
        tools.Should().NotContain("request_handoff");
        tools.Should().NotContain("edit_notebook_file");

        var agent = new EditAgent(_apiService);
        agent.Definition.AllowedTools.Should().BeEquivalentTo(tools);
    }

    [Fact]
    public void EditAgent_DoesNotDefineUnusedExplorationTools()
    {
        var field = typeof(EditAgent).GetField(
            "ExplorationTools",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        field.Should().BeNull();
    }

    [Fact]
    public void BuildStepPrompt_ContainsOnlyCurrentStepAndPlanSection()
    {
        var agent = new EditAgent(_apiService);
        var plan = new AgentTaskPlan
        {
            Title = "Implement feature",
            TaskDescription = "## 背景\n这是一段很长的移交任务描述，步骤推进时不应逐条重发。",
            Steps =
            {
                new AgentStep
                {
                    Index = 1,
                    Title = "创建 test.txt 文件",
                    Description = "在项目根目录创建测试文件。"
                }
            }
        };
        var method = typeof(EditAgent).GetMethod(
            "BuildStepPrompt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var prompt = (string)method!.Invoke(agent, new object[]
        {
            plan.Steps[0], plan, new AgentContext()
        })!;

        // 只保留：plan 标题前缀 + 当前步骤标题 + plan.md 章节 + 复用历史约束
        prompt.Should().Contain("创建 test.txt 文件");
        prompt.Should().Contain("1/1");

        // 代码记忆移除后，跨步骤文件内容只能靠对话历史承接，必须显式提示复用
        prompt.Should().Contain("优先复用对话历史中已读取的文件内容");

        // 精简后不再逐步骤重发的冗余块（代码记忆功能已移除）
        prompt.Should().NotContain("## 任务描述（Handoff 携带，必须严格按此执行）");
        prompt.Should().NotContain("这是一段很长的移交任务描述");
        prompt.Should().NotContain("代码记忆");
        prompt.Should().NotContain("## 前面步骤的缓存文件内容");
        prompt.Should().NotContain("## 前面步骤的执行结果");
        prompt.Should().NotContain("## 计划进度");
        prompt.Should().NotContain("## 统一执行规则");
        prompt.Should().NotContain("## 重要提示");
        prompt.Should().NotContain("## 代码修改步骤");
    }

    [Fact]
    public void SystemPrompt_DeclaresSubsequentStepCompletionRule()
    {
        // 步骤提示精简后，"顺带完成后续步骤需声明" 规则移至常驻 system 提示词，
        // 否则 DetectAndAutoCompleteLaterSteps 失去触发来源。
        var agent = new EditAgent(_apiService);

        agent.Definition.SystemPrompt.Should().Contain("也完成了步骤X、Y");
        agent.Definition.SystemPrompt.Should().Contain("also completed step X, Y");
    }

    #endregion

    #region Build Permission — 步骤级构建许可

    /// <summary>中间步骤 + 文本未要求构建 → 不允许构建。</summary>
    [Fact]
    public void IsBuildAllowedForStep_MiddleStepWithoutBuildIntent_IsBlocked()
    {
        var plan = BuildPlan(
            (1, "修改 Settings 页", "调整属性定义"),
            (2, "更新本地化键", "补充字符串"),
            (3, "同步测试并构建", "更新测试"));   // 最后一步带构建意图

        bool allowed = EditAgent.IsBuildAllowedForStep(plan.Steps[0], plan, out string? reason);

        allowed.Should().BeFalse();
        reason.Should().BeNull();
    }

    /// <summary>步骤标题明确要求构建 → 允许，即使不是最后一步。</summary>
    /// <remarks>
    /// 判据只看 Title。此前的用例把构建词放在 Description（如 "步骤 2" / "同步测试并构建"），
    /// 那是改版前的契约；现由
    /// <see cref="IsBuildAllowedForStep_BuildWordOnlyInDescription_IsBlocked"/> 反向覆盖。
    /// </remarks>
    [Theory]
    [InlineData("同步测试并构建", "执行构建")]
    [InlineData("构建验证", "运行一次编译")]
    [InlineData("Build and verify", "compile the solution")]
    [InlineData("Rebuild project", "run rebuild")]
    public void IsBuildAllowedForStep_StepTextRequiresBuild_IsAllowed(string title, string description)
    {
        var plan = BuildPlan(
            (1, title, description),
            (2, "收尾步骤", "无构建要求"));

        bool allowed = EditAgent.IsBuildAllowedForStep(plan.Steps[0], plan, out string? reason);

        allowed.Should().BeTrue();
        reason.Should().Be(LocalizationService.Instance["agent.step.buildAllowedReasonExplicit"]);
    }

    /// <summary>最后一步即使未明说构建 → 允许（收敛验证点）。</summary>
    [Fact]
    public void IsBuildAllowedForStep_LastStepWithoutBuildIntent_IsAllowed()
    {
        var plan = BuildPlan(
            (1, "修改代码", "调整实现"),
            (2, "收尾清理", "整理注释"));

        bool allowed = EditAgent.IsBuildAllowedForStep(plan.Steps[1], plan, out string? reason);

        allowed.Should().BeTrue();
        reason.Should().Be(LocalizationService.Instance["agent.step.buildAllowedReasonLastStep"]);
    }

    /// <summary>
    /// 回归：构建意图只看 Title，描述里的约束性说法不得放行。
    /// 实测一轮 6 步计划中「抽取纯函数计数器 + 单元测试」被放行构建——
    /// 标题无关键词，但描述含构建词，而中文按子串匹配、无否定语气识别，
    /// 「确保可编译」这类约束被误当成构建意图，导致白名单未裁剪 build_solution。
    /// </summary>
    [Theory]
    [InlineData("抽取纯函数计数器 + 单元测试", "确保新文件可编译，避免编译错误")]
    [InlineData("重构解析器", "改动后代码应能正常编译")]
    [InlineData("补充单元测试", "本次不构建，仅新增测试用例")]
    [InlineData("更新文档", "说明如何编译本仓库")]
    public void IsBuildAllowedForStep_BuildWordOnlyInDescription_IsBlocked(string title, string description)
    {
        var plan = BuildPlan(
            (1, title, description),
            (2, "下一步", "继续"));

        bool allowed = EditAgent.IsBuildAllowedForStep(plan.Steps[0], plan, out string? reason);

        allowed.Should().BeFalse(
            "描述里的构建词是约束性说法，不代表本步骤要求执行构建");
        reason.Should().BeNull();
    }

    /// <summary>标题明确要求构建 → 仍放行，即使描述未提构建。</summary>
    [Theory]
    [InlineData("单元测试补充与构建验证")]
    [InlineData("构建解决方案")]
    [InlineData("编译并修复错误")]
    [InlineData("Build the solution")]
    public void IsBuildAllowedForStep_BuildWordInTitle_IsAllowed(string title)
    {
        var plan = BuildPlan(
            (1, title, "无描述"),
            (2, "下一步", "继续"));

        bool allowed = EditAgent.IsBuildAllowedForStep(plan.Steps[0], plan, out string? reason);

        allowed.Should().BeTrue("标题写明的构建意图应被尊重");
        reason.Should().Be(LocalizationService.Instance["agent.step.buildAllowedReasonExplicit"]);
    }

    /// <summary>英文关键词按词边界匹配，避免子串误命中（如 "build" 不应命中 "rebuilding" 之外的无关词）。</summary>
    [Theory]
    [InlineData("latest changes applied", false)]      // "test" 不在其中，且不应命中 "latest"
    [InlineData("the builder pattern", false)]         // "builder" 不是 "build" 的独立词
    [InlineData("building blocks", false)]             // "building" 同样不应命中
    [InlineData("build", true)]
    [InlineData("please Build it", true)]
    [InlineData("compile", true)]
    [InlineData("rebuild", true)]
    public void ContainsBuildIntent_EnglishKeywords_RespectWordBoundaries(string text, bool expected)
    {
        EditAgent.ContainsBuildIntent(text).Should().Be(expected);
    }

    /// <summary>中文关键词按子串匹配。</summary>
    [Theory]
    [InlineData("构建解决方案", true)]
    [InlineData("重新编译", true)]
    [InlineData("构建验证", true)]
    [InlineData("修改属性", false)]
    [InlineData("更新文档", false)]
    public void ContainsBuildIntent_ChineseKeywords_SubstringMatch(string text, bool expected)
    {
        EditAgent.ContainsBuildIntent(text).Should().Be(expected);
    }

    /// <summary>步骤提示词随构建许可给出对应说明。</summary>
    [Fact]
    public void BuildStepPrompt_ReflectsBuildPermission()
    {
        var agent = new EditAgent(_apiService);

        // 中间步骤（无构建意图）→ 提示不允许构建
        var blockedPlan = BuildPlan(
            (1, "修改 Settings 页", "调整属性定义"),
            (2, "收尾清理", "整理注释"));
        var blocked = InvokeBuildStepPrompt(agent, blockedPlan.Steps[0], blockedPlan);
        blocked.Should().Contain("不允许");
        blocked.Should().Contain("build_solution");

        // 最后一步 → 提示允许构建
        var allowed = InvokeBuildStepPrompt(agent, blockedPlan.Steps[1], blockedPlan);
        allowed.Should().Contain("允许调用 build_solution");
    }

    private static string InvokeBuildStepPrompt(EditAgent agent, AgentStep step, AgentTaskPlan plan)
    {
        var method = typeof(EditAgent).GetMethod(
            "BuildStepPrompt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (string)method!.Invoke(agent, new object[] { step, plan, new AgentContext() })!;
    }

    private static AgentTaskPlan BuildPlan(params (int Index, string Title, string Description)[] steps)
    {
        var plan = new AgentTaskPlan { Title = "测试计划" };
        foreach (var (index, title, description) in steps)
        {
            plan.Steps.Add(new AgentStep
            {
                Index = index,
                Title = title,
                Description = description,
            });
        }
        return plan;
    }

    #endregion

    #region ExploreAgent Property

    [Fact]
    public void ExploreAgent_DefaultsToNull()
    {
        var agent = new EditAgent(_apiService);

        agent.ExploreAgent.Should().BeNull();
    }

    [Fact]
    public void ExploreAgent_CanBeSet()
    {
        var agent = new EditAgent(_apiService);
        var exploreAgent = new ExploreAgent(_apiService);

        agent.ExploreAgent = exploreAgent;

        agent.ExploreAgent.Should().Be(exploreAgent);
    }

    [Fact]
    public void ExploreAgent_SettingToNull_AfterSet_DoesNotThrow()
    {
        var agent = new EditAgent(_apiService);
        agent.ExploreAgent = new ExploreAgent(_apiService);

        Action act = () => agent.ExploreAgent = null;
        act.Should().NotThrow();

        agent.ExploreAgent.Should().BeNull();
    }

    [Fact]
    public void ExploreAgent_Replacing_UnsubscribesPrevious()
    {
        var agent = new EditAgent(_apiService);
        var explore1 = new ExploreAgent(_apiService);
        var explore2 = new ExploreAgent(_apiService);

        agent.ExploreAgent = explore1;
        agent.ExploreAgent = explore2;

        agent.ExploreAgent.Should().Be(explore2);
    }

    #endregion

    #region CurrentPlan Property

    [Fact]
    public void CurrentPlan_DefaultsToNull()
    {
        var agent = new EditAgent(_apiService);

        agent.CurrentPlan.Should().BeNull();
    }

    [Fact]
    public void CurrentPlan_CanBeSet()
    {
        var agent = new EditAgent(_apiService);
        var plan = new AgentTaskPlan { Title = "Test Plan" };

        agent.CurrentPlan = plan;

        agent.CurrentPlan.Should().Be(plan);
        agent.CurrentPlan!.Title.Should().Be("Test Plan");
    }

    #endregion

    #region CreateSingleStepPlan

    [Fact]
    public void CreateSingleStepPlan_ReturnsPlanWithOneStep()
    {
        var plan = CreateSingleStepPlanPublic("修改 app.ts 中的配置");

        plan.Steps.Should().HaveCount(1);
        plan.Steps[0].Index.Should().Be(1);
        plan.Steps[0].Description.Should().Contain("app.ts");
        plan.Intent.Should().Be(AgentIntent.CodeChange);
        plan.PlanId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void CreateSingleStepPlan_PreservesFullMessage()
    {
        var longMessage = new string('x', 500);

        var plan = CreateSingleStepPlanPublic(longMessage);

        // Description stores the full user message
        plan.Steps[0].Description.Should().Be(longMessage);
    }

    [Fact]
    public void CreateSingleStepPlan_ReadsAndOutputsContent_UsesReadOnlyExecution()
    {
        const string userMessage = "运行一段python代码读取磁盘文件的代码备份文件并输出";

        var plan = CreateSingleStepPlanPublic(userMessage);

        plan.Intent.Should().Be(AgentIntent.QandA);
        plan.Steps.Should().HaveCount(1);
        plan.Steps[0].Title.Should().Be(
            LocalizationService.Instance["agent.step.executeReadOnlyCommand"]);
        plan.ChangedFiles.Should().BeEmpty();
    }

    [Fact]
    public void CreateSingleStepPlan_ModificationRequest_KeepsCodeChangeIntent()
    {
        var plan = CreateSingleStepPlanPublic("修改 app.ts 中的配置");

        plan.Intent.Should().Be(AgentIntent.CodeChange);
        plan.Steps[0].Title.Should().Be(
            LocalizationService.Instance["agent.step.analyzeAndModify"]);
    }

    [Theory]
    [InlineData("运行一段python代码读取磁盘文件的代码备份文件并输出", true)]
    [InlineData("运行脚本并输出文件内容", true)]
    [InlineData("执行命令查看配置内容", true)]
    [InlineData("修改 app.ts 中的配置", false)]
    [InlineData("创建一个备份文件", false)]
    [InlineData("运行脚本并保存输出到文件", false)]
    public void IsReadOnlyExecutionRequest_ClassifiesExecutionIntent(string message, bool expected)
    {
        var method = typeof(EditAgent).GetMethod(
            "IsReadOnlyExecutionRequest",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var result = (bool)method!.Invoke(null, new object[] { message })!;

        result.Should().Be(expected);
    }

    [Fact]
    public void BuildToolStepContent_AppendsMissingRawOutput()
    {
        const string aiSummary = "已读取文件，以下是说明。";
        const string rawOutput = " 终端输出 (退出码: 0):\n#include <iostream>\nint main() {}";

        var result = BuildToolStepContentPublic(aiSummary, rawOutput);

        result.Should().Contain(aiSummary);
        result.Should().Contain("--- 完整工具输出 ---");
        result.Should().Contain("#include <iostream>");
        result.Should().Contain("--- 完整工具输出结束 ---");
    }

    [Fact]
    public void BuildToolStepContent_DoesNotDuplicateCompleteOutput()
    {
        const string rawOutput = "终端输出 (退出码: 0):\n#include <iostream>\nint main() {}";

        var result = BuildToolStepContentPublic(rawOutput, rawOutput);

        result.Should().Be(rawOutput);
    }

    #endregion

    #region PlanUpdated Event

    [Fact]
    public void PlanUpdated_CanSubscribeAndUnsubscribe()
    {
        var agent = new EditAgent(_apiService);
        int callCount = 0;
        Action<AgentTaskPlan> handler = _ => callCount++;

        agent.PlanUpdated += handler;
        agent.PlanUpdated -= handler;

        // Unsubscribed, so invoking should not increment
        callCount.Should().Be(0);
    }

    #endregion

    #region Logging Events

    [Fact]
    public void DetectAndAutoCompleteLaterSteps_RangeDeclaration_CompletesRangeAndAdvancesIndex()
    {
        var agent = new EditAgent(_apiService);
        var plan = new AgentTaskPlan
        {
            Title = "Test Plan",
            CurrentStepIndex = 1,
            Steps = Enumerable.Range(1, 6)
                .Select(i => new AgentStep
                {
                    Index = i,
                    Title = $"步骤{i}",
                    Status = i == 1 ? AgentStepStatus.Pending : AgentStepStatus.Pending,
                })
                .ToList(),
        };
        plan.Steps[0].Status = AgentStepStatus.InProgress;
        plan.Steps[0].AiResponse = "已完成第一步。步骤1-6 已完成。";

        DetectAndAutoCompleteLaterStepsPublic(agent, plan.Steps[0], plan);

        // 步骤 2..6 应被自动标记为完成
        plan.Steps.Skip(1).Should().OnlyContain(s => s.Status == AgentStepStatus.Completed);
        // CurrentStepIndex 应推进到 6
        plan.CurrentStepIndex.Should().Be(6);
    }

    [Fact]
    public void DetectAndAutoCompleteLaterSteps_DashRange_CompletesSteps()
    {
        var agent = new EditAgent(_apiService);
        var plan = new AgentTaskPlan
        {
            Title = "Test Plan",
            CurrentStepIndex = 1,
            Steps = Enumerable.Range(1, 5)
                .Select(i => new AgentStep
                {
                    Index = i,
                    Title = $"步骤{i}",
                    Status = AgentStepStatus.Pending,
                })
                .ToList(),
        };
        plan.Steps[0].Status = AgentStepStatus.InProgress;
        plan.Steps[0].AiResponse = "前两步已完成，步骤2-4 也随之完成。";

        DetectAndAutoCompleteLaterStepsPublic(agent, plan.Steps[0], plan);

        plan.Steps[1].Status.Should().Be(AgentStepStatus.Completed); // 2
        plan.Steps[2].Status.Should().Be(AgentStepStatus.Completed); // 3
        plan.Steps[3].Status.Should().Be(AgentStepStatus.Completed); // 4
        plan.Steps[4].Status.Should().Be(AgentStepStatus.Pending);   // 5 不受影响
        plan.CurrentStepIndex.Should().Be(4);
    }

    [Fact]
    public void DetectAndAutoCompleteLaterSteps_EnglishRange_CompletesSteps()
    {
        var agent = new EditAgent(_apiService);
        var plan = new AgentTaskPlan
        {
            Title = "Test Plan",
            CurrentStepIndex = 1,
            Steps = Enumerable.Range(1, 4)
                .Select(i => new AgentStep
                {
                    Index = i,
                    Title = $"Step {i}",
                    Status = AgentStepStatus.Pending,
                })
                .ToList(),
        };
        plan.Steps[0].Status = AgentStepStatus.InProgress;
        plan.Steps[0].AiResponse = "Step 1 done. Steps 2 through 4 are also completed.";

        DetectAndAutoCompleteLaterStepsPublic(agent, plan.Steps[0], plan);

        plan.Steps[1].Status.Should().Be(AgentStepStatus.Completed); // 2
        plan.Steps[2].Status.Should().Be(AgentStepStatus.Completed); // 3
        plan.Steps[3].Status.Should().Be(AgentStepStatus.Completed); // 4
        plan.CurrentStepIndex.Should().Be(4);
    }

    [Fact]
    public void DetectAndAutoCompleteLaterSteps_NoDeclaration_DoesNotAdvanceIndex()
    {
        var agent = new EditAgent(_apiService);
        var plan = new AgentTaskPlan
        {
            Title = "Test Plan",
            CurrentStepIndex = 1,
            Steps = Enumerable.Range(1, 4)
                .Select(i => new AgentStep
                {
                    Index = i,
                    Title = $"步骤{i}",
                    Status = AgentStepStatus.Pending,
                })
                .ToList(),
        };
        plan.Steps[0].Status = AgentStepStatus.InProgress;
        plan.Steps[0].AiResponse = "完成了第一步，但没有涉及后续步骤。";

        DetectAndAutoCompleteLaterStepsPublic(agent, plan.Steps[0], plan);

        plan.Steps.Skip(1).Should().OnlyContain(s => s.Status == AgentStepStatus.Pending);
        plan.CurrentStepIndex.Should().Be(1);
    }

    [Fact]
    public void DetectAndAutoCompleteLaterSteps_ThinkingContent_DeclaresRange_CompletesAndAdvances()
    {
        // 场景：正式输出未提及后续步骤，但思考内容中声明了范围式完成
        var agent = new EditAgent(_apiService);
        var plan = new AgentTaskPlan
        {
            Title = "Test Plan",
            CurrentStepIndex = 1,
            Steps = Enumerable.Range(1, 6)
                .Select(i => new AgentStep
                {
                    Index = i,
                    Title = $"步骤{i}",
                    Status = AgentStepStatus.Pending,
                })
                .ToList(),
        };
        plan.Steps[0].Status = AgentStepStatus.InProgress;
        plan.Steps[0].AiResponse = "第一步完成。";
        const string thinking = "已经把所有文件都改好了，步骤1-6 已完成。";

        DetectAndAutoCompleteLaterStepsPublic(agent, plan.Steps[0], plan, thinking);

        plan.Steps.Skip(1).Should().OnlyContain(s => s.Status == AgentStepStatus.Completed);
        plan.CurrentStepIndex.Should().Be(6);
    }

    [Fact]
    public void DetectAndAutoCompleteLaterSteps_ThinkingContent_ListDeclares_Completes()
    {
        // 场景：逗号/顿号分隔列表声明出现在思考内容中
        var agent = new EditAgent(_apiService);
        var plan = new AgentTaskPlan
        {
            Title = "Test Plan",
            CurrentStepIndex = 1,
            Steps = Enumerable.Range(1, 4)
                .Select(i => new AgentStep
                {
                    Index = i,
                    Title = $"步骤{i}",
                    Status = AgentStepStatus.Pending,
                })
                .ToList(),
        };
        plan.Steps[0].Status = AgentStepStatus.InProgress;
        plan.Steps[0].AiResponse = "第一步完成。";
        const string thinking = "步骤2、3也完成了，可以直接跳到第4步。";

        DetectAndAutoCompleteLaterStepsPublic(agent, plan.Steps[0], plan, thinking);

        plan.Steps[1].Status.Should().Be(AgentStepStatus.Completed); // 2
        plan.Steps[2].Status.Should().Be(AgentStepStatus.Completed); // 3
        plan.Steps[3].Status.Should().Be(AgentStepStatus.Pending);   // 4 不受影响
        plan.CurrentStepIndex.Should().Be(3);
    }

    [Fact]
    public void DetectAndAutoCompleteLaterSteps_ThinkingContent_NoDeclaration_DoesNotAdvance()
    {
        // 场景：思考内容只是计划性表述，不应被误判为完成
        var agent = new EditAgent(_apiService);
        var plan = new AgentTaskPlan
        {
            Title = "Test Plan",
            CurrentStepIndex = 1,
            Steps = Enumerable.Range(1, 4)
                .Select(i => new AgentStep
                {
                    Index = i,
                    Title = $"步骤{i}",
                    Status = AgentStepStatus.Pending,
                })
                .ToList(),
        };
        plan.Steps[0].Status = AgentStepStatus.InProgress;
        plan.Steps[0].AiResponse = "第一步完成。";
        const string thinking = "接下来打算执行步骤2和步骤3，然后步骤4。";

        DetectAndAutoCompleteLaterStepsPublic(agent, plan.Steps[0], plan, thinking);

        plan.Steps.Skip(1).Should().OnlyContain(s => s.Status == AgentStepStatus.Pending);
        plan.CurrentStepIndex.Should().Be(1);
    }

    #endregion

    [Fact]
    public void LogEntryAdded_FiresWhenExploreAgentLogs()
    {
        var agent = new EditAgent(_apiService);
        var exploreAgent = new ExploreAgent(_apiService);

        AgentLogEntry? forwardedEntry = null;
        agent.LogEntryAdded += entry => forwardedEntry = entry;

        agent.ExploreAgent = exploreAgent;

        // Simulate ExploreAgent adding a log — EditAgent should forward via LogEntryAdded
        RaiseLogEntryAddedPublic(exploreAgent, new AgentLogEntry { Level = "INFO", Message = "探索中..." });

        forwardedEntry.Should().NotBeNull();
        forwardedEntry!.Message.Should().Be("探索中...");
        forwardedEntry!.Level.Should().Be("INFO");
    }

    #region Step Satisfaction — 前序步骤已代劳（方案 1 / 方案 2 的判定核心）

    private static readonly DateTime PlanStart = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    private static AgentStep NewStep(int index, string title, string description = "")
        => new() { Index = index, Title = title, Description = description };

    private static List<FileChangeSummary> ChangedFiles(params string[] paths)
        => paths.Select(p => new FileChangeSummary { FilePath = p }).ToList();

    [Fact]
    public void ExtractDeclaredFiles_PicksCodeAndConfigFileNames()
    {
        var step = NewStep(2, "解耦余额与用量显示",
            @"修改 Services\Agents\EditAgent.cs 与 DeepSeek_v4_for_VisualStudio.csproj");

        var files = EditAgent.ExtractDeclaredFiles(step);

        files.Should().BeEquivalentTo(new[] { "EditAgent.cs", "DeepSeek_v4_for_VisualStudio.csproj" });
    }

    [Fact]
    public void ExtractDeclaredFiles_WithoutFileReference_ReturnsEmpty()
    {
        var step = NewStep(2, "解耦余额与用量显示", "把用量显示从余额门控中解耦，并在端点切换时清空残留");

        EditAgent.ExtractDeclaredFiles(step).Should().BeEmpty();
    }

    [Fact]
    public void IsStepCoveredByModifiedFiles_TargetModifiedThisRun_ClaimsStepWithEvidence()
    {
        var step = NewStep(3, "清理缓存残留", @"修改 Services\Agents\EditAgent.cs");
        var changed = ChangedFiles(@"F:\repo\Services\Agents\EditAgent.cs");

        bool covered = EditAgent.IsStepCoveredByModifiedFiles(
            step, changed, PlanStart, _ => PlanStart.AddMinutes(1), out string evidence);

        covered.Should().BeTrue();
        evidence.Should().Be("EditAgent.cs");
    }

    [Fact]
    public void IsStepCoveredByModifiedFiles_CompoundExtension_StillClaims()
    {
        // 步骤描述写 a.xaml.cs，提取正则只能截到 a.xaml —— 判定必须仍能匹配真实文件 a.xaml.cs
        var step = NewStep(3, "清理缓存残留", @"修改 View\DeepSeekChatControl.xaml.cs");
        var changed = ChangedFiles(@"F:\repo\View\DeepSeekChatControl.xaml.cs");

        bool covered = EditAgent.IsStepCoveredByModifiedFiles(
            step, changed, PlanStart, _ => PlanStart.AddMinutes(1), out string evidence);

        covered.Should().BeTrue();
        evidence.Should().Be("DeepSeekChatControl.xaml");
    }

    [Fact]
    public void IsStepCoveredByModifiedFiles_StaleChangeFromPreviousRun_DoesNotClaim()
    {
        var step = NewStep(3, "清理缓存残留", @"修改 Services\Agents\EditAgent.cs");
        var changed = ChangedFiles(@"F:\repo\Services\Agents\EditAgent.cs");

        bool covered = EditAgent.IsStepCoveredByModifiedFiles(
            step, changed, PlanStart, _ => PlanStart.AddHours(-1), out _);

        covered.Should().BeFalse();
    }

    [Fact]
    public void IsStepCoveredByModifiedFiles_DeclaredFileNotModified_DoesNotClaim()
    {
        var step = NewStep(3, "清理缓存残留", @"修改 Services\Agents\EditAgent.cs");
        var changed = ChangedFiles(@"F:\repo\Services\Agents\OtherAgent.cs");

        EditAgent.IsStepCoveredByModifiedFiles(
            step, changed, PlanStart, _ => PlanStart.AddMinutes(1), out _).Should().BeFalse();
    }

    [Fact]
    public void IsStepCoveredByModifiedFiles_WithoutFileReference_DoesNotClaim()
    {
        var step = NewStep(2, "解耦余额与用量显示", "把用量显示从余额门控中解耦");
        var changed = ChangedFiles(@"F:\repo\Services\Agents\EditAgent.cs");

        EditAgent.IsStepCoveredByModifiedFiles(
            step, changed, PlanStart, _ => PlanStart.AddMinutes(1), out _).Should().BeFalse();
    }

    [Fact]
    public void IsStepCoveredByModifiedFiles_FileMissingOnDisk_DoesNotClaim()
    {
        var step = NewStep(3, "清理缓存残留", @"修改 Services\Agents\EditAgent.cs");
        var changed = ChangedFiles(@"F:\repo\Services\Agents\EditAgent.cs");

        EditAgent.IsStepCoveredByModifiedFiles(
            step, changed, PlanStart, _ => default, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("构建验证并汇报")]
    [InlineData("运行单元测试")]
    [InlineData("提交并推送修复")]
    [InlineData("Build and verify the fix")]
    public void IsStepCoveredByModifiedFiles_StepWithOwnAction_DoesNotClaim(string title)
    {
        var step = NewStep(4, title, @"修改 Services\Agents\EditAgent.cs");
        var changed = ChangedFiles(@"F:\repo\Services\Agents\EditAgent.cs");

        EditAgent.IsStepCoveredByModifiedFiles(
            step, changed, PlanStart, _ => PlanStart.AddMinutes(1), out _).Should().BeFalse();
    }

    // 英文标记按整词匹配：标识符里的 test/restore 等子串不应把纯改文件步骤误判为"必须亲自执行"。
    // 注意标题只点同一个目标文件，避免引入"点名了别的文件"这一无关失败原因。
    [Theory]
    [InlineData("重构 EditAgent.cs 的 TestData 处理")]
    [InlineData("调整 EditAgent.cs 里 RestorePoint 的断言")]
    [InlineData("修正 EditAgent.cs 的 ParseTestResult 逻辑")]
    public void IsStepCoveredByModifiedFiles_IdentifierSubstring_StillClaims(string title)
    {
        var step = NewStep(4, title, @"修改 Services\Agents\EditAgent.cs");
        var changed = ChangedFiles(@"F:\repo\Services\Agents\EditAgent.cs");

        EditAgent.IsStepCoveredByModifiedFiles(
            step, changed, PlanStart, _ => PlanStart.AddMinutes(1), out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("", false, EditAgent.StepNoToolCallOutcome.EmptyResponse)]
    [InlineData("   ", false, EditAgent.StepNoToolCallOutcome.EmptyResponse)]
    [InlineData("无需修改", false, EditAgent.StepNoToolCallOutcome.ConfirmedNoChange)]
    [InlineData("", true, EditAgent.StepNoToolCallOutcome.SatisfiedByPrevious)]
    [InlineData("本步骤目标已由前序步骤完成，因此未做改动。", true, EditAgent.StepNoToolCallOutcome.SatisfiedByPrevious)]
    [InlineData("已完成", true, EditAgent.StepNoToolCallOutcome.SatisfiedByPrevious)]
    [InlineData("无需修改", true, EditAgent.StepNoToolCallOutcome.SatisfiedByPrevious)]
    [InlineData("已提交", false, EditAgent.StepNoToolCallOutcome.ConfirmedNoChange)]
    [InlineData("由于前序步骤已经把两处修复全部落盘，且构建已通过，因此不再重复读取或构建，仅说明本轮情况即可，无需再次执行任何操作。", false, EditAgent.StepNoToolCallOutcome.TextOnlyFailure)]
    public void ClassifyNoToolCallStep_SplitsEmptySatisfiedAndTextOnly(
        string result, bool coveredByPrevious, EditAgent.StepNoToolCallOutcome expected)
    {
        EditAgent.ClassifyNoToolCallStep(result, coveredByPrevious).Should().Be(expected);
    }

    /// <summary>
    /// 回归：以文字交付为目的的收尾步骤（如「回归风险清单与手动验证」）不产生文件修改，
    /// 回复又长又有结构，原先被长度闸门冤判为 TextOnlyFailure 导致整轮失败。
    /// 含结构化交付物（标题/列表/表格）且声明完成时长回复应判为已完成。
    /// </summary>
    [Fact]
    public void ClassifyNoToolCallStep_LongStructuredDeliverable_IsConfirmedNoChange()
    {
        // 还原截图里步骤 6 的实际输出形态：说明已完成 + 风险清单 + 表格
        string step6 = string.Join("\n", new[]
        {
            "本步骤已完成。代码修改已由前序步骤落盘，此处不再改动文件。",
            "",
            "## 回归风险清单",
            "",
            "- MentionTokenizer 边界：空输入与全空白输入需重点验证",
            "- XAML 高亮层：滚动同步在超长文档下可能滞后",
            "- WebView2 消息高亮：需确认 Regenerate 后再次高亮不重复",
            "",
            "## 手动验证步骤",
            "",
            "1. 打开聊天窗口，发送包含 @文件 的消息，确认蓝色高亮出现",
            "2. 滚动编辑器，确认高亮层与文本同步刷新",
            "3. 触发 Regenerate，确认高亮不叠加",
        });

        EditAgent.ClassifyNoToolCallStep(step6, coveredByPreviousSteps: false)
            .Should().Be(EditAgent.StepNoToolCallOutcome.ConfirmedNoChange,
                "结构化交付物应以完成收尾，而非判失败");
    }

    /// <summary>
    /// 反向保护：长篇「辩解式」叙述即使很长也不算交付物，长度闸门对借口仍然有效。
    /// 这是 IsNoChangesResponse 长度限制的原始目的，不得被上面的放行破坏。
    /// 注：文本必须真的超过 200 字符才会走到结构判定分支，否则测的是短回复路径。
    /// </summary>
    [Theory]
    [InlineData("由于前序步骤已经把两处修复全部落盘，且构建已通过，因此不再重复读取或构建，仅说明本轮情况即可，无需再次执行任何操作。")]
    [InlineData("已经完成。前面几步已经把所有需要改的地方都改完了，所以这里没有必要再调用任何工具去读取或修改文件，直接说明一下当前的状态就可以了，不需要再做别的事情。")]
    public void ClassifyNoToolCallStep_LongUnstructuredExcuse_StillFails(string excuse)
    {
        // 补足长度确保跨过 200 字符闸门，进入结构判定：这才是要保护的分支
        string padded = excuse + new string('说', 150);
        padded.Length.Should().BeGreaterThan(200, "用例必须长于长度闸门，否则测不到结构判定");

        EditAgent.ClassifyNoToolCallStep(padded, coveredByPreviousSteps: false)
            .Should().Be(EditAgent.StepNoToolCallOutcome.TextOnlyFailure,
                "无结构的冗长辩解不构成交付物");
    }

    /// <summary>
    /// 反向保护：单行列表噪声不足以判定为交付物（需至少两条结构性线索）。
    /// </summary>
    [Fact]
    public void ClassifyNoToolCallStep_SingleStructuralLine_StillFails()
    {
        string text = "已完成。本步骤无需修改任何文件，因此没有再调用工具，具体原因如上所述，"
            + "前序步骤已经覆盖了全部改动点，这里只是做一次简短的收尾说明，确保流程闭环即可。"
            + new string('补', 150) + "\n- 无";

        text.Length.Should().BeGreaterThan(200, "用例必须长于长度闸门，否则测不到结构判定");

        EditAgent.ClassifyNoToolCallStep(text, coveredByPreviousSteps: false)
            .Should().Be(EditAgent.StepNoToolCallOutcome.TextOnlyFailure,
                "仅一条列表项不足以构成结构化交付物");
    }

    #endregion

    // ──────────── Reflection helpers for testing private methods ────────────

    private static void DetectAndAutoCompleteLaterStepsPublic(EditAgent agent, AgentStep completedStep, AgentTaskPlan plan, string? thinkingContent = null)
    {
        var method = typeof(EditAgent).GetMethod("DetectAndAutoCompleteLaterSteps",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(agent, new object[] { completedStep, plan, thinkingContent });
    }

    private static AgentTaskPlan CreateSingleStepPlanPublic(string userMessage)
    {
        var method = typeof(EditAgent).GetMethod("CreateSingleStepPlan",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (AgentTaskPlan)method!.Invoke(null, new object[] { userMessage })!;
    }

    private static string BuildToolStepContentPublic(string aiResult, string rawToolOutput)
    {
        var method = typeof(EditAgent).GetMethod(
            "BuildToolStepContent",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, new object[] { aiResult, rawToolOutput })!;
    }

    private static void RaiseLogEntryAddedPublic(ExploreAgent agent, AgentLogEntry entry)
    {
        var field = typeof(BaseAgent).GetField("LogEntryAdded",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        // Actually the event is public, let's use the public API
        // Simulate by calling the protected RaiseLogEntryAdded method on BaseAgent
        var method = typeof(BaseAgent).GetMethod("RaiseLogEntryAdded",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(agent, new object[] { entry });
    }
}
