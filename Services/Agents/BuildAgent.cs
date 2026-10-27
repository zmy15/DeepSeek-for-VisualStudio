using DeepSeek_v4_for_VisualStudio.Models;
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
    /// Build Agent — 构建修复代理。
    /// 
    /// 职责：
    /// - 编译验证代码变更
    /// - 诊断编译错误并直接修复 bug（拥有完整编辑权限）
    /// - 循环修复直到编译通过
    /// - 可独立调用（用户遇到编译错误时显式 @build）
    /// - 作为 Edit Agent 完成后的 Handoff 目标
    /// - 可与 Edit Agent 双向移交（复杂重构场景移交 Edit，编译修复场景接受 Edit 移交）
    /// 
    /// 设计原则：
    /// - build_solution → get_errors → read_file → replace_string_in_file → build_solution 循环
    /// - 拥有完整编辑工具集，可直接修复任何编译错误和代码 bug
    /// - 最多尝试修复 3 次，但新错误不计入限制
    /// - 修复过程中可使用 file_search/grep_search/list_dir 探索代码结构
    /// </summary>
    public class BuildAgent : BaseAgent
    {
        /// <summary>
        /// Build Agent 工具集 — 构建、诊断、编辑全覆盖。
        /// 代码库探索（搜索、列表、grep）通过 runSubagent 委派给 ExploreAgent。
        /// read_file 保留用于修复前确认文件内容（利用 ExploreAgent 预热缓存）。
        /// </summary>
        public static readonly string[] BuildTools = new[]
        {
            // 构建与诊断
            "build_solution",
            "run_tests",
            "get_errors",
            "file_search",      // 直接定位文件，构建错误路径/名称不完整时使用
            "grep_search",      // 直接定位符号/错误文本引用
            "get_file_symbols", // 单文件符号定义列表
            "list_dir",         // 快速查看项目目录结构
            // 文件读写与编辑
            "read_file",
            "web_search",       // 联网搜索（需在设置中开启）
            "fetch_webpage",    // 抓取指定网页内容
            "capture_window",    // 捕获窗口截图（视觉模型直接查看）
            "capture_webpage",   // 离屏渲染 URL 截图（视觉模型直接查看）
            "replace_string_in_file",
            "multi_replace_string_in_file",
            "create_file",
            "delete_file",
            "apply_patch",
            "create_directory",
            // 子代理委派与移交
            "runSubagent",
            "request_handoff",
            // 终端
            "run_in_terminal",
            "get_terminal_output",
            // 用户交互
            "VisualStudio_askQuestions",  // 向用户提问澄清（如构建配置确认）
            // Git 版本控制
            "git",
            // 记忆
            "memory",
        };

        public BuildAgent(DeepSeekApiService apiService) : base(apiService, AgentType.Build) { }

        #region Agent Definition

        protected override AgentDefinition CreateDefinition(AgentType agentType)
        {
            return new AgentDefinition
            {
                Type = AgentType.Build,
                Name = "Build",
                AllowedTools = new List<string>(BuildTools),
                SystemPrompt = BuildSystemPrompt(),
            };
        }

        private static string BuildSystemPrompt()
        {
            return LocalizationService.Instance["system.agent.buildPrompt"]
                + LocalizationService.Instance["system.agent.buildTrustRule"]
                + AiPrompts.BuildAgentMcpFragment
                + AiPrompts.AgentConclusionStopRule
                + "\n\n" + LocalizationService.Instance["system.agent.buildNoRebuildRule"];
        }

        #endregion

        #region Execute

        /// <summary>
        /// Build Agent 执行入口。
        /// 接受编译错误上下文，循环诊断并修复。
        /// </summary>
        public override async Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context)
        {
            // ── 清空上次执行的日志，防止旧日志干扰判断 ──
            _logs.Clear();

            // ── 清理上次执行的移交状态，防止跨调用污染 ──
            PendingHandoffRequest = null;

            var L = LocalizationService.Instance;
            // 时间线只显示稳定短句：Handoff prompt 尾部带「优先复用对话历史…」等给模型的脚手架，
            // 整段经过 AddLog 会渲染进 UI，把内部提示词显示给用户。
            // 原文压缩后只写日志文件。
            Logger.Info(string.Format(L["agent.log.buildStarted"], SummarizeForLog(userMessage)));
            AddLog("INFO", L["agent.log.buildStartedShort"]);

            var result = new AgentResult
            {
                Success = true,
            };

            var ct = context.CancellationToken;
            string workspaceRoot = GetWorkspaceRoot(context);

            try
            {
                // ── 构建上下文增强消息 ──
                string enhancedMessage = BuildEnhancedUserMessage(userMessage, context);

                // ── 构建消息列表（含对话历史 + Agent 专属 prompt）──
                var messages = BuildContextAwareMessages(
                    Definition.SystemPrompt,
                    enhancedMessage,
                    maxRecentTurns: int.MaxValue);

                // ── 使用工具调用循环 ──
                var thinkingBuilder = new StringBuilder();
                string aiResponse = await CallAiWithToolLoopAsync(
                    messages,
                    workspaceRoot,
                    ct,
                    toolWhitelist: new List<string>(BuildTools),
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

                // ── 保存推理内容，供 UI 渲染思考面板 ──
                if (thinkingBuilder.Length > 0)
                    result.ReasoningContent = thinkingBuilder.ToString();

                result.Content = aiResponse;

                // ── 判断最终是否还有编译错误 ──
                if (HasBuildFailure(aiResponse))
                {
                    AddLog("WARN", L["agent.log.buildStillHasErrors"]);
                    PlanBuildOutcomeReconciler.MarkBuildFailed(context.ActivePlan);
                    result.Content += "\n\n " + L["agent.log.buildStillHasErrors"];
                }
                else
                {
                    AddLog("INFO", L["agent.log.buildPassed"]);
                    if (context.ActivePlan != null)
                    {
                        int reconciled = PlanBuildOutcomeReconciler.ReconcileAfterBuildSuccess(
                            context.ActivePlan,
                            L["agent.log.buildReconciledStepResult"]);
                        if (reconciled > 0)
                        {
                            AddLog("INFO", string.Format(L["agent.log.buildReconciledSteps"], reconciled));
                            await ClearStalePlanSummaryMemoryAsync(context);
                        }
                    }
                }

                // ── AI 通过 request_handoff 工具主动请求移交（优先于程序化移交）──
                if (PendingHandoffRequest != null)
                {
                    result.Handoff = ConvertHandoffRequestToHandoff(PendingHandoffRequest);
                }
                // ── 携带计划上下文，链回 Ask Agent 生成最终变更总结 ──
                else if (context.ActivePlan != null && context.ActivePlan.IsCompleted)
                {
                    result.Plan = context.ActivePlan;
                    result.FileChanges = context.ActivePlan.ChangedFiles;

                    // 计划统计与构建结果由源 Agent 的 assistant 消息承载；
                    // 总结 user 只保留纯指令，避免把结构化执行数据重复塞进用户消息。
                    result.Handoff = CreateAskSummaryHandoff(
                        L["agent.summary.handoffUserPrompt"]);
                }
                else
                {
                    // ── 无计划上下文时也移交 Ask 生成总结 ──
                    result.Handoff = CreateAskSummaryHandoff(
                        L["agent.summary.handoffUserPrompt"]);
                }

                result.Logs.AddRange(_logs);
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = L["agent.log.buildCancelled"];
                AddLog("WARN", L["agent.log.buildCancelled"]);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                AddLog("ERROR", string.Format(L["agent.log.buildFailed"], ex.Message));
            }

            return result;
        }

        #endregion

        #region Helpers

        internal static AgentHandoff CreateAskSummaryHandoff(string prompt)
        {
            return new AgentHandoff
            {
                Label = LocalizationService.Instance["agent.edit.handoffAskLabel"],
                TargetAgent = AgentType.Ask,
                Prompt = prompt,
                AutoSend = true,
                ShowContinueOn = false,
                IsSummaryOnly = true,
            };
        }

        /// <summary>
        /// 构建增强用户消息：附加当前编译错误上下文和变更文件信息。
        /// </summary>
        private string BuildEnhancedUserMessage(string userMessage, AgentContext context)
        {
            var sb = new StringBuilder();

            // ── 缓存优化：稳定性高的内容放前面 ──

            // 第1层：附加文件内容（用户提供，会话内稳定）
            if (!string.IsNullOrEmpty(context.FileContext))
            {
                sb.AppendLine(context.FileContext);
                sb.AppendLine();
            }

            // 第2层：已修改文件列表（同步骤内不变）
            if (context.ActivePlan != null && context.ActivePlan.ChangedFiles.Count > 0)
            {
                sb.AppendLine("## 已修改的文件");
                foreach (var change in context.ActivePlan.ChangedFiles)
                {
                    sb.AppendLine($"- `{change.FilePath}` (+{change.LinesAdded} -{change.LinesRemoved})");
                }
                sb.AppendLine();
            }

            // 第3层：前置步骤上下文（每步追加，前缀稳定）
            if (!string.IsNullOrEmpty(context.AccumulatedContext))
            {
                sb.AppendLine("## 前置步骤上下文");
                sb.AppendLine(context.AccumulatedContext);
                sb.AppendLine();
            }

            // 第4层：用户消息（每问必变，放最后）
            // ── 打「当前任务目标」标记 ──
            //   BuildAgent 走 BuildContextAwareMessages(..., deduplicateCurrentUser: false)，
            //   该消息是**新追加**的 user，不经过 Handoff 分支的
            //   ApplyCurrentUserQuestionPrefix，也不入 _entries。缺该标记时，
            //   切换 Agent 后的长上下文里模型容易把中间内容误当成本轮目标。
            sb.Append(LocalizationService.Instance["system.agent.handoffTaskGoalPrefix"]);
            sb.AppendLine(userMessage);

            return sb.ToString();
        }

        /// <summary>
        /// 从 AgentContext 解析工作区根目录。
        /// </summary>
        private static new string GetWorkspaceRoot(AgentContext context)
        {
            string? sln = context.SolutionPath;
            if (!string.IsNullOrEmpty(sln))
            {
                if (Directory.Exists(sln))
                    return sln;
                string? dir = Path.GetDirectoryName(sln);
                if (!string.IsNullOrEmpty(dir))
                    return dir;
            }
            return string.Empty;
        }

        /// <summary>
        /// 最终构建通过后清理旧的计划摘要记忆，
        /// 让 Ask Agent 基于已被回写成功状态的计划生成总结，而不是旧 Error: 状态。
        /// </summary>
        private async Task ClearStalePlanSummaryMemoryAsync(AgentContext context)
        {
            if (MemoryService == null || BuiltInTools == null) return;

            try
            {
                string? sessionId = BuiltInTools.CurrentSessionId;
                var fileNames = new List<string> { "plan-final-summary.md" };
                for (int i = 1; i <= 50; i++)
                    fileNames.Add($"step-{i:D2}-summary.md");

                foreach (var fileName in fileNames)
                {
                    try
                    {
                        await MemoryService.DeleteAsync(
                            MemoryScope.Session, fileName, sessionId, context.SolutionPath);
                    }
                    catch (FileNotFoundException) { /* 不存在，无需清理 */ }
                    catch { /* 单文件清理失败不影响整体 */ }
                }

                AddLog("INFO", $"[Memory] {LocalizationService.Instance["agent.log.memoryPlanSummaryCleared"]}");
            }
            catch (Exception ex)
            {
                AddLog("WARN", $"[Memory] {LocalizationService.Instance.Format("agent.log.memoryPlanSummaryClearFailed", ex.Message)}");
            }
        }
        #endregion
    }
}
