using System;
using System.Threading;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// Agent 执行预算/超时的归一化工具（从 <see cref="BaseAgent"/> 下沉）。
    /// 全部为无状态静态方法；<see cref="BaseAgent"/> 上保留同名转发以维持既有测试的编译契约。
    /// </summary>
    internal static class ExecutionLimits
    {
        /// <summary>
        /// Resolves the per-invocation tool-loop limit. This value intentionally
        /// does not include the global cache round counter.
        /// </summary>
        /// <param name="configuredSafetyLimit">配置的最大工具轮数；小于 1 时回退为 200。</param>
        /// <param name="maxToolRounds">本次调用显式指定的轮数上限。</param>
        /// <returns>本次调用生效的工具轮数上限。</returns>
        internal static int ResolveEffectiveToolRoundLimit(
            int configuredSafetyLimit,
            int? maxToolRounds)
        {
            if (configuredSafetyLimit < 1)
                configuredSafetyLimit = 200;

            return maxToolRounds is > 0
                ? Math.Min(configuredSafetyLimit, maxToolRounds.Value)
                : configuredSafetyLimit;
        }

        /// <summary>
        /// 解析子代理看门狗超时时间。
        /// </summary>
        /// <param name="configuredSeconds">配置的秒数；非正值表示不限时。</param>
        /// <returns>上限 7200 秒的超时；配置非正时返回 <see cref="Timeout.InfiniteTimeSpan"/>。</returns>
        internal static TimeSpan ResolveSubagentWatchdogTimeout(int configuredSeconds)
        {
            if (configuredSeconds <= 0)
                return Timeout.InfiniteTimeSpan;

            return TimeSpan.FromSeconds(Math.Min(configuredSeconds, 7200));
        }

        /// <summary>
        /// Applies a configured execution budget with a deterministic fallback
        /// and bounds. A minimum of zero allows nullable or unlimited budgets.
        /// </summary>
        /// <param name="configuredValue">配置值；非正时使用回退值。</param>
        /// <param name="fallbackValue">回退值。</param>
        /// <param name="minimum">允许的最小值。</param>
        /// <param name="maximum">允许的最大值。</param>
        /// <returns>夹在 [minimum, maximum] 区间内的最终值。</returns>
        /// <exception cref="ArgumentOutOfRangeException">当 <paramref name="minimum"/> 大于 <paramref name="maximum"/> 时抛出。</exception>
        internal static int NormalizeExecutionSetting(
            int configuredValue,
            int fallbackValue,
            int minimum,
            int maximum)
        {
            if (minimum > maximum)
                throw new ArgumentOutOfRangeException(nameof(minimum));

            int value = configuredValue <= 0 ? fallbackValue : configuredValue;
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
