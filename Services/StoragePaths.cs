using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 工作区/解决方案路径与本地存储路径的统一计算。
    ///
    /// 此前同一套「解决方案路径 → proj_{sha256 前16位}」规则在
    /// <c>ChatPersistenceService</c>、输入历史、Plan 保存/恢复（PlanAgent、PlanIntent、
    /// RetryEdit）里各写了一遍；<c>NormalizeToDirectory</c> 也在 BuildService 与
    /// TestService 各有一份。哈希长度、目录名或字面量任一处漂移都会让数据"看起来丢了"。
    /// </summary>
    internal static class StoragePaths
    {
        /// <summary>解决方案指纹长度（十六进制字符数）。</summary>
        private const int SolutionHashLength = 16;

        private const string UnsavedKey = "_unsaved";

        /// <summary>%LocalAppData%\DeepSeekVS</summary>
        public static string RootDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekVS");

        /// <summary>会话持久化目录。</summary>
        public static string ConversationsDirectory => Path.Combine(RootDirectory, "conversations");

        /// <summary>计划 Markdown 保存目录。</summary>
        public static string PlansDirectory => Path.Combine(RootDirectory, "plans");

        /// <summary>
        /// 解决方案标识：未保存时为 <c>_unsaved</c>，否则为 <c>proj_{sha256前16位}</c>。
        /// </summary>
        public static string ProjectKey(string? solutionPath)
        {
            if (string.IsNullOrWhiteSpace(solutionPath))
                return UnsavedKey;

            using (var sha256 = SHA256.Create())
            {
                var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(solutionPath));
                var hash = BitConverter.ToString(hashBytes).Replace("-", "")
                    .Substring(0, SolutionHashLength);
                return $"proj_{hash}";
            }
        }

        /// <summary>
        /// 按解决方案隔离的 JSON 文件路径（如 <c>conversations/proj_xxx.json</c>）。
        /// </summary>
        public static string ProjectFilePath(string baseDirectory, string? solutionPath)
            => Path.Combine(baseDirectory, ProjectKey(solutionPath) + ".json");

        /// <summary>某个解决方案的计划目录（必要时可交给 Directory.CreateDirectory）。</summary>
        public static string PlanDirectory(string? solutionPath)
            => Path.Combine(PlansDirectory, ProjectKey(solutionPath));

        /// <summary>某个解决方案的 plan.md 路径。</summary>
        public static string PlanFilePath(string? solutionPath)
            => Path.Combine(PlanDirectory(solutionPath), "plan.md");

        /// <summary>
        /// 把「解决方案文件路径 / 目录 / null」归一化为目录；无法判定时返回 null。
        /// </summary>
        public static string? NormalizeToDirectory(string? solutionPath)
        {
            if (string.IsNullOrEmpty(solutionPath))
                return null;

            try
            {
                if (File.Exists(solutionPath))
                    return Path.GetDirectoryName(solutionPath);
                if (Directory.Exists(solutionPath))
                    return solutionPath;
            }
            catch { }

            return null;
        }
    }
}
