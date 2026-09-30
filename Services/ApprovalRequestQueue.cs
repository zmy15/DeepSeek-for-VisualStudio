using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.Collections.Generic;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 审批请求的串行显示队列：保证聊天区同一时刻只显示一个审批卡片，
    /// 当前请求处理完成后才交出下一个；排队期间已失效（取消 / 已响应）的请求自动跳过。
    ///
    /// 背景：一轮工具调用是并行执行的（<c>BaseAgent</c> 用 <c>Task.WhenAll</c>），
    /// 多个终端 / git / 删除操作会同时进入审批。此前每个请求各自注入一张卡片，
    /// 并发审批会在聊天区堆叠显示，用户难以判断先处理哪一个。
    ///
    /// 本类只负责「显示顺序」这一事实，不触碰 UI；线程安全。
    /// </summary>
    public sealed class ApprovalRequestQueue
    {
        private readonly object _lock = new object();
        private readonly Queue<AgentPermissionRequest> _queue = new Queue<AgentPermissionRequest>();
        private readonly HashSet<string> _queuedIds = new HashSet<string>(StringComparer.Ordinal);
        private AgentPermissionRequest? _current;

        /// <summary>当前应显示的审批请求；没有正在显示的请求时为 null。</summary>
        public AgentPermissionRequest? Current
        {
            get { lock (_lock) return _current; }
        }

        /// <summary>尚未显示的排队请求数（不含当前显示的请求）。</summary>
        public int PendingCount
        {
            get { lock (_lock) return _queue.Count; }
        }

        /// <summary>
        /// 将审批请求加入队列；同一 RequestId 重复入队会被忽略。
        /// </summary>
        /// <param name="request">待显示的审批请求。</param>
        /// <returns>true 表示已入队（调用方应随即尝试显示队首请求）。</returns>
        public bool Enqueue(AgentPermissionRequest request)
        {
            if (request == null)
                return false;

            lock (_lock)
            {
                if (string.Equals(_current?.RequestId, request.RequestId, StringComparison.Ordinal))
                    return false;

                if (!_queuedIds.Add(request.RequestId))
                    return false;

                _queue.Enqueue(request);
                return true;
            }
        }

        /// <summary>
        /// 取出下一个仍在等待用户响应的请求作为当前项。
        /// 已有当前项、或队列中没有有效请求时返回 null（保证不会同时显示两个）。
        /// </summary>
        /// <returns>新的当前显示项；无可用项时返回 null。</returns>
        public AgentPermissionRequest? TakeNext()
        {
            lock (_lock)
            {
                if (_current != null)
                    return null;

                while (_queue.Count > 0)
                {
                    var candidate = _queue.Dequeue();
                    _queuedIds.Remove(candidate.RequestId);

                    // 排队期间已被响应 / 取消 → 跳过，避免显示永远无法完成的僵尸卡片
                    if (!IsStillPending(candidate))
                        continue;

                    _current = candidate;
                    return candidate;
                }

                return null;
            }
        }

        /// <summary>
        /// 标记请求已结束（用户响应 / 取消 / 超时）。
        /// </summary>
        /// <param name="requestId">已结束的请求 ID。</param>
        /// <returns>该请求正是当前显示项时返回它（调用方据此移除卡片）；否则返回 null。</returns>
        public AgentPermissionRequest? Complete(string requestId)
        {
            if (string.IsNullOrEmpty(requestId))
                return null;

            lock (_lock)
            {
                if (_current == null
                    || !string.Equals(_current.RequestId, requestId, StringComparison.Ordinal))
                {
                    return null;
                }

                var completed = _current;
                _current = null;
                return completed;
            }
        }

        /// <summary>
        /// 清空队列与当前项（停止生成 / 清空会话 / 切换会话时调用）。
        /// </summary>
        /// <returns>被清空的当前显示项（调用方据此移除残留卡片）；无当前项时返回 null。</returns>
        public AgentPermissionRequest? Reset()
        {
            lock (_lock)
            {
                var displayed = _current;
                _current = null;
                _queue.Clear();
                _queuedIds.Clear();
                return displayed;
            }
        }

        /// <summary>
        /// 请求是否仍在等待用户响应：以 <see cref="AgentPermissionRequest.ResponseTcs"/> 是否完成为准。
        /// 比查询 Agent 的待处理字典更可靠——停止生成、超时或 Agent 异常结束时
        /// TCS 会立即置位，而字典条目可能尚未清理，此时继续显示卡片会永久阻塞后续审批。
        /// </summary>
        /// <param name="request">待判定的请求。</param>
        /// <returns>仍在等待响应时返回 true。</returns>
        public static bool IsStillPending(AgentPermissionRequest request)
        {
            if (request == null)
                return false;

            var tcs = request.ResponseTcs;
            return tcs == null || !tcs.Task.IsCompleted;
        }
    }
}
