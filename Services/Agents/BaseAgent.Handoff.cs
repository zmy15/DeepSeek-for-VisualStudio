using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using DeepSeek_v4_for_VisualStudio.Utils;
using DeepSeek_v4_for_VisualStudio.View;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// <see cref="BaseAgent"/> 的 移交执行 分部实现。
    /// 承载该职责域的成员，使主文件聚焦于 Agent 执行编排。
    /// </summary>
    public abstract partial class BaseAgent
    {
        /// <summary>
        /// 将 HandoffRequest（AI 通过 request_handoff 工具发起的 JSON 移交）
        /// 转换为 AgentHandoff（供 UI 层使用的移交格式）。
        /// </summary>
        protected AgentHandoff ConvertHandoffRequestToHandoff(HandoffRequest request)
        {
            var L = LocalizationService.Instance;
            string label = request.TargetAgent switch
            {
                AgentType.Ask => L["agent.edit.handoffAskLabel"],
                AgentType.Edit => L["agent.ask.handoffEditLabel"],
                AgentType.Plan => L["agent.ask.handoffPlanLabel"],
                AgentType.Build => L["agent.ask.handoffBuildLabel"],
                AgentType.Explore => L["agent.ask.handoffExploreLabel"],
                _ => L["agent.handoff.defaultLabel"]
            };

            string prompt = LocalizationService.Instance.Format(
                "agent.handoffFrom", request.SourceAgent, request.Reason, request.TaskDescription);

            return new AgentHandoff
            {
                Label = label,
                TargetAgent = request.TargetAgent,
                SourceAgent = request.SourceAgent,
                Prompt = prompt,
                AutoSend = request.AutoSend,
                ShowContinueOn = !request.AutoSend,
                ChainBack = request.ChainBack,
                EditSteps = request.EditSteps,
                GitState = request.GitState,
            };
        }

        /// <summary>
        /// 根据移交携带的步骤构造轻量执行计划（不生成 plan.md，但作为 Edit 计划显示并持久化面板）。
        /// </summary>
        /// <param name="handoff">移交定义（EditSteps 非空，调用方已保证）</param>
        internal static AgentTaskPlan BuildLightweightPlanFromHandoff(AgentHandoff handoff)
        {
            var steps = new List<AgentStep>();
            foreach (var s in handoff.EditSteps!.Take(4))     // 双重保护：工具层已截断，这里兜底
            {
                steps.Add(new AgentStep
                {
                    Index = steps.Count + 1,                                                       // 顺序重排
                    Title = s.Title,
                    Description = string.IsNullOrWhiteSpace(s.Description) ? s.Title : s.Description,  // 空描述回退标题
                    Status = AgentStepStatus.Pending,
                    RequiresApproval = false,
                });
            }

            return new AgentTaskPlan
            {
                Intent = AgentIntent.CodeChange,
                Title = LocalizationService.Instance["plan.lightweightTitle"],
                Steps = steps,
                TaskDescription = handoff.Prompt, // 保留完整任务内容，Edit 步骤提示中再注入
                // Edit 执行计划同样需要面板展示步骤进度；该来源不会显示“执行当前计划”按钮。
                Source = PlanSource.EditAgent,
                IsCompleted = false,
                IsCancelled = false,
                PlanFilePath = null,           // 无 plan.md；ExecuteHandoffAsync 的 File.Exists 判定会自动跳过
                // 严禁设置 IsFromPlanAgent = true —— setter 副作用会把 Source 提升为 PlanAgent
            };
        }

        /// <summary>
        /// 执行 Handoff：从当前 Agent 移交到目标 Agent。
        /// 构建 Handoff prompt（含计划上下文 + plan.md），调用目标 Agent 的 ExecuteAsync。
        /// 
        /// </summary>
        /// <param name="handoff">移交定义</param>
        /// <param name="context">执行上下文</param>
        /// <param name="activePlan">当前活跃计划（可选，注入到 prompt 中）</param>
        /// <param name="agentFactory">AgentFactory 引用，用于获取目标 Agent 实例</param>
        public async Task<AgentResult> ExecuteHandoffAsync(
            AgentHandoff handoff,
            AgentContext context,
            AgentTaskPlan? activePlan = null,
            AgentFactory? agentFactory = null)
        {
            Logger.Info($"[{Definition.Name}] Handoff: → {handoff.TargetAgent} ({handoff.Label})");
            var handoffStopwatch = System.Diagnostics.Stopwatch.StartNew();

            // ── 计算有效计划：原计划非空且未完成时优先；否则按移交携带的 EditSteps 构造轻量计划 ──
            AgentTaskPlan? effectivePlan = activePlan;
            if (handoff.TargetAgent == AgentType.Edit
                && handoff.EditSteps != null && handoff.EditSteps.Count > 0
                && (effectivePlan == null || effectivePlan.IsCompleted))
            {
                effectivePlan = BuildLightweightPlanFromHandoff(handoff);
            }

            BaseAgent targetAgent;
            if (agentFactory != null)
                targetAgent = agentFactory.GetAgent(handoff.TargetAgent);
            else
                throw new InvalidOperationException("AgentFactory 引用为空，无法获取目标 Agent");

            targetAgent.Context = context;
            context.IsSummaryOnlyHandoff = handoff.IsSummaryOnly;

            // Handoff 携带的快照是调用方刚刚基于 completedResult 生成的最终版本，
            // 必须优先于 Context 中可能残留的源 Agent 旧快照（例如 build_solution 成功后保存的列表）；
            // 否则目标 Agent 会丢掉刚追加的源 Agent 最终 assistant 回答。
            if (handoff.ForwardedMessages != null)
                context.ForwardedMessages = CloneApiMessages(handoff.ForwardedMessages);

            // ── 真移交：允许目标 Agent 首轮复用移交前缀（含身份边界提示语义）──
            context.AllowForwardedMessageReuse = context.ForwardedMessages is { Count: > 0 };

            // ──  缓存边界快照（v1.1.10）：在 Handoff 前保存 ContextManager 状态 ──
            //     目标 Agent 通过 BuildApiMessages 读取历史时，仅包含边界前的条目，
            //     排除 Handoff 过渡消息（步骤完成通知、最终构建结果等），
            //     使 DeepSeek Prefix Cache 在跨 Agent 切换时仍能命中。
            //     例外：Edit 目标不会用到它 —— ExecutePlanAsync 在第一步之前就 ClearCacheSnapshot，
            //     而目标首轮走 ForwardedMessages 分支、根本不查边界。快照只会「存了立刻丢」。
            if (handoff.TargetAgent != AgentType.Edit)
                context.ContextManager?.SnapshotForCache();

            // ── 构建 Handoff prompt ──
            // 精简原则（v1.1.14）：handoff 只承载「目标 Agent 是谁」+「移交方要求做什么」
            // +「复用历史不要再读一遍」。handoff.Prompt 已包含 "[移交自 X] 原因 + 完整任务描述"。
            // 以下内容不再注入，因为移交前缀（ForwardedMessages / 对话历史）里已有原始信息，
            // 目标 Agent 可从历史中的 read_file / runSubagent 结果直接获取，重复注入属于冗余：
            //   · 结构化步骤列表（Edit 每步会通过步骤提示给出当前步骤 + plan.md 对应章节）
            //   · plan.md 概述（计划执行器按需读取）
            //   · 前一 Agent 的探索结果、Git 状态
            var sb = new StringBuilder();
            sb.AppendLine(handoff.Prompt);
            sb.AppendLine();
            // 复用历史约束：与 Edit 步骤提示共用同一文案，避免接手 Agent 重复读取
            // 历史中已存在的文件内容（代码记忆移除后，跨 Agent 的文件内容只能靠历史承接）。
            sb.AppendLine(LocalizationService.Instance["agent.step.reuseHistoryHint"]);

            string handoffMessage = sb.ToString();
            // ── 保留 context 中已有的 ActivePlan，仅在非 null 时覆盖 ──
            context.ActivePlan = effectivePlan ?? context.ActivePlan;
            // 注：不再刷新 context.ConversationHistory。该字段在生产代码中没有任何读取方
            //     （UI/Agent 都直接读 ContextManager），每次 Handoff 重建它只是一次 O(entries)
            //     的无效分配，恰好落在交接关键路径上。
            // ── Planning 模式仅对 Edit/Build 等执行类 Agent 生效 ──
            if (handoff.TargetAgent == AgentType.Edit || handoff.TargetAgent == AgentType.Build)
                context.IsPlanningMode = true;

            // ── 耗时探针：prepare（交接准备） / firstToken（目标首个流式分片） / total ──
            //     prepare 覆盖计划选择、快照克隆、缓存边界、提示词组装；
            //     firstToken−prepare 即目标 Agent 的上下文组装 + 网络 + 模型首 token。
            long prepareMs = handoffStopwatch.ElapsedMilliseconds;
            long firstTokenMs = -1;
            var originalThinking = context.OnThinkingChunk;
            var originalContent = context.OnContentChunk;
            if (originalThinking != null || originalContent != null)
            {
                void MarkFirstToken()
                {
                    if (firstTokenMs < 0)
                        firstTokenMs = handoffStopwatch.ElapsedMilliseconds;
                }

                context.OnThinkingChunk = chunk => { MarkFirstToken(); originalThinking?.Invoke(chunk); };
                context.OnContentChunk = chunk => { MarkFirstToken(); originalContent?.Invoke(chunk); };
            }

            try
            {
                return await targetAgent.ExecuteAsync(handoffMessage, context);
            }
            finally
            {
                context.OnThinkingChunk = originalThinking;
                context.OnContentChunk = originalContent;
                Logger.Info($"[HandoffTiming] {Definition.Name}→{handoff.TargetAgent}: " +
                    $"prepare={prepareMs}ms, " +
                    $"firstToken={(firstTokenMs < 0 ? "n/a" : firstTokenMs + "ms")}, " +
                    $"total={handoffStopwatch.ElapsedMilliseconds}ms");
            }
        }
    }
}
