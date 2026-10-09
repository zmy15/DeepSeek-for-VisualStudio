using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.EditTools;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.ToolWindows;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// Edit Agent — 代码修改执行代理。
    /// 
    /// 职责：
    /// - 按计划逐步执行代码修改
    /// - 通过原生编辑工具直接修改项目文件
    /// - 支持构建/运行验证步骤
    /// - 请求用户权限确认
    /// - 追踪文件变更
    /// 
    /// 限制策略（v1.1.10）：
    /// - 每次编辑 ≤ 3 个文件
    /// - 每次编辑 ≤ 500 行代码变更
    /// - 文件修改后再次编辑前强制重新读取
    /// </summary>
    public partial class EditAgent : BaseAgent
    {
        // ── 编辑限制常量 ──
        internal const int MaxFilesPerEdit = 3;
        internal const int MaxLinesPerEdit = 500;

        private CancellationTokenSource? _agentCts;
        private ExploreAgent? _exploreAgent;

        // ── 累积累推理/思考内容（跨步骤收集，供 UI 渲染思考面板）──
        private string? _accumulatedReasoning;

        // ── 用户原始消息（用于检测跳过构建的意图）──
        private string? _lastUserMessage;

        // ── 最近一次构建结果（随 Handoff 交给 Build Agent，避免重复构建）──
        private string? _lastDirectBuildResult;

        // ── 本轮是否实际调用过构建，以及最后一次构建是否成功 ──
        private bool _didAttemptBuild;
        private bool? _lastBuildSucceeded;

        // ── 本轮已修改文件追踪（用于步骤间重读提示）──
        private readonly HashSet<string> _lastModifiedFiles = new(StringComparer.OrdinalIgnoreCase);

        // ── 本轮计划开始时刻（UTC）──
        // 用于判定"某步骤的目标文件是否在本轮被前序步骤修改过"：
        // 只有最后写入时间晚于该时刻的文件才算"本轮改的"，避免把上一轮残留的
        // plan.ChangedFiles 误判为"前序步骤已代劳"。
        private DateTime _planStartedUtc;

        // ── Agent 多步编辑 Workspace ──
        private Editing.StagedEditWorkspace? _stagedWorkspace;

        /// <summary>
        /// ExploreAgent 引用，由 AgentFactory 注入。
        /// 用于在执行代码修改前智能发现相关文件。
        /// 设置时自动转发 ExploreAgent 的日志和文件变更事件。
        /// </summary>
        public new ExploreAgent? ExploreAgent
        {
            get => _exploreAgent;
            set
            {
                RegisterExploreAgent(value, ref _exploreAgent);
                base.ExploreAgent = value; //  同步到基类属性，确保 ExecuteToolAsync 可见
            }
        }

        /// <summary>当前正在执行的任务计划</summary>
        public AgentTaskPlan? CurrentPlan { get; set; }

        /// <summary>计划/步骤状态变更事件（UI 订阅）</summary>
        public event Action<AgentTaskPlan>? PlanUpdated;

        public EditAgent(DeepSeekApiService apiService) : base(apiService, AgentType.Edit) { }

        #region Agent Definition

        /// <summary>
        /// Edit Agent 统一步骤工具集。
        /// 所有步骤都使用同一工具循环，由模型根据步骤要求选择读取、编辑、终端、构建或 Git 工具。
        /// request_handoff 由系统统一决策，不暴露给步骤工具循环。
        /// </summary>
        private static readonly string[] StepTools = new[]
        {
            // 读取与探索工具
            "read_file",
            "capture_window",
            "capture_webpage",
            "file_search",
            "grep_search",
            "symbol_search",
            "get_file_symbols",
            "list_dir",
            "get_errors",
            "runSubagent",
            // 终端、构建与 Git
            "build_solution",
            "run_in_terminal",
            "get_terminal_output",
            "git",
            // 编辑工具
            "replace_string_in_file",
            "multi_replace_string_in_file",
            "create_file",
            "delete_file",
            "apply_patch",
            "create_directory",
            // 记忆与用户交互
            "memory",
            "VisualStudio_askQuestions",
        };

        protected override AgentDefinition CreateDefinition(AgentType agentType)
        {
            return new AgentDefinition
            {
                Type = AgentType.Edit,
                Name = "Edit",
                AllowedTools = new List<string>(StepTools),
                SystemPrompt = BuildSystemPrompt(),
            };
        }

        /// <summary>
        /// 构建 Edit Agent 专属系统提示词。
        /// 
        /// 说明：编辑类工具的选择与幂等性细则、Git 验证优先级、Handoff Git 状态信任等
        /// 均已下沉到对应工具的 description（见 tool.apply_patch.desc、
        /// tool.replace_string_in_file.desc、tool.git.desc 等），
        /// 这里只保留角色定义、构建信任与工具执行约束。
        /// </summary>
        private static string BuildSystemPrompt()
        {
            return AiPrompts.EditSystemPromptFragment
                + LocalizationService.Instance["agent.edit.mcpSystemPrompt"]
                + LocalizationService.Instance["system.agent.editBuildTrustRule"]
                + LocalizationService.Instance["system.agent.editPhaseToolOverride"]
                + AiPrompts.AgentConclusionStopRule
                // 顺带完成后续步骤时的声明规则：原先写在每个步骤的 user 提示词末尾，
                // 步骤提示精简后移至常驻 system 提示词，规则不丢失且每步不变、利于前缀缓存。
                + "\n\n- 如果本步骤顺带完成了后续步骤，请在响应末尾声明：\"也完成了步骤X、Y\" 或 \"also completed step X, Y\"。";
        }

        #endregion

        #region Execute

        /// <summary>
        /// Edit Agent 执行入口。
        /// 接收计划并逐步执行代码修改。
        /// </summary>
        public override async Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context)
        {
            // ── 清空上次执行的日志、推理内容和移交状态 ──
            _logs.Clear();
            _accumulatedReasoning = null;
            PendingHandoffRequest = null;
            _lastUserMessage = userMessage;
            _lastDirectBuildResult = null;
            _didAttemptBuild = false;
            _lastBuildSucceeded = null;

            var result = new AgentResult
            {
                Success = true,
            };

            // ── 如果有 ActivePlan 且未完成，执行计划 ──
            // 如果计划已完成（如上一轮 plan→edit 已执行完毕），则视为新任务重新路由
            AgentTaskPlan plan;
            if (context.ActivePlan != null && context.ActivePlan.Steps.Count > 0
                && !context.ActivePlan.IsCompleted)
            {
                plan = context.ActivePlan;
                await ExecutePlanAsync(plan, context);
            }
            else
            {
                // ── 使用首次路由时预分类的 TaskSize，避免对 handoff 长消息重复分类 ──
                var taskSize = context.PreClassifiedTaskSize;
                AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.editTaskSize"], taskSize));

                // ── 用户 @edit 显式指定时尊重用户意图，跳过自动移交 ──
                if (taskSize == TaskSize.Large && context.IsExplicitRoute)
                {
                    AddLog("INFO", LocalizationService.Instance["agent.log.editExplicitRouteSkipHandoff"]);
                    taskSize = TaskSize.Medium;
                }

                if (taskSize == TaskSize.Large)
                {
                    // ── Large 任务：移交 Plan Agent 进行深入规划 ──
                    AddLog("INFO", LocalizationService.Instance["agent.log.editLargeTaskHandoff"]);
                    return BuildLargeTaskHandoffResult(userMessage);
                }
                else if (taskSize == TaskSize.Medium)
                {
                    // ── Medium 任务：AI 自主拆分步骤 ──
                    AddLog("INFO", LocalizationService.Instance["agent.log.editAutoSplit"]);
                    plan = await CreateAutoSplitPlanAsync(userMessage, context);
                }
                else
                {
                    // ── Small 任务：单步执行 ──
                    AddLog("INFO", LocalizationService.Instance["agent.log.editNoPlan"]);
                    plan = CreateSingleStepPlan(userMessage);
                }
                plan.Source = PlanSource.EditAgent;
                context.ActivePlan = plan;
                await ExecutePlanAsync(plan, context);
            }

            result.Plan = plan;
            result.FileChanges = plan.ChangedFiles;

            // ── 确定 Handoff 目标（取消后不再启动后续 Agent）──
            bool cancelled = plan.IsCancelled || context.CancellationToken.IsCancellationRequested;
            result.Handoff = cancelled ? null : ResolveHandoff(plan);

            // ── 传递累积累的推理内容供 UI 渲染思考面板 ──
            if (!string.IsNullOrEmpty(_accumulatedReasoning))
                result.ReasoningContent = _accumulatedReasoning;

            // ── 构建最终回复内容（Content）──
            // 纯只读/终端任务无文件变更时直接沿用 AI 的最终回复（例如“输出代码内容”），
            // 避免后续被“变更总结”形式的 Handoff 覆盖；有文件变更时仍是执行结果摘要。
            result.Content = BuildFinalContent(result.Plan, result.Handoff == null);

            result.Logs.AddRange(_logs);
            return result;
        }

        #endregion


        #region Plan Execution

        /// <summary>
        /// 执行任务计划中的所有步骤。
        /// </summary>
        public async Task ExecutePlanAsync(
            AgentTaskPlan plan,
            AgentContext context)
        {
            CurrentPlan = plan;
            _agentCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);

            // ── 记录本轮计划开始时刻：供"前序步骤已代劳"判定区分本轮改动与上一轮残留 ──
            _planStartedUtc = DateTime.UtcNow;

            // ── P0-6: 新计划开始，重置跨计划状态（防止前一个计划的累积上下文泄漏）──
            context.AccumulatedContext = null;

            // ── v1.1.11: 清理上一次计划的步骤摘要记忆文件，防止新旧摘要混在一起 ──
            await ClearPreviousPlanMemoryAsync(context);

            // Handoff 快照只保护 Plan→Edit 的首次请求前缀。
            // 计划包含多个步骤时必须回到完整上下文，否则第 2 步起看不到第 1 步的工具历史。
            if (plan.Steps.Count > 1
                && context.ContextManager != null
                && !context.ContextManager.IsEmpty)
            {
                context.ForwardedMessages = null;
            }
            context.ContextManager?.ClearCacheSnapshot();

            // ═══════════════════════════════════════════════════════════════
            // 缓存策略：将 BuiltInToolService 已读取的文件同步到 AgentContext
            // 全局缓存，避免后续步骤重复 read_file（以后会被 RAG 替代）
            // ═══════════════════════════════════════════════════════════════
            if (context.FileReadCache.Count == 0 && BuiltInTools != null)
            {
                var builtInCache = BuiltInTools.GetFileReadCacheSnapshot();
                if (builtInCache.Count > 0)
                {
                    foreach (var kvp in builtInCache)
                        context.FileReadCache[kvp.Key] = kvp.Value;
                    AddLog("INFO", LocalizationService.Instance.Format("agent.log.editCachedFiles", builtInCache.Count));
                }
            }

            // ── 防重守卫：如果计划已完成，跳过重复执行 ──
            if (plan.IsCompleted)
            {
                AddLog("INFO", LocalizationService.Instance["agent.log.editPlanDone"]);
                return;
            }

            try
            {
                for (int i = 0; i < plan.Steps.Count; i++)
                {
                    if (_agentCts.IsCancellationRequested)
                    {
                        plan.IsCancelled = true;
                        break;
                    }

                    var step = plan.Steps[i];

                    // ── 跳过已完成的步骤（防止计划被恢复后重复执行）──
                    if (step.Status is AgentStepStatus.Completed or AgentStepStatus.Skipped)
                    {
                        AddLog("INFO", LocalizationService.Instance.Format("agent.log.editStepSkipped", step.Index, step.Title));
                        continue;
                    }

                    plan.CurrentStepIndex = i + 1;
                    step.Status = AgentStepStatus.InProgress;
                    NotifyPlanUpdated();

                    // ── 记录本步骤开始前的累积思考长度，用于提取本步骤思考增量（供完成声明检测）──
                    int reasoningBaseLength = _accumulatedReasoning?.Length ?? 0;

                    var L = LocalizationService.Instance;
                    AddLog("INFO", string.Format(L["agent.log.editStepExec"], step.Index, plan.Steps.Count, step.Title));

                    try
                    {
                        await ExecuteStepAsync(step, plan, context);
                        step.Status = AgentStepStatus.Completed;
                        AddLog("INFO", string.Format(L["agent.log.editStepDone"], step.Index, step.ResultSummary ?? "OK"));
                    }
                    catch (OperationCanceledException)
                    {
                        step.Status = AgentStepStatus.Skipped;
                        AddLog("WARN", string.Format(L["agent.log.editStepCancelled"], step.Index));
                        plan.IsCancelled = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        step.Status = AgentStepStatus.Failed;
                        step.ResultSummary = ex.Message;
                        AddLog("ERROR", string.Format(L["agent.log.editStepFailed"], step.Index, ex.Message));
                    }

                    NotifyPlanUpdated();

                    // ── 继承上下文：将刚完成的步骤结果累积（所有模式通用）──
                    if (step.Status == AgentStepStatus.Completed)
                    {
                        string stepResult = string.IsNullOrEmpty(step.ResultSummary)
                            ? string.Format(L["agent.log.editStepContextCompleted"], step.Index, step.Title)
                            : string.Format(L["agent.log.editStepContextWithResult"], step.Index, step.Title, step.ResultSummary);
                        context.AccumulatedContext = (context.AccumulatedContext ?? "") + "\n" + stepResult;
                        if (!string.IsNullOrEmpty(step.AiResponse) && step.AiResponse!.Length < 3000)
                            context.AccumulatedContext += "\n" + step.AiResponse;

                        // ── 截断：保留最近 8000 字符，防止无限增长导致 token 爆炸 ──
                        const int maxAccumulatedChars = 8000;
                        if (context.AccumulatedContext.Length > maxAccumulatedChars)
                        {
                            context.AccumulatedContext = "...(早期上下文已截断)\n"
                                + context.AccumulatedContext.Substring(
                                    context.AccumulatedContext.Length - maxAccumulatedChars);
                        }
                        AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.contextAccumulated"], context.AccumulatedContext.Length));

                        // ── 将步骤摘要写入会话记忆（供 Ask Agent 最终汇总使用）──
                        await SaveStepSummaryToMemoryAsync(step, plan, context);

                        // ── v1.1.10: 检测 AI 输出中声明的后续步骤完成情况（含思考内容）──
                        string stepThinking = _accumulatedReasoning != null && _accumulatedReasoning.Length > reasoningBaseLength
                            ? _accumulatedReasoning.Substring(reasoningBaseLength)
                            : "";
                        DetectAndAutoCompleteLaterSteps(step, plan, stepThinking);
                    }
                }

                plan.IsCompleted = plan.Steps.All(s =>
                    s.Status is AgentStepStatus.Completed or AgentStepStatus.Skipped);

                // ── 计划完成后，将聚合摘要写入会话记忆 ──
                if (plan.IsCompleted && !plan.IsCancelled)
                {
                    await SaveFinalPlanSummaryToMemoryAsync(plan, context);
                }

                // ── 诊断日志：记录步骤完成情况 ──
                int completedCount = plan.Steps.Count(s => s.Status == AgentStepStatus.Completed);
                int skippedCount = plan.Steps.Count(s => s.Status == AgentStepStatus.Skipped);
                int failedCount = plan.Steps.Count(s => s.Status == AgentStepStatus.Failed);
                int pendingCount = plan.Steps.Count(s => s.Status == AgentStepStatus.Pending);
                AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.editPlanProgress"],
                    plan.Steps.Count, completedCount, skippedCount, failedCount, pendingCount));

                // ── v1.1.10: Plan 级别变更追踪 — 对比计划预期与实际修改 ──
                PerformPlanChangeTracking(plan, context);

                // ── 最终构建不再由 EditAgent 自动触发 ──
                // ResolveHandoff 根据最后一次真实构建结果决定：
                // 未构建、构建失败或构建后又有文件变更 → Build Agent；构建成功或无文件变更 → Ask 总结。
            }
            finally
            {
                NotifyPlanUpdated();

                // ── Toast 通知：任务完成或中断 ──
                NotifyPlanCompletionViaToast(plan);

                // ── 清理 Plan Agent 生成?plan.md ──
                await CleanupPlanMarkdownAsync(plan, context);

                // ── 结束本轮编辑会话：清空备份会话目录（空目录回收）──
                // 此前无调用点，会话目录随进程存活持续累积（见还原点分析报告 P1-1）。
                BackupService.EndSession();
            }
        }

        /// <summary>
        /// 删除 Plan Agent 生成的 plan.md 文件（Edit Agent 执行完毕后清理）。
        /// </summary>
        private async Task CleanupPlanMarkdownAsync(AgentTaskPlan plan, AgentContext context)
        {
            string? planFilePath = plan.PlanFilePath ?? context.PlanFilePath;
            if (string.IsNullOrEmpty(planFilePath))
                return;

            try
            {
                await Task.Run(() =>
                {
                    if (File.Exists(planFilePath))
                    {
                        File.Delete(planFilePath);
                        Logger.Info($"[EditAgent] 已清理 plan.md: {planFilePath}");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditAgent] 清理 plan.md 失败（非致命）: {ex.Message}");
            }
        }

        /// <summary>
        /// 执行单个步骤。
        /// </summary>
        private async Task ExecuteStepAsync(
            AgentStep step, AgentTaskPlan plan, AgentContext context)
        {
            var ct = _agentCts?.Token ?? context.CancellationToken;

            // ── 权限确认 ──
            if (step.RequiresApproval && !string.IsNullOrEmpty(step.PendingCommand))
            {
                step.Status = AgentStepStatus.WaitingApproval;
                NotifyPlanUpdated();

                bool approved = await RequestPermissionAsync(step.Title, step.PendingCommand!, "command");
                if (!approved)
                {
                    step.Status = AgentStepStatus.Skipped;
                    step.ResultSummary = LocalizationService.Instance["agent.log.editStepPermissionDenied"];
                    return;
                }

                step.Status = AgentStepStatus.InProgress;
                NotifyPlanUpdated();
            }

            // ── 所有步骤统一走工具循环，由模型自行选择工具。 ──
            // 步骤推进属于同一 Agent 内部流转，不是移交：必须清掉可能残留的移交前缀
            // （如上一步 build_solution 成功提前终止循环时保存的 ForwardedMessages），
            // 否则 BuildContextAwareMessages 会误走 Handoff 复用分支，
            // 插入身份边界提示 + 移交上下文块，把步骤提示伪装成第二次任务下发。
            if (context.ForwardedMessages != null)
            {
                context.ForwardedMessages = null;
                AddLog("INFO", "[EditAgent] " + LocalizationService.Instance["agent.log.editHandoffPrefixCleared"]);
            }
            context.AllowForwardedMessageReuse = false;

            string stepPrompt = BuildStepPrompt(step, plan, context);
            await ExecuteStepWithToolsAsync(step, plan, context, stepPrompt, ct);
        }

        /// <summary>
        /// 执行单个步骤。所有步骤共用同一工具循环，不再区分代码、构建、验证或只读阶段；
        /// 文件变更、构建状态和工具输出均以真实工具调用记录为准。
        /// </summary>
        private async Task ExecuteStepWithToolsAsync(
            AgentStep step, AgentTaskPlan plan, AgentContext context,
            string stepPrompt, CancellationToken ct)
        {
            string result = string.Empty;
            List<FileChangeSummary> changes = new();

            // ── 解析工作区根目录 ──
            string workspaceRoot = context.SolutionPath ?? string.Empty;
            if (!string.IsNullOrEmpty(workspaceRoot) && System.IO.File.Exists(workspaceRoot))
                workspaceRoot = System.IO.Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;

            // ── 创建 / 重置 StagedEditWorkspace（须在 AI 工具循环之前！）──
            // 工具循环中的 create_file / apply_patch 等会通过 WriteFile 登记 Baseline，
            // 供 diff 预览和逐块撤销使用。若在此之后才初始化，工具编辑将走 BackupService 直接落盘，
            // 不登记 hunks，导致 diff 无数据可显示。
            _stagedWorkspace ??= new Editing.StagedEditWorkspace();

            // ── 注入已打开文档写入器：已打开文档通过 buffer+编辑器 Save 写入 ──
            // 避免 File.WriteAllText 裸写盘在 dirty buffer 场景触发 VS「文件已在磁盘上修改」弹窗；
            // 未打开的文件 writer 返回 false，自动回退裸写盘。
            _stagedWorkspace.OpenDocumentWriter = EditBufferApplier.TryWriteOpenDocument;
            _stagedWorkspace.OpenDocumentContentProvider = EditBufferApplier.TryGetOpenDocumentContent;

            _stagedWorkspace.Discard(); // 清空上一轮残留

            if (BuiltInTools != null)
                BuiltInTools.Workspace = _stagedWorkspace;

            // ── AI 工具循环：编辑必须通过真实工具调用完成 ──
            var messages = BuildContextAwareMessages(Definition.SystemPrompt, stepPrompt);
            var thinkingBuilder = new StringBuilder();
            var stepToolWhitelist = new List<string>(StepTools);

            // ── 步骤级构建许可：非"要求构建"的中间步骤禁用 build_solution ──
            // 只裁剪客户端拦截白名单，tools JSON 仍发送完整集（保持 Prefix Cache 稳定）。
            // 模型若仍调用 build_solution，会收到白名单拒绝消息并据此调整。
            if (!IsBuildAllowedForStep(step, plan, out string? buildAllowedReason))
            {
                stepToolWhitelist.RemoveAll(t =>
                    string.Equals(t, "build_solution", StringComparison.OrdinalIgnoreCase));
                AddLog("INFO", string.Format(
                    LocalizationService.Instance["agent.log.editStepBuildBlocked"], step.Index));
            }
            else
            {
                AddLog("INFO", string.Format(
                    LocalizationService.Instance["agent.log.editStepBuildAllowed"],
                    step.Index, buildAllowedReason));
            }

            AddLog("INFO", LocalizationService.Instance["agent.log.callingAiToolLoop"]);
            result = await CallAiWithToolLoopAsync(
                messages,
                workspaceRoot,
                ct,
                toolWhitelist: stepToolWhitelist,
                onThinking: (thinking) =>
                {
                    thinkingBuilder.Append(thinking);
                    context.OnThinkingChunk?.Invoke(thinking);
                },
                onContent: (content) =>
                {
                    context.OnContentChunk?.Invoke(content);
                },
                onToolCall: (toolSummary) =>
                {
                    AddLog("TOOL", toolSummary);
                });

            int stepToolLoopStart = Math.Max(
                0,
                Context?.ToolHistoryInsertIndex ?? Math.Max(0, messages.Count - 2));

            if (thinkingBuilder.Length > 0)
            {
                if (!string.IsNullOrEmpty(_accumulatedReasoning))
                    _accumulatedReasoning += "\n\n";
                _accumulatedReasoning += thinkingBuilder.ToString();
            }

            step.AiResponse = result;
            var stepMessages = GetStepToolLoopMessages(messages, stepToolLoopStart);
            TrackBuildStateFromToolMessages(stepMessages);
            var toolMadeEdits = ExtractToolMadeEdits(stepMessages);
            bool hasToolEdits = toolMadeEdits.Count > 0;

            if (!hasToolEdits)
            {
                bool hasToolCalls = stepMessages.Any(m => m.ToolCalls != null && m.ToolCalls.Count > 0);
                if (!hasToolCalls)
                {
                    // ── 本步骤一个工具调用都没有：区分四种结局（见 ClassifyNoToolCallStep）──
                    // ① 明确声明"无需修改" → 成功；
                    // ② 目标文件已由前序步骤修改（工作区客观事实）→ 认领为已完成，不再判失败；
                    // ③ 真·空响应（无内容、无工具调用）→ 失败，但走专用诊断文案；
                    // ④ 仅文字说明、未调用任何工具 → 失败（原文案，已改为准确表述）。
                    bool coveredByPreviousSteps = IsStepCoveredByModifiedFiles(
                        step, plan.ChangedFiles, _planStartedUtc, GetFileWriteTimeUtcSafe,
                        out string coveredFiles);

                    switch (ClassifyNoToolCallStep(result, coveredByPreviousSteps))
                    {
                        case StepNoToolCallOutcome.ConfirmedNoChange:
                            step.ResultSummary = LocalizationService.Instance["agent.log.editNoChangesConfirmed"];
                            AddLog("INFO", LocalizationService.Instance["agent.log.editNoChange"]);
                            return;

                        case StepNoToolCallOutcome.SatisfiedByPrevious:
                            step.ResultSummary = LocalizationService.Instance.Format(
                                "agent.log.editStepClaimedSummary", coveredFiles);
                            AddLog("INFO", LocalizationService.Instance.Format(
                                "agent.log.editStepClaimedNoTool", step.Index, coveredFiles));
                            return;

                        case StepNoToolCallOutcome.EmptyResponse:
                            throw new InvalidOperationException(
                                LocalizationService.Instance["agent.log.editEmptyResponse"]);

                        default:
                            throw new InvalidOperationException(
                                LocalizationService.Instance["agent.log.editNoEditsProduced"]);
                    }
                }

                bool hasBuildCall = stepMessages.Any(m =>
                    m.ToolCalls != null &&
                    m.ToolCalls.Any(tc => string.Equals(
                        tc.Function?.Name,
                        "build_solution",
                        StringComparison.OrdinalIgnoreCase)));

                step.AiResponse = BuildToolStepContent(
                    result,
                    GetLastUserFacingToolOutput(stepMessages));

                if (hasBuildCall)
                {
                    bool buildSucceeded = _lastBuildSucceeded == true;
                    step.ResultSummary = buildSucceeded
                        ? LocalizationService.Instance["agent.log.editBuildStepPassed"]
                        : LocalizationService.Instance["agent.log.editBuildStepFailed"];
                    // 保留 [EditAgent] 前缀：FormatLogForThinking 依赖该前缀判定透传，
// 去掉前缀会导致本条日志被过滤器静默丢弃、不再显示在思考气泡中。
                    AddLog(buildSucceeded ? "INFO" : "WARN",
                        "[EditAgent] " + (buildSucceeded
                            ? LocalizationService.Instance["agent.log.editBuildStepPassed"]
                            : LocalizationService.Instance["agent.log.editBuildStepFailed"]));
                }
                else
                {
                    step.ResultSummary = LocalizationService.Instance["agent.step.completed"];
                    AddLog("INFO", "[EditAgent] " + LocalizationService.Instance["agent.log.editToolStepNoChange"]);
                }

                return;
            }

            AddLog("INFO", "[EditAgent] " + LocalizationService.Instance.Format(
                "agent.log.editToolEditsDetected", toolMadeEdits.Count,
                string.Join(", ", toolMadeEdits.Select(e => Path.GetFileName(e.FilePath)).Distinct())));

            // ── 保存原始文件内容（用于最终 diff 比较）──
            var originalContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var appliedResults = new List<EditApplyResult>();

            await CollectToolMadeEditsAsync(toolMadeEdits, plan, workspaceRoot,
                originalContents, appliedResults, ct);

            // ── 收集所有变更到 changes 列表（使用真实行数差异而非编辑块数量）──
            changes = appliedResults
                .Where(r => r.Success)
                .Select(r =>
                {
                    // 从 originalContents 计算真实行数变化（使用 diff 算法）
                    int realAdded = 0;
                    int realRemoved = 0;
                    if (originalContents.TryGetValue(r.FilePath, out string? original))
                    {
                        // RAG-SOURCE: file-read 读取最终文件内容（计算变更统计）
                        string final = File.Exists(r.FilePath)
                            ? FileEncodingHelper.ReadAllText(r.FilePath)
                            : (r.FinalContent ?? string.Empty);
                        CountDiffLines(original, final, out realAdded, out realRemoved);
                    }
                    else
                    {
                        // 新文件（未在 originalContents 中）：读取实际文件内容计算行数
                        if (File.Exists(r.FilePath))
                        {
                            string content = FileEncodingHelper.ReadAllText(r.FilePath);
                            realAdded = CountLines(content);
                        }
                        else if (!string.IsNullOrEmpty(r.FinalContent))
                        {
                            realAdded = CountLines(r.FinalContent!);
                        }
                        else
                        {
                            realAdded = r.AppliedEdits.Count > 0 ? r.AppliedEdits.Count : 1;
                        }
                    }

                    return new FileChangeSummary
                    {
                        FilePath = r.FilePath,
                        LinesAdded = realAdded,
                        LinesRemoved = realRemoved,
                        BriefDescription = $"{Path.GetFileName(r.FilePath)} ({r.OperationType})",
                    };
                })
                .Concat(plan.ChangedFiles)
                .GroupBy(c => NormalizePath(c.FilePath), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(c => c.LinesAdded + c.LinesRemoved).First())
                .ToList();

            var L = LocalizationService.Instance;

            // ── 汇总操作类型描述 ──
            const string operationTypeLabel = "tool_edit";

            // ── 使用实际变更文件数（而非仅 appliedResults 中的成功计数）──
            int actualChangedFileCount = changes.Count > 0
                ? changes.Count
                : plan.ChangedFiles.Count;

            step.ResultSummary = actualChangedFileCount > 0
                ? string.Format(L["agent.log.editFilesModified"], actualChangedFileCount, operationTypeLabel)
                : string.Format(L["agent.log.editNoFilesChanged"], operationTypeLabel);

            // ── 修改文件后的构建验证统一交给 Build Agent ──
            // 代码步骤内若已构建，ResolveHandoff 会复用该结果；未构建、构建失败或构建后又有变更时移交 Build Agent。
            // Edit 不再启动第二段验证阶段，避免同一步骤重复构建。

            // ── 编辑后诊断检查 ──
            if (appliedResults.Count > 0)
            {
                foreach (var editResult in appliedResults.Where(r => r.Success))
                {
                    var newDiags = await EditPatchService.CheckNewDiagnosticsAsync(editResult.FilePath);
                    if (newDiags.Count > 0)
                    {
                        editResult.NewDiagnostics = newDiags;
                        AddLog("WARN", string.Format(LocalizationService.Instance["agent.log.newDiagnostics"],
                            Path.GetFileName(editResult.FilePath), newDiags.Count,
                            string.Join("; ", newDiags.Take(5))));
                    }
                }
            }

            // ── 刷新已修改文件的缓存：先移除旧内容，再写入磁盘上的最新快照 ──
            if (BuiltInTools != null && appliedResults.Count > 0)
            {
                var modifiedPaths = appliedResults
                    .Where(r => r.Success)
                    .Select(r => r.FilePath)
                    .Concat(plan.ChangedFiles.Select(c => c.FilePath))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                BuiltInTools.InvalidateFileReadCache(modifiedPaths);

                var latestContents = new List<KeyValuePair<string, string>>();
                foreach (var path in modifiedPaths)
                {
                    if (!File.Exists(path))
                        continue;

                    try
                    {
                        var content = await Task.Run(() => FileEncodingHelper.ReadAllText(path), ct);
                        latestContents.Add(new KeyValuePair<string, string>(path, content));
                    }
                    catch (Exception ex)
                    {
                        AddLog("WARN", string.Format(
                            LocalizationService.Instance["agent.log.editFileCacheRefreshFailed"],
                            Path.GetFileName(path), ex.Message));
                    }
                }

                BuiltInTools.UpdateFileReadCache(latestContents);
            }

            // ── 恢复 diff 预览，从 Workspace 生成 Batch 并创建 Session ──
            var batch = _stagedWorkspace!.ToPreparedChangeBatch();

            if (batch.Changes.Count > 0)
            {
                foreach (var change in batch.Changes)
                {
                    // RAG-SOURCE: file-read 读取最终文件内容（diff 预览对比）
                    string finalContent = change.ProposedText;
                    string oldContent = change.BaselineText;
                    if (oldContent != finalContent)
                    {
                        // 写穿模式：已落盘，撤销时通过 _stagedWorkspace 恢复磁盘 Baseline
                        await TerminalWindowHelper.ShowFinalDiffAsync(
                            oldContent, finalContent, change.FilePath, _stagedWorkspace);
                    }
                }
            }
            else
            {
                // 无 Workspace 变更 → 保持旧版路径兼容
                foreach (var kvp in originalContents)
                {
                    string finalContent = File.Exists(kvp.Key)
                        ? await Task.Run(() => FileEncodingHelper.ReadAllText(kvp.Key), ct)
                        : string.Empty;
                    if (kvp.Value != finalContent)
                    {
                        await TerminalWindowHelper.ShowFinalDiffAsync(kvp.Value, finalContent, kvp.Key);
                    }
                }
            }

            // ── v1.1.10: 步骤完成自检 — 对比步骤描述中的文件名与实际修改 ──
            // 如果步骤描述明确提到了某文件但未在本次编辑中修改，记录提示。
            PerformStepCompletenessCheck(step, appliedResults, workspaceRoot);
        }

        /// <summary>
        /// 步骤完整性自检（v1.1.10）。
        /// 对比步骤描述中引用的文件与实际修改的文件，发现遗漏时记录警告。
        /// 不阻断执行，仅作为日志提示供用户参考。
        /// </summary>
        private void PerformStepCompletenessCheck(
            AgentStep step, List<EditApplyResult> appliedResults, string workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(step.Description) || appliedResults.Count == 0)
                return;

            // 从步骤描述中提取文件引用（匹配常见代码文件扩展名）
            var matches = StepFileReferenceRegex.Matches(step.Description);

            if (matches.Count == 0) return;

            var mentionedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                mentionedFiles.Add(m.Groups[1].Value);
            }

            // 收集实际修改的文件名
            var actuallyModified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in appliedResults.Where(r => r.Success))
            {
                actuallyModified.Add(Path.GetFileName(r.FilePath));
            }

            // 找出步骤描述中提到但未修改的文件
            var untouched = mentionedFiles
                .Where(f => !actuallyModified.Contains(f))
                .ToList();

            if (untouched.Count > 0)
            {
                AddLog("WARN", string.Format(
 "[EditAgent]  步骤自检：步骤描述中提到了 {0} 个文件，但以下文件未被本次编辑修改: {1}。" +
                    "如果这些修改在后续步骤中完成则可忽略，否则可能是遗漏。",
                    mentionedFiles.Count,
                    string.Join(", ", untouched)));
            }
        }

        #region Step Satisfaction — "前序步骤已代劳"判定

        /// <summary>
        /// 步骤/计划描述中出现的文件引用（含常见代码与配置文件扩展名）。
        /// 供步骤完整性自检、计划变更追踪与"前序步骤已代劳"判定共用，避免三处正则各自漂移。
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex StepFileReferenceRegex = new(
            @"\b(\w+\.(?:cs|ts|js|py|java|cpp|h|hpp|xml|json|yaml|yml|md|csproj|sln|vb|fs|cshtml|razor|css|scss|html|xaml|config|props|targets))\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// 中文自有动作标记（构建、测试、Git、终端等）。
        /// 中文字面量不会与英文标识符混淆，可直接子串匹配。
        /// </summary>
        private static readonly string[] OwnExecutionMarkersCn = new[]
        {
            "构建", "编译", "测试", "验证", "运行", "终端", "回滚", "提交", "推送",
        };

        /// <summary>
        /// 英文自有动作标记（构建、测试、Git、终端等）。
        /// 必须按整词匹配，否则被标识符子串误伤：例如描述里的 "TestData"/"CacheHelper.restore"
        /// 含 test/restore，会把本可认领的纯改文件步骤判成"需要亲自执行"，令新功能失效。
        /// 命中即为保守执行（照常调用模型），不会造成漏改。
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex OwnExecutionRegex = new(
            @"\b(?:build|compile|test|verify|validate|run|terminal|git|commit|push|revert|msbuild|dotnet|npm|restore)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// 从步骤标题与描述中提取被点名的文件名（仅文件名，不含目录）。
        /// 描述未点名任何文件时返回空集合 —— 此时无法用文件覆盖判定"已满足"。
        /// </summary>
        internal static HashSet<string> ExtractDeclaredFiles(AgentStep step)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (step == null) return files;

            string text = $"{step.Title} {step.Description}";
            foreach (System.Text.RegularExpressions.Match m in StepFileReferenceRegex.Matches(text))
                files.Add(m.Groups[1].Value);

            return files;
        }

        /// <summary>
        /// 本步骤是否必须由模型亲自执行（标题/描述含构建、测试、Git、终端等独立动作）。
        /// </summary>
        internal static bool RequiresOwnExecution(AgentStep step)
        {
            if (step == null) return true;

            string text = $"{step.Title} {step.Description}";
            foreach (var marker in OwnExecutionMarkersCn)
            {
                if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return OwnExecutionRegex.IsMatch(text);
        }

        /// <summary>
        /// 判定"本步骤的目标文件已由本轮的前序步骤修改"。
        /// 该判定驱动两处行为：① 步骤提示词中注入"前序步骤已修改"提醒，避免模型重放已生效的补丁；
        /// ② 该步骤最终一个工具调用都没有时，作为客观依据认定为"已由前序步骤完成"而非失败。
        ///
        /// 双重门槛（缺一不可）：
        /// ① 步骤标题/描述必须点名文件，且这些文件全部出现在 changedFiles 中（有声明文件未改 → 不认领）；
        /// ② 文件在磁盘上的最后写入时间必须晚于本轮计划开始时刻（排除上一轮已落盘的残留改动）。
        ///
        /// 注意：文件级覆盖不能证明语义完成 —— 前序步骤可能只改了同一文件的另一部分。
        /// 因此本判定不用于"跳过模型直接判完成"，只用于提示模型与兜底认定，剩余改动仍由模型补齐。
        /// </summary>
        /// <param name="step">待判定的步骤</param>
        /// <param name="changedFiles">本轮累计变更文件（plan.ChangedFiles）</param>
        /// <param name="planStartedUtc">本轮计划开始时刻（UTC）</param>
        /// <param name="lastWriteTimeUtc">取文件最后写入时间的委托（便于测试注入）</param>
        /// <param name="evidence">命中时输出被覆盖的文件名列表</param>
        internal static bool IsStepCoveredByModifiedFiles(
            AgentStep step,
            IReadOnlyCollection<FileChangeSummary>? changedFiles,
            DateTime planStartedUtc,
            Func<string, DateTime> lastWriteTimeUtc,
            out string evidence)
        {
            evidence = string.Empty;
            if (step == null || changedFiles == null || changedFiles.Count == 0) return false;

            var declared = ExtractDeclaredFiles(step);
            if (declared.Count == 0) return false;      // 描述没点名文件 → 无客观依据
            if (RequiresOwnExecution(step)) return false;

            var covered = new List<string>();
            foreach (var name in declared)
            {
                string? path = changedFiles
                    .FirstOrDefault(c => FileNameMatches(Path.GetFileName(c.FilePath), name))
                    ?.FilePath;
                if (string.IsNullOrEmpty(path)) return false;

                DateTime stamp;
                try { stamp = lastWriteTimeUtc(path); }
                catch { return false; }

                // 允许 2 秒容差：FAT/UNC 等文件系统的最后写入时间精度为 2 秒
                if (stamp == default || stamp < planStartedUtc.AddSeconds(-2)) return false;

                covered.Add(name);
            }

            evidence = string.Join(", ", covered.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            return true;
        }

        /// <summary>
        /// 文件名匹配（大小写不敏感），并兼容复合扩展名：
        /// 步骤描述里的 "View\DeepSeekChatControl.xaml.cs" 经 <see cref="StepFileReferenceRegex"/>
        /// 只能截到 "DeepSeekChatControl.xaml"（正则首个命中的已知扩展名即 xaml），
        /// 若用严格等值比较会漏判 .xaml.cs / .cshtml / .d.ts 等文件，导致"已代劳"判定失效。
        /// </summary>
        private static bool FileNameMatches(string changedFileName, string declaredName)
        {
            if (string.Equals(changedFileName, declaredName, StringComparison.OrdinalIgnoreCase))
                return true;

            // 仅允许"真实文件名以声明名加一个点号开头"这一方向：
            // 步骤写 a.xaml.cs 时正则只截到 a.xaml，真实文件 a.xaml.cs 需命中。
            // 反向放宽会把 a.cs 误配到 a.cs.bak / a.xaml.csproj 等近似名，故不采用。
            return changedFileName.StartsWith(declaredName + ".", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 读取文件最后写入时间（UTC）。文件不存在或读取失败返回 default，
        /// 由 <see cref="IsStepCoveredByModifiedFiles"/> 判为"未覆盖"（保守）。
        /// </summary>
        private static DateTime GetFileWriteTimeUtcSafe(string path)
        {
            try
            {
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
            }
            catch
            {
                return default;
            }
        }

        /// <summary>本步骤一个工具调用都没有时的结局分类。</summary>
        public enum StepNoToolCallOutcome
        {
            /// <summary>模型明确声明无需修改 → 视为已完成（原行为）。</summary>
            ConfirmedNoChange,

            /// <summary>目标文件已由前序步骤修改 → 认领为已完成（新增出口，修复误判失败）。</summary>
            SatisfiedByPrevious,

            /// <summary>真·空响应（无内容、无工具调用）→ 失败，走专用诊断文案。</summary>
            EmptyResponse,

            /// <summary>仅文字说明、未调用任何工具 → 失败。</summary>
            TextOnlyFailure,
        }

        /// <summary>
        /// 分类"本步骤一个工具调用都没有"的结局。
        /// 优先级：客观的"文件已被前序步骤改过"证据 → 明确的"无需修改"声明 → 空响应诊断 → 纯文字失败。
        ///
        /// 客观证据必须优先于声明，原因有二：
        /// ① 模型常以"已由前序步骤完成"收尾，而 <see cref="IsNoChangesResponse"/> 里的"已完成/已经.*完成"
        ///    会先把这类回复误归为 ConfirmedNoChange，使新出口与专用日志永不触发、归因失真；
        /// ② 该函数还包含"已提交/已推送/暂存成功"等 Git 动作模式，若这些回复先被判为 ConfirmedNoChange，
        ///    零工具调用会被静默当作成功 —— 那本是要判失败的场景。
        /// </summary>
        internal static StepNoToolCallOutcome ClassifyNoToolCallStep(
            string? result, bool coveredByPreviousSteps)
        {
            if (coveredByPreviousSteps)
                return StepNoToolCallOutcome.SatisfiedByPrevious;

            if (IsNoChangesResponse(result ?? string.Empty))
                return StepNoToolCallOutcome.ConfirmedNoChange;

            if (string.IsNullOrWhiteSpace(result))
                return StepNoToolCallOutcome.EmptyResponse;

            return StepNoToolCallOutcome.TextOnlyFailure;
        }

        #endregion

        /// <summary>
        /// 从 AI 响应中检测是否声明了后续步骤也已完成（v1.1.10）。
        /// 解析 AI 输出中的步骤完成声明（如"步骤2和3也完成了"/"also completed step 2"），
        /// 自动将对应步骤标记为 Completed。比文件级启发式更可靠，尤其适用于同文件多步骤场景。
        /// v1.1.12: 支持范围式声明（"步骤1-6 已完成"），并合并扫描思考内容（thinkingContent），
        /// 因为 AI 可能在思考过程中声明步骤完成而正式输出未提及。
        /// </summary>
        private void DetectAndAutoCompleteLaterSteps(AgentStep completedStep, AgentTaskPlan plan, string? thinkingContent = null)
        {
            if (string.IsNullOrWhiteSpace(completedStep.AiResponse) && string.IsNullOrWhiteSpace(thinkingContent)) return;

            // 检测范围：正式输出 + 思考内容（合并后同一套正则均生效）
            string response = (completedStep.AiResponse ?? "") + "\n[思考内容]\n" + (thinkingContent ?? "");
            var autoCompletedIndices = new HashSet<int>();

            // 模式1: 中文 "步骤2、3也完成了" / "步骤2和3已完成" / "也完成了步骤2,3"
            var cnPatterns = new[]
            {
                @"步骤\s*(\d+(?:[、,，\s]+(?:和|及|与)?\s*\d+)*)\s*(?:也|已|同样|一并|同时)?\s*(?:完成|做完|搞定)",
                @"(?:也|已|同样|一并|同时)\s*(?:完成|做完|搞定)了?\s*步骤\s*(\d+(?:[、,，\s]+(?:和|及|与)?\s*\d+)*)",
            };
            foreach (var pattern in cnPatterns)
            {
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(response, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    ParseStepNumbers(m.Groups[1].Value, autoCompletedIndices);
                }
            }

            // 模式2: 英文 "also completed step 2,3" / "steps 2 and 3 are done"
            var enPatterns = new[]
            {
                @"(?:also\s+)?(?:completed?|finished?|done)\s+steps?\s*(\d+(?:[,\s]+(?:and\s+)?\d+)*)",
                @"steps?\s*(\d+(?:[,\s]+(?:and\s+)?\d+)*)\s*(?:are\s+)?(?:also\s+)?(?:done|completed?|finished?)",
            };
            foreach (var pattern in enPatterns)
            {
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(response, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    ParseStepNumbers(m.Groups[1].Value, autoCompletedIndices);
                }
            }

            // 模式3（原"勾号+步骤"）：勾号字形已随 emoji 清理移除，
            // 无完成词的裸步骤列表不再作为自动完成依据，避免误标

            // 模式4: 范围式声明 "步骤1-6 已完成" / "步骤1~6" / "步骤1到6" / "步骤1至6"
            // "steps 1 through 6 done" / "steps 1-6 completed"
            var rangePatterns = new[]
            {
                // "步骤2-4 也随之完成" / "步骤1~6 已完成" / "步骤1-6做完"
                @"步骤\s*(\d+)\s*[-~—–]\s*(\d+)\s*[也已均都随之一同顺并]*\s*(?:完成|做完|搞定)",
                @"步骤\s*(\d+)\s*(?:到|至|一直到)\s*(\d+)\s*[也已均都随之一同顺并]*\s*(?:完成|做完|搞定)",
                @"(?:也|已|同样|一并|同时)\s*完成(?:了)?\s*步骤\s*(\d+)\s*[-~—–到至]\s*(\d+)",
                @"steps?\s*(\d+)\s*(?:through|to|-|~|&amp;)\s*(\d+)\s*(?:are\s+)?(?:also\s+)?(?:done|completed|finished)",
            };
            foreach (var pattern in rangePatterns)
            {
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(response, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    if (int.TryParse(m.Groups[1].Value, out int start) && int.TryParse(m.Groups[2].Value, out int end))
                    {
                        if (start > 0 && end >= start && end - start <= 50)
                        {
                            for (int n = start; n <= end; n++)
                                autoCompletedIndices.Add(n);
                        }
                    }
                }
            }

            // 过滤：仅处理当前步骤之后的 Pending 步骤
            var toAutoComplete = autoCompletedIndices
                .Where(idx => idx > completedStep.Index && idx <= plan.Steps.Count)
                .Select(idx => plan.Steps[idx - 1])
                .Where(s => s.Status == AgentStepStatus.Pending)
                .ToList();

            foreach (var s in toAutoComplete)
            {
                s.Status = AgentStepStatus.Completed;
                s.ResultSummary = LocalizationService.Instance.Format(
                    "agent.log.editStepClaimedByAiSummary", completedStep.Index);
                AddLog("INFO", "[EditAgent] " + LocalizationService.Instance.Format(
                    "agent.log.editStepClaimedByAi", s.Index, s.Title));
            }

            if (toAutoComplete.Count > 0)
            {
                // 推进 CurrentStepIndex 到最后一个已完成/跳过步骤，
                // 使 UI 顶栏进度(如 "6/10")随之同步
                // （步骤2..n 由 AI 声明完成时若仍停在旧索引，状态栏会一直显示旧计数）。
                int advanced = completedStep.Index;
                for (int idx = completedStep.Index + 1; idx <= plan.Steps.Count; idx++)
                {
                    var s = plan.Steps[idx - 1];
                    if (s.Status is AgentStepStatus.Completed or AgentStepStatus.Skipped)
                        advanced = idx;
                    else
                        break;
                }
                plan.CurrentStepIndex = advanced;
                NotifyPlanUpdated();
            }
        }

        /// <summary>
        /// 解析步骤编号字符串（如 "2、3、5" 或 "2,3,5" 或 "2 and 3"）并加入集合。
        /// </summary>
        private static void ParseStepNumbers(string text, HashSet<int> result)
        {
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(text, @"\d+"))
            {
                if (int.TryParse(m.Value, out int num) && num > 0)
                    result.Add(num);
            }
        }

        /// <summary>
        /// Plan 级别变更追踪（v1.1.10）。
        /// 对比所有步骤描述中引用的文件与实际修改的文件，
        /// 发现遗漏时在日志中提示，帮助用户判断计划是否执行完整。
        /// </summary>
        private void PerformPlanChangeTracking(AgentTaskPlan plan, AgentContext context)
        {
            if (plan.Steps.Count == 0 || plan.ChangedFiles.Count == 0)
                return;

            // 从所有步骤描述中提取文件引用
            var allMentioned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var step in plan.Steps)
            {
                if (string.IsNullOrWhiteSpace(step.Description)) continue;
                var matches = StepFileReferenceRegex.Matches(step.Description);
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    allMentioned.Add(m.Groups[1].Value);
                }
            }

            if (allMentioned.Count == 0) return;

            // 收集所有实际修改的文件名
            var allModified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ch in plan.ChangedFiles)
            {
                allModified.Add(Path.GetFileName(ch.FilePath));
            }

            // 计划中提到但完全没被修改的文件
            var neverTouched = allMentioned
                .Where(f => !allModified.Contains(f))
                .ToList();

            // 实际修改但计划中未提到的文件（额外修改，可能是好事也可能是跑偏）
            var extraModified = allModified
                .Where(f => !allMentioned.Contains(f))
                .ToList();

            if (neverTouched.Count > 0)
            {
                AddLog("WARN", string.Format(
                    "[EditAgent] " + LocalizationService.Instance["agent.log.editPlanTrackingNeverTouched"],
                    allMentioned.Count, neverTouched.Count,
                    string.Join(", ", neverTouched.Take(10))));
            }

            if (extraModified.Count > 0)
            {
                AddLog("INFO", string.Format(
                    "[EditAgent] " + LocalizationService.Instance["agent.log.editPlanTrackingExtraModified"],
                    extraModified.Count,
                    string.Join(", ", extraModified.Take(10))));
            }

            if (neverTouched.Count == 0 && extraModified.Count == 0)
            {
                AddLog("INFO", "[EditAgent] " + LocalizationService.Instance.Format(
                    "agent.log.editFileTrackingConsistent", allMentioned.Count));
            }
        }

        /// <summary>
        /// 检测 AI 是否明确表示没有需要更改的内容。
        /// 空响应可能来自 token 截断，不能据此跳过整个编辑步骤。
        /// </summary>
        /// <remarks>
        /// 长度闸门（短回复才算数）是为了挡住「长篇辩解自己为何没干活」——见
        /// <c>ClassifyNoToolCallStep_SplitsEmptySatisfiedAndTextOnly</c> 里那条长文本必须判失败的用例。
        /// 但它会误伤另一类步骤：像「回归风险清单与手动验证」这种<em>以文字交付物为目的</em>的收尾步骤，
        /// 本来就不产生文件修改，回复又长又有结构，于是被冤判 TextOnlyFailure 而整轮失败。
        /// 因此对长回复补一条出口：<em>有结构</em>（标题/列表/表格）且声明了完成即视为已完成；
        /// 纯粹的流水叙述即使很长也不算，闸门对「辩解」仍然有效。
        /// </remarks>
        private static bool IsNoChangesResponse(string aiResult)
        {
            if (string.IsNullOrWhiteSpace(aiResult)) return false;

            string clean = StripDsmlContent(aiResult, removeResidualAttributes: false);
            clean = System.Text.RegularExpressions.Regex.Replace(clean,
                @"```[\s\S]*?```", string.Empty);
            clean = System.Text.RegularExpressions.Regex.Replace(clean,
                @"</?think>", string.Empty);
            clean = System.Text.RegularExpressions.Regex.Replace(clean,
                @"\s*think\s*", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (string.IsNullOrWhiteSpace(clean)) return false;

            var noChangesPatterns = new[]
            {
                @"不需要修改|无需修改|没有需要更改|无变更|已完成",
                @"无需.*(?:修改|更改|变更|编辑)",
                @"已经.*(?:完成|好了|修改好)",
                @"all\s+changes?\s+(?:are\s+)?done",
                @"no\s+(?:further\s+)?changes?\s+(?:needed|required)",
                @"nothing\s+to\s+(?:change|modify|edit)",
                @"已推送|推送成功|推送完成|push.*(?:success|done|ok)",
                @"已提交|提交成功|commit.*(?:success|done|ok)",
                @"已暂存|已添加|add.*(?:success|done|ok)|暂存.*成功",
                @"stash.*(?:success|done)",
                @"切换.*成功|已切换到|checkout.*success",
                @"(?:git\s+)?操作.*(?:完成|成功|已执行)",
                @"^(?:OK|Done|完成|好了|搞定|成功|已执行|已处理)[。！!.\s]*$",
            };

            bool matched = false;
            foreach (var pattern in noChangesPatterns)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(clean, pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched) return false;

            // 短回复：维持原判定（含 Git 动作等一次性声明）。
            if (clean.Trim().Length < 200) return true;

            // 长回复：仅当它是「有结构的交付物」时才认，纯叙述仍判失败。
            return IsStructuredDeliverable(clean);
        }

        /// <summary>
        /// 长回复是否为「有结构的交付物」：含 Markdown 标题、有序/无序列表或表格。
        /// 用于把「以文字交付为目的」的收尾步骤（风险清单、验证步骤、后续操作说明）
        /// 与「长篇辩解自己为何没干活」区分开。判定只看结构，不猜语义。
        /// </summary>
        /// <param name="clean">已剥离代码块与思考标记的回复正文。</param>
        private static bool IsStructuredDeliverable(string clean)
        {
            if (string.IsNullOrWhiteSpace(clean)) return false;

            string[] structuralPatterns =
            {
                @"^\s{0,3}#{1,6}\s+\S",          // Markdown 标题
                @"^\s{0,3}[-*+]\s+\S",           // 无序列表
                @"^\s{0,3}\d+[.)]\s+\S",         // 有序列表
                @"^\s{0,3}\|\s*\S.*\|",          // 表格行
            };

            int signals = 0;
            foreach (string line in clean.Split('\n'))
            {
                foreach (string pattern in structuralPatterns)
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(
                            line, pattern,
                            System.Text.RegularExpressions.RegexOptions.Multiline))
                    {
                        signals++;
                        break;   // 同一行只计一次
                    }
                }
            }

            // 至少两条结构性线索，避免单行「- 无」这类噪声被当成交付物。
            return signals >= 2;
        }

        #region Tool-Made Edit Detection (v1.1.10)

        /// <summary>
        /// 截取当前步骤工具循环期间新增的消息（排除 Handoff/上下文中历史工具调用）。
        /// 防止历史中的 create_file 等调用被误判为本轮“工具编辑”。
        /// </summary>
        private static List<ChatApiMessage> GetStepToolLoopMessages(
            List<ChatApiMessage> messages, int startIndex)
        {
            if (messages == null || messages.Count == 0 || startIndex < 0)
                return new List<ChatApiMessage>();
            if (startIndex >= messages.Count)
                return new List<ChatApiMessage>();

            int count = messages.Count - startIndex;
            var slice = new List<ChatApiMessage>(count);
            for (int i = startIndex; i < messages.Count; i++)
            {
                slice.Add(messages[i]);
            }
            return slice;
        }

        /// <summary>
        /// 从消息列表中提取通过工具调用完成的文件编辑记录。
        /// 解析 tool_calls 中的 JSON 参数，提取目标文件路径。
        /// </summary>
        /// <returns>列表元素: (解析后的绝对路径, 工具名)</returns>
        private static List<(string FilePath, string ToolName)> ExtractToolMadeEdits(
            List<ChatApiMessage> messages)
        {
            var edits = new List<(string FilePath, string ToolName)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var msg in messages)
            {
                if (msg.ToolCalls == null || msg.ToolCalls.Count == 0) continue;

                foreach (var tc in msg.ToolCalls)
                {
                    string name = tc.Function?.Name ?? "";
                    string args = tc.Function?.Arguments ?? "{}";

                    // 仅处理编辑类工具
                    if (name != "replace_string_in_file" &&
                        name != "multi_replace_string_in_file" &&
                        name != "create_file" &&
                        name != "delete_file" &&
                        name != "apply_patch")
                        continue;

                    string? filePath = null;
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(args);
                        var root = doc.RootElement;

                        if (name == "apply_patch"
                            && root.TryGetProperty("patch", out var patchElement))
                        {
                            string patchText = patchElement.GetString() ?? string.Empty;
                            foreach (var patch in Services.EditTools.ApplyPatchTool.ParsePatches(patchText))
                            {
                                string patchPath = string.IsNullOrWhiteSpace(patch.MoveToPath)
                                    ? patch.FilePath
                                    : patch.MoveToPath!;
                                if (!string.IsNullOrEmpty(patchPath) && seen.Add(patchPath))
                                    edits.Add((patchPath, name));
                            }
                            continue;
                        }

                        if (root.TryGetProperty("filePath", out var fp))
                            filePath = fp.GetString();
                        else if (root.TryGetProperty("path", out var p))
                            filePath = p.GetString();
                    }
                    catch
                    {
                        // JSON 解析失败，尝试正则提取 filePath
                        var match = System.Text.RegularExpressions.Regex.Match(args,
                            @"""filePath""\s*:\s*""([^""]+)""");
                        if (match.Success)
                            filePath = match.Groups[1].Value;
                    }

                    if (!string.IsNullOrEmpty(filePath) && seen.Add(filePath!))
                    {
                        edits.Add((filePath!, name));
                    }
                }
            }
            return edits;
        }

        /// <summary>
        /// 收集工具循环中完成的文件编辑，将其转换为 EditApplyResult 并加入 appliedResults。
        /// 同时追踪变更到 plan.ChangedFiles。
        /// </summary>
        private async Task CollectToolMadeEditsAsync(
            List<(string FilePath, string ToolName)> toolEdits,
            AgentTaskPlan plan,
            string workspaceRoot,
            Dictionary<string, string> originalContents,
            List<EditApplyResult> appliedResults,
            CancellationToken ct)
        {
            foreach (var (filePath, toolName) in toolEdits)
            {
                string resolvedPath = EditPatchService.ResolvePath(filePath, workspaceRoot);

                // ── 确定操作类型 ──
                var opType = toolName switch
                {
                    "create_file" => EditOperationType.CreateFile,
                    "delete_file" => EditOperationType.DeleteFile,
                    "apply_patch" => EditOperationType.ApplyPatch,
                    _ => EditOperationType.ApplyPatch, // replace_string_in_file 等归为 Patch 类
                };

                bool fileExists = File.Exists(resolvedPath);
                bool isNewFile = _stagedWorkspace?.GetOperation(resolvedPath) == ProposedFileOperation.Add
                    || (toolName == "create_file" && !fileExists);

                // ── 为新文件设置空原始内容，供变更统计使用 ──
                if (isNewFile)
                {
                    originalContents[resolvedPath] = string.Empty;
                }

                if (toolName == "delete_file")
                {
                    appliedResults.Add(new EditApplyResult
                    {
                        FilePath = resolvedPath,
                        Success = true,
                        OperationType = EditOperationType.DeleteFile,
                    });
                    NotifyFileChange(plan.PlanId, "delete", resolvedPath,
                    LocalizationService.Instance.Format("agent.log.editToolEditNotify", "delete_file"));

                    if (!plan.ChangedFiles.Any(c => string.Equals(c.FilePath, resolvedPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        plan.ChangedFiles.Add(new FileChangeSummary
                        {
                            FilePath = resolvedPath,
                            LinesAdded = 0,
                            LinesRemoved = -1,
                            BriefDescription = $"{Path.GetFileName(resolvedPath)} (delete_file)",
                        });
                    }
                    continue;
                }

                if (!fileExists && !isNewFile)
                {
                    AddLog("WARN", "[EditAgent] " + LocalizationService.Instance.Format(
                    "agent.log.editToolEditTargetMissing", Path.GetFileName(resolvedPath), toolName));
                    appliedResults.Add(new EditApplyResult
                    {
                        FilePath = resolvedPath,
                        Success = false,
                        OperationType = opType,
                        ErrorMessage = "文件不存在",
                    });
                    continue;
                }

                // ── 工具编辑已在磁盘生效；新建文件使用空基线，其余文件由 Workspace 保存原始快照。──

                // ── 新文件处理：添加到项目 ──
                if (isNewFile && fileExists)
                {
                    await AddFileToProjectAsync(resolvedPath, ct);
                }

                // ── 记录编辑结果 ──
                AddLog("INFO", "[EditAgent] " + LocalizationService.Instance.Format(
                    "agent.log.editToolEditApplied", Path.GetFileName(resolvedPath), toolName));

                appliedResults.Add(new EditApplyResult
                {
                    FilePath = resolvedPath,
                    Success = true,
                    OperationType = opType,
                });

                // ── 变更通知 ──
                string changeType = isNewFile ? "create" : "modify";
                NotifyFileChange(plan.PlanId, changeType, resolvedPath,
                    LocalizationService.Instance.Format("agent.log.editToolEditNotify", toolName));

                // ── 更新 plan.ChangedFiles ──
                if (!plan.ChangedFiles.Any(c => string.Equals(c.FilePath, resolvedPath, StringComparison.OrdinalIgnoreCase)))
                {
                    int added = 0, removed = 0;
                    if (isNewFile || !fileExists)
                    {
                        added = 1; // 新文件或异常情况
                    }
                    else
                    {
                        // 工具修改了已存在的文件，无法获取原始内容做精确 diff，
                        // 使用最终文件行数作为变更量估算
                        try
                        {
                            string content = await Task.Run(() => FileEncodingHelper.ReadAllText(resolvedPath), ct);
                            added = CountLines(content);
                        }
                        catch { added = 1; }
                    }

                    plan.ChangedFiles.Add(new FileChangeSummary
                    {
                        FilePath = resolvedPath,
                        LinesAdded = added,
                        LinesRemoved = removed,
                        BriefDescription = $"{Path.GetFileName(resolvedPath)} ({toolName})",
                    });
                }
            }
        }

        #endregion

        #endregion

        #region Build State

        /// <summary>
        /// 按消息顺序追踪本轮 build_solution 与文件修改，确保最终构建状态来自最后一次有效构建。
        /// 如果构建后又修改了文件，则把上次构建标记为失效，后续必须由 Build Agent 重新构建。
        /// </summary>
        private void TrackBuildStateFromToolMessages(List<ChatApiMessage> messages)
        {
            if (messages == null || messages.Count == 0)
                return;

            var toolResults = messages
                .Where(m => m.Role == "tool" && !string.IsNullOrEmpty(m.ToolCallId))
                .GroupBy(m => m.ToolCallId!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Last().Content, StringComparer.Ordinal);

            foreach (var message in messages)
            {
                if (message.ToolCalls == null || message.ToolCalls.Count == 0)
                    continue;

                foreach (var toolCall in message.ToolCalls)
                {
                    string toolName = toolCall.Function?.Name ?? string.Empty;

                    if (IsFileModificationToolName(toolName))
                    {
                        // 文件在构建后发生变化，旧构建结果不再可信。
                        _lastBuildSucceeded = null;
                        continue;
                    }

                    if (!string.Equals(toolName, "build_solution", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string? buildResult = null;
                    if (!string.IsNullOrEmpty(toolCall.Id))
                        toolResults.TryGetValue(toolCall.Id, out buildResult);

                    _didAttemptBuild = true;
                    _lastBuildSucceeded = DeepSeek_v4_for_VisualStudio.Services.BuiltInTools.BuildSolutionTool.IsSuccessResult(buildResult);

                    if (!string.IsNullOrWhiteSpace(buildResult))
                        _lastDirectBuildResult = buildResult;
                }
            }
        }

        private static bool IsFileModificationToolName(string toolName)
        {
            return toolName is "create_file"
                or "delete_file"
                or "replace_string_in_file"
                or "multi_replace_string_in_file"
                or "apply_patch"
                or "create_directory";
        }

        #endregion

        #region Step Classification & Prompt

        /// <summary>
        /// 生成计划进度快照（已完成/当前/待执行步骤列表）。
        /// 工具循环中消息历史会保留旧的“当前步骤”提示，明确列出进度可避免模型误读。
        /// </summary>
        internal static string BuildPlanProgressSnapshot(AgentTaskPlan plan)
        {
            if (plan == null || plan.Steps.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("## 计划进度");
            int current = Math.Max(1, Math.Min(plan.CurrentStepIndex, plan.Steps.Count));
            foreach (var s in plan.Steps)
            {
                string state = (s.Index == current)
                    ? "▶ 当前"
                    : s.Status switch
                    {
                        AgentStepStatus.Completed => "✓ 已完成",
                        AgentStepStatus.Skipped => "– 已跳过",
                        AgentStepStatus.Failed => "✗ 失败",
                        _ => "○ 待执行",
                    };
                sb.AppendLine($"- {state} 步骤 {s.Index}: {s.Title}");
            }
            sb.AppendLine();
            sb.AppendLine("> 如果你在当前步骤内顺带完成后继步骤，系统会在本步骤结束时自动同步状态并推进进度；不要等待新的步骤提示，直接完成并在回复末尾声明。");
            return sb.ToString();
        }

        #region Build Permission — 步骤级构建许可

        /// <summary>
        /// 步骤文本中表示"本步骤需要构建/编译/验证"的关键词。
        /// 命中任一关键词即认为该步骤明确要求构建，允许调用 build_solution。
        /// 匹配不区分大小写；中文关键词按子串匹配，英文按词边界匹配以避免
        /// 例如 "test" 命中 "latest" 这类误判。
        /// </summary>
        private static readonly string[] BuildIntentKeywordsZh = new[]
        {
            "构建", "编译", "生成解决方案", "验证构建", "构建验证", "重新构建",
        };

        private static readonly string[] BuildIntentKeywordsEn = new[]
        {
            "build", "compile", "rebuild", "msbuild", "dotnet build",
        };

        /// <summary>
        /// 判断某步骤是否允许调用 build_solution。
        /// 规则（按用户约定）：仅当
        ///   ① 当前步骤<strong>标题</strong>明确要求构建（关键词命中），或
        ///   ② 当前步骤是计划的最后一步
        /// 时允许构建；其余步骤禁用，避免每个中间步骤都触发一次昂贵的解决方案构建。
        /// </summary>
        /// <remarks>
        /// 只匹配 <see cref="AgentStep.Title"/>，不再拼接 <see cref="AgentStep.Description"/>。
        /// 描述里常出现「确保可编译」「避免编译错误」这类<em>约束性说法</em>，语义上并不要求
        /// 本步骤执行构建；而中文关键词按子串匹配、无词边界与否定语气识别，一旦纳入描述
        /// 就会把这类步骤误判为构建意图，令白名单不裁剪 build_solution。
        /// 标题是计划作者对本步骤动作的凝练表达，作为「是否构建」的判据更可预测。
        /// 注：代价是标题未写构建、描述却明确要求构建时不再放行；此类步骤仍可依赖
        /// ②「最后一步」兜底，或由计划作者把构建意图写进标题。
        /// </remarks>
        /// <param name="step">当前步骤</param>
        /// <param name="plan">所属计划（用于判定是否为最后一步）</param>
        /// <param name="reason">命中的允许原因（用于提示词文案），不允许时为 null</param>
        internal static bool IsBuildAllowedForStep(AgentStep step, AgentTaskPlan plan, out string? reason)
        {
            reason = null;
            if (step == null || plan == null)
                return false;

            // ① 步骤标题明确要求构建
            if (ContainsBuildIntent(step.Title))
            {
                reason = LocalizationService.Instance["agent.step.buildAllowedReasonExplicit"];
                return true;
            }

            // ② 最后一步：收敛验证点，允许构建
            //    注：net472 不支持 System.Index（[^1]），使用传统索引。
            int lastIndex = plan.Steps.Count > 0
                ? plan.Steps[plan.Steps.Count - 1].Index
                : step.Index;
            if (step.Index >= lastIndex)
            {
                reason = LocalizationService.Instance["agent.step.buildAllowedReasonLastStep"];
                return true;
            }

            return false;
        }

        /// <summary>
        /// 步骤文本是否表达了构建意图。中文关键词子串匹配；英文关键词要求词边界，
        /// 避免 "build" 之外的子串误命中（如 "rebuild" 需单独列出）。
        /// </summary>
        internal static bool ContainsBuildIntent(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            foreach (string kw in BuildIntentKeywordsZh)
            {
                if (text.Contains(kw, StringComparison.Ordinal))
                    return true;
            }

            foreach (string kw in BuildIntentKeywordsEn)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(
                        text,
                        $@"(?<![A-Za-z]){System.Text.RegularExpressions.Regex.Escape(kw)}(?![A-Za-z])",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        #endregion

        private string BuildStepPrompt(AgentStep step, AgentTaskPlan plan,
            AgentContext context)
        {
            var sb = new StringBuilder();

            // ── 步骤推进语义（v1.1.14 精简）──
            // 步骤切换发生在同一 Agent 内部，user 消息只承载"当前该做哪一步"：
            //   1) plan 标题（同计划内恒定，最稳定，利于前缀缓存）
            //   2) 当前步骤标题：当前步骤 (n/N): 标题
            //   3) plan.md 中该步骤对应的章节（若有 plan.md）
            // 任务描述、累积上下文、缓存文件内容等由对话历史（上一步骤的
            // assistant/tool 结果）自然承接，不再逐步重发，避免每次步骤推进重复注入
            // 数十 KB 的冗余上下文。

            // 第1层：Plan 标题（同计划内所有步骤完全相同，最稳定）
            // ── 该 user 消息即「当前任务目标」──
            //   EditAgent 走 BuildContextAwareMessages(..., deduplicateCurrentUser: false)，
            //   步骤提示是**新追加**的 user，不会经过 Handoff 分支的
            //   ApplyCurrentUserQuestionPrefix，也不会经过 AddUserMessage（步骤提示不入 _entries）。
            //   因此必须在此处显式打标：上下文最长、身份刚切换，历史里堆着源 Agent 的
            //   user 轮次与工具记录，没有该标记时模型容易把中间内容误当成本轮目标。
            sb.Append(LocalizationService.Instance["system.agent.handoffTaskGoalPrefix"]);
            sb.AppendLine(string.Format(AiPrompts.EditStepPromptPrefix, plan.Title));
            sb.AppendLine();

            // 第2层：当前步骤标题（每步唯一变化的部分）
            sb.AppendLine(string.Format(
                LocalizationService.Instance["agent.step.currentStepPrompt"],
                step.Index, plan.Steps.Count, step.Title));
            sb.AppendLine();

            // 第3层：plan.md 中当前步骤对应的章节详情
            string? planFilePath = context.PlanFilePath ?? plan.PlanFilePath;
            if (!string.IsNullOrEmpty(planFilePath) && File.Exists(planFilePath))
            {
                try
                {
                    string planMd = File.ReadAllText(planFilePath);
                    if (planMd.Length > 0)
                    {
                        string stepSection = ExtractPlanMdStepSection(planMd, step);
                        if (!string.IsNullOrEmpty(stepSection))
                        {
                            sb.AppendLine(string.Format(
                                LocalizationService.Instance["agent.step.planMdDetail"], step.Index));
                            sb.AppendLine(stepSection);
                            sb.AppendLine();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[EditAgent] 读取 plan.md 章节失败: {ex.Message}");
                }
            }

            // 第4层：复用对话历史的约束（每步恒定，置于尾部不影响前缀缓存）
            // 代码记忆功能移除后，跨步骤的文件内容只能靠对话历史中的 read_file 结果承接，
            // 显式提示模型优先复用，避免它重新读取未变化的文件。
            sb.AppendLine(LocalizationService.Instance["agent.step.reuseHistoryHint"]);
            sb.AppendLine();

            // 第5层：构建许可说明（仅当本步骤允许构建时给出；不允许时由工具白名单拦截）
            if (IsBuildAllowedForStep(step, plan, out string? buildReason))
            {
                sb.AppendLine(string.Format(
                    LocalizationService.Instance["agent.step.buildAllowedHint"], buildReason));
            }
            else
            {
                sb.AppendLine(LocalizationService.Instance["agent.step.buildBlockedHint"]);
            }

            return sb.ToString();
        }

        /// <summary>
        /// 从 plan.md 中提取概述部分（详细步骤章节之前的内容），按章节边界截断。
        /// 在"详细步骤"/"Detailed Steps"章节标题前切断，保留项目目标、结构分析等概述信息。
        /// </summary>
        private static string ExtractPlanMdOverview(string planMd)
        {
            // 找到"详细步骤"章节的起始位置（中英文两种模式）
            var stepSectionPatterns = new[]
            {
                "### 3.", "### 2.", "### 3 ", "### 2 ",
                "## 详细步骤", "## Detailed Steps",
 "## ", "## 实现步骤", "## Implementation",
                "## 步骤", "## Steps"
            };

            int cutPos = planMd.Length;
            foreach (var pattern in stepSectionPatterns)
            {
                int idx = planMd.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                if (idx > 0 && idx < cutPos)
                    cutPos = idx;
            }

            string overview = planMd.Substring(0, cutPos).TrimEnd();
            const int maxOverviewChars = 2000;
            if (overview.Length > maxOverviewChars)
            {
                // 在 maxOverviewChars 附近找最近的 \n\n 段落边界切断
                int boundary = overview.LastIndexOf("\n\n", maxOverviewChars, StringComparison.Ordinal);
                if (boundary > maxOverviewChars / 2)
                    overview = overview.Substring(0, boundary).TrimEnd() + "\n\n... (概述已截断)";
                else
                    overview = overview.Substring(0, maxOverviewChars) + "\n... (概述已截断)";
            }

            return overview;
        }

        /// <summary>
        /// 从 plan.md 中提取当前步骤对应的章节内容。
        /// 匹配策略：按步骤索引号（如 "步骤 1"、"Step 1"、"1."）定位到下一个同级/上级标题。
        /// </summary>
        private static string ExtractPlanMdStepSection(string planMd, AgentStep step)
        {
            // 构建匹配模式：支持 "步骤 N"、"Step N"、"#### N."、"#### Step N" 等多种格式
            var patterns = new[]
            {
                $"#### 步骤 {step.Index}:", $"#### 步骤 {step.Index}：",
                $"#### Step {step.Index}:", $"#### Step {step.Index}.",
                $"#### {step.Index}.", $"#### {step.Index} ",
                $"### 步骤 {step.Index}:", $"### 步骤 {step.Index}：",
                $"### Step {step.Index}:", $"### Step {step.Index}.",
                $"### {step.Index}.", $"### {step.Index} ",
            };

            int startIdx = -1;
            foreach (var pattern in patterns)
            {
                int idx = planMd.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    startIdx = idx;
                    break;
                }
            }

            if (startIdx < 0)
                return string.Empty;

            // 从该步骤标题的下一行开始提取
            int contentStart = planMd.IndexOf('\n', startIdx);
            if (contentStart < 0) return string.Empty;
            contentStart++; // 跳过换行符

            // 找到下一个 ## 或 ### 或 #### 标题作为结束边界
            int endIdx = planMd.Length;
            var headerPattern = System.Text.RegularExpressions.Regex.Match(
                planMd, @"^#{2,4}\s", System.Text.RegularExpressions.RegexOptions.Multiline);
            
            // 使用逐行扫描找下一个标题
            int searchStart = contentStart;
            int nextHeader = planMd.IndexOf("\n##", searchStart, StringComparison.Ordinal);
            if (nextHeader < 0) nextHeader = planMd.IndexOf("\r\n##", searchStart, StringComparison.Ordinal);
            if (nextHeader >= 0) endIdx = nextHeader;
            
            // 也检查 ### 和 ####
            int nextH3 = planMd.IndexOf("\n###", searchStart, StringComparison.Ordinal);
            if (nextH3 < 0) nextH3 = planMd.IndexOf("\r\n###", searchStart, StringComparison.Ordinal);
            if (nextH3 >= 0 && nextH3 < endIdx) endIdx = nextH3;
            
            int nextH4 = planMd.IndexOf("\n####", searchStart, StringComparison.Ordinal);
            if (nextH4 < 0) nextH4 = planMd.IndexOf("\r\n####", searchStart, StringComparison.Ordinal);
            if (nextH4 >= 0 && nextH4 < endIdx) endIdx = nextH4;

            string section = planMd.Substring(contentStart, endIdx - contentStart).Trim();
            
            // 截断过长内容
            const int maxSectionChars = 3000;
            if (section.Length > maxSectionChars)
            {
                int boundary = section.LastIndexOf("\n\n", maxSectionChars, StringComparison.Ordinal);
                if (boundary > maxSectionChars / 2)
                    section = section.Substring(0, boundary).TrimEnd() + "\n\n... (章节内容已截断)";
                else
                    section = section.Substring(0, maxSectionChars) + "\n... (章节内容已截断)";
            }

            return section;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// 需要用户确认才能修改的项目文件扩展名集合。
        /// 修改这些文件可能影响项目结构，需要用户明确许可。
        /// </summary>
        /// <summary>
        /// 构建定义文件名集合（CMakeLists.txt、Makefile 等）。
        /// 这些文件引用源文件，因此必须在源文件创建完成后才能写入，
        /// 否则构建系统会在文件还不存在时尝试编译它们。
        /// </summary>
        private static readonly HashSet<string> BuildDefinitionFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CMakeLists.txt", "Makefile", "GNUmakefile", "makefile",
        };

        /// <summary>
        /// 检查文件是否为构建定义文件（CMakeLists.txt / Makefile 等）。
        /// 构建定义文件引用源文件，必须在源文件创建完成后才能处理。
        /// </summary>
        private static bool IsBuildDefinitionFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            string fileName = Path.GetFileName(filePath);
            return BuildDefinitionFileNames.Contains(fileName);
        }

        /// <summary>
        /// 在写入项目文件前请求用户确认。
        /// 非项目文件直接返回 true（放行）。
        /// </summary>
        /// <param name="filePath">目标文件绝对路径</param>
        /// <param name="operationDescription">操作描述（如"修改 leetcode.vcxproj"）</param>
        /// <param name="fileContent">可选，即将写入的文件内容（用于向用户展示变更预览，自动截断过长内容）</param>
        /// <param name="purpose">操作目的（告诉用户为什么要修改此项目文件，如"添加新源文件到项目中"）</param>
        /// <returns>true=允许写入, false=用户拒绝</returns>
        private async Task<bool> EnsureProjectFileWriteConfirmedAsync(string filePath, string operationDescription = "", string fileContent = "", string purpose = "")
        {
            if (!IsProjectFile(filePath))
                return true; // 非项目文件，直接放行

            string fileName = Path.GetFileName(filePath);
            string desc = !string.IsNullOrEmpty(operationDescription)
                ? operationDescription
                : $"修改项目文件: {fileName}";

            // 自动推断目的（如果调用方未提供）
            string effectivePurpose = purpose;
            if (string.IsNullOrEmpty(effectivePurpose))
            {
                if (operationDescription.Contains("新建") || operationDescription.Contains("create_file"))
                    effectivePurpose = "创建新文件需要更新项目配置以将其纳入编译";
                else if (operationDescription.Contains("删除") || operationDescription.Contains("移除"))
                    effectivePurpose = "删除文件后需要从项目配置中移除对应引用";
                else
                    effectivePurpose = "代码修改涉及项目配置变更，需要更新项目文件以保持一致";
            }

            AddLog("WARN", LocalizationService.Instance.Format("agent.log.editProjectModDetected", fileName));

            // 构造内容预览（截断过长内容，保留前后各 30 行）
            string detail = "";
            if (!string.IsNullOrWhiteSpace(fileContent))
            {
                const int maxPreviewLines = 60;
                var lines = fileContent.Replace("\r\n", "\n").Split('\n');
                if (lines.Length > maxPreviewLines)
                {
                    int headLines = 30;
                    int tailLines = 30;
                    var preview = new System.Text.StringBuilder();
                    preview.AppendLine("```xml");
                    for (int i = 0; i < headLines && i < lines.Length; i++)
                        preview.AppendLine(lines[i]);
                    preview.AppendLine($"... (省略 {lines.Length - headLines - tailLines} 行) ...");
                    for (int i = Math.Max(headLines, lines.Length - tailLines); i < lines.Length; i++)
                        preview.AppendLine(lines[i]);
                    preview.Append("```");
                    detail = preview.ToString();
                }
                else
                {
                    detail = "```xml\n" + string.Join("\n", lines) + "\n```";
                }
            }

            bool approved = await RequestPermissionAsync(
                $"确认修改项目文件: {fileName}",
                $"即将修改项目配置文件 `{fileName}`\n\n路径: {filePath}\n\n{desc}\n\n 修改项目文件可能影响构建配置和项目结构。",
                "file_write",
                detail,
                effectivePurpose);

            if (!approved)
            {
                AddLog("WARN", LocalizationService.Instance.Format("agent.log.projectModDenied", fileName));
            }
            return approved;
        }

        private static AgentTaskPlan CreateSingleStepPlan(string userMessage)
        {
            bool isReadOnlyExecution = IsReadOnlyExecutionRequest(userMessage);
            string stepTitle = isReadOnlyExecution
                ? LocalizationService.Instance["agent.step.executeReadOnlyCommand"]
                : LocalizationService.Instance["agent.step.analyzeAndModify"];

            return new AgentTaskPlan
            {
                Intent = isReadOnlyExecution ? AgentIntent.QandA : AgentIntent.CodeChange,
                Title = isReadOnlyExecution
                    ? LocalizationService.Instance["agent.step.executeReadOnlyCommand"]
                    : LocalizationService.Instance["agent.step.executeCodeChange"],
                Steps = new List<AgentStep>
                {
                    new AgentStep
                    {
                        Index = 1,
                        Title = stepTitle,
                        Description = userMessage,
                        RequiresApproval = false,
                    }
                },
            };
        }

        /// <summary>
        /// 识别“运行命令以读取/输出内容”的只读执行请求。
        /// 这类任务允许执行终端命令，但禁止任何文件写入，避免把输出内容误落地为代码文件。
        /// </summary>
        private static bool IsReadOnlyExecutionRequest(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            string text = message.Trim();

            bool hasExecutionIntent =
                text.Contains("运行", StringComparison.OrdinalIgnoreCase)
                || text.Contains("执行", StringComparison.OrdinalIgnoreCase)
                || text.Contains("终端命令", StringComparison.OrdinalIgnoreCase)
                || text.Contains("python", StringComparison.OrdinalIgnoreCase)
                || text.Contains("powershell", StringComparison.OrdinalIgnoreCase)
                || text.Contains("script", StringComparison.OrdinalIgnoreCase)
                || text.Contains("run ", StringComparison.OrdinalIgnoreCase)
                || text.Contains("execute ", StringComparison.OrdinalIgnoreCase);

            bool hasReadOrOutputIntent =
                text.Contains("读取", StringComparison.OrdinalIgnoreCase)
                || text.Contains("读出", StringComparison.OrdinalIgnoreCase)
                || text.Contains("查看", StringComparison.OrdinalIgnoreCase)
                || text.Contains("显示", StringComparison.OrdinalIgnoreCase)
                || text.Contains("输出", StringComparison.OrdinalIgnoreCase)
                || text.Contains("打印", StringComparison.OrdinalIgnoreCase)
                || text.Contains("read ", StringComparison.OrdinalIgnoreCase)
                || text.Contains("output ", StringComparison.OrdinalIgnoreCase)
                || text.Contains("print ", StringComparison.OrdinalIgnoreCase)
                || text.Contains("show ", StringComparison.OrdinalIgnoreCase);

            bool hasWriteIntent =
                text.Contains("修改", StringComparison.OrdinalIgnoreCase)
                || text.Contains("更改", StringComparison.OrdinalIgnoreCase)
                || text.Contains("创建", StringComparison.OrdinalIgnoreCase)
                || text.Contains("新建", StringComparison.OrdinalIgnoreCase)
                || text.Contains("写入", StringComparison.OrdinalIgnoreCase)
                || text.Contains("保存", StringComparison.OrdinalIgnoreCase)
                || text.Contains("替换", StringComparison.OrdinalIgnoreCase)
                || text.Contains("删除", StringComparison.OrdinalIgnoreCase)
                || text.Contains("实现", StringComparison.OrdinalIgnoreCase)
                || text.Contains("修复", StringComparison.OrdinalIgnoreCase)
                || text.Contains("create", StringComparison.OrdinalIgnoreCase)
                || text.Contains("write", StringComparison.OrdinalIgnoreCase)
                || text.Contains("save", StringComparison.OrdinalIgnoreCase)
                || text.Contains("modify", StringComparison.OrdinalIgnoreCase)
                || text.Contains("change", StringComparison.OrdinalIgnoreCase)
                || text.Contains("update", StringComparison.OrdinalIgnoreCase)
                || text.Contains("delete", StringComparison.OrdinalIgnoreCase)
                || text.Contains("fix", StringComparison.OrdinalIgnoreCase);

            return hasExecutionIntent && hasReadOrOutputIntent && !hasWriteIntent;
        }

        /// <summary>
        /// 获取最近一次适合直接呈现给用户的工具原始输出。
        /// </summary>
        private static string? GetLastUserFacingToolOutput(List<ChatApiMessage> messages)
        {
            return messages
                .LastOrDefault(m => string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(m.Name, "run_in_terminal", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(m.Name, "read_file", StringComparison.OrdinalIgnoreCase)))
                ?.Content;
        }

        /// <summary>
        /// 组装统一工具步骤结果，确保模型摘要没有覆盖或省略用户要求的原始工具输出。
        /// </summary>
        private static string BuildToolStepContent(
            string? aiResult,
            string? rawToolOutput)
        {
            string summary = aiResult?.Trim() ?? string.Empty;
            string raw = rawToolOutput?.Trim() ?? string.Empty;

            if (raw.Length == 0)
                return summary;

            if (summary.Length == 0)
                return raw;

            // 模型已经完整携带原始输出时，不再重复附加。
            if (ContainsNormalized(summary, raw))
                return summary;

            return summary
                + "\n\n--- 完整工具输出 ---\n"
                + raw
                + "\n--- 完整工具输出结束 ---";
        }

        private static bool ContainsNormalized(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle))
                return false;

            string Normalize(string value)
            {
                return value
                    .Replace("\r\n", "\n")
                    .Replace("\r", "\n")
                    .Trim();
            }

            return Normalize(haystack).Contains(
                Normalize(needle),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 取消当前任务。
        /// </summary>
        public void Cancel()
        {
            _agentCts?.Cancel();
            AddLog("WARN", LocalizationService.Instance["edit.summary.cancelled"]);
        }

        private void NotifyPlanUpdated()
        {
            try { PlanUpdated?.Invoke(CurrentPlan!); } catch { }
        }

        /// <summary>
        /// 通过 Toast 通知用户计划执行结果。
        /// </summary>
        private void NotifyPlanCompletionViaToast(AgentTaskPlan plan)
        {
            try
            {
                var toastService = CompositionRoot.GetServiceOrDefault<ToastNotificationService>();
                if (toastService == null)
                    return;

                int completed = plan.Steps.Count(s => s.Status == AgentStepStatus.Completed);
                int failed = plan.Steps.Count(s => s.Status == AgentStepStatus.Failed);
                int total = plan.Steps.Count;

                if (plan.IsCancelled)
                {
                    toastService.Show(
                        "DeepSeek",
                        string.Format(LocalizationService.Instance["toast.taskCancelled"], completed, total));
                    return;
                }

                // ── 编译存在问题且即将移交 Build 修复：本阶段不通知 ──
                // 完成通知统一由 Ask Agent 在最终总结生成后弹出，避免“任务完成”的误提示。
                if (RequiresBuildRepairToast(HasBuildWarningsInLogs(), ShouldSkipAutoBuild()))
                    return;

                // ── 普通代码任务会继续移交 Ask 出总结：这里不提前通知 ──
                // 只有无 Ask 移交的只读/输出任务（QandA）才在此通知执行结果。
                if (plan.Intent != AgentIntent.QandA)
                    return;

                if (plan.IsCompleted && failed == 0)
                {
                    toastService.Show(
                        "DeepSeek",
                        string.Format(LocalizationService.Instance["toast.taskComplete"], completed, total));
                }
                else if (plan.IsCompleted && failed > 0)
                {
                    toastService.Show(
                        "DeepSeek",
                        string.Format(LocalizationService.Instance["toast.taskPartialComplete"], completed, total, failed));
                }
            }
            catch
            {
                // Toast 通知失败不应影响主流程
            }
        }

        /// <summary>
        /// 判断是否应推迟“任务完成”通知：最终构建存在编译问题且即将移交 Build 修复时，
        /// 只提示“正在修复”，避免给用户已完成的错觉。
        /// </summary>
        internal static bool RequiresBuildRepairToast(bool hasBuildWarnings, bool skipAutoBuild)
        {
            return hasBuildWarnings && !skipAutoBuild;
        }

        /// <summary>
        /// 判断是否必须把最终构建交给 Build Agent。
        /// 有文件变更时，只有“本轮确实构建过且最后一次构建成功”才能跳过；
        /// 未构建、构建失败或构建后又修改文件，都需要 Build Agent 处理。
        /// </summary>
        internal static bool ShouldHandoffToBuild(
            bool hasFileChanges,
            bool didAttemptBuild,
            bool? lastBuildSucceeded,
            bool skipAutoBuild)
        {
            if (skipAutoBuild || !hasFileChanges)
                return false;

            return !didAttemptBuild || lastBuildSucceeded != true;
        }

        /// <summary>
        /// 确定 Handoff 目标：AI 动态移交优先；文件修改后的构建状态决定是否移交 Build Agent；
        /// 无文件变更时跳过构建和 Build 移交，交给 Ask Agent 总结。
        /// </summary>
        private AgentHandoff ResolveHandoff(AgentTaskPlan plan)
        {
            bool hasFileChanges = plan.ChangedFiles.Count > 0;
            bool skipBuild = ShouldSkipAutoBuild();
            bool shouldHandoffToBuild = ShouldHandoffToBuild(
                hasFileChanges,
                _didAttemptBuild,
                _lastBuildSucceeded,
                skipBuild);

            // ── AI 动态移交优先，但 Build 移交仍受最终构建状态约束 ──
            if (PendingHandoffRequest != null)
            {
                var requestedHandoff = ConvertHandoffRequestToHandoff(PendingHandoffRequest);
                if (requestedHandoff.TargetAgent == AgentType.Build && !shouldHandoffToBuild)
                    return BuildSummaryHandoff(plan);

                return requestedHandoff;
            }

            // ── 纯只读/输出任务（QandA 意图、未产生文件变更且无构建警告）：不再移交 Ask ──
            // 直接返回 Edit Agent 的最终回复作为结果（例如用户要求运行命令并输出内容）。
            // 其余任务（包括仅执行 git/终端写操作、无文件变更的 Edit 任务）仍移交 Ask 出总结，
            // AskAgent 已支持基于步骤摘要为空变更场景生成执行结果总结。
            if (ShouldSkipAskSummaryHandoff(plan, HasBuildWarningsInLogs()))
            {
                AddLog("INFO", LocalizationService.Instance["agent.log.editReadOnlyOutputSkippedAsk"]);
                return null;
            }

            if (shouldHandoffToBuild)
                return BuildBuildHandoff();

            if (skipBuild && hasFileChanges)
                AddLog("INFO", LocalizationService.Instance["agent.edit.autoBuildDisabledByUser"]);

            return BuildSummaryHandoff(plan);
        }

        /// <summary>
        /// 判断是否应跳过移交 Ask 生成总结。
        /// 只跳过“纯只读/输出执行任务”（QandA 意图、无文件变更、无构建警告）；
        /// 普通代码修改任务即使因 Git/终端操作导致 ChangedFiles 为空，也必须移交 Ask 出总结。
        /// </summary>
        internal static bool ShouldSkipAskSummaryHandoff(AgentTaskPlan plan, bool hasBuildWarnings)
        {
            return plan != null
                && plan.ChangedFiles.Count == 0
                && !hasBuildWarnings
                && plan.Intent == AgentIntent.QandA;
        }

        /// <summary>
        /// 构建移交 Build Agent 执行编译验证的 Handoff。
        /// </summary>
        private AgentHandoff BuildBuildHandoff()
        {
            var L = LocalizationService.Instance;
            var prompt = new StringBuilder();
            prompt.AppendLine(L["agent.edit.handoffBuildPrompt"]);

            if (!string.IsNullOrWhiteSpace(_lastDirectBuildResult))
            {
                prompt.AppendLine();
                prompt.AppendLine(L["agent.edit.handoffBuildResultHeader"]);
                prompt.AppendLine(TruncateBuildResultForHandoff(_lastDirectBuildResult!));
                prompt.AppendLine();
                prompt.AppendLine(L["agent.edit.handoffBuildNoRebuildRule"]);
            }

            return new AgentHandoff
            {
                Label = L["agent.edit.handoffBuildLabel"],
                TargetAgent = AgentType.Build,
                Prompt = prompt.ToString().TrimEnd(),
                AutoSend = true,
                ShowContinueOn = false,
            };
        }

        /// <summary>
        /// 截断过长的构建输出，仅保留首尾关键片段，避免把海量编译日志
        /// 写入 Handoff/Ask 总结或 UI。完整输出请查看 VS 构建输出 / Error List。
        /// </summary>
        internal static string TruncateBuildResultForHandoff(string buildResult, int maxChars = 8000)
        {
            string trimmed = buildResult?.Trim() ?? string.Empty;
            if (trimmed.Length <= maxChars)
                return trimmed;

            const int headChars = 3000;
            int tailChars = Math.Max(0, maxChars - headChars);
            string head = trimmed.Substring(0, Math.Min(headChars, trimmed.Length));
            string tail = trimmed.Substring(trimmed.Length - Math.Min(tailChars, trimmed.Length));
            return head
                + "\n\n...(构建输出过长，已截断，完整内容请查看 VS 构建输出 / Error List)...\n\n"
                + tail;
        }

        /// <summary>
        /// 构建移交 Ask Agent 生成总结的 Handoff。
        /// 将文件变更统计、步骤执行情况、缓存命中率等上下文打包传递给 Ask Agent。
        /// </summary>
        private AgentHandoff BuildSummaryHandoff(AgentTaskPlan plan)
        {
            var L = LocalizationService.Instance;

            if (plan.IsCancelled)
            {
                return new AgentHandoff
                {
                    Label = L["agent.edit.handoffAskLabel"],
                    TargetAgent = AgentType.Ask,
                    Prompt = L["edit.summary.cancelled"],
                    AutoSend = true,
                    ShowContinueOn = false,
                };
            }

            // 构建包含所有统计数据的 handoff prompt
            var sb = new StringBuilder();
            sb.AppendLine(L["agent.edit.handoffAskPrompt"]);
            sb.AppendLine();
            sb.AppendLine($"**{L["edit.summary.taskLabel"]}**: {plan.Title}");
            sb.AppendLine();

            // ── 步骤执行情况（优先：描述完成了什么）──
            if (plan.Steps.Count > 0)
            {
                sb.AppendLine(L["edit.summary.stepExecutionHeader"]);
                foreach (var step in plan.Steps)
                {
                    string statusIcon = step.Status == AgentStepStatus.Completed ? "✅"
                        : step.Status == AgentStepStatus.Failed ? "❌"
                        : step.Status == AgentStepStatus.Skipped ? "⏭️"
                        : "🔄";
                    string summary = !string.IsNullOrWhiteSpace(step.ResultSummary)
                        ? step.ResultSummary!
                        : LocalizationService.Instance["agent.step.noDetail"];
                    sb.AppendLine(L.Format("edit.summary.stepLineFormat",
                        statusIcon, step.Index, step.Title, summary));
                }
                sb.AppendLine();
            }

            // ── 文件变更统计（辅助参考）──
            if (plan.ChangedFiles.Count > 0)
            {
                var mergedFiles = plan.ChangedFiles
                    .GroupBy(c => NormalizePath(c.FilePath), StringComparer.OrdinalIgnoreCase)
                    .Select(g => new
                    {
                        FileName = Path.GetFileName(g.First().FilePath),
                        LinesAdded = g.Sum(c => c.LinesAdded),
                        LinesRemoved = g.Sum(c => c.LinesRemoved),
                    })
                    .ToList();

                sb.AppendLine(L.Format("edit.summary.changeStats",
                    mergedFiles.Sum(c => c.LinesAdded),
                    mergedFiles.Sum(c => c.LinesRemoved),
                    mergedFiles.Count));
                sb.AppendLine();
                sb.AppendLine(L["edit.summary.modifiedFiles"]);
                foreach (var file in mergedFiles)
                {
                    sb.AppendLine($"- **{file.FileName}** (+{file.LinesAdded} -{file.LinesRemoved})");
                }
                sb.AppendLine();
            }

            // 编译警告（如果有）
            if (HasBuildWarningsInLogs())
            {
                sb.AppendLine(LocalizationService.Instance["agent.edit.handoffBuildWarningHint"]);
            }

            return new AgentHandoff
            {
                Label = L["agent.edit.handoffAskLabel"],
                TargetAgent = AgentType.Ask,
                Prompt = sb.ToString(),
                AutoSend = true,
                ShowContinueOn = false,
            };
        }

        /// <summary>
        /// 构建执行结果摘要（用于 AgentResult.Content，使 Handoff 合并时 UI 可见执行结果）。
        /// 与 BuildSummaryHandoff 不同，此摘要面向用户展示（而非作为 Agent prompt）。
        /// </summary>
        private string BuildExecutionSummary(AgentTaskPlan? plan)
        {
            if (plan == null) return string.Empty;

            var L = LocalizationService.Instance;
            var sb = new StringBuilder();

            // 步骤完成情况
            if (plan.Steps.Count > 0)
            {
                int completed = plan.Steps.Count(s => s.Status == AgentStepStatus.Completed);
                int failed = plan.Steps.Count(s => s.Status == AgentStepStatus.Failed);
                int skipped = plan.Steps.Count(s => s.Status == AgentStepStatus.Skipped);

                sb.AppendLine(L.Format("edit.summary.executionHeader", plan.Title, 
                    $" {completed} / Error: {failed} /  {skipped}"));
            }

            // 文件变更
            if (plan.ChangedFiles.Count > 0)
            {
                var mergedFiles = plan.ChangedFiles
                    .GroupBy(c => NormalizePath(c.FilePath), StringComparer.OrdinalIgnoreCase)
                    .Select(g => new
                    {
                        FileName = Path.GetFileName(g.First().FilePath),
                        LinesAdded = g.Sum(c => c.LinesAdded),
                        LinesRemoved = g.Sum(c => c.LinesRemoved),
                    })
                    .ToList();

                sb.AppendLine(L.Format("edit.summary.fileCountWithValue",
                    L["edit.summary.fileCount"], mergedFiles.Count.ToString()));
                foreach (var file in mergedFiles)
                {
                    sb.AppendLine($"  - `{file.FileName}` (+{file.LinesAdded} -{file.LinesRemoved})");
                }
            }
            else
            {
                sb.AppendLine(L.Format("edit.summary.fileCountWithValue",
                    L["edit.summary.fileCount"], "0"));
            }

            // 构建结果
            if (HasBuildWarningsInLogs())
            {
                sb.AppendLine(LocalizationService.Instance["agent.log.buildWarningInSummary"]);
            }
            else
            {
                sb.AppendLine(LocalizationService.Instance["agent.log.buildPassInSummary"]);
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 构建最终回复内容：纯只读/终端任务（无文件变更）直接沿用 AI 的最终回复，
        /// 避免被“变更总结”形式的 Handoff 覆盖；有文件变更时仍使用执行结果摘要。
        /// </summary>
        private string BuildFinalContent(AgentTaskPlan plan, bool hasNoFileChanges)
        {
            if (!hasNoFileChanges)
                return BuildExecutionSummary(plan);

            var completedStep = plan.Steps.LastOrDefault(s => s.Status == AgentStepStatus.Completed);
            if (completedStep != null && !string.IsNullOrWhiteSpace(completedStep.AiResponse))
                return completedStep.AiResponse.Trim();

            return BuildExecutionSummary(plan);
        }

        #endregion

        #region Project Integration Helpers

        /// <summary>
        /// 将新建文件添加到 Visual Studio 解决方案的项目中。
        /// 如果文件已存在于项目中，则跳过。
        /// </summary>
        private static async Task AddFileToProjectAsync(string filePath, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return;

            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            try
            {
                var dteService = Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider
                    .GetService(typeof(EnvDTE.DTE));
                if (dteService is not EnvDTE.DTE dte || dte.Solution == null || !dte.Solution.IsOpen)
                    return;

                // 遍历所有项目，找到包含该文件路径的最佳匹配项目
                string? fileDir = Path.GetDirectoryName(filePath);
                EnvDTE.Project? bestProject = null;
                string? bestProjectDir = null;

                foreach (EnvDTE.Project project in dte.Solution.Projects)
                {
                    try
                    {
                        string? projectDir = Path.GetDirectoryName(project.FullName);
                        if (projectDir == null) continue;

                        // 检查文件是否已经在项目中
                        foreach (EnvDTE.ProjectItem item in project.ProjectItems)
                        {
                            try
                            {
                                for (short i = 1; i <= item.FileCount; i++)
                                {
                                    if (string.Equals(item.get_FileNames(i), filePath,
                                        StringComparison.OrdinalIgnoreCase))
                                        return; // 文件已在项目中
                                }
                            }
                            catch { }
                        }

                        // 优先匹配目录更深的项目（更具体的项目）
                        if (fileDir != null && fileDir.StartsWith(projectDir, StringComparison.OrdinalIgnoreCase)
                            && (bestProjectDir == null || projectDir.Length > bestProjectDir.Length))
                        {
                            bestProject = project;
                            bestProjectDir = projectDir;
                        }
                    }
                    catch { }
                }

                if (bestProject != null)
                {
                    bestProject.ProjectItems.AddFromFile(filePath);
                    Logger.Info($"[EditAgent]  已将文件加入项目: {Path.GetFileName(filePath)} → {bestProject.Name}");
                }
                else
                {
                    Logger.Warn($"[EditAgent] 未找到合适的项目来添加文件: {Path.GetFileName(filePath)}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditAgent] 添加文件到项目失败: {ex.Message}");
            }
        }

        #endregion

        #region Verify Phase Tracking

        /// <summary>
        /// 追踪验证阶段产生的文件变更，合并到 plan.ChangedFiles。
        /// 验证阶段 AI 可通过工具直接修改/创建文件，这些变更需要反映在最终总结中。
        /// </summary>
        private void TrackVerifyPhaseChanges(List<ChatApiMessage> verifyMessages, AgentTaskPlan plan)
        {
            try
            {
                for (int i = 0; i < verifyMessages.Count; i++)
                {
                    var msg = verifyMessages[i];
                    if (msg.Role != "assistant" || msg.ToolCalls == null || msg.ToolCalls.Count == 0)
                        continue;

                    foreach (var tc in msg.ToolCalls)
                    {
                        string toolName = tc.Function?.Name ?? "";
                        if (!IsFileModifyingTool(toolName))
                            continue;

                        string? filePath = ExtractFilePathFromArgs(tc.Function?.Arguments ?? "");
                        if (string.IsNullOrWhiteSpace(filePath))
                            continue;

                        // 查找对应的 tool result 消息
                        string toolResult = "";
                        for (int j = i + 1; j < verifyMessages.Count; j++)
                        {
                            if (verifyMessages[j].Role == "tool"
                                && verifyMessages[j].ToolCallId == tc.Id)
                            {
                                toolResult = verifyMessages[j].Content ?? "";
                                break;
                            }
                        }

                        // 判断操作是否成功（以 Error:/Timeout: 标记开头表示失败）
                        if (toolResult.StartsWith("Error: ") || toolResult.StartsWith("Timeout: ")) continue;

                        // 估算行数变更（从工具结果中提取 +N -M 模式）
                        int linesAdded = 0;
                        int linesRemoved = 0;
                        var lineMatch = System.Text.RegularExpressions.Regex.Match(
                            toolResult, @"\+(\d+)\s*-(\d+)");
                        if (lineMatch.Success)
                        {
                            int.TryParse(lineMatch.Groups[1].Value, out linesAdded);
                            int.TryParse(lineMatch.Groups[2].Value, out linesRemoved);
                        }
                        else if (toolName == "create_file")
                        {
                            // 读取实际文件内容计算行数
                            if (File.Exists(filePath))
                            {
                                string content = FileEncodingHelper.ReadAllText(filePath);
                                linesAdded = CountLines(content);
                            }
                            else
                            {
                                linesAdded = 1; // 文件不存在时至少标记为有变更
                            }
                        }
                        else if (linesAdded == 0 && linesRemoved == 0)
                        {
                            // replace_string_in_file / multi_replace_string_in_file 等工具
                            // 不返回 +N -M 格式，从参数中提取 oldString/newString 计算行数
                            string? oldStr = ExtractStringArg(tc.Function?.Arguments ?? "", "oldString");
                            string? newStr = ExtractStringArg(tc.Function?.Arguments ?? "", "newString");
                            if (oldStr != null || newStr != null)
                            {
                                linesRemoved = oldStr != null ? CountLines(oldStr) : 0;
                                linesAdded = newStr != null ? CountLines(newStr) : 0;
                            }
                            else if (File.Exists(filePath))
                            {
                                // 无法提取参数时，至少标记文件被修改
                                linesAdded = 1;
                                linesRemoved = 1;
                            }
                            else
                            {
                                linesAdded = 1;
                            }
                        }

                        string fileName = System.IO.Path.GetFileName(filePath);
                        string description = toolName switch
                        {
                            "replace_string_in_file" => string.Format(LocalizationService.Instance["agent.log.toolModifyFile"], fileName),
                            "multi_replace_string_in_file" => string.Format(LocalizationService.Instance["agent.log.toolBatchModifyFile"], fileName),
                            "create_file" => string.Format(LocalizationService.Instance["agent.log.toolCreateFile"], fileName),
                            _ => string.Format(LocalizationService.Instance["agent.log.toolOperateFile"], fileName),
                        };

                        // 合并同一文件的多次变更
                        var existing = plan.ChangedFiles.FirstOrDefault(
                            c => string.Equals(c.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            existing.LinesAdded += linesAdded;
                            existing.LinesRemoved += linesRemoved;
                            if (!string.IsNullOrEmpty(description)
                                && !(existing.BriefDescription ?? "").Contains(description))
                            {
                                existing.BriefDescription = (existing.BriefDescription ?? "") + "; " + description;
                            }
                        }
                        else
                        {
                            plan.ChangedFiles.Add(new FileChangeSummary
                            {
                                FilePath = filePath!,
                                LinesAdded = linesAdded,
                                LinesRemoved = linesRemoved,
                                BriefDescription = description,
                            });
                        }
                    }
                }

                if (plan.ChangedFiles.Count > 0)
                {
                    Logger.Info($"[EditAgent] 验证阶段追踪到文件变更，当前 ChangedFiles 总数: {plan.ChangedFiles.Count}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditAgent] 追踪验证阶段变更失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从工具参数 JSON 中提取 filePath。
        /// </summary>
        private static string? ExtractFilePathFromArgs(string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty("filePath", out var fpProp))
                    return fpProp.GetString();
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 从工具参数 JSON 中按名称提取字符串参数。
        /// </summary>
        private static string? ExtractStringArg(string argumentsJson, string argName)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty(argName, out var prop))
                    return prop.GetString();
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 判断工具名是否为文件修改类工具。
        /// </summary>
        private static bool IsFileModifyingTool(string toolName)
        {
            return toolName is "replace_string_in_file"
                or "multi_replace_string_in_file"
                or "create_file"
                or "apply_patch";
        }

        /// <summary>
        /// 智能检测验证结果中是否真的存在编译/构建失败。
        /// 
        /// 与简单关键词匹配不同，此方法会排除 AI 自然语言中的否定表述
        /// <summary>
        /// 检查执行日志中是否有编译警告或失败信号。
        /// 仅匹配明确的构建失败标记（错误代码、构建摘要行），避免
        /// 因日志中包含 "build"/"Build"/"Error: " 等通用词而产生误判。
        /// 用于判断是否应建议 Handoff 到 Build Agent。
        /// </summary>
        private bool HasBuildWarningsInLogs()
        {
            // 本轮已有明确构建结果时，以最后一次构建为准，避免旧失败记录覆盖后续成功。
            if (_lastBuildSucceeded == true)
                return false;
            if (_lastBuildSucceeded == false)
                return true;

            foreach (var log in _logs)
            {
                // ── 检查 WARN / ERROR 级别日志，以及 INFO 级别中包含构建失败标记的日志 ──
                bool isRelevantLevel = log.Level == "WARN" || log.Level == "ERROR" || log.Level == "INFO";
                if (!isRelevantLevel) continue;

                string msg = log.Message ?? string.Empty;

                // ── 明确的构建/编译失败标记（含 Error: 前缀）──
                if (msg.Contains("Error: 构建失败") || msg.Contains("Error: 编译失败")
                    || msg.Contains("Error: build") || msg.Contains("Error: Build")
                    || msg.Contains("Error: CMake") || msg.Contains("Error: MSBuild"))
                    return true;

                // ── 编译器/MSBuild 错误代码 ──
                if (System.Text.RegularExpressions.Regex.IsMatch(msg,
                    @"\berror\s+(CS|C|LNK|MSB|BC|FS|TS|RUST)\d+\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return true;

                // ── MSBuild 摘要失败模式 ──
                if (msg.Contains("Build FAILED"))
                    return true;

                // ── 本地化构建失败关键词（精确匹配，避免 "build" 误判）──
                if (msg.Contains("构建失败") || msg.Contains("编译失败")
                    || msg.Contains("build failed") || msg.Contains("Build failed"))
                    return true;

                // ── CMake 构建失败 ──
                if (msg.Contains("CMake build failed") || msg.Contains("CMake 构建失败"))
                    return true;

                // ── 非零退出码（构建进程异常退出）──
                if (System.Text.RegularExpressions.Regex.IsMatch(msg,
                    @"exit code:\s*[1-9]\d*",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return true;

                // ── 最终编译验证的警告日志（中/英文 locale）──
                if (msg.Contains("最终编译存在问题") || msg.Contains("最终编译异常")
                    || msg.Contains("最终编译失败")
                    || msg.Contains("Final build has issues")
                    || msg.Contains("Final build exception")
                    || msg.Contains("Final build failed"))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 判断是否应跳过自动编译（整合设置和用户提示两个维度）。
        /// 1. 用户设置：DeepSeekOptionsPage.Instance.EnableAutoBuild 为 false
        /// 2. 用户提示：原始消息中包含"不要编译""跳过构建""don't build"等短语
        /// </summary>
        private bool ShouldSkipAutoBuild()
        {
            // 维度1：检查用户设置
            if (!(Settings.DeepSeekOptionsPage.Instance?.EnableAutoBuild ?? true))
                return true;

            // 维度2：检查用户提示中的意图
            return UserPromptSaysSkipBuild();
        }

        /// <summary>
        /// 检查用户原始消息中是否包含跳过构建的意图。
        /// 支持中/英文关键词匹配。
        /// </summary>
        private bool UserPromptSaysSkipBuild()
        {
            if (string.IsNullOrWhiteSpace(_lastUserMessage))
                return false;

            string msg = _lastUserMessage;

            // ── 中文关键词 ──
            if (msg.Contains("不要编译") || msg.Contains("不要构建")
                || msg.Contains("别编译") || msg.Contains("别构建")
                || msg.Contains("跳过编译") || msg.Contains("跳过构建")
                || msg.Contains("不编译") || msg.Contains("不构建")
                || msg.Contains("无需编译") || msg.Contains("无需构建")
                || msg.Contains("不用编译") || msg.Contains("不用构建")
                || msg.Contains("禁止编译") || msg.Contains("禁止构建")
                || msg.Contains("免编译") || msg.Contains("免构建"))
                return true;

            // ── 英文关键词 ──
            string lower = msg.ToLowerInvariant();
            if (lower.Contains("don't build") || lower.Contains("do not build")
                || lower.Contains("skip build") || lower.Contains("skip the build")
                || lower.Contains("no build") || lower.Contains("without build")
                || lower.Contains("without building") || lower.Contains("don't compile")
                || lower.Contains("do not compile") || lower.Contains("skip compile")
                || lower.Contains("no compile") || lower.Contains("without compile")
                || lower.Contains("without compiling") || lower.Contains("don't run build"))
                return true;

            return false;
        }

        #endregion

        #region Memory — 步骤摘要写入会话记忆

        /// <summary>
        /// 将单个步骤的完成摘要写入会话记忆，供 Ask Agent 最终汇总使用。
        /// </summary>
        private async Task SaveStepSummaryToMemoryAsync(AgentStep step, AgentTaskPlan plan, AgentContext context)
        {
            if (MemoryService == null) return;

            try
            {
                string? sessionId = BuiltInTools?.CurrentSessionId;
                string stepSummary = BuildStepSummaryMarkdown(step, plan);
                string fileName = $"step-{step.Index:D2}-summary.md";

                // 先检查文件是否已存在（防止重复写入）
                try
                {
                    await MemoryService.ViewAsync(MemoryScope.Session, fileName, sessionId, context.SolutionPath);
                    // 文件已存在，先删除再重新创建（内容可能已更新）
                    await MemoryService.DeleteAsync(MemoryScope.Session, fileName, sessionId, context.SolutionPath);
                    await MemoryService.CreateAsync(MemoryScope.Session, fileName,
                        stepSummary, sessionId, context.SolutionPath);
                }
                catch (FileNotFoundException)
                {
                    // 文件不存在，创建新文件
                    await MemoryService.CreateAsync(MemoryScope.Session, fileName,
                        stepSummary, sessionId, context.SolutionPath);
                }

                AddLog("INFO", $"[Memory] {LocalizationService.Instance.Format("agent.log.memoryStepSummaryWritten", step.Index, fileName)}");
            }
            catch (Exception ex)
            {
                AddLog("WARN", $"[Memory] {LocalizationService.Instance.Format("agent.log.memoryStepSummaryFailed", ex.Message)}");
            }
        }

        /// <summary>
        /// 计划全部完成后，将聚合摘要写入会话记忆。
        /// </summary>
        private async Task SaveFinalPlanSummaryToMemoryAsync(AgentTaskPlan plan, AgentContext context)
        {
            if (MemoryService == null) return;

            try
            {
                string? sessionId = BuiltInTools?.CurrentSessionId;
                string finalSummary = BuildFinalPlanSummaryMarkdown(plan);
                string fileName = "plan-final-summary.md";

                await MemoryService.CreateAsync(MemoryScope.Session, fileName,
                    finalSummary, sessionId, context.SolutionPath);

                AddLog("INFO", $"[Memory] {LocalizationService.Instance.Format("agent.log.memoryFinalSummaryWritten", fileName)}");
            }
            catch (Exception ex)
            {
                // 文件可能已存在，删除旧文件后重新创建
                try
                {
                    string? sessionId = BuiltInTools?.CurrentSessionId;
                    await MemoryService.DeleteAsync(MemoryScope.Session, "plan-final-summary.md",
                        sessionId, context.SolutionPath);
                    await MemoryService.CreateAsync(MemoryScope.Session, "plan-final-summary.md",
                        BuildFinalPlanSummaryMarkdown(plan),
                        sessionId, context.SolutionPath);
                }
                catch
                {
                    AddLog("WARN", $"[Memory] {LocalizationService.Instance.Format("agent.log.memoryFinalSummaryFailed", ex.Message)}");
                }
            }
        }

        /// <summary>
        /// v1.1.11: 清理上一次计划遗留的步骤摘要和最终摘要记忆文件。
        /// 在 ExecutePlanAsync 开始时调用，防止新旧计划摘要混在一起。
        /// </summary>
        private async Task ClearPreviousPlanMemoryAsync(AgentContext context)
        {
            if (MemoryService == null) return;

            try
            {
                string? sessionId = BuiltInTools?.CurrentSessionId;

                // 清理最终摘要
                try
                {
                    await MemoryService.DeleteAsync(MemoryScope.Session, "plan-final-summary.md",
                        sessionId, context.SolutionPath);
                }
                catch (FileNotFoundException) { /* 不存在，无需清理 */ }
                catch { /* 静默忽略其他错误 */ }

                // 清理步骤摘要 (step-01 ~ step-99)
                for (int i = 1; i <= 99; i++)
                {
                    string fileName = $"step-{i:D2}-summary.md";
                    try
                    {
                        await MemoryService.DeleteAsync(MemoryScope.Session, fileName,
                            sessionId, context.SolutionPath);
                    }
                    catch (FileNotFoundException)
                    {
                        // 连续两个文件不存在则停止（假设后续也没有）
                        if (i > 1)
                        {
                            string prevFile = $"step-{(i - 1):D2}-summary.md";
                            try
                            {
                                await MemoryService.ViewAsync(MemoryScope.Session, prevFile,
                                    sessionId, context.SolutionPath);
                            }
                            catch (FileNotFoundException)
                            {
                                break; // 前一个也不存在，确认没有更多旧文件
                            }
                        }
                    }
                    catch { /* 静默忽略其他错误 */ }
                }

                AddLog("INFO", $"[Memory] {LocalizationService.Instance["agent.log.memoryStepSummariesCleared"]}");
            }
            catch (Exception ex)
            {
                AddLog("WARN", $"[Memory] {LocalizationService.Instance.Format("agent.log.memoryCleanupFailed", ex.Message)}");
            }
        }

        /// <summary>
        /// 构建单个步骤的 Markdown 摘要。
        /// </summary>
        private static string BuildStepSummaryMarkdown(AgentStep step, AgentTaskPlan plan)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# 步骤 {step.Index}/{plan.Steps.Count}: {step.Title}");
            sb.AppendLine();
            sb.AppendLine($"- **状态**: {(step.Status == AgentStepStatus.Completed ? LocalizationService.Instance["agent.step.completed"] : LocalizationService.Instance["agent.step.failed"])}");
            sb.AppendLine($"- **任务**: {plan.Title}");
            if (!string.IsNullOrWhiteSpace(step.ResultSummary))
            {
                sb.AppendLine($"- **结果**: {step.ResultSummary}");
            }
            if (!string.IsNullOrWhiteSpace(step.Description))
            {
                sb.AppendLine($"- **描述**: {step.Description}");
            }
            // 展示当前所有已变更文件
            var allFiles = plan.ChangedFiles
                .GroupBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { Name = System.IO.Path.GetFileName(g.Key), Added = g.Sum(c => c.LinesAdded), Removed = g.Sum(c => c.LinesRemoved) })
                .ToList();
            if (allFiles.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## 已修改的文件");
                foreach (var file in allFiles)
                {
                    string delta = $"{(file.Added > 0 ? $"+{file.Added}" : "")}"
                        + $"{(file.Removed > 0 ? $" -{file.Removed}" : "")}";
                    sb.AppendLine($"- `{file.Name}` {delta}");
                }
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 构建最终计划聚合摘要。
        /// </summary>
        private static string BuildFinalPlanSummaryMarkdown(AgentTaskPlan plan)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# 计划完成: {plan.Title}");
            sb.AppendLine();
            int completed = plan.Steps.Count(s => s.Status == AgentStepStatus.Completed);
            int failed = plan.Steps.Count(s => s.Status == AgentStepStatus.Failed);
            int skipped = plan.Steps.Count(s => s.Status == AgentStepStatus.Skipped);
            sb.AppendLine($"- **总步骤**: {plan.Steps.Count}");
            sb.AppendLine($"- **完成**: {completed} | **失败**: {failed} | **跳过**: {skipped}");
            sb.AppendLine();

            // 汇总所有步骤
            sb.AppendLine("## 步骤摘要");
            sb.AppendLine();
            foreach (var step in plan.Steps)
            {
                string icon = step.Status switch
                {
                    AgentStepStatus.Completed => "",
                    AgentStepStatus.Failed => "Error: ",
                    AgentStepStatus.Skipped => "",
                    _ => "",
                };
                string summary = !string.IsNullOrWhiteSpace(step.ResultSummary)
                    ? step.ResultSummary!
                    : "(无详细结果)";
                sb.AppendLine($"- {icon} **{step.Title}**: {summary}");
            }

            // 汇总所有文件变更
            var mergedFiles = plan.ChangedFiles
                .GroupBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    Path = System.IO.Path.GetFileName(g.Key),
                    Added = g.Sum(c => c.LinesAdded),
                    Removed = g.Sum(c => c.LinesRemoved),
                })
                .ToList();

            if (mergedFiles.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## 文件变更汇总");
                sb.AppendLine();
                sb.AppendLine("| 文件 | 变更 |");
                sb.AppendLine("|------|------|");
                foreach (var f in mergedFiles)
                {
                    string delta = $"{(f.Added > 0 ? $"+{f.Added}" : "")}"
                        + $"{(f.Removed > 0 ? $" -{f.Removed}" : "")}";
                    sb.AppendLine($"| `{f.Path}` | {delta} |");
                }
            }

            return sb.ToString().TrimEnd();
        }

        #endregion

        #region IDisposable

        public override void Dispose()
        {
            _agentCts?.Cancel();
            _agentCts?.Dispose();
            base.Dispose();
        }

        #endregion
    }
}
