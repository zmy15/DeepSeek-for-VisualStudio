using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 解析并计算模型上下文 Token 限制。
    /// 全局预算使用百分比；每个模型独立配置最大 Token，未配置时默认 1M。
    /// </summary>
    public static class ModelTokenLimitService
    {
        public const int DefaultMaxTokens = 1_000_000;
        public const int DefaultBudgetPercent = 90;

        /// <summary>
        /// 解析模型最大 Token 配置。每行格式为 "模型名=最大Token"，也支持冒号分隔。
        /// 数值支持 K/M 后缀，例如 128K、1M；空行和 # 注释会被忽略。
        /// </summary>
        public static IReadOnlyDictionary<string, int> ParseLimits(string? configuration)
        {
            var limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(configuration))
                return limits;

            foreach (string rawLine in configuration.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                int separator = line.IndexOf('=');
                if (separator < 0)
                    separator = line.IndexOf(':');
                if (separator <= 0 || separator >= line.Length - 1)
                    continue;

                string model = line.Substring(0, separator).Trim();
                string rawValue = line.Substring(separator + 1).Trim();
                if (model.Length == 0 || !TryParseTokenCount(rawValue, out int maxTokens))
                    continue;

                limits[model] = maxTokens;
            }

            return limits;
        }

        /// <summary>获取指定模型的最大 Token；未配置时默认 1M。</summary>
        public static int GetMaxTokens(string? model, string? configuration)
        {
            string normalizedModel = (model ?? string.Empty).Trim();
            if (normalizedModel.Length > 0 &&
                ParseLimits(configuration).TryGetValue(normalizedModel, out int configured))
            {
                return configured;
            }

            return DefaultMaxTokens;
        }

        /// <summary>按全局百分比计算有效上下文预算。</summary>
        public static int CalculateBudget(string? model, int configuredPercent, string? configuration)
        {
            int percent = NormalizeBudgetPercent(configuredPercent);
            int maxTokens = GetMaxTokens(model, configuration);
            int budget = (int)Math.Round(
                maxTokens * (percent / 100.0),
                MidpointRounding.AwayFromZero);
            return Math.Max(1, budget);
        }

        /// <summary>
        /// 将设置值归一化为 1-100。
        /// 兼容旧版本保存的绝对 Token 数（例如 900000 → 90）。
        /// </summary>
        public static int NormalizeBudgetPercent(int configured)
        {
            if (configured <= 0)
                return DefaultBudgetPercent;

            if (configured > 1_000)
            {
                int migrated = (int)Math.Round(
                    configured * 100.0 / DefaultMaxTokens,
                    MidpointRounding.AwayFromZero);
                configured = migrated;
            }

            return Math.Max(1, Math.Min(100, configured));
        }

        /// <summary>将整数 Token 数格式化为便于编辑的 K/M 文本。</summary>
        internal static string FormatTokenCount(int value)
        {
            if (value > 0 && value % 1_000_000 == 0)
                return (value / 1_000_000) + "M";
            if (value > 0 && value % 1_000 == 0)
                return (value / 1_000) + "K";
            return value.ToString(CultureInfo.InvariantCulture);
        }

        internal static bool TryParseTokenCount(string text, out int value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string normalized = text.Trim()
                .Replace("_", string.Empty)
                .Replace(",", string.Empty);
            double multiplier = 1;
            char suffix = normalized[normalized.Length - 1];
            if (suffix == 'k' || suffix == 'K')
            {
                multiplier = 1_000;
                normalized = normalized.Substring(0, normalized.Length - 1).Trim();
            }
            else if (suffix == 'm' || suffix == 'M')
            {
                multiplier = 1_000_000;
                normalized = normalized.Substring(0, normalized.Length - 1).Trim();
            }

            if (!double.TryParse(
                    normalized,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double number) ||
                double.IsNaN(number) ||
                double.IsInfinity(number) ||
                number <= 0)
            {
                return false;
            }

            double tokens = Math.Round(number * multiplier, MidpointRounding.AwayFromZero);
            if (tokens < 1 || tokens > int.MaxValue)
                return false;

            value = (int)tokens;
            return true;
        }
    }
}