using System;
using System.Collections.Generic;

namespace DeepSeek_v4_for_VisualStudio.CodeCompletion
{
    /// <summary>
    /// C/C++ 补全上下文的纯文本扫描器（不依赖任何 COM / 语言服务）。
    ///
    /// ── 为什么需要它 ──
    /// <see cref="InlinePredictionManager"/> 原先用 <c>ITextStructureNavigator.GetSpanOfEnclosing</c>
    /// 逐层向外扩展来定位光标所在的方法/类。C/C++ 的导航器由 VC 语言服务（原生 COM）
    /// 实现，在代码语法不完整、光标位于空行/注释/预处理区、或 IntelliSense 尚未解析完成时
    /// 会直接抛 COMException(E_FAIL)，于是上下文退化为"光标前后各 500 字符"，
    /// 既拿不到函数签名也拿不到完整函数体——这正是日志里
    /// <c>GetSpanOfEnclosing COM 异常 (level=0)</c> 之后"上下文长度=1/6"的原因。
    ///
    /// 本扫描器只读文本，永不抛异常：
    ///   1. 先把注释、字符串、字符字面量、原始字符串中的花括号屏蔽成空格（保留换行）；
    ///   2. 用花括号配对（含未闭合的 <c>{</c>，即"正在写"的函数体）找到光标所在的最内层块；
    ///   3. 前缀起点向外扩展到预算允许的最外层块，再回溯到该块的声明起始处
    ///      （函数签名 / 类头 / <c>namespace</c> 行），从而拿到"签名到函数右括号"的完整代码；
    ///   4. 后缀终点取最内层块的右括号；若过短再向外扩展一层，但不超出字符预算。
    ///
    /// 结果始终满足 <c>0 &lt;= Start &lt;= caret &lt;= End &lt;= text.Length</c>。
    /// </summary>
    internal static class CppBlockContextScanner
    {
        /// <summary>前缀最小字符数：不足时向前补足，避免"上下文长度=1"这种近乎空提示。</summary>
        internal const int DefaultMinPrefixChars = 600;

        /// <summary>前缀最大字符数：超出时从左侧裁剪（保留离光标最近的代码）。</summary>
        internal const int DefaultMaxPrefixChars = 4096;

        /// <summary>
        /// 后缀短于该值时把后缀终点向外扩展一层（例如光标在 if 块内时补到函数右括号），
        /// 也用作后缀为空时的补足量。不会把相邻函数拉进 suffix。
        /// </summary>
        internal const int DefaultMinSuffixChars = 300;

        /// <summary>后缀最大字符数。</summary>
        internal const int DefaultMaxSuffixChars = 2048;

        /// <summary>向外扩展的最大层数（防御异常深的嵌套）。</summary>
        private const int MaxNestingLevels = 8;

        /// <summary>原始字符串分隔符的最大长度（C++ 标准规定不超过 16 个字符）。</summary>
        private const int MaxRawStringDelimiterLength = 16;

        /// <summary>
        /// 计算光标处的补全上下文区间：前缀为 <c>[Start, caret)</c>，后缀为 <c>[caret, End)</c>。
        /// </summary>
        /// <param name="text">文档全文。</param>
        /// <param name="caret">光标绝对偏移（会被夹取到 <c>[0, text.Length]</c>）。</param>
        internal static (int Start, int End) GetContext(
            string text,
            int caret,
            int minPrefixChars = DefaultMinPrefixChars,
            int maxPrefixChars = DefaultMaxPrefixChars,
            int minSuffixChars = DefaultMinSuffixChars,
            int maxSuffixChars = DefaultMaxSuffixChars)
        {
            if (string.IsNullOrEmpty(text))
            {
                return (0, 0);
            }

            caret = Math.Max(0, Math.Min(caret, text.Length));

            string masked = MaskNonCode(text);
            List<(int Open, int Close)> blocks = FindBraceBlocks(masked);

            int start;
            int end;

            (int Open, int Close)? enclosing = FindInnermostEnclosingBlock(blocks, caret);
            if (enclosing.HasValue)
            {
                int prefixOpen = enclosing.Value.Open;
                int suffixOpen = enclosing.Value.Open;
                int suffixClose = enclosing.Value.Close;

                // 前缀向外扩展：函数体里的 if/for 块起点拿不到函数签名，需扩到方法/类级。
                int budget = Math.Max(0, maxPrefixChars) + Math.Max(0, maxSuffixChars);
                for (int level = 1; level < MaxNestingLevels; level++)
                {
                    (int Open, int Close)? outer = FindParentBlock(blocks, prefixOpen, suffixClose);
                    if (!outer.HasValue || outer.Value.Close - outer.Value.Open + 1 > budget)
                    {
                        break;
                    }

                    prefixOpen = outer.Value.Open;
                }

                start = FindDeclarationStart(masked, text, prefixOpen);
                end = Math.Min(text.Length, suffixClose + 1);

                // 后缀过短（光标贴近右括号）时向外补一层，给模型留下"函数之后"的上下文。
                int minSuffix = Math.Max(0, minSuffixChars);
                int maxSuffix = Math.Max(minSuffix, maxSuffixChars);
                for (int level = 1; level < MaxNestingLevels && end - caret < minSuffix; level++)
                {
                    (int Open, int Close)? outerBlock = FindParentBlock(blocks, suffixOpen, suffixClose);
                    if (!outerBlock.HasValue || outerBlock.Value.Close + 1 - caret > maxSuffix)
                    {
                        break;
                    }

                    suffixOpen = outerBlock.Value.Open;
                    suffixClose = outerBlock.Value.Close;
                    end = Math.Min(text.Length, suffixClose + 1);
                }
            }
            else
            {
                // 光标不在任何花括号块内（顶层声明、头文件、空文件）：以"当前声明"为界。
                start = FindDeclarationStart(masked, text, caret);
                end = FindNextDeclarationEnd(masked, blocks, caret, text.Length);
            }

            start = ClampStart(text, start, caret, minPrefixChars, maxPrefixChars);
            end = ClampEnd(text, end, caret, minSuffixChars, maxSuffixChars);

            return (start, end);
        }

        /// <summary>
        /// 把注释、字符串、字符字面量、原始字符串替换为空格（保留 <c>\r</c>/<c>\n</c>），
        /// 使花括号配对只统计真正的代码块。无副作用，不抛异常。
        /// </summary>
        internal static string MaskNonCode(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            char[] buffer = text.ToCharArray();
            int length = text.Length;
            int i = 0;

            while (i < length)
            {
                char c = text[i];

                // 行注释：// ... 行尾
                if (c == '/' && i + 1 < length && text[i + 1] == '/')
                {
                    while (i < length && text[i] != '\n')
                    {
                        buffer[i] = ' ';
                        i++;
                    }

                    continue;
                }

                // 块注释：/* ... */
                if (c == '/' && i + 1 < length && text[i + 1] == '*')
                {
                    buffer[i] = ' ';
                    buffer[i + 1] = ' ';
                    i += 2;

                    while (i < length)
                    {
                        if (text[i] == '*' && i + 1 < length && text[i + 1] == '/')
                        {
                            buffer[i] = ' ';
                            buffer[i + 1] = ' ';
                            i += 2;
                            break;
                        }

                        if (text[i] != '\n' && text[i] != '\r')
                        {
                            buffer[i] = ' ';
                        }

                        i++;
                    }

                    continue;
                }

                // 原始字符串：R"delim( ... )delim"
                if (TrySkipRawString(text, i, out int rawEnd))
                {
                    for (int k = i; k < rawEnd; k++)
                    {
                        if (text[k] != '\n' && text[k] != '\r')
                        {
                            buffer[k] = ' ';
                        }
                    }

                    i = rawEnd;
                    continue;
                }

                // 字符字面量 / 字符串字面量（跳过转义）
                if (c == '"' || c == '\'')
                {
                    if (c == '\'' && IsDigitSeparator(text, i))
                    {
                        // C++14 数字分隔符 1'000'000 不是字符字面量，不能吞掉本行剩余代码
                        i++;
                        continue;
                    }

                    char quote = c;
                    buffer[i] = ' ';
                    i++;

                    while (i < length)
                    {
                        char d = text[i];

                        if (d == '\\' && i + 1 < length)
                        {
                            if (text[i] != '\n' && text[i] != '\r')
                            {
                                buffer[i] = ' ';
                            }

                            if (text[i + 1] != '\n' && text[i + 1] != '\r')
                            {
                                buffer[i + 1] = ' ';
                            }

                            i += 2;
                            continue;
                        }

                        if (d == quote)
                        {
                            buffer[i] = ' ';
                            i++;
                            break;
                        }

                        if (d == '\n')
                        {
                            // 未闭合的字面量（用户正在输入）：止于行尾，避免吞掉后续代码
                            break;
                        }

                        buffer[i] = ' ';
                        i++;
                    }

                    continue;
                }

                i++;
            }

            return new string(buffer);
        }

        /// <summary>用栈配对花括号；未闭合的 <c>{</c> 视为延伸到文末（"正在写"的函数体）。</summary>
        internal static List<(int Open, int Close)> FindBraceBlocks(string masked)
        {
            var blocks = new List<(int Open, int Close)>();
            var stack = new Stack<int>();

            for (int i = 0; i < masked.Length; i++)
            {
                char c = masked[i];
                if (c == '{')
                {
                    stack.Push(i);
                }
                else if (c == '}' && stack.Count > 0)
                {
                    blocks.Add((stack.Pop(), i));
                }
            }

            while (stack.Count > 0)
            {
                blocks.Add((stack.Pop(), masked.Length));
            }

            return blocks;
        }

        /// <summary>光标所在的最内层块（<c>Open &lt; caret &lt;= Close</c>）。</summary>
        private static (int Open, int Close)? FindInnermostEnclosingBlock(
            List<(int Open, int Close)> blocks, int caret)
        {
            (int Open, int Close)? best = null;

            foreach ((int open, int close) in blocks)
            {
                if (open < caret && caret <= close &&
                    (best == null || open > best.Value.Open))
                {
                    best = (open, close);
                }
            }

            return best;
        }

        /// <summary>严格包含指定块的最小外层块。</summary>
        private static (int Open, int Close)? FindParentBlock(
            List<(int Open, int Close)> blocks, int open, int close)
        {
            (int Open, int Close)? best = null;

            foreach ((int candidateOpen, int candidateClose) in blocks)
            {
                if (candidateOpen < open && candidateClose > close &&
                    (best == null || candidateOpen > best.Value.Open))
                {
                    best = (candidateOpen, candidateClose);
                }
            }

            return best;
        }

        /// <summary>
        /// 从块的左花括号回溯到声明起始处：向前找到最近的 <c>;</c>、<c>}</c> 或 <c>{</c>，
        /// 再跳过空白，从而把多行函数签名/类头 <c>template&lt;...&gt;</c> 等一并包含进来。
        /// </summary>
        private static int FindDeclarationStart(string masked, string original, int openPosition)
        {
            int boundary = -1;

            for (int i = openPosition - 1; i >= 0; i--)
            {
                char c = masked[i];
                if (c == ';' || c == '}' || c == '{')
                {
                    boundary = i;
                    break;
                }
            }

            int start = boundary + 1;
            while (start < openPosition && char.IsWhiteSpace(original[start]))
            {
                start++;
            }

            return start < openPosition ? start : openPosition;
        }

        /// <summary>
        /// 光标在块外（顶层）时，取"当前声明"的结束位置：优先包含紧随其后的块体
        /// （正在输入的 <c>void foo()</c> 后跟 <c>{</c>），其次是分号。
        /// </summary>
        private static int FindNextDeclarationEnd(
            string masked, List<(int Open, int Close)> blocks, int caret, int textLength)
        {
            for (int i = caret; i < textLength; i++)
            {
                char c = masked[i];

                if (c == '{')
                {
                    int close = textLength;
                    foreach ((int open, int blockClose) in blocks)
                    {
                        if (open == i)
                        {
                            close = blockClose;
                            break;
                        }
                    }

                    return Math.Min(textLength, close + 1);
                }

                if (c == ';')
                {
                    return i + 1;
                }

                if (c == '}')
                {
                    return i;
                }
            }

            return textLength;
        }

        /// <summary>保证前缀既不短于下限也不长于上限，并按行对齐避免从标识符中间开始。</summary>
        private static int ClampStart(string text, int start, int caret, int minPrefixChars, int maxPrefixChars)
        {
            start = Math.Max(0, Math.Min(start, caret));

            int min = Math.Max(0, minPrefixChars);
            if (caret - start < min)
            {
                start = Math.Max(0, caret - min);
                if (start > 0)
                {
                    start = MoveToLineStart(text, start);
                }
            }

            int max = Math.Max(min, maxPrefixChars);
            if (caret - start > max)
            {
                int candidate = caret - max;
                int nextLine = MoveToNextLineStart(text, candidate);
                start = nextLine <= caret ? nextLine : candidate;
            }

            return Math.Max(0, Math.Min(start, caret));
        }

        /// <summary>
        /// 保证后缀不越界、不超上限；只在后缀为空时补到行尾。
        /// 注意：这里不做"补足最短后缀"，否则会把下一个函数/类体拉进 suffix
        /// （向外扩展只应发生在块结构内，由 <see cref="GetContext"/> 中的外扩循环负责）。
        /// </summary>
        private static int ClampEnd(string text, int end, int caret, int minSuffixChars, int maxSuffixChars)
        {
            end = Math.Max(caret, Math.Min(end, text.Length));

            if (end == caret)
            {
                int nextLine = MoveToNextLineStart(text, caret);
                end = nextLine > caret
                    ? nextLine
                    : Math.Min(text.Length, caret + Math.Max(1, minSuffixChars));
            }

            int max = Math.Max(1, Math.Max(minSuffixChars, maxSuffixChars));
            if (end - caret > max)
            {
                int candidate = caret + max;
                int lineStart = MoveToLineStart(text, candidate);
                end = lineStart >= caret ? lineStart : candidate;
            }

            return Math.Max(caret, Math.Min(end, text.Length));
        }

        /// <summary>向前移动到当前行的行首（返回行首偏移，绝不大于 pos）。</summary>
        private static int MoveToLineStart(string text, int pos)
        {
            int i = Math.Max(0, Math.Min(pos, text.Length));
            while (i > 0 && text[i - 1] != '\n')
            {
                i--;
            }

            return i;
        }

        /// <summary>向后移动到下一行行首（文件末尾时返回 text.Length）。</summary>
        private static int MoveToNextLineStart(string text, int pos)
        {
            int i = Math.Max(0, Math.Min(pos, text.Length));
            while (i < text.Length && text[i] != '\n')
            {
                i++;
            }

            return i < text.Length ? i + 1 : text.Length;
        }

        /// <summary>识别 C++14 数字分隔符（形如 <c>1'000</c>）中的单引号。</summary>
        private static bool IsDigitSeparator(string text, int quoteIndex)
        {
            if (quoteIndex <= 0 || quoteIndex + 1 >= text.Length)
            {
                return false;
            }

            char previous = text[quoteIndex - 1];
            char next = text[quoteIndex + 1];

            return char.IsDigit(previous) && IsHexDigit(next);
        }

        private static bool IsHexDigit(char c)
            => char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        /// <summary>识别并跳过 <c>R"delim(...)delim"</c> 原始字符串；未闭合时吞到文末。</summary>
        private static bool TrySkipRawString(string text, int start, out int end)
        {
            end = start;

            if (start + 1 >= text.Length || text[start] != 'R' || text[start + 1] != '"')
            {
                return false;
            }

            int delimiterStart = start + 2;
            int openParen = -1;

            for (int i = delimiterStart; i < text.Length && i - delimiterStart <= MaxRawStringDelimiterLength; i++)
            {
                char c = text[i];
                if (c == '(')
                {
                    openParen = i;
                    break;
                }

                if (c == ')' || c == '\\' || c == '"' || c == '\n' || c == '\r' || char.IsWhiteSpace(c))
                {
                    return false;
                }
            }

            if (openParen < 0)
            {
                return false;
            }

            string terminator = ")" + text.Substring(delimiterStart, openParen - delimiterStart) + "\"";
            int close = text.IndexOf(terminator, openParen + 1, StringComparison.Ordinal);

            end = close < 0 ? text.Length : close + terminator.Length;
            return true;
        }
    }
}
