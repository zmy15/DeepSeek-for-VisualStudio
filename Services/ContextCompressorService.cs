using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 上下文压缩服务 — 当 Token 预算接近上限时，将早期消息压缩为精简摘要，
    /// 替代直接删除旧消息，保留关键信息不丢失。
    /// 
    /// 压缩策略：
    /// 1. 保留最近 N 轮完整消息（由 PreserveRecentTurns 控制）
    /// 2. 更早的轮次按批压缩为摘要文本
    /// 3. 压缩摘要由 ConversationContextManager 注入到动态上下文块（messages[1]）中
    ///    而非作为独立的 system 消息，以保护 messages[0] 的前缀缓存稳定性
    /// 4. 支持多次渐进压缩（压缩后的摘要可被再次压缩）
    /// 
    /// ── 缓存对齐策略（v1.1.9）──
    /// 压缩摘要不再作为独立的 system 消息注入，而是合并到动态上下文块中。
    /// 这样 messages[0]（冻结的系统提示词）和对话历史的前缀在压缩前后保持不变，
    /// DeepSeek 的自动前缀缓存可以持续命中。
    /// 参考：CodeWhale compaction.rs Cache-Aligned Summary Path
    /// </summary>
    public class ContextCompressorService : IContextCompressorService
    {
        /// <summary>
        /// LLM 摘要器。传入的 messages 是从对话开头截取到待压缩区间末尾的完整前缀，
        /// 最后一条是压缩要求。
        /// </summary>
        public delegate Task<string> ContextSummarizer(
            IReadOnlyList<ChatApiMessage> messages,
            CancellationToken cancellationToken);

        private readonly CompressionConfig _config;
        private readonly List<CompressedTurnSummary> _compressedSummaries = new();
        private readonly ContextSummarizer? _summarizer;
        private readonly object _summaryLock = new();
        private string? _frozenCompressedContextText;

        /// <summary>已压缩的轮次摘要列表</summary>
        public IReadOnlyList<CompressedTurnSummary> CompressedSummaries => _compressedSummaries;

        /// <summary>压缩配置</summary>
        public CompressionConfig Config => _config;

        /// <summary>
        /// 创建压缩服务实例。
        /// </summary>
        /// <param name="summarizer">
        /// 摘要生成函数，接收待压缩文本和取消令牌，返回压缩后的摘要。
        /// 如果为 null，则使用基于规则的本地提取（无 LLM 调用）。
        /// </param>
        /// <param name="config">压缩配置，为 null 时使用默认配置</param>
        public ContextCompressorService(
            ContextSummarizer? summarizer = null,
            CompressionConfig? config = null)
        {
            _summarizer = summarizer;
            _config = config ?? new CompressionConfig();
        }

        /// <summary>
        /// 计算压缩后的摘要 Token 总数。
        /// </summary>
        public int TotalCompressedTokens
        {
            get
            {
                lock (_summaryLock)
                    return _compressedSummaries.Sum(s => s.CompressedTokens);
            }
        }

        /// <summary>
        /// 获取所有压缩摘要的合并文本（用于注入到 system 消息）。
        /// </summary>
        public string GetCompressedContextText()
        {
            lock (_summaryLock)
            {
                if (_compressedSummaries.Count == 0)
                    return string.Empty;

                if (_frozenCompressedContextText != null)
                    return _frozenCompressedContextText;

                var sb = new StringBuilder();
                var L = LocalizationService.Instance;
                sb.AppendLine(L["compress.historyHeader"]);
                sb.AppendLine(L["compress.historyIntro"]);
                sb.AppendLine();

                foreach (var summary in _compressedSummaries.OrderBy(s => s.FromTurn))
                {
                    sb.AppendLine(string.Format(L["compress.turnSeparator"], summary.FromTurn, summary.ToTurn));
                    sb.AppendLine(PrepareSummaryForInjection(summary.Summary));
                    sb.AppendLine();
                }

                sb.AppendLine(L["compress.historyFooter"]);
                _frozenCompressedContextText = sb.ToString();
                return _frozenCompressedContextText;
            }
        }

        /// <summary>
        /// 压缩指定轮次的对话消息。
        /// </summary>
        /// <param name="turnsToCompress">待压缩的上下文条目（由 ConversationContextManager 提供）</param>
        /// <param name="fromTurn">起始轮次号</param>
        /// <param name="toTurn">结束轮次号</param>
        /// <param name="prefixMessages">
        /// 从对话开头截取到待压缩区间末尾的完整消息前缀。
        /// 压缩请求会直接在该前缀后追加压缩要求，从而复用 DeepSeek Prefix Cache。
        /// </param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>压缩结果</returns>
        internal async Task<CompressedTurnSummary> CompressTurnsAsync(
            List<ConversationContextManager.ContextEntry> turnsToCompress,
            int fromTurn,
            int toTurn,
            IReadOnlyList<ChatApiMessage>? prefixMessages = null,
            CancellationToken cancellationToken = default,
            int targetSummaryTokens = 0)
        {
            if (turnsToCompress == null || turnsToCompress.Count == 0)
                return new CompressedTurnSummary
                {
                    FromTurn = fromTurn,
                    ToTurn = toTurn,
                    Summary = LocalizationService.Instance["compress.emptyConversation"],
                };

            // 1. 拼接待压缩的对话文本
            string rawText = FormatTurnsForCompression(turnsToCompress);
            int originalTokens = ConversationContextManager.EstimateTokens(rawText);

            // 2. 生成摘要
            string summary;
            if (_summarizer != null)
            {
                int effectiveTargetTokens = ResolveTargetSummaryTokens(originalTokens, targetSummaryTokens);
                string prompt = BuildCompressionPrompt(_config.CompressionPrompt, effectiveTargetTokens);
                if (_compressedSummaries.Count > 0)
                {
                    prompt += "\n\n" + LocalizationService.Instance["system.compressionIncrementalPrompt"];
                }

                // 保持正常对话前缀逐 token 不变，只在末尾追加压缩指令。
                var requestMessages = new List<ChatApiMessage>(
                    prefixMessages ?? Array.Empty<ChatApiMessage>());
                requestMessages.Add(new ChatApiMessage
                {
                    // 正常对话末尾通常是 user；追加 system 指令可保持原 user 前缀逐 token 不变，
                    // 同时避免发送层把两条连续 user 消息合并。
                    Role = "system",
                    Content = prompt,
                });

                try
                {
                    summary = await _summarizer(requestMessages, cancellationToken);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[ContextCompressor] LLM 摘要失败，回退到本地提取: {ex.Message}");
                    summary = string.Empty;
                }

                if (string.IsNullOrWhiteSpace(summary))
                    summary = ExtractLocalSummary(rawText, turnsToCompress);
            }
            else
            {
                // 基于规则的本地提取（无 LLM 调用）
                summary = ExtractLocalSummary(rawText, turnsToCompress);
            }

            // 不启用 response_format；通过固定 Markdown 章节约束模型输出。
            // 结构不完整时直接回退到本地结构化摘要，绝不把自由格式正文注入 system。
            string? structuredSummary = NormalizeStructuredSummary(summary);
            summary = structuredSummary ?? ExtractLocalSummary(rawText, turnsToCompress);

            int compressedTokens = ConversationContextManager.EstimateTokens(summary);

            var result = new CompressedTurnSummary
            {
                Summary = summary,
                FromTurn = fromTurn,
                ToTurn = toTurn,
                OriginalTokens = originalTokens,
                CompressedTokens = compressedTokens,
            };

            lock (_summaryLock)
            {
                _compressedSummaries.Add(result);
                _frozenCompressedContextText = null;
            }

            Logger.Info($"[ContextCompressor] 压缩第 {fromTurn}-{toTurn} 轮: " +
                $"{originalTokens} → {compressedTokens} tokens " +
                $"(压缩率 {result.CompressionRatio:P0})");

            return result;
        }

        /// <summary>
        /// 清空所有压缩摘要。
        /// </summary>
        public void Clear()
        {
            lock (_summaryLock)
            {
                _compressedSummaries.Clear();
                _frozenCompressedContextText = null;
            }
        }

        /// <summary>
        /// 用持久化恢复的摘要替换当前摘要集合。
        /// </summary>
        public void ReplaceSummaries(IEnumerable<CompressedTurnSummary>? summaries)
        {
            lock (_summaryLock)
            {
                _compressedSummaries.Clear();
                _frozenCompressedContextText = null;
                if (summaries == null)
                    return;

                foreach (var summary in summaries.OrderBy(s => s.FromTurn))
                {
                    _compressedSummaries.Add(new CompressedTurnSummary
                    {
                        Summary = summary.Summary,
                        FromTurn = summary.FromTurn,
                        ToTurn = summary.ToTurn,
                        OriginalTokens = summary.OriginalTokens,
                        CompressedTokens = summary.CompressedTokens,
                        CompressedAt = summary.CompressedAt,
                    });
                }
            }
        }

        #region Private Methods

        private static int ResolveTargetSummaryTokens(int originalTokens, int targetSummaryTokens)
        {
            int configuredTarget = targetSummaryTokens > 0
                ? targetSummaryTokens
                : Math.Max(1, originalTokens / 10);

            int sourceBasedTarget = Math.Max(1, originalTokens / 10);
            return Math.Max(1, Math.Min(configuredTarget, sourceBasedTarget));
        }

        private static string BuildCompressionPrompt(string? template, int targetTokens)
        {
            string targetText = targetTokens.ToString("N0", CultureInfo.InvariantCulture);
            string prompt = template ?? string.Empty;
            if (prompt.Contains("{0}"))
                return prompt.Replace("{0}", targetText);

            return prompt.TrimEnd()
                + string.Format(
                    LocalizationService.Instance["compress.targetTokensInstruction"],
                    targetText);
        }

        internal static string[] GetSummarySectionHeadings()
        {
            var L = LocalizationService.Instance;
            return new[]
            {
                "## " + L["compress.section.goals"],
                "## " + L["compress.section.status"],
                "## " + L["compress.section.completed"],
                "## " + L["compress.section.decisions"],
                "## " + L["compress.section.files"],
                "## " + L["compress.section.errors"],
                "## " + L["compress.section.pending"],
                "## " + L["compress.section.context"],
            };
        }

        /// <summary>
        /// 将自由格式模型输出规范化为固定 Markdown 章节。
        /// 不使用 response_format；若章节缺失则返回 null，由本地规则摘要兜底。
        /// </summary>
        internal static string? NormalizeStructuredSummary(string? rawSummary)
        {
            if (string.IsNullOrWhiteSpace(rawSummary))
                return null;

            string text = StripCodeFence(rawSummary.Trim());
            string[] headings = GetSummarySectionHeadings();

            foreach (string heading in headings)
            {
                if (text.IndexOf(heading, StringComparison.Ordinal) < 0)
                    return null;
            }

            var sb = new StringBuilder();
            for (int i = 0; i < headings.Length; i++)
            {
                string heading = headings[i];
                int headingIndex = text.IndexOf(heading, StringComparison.Ordinal);
                int bodyStart = headingIndex + heading.Length;
                int bodyEnd = text.Length;

                foreach (string otherHeading in headings)
                {
                    int otherIndex = text.IndexOf(otherHeading, bodyStart, StringComparison.Ordinal);
                    if (otherIndex >= 0 && otherIndex < bodyEnd)
                        bodyEnd = otherIndex;
                }

                string body = text.Substring(bodyStart, Math.Max(0, bodyEnd - bodyStart)).Trim();
                if (body.Length == 0)
                    body = LocalizationService.Instance["compress.none"];

                if (sb.Length > 0)
                    sb.AppendLine();
                sb.AppendLine(heading);
                sb.AppendLine(body);
            }

            return sb.ToString().TrimEnd();
        }

        private static string PrepareSummaryForInjection(string summary)
        {
            string? structured = NormalizeStructuredSummary(summary);
            if (structured != null)
                return structured;

            string[] headings = GetSummarySectionHeadings();
            var sb = new StringBuilder();
            for (int i = 0; i < headings.Length; i++)
            {
                if (sb.Length > 0)
                    sb.AppendLine();
                sb.AppendLine(headings[i]);
                sb.AppendLine(i == headings.Length - 1
                    ? summary.Trim()
                    : LocalizationService.Instance["compress.none"]);
            }
            return sb.ToString().TrimEnd();
        }

        private static string StripCodeFence(string text)
        {
            if (!text.StartsWith("```", StringComparison.Ordinal))
                return text;

            int firstLineBreak = text.IndexOf('\n');
            int closingFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLineBreak < 0 || closingFence <= firstLineBreak)
                return text;

            return text.Substring(firstLineBreak + 1, closingFence - firstLineBreak - 1).Trim();
        }

        /// <summary>
        /// 将上下文条目格式化为适合压缩的文本。
        /// </summary>
        private static string FormatTurnsForCompression(List<ConversationContextManager.ContextEntry> entries)
        {
            var sb = new StringBuilder();
            foreach (var entry in entries)
            {
                var L = LocalizationService.Instance;
                string roleLabel = entry.Role switch
                {
                    "user" => L["compress.roleUser"],
                    "assistant" => L["compress.roleAssistant"],
                    "tool" => L["compress.roleTool"],
                    _ => entry.Role,
                };

                sb.AppendLine($"[{roleLabel}]");
                if (!string.IsNullOrEmpty(entry.Content))
                {
                    sb.AppendLine(entry.Content);
                }
                if (!string.IsNullOrEmpty(entry.ReasoningContent))
                {
                    // Reasoning can be much larger than the useful conclusion. Keep a
                    // bounded head/tail excerpt so compaction does not create a second
                    // memory spike from the original thinking stream.
                    string boundedReasoning = ReasoningTextPolicy.ClampForCompression(entry.ReasoningContent)
                        ?? string.Empty;
                    sb.AppendLine(string.Format(LocalizationService.Instance["compress.thinkingLabel"], boundedReasoning));
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        /// <summary>
        /// 基于规则的本地摘要提取（无 LLM 调用）。
        /// 提取用户问题、关键文件名、代码片段、错误信息等。
        /// </summary>
        private static string ExtractLocalSummary(string rawText, List<ConversationContextManager.ContextEntry> entries)
        {
            var L = LocalizationService.Instance;
            string[] headings = GetSummarySectionHeadings();
            var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string heading in headings)
                sections[heading] = new List<string>();

            // 提取用户消息的核心内容
            var userMessages = entries
                .Where(e => e.Role == "user" && !string.IsNullOrEmpty(e.Content))
                .ToList();

            if (userMessages.Count > 0)
            {
                foreach (var msg in userMessages.Take(20))
                {
                    string content = msg.Content ?? "";
                    int newlineIdx = content.IndexOf('\n');
                    string firstLine = newlineIdx > 0
                        ? content.Substring(0, newlineIdx).Trim()
                        : content.Trim();
                    if (firstLine.Length > 0)
                        sections[headings[0]].Add($"- {firstLine}");
                }
            }

            sections[headings[1]].Add($"- {L["compress.status.inProgress"]}");

            var lastAssistant = entries.LastOrDefault(e => e.Role == "assistant" && !string.IsNullOrEmpty(e.Content));
            if (lastAssistant != null)
            {
                string content = lastAssistant.Content ?? "";
                if (content.Length > 800)
                    content = content.Substring(content.Length - 800);
                sections[headings[2]].Add(content.Trim());
            }

            // 提取文件路径引用
            var filePattern = new System.Text.RegularExpressions.Regex(
                @"(?:\s*)?([\w\-./\\]+\.(?:cs|py|js|ts|java|cpp|c|h|xml|json|yaml|yml|md|sql|html|css|txt))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var files = new HashSet<string>();
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Content)) continue;
                foreach (System.Text.RegularExpressions.Match m in filePattern.Matches(entry.Content))
                {
                    files.Add(m.Groups[1].Value);
                }
            }
            if (files.Count > 0)
            {
                foreach (var f in files.Take(10))
                    sections[headings[4]].Add($"- {f}");
            }

            // 提取错误信息
            var errorPattern = new System.Text.RegularExpressions.Regex(
                @"(?:error|Error|ERROR|Exception|异常|错误)[:：]\s*(.+?)(?:\n|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var errors = new HashSet<string>();
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Content)) continue;
                foreach (System.Text.RegularExpressions.Match m in errorPattern.Matches(entry.Content))
                {
                    string err = m.Groups[1].Value.Trim();
                    errors.Add(err);
                }
            }
            if (errors.Count > 0)
            {
                foreach (var e in errors.Take(5))
                    sections[headings[5]].Add($"- {e}");
            }

            sections[headings[7]].Add(string.Format(
                L["compress.localContextStats"],
                entries.Count,
                ConversationContextManager.EstimateTokens(rawText)));

            var sb = new StringBuilder();
            foreach (string heading in headings)
            {
                if (sb.Length > 0)
                    sb.AppendLine();
                sb.AppendLine(heading);
                if (sections[heading].Count == 0)
                    sb.AppendLine(L["compress.none"]);
                else
                    sb.AppendLine(string.Join(Environment.NewLine, sections[heading]));
            }

            return sb.ToString().Trim();
        }

        #endregion
    }
}
