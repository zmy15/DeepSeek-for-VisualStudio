using System;
using System.IO;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 解决方案身份判定。
    /// <para>
    /// 存在的原因：Visual Studio 在解决方案文件（.sln/.slnx/.csproj/Directory.Build.props 等）
    /// 被外部修改后会先 <c>OnAfterCloseSolution</c> 再 <c>OnAfterOpenSolution</c>，
    /// 这是一次「同一解决方案重载」而非「切换解决方案」。若把两者混为一谈，
    /// 扩展会在 Agent 正在执行时清空内存中的对话并从磁盘重载，导致界面回滚到任务执行前。
    /// </para>
    /// <para>
    /// 比较前统一做规范化：去空白、去末尾分隔符、转绝对路径，忽略大小写（Windows 语义）。
    /// 任一侧为空都视为「不同」，因为「空」代表当前没有绑定任何解决方案。
    /// </para>
    /// </summary>
    internal static class SolutionIdentity
    {
        /// <summary>
        /// 判断两个解决方案/工作区路径是否指向同一个解决方案。
        /// 任一侧为 null / 空白时返回 false（无解决方案不算「同一个」）。
        /// </summary>
        public static bool IsSame(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 规范化路径：去首尾空白、去末尾目录分隔符，并尽量转成绝对路径。
        /// 转换失败（非法字符、路径过长等）时退回「去空白 + 去末尾分隔符」的结果，不抛异常。
        /// </summary>
        public static string Normalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            string trimmed = path!.Trim();
            try
            {
                string full = Path.GetFullPath(trimmed);
                return TrimSeparators(full);
            }
            catch
            {
                return TrimSeparators(trimmed);
            }
        }

        private static string TrimSeparators(string path)
        {
            if (path.Length <= 1)
                return path;

            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // 盘符根（C:\）去掉分隔符会退化为 "C:"（「当前目录」语义），必须保留原样。
            if (trimmed.Length == 2 && trimmed[1] == ':')
                return path;

            return trimmed;
        }
    }
}
