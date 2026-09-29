using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Utils
{
    /// <summary>
    /// 文件写入编码决策辅助类。
    /// 统一「编辑文件的工具」的写入编码策略，避免写入时无意改变文件原有编码或引入/丢失 BOM：
    /// 1) 目标文件已存在 → 保持其原有编码（依据文件头 BOM 判定，无 BOM 视为 UTF-8 无 BOM）；
    /// 2) 新建文件 → 采样项目内同类文件的编码惯例，按多数派写入；
    /// 3) 无法判定（无样本/探测失败）→ 兜底 UTF-8 无 BOM。
    /// </summary>
    public static class FileEncodingHelper
    {
        /// <summary>UTF-8 带 BOM 编码实例（preamble = EF BB BF）。</summary>
        private static readonly Encoding Utf8WithBom = new UTF8Encoding(true);

        /// <summary>UTF-8 无 BOM 编码实例（兜底默认）。</summary>
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

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
        /// 检测已存在文件的编码：依据文件头 BOM 字节判定；未识别到 BOM 时视为 UTF-8 无 BOM。
        /// </summary>
        /// <param name="filePath">已存在的文件路径。</param>
        /// <returns>与文件当前编码一致的编码实例。</returns>
        /// <exception cref="IOException">文件不存在或无法读取时由底层 IO 抛出。</exception>
        public static Encoding DetectExistingFileEncoding(string filePath)
        {
            return EncodingFromKind(DetectEncodingKind(filePath));
        }

        /// <summary>
        /// 读取文件头 4 字节判定编码类别。
        /// 注意：UTF-32 LE 的 BOM（FF FE 00 00）以 UTF-16 LE 的 BOM（FF FE）为前缀，必须先判 4 字节再判 2 字节。
        /// </summary>
        /// <param name="filePath">目标文件路径。</param>
        /// <returns>探测出的编码类别。</returns>
        private static EncodingKind DetectEncodingKind(string filePath)
        {
            byte[] head = new byte[4];
            int read;
            // FileShare.ReadWrite：允许读取被编辑器/索引器持有句柄的文件
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                read = stream.Read(head, 0, head.Length);
            }

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
        }
    }
}
