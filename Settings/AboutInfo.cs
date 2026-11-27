using System;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>扩展公开信息。</summary>
    internal static class AboutInfo
    {
        public const string RepositoryUrl = "https://github.com/zmy15/DeepSeek-for-VisualStudio";
        public const string IssuesUrl = RepositoryUrl + "/issues";

        /// <summary>
        /// 新建 Bug 报告的直达链接，直接打开 .github/ISSUE_TEMPLATE/bug_report.yml 模板表单。
        /// 用 template 参数直达具体模板，避免用户先在模板选择页里多跳一次。
        /// </summary>
        public const string BugReportUrl = IssuesUrl + "/new?template=bug_report.yml";

        /// <summary>
        /// 新建功能建议的直达链接，直接打开 .github/ISSUE_TEMPLATE/feature_request.yml 模板表单。
        /// 同上，用 template 参数跳过模板选择页。
        /// </summary>
        public const string FeatureRequestUrl = IssuesUrl + "/new?template=feature_request.yml";

        public const string StarUrl = RepositoryUrl;

        public static string Version => DeepSeek_v4_for_VisualStudio.Vsix.Version;
    }
}