using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    public sealed class AgentExecutionPolicy
    {
        public int MaxSteps { get; init; } = 200;
        public TimeSpan MaxWallTime { get; init; }
        public long MaxTotalTokens { get; init; }
        public int MaxToolCalls { get; init; } = 400;
        public int MaxExecutionDepth { get; init; } = 3;
        public int MaxNoProgressRounds { get; init; } = 5;
    }

    public enum AgentExecutionStopReason
    {
        None,
        MaxSteps,
        WallTime,
        TokenBudget,
        ToolCallBudget,
        MaxDepth,
        NoProgress
    }

    public readonly struct AgentExecutionDecision
    {
        private AgentExecutionDecision(
            bool shouldStop,
            AgentExecutionStopReason reason,
            string message)
        {
            ShouldStop = shouldStop;
            Reason = reason;
            Message = message;
        }

        public bool ShouldStop { get; }
        public AgentExecutionStopReason Reason { get; }
        public string Message { get; }

        public static AgentExecutionDecision Continue()
            => new(false, AgentExecutionStopReason.None, string.Empty);

        public static AgentExecutionDecision Stop(
            AgentExecutionStopReason reason,
            string message)
            => new(true, reason, message);
    }

    internal sealed class AgentExecutionGuard
    {
        private readonly AgentExecutionPolicy _policy;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly Queue<string> _recentStateFingerprints = new();
        private long _totalTokens;
        private int _toolCallCount;
        private bool _warningEmitted;

        public AgentExecutionGuard(AgentExecutionPolicy policy, int executionDepth)
        {
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            ExecutionDepth = executionDepth;
        }

        public int ExecutionDepth { get; }
        private bool HasWallTimeLimit => _policy.MaxWallTime > TimeSpan.Zero;

        public AgentExecutionDecision CheckBeforeStep(int nextStep)
        {
            if (ExecutionDepth > _policy.MaxExecutionDepth)
            {
                return AgentExecutionDecision.Stop(
                    AgentExecutionStopReason.MaxDepth,
                    $"已超过最大 Agent 递归深度 {_policy.MaxExecutionDepth}，已停止当前分支。");
            }

            if (nextStep > _policy.MaxSteps)
            {
                return AgentExecutionDecision.Stop(
                    AgentExecutionStopReason.MaxSteps,
                    $"已达到最大工具循环步数 {_policy.MaxSteps}，已安全停止。");
            }

            if (HasWallTimeLimit && _stopwatch.Elapsed >= _policy.MaxWallTime)
            {
                return AgentExecutionDecision.Stop(
                    AgentExecutionStopReason.WallTime,
                    $"已超过 Agent 墙钟时间上限 {FormatDuration(_policy.MaxWallTime)}，已安全停止。");
            }

            return AgentExecutionDecision.Continue();
        }

        public AgentExecutionDecision RecordUsage(int promptTokens, int completionTokens)
        {
            _totalTokens += Math.Max(0, promptTokens) + Math.Max(0, completionTokens);
            if (_policy.MaxTotalTokens > 0 && _totalTokens > _policy.MaxTotalTokens)
            {
                return AgentExecutionDecision.Stop(
                    AgentExecutionStopReason.TokenBudget,
                    $"已超过 Agent Token 预算 {_policy.MaxTotalTokens:N0}，已安全停止。");
            }

            return AgentExecutionDecision.Continue();
        }

        public AgentExecutionDecision RecordToolCalls(int count)
        {
            _toolCallCount += Math.Max(0, count);
            if (_toolCallCount > _policy.MaxToolCalls)
            {
                return AgentExecutionDecision.Stop(
                    AgentExecutionStopReason.ToolCallBudget,
                    $"已超过 Agent 工具调用次数上限 {_policy.MaxToolCalls}，已安全停止。");
            }

            return AgentExecutionDecision.Continue();
        }

        public AgentExecutionDecision RecordState(IEnumerable<string> stateParts)
        {
            string fingerprint = ComputeFingerprint(stateParts);
            _recentStateFingerprints.Enqueue(fingerprint);

            int windowSize = Math.Max(_policy.MaxNoProgressRounds * 2, 8);
            while (_recentStateFingerprints.Count > windowSize)
                _recentStateFingerprints.Dequeue();

            int repetitions = _recentStateFingerprints.Count(item => item == fingerprint);
            if (repetitions >= _policy.MaxNoProgressRounds)
            {
                return AgentExecutionDecision.Stop(
                    AgentExecutionStopReason.NoProgress,
                    $"检测到连续 {repetitions} 次无进展状态，已停止以避免无效循环。");
            }

            return AgentExecutionDecision.Continue();
        }

        public string? GetBudgetWarning(int nextStep)
        {
            if (_warningEmitted)
                return null;

            bool warning =
                nextStep >= _policy.MaxSteps * 0.8 ||
                (HasWallTimeLimit && _stopwatch.Elapsed >= TimeSpan.FromTicks((long)(_policy.MaxWallTime.Ticks * 0.8))) ||
                (_policy.MaxTotalTokens > 0 && _totalTokens >= _policy.MaxTotalTokens * 0.8) ||
                _toolCallCount >= _policy.MaxToolCalls * 0.8;

            if (!warning)
                return null;

            _warningEmitted = true;
            return $"Agent 执行预算已使用超过 80%：steps={nextStep}/{_policy.MaxSteps}, " +
                $"tokens={_totalTokens:N0}/{_policy.MaxTotalTokens:N0}, " +
                $"tools={_toolCallCount}/{_policy.MaxToolCalls}, " +
                $"elapsed={FormatDuration(_stopwatch.Elapsed)}/{FormatWallTimeLimit()}";
        }

        private static string ComputeFingerprint(IEnumerable<string> stateParts)
        {
            unchecked
            {
                const ulong offset = 14695981039346656037;
                const ulong prime = 1099511628211;
                ulong hash = offset;

                foreach (string part in stateParts ?? Enumerable.Empty<string>())
                {
                    string text = part ?? string.Empty;
                    int sampleLength = Math.Min(text.Length, 4096);
                    for (int i = 0; i < sampleLength; i++)
                    {
                        hash ^= text[i];
                        hash *= prime;
                    }

                    hash ^= (ulong)text.Length;
                    hash *= prime;
                    hash ^= '\u001f';
                    hash *= prime;
                }

                return hash.ToString("X16");
            }
        }

        private static string FormatDuration(TimeSpan duration)
            => duration.TotalMinutes >= 1
                ? $"{duration.TotalMinutes:F1} min"
                : $"{duration.TotalSeconds:F0} s";

        private string FormatWallTimeLimit()
            => HasWallTimeLimit ? FormatDuration(_policy.MaxWallTime) : "unlimited";
    }
}
