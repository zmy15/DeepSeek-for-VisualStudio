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
    /// <see cref="BaseAgent"/> 的 上下文消息构建 分部实现。
    /// 承载该职责域的成员，使主文件聚焦于 Agent 执行编排。
    /// </summary>
    public abstract partial class BaseAgent
    {
        /// <summary>
        /// 构建上下文感知的消息列表，将 Agent 的 system prompt 与对话历史合并。
        /// 
        /// ── 消息结构（v1.1.12）──
        ///   messages[0] = 稳定 system（SharedImmutablePrefix + fixedPrompt）
        ///   messages[1] = 可选用户记忆（会话级固定前缀）
        ///   messages[2] = 可选 dynamicBlock（压缩摘要）
        ///   messages[3..N] = 对话历史（来自 ContextManager，起始位置固定）
        ///   messages[N+1] = 当前用户消息
        ///   [插入点] = 当前用户消息后（工具循环插入 assistant/tool）
        ///   最后一条 = Agent 专属行为指令
        /// 
        /// 设计决策：Agent 提示词固定在列表最后；assistant/tool 历史插入到当前
        /// 用户消息之后、Agent 提示词之前，保持标准多轮对话语义。
        /// Handoff 时同样复用旧前缀，追加新用户消息和新 Agent 提示词。
        /// </summary>
        /// <param name="systemPrompt">Agent 专属行为指令（不含共享前缀）</param>
        /// <param name="userPrompt">当前的用户消息/步骤描述</param>
        /// <param name="maxRecentTurns">注入对话历史的最近轮次数（0 = 不注入，默认不限制）</param>
        /// <returns>按缓存优化顺序排列的消息列表</returns>
        protected List<ChatApiMessage> BuildContextAwareMessages(
            string systemPrompt,
            string userPrompt,
            int maxRecentTurns = int.MaxValue,
            bool deduplicateCurrentUser = false)
            => BuildContextAwareMessages(systemPrompt, userPrompt, maxRecentTurns, deduplicateCurrentUser, false);

        protected List<ChatApiMessage> BuildContextAwareMessages(
            string systemPrompt,
            string userPrompt,
            int maxRecentTurns,
            bool deduplicateCurrentUser,
            bool persistVolatileToHistory)
        {
            // ──  Handoff 消息复用（v1.1.10）：若源 Agent 传递了工具循环消息列表，
            //    直接复用作为前缀，不再从 ContextManager 重建。
            //    这确保 Handoff 前后消息结构完全一致，DeepSeek Prefix Cache 可直接命中。
            List<ChatApiMessage>? forwarded = Context?.ForwardedMessages;
            // ── Handoff 消息复用（v1.1.10）：若源 Agent 传递了工具循环消息列表，
            //    直接复用作为前缀，不再从 ContextManager 重建。
            //    这确保 Handoff 前后消息结构完全一致，DeepSeek Prefix Cache 可直接命中。
            // ── 仅真移交允许复用：AllowForwardedMessageReuse 由 ExecuteHandoffAsync /
            //    子 Agent 派发路径显式置位。同一 Agent 内部推进（Edit 计划进入下一步骤）
            //    保持 false —— 此时 ForwardedMessages 只是缓存快照残留（如 build_solution
            //    成功提前终止循环时保存），若误走本分支会插入身份边界提示与移交上下文块，
            //    并把当前 user 变成"第二次任务下发"，污染同 Agent 的步骤语义。
            if (forwarded != null && forwarded.Count > 0 && Context!.AllowForwardedMessageReuse)
            {
                Context.AllowForwardedMessageReuse = false; // 一次性消费，防下次误用
                Context!.ConsumedForwardedMessages = forwarded;
                Context!.ForwardedMessages = null; // 消费后清空，防止下次误用
                var result = new List<ChatApiMessage>(forwarded);

                // ── 新结构：旧前缀 + [当前用户] + [当前 Agent 提示词] ──
                // 仅移除源 Agent 末尾提示词，旧用户消息和历史保留，目标 Agent 提示词追加到最后。
                int count = result.Count;
                if (count >= 1 && result[count - 1].Role == "system")
                {
                    result.RemoveAt(count - 1);
                }
                // 注：不再对尾部 [system, user] 做"旧结构一并移除"——新结构快照中 user 前紧邻的
                // system 是 volatile 上下文块，与旧结构 agent 提示词无法区分；跨版本旧快照中的
                // 遗留身份指令由 HandoffRoleBoundaryPrompt 兜底。

                // Handoff 前缀保留到源 Agent 的稳定工具历史为止；目标 Agent 的
                // 身份边界以后的内容由目标 Agent 重新生成。
                if (Context != null)
                    Context.ToolHistoryInsertIndex = result.Count;

                result.Add(new ChatApiMessage { Role = "system", Content = AiPrompts.HandoffRoleBoundaryPrompt });

                // ── 身份边界提示固化进 _entries（持久化）──
                //   上述 result 是「发射即焚」的局部列表：只参与本次 API 请求，不回写
                //   ContextManager，故 GetFullContext()/ApiHistory 都导不出它。重启或切换会话后，
                //   _entries 里仍保留着带来源 Agent 身份的历史（如「你是一个 Edit Agent」的任务
                //   提示与工具调用记录），却没有对应的边界提示来中和，模型可能沿用旧身份。
                //   这里同步写一份到 _entries，使边界提示随会话持久化，重启后仍然生效。
                Context?.ContextManager?.AddCustomMessage("system", AiPrompts.HandoffRoleBoundaryPrompt);

                // ── 不再在 Handoff 分支注入易变上下文块（IDE Context / 工作区快照）──
                //   易变块只应在「用户发起一次提问」时注入一次：主对话路径由
                //   AskAgent.ExecuteAsync → BuildContextAwareMessages(persistVolatileToHistory:true)
                //   → PersistCurrentVolatileSnapshot() 完成该轮固化，Handoff 只是同一轮内的
                //   Agent 交接，再次注入会产生同一轮内重复的 IDE 快照（曾出现同一请求内
                //   两份逐字符相同的 361 字符块），既浪费 token 又稀释身份边界提示。
                //   目标 Agent 需要实时 IDE 态时可经历史中已固化的快照获取。

                result.Add(CreateCurrentUserMessage(userPrompt));

                // 目标 Agent 的 assistant/tool 历史插入到当前 user 之后、
                // Agent 专属提示词之前，保持标准多轮对话语义。
                if (Context != null)
                    Context.ToolHistoryInsertIndex = result.Count;
                if (!string.IsNullOrWhiteSpace(systemPrompt))
                    result.Add(new ChatApiMessage { Role = "system", Content = systemPrompt });
                AppendExplicitRouteInstruction(result);

                // ── 标记移交后的当前任务目标 ──
                //   Handoff 分支在下方的前缀逻辑之前就 return 了，目标 Agent 收到的
                //   user（「你是一个 Edit Agent，正在执行任务…」）没有任何「这是当前目标」
                //   的显式标记。而这恰恰是最需要该标记的场景：上下文最长、身份刚切换，
                //   历史里还堆着源 Agent 的 user 轮次与工具记录，模型容易把中间内容或
                //   上一轮需求误当成本轮目标而跑偏。
                //   源轮次那条 user 的 [本轮用户需求] 前缀已由 AddUserMessage 落进 _entries，
                //   随移交快照自然带出，无需在此处补写（此前补写会逐次叠加，且命中
                //   "倒数第二条 user" 的脆弱定位）。
                ApplyCurrentUserQuestionPrefix(result, "system.agent.handoffTaskGoalPrefix");
                return result;
            }

            var messages = new List<ChatApiMessage>();
            var ctxManager = Context?.ContextManager;
            bool volatilePersisted = false;

            // ── 易变上下文块固化（IDE Context / 工作区快照）──
            //   调用方显式要求（persistVolatileToHistory=true，AskAgent 主流程）时必须固化；
            //   即便未显式要求，也尝试固化一次——@agent 显式路由等场景不经 AskAgent，
            //   若此处不固化，易变块只会临时进入本次请求而不落 _entries，重启后即丢失。
            //   PersistCurrentVolatileSnapshot 自带同轮幂等保护（重复调用返回 false），
            //   故对所有调用方默认尝试是安全的：同一轮内始终只有一份快照。
            //   注意必须在选取历史之前完成，快照需插在本轮 user 之前。
            if (ctxManager != null && maxRecentTurns > 0)
            {
                volatilePersisted = ctxManager.PersistCurrentVolatileSnapshot();

                if (persistVolatileToHistory && !volatilePersisted)
                {
                    // 调用方要求固化但未成功（如无易变内容可固化），保持原语义记录日志便于排查
                    Logger.Debug("[Agent] 易变上下文固化未生效（无可固化内容或同轮已固化）");
                }
            }

            if (ctxManager != null && !ctxManager.IsEmpty && maxRecentTurns > 0)
            {
                var recentMessages = ctxManager.BuildApiMessagesRecentTurns(maxRecentTurns);
                if (recentMessages.Count > 0)
                {
                    // [0]=稳定 system（共享前缀+固定提示词），[1]=可选动态块，[2..]=历史
                    messages.AddRange(recentMessages);
                }
            }
            else
            {
                if (ctxManager != null)
                    messages.AddRange(ctxManager.BuildContextPrefix());

                if (messages.Count == 0)
                    messages.Add(new ChatApiMessage { Role = "system", Content = GetSharedImmutablePrefix() });
            }

            // UI 层在进入 Agent 前已把当前轮原始 user 追加到 ContextManager。
            // Ask 主流程使用该 user 作为标准多轮对话的当前轮次；
            // 只有非会话历史 fallback 时才追加传入的 contextualPrompt。
            if (deduplicateCurrentUser
                && messages.All(m => m.Role != "user")
                && !string.IsNullOrWhiteSpace(Context?.CurrentUserContent))
            {
                messages.Add(CreateCurrentUserMessage(Context!.CurrentUserContent!));
            }

            // ── 第4层：易变上下文。若已固化到标准历史，则不再重复注入；
            //     否则作为当前轮临时 system 追加。 ──
            if (ctxManager != null && !volatilePersisted)
            {
                string? volatileBlock = ctxManager.BuildVolatileContextBlock();
                // ── 去重：本轮已有一份内容完全相同的 volatile 快照（固化或对账补写）时不再注入 ──
                //   否则同一轮内的多次请求（如 Edit 多步骤推进）会把同一份易变块重复叠加，
                //   既浪费 token，也让从 _entries 重建的链路与线上请求在这一点上前缀分叉。
                if (!string.IsNullOrWhiteSpace(volatileBlock)
                    && !ctxManager.HasVolatileSnapshotInCurrentTurn(volatileBlock))
                {
                    messages.Add(new ChatApiMessage { Role = "system", Content = volatileBlock });
                }
            }

            // ── 第5层：当前用户消息 ──
            // deduplicateCurrentUser=true 表示当前 user 已位于 ContextManager 历史中，
            // 不再重新包装/更新；每次新提问都保留为新增的标准 user 轮次。
            if (!deduplicateCurrentUser)
                messages.Add(CreateCurrentUserMessage(userPrompt));

            // 工具循环产生的 assistant/tool 历史插入到当前 user 之后、
            // Agent 专属提示词之前，保持标准多轮对话语义。
            if (Context != null)
                Context.ToolHistoryInsertIndex = messages.Count;

            // ── 第6层：Agent 专属行为指令（固定在最后）──
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                messages.Add(new ChatApiMessage { Role = "system", Content = systemPrompt });
            AppendExplicitRouteInstruction(messages);

            // ── 标记当前用户提问：Ask 主流程（deduplicateCurrentUser=true）的
            //    当前 user 已在历史中，给最后一条 user 消息加显式前缀，
            //    避免长上下文/工具链中模型把中间内容误当用户需求而跑偏。 ──
            if (deduplicateCurrentUser)
                ApplyCurrentUserQuestionPrefix(messages);

            return messages;
        }

        /// <summary>
        /// 给“当前用户提问”消息加显式前缀。
        /// 仅作用于本次请求的消息副本（BuildApiMessagesRecentTurns 已克隆历史），
        /// 不修改持久化上下文；已有前缀时不重复添加。
        /// </summary>
        internal static void ApplyCurrentUserQuestionPrefix(List<ChatApiMessage> messages)
            => ApplyCurrentUserQuestionPrefix(messages, "system.agent.currentUserQuestionPrefix");

        /// <summary>
        /// 给最后一条 user 消息加显式前缀，前缀文案由本地化键决定。
        /// 主对话路径用 <c>system.agent.currentUserQuestionPrefix</c>（本轮用户需求）；
        /// Handoff 目标 Agent 用 <c>system.agent.handoffTaskGoalPrefix</c>（当前任务目标）。
        /// </summary>
        /// <param name="messages">本次请求的消息列表（就地修改）。</param>
        /// <param name="localizationKey">前缀文案的本地化键。</param>
        internal static void ApplyCurrentUserQuestionPrefix(List<ChatApiMessage> messages, string localizationKey)
        {
            if (messages == null || messages.Count == 0) return;

            // 已落库的前缀不再重复处理：_entries 中的 user 在 AddUserMessage 时
            // 已写入前缀，这里用 StartsWith 兜底避免二次叠加。
            string prefix = LocalizationService.Instance[localizationKey];
            if (string.IsNullOrWhiteSpace(prefix)) return;

            // 从末尾向前找最后一条 user 消息（跳过尾部 system/工具历史）
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                var m = messages[i];
                if (m.Role != "user") continue;

                if (m.MultimodalContent is { Count: > 0 })
                {
                    var firstText = m.MultimodalContent.FirstOrDefault(p => p.Type == "text");
                    if (firstText != null
                        && !firstText.Text.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        firstText.Text = prefix + firstText.Text;
                    }
                }
                else if (!string.IsNullOrEmpty(m.Content))
                {
                    if (!m.Content!.StartsWith(prefix, StringComparison.Ordinal))
                        m.Content = prefix + m.Content;
                }
                return;
            }
        }

        /// <summary>
        /// 将显式 @Agent 的路由边界追加为最后一条 system 消息。
        /// 该消息优先级高于角色默认的自动移交策略，但仍允许必要的权限边界移交。
        /// </summary>
        private void AppendExplicitRouteInstruction(List<ChatApiMessage> messages)
        {
            if (Context?.IsExplicitRoute != true)
                return;
            if (Context.ExplicitRouteTarget.HasValue
                && Context.ExplicitRouteTarget.Value != Definition.Type)
                return;

            messages.Add(new ChatApiMessage
            {
                Role = "system",
                Content = string.Format(
                    LocalizationService.Instance["agent.explicitRoute.doNotHandoff"],
                    Definition.Name)
            });
        }

        /// <summary>
        /// 创建当前用户消息。视觉模型场景下，把 VisionContent 图片块和用户文本
        /// 合成为 OpenAI 兼容的 content 数组。
        /// </summary>
        private ChatApiMessage CreateCurrentUserMessage(string userPrompt)
        {
            var message = new ChatApiMessage
            {
                Role = "user",
                Content = userPrompt,
            };

            if (Context?.VisionContent is { Count: > 0 } visionParts)
            {
                var parts = new List<ChatContentPart>
                {
                    new ChatContentPart { Type = "text", Text = userPrompt },
                };
                parts.AddRange(visionParts);
                message.MultimodalContent = parts;
                // 一次性消费：历史消息已带原图，避免 Handoff / 步骤重试把同一批图片重复注入。
                Context.VisionContent = null;
            }

            return message;
        }

        /// <summary>
        /// 构建上下文感知的消息列表（支持注入额外的系统级上下文）。
        /// 
        /// extraSystemMessages 插入在用户消息之前，Agent 提示词仍固定在最后。
        /// 前缀结构 [0..1] 与主重载一致，确保 DeepSeek Prefix Cache 跨路径共享。
        /// </summary>
        protected List<ChatApiMessage> BuildContextAwareMessages(
            string systemPrompt,
            string userPrompt,
            List<ChatApiMessage> extraSystemMessages,
            int maxRecentTurns = int.MaxValue)
        {
            var messages = BuildContextAwareMessages(systemPrompt, userPrompt, maxRecentTurns);

            // ── 在用户消息之前注入额外的 system 消息 ──
            // 结构为 [prefix][volatile][user][agent]，用户消息在倒数第二位。
            int insertPos = Math.Max(0, messages.Count - 2);
            if (extraSystemMessages != null && extraSystemMessages.Count > 0)
            {
                messages.InsertRange(insertPos, extraSystemMessages);
            }

            return messages;
        }

        private async Task<int> ApplyPendingGuidanceMessageAsync(
            List<ChatApiMessage> messages,
            int toolInsertPos)
        {
            var guidanceItem = Context?.PendingAppendMessages?.TakeNextGuidance();
            if (guidanceItem == null)
                return toolInsertPos;

            if (Context?.OnGuidanceTurnRequested != null)
                await Context.OnGuidanceTurnRequested(guidanceItem).ConfigureAwait(false);

            string guidance = "- " + guidanceItem.Text.Trim()
                .Replace("\r\n", "\n")
                .Replace("\n", "\n  ");
            if (string.IsNullOrWhiteSpace(guidance))
                return toolInsertPos;

            string guidanceContent = string.Format(
                LocalizationService.Instance["system.agent.appendGuidance"],
                guidance);
            messages.Insert(toolInsertPos, new ChatApiMessage
            {
                Role = "user",
                Content = guidanceContent,
            });
            toolInsertPos++;
            if (Context != null)
                Context.ToolHistoryInsertIndex = toolInsertPos;
            Context?.ContextManager?.AddUserMessage(guidanceContent);

            Logger.Info($"[Agent:{Definition.Name}] 已插入 1 条生成中引导消息");
            return toolInsertPos;
        }
    }
}
