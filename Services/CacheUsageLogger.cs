using System;
using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// Prompt Cache 命中率日志。
    ///
    /// <c>BaseAgent</c> 与 <c>DeepSeekChatControl</c> 此前各有一份逐字相同的
    /// <c>LogCacheHitRate</c>（含同样的 emoji 分档与本地化格式），
    /// 改动一处格式必须同步另一处。统一收敛到这里。
    /// </summary>
    internal static class CacheUsageLogger
    {
        /// <summary>
        /// 记录一次 API 调用的 Cache 命中率。
        /// </summary>
        /// <param name="usage">上一次调用的 usage 统计；null 时静默返回。</param>
        /// <param name="round">当前工具调用轮次（0 表示非工具循环模式）。</param>
        public static void Log(DeepSeekUsage? usage, int round = 0)
        {
            if (usage == null)
                return;

            try
            {
                int hit = usage.EffectiveHitTokens;
                int miss = usage.EffectiveMissTokens;
                int total = hit + miss;
                double rate = usage.CacheHitRate;

                string roundInfo = round > 0 ? $"[轮次#{round}] " : "";
                string level = rate >= 0.90 ? "🟢" : rate >= 0.50 ? "🟡" : rate >= 0.20 ? "🟠" : "🔴";

                Logger.Info($"[Cache] {level} {roundInfo}命中率: {usage.CacheHitRatePercent} " +
                    $"(命中 {hit:N0} / 未命中 {miss:N0} / 总计 {total:N0} tokens)");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Cache] 记录命中率异常: {ex.Message}");
            }
        }
    }
}
