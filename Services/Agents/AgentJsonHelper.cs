using System;
using System.Text;
using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// Agent 通用 JSON 处理工具（从 <see cref="BaseAgent"/> 下沉）。
    /// 承载围栏提取、截断修复、括号配平与行数统计等无状态纯函数；
    /// <see cref="BaseAgent"/> 上保留同名转发以维持子类与既有测试的编译契约。
    /// </summary>
    internal static class AgentJsonHelper
    {
        /// <summary>
        /// 从 AI 返回结果中提取 JSON（可能被 markdown 代码块包裹）。
        /// 同时剥离 DeepSeek 可能在 content 中输出的 XML 风格标签。
        /// 当 tools=null 时，DeepSeek 会将工具调用意图以 DSML/function_call 等标签泄露到 content。
        /// </summary>
        /// <param name="text">AI 返回的原始文本。</param>
        /// <returns>提取出的 JSON 文本；输入为空时返回 <c>{}</c>。</returns>
        internal static string ExtractJsonFromMarkdown(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "{}";

            // ── 剥离 XML 风格的标签（DeepSeek 可能将推理/工具调用泄露到 content 字段）──
            // 支持标准 XML <tag> 和 DeepSeek 管道分隔 <|tag|> 两种格式
            string cleaned = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"</?(?:\|?\s*)?(?:thinking|analysis|reasoning|plan|reflection|response|result|output|DSML|function_calls?|tool_calls?)(?:\s*\|?)?[^>]*>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // 额外处理管道分隔格式的残留
            cleaned = System.Text.RegularExpressions.Regex.Replace(
                cleaned,
                @"<\|?\s*(?:invoke|parameter|read_file|file_search|grep_search|list_dir|run_in_terminal|create_file|replace_string_in_file|runSubagent)[^>]*\|?>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

            int jsonStart = cleaned.IndexOf('{');
            int jsonEnd = cleaned.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
                return cleaned.Substring(jsonStart, jsonEnd - jsonStart + 1);

            return cleaned.Trim();
        }

        /// <summary>
        /// 尝试修复因 max_tokens 不足而被截断的 JSON。
        /// 策略：计数字符串外的括号不平衡数，在末尾补充缺失的引号/方括号/花括号。
        /// </summary>
        /// <param name="json">可能截断的 JSON 字符串</param>
        /// <returns>修复后的 JSON 字符串。如果无法安全修复则返回原字符串。</returns>
        internal static string TryRepairTruncatedJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return json;
            if (!json.TrimStart().StartsWith("{")) return json;

            // 快速检查：如果末尾已经是 }，可能不需要修复
            string trimmed = json.TrimEnd();
            if (trimmed.EndsWith("}"))
            {
                // 验证括号平衡
                if (IsJsonBracketBalanced(trimmed))
                    return json;
            }

            bool inString = false;
            bool escaped = false;
            int braceDepth = 0;
            int bracketDepth = 0;

            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\' && inString)
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (inString) continue;

                if (c == '{') braceDepth++;
                else if (c == '}') braceDepth--;
                else if (c == '[') bracketDepth++;
                else if (c == ']') bracketDepth--;
            }

            var sb = new StringBuilder(json);

            // 如果截断在字符串内部（未闭合的引号）
            if (inString)
            {
                // 找到最后一个未转义的引号，在其后添加闭合引号
                sb.Append("\"");
            }

            // 闭合未配对的数组
            while (bracketDepth > 0)
            {
                sb.Append("]");
                bracketDepth--;
            }

            // 闭合未配对的对象
            while (braceDepth > 0)
            {
                sb.Append("}");
                braceDepth--;
            }

            string repaired = sb.ToString();

            // 验证修复结果（快速括号平衡检查）
            if (IsJsonBracketBalanced(repaired) && repaired.TrimEnd().EndsWith("}"))
                return repaired;

            // 修复失败 → 尝试暴力闭合（找到最后一个 " 后闭合）
            int lastQuote = json.LastIndexOf('"');
            if (lastQuote > 0)
            {
                // 从最后一个引号处截断，闭合所有结构
                string truncated = json.Substring(0, lastQuote + 1);
                // 重新计算括号深度
                braceDepth = 0;
                bracketDepth = 0;
                inString = false;
                escaped = false;
                for (int i = 0; i < truncated.Length; i++)
                {
                    char c2 = truncated[i];
                    if (escaped) { escaped = false; continue; }
                    if (c2 == '\\') { escaped = true; continue; }
                    if (c2 == '"') { inString = !inString; continue; }
                    if (inString) continue;
                    if (c2 == '{') braceDepth++;
                    else if (c2 == '}') braceDepth--;
                    else if (c2 == '[') bracketDepth++;
                    else if (c2 == ']') bracketDepth--;
                }
                var sb2 = new StringBuilder(truncated);
                if (inString) sb2.Append("\"");
                while (bracketDepth-- > 0) sb2.Append("]");
                while (braceDepth-- > 0) sb2.Append("}");
                return sb2.ToString();
            }

            return json; // 实在修不了，返回原文
        }

        /// <summary>
        /// 快速检查 JSON 的花括号和方括号是否平衡。
        /// </summary>
        /// <param name="json">待检查的 JSON 文本。</param>
        /// <returns>括号完全配对时返回 <c>true</c>。</returns>
        internal static bool IsJsonBracketBalanced(string json)
        {
            int braceDepth = 0, bracketDepth = 0;
            bool inString = false;
            bool escaped = false;
            foreach (char c in json)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '{') braceDepth++;
                else if (c == '}') braceDepth--;
                else if (c == '[') bracketDepth++;
                else if (c == ']') bracketDepth--;
            }
            return braceDepth == 0 && bracketDepth == 0;
        }

        /// <summary>
        /// 计算文本行数。
        /// </summary>
        /// <param name="text">待统计的文本。</param>
        /// <returns>行数；空文本返回 0。</returns>
        internal static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int count = 1;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') count++;
            return count;
        }

        /// <summary>
        /// 使用 CodeDiffService 精确计算新增行数和删除行数。
        /// 替代之前仅用净行数差值的错误算法。
        /// </summary>
        /// <param name="oldText">修改前文本。</param>
        /// <param name="newText">修改后文本。</param>
        /// <param name="added">输出：新增行数。</param>
        /// <param name="removed">输出：删除行数。</param>
        internal static void CountDiffLines(string oldText, string newText, out int added, out int removed)
        {
            added = 0;
            removed = 0;
            if (string.IsNullOrEmpty(oldText) && string.IsNullOrEmpty(newText))
                return;

            try
            {
                var diff = CodeDiffService.ComputeDiff(oldText ?? string.Empty, newText ?? string.Empty);
                foreach (var line in diff)
                {
                    if (line.Type == DiffLineType.Added)
                        added++;
                    else if (line.Type == DiffLineType.Deleted)
                        removed++;
                }
            }
            catch
            {
                // 回退到简单行数比较
                int oldLines = CountLines(oldText ?? string.Empty);
                int newLines = CountLines(newText ?? string.Empty);
                added = Math.Max(0, newLines - oldLines);
                removed = Math.Max(0, oldLines - newLines);
            }
        }
    }
}
