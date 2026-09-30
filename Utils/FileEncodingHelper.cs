using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Utils
{
    /// <summary>
    /// 文件编码决策辅助类：统一「读写项目文件」的编码事实源。
    /// 读写两侧共用同一套判定，避免「读出来的内容」与「写回去的编码」不一致：
    /// 1) 已存在文件 → 依据 BOM、严格 UTF-8 校验、无 BOM UTF-16 启发式判定其编码，
    ///    无法按 UTF-8 解码时回退系统 ANSI 代码页（中文 Windows = GBK/936，与 VS 编辑器默认行为一致）；
    /// 2) 新建文件 → 采样项目内同类文件的编码惯例，按多数派写入；
    /// 3) 无法判定（无样本/探测失败）→ 兜底 UTF-8 无 BOM。
    ///
    /// 重要：编辑类工具读取待编辑文件时必须使用本类的 <see cref="ReadAllText"/>，
    /// 否则可能出现「按 UTF-8 误读成乱码 → 按原编码写回」造成的不可逆内容损坏。
    /// </summary>
    public static class FileEncodingHelper
    {
        /// <summary>UTF-8 带 BOM 编码实例（preamble = EF BB BF）。</summary>
        private static readonly Encoding Utf8WithBom = new UTF8Encoding(true);

        /// <summary>UTF-8 无 BOM 编码实例（兜底默认）。</summary>
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>
        /// 严格 UTF-8 编码实例：遇到非法字节抛 <see cref="DecoderFallbackException"/>。
        /// 注意 <see cref="Encoding.UTF8"/> 默认是「替换回退」（坏字节 → U+FFFD，不抛异常），
        /// 因此编码判定必须显式使用本实例，否则永远无法发现非法 UTF-8 字节。
        /// </summary>
        private static readonly Encoding Utf8Strict = new UTF8Encoding(false, throwOnInvalidBytes: true);

        /// <summary>UTF-16 小端无 BOM 编码实例（无 BOM 的 UTF-16 文件写回时不应凭空添加 BOM）。</summary>
        private static readonly Encoding Utf16LeNoBom = new UnicodeEncoding(false, false);

        /// <summary>UTF-16 大端无 BOM 编码实例。</summary>
        private static readonly Encoding Utf16BeNoBom = new UnicodeEncoding(true, false);

        /// <summary>
        /// 无法按 UTF-8 解码时的回退编码：系统 ANSI 代码页（中文 Windows = GBK/936）。
        /// 与 VS 编辑器「无法按 UTF-8 解码时使用系统区域设置编码」的默认行为保持一致。
        /// </summary>
        private static readonly Encoding FallbackEncoding = Encoding.Default;

        /// <summary>判定编码时读取的文件头样本大小（字节）。</summary>
        private const int HeadSampleBytes = 8 * 1024;

        /// <summary>UTF-16 小端带 BOM 编码实例。</summary>
        private static readonly Encoding Utf16Le = new UnicodeEncoding(false, true);

        /// <summary>UTF-16 大端带 BOM 编码实例。</summary>
        private static readonly Encoding Utf16Be = new UnicodeEncoding(true, true);

        /// <summary>UTF-32 小端带 BOM 编码实例。</summary>
        private static readonly Encoding Utf32Le = new UTF32Encoding(false, true);

        /// <summary>UTF-32 大端带 BOM 编码实例。</summary>
        private static readonly Encoding Utf32Be = new UTF32Encoding(true, true);

        /// <summary>项目编码惯例探测结果的进程内缓存，键为「项目根路径|扩展名」（忽略大小写）。</summary>
        private static readonly ConcurrentDictionary<string, Encoding> ProjectConventionCache =
            new ConcurrentDictionary<string, Encoding>(StringComparer.OrdinalIgnoreCase);

        /// <summary>项目惯例采样的最大文件数（防止大仓库扫描过慢）。</summary>
        private const int MaxConventionSamples = 30;

        /// <summary>项目惯例采样时最多遍历的目录数（防御性上限）。</summary>
        private const int MaxConventionDirectories = 200;

        /// <summary>向上查找项目标志文件的最大层级数。</summary>
        private const int MaxProjectRootSearchDepth = 12;

        /// <summary>
        /// 按「已存在文件保持原编码 → 新建文件采用项目惯例 → UTF-8 无 BOM 兜底」策略写入文本文件。
        /// </summary>
        /// <param name="filePath">目标文件路径。</param>
        /// <param name="content">要写入的文本内容。</param>
        /// <param name="workspaceRoot">工作区/项目根目录（可为 null；为 null 时尝试从文件路径逐级向上自动推断项目根）。</param>
        /// <exception cref="IOException">写入失败（如文件被独占锁定）时由底层 IO 抛出。</exception>
        public static void WriteAllText(string filePath, string content, string? workspaceRoot)
        {
            File.WriteAllText(filePath, content, ResolveWriteEncoding(filePath, workspaceRoot));
        }

        #region 读取

        /// <summary>
        /// 按探测到的编码读取文本文件。所有「读取用户项目文件」的场景都应走本方法，
        /// 以保证与 <see cref="WriteAllText"/> 的编码判定完全一致（读什么编码，就写什么编码）。
        ///
        /// 快路径：单遍严格 UTF-8 解码且结果不含 NUL（绝大多数文件命中，无额外 I/O 开销）；
        /// 慢路径：非法 UTF-8 或解码结果含 NUL → 无 BOM UTF-16 启发式 → 系统 ANSI 代码页回退。
        /// （无 BOM 的 UTF-16 字节本身是合法 UTF-8——NUL 是合法字节——因此不能只靠「UTF-8 解码失败」发现它。）
        /// </summary>
        /// <param name="filePath">目标文件路径。</param>
        /// <returns>文件文本内容（BOM 已被剥离）。</returns>
        /// <exception cref="IOException">文件不存在或无法读取时由底层 IO 抛出（与 File.ReadAllText 一致）。</exception>
        public static string ReadAllText(string filePath)
        {
            string? utf8Content = TryReadAllTextAsStrictUtf8(filePath);

            // 含 NUL 的「合法 UTF-8」极可能是被误读的无 BOM UTF-16，需继续走启发式判定
            if (utf8Content != null && utf8Content.IndexOf('\0') < 0)
                return utf8Content;

            byte[] head = new byte[HeadSampleBytes];
            int read = ReadHead(filePath, head);
            bool hasBom = DetectBomKind(head, read) != EncodingKind.Utf8NoBom;
            if (!hasBom && TryDetectBomlessUtf16(head, read, out Encoding bomlessUtf16))
                return ReadAllTextWith(filePath, bomlessUtf16);

            // 含 NUL 但不像 UTF-16 → 维持 UTF-8 结果（无 BOM 探测空间时的原有行为）
            if (utf8Content != null)
                return utf8Content;

            return ReadAllTextWith(filePath, FallbackEncoding);
        }

        /// <summary>
        /// 以指定编码读取整个文件。
        /// </summary>
        /// <param name="filePath">目标文件路径。</param>
        /// <param name="encoding">使用的编码。</param>
        /// <returns>文件文本内容。</returns>
        private static string ReadAllTextWith(string filePath, Encoding encoding)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// 按探测到的编码读取文本文件并按行切分，语义与 <see cref="File.ReadAllLines(string)"/> 一致
        /// （识别 CRLF/LF/CR，且行尾终止符不产生多余的空行项）。
        /// </summary>
        /// <param name="filePath">目标文件路径。</param>
        /// <returns>文件各行内容。</returns>
        public static string[] ReadAllLines(string filePath)
        {
            string text = ReadAllText(filePath);
            if (text.Length == 0)
                return Array.Empty<string>();

            string[] lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

            // 文件以换行结束时不额外产生一个空行项（对齐 File.ReadAllLines 语义）
            char last = text[text.Length - 1];
            if ((last == '\n' || last == '\r') && lines.Length > 0 && lines[lines.Length - 1].Length == 0)
            {
                var trimmed = new string[lines.Length - 1];
                Array.Copy(lines, trimmed, trimmed.Length);
                return trimmed;
            }

            return lines;
        }

        /// <summary>
        /// 探测已存在文件应使用的编码（读写共用）。
        /// 判定顺序：BOM（UTF-32 → UTF-8 → UTF-16）→ 无 BOM UTF-16 启发式 → 全文件严格 UTF-8 校验 → 系统 ANSI 回退。
        /// </summary>
        /// <param name="filePath">目标文件路径。</param>
        /// <returns>该文件当前的编码实例。</returns>
        /// <exception cref="IOException">文件不存在或无法读取时由底层 IO 抛出。</exception>
        public static Encoding DetectFileEncoding(string filePath)
        {
            byte[] head = new byte[HeadSampleBytes];
            int read = ReadHead(filePath, head);

            EncodingKind bomKind = DetectBomKind(head, read);
            if (bomKind != EncodingKind.Utf8NoBom)
                return EncodingFromKind(bomKind);

            // 无 BOM 的 UTF-16 同样能通过 UTF-8 校验（NUL 是合法 UTF-8 字节），必须先排除
            if (TryDetectBomlessUtf16(head, read, out Encoding bomlessUtf16))
                return bomlessUtf16;

            return IsValidUtf8(filePath) ? Utf8NoBom : FallbackEncoding;
        }

        /// <summary>
        /// 单遍尝试按严格 UTF-8 解码整个文件（BOM 由 StreamReader 自动识别并切换编码）。
        /// </summary>
        /// <param name="filePath">目标文件路径。</param>
        /// <returns>解码成功时返回文本；遇到非法 UTF-8 字节时返回 null（交由回退路径处理）。</returns>
        private static string? TryReadAllTextAsStrictUtf8(string filePath)
        {
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Utf8Strict, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }

        #endregion

        /// <summary>
        /// 解析写入指定文件时应使用的编码。
        /// </summary>
        /// <param name="filePath">目标文件路径。</param>
        /// <param name="workspaceRoot">工作区/项目根目录（可为 null）。</param>
        /// <returns>写入时应使用的编码实例；无法探测时返回 UTF-8 无 BOM。</returns>
        public static Encoding ResolveWriteEncoding(string filePath, string? workspaceRoot)
        {
            try
            {
                // 1) 已存在文件 → 保持其原有编码
                if (File.Exists(filePath))
                    return DetectExistingFileEncoding(filePath);

                // 2) 新建文件 → 探测项目内同类文件的编码惯例
                string? root = !string.IsNullOrEmpty(workspaceRoot)
                    ? workspaceRoot
                    : TryFindProjectRoot(filePath);
                if (!string.IsNullOrEmpty(root))
                {
                    Encoding? convention = DetectProjectConvention(filePath, root!);
                    if (convention != null)
                        return convention;
                }
            }
            catch
            {
                // 探测过程中的任何异常都不阻断写入流程：落入兜底编码
            }

            // 3) 兜底 UTF-8 无 BOM
            return Utf8NoBom;
        }

        /// <summary>
        /// 检测已存在文件的编码，等价于 <see cref="DetectFileEncoding"/>（保留原公开 API）。
        /// </summary>
        /// <param name="filePath">已存在的文件路径。</param>
        /// <returns>与文件当前编码一致的编码实例。</returns>
        /// <exception cref="IOException">文件不存在或无法读取时由底层 IO 抛出。</exception>
        public static Encoding DetectExistingFileEncoding(string filePath)
        {
            return DetectFileEncoding(filePath);
        }

        /// <summary>
        /// 读取文件头样本（最多 <see cref="HeadSampleBytes"/> 字节）。
        /// </summary>
        /// <param name="filePath">目标文件路径。</param>
        /// <param name="buffer">接收样本的缓冲区。</param>
        /// <returns>实际读入的字节数。</returns>
        private static int ReadHead(string filePath, byte[] buffer)
        {
            int total = 0;
            // FileShare.ReadWrite：允许读取被编辑器/索引器持有句柄的文件
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int read;
                while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
                    total += read;
            }

            return total;
        }

        /// <summary>
        /// 依据文件头 BOM 判定编码类别。
        /// 注意：UTF-32 LE 的 BOM（FF FE 00 00）以 UTF-16 LE 的 BOM（FF FE）为前缀，必须先判 4 字节再判 2 字节。
        /// 无 BOM 时统一返回 <see cref="EncodingKind.Utf8NoBom"/> 作为「需要进一步判定」的哨兵值。
        /// </summary>
        /// <param name="head">文件头字节。</param>
        /// <param name="read">文件头实际字节数。</param>
        /// <returns>探测出的编码类别。</returns>
        private static EncodingKind DetectBomKind(byte[] head, int read)
        {
            if (read >= 4 && head[0] == 0xFF && head[1] == 0xFE && head[2] == 0x00 && head[3] == 0x00)
                return EncodingKind.Utf32Le;
            if (read >= 4 && head[0] == 0x00 && head[1] == 0x00 && head[2] == 0xFE && head[3] == 0xFF)
                return EncodingKind.Utf32Be;
            if (read >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
                return EncodingKind.Utf8Bom;
            if (read >= 2 && head[0] == 0xFF && head[1] == 0xFE)
                return EncodingKind.Utf16Le;
            if (read >= 2 && head[0] == 0xFE && head[1] == 0xFF)
                return EncodingKind.Utf16Be;

            return EncodingKind.Utf8NoBom;
        }

        /// <summary>
        /// 「无 BOM 的 UTF-16」启发式判定：头部样本零字节占比 ≥ 25%，
        /// 且零字节几乎全部落在同一奇偶位（小端 → 奇数位为 0；大端 → 偶数位为 0）。
        ///
        /// 之所以需要该启发式：ASCII 文本的 UTF-16 字节同样能通过 UTF-8 校验（NUL 是合法 UTF-8 字节），
        /// 不能依赖「UTF-8 校验失败」来发现无 BOM 的 UTF-16 文件。
        /// </summary>
        /// <param name="head">文件头样本。</param>
        /// <param name="read">样本字节数。</param>
        /// <param name="encoding">判定命中时的编码实例。</param>
        /// <returns>判定为无 BOM 的 UTF-16 时返回 true。</returns>
        private static bool TryDetectBomlessUtf16(byte[] head, int read, out Encoding encoding)
        {
            encoding = Utf8NoBom;
            if (read < 16)
                return false;

            int nulCount = 0;
            int nulOnEvenIndex = 0;
            int nulOnOddIndex = 0;
            for (int i = 0; i < read; i++)
            {
                if (head[i] != 0)
                    continue;

                nulCount++;
                if ((i & 1) == 0)
                    nulOnEvenIndex++;
                else
                    nulOnOddIndex++;
            }

            // 零字节太稀疏 → 不是 UTF-16 文本
            if (nulCount * 4 < read)
                return false;

            if (nulOnOddIndex >= nulCount * 9 / 10)
            {
                encoding = Utf16LeNoBom;
                return true;
            }

            if (nulOnEvenIndex >= nulCount * 9 / 10)
            {
                encoding = Utf16BeNoBom;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 判定头部样本是否整体是合法 UTF-8。
        /// 采样时以 flush:false 解码：样本末尾可能正好截断一个多字节序列，这种「不完整」不算非法。
        /// </summary>
        /// <param name="head">文件头样本。</param>
        /// <param name="read">样本字节数。</param>
        /// <returns>样本合法时返回 true。</returns>
        private static bool IsSampleValidUtf8(byte[] head, int read)
        {
            if (read <= 0)
                return true;

            try
            {
                Utf8Strict.GetDecoder().GetCharCount(head, 0, read, flush: false);
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        /// <summary>
        /// 全文件严格 UTF-8 校验（只校验不产出文本，逐块读取以避免整文件驻留内存）。
        /// </summary>
        /// <param name="filePath">目标文件路径。</param>
        /// <returns>整个文件都是合法 UTF-8 时返回 true。</returns>
        private static bool IsValidUtf8(string filePath)
        {
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Utf8Strict, detectEncodingFromByteOrderMarks: true);
                char[] sink = new char[8192];
                while (reader.Read(sink, 0, sink.Length) > 0)
                {
                    // 只为触发解码校验，内容丢弃
                }

                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        /// <summary>
        /// 读取文件头样本判定编码类别（项目编码惯例采样用）。
        /// 无 BOM 时：样本能通过 UTF-8 校验视为 UTF-8，否则视为系统 ANSI（GBK 等）。
        /// </summary>
        /// <remarks>
        /// 采样只看文件头样本（<see cref="HeadSampleBytes"/> 字节）而非全文件，避免扫描大仓库过慢；
        /// 中文注释通常出现在文件头部，判定准确度足够。
        /// </remarks>
        /// <param name="filePath">目标文件路径。</param>
        /// <returns>探测出的编码类别。</returns>
        private static EncodingKind DetectEncodingKind(string filePath)
        {
            byte[] head = new byte[HeadSampleBytes];
            int read = ReadHead(filePath, head);

            EncodingKind bomKind = DetectBomKind(head, read);
            if (bomKind != EncodingKind.Utf8NoBom)
                return bomKind;

            return IsSampleValidUtf8(head, read) ? EncodingKind.Utf8NoBom : EncodingKind.Ansi;
        }

        /// <summary>
        /// 将编码类别映射为编码实例。
        /// </summary>
        /// <param name="kind">编码类别。</param>
        /// <returns>对应的编码实例。</returns>
        private static Encoding EncodingFromKind(EncodingKind kind)
        {
            switch (kind)
            {
                case EncodingKind.Utf8Bom: return Utf8WithBom;
                case EncodingKind.Utf16Le: return Utf16Le;
                case EncodingKind.Utf16Be: return Utf16Be;
                case EncodingKind.Utf32Le: return Utf32Le;
                case EncodingKind.Utf32Be: return Utf32Be;
                case EncodingKind.Ansi: return FallbackEncoding;
                default: return Utf8NoBom;
            }
        }

        /// <summary>
        /// 采样项目内文件的编码惯例，返回多数派编码；无有效样本时返回 null。
        /// 优先按同扩展名采样（同类文件通常共享编码惯例），无样本时回退到全项目任意文件。
        /// 结果按「项目根|扩展名」缓存，避免频繁新建文件时重复扫描项目目录。
        /// </summary>
        /// <param name="filePath">待新建文件路径（用于提取扩展名）。</param>
        /// <param name="projectRoot">项目根目录。</param>
        /// <returns>项目惯例编码；无样本或无法判定时返回 null。</returns>
        private static Encoding? DetectProjectConvention(string filePath, string projectRoot)
        {
            string extension = Path.GetExtension(filePath);
            string cacheKey = projectRoot + "|" + (extension ?? string.Empty).ToLowerInvariant();
            if (ProjectConventionCache.TryGetValue(cacheKey, out var cached))
                return cached;

            Dictionary<EncodingKind, int> counts = SampleEncodings(
                projectRoot, string.IsNullOrEmpty(extension) ? null : extension);
            if (counts.Count == 0 && !string.IsNullOrEmpty(extension))
                counts = SampleEncodings(projectRoot, null);

            if (counts.Count == 0)
                return null;

            EncodingKind winner = counts.OrderByDescending(pair => pair.Value).First().Key;
            Encoding encoding = EncodingFromKind(winner);
            ProjectConventionCache[cacheKey] = encoding;
            return encoding;
        }

        /// <summary>
        /// 广度优先采样指定根目录下文件的编码类别计数，限制样本数与目录数避免大仓库全量扫描。
        /// </summary>
        /// <param name="projectRoot">采样根目录。</param>
        /// <param name="extension">扩展名过滤（含点号，如 ".cs"）；为 null 时采样任意文件。</param>
        /// <returns>编码类别 → 样本数 的计数字典。</returns>
        private static Dictionary<EncodingKind, int> SampleEncodings(
            string projectRoot, string? extension)
        {
            var counts = new Dictionary<EncodingKind, int>();
            int sampledFiles = 0;
            int visitedDirectories = 0;

            var pending = new Queue<string>();
            pending.Enqueue(projectRoot);

            while (pending.Count > 0 &&
                   sampledFiles < MaxConventionSamples &&
                   visitedDirectories < MaxConventionDirectories)
            {
                string directory = pending.Dequeue();
                visitedDirectories++;

                try
                {
                    foreach (string subDir in Directory.GetDirectories(directory))
                    {
                        if (!IsSkippedDirectory(Path.GetFileName(subDir)))
                            pending.Enqueue(subDir);
                    }

                    string searchPattern = extension == null ? "*" : "*" + extension;
                    foreach (string candidate in Directory.GetFiles(directory, searchPattern))
                    {
                        if (sampledFiles >= MaxConventionSamples)
                            break;

                        // Windows 通配符可能因 8.3 短名匹配到其它扩展名，需精确复核
                        if (extension != null &&
                            !string.Equals(Path.GetExtension(candidate), extension, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        try
                        {
                            EncodingKind kind = DetectEncodingKind(candidate);
                            counts[kind] = counts.TryGetValue(kind, out int existing) ? existing + 1 : 1;
                            sampledFiles++;
                        }
                        catch
                        {
                            // 单个样本不可读 → 跳过，不影响其余采样
                        }
                    }
                }
                catch
                {
                    // 目录不可读（权限等）→ 跳过
                }
            }

            return counts;
        }

        /// <summary>
        /// 从目标文件所在目录向上逐级查找项目标志文件（.sln/.slnx/.csproj 等），
        /// 用于路径未携带显式项目根时仍能按项目惯例探测新建文件编码。
        /// </summary>
        /// <param name="filePath">目标文件路径。</param>
        /// <returns>项目根目录全路径；未找到时返回 null。</returns>
        private static string? TryFindProjectRoot(string filePath)
        {
            try
            {
                DirectoryInfo? dir = new FileInfo(filePath).Directory;
                int depth = 0;
                while (dir != null && depth < MaxProjectRootSearchDepth)
                {
                    if (ContainsProjectMarker(dir))
                        return dir.FullName;
                    dir = dir.Parent;
                    depth++;
                }
            }
            catch
            {
                // 路径异常 → 视为无项目根
            }

            return null;
        }

        /// <summary>
        /// 判断目录中是否包含项目/解决方案标志文件。
        /// </summary>
        /// <param name="dir">待检查目录。</param>
        /// <returns>包含任一标志文件时返回 true。</returns>
        private static bool ContainsProjectMarker(DirectoryInfo dir)
        {
            string[] patterns = { "*.sln", "*.slnx", "*.csproj", "*.vbproj", "*.fsproj", "*.vcxproj" };
            foreach (string pattern in patterns)
            {
                try
                {
                    if (dir.EnumerateFiles(pattern).Any())
                        return true;
                }
                catch
                {
                    // 单模式枚举失败 → 尝试下一个
                }
            }

            return false;
        }

        /// <summary>
        /// 项目惯例采样时需要跳过的目录名（构建产物/版本控制/依赖缓存）。
        /// </summary>
        /// <param name="name">目录名（不含路径）。</param>
        /// <returns>应跳过时返回 true。</returns>
        private static bool IsSkippedDirectory(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "bin":
                case "obj":
                case ".git":
                case ".vs":
                case ".svn":
                case ".idea":
                case "node_modules":
                case "packages":
                case "testresults":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>文件头 BOM 判定的编码类别。</summary>
        private enum EncodingKind
        {
            Utf8NoBom,
            Utf8Bom,
            Utf16Le,
            Utf16Be,
            Utf32Le,
            Utf32Be,

            /// <summary>无 BOM 且无法按 UTF-8 解码 → 系统 ANSI 代码页（中文 Windows = GBK/936）。</summary>
            Ansi,
        }
    }
}
