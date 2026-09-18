using System;
using System.Collections.Generic;
using System.Linq;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    /// <summary>
    /// 生成过程中发送的追加消息处理模式。
    /// </summary>
    public enum AppendMessageMode
    {
        /// <summary>当前任务完全结束后，作为下一轮新会话自动发送。</summary>
        Queue,

        /// <summary>在当前思考与工具调用循环的下一轮边界插入，用于引导当前任务。</summary>
        Guidance,
    }

    /// <summary>
    /// 追加消息的运行时状态。
    /// </summary>
    public enum AppendMessageState
    {
        Pending,
        Dispatched,
    }

    /// <summary>
    /// 一条等待处理的生成中追加消息。
    /// </summary>
    public sealed class PendingAppendMessage
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");

        public long Sequence { get; internal set; }

        public string Text { get; }

        public string Model { get; }

        public AppendMessageMode Mode { get; internal set; }

        public AppendMessageState State { get; internal set; }

        public PendingAppendMessage(string text, string model, AppendMessageMode mode)
        {
            Text = text ?? string.Empty;
            Model = model ?? string.Empty;
            Mode = mode;
        }
    }

    /// <summary>
    /// 线程安全的追加消息协调器。UI 与 AgentContext 共享同一实例：
    /// Agent 循环只消费引导项，工作流收尾只消费排队项。
    /// </summary>
    public sealed class PendingAppendMessageStore
    {
        private readonly object _sync = new();
        private readonly List<PendingAppendMessage> _items = new();
        private long _sequence;

        public PendingAppendMessage Add(string text, string model, AppendMessageMode mode)
        {
            lock (_sync)
            {
                var item = new PendingAppendMessage(text, model, mode)
                {
                    Sequence = ++_sequence,
                };
                _items.Add(item);
                return item;
            }
        }

        public IReadOnlyList<PendingAppendMessage> Snapshot()
        {
            lock (_sync)
            {
                return _items.OrderBy(item => item.Sequence).ToList();
            }
        }

        public bool ToggleMode(string id)
        {
            lock (_sync)
            {
                var item = _items.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, id, StringComparison.Ordinal));
                if (item == null || item.State != AppendMessageState.Pending)
                    return false;

                item.Mode = item.Mode == AppendMessageMode.Queue
                    ? AppendMessageMode.Guidance
                    : AppendMessageMode.Queue;
                return true;
            }
        }

        /// <summary>
        /// 取出最早的一条引导消息并从待处理集合移除。
        /// 一次只消费一条，使多条引导能够依次形成独立轮次。
        /// </summary>
        public PendingAppendMessage? TakeNextGuidance()
        {
            lock (_sync)
            {
                var item = FindNextPending(AppendMessageMode.Guidance);
                if (item == null)
                    return null;

                item.State = AppendMessageState.Dispatched;
                _items.Remove(item);
                return item;
            }
        }

        /// <summary>
        /// 取出最早的一条排队消息并标记为已派发；没有排队项时返回 null。
        /// </summary>
        public PendingAppendMessage? TakeNextQueued()
        {
            lock (_sync)
            {
                var item = FindNextPending(AppendMessageMode.Queue);
                if (item == null)
                    return null;

                item.State = AppendMessageState.Dispatched;
                _items.Remove(item);
                return item;
            }
        }

        /// <summary>
        /// 兜底取出尚未被工具循环消费的引导项，转为下一轮发送。
        /// 用于任务在引导到达的同一瞬间结束、已经没有下一轮可插入的场景。
        /// </summary>
        public PendingAppendMessage? TakeNextUndeliveredGuidance()
        {
            lock (_sync)
            {
                var item = FindNextPending(AppendMessageMode.Guidance);
                if (item == null)
                    return null;

                item.State = AppendMessageState.Dispatched;
                _items.Remove(item);
                return item;
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _items.Clear();
                _sequence = 0;
            }
        }

        private PendingAppendMessage? FindNextPending(AppendMessageMode mode)
        {
            return _items
                .Where(candidate =>
                    candidate.Mode == mode
                    && candidate.State == AppendMessageState.Pending)
                .OrderBy(candidate => candidate.Sequence)
                .FirstOrDefault();
        }
    }
}
