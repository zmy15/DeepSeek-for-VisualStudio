using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Services.IdeContext
{
    /// <summary>
    /// 当前 Git 仓库状态提供器（P1-A 扩展）。
    ///
    /// 用户提问时由 View 在注入 IDE 活动文本的位置同步采集 git 状态：
    /// 当前分支、最新提交（短 SHA + 首行消息）；工作区向上找不到 .git 时注入“仓库不存在”。
    /// 采集通过命令行 git 完成，单条命令有短超时并静默降级，不影响主流程。
    /// </summary>
    public sealed class GitContextProvider
    {
        private const int GitTimeoutMilliseconds = 2000;
        private readonly Func<string, string, string?>? _gitRunner;

        /// <summary>
        /// 创建提供器。测试可传入自定义执行器，避免依赖真实 git 子进程；
        /// 生产环境不传，走 <see cref="RunGitProcess"/>。
        /// </summary>
        public GitContextProvider(Func<string, string, string?>? gitRunner = null)
        {
            _gitRunner = gitRunner;
        }

        /// <summary>
        /// 采集当前 Git 状态。
        /// </summary>
        /// <param name="solutionPath">解决方案文件路径或打开的文件夹路径；为 null 时回退到当前目录。</param>
        public GitContextSnapshot Capture(string? solutionPath)
        {
            string searchRoot = ResolveWorkspaceRoot(solutionPath)
                ?? Directory.GetCurrentDirectory();
            string? gitRoot = FindGitRoot(searchRoot);

            if (gitRoot == null)
            {
                return new GitContextSnapshot
                {
                    IsRepository = false,
                    RootPath = searchRoot,
                    CapturedAt = DateTime.Now,
                };
            }

            string? branch = GetCurrentBranch(gitRoot);
            string? head = RunGit(gitRoot, "log -1 --format=\"%h %s\"");

            return new GitContextSnapshot
            {
                IsRepository = true,
                RootPath = gitRoot,
                Branch = string.IsNullOrWhiteSpace(branch) ? null : branch,
                HeadSummary = string.IsNullOrWhiteSpace(head) ? null : head!.Trim(),
                CapturedAt = DateTime.Now,
            };
        }

        /// <summary>
        /// 格式化为注入 volatile 块的文本；快照为空或采集失败时返回 null（调用方跳过注入）。
        /// </summary>
        public string? BuildPromptBlock(GitContextSnapshot? state)
        {
            if (state == null || !state.HasContent) return null;

            if (!state.IsRepository)
            {
                return "[Git Context]\nGit: repository not present";
            }

            var sb = new StringBuilder(128);
            sb.AppendLine("[Git Context]");
            if (!string.IsNullOrWhiteSpace(state.Branch))
                sb.Append("Git Branch: ").AppendLine(state.Branch!);
            if (!string.IsNullOrWhiteSpace(state.HeadSummary))
                sb.Append("Git HEAD: ").AppendLine(state.HeadSummary!);
            return sb.ToString().TrimEnd();
        }

        private string? GetCurrentBranch(string gitRoot)
        {
            string? symbolic = RunGit(gitRoot, "symbolic-ref --short -q HEAD");
            if (!string.IsNullOrWhiteSpace(symbolic))
                return symbolic.Trim();

            string? sha = RunGit(gitRoot, "rev-parse --short HEAD");
            return string.IsNullOrWhiteSpace(sha) ? null : "detached HEAD at " + sha.Trim();
        }

        private static string? ResolveWorkspaceRoot(string? solutionPath)
        {
            if (string.IsNullOrWhiteSpace(solutionPath)) return null;
            if (Directory.Exists(solutionPath))
                return Path.GetFullPath(solutionPath);

            string? dir = Path.GetDirectoryName(solutionPath);
            return string.IsNullOrWhiteSpace(dir) ? null : Path.GetFullPath(dir);
        }

        private static string? FindGitRoot(string startDir)
        {
            string? current;
            try
            {
                current = Path.GetFullPath(startDir);
            }
            catch
            {
                return null;
            }

            while (!string.IsNullOrEmpty(current))
            {
                string marker = Path.Combine(current, ".git");
                if (Directory.Exists(marker) || File.Exists(marker))
                    return current;

                string? parent = Path.GetDirectoryName(current);
                if (parent == null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    break;
                current = parent;
            }

            return null;
        }

        private string? RunGit(string workDir, string arguments)
            => _gitRunner != null
                ? _gitRunner(workDir, arguments)
                : RunGitProcess(workDir, arguments);

        internal static string? RunGitProcess(string workDir, string arguments)
        {
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workDir,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                var output = new StringBuilder();
                var error = new StringBuilder();
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null) output.AppendLine(e.Data);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) error.AppendLine(e.Data);
                };

                if (!process.Start()) return null;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(GitTimeoutMilliseconds))
                {
                    try { process.Kill(); } catch { }
                    return null;
                }

                // 第二次 WaitForExit 确保异步事件读取完成（.NET Framework 建议）。
                process.WaitForExit();

                return string.IsNullOrWhiteSpace(output.ToString()) ? null : output.ToString().TrimEnd();
            }
            catch
            {
                return null;
            }
        }
    }
}
