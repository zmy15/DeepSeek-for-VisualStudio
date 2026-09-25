using System;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>扩展公开信息。</summary>
    internal static class AboutInfo
    {
        public const string RepositoryUrl = "https://github.com/zmy15/DeepSeek-for-VisualStudio";
        public const string IssuesUrl = RepositoryUrl + "/issues";
        public const string StarUrl = RepositoryUrl;

        public static string Version => DeepSeek_v4_for_VisualStudio.Vsix.Version;
    }
}