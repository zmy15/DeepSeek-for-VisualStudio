using System;
using System.Collections.Generic;

namespace DeepSeek_v4_for_VisualStudio.Utils
{
    /// <summary>
    /// 提及 token 的类型：区分 @ Agent 路由与 / 技能命令。
    /// </summary>
    public enum MentionTokenKind
    {
        /// <summary>@ 开头的 Agent 路由 token。</summary>
        Agent,

        /// <summary>/ 开头的技能命令 token。</summary>
        Skill
    }

    /// <summary>
    /// 提及 token 的位置描述（不可变值类型），供调用方按区间切分原文。
    /// </summary>
    public readonly struct MentionToken
    {
        /// <summary>
        /// 构造一个提及 token 描述。
        /// </summary>
        /// <param name="start">token 在原文中的起始索引（含），必须非负。</param>
        /// <param name="length">token 的长度，必须大于 0。</param>
        /// <param name="kind">token 类型（Agent 或 Skill）。</param>
        /// <exception cref="ArgumentOutOfRangeException">当 <paramref name="start"/> 为负或 <paramref name="length"/> 非正时抛出。</exception>
        public MentionToken(int start, int length, MentionTokenKind kind)
        {
            if (start < 0)
                throw new ArgumentOutOfRangeException(nameof(start));
            if (length <= 0)
                throw new ArgumentOutOfRangeException(nameof(length));

            Start = start;
            Length = length;
            Kind = kind;
        }

        /// <summary>token 在原文中的起始索引（含）。</summary>
        public int Start { get; }

        /// <summary>token 的长度（不含触发字符之外的空白）。</summary>
        public int Length { get; }

        /// <summary>token 类型（Agent 或 Skill）。</summary>
        public MentionTokenKind Kind { get; }

        /// <summary>token 结束索引（不含），恒等于 Start + Length。</summary>
        public int End => Start + Length;
    }

    /// <summary>
    /// 提及分词器：把文本切成「以空白分隔、首字符为 @ 或 / 且长度大于 1」的 token。
    /// 纯函数实现，不依赖任何 UI 类型，便于单测与输入框高亮、聊天 HTML 两侧复用。
    /// </summary>
    public static class MentionTokenizer
    {
        /// <summary>
        /// 扫描全文，返回所有需要着色的提及 token，按 Start 升序排列。
        /// 代码围栏（```）内的所有内容一律跳过，避免误着色代码示例。
        /// </summary>
        /// <param name="text">待扫描的原始文本，可为 null。</param>
        /// <returns>按 Start 升序排列的 token 列表；无命中时返回空列表（非 null）。</returns>
        public static List<MentionToken> FindTokens(string text)
        {
            var tokens = new List<MentionToken>();
            if (string.IsNullOrEmpty(text))
                return tokens;

            // 围栏状态跨行保持：进入围栏后整块跳过，直到遇到下一个围栏行
            var inFence = false;
            var lineStart = 0;

            while (lineStart <= text.Length)
            {
                // 行结束位置（不含换行符），避免 Substring 时把换行卷进 token
                var lineEnd = lineStart;
                while (lineEnd < text.Length && text[lineEnd] != '\n' && text[lineEnd] != '\r')
                    lineEnd++;

                var isFenceLine = IsFenceLine(text, lineStart, lineEnd);
                if (isFenceLine)
                {
                    // 围栏行本身与围栏内内容都不参与着色
                    inFence = !inFence;
                }
                else if (!inFence)
                {
                    ScanLine(text, lineStart, lineEnd, tokens);
                }

                if (lineEnd >= text.Length)
                    break;

                // 跳过换行符，\r\n 视为单个换行以免空行导致多算一行
                lineStart = lineEnd + 1;
                if (text[lineEnd] == '\r' && lineStart < text.Length && text[lineStart] == '\n')
                    lineStart++;
            }

            return tokens;
        }

        /// <summary>
        /// 判断某一行的内容是否为 Markdown 代码围栏（允许行首缩进）。
        /// </summary>
        /// <param name="text">完整原文。</param>
        /// <param name="lineStart">当前行起始索引（含）。</param>
        /// <param name="lineEnd">当前行结束索引（不含）。</param>
        /// <returns>该行去除前导空白后以三个反引号开头时返回 true。</returns>
        private static bool IsFenceLine(string text, int lineStart, int lineEnd)
        {
            var i = lineStart;
            while (i < lineEnd && (text[i] == ' ' || text[i] == '\t'))
                i++;

            // 至少需要三个反引号，故要求 i + 2 仍在行内
            return i + 2 < lineEnd && text[i] == '`' && text[i + 1] == '`' && text[i + 2] == '`';
        }

        /// <summary>
        /// 扫描单行内以空白分隔的 token，把以 @ 或 / 开头且长度大于 1 的片段收集为提及 token。
        /// </summary>
        /// <param name="text">完整原文。</param>
        /// <param name="lineStart">当前行起始索引（含）。</param>
        /// <param name="lineEnd">当前行结束索引（不含）。</param>
        /// <param name="tokens">收集结果的列表，按扫描顺序追加。</param>
        private static void ScanLine(string text, int lineStart, int lineEnd, List<MentionToken> tokens)
        {
            var i = lineStart;
            while (i < lineEnd)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    i++;
                    continue;
                }

                var tokenStart = i;
                while (i < lineEnd && !char.IsWhiteSpace(text[i]))
                    i++;

                // 只有触发字符本身（如孤立的 "@" 或 "/"）不算提及，长度必须大于 1
                if (i - tokenStart <= 1)
                    continue;

                // 以 token 首字符判定类型：这样 a@b.com、https://host/path 等夹带符号的
                // 普通词不会被误判为提及
                var first = text[tokenStart];
                if (first == '@')
                    tokens.Add(new MentionToken(tokenStart, i - tokenStart, MentionTokenKind.Agent));
                else if (first == '/')
                    tokens.Add(new MentionToken(tokenStart, i - tokenStart, MentionTokenKind.Skill));
            }
        }
    }
}
