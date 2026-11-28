using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using DeepSeek_v4_for_VisualStudio.Utils;
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
    /// <see cref="BaseAgent"/> 的 移交缓存与消息克隆 分部实现。
    /// 承载该职责域的成员，使主文件聚焦于 Agent 执行编排。
    /// </summary>
    public abstract partial class BaseAgent
    {
        /// <summary>
        /// 获取最近一次成功发送的清洗后消息列表快照。
        /// 用于 Handoff / runSubagent 转发缓存前缀；返回独立副本，避免调用方复用后再次修改。
        /// </summary>
        protected List<ChatApiMessage>? CaptureLastSentMessagesForCache()
        {
            IReadOnlyList<ChatApiMessage>? sent = _apiService.LastSentMessages;
            if (sent == null || sent.Count == 0)
                return null;

            return CloneApiMessages(sent);
        }

        /// <summary>
        /// 把本次实际发出的消息与内部历史对账，补齐主流程「发出但未落库」的内容。
        ///
        /// 背景：请求构建器会在消息副本上临时追加几类**主流程**内容（Handoff 的 volatile 块与
        /// 任务提示、路由边界指令、Agent 行为指令），它们此前从不写回 <c>_entries</c>。
        /// 这会让从 <c>_entries</c> 重建的历史「缺了几条」，既丢上下文，也让前缀缓存从缺失点断裂。
        ///
        /// <b>仅主流程可调用</b>：一次性子任务（记忆判定、总结润色）的提示词与返回结果
        /// 不属于主流程，绝不能落库，否则会污染下一轮主对话请求。
        /// </summary>
        /// <param name="sentMessages">
        /// 显式指定的发送快照；传 null 时取 <see cref="CaptureLastSentMessagesForCache"/>。
        /// 工具循环内应传入本地 messages（含当轮新插入的 assistant/tool），
        /// 因为 Provider 的 LastSentMessages 只反映最近一次 HTTP 调用。
        /// </param>
        protected int SyncSentMessagesToHistory(List<ChatApiMessage>? sentMessages = null)
        {
            var ctxManager = Context?.ContextManager;
            if (ctxManager == null)
                return 0;

            var sent = sentMessages ?? CaptureLastSentMessagesForCache();
            if (sent == null || sent.Count == 0)
                return 0;

            try
            {
                return ctxManager.SyncSentMessagesToHistory(sent);
            }
            catch (Exception ex)
            {
                // 对账失败不能影响主流程：历史补齐是增强，不是必需路径
                Logger.Warn($"[Agent:{Definition.Name}] 发送内容对账失败: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// 获取当前 Agent 可复用给 Handoff 的缓存前缀。
        /// 优先使用工具循环保存的 ForwardedMessages，其次回退到最近一次实际发送的清洗后消息。
        /// </summary>
        public List<ChatApiMessage>? SnapshotHandoffCacheMessages()
        {
            var forwarded = Context?.ForwardedMessages;
            if (forwarded != null && forwarded.Count > 0)
                return CloneApiMessages(forwarded);

            var sentMessages = CaptureLastSentMessagesForCache();
            return sentMessages == null ? null : PrepareHandoffCacheMessages(sentMessages);
        }

        /// <summary>
        /// 获取 Handoff 缓存前缀，并把源 Agent 已完成但尚未落库的最终 assistant 回答追加进去。
        /// 标准顺序应为：... tool result → assistant 最终回答 → 目标 Agent 的 user/system。
        /// 若源 Agent 最后一步本身是 request_handoff 工具链，则不再追加移交提示，避免重复。
        /// </summary>
        /// <param name="completedResult">源 Agent 已完成的执行结果。</param>
        /// <returns>可直接交给目标 Agent 复用的缓存前缀。</returns>
        public List<ChatApiMessage>? SnapshotHandoffCacheMessages(AgentResult? completedResult)
        {
            var snapshot = SnapshotHandoffCacheMessages();
            EnsureCompletedAssistantInHandoffPrefix(snapshot, completedResult);
            return snapshot;
        }

        /// <summary>
        /// 确保源 Agent 的最终回答作为 assistant 消息位于 Handoff 前缀末尾。
        /// 该方法会去重，并跳过已经由 request_handoff 工具链表达完成的移交。
        /// </summary>
        /// <param name="snapshot">待补充的 Handoff 前缀；可为 null。</param>
        /// <param name="completedResult">源 Agent 已完成的执行结果。</param>
        /// <returns>是否新增了 assistant 消息。</returns>
        internal static bool EnsureCompletedAssistantInHandoffPrefix(
            List<ChatApiMessage>? snapshot,
            AgentResult? completedResult)
        {
            if (snapshot == null
                || completedResult == null
                || string.IsNullOrWhiteSpace(completedResult.Content))
            {
                return false;
            }

            // 源 Agent 的行为 system prompt 位于请求尾部，不属于对话内容。
            // build_solution 等路径保存的快照可能仍带着它；先移除，再追加最终 assistant 回答。
            if (snapshot.Count > 0
                && string.Equals(snapshot[snapshot.Count - 1].Role, "system", StringComparison.Ordinal))
            {
                snapshot.RemoveAt(snapshot.Count - 1);
            }

            int lastAssistantIndex = snapshot.FindLastIndex(
                m => string.Equals(m.Role, "assistant", StringComparison.Ordinal));

            // request_handoff 的 assistant(tool_calls)+tool 结果已经表达完本轮动作；
            // 其结果正文通常是 HANDOFF_REQUESTED，不应再伪装成一条 assistant 回答。
            for (int i = lastAssistantIndex + 1; i < snapshot.Count; i++)
            {
                var message = snapshot[i];
                if (string.Equals(message.Role, "tool", StringComparison.Ordinal)
                    && string.Equals(message.Name, "request_handoff", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (lastAssistantIndex >= 0
                && string.Equals(
                    snapshot[lastAssistantIndex].Content?.Trim(),
                    completedResult.Content.Trim(),
                    StringComparison.Ordinal))
            {
                return false;
            }

            snapshot.Add(new ChatApiMessage
            {
                Role = "assistant",
                Content = completedResult.Content,
                // completedResult.ReasoningContent 是跨 API 子轮次聚合后的 UI 展示值，
                // 不是某一次 assistant 响应自己的 reasoning_content。写入 API 历史
                // 会破坏官方要求的「逐条、原样回传」语义，因此合成消息不携带该字段。
            });
            return true;
        }

        /// <summary>
        /// 准备 Handoff 转发的完整消息历史。只移除源 Agent 末尾的身份提示词，
        /// 保留当前 user 及其全部 assistant/tool_calls + tool result 历史。
        /// </summary>
        private static List<ChatApiMessage> PrepareHandoffCacheMessages(List<ChatApiMessage> messages)
        {
            if (messages.Count > 0 && messages[messages.Count - 1].Role == "system")
                messages.RemoveAt(messages.Count - 1);

            return messages;
        }

        /// <summary>
        /// 在 Agent 工具循环中进行上下文压缩，并同步裁剪本地 messages。
        /// 压缩边界与正常请求共用 ContextManager 的判断逻辑。
        /// </summary>
        private void TryCompressToolLoopContext(
            ref List<ChatApiMessage> messages,
            ref int toolInsertPos)
        {
            var contextManager = Context?.ContextManager;
            if (contextManager == null || Context?.ForwardedMessages != null)
                return;

            if (!contextManager.TryCompressForToolLoop(
                    out int staticPrefixMessageCount,
                    out int removedMessageCount,
                    out string? dynamicBlock))
                return;

            int removeStart = staticPrefixMessageCount;
            if (removedMessageCount <= 0
                || removeStart < 0
                || removeStart + removedMessageCount > messages.Count)
            {
                Logger.Warn($"[ToolLoopCompression] 本地消息与压缩计数不匹配: " +
                    $"removeStart={removeStart}, removeCount={removedMessageCount}, messages={messages.Count}");
                return;
            }

            int dynamicBlockIndex = contextManager.HasMemoryContext ? 2 : 1;
            bool hadDynamicBlock = staticPrefixMessageCount > dynamicBlockIndex;
            bool hasDynamicBlock = !string.IsNullOrWhiteSpace(dynamicBlock);

            messages.RemoveRange(removeStart, removedMessageCount);
            if (toolInsertPos > removeStart)
                toolInsertPos -= Math.Min(removedMessageCount, toolInsertPos - removeStart);

            if (hasDynamicBlock && !hadDynamicBlock)
            {
                messages.Insert(dynamicBlockIndex, new ChatApiMessage
                {
                    Role = "system",
                    Content = dynamicBlock,
                });
                toolInsertPos++;
            }
            else if (!hasDynamicBlock && hadDynamicBlock)
            {
                messages.RemoveAt(dynamicBlockIndex);
                toolInsertPos--;
            }
            else if (hasDynamicBlock && messages.Count > dynamicBlockIndex)
            {
                messages[dynamicBlockIndex].Content = dynamicBlock;
            }

            toolInsertPos = Math.Max(0, Math.Min(toolInsertPos, messages.Count));
            if (Context != null)
                Context.ToolHistoryInsertIndex = toolInsertPos;

            Logger.Info($"[ToolLoopCompression] 已裁剪本地消息 {removedMessageCount} 条，" +
                $"toolInsertPos={toolInsertPos}, remaining={messages.Count}");
        }

        /// <summary>
        /// 深拷贝 ChatApiMessage 列表，避免 Handoff 后目标 Agent 修改复制列表时影响源快照。
        /// </summary>
        protected static List<ChatApiMessage> CloneApiMessages(IEnumerable<ChatApiMessage> messages)
            => ChatMessageCloner.CloneAll(messages);
    }
}
