using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.WebAssets
{
    /// <summary>
    /// 协调 WebView 前端「过程折叠块已挂载到 DOM」回执的等待器。
    /// 以轮次标识为键登记一次性等待，允许超时静默放弃，并在页面导航或控件销毁时统一取消，
    /// 避免「节点永远不会出现」的正常场景（页面已切走、会话被替换）留下悬挂任务。
    /// </summary>
    public sealed class TurnProcessReceiptWaiter : IDisposable
    {
        /// <summary>已登记但尚未完成的等待项，键为轮次标识。</summary>
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending =
            new ConcurrentDictionary<string, TaskCompletionSource<bool>>(StringComparer.Ordinal);

        /// <summary>是否已释放。释放后不再接受新登记，已有等待立即以 false 结束。</summary>
        private volatile bool _disposed;

        /// <summary>
        /// 登记一个轮次的等待项。必须在执行握手脚本之前调用，
        /// 否则回执可能先于登记到达而被丢弃。
        /// </summary>
        /// <param name="turnId">本轮流式消息的唯一标识，与过程折叠块的 data-turn-id 一致。</param>
        public void Register(string turnId)
        {
            if (string.IsNullOrEmpty(turnId) || _disposed)
                return;

            // 同一轮次重复登记（重试、兜底重入）时复用既有等待项，避免丢失已到达的回执。
            _pending.GetOrAdd(
                turnId,
                _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        /// <summary>
        /// 标记指定轮次的过程折叠块已渲染。未登记的轮次会被忽略（可能来自已放弃的回合）；
        /// 重复调用幂等。
        /// </summary>
        /// <param name="turnId">前端回执携带的轮次标识。</param>
        public void Complete(string turnId)
        {
            if (string.IsNullOrEmpty(turnId))
                return;

            if (_pending.TryRemove(turnId, out var tcs))
            {
                tcs.TrySetResult(true);
                return;
            }

            // 未登记即到达：多为会话切换后旧页面的迟到回执，属预期情况，不升级为警告。
            Logger.Debug($"[Render] 收到未登记的过程折叠块回执，已忽略: turn={turnId}");
        }

        /// <summary>取消并移除全部未完成等待项，等待方立即以 false 返回。</summary>
        public void CancelAll()
        {
            foreach (var key in _pending.Keys)
            {
                if (_pending.TryRemove(key, out var tcs))
                    tcs.TrySetResult(false);
            }
        }

        /// <summary>
        /// 等待指定轮次的渲染回执。
        /// </summary>
        /// <param name="turnId">已登记的轮次标识。</param>
        /// <param name="timeout">最长等待时长；超时返回 false 并移除条目。</param>
        /// <param name="ct">取消令牌，通常绑定页面存活期。</param>
        /// <returns>在超时或取消前收到回执返回 true，否则返回 false；本方法不抛出取消异常。</returns>
        public async Task<bool> WaitAsync(string turnId, TimeSpan timeout, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(turnId))
                return false;

            if (!_pending.TryGetValue(turnId, out var tcs))
                return false;

            // 用 Task<bool> 而非 Task：等待结果的语义就是布尔值，
            // 声明成 Task 后 await 得到 void，无法直接作为返回值。
            Task<bool> receipt = tcs.Task;

            // 超时计时器与会话取消并联：任一先到都结束等待，并显式取消计时器释放资源。
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                Task delay = Task.Delay(timeout, timeoutCts.Token);

                try
                {
                    Task winner = await Task.WhenAny(receipt, delay).ConfigureAwait(false);
                    timeoutCts.Cancel();

                    if (ReferenceEquals(winner, receipt))
                        return await receipt.ConfigureAwait(false);

                    // 超时清理，防止字典无界增长。
                    _pending.TryRemove(turnId, out _);
                    return false;
                }
                catch (OperationCanceledException)
                {
                    // 页面导航或控件销毁导致的取消属正常路径，等待方按「未就绪」处理。
                    _pending.TryRemove(turnId, out _);
                    return false;
                }
            }
        }

        /// <summary>释放等待器并取消全部在途等待。</summary>
        public void Dispose()
        {
            _disposed = true;
            CancelAll();
        }
    }
}
