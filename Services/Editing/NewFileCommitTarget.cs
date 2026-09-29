using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.EditTools;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Editing
{
    /// <summary>
    /// 新建文件的提交目标。
    /// 只在用户确认后才创建文件并尝试加入 VS 项目。
    /// 用户撤销时磁盘和项目结构中不会出现该文件。
    /// </summary>
    public sealed class NewFileCommitTarget : IProposalCommitTarget
    {
        private string? _createdFilePath;

        public Task<PreflightResult> PreflightAsync(
            PreparedChangeSet change, CancellationToken cancellationToken)
        {
            // 检查是否已有同名文件被其他进程创建
            if (File.Exists(change.FilePath))
            {
                return Task.FromResult(PreflightResult.Fail(
                    $"文件 {Path.GetFileName(change.FilePath)} 已在预览期间被创建。",
                    ConflictLevel.ContentChanged));
            }

            return Task.FromResult(PreflightResult.Ok());
        }

        public async Task<ApplyResult> CommitAsync(
            PreparedChangeSet change, CancellationToken cancellationToken)
        {
            try
            {
                // 1. 确保目录存在
                string? dir = Path.GetDirectoryName(change.FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // 2. 写入文件（按项目编码惯例；兜底 UTF-8 无 BOM）
                var content = EditStringMatcher.NormalizeToCrLf(change.ProposedText);
                await Task.Run(() => FileEncodingHelper.WriteAllText(change.FilePath, content, null),
                    cancellationToken);

                _createdFilePath = change.FilePath;

                // 3. 异步尝试加入项目（不阻塞提交确认）
                _ = TryAddToProjectAsync(change.FilePath);

                Logger.Info($"[NewFile] 已创建: {Path.GetFileName(change.FilePath)}");
                return ApplyResult.Ok(change.FilePath);
            }
            catch (Exception ex)
            {
                Logger.Error($"[NewFile] 创建失败: {change.FilePath} — {ex.Message}", ex);
                return ApplyResult.Failed(change.FilePath, ex.Message);
            }
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            if (_createdFilePath != null && File.Exists(_createdFilePath))
            {
                try
                {
                    File.Delete(_createdFilePath);
                    Logger.Info($"[NewFile] 已回滚删除: {Path.GetFileName(_createdFilePath)}");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[NewFile] 回滚删除失败: {ex.Message}");
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 异步尝试将新文件添加到 VS 项目。失败不影响提交结果。
        /// </summary>
        private static async Task TryAddToProjectAsync(string filePath)
        {
            try
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .SwitchToMainThreadAsync();

                var dte = (EnvDTE.DTE?)Microsoft.VisualStudio.Shell.Package
                    .GetGlobalService(typeof(EnvDTE.DTE));

                if (dte?.Solution == null) return;

                // 查找包含此文件的项目
                foreach (EnvDTE.Project project in dte.Solution.Projects)
                {
                    string? projectDir = Path.GetDirectoryName(project.FullName);
                    if (projectDir == null) continue;

                    if (filePath.StartsWith(projectDir, StringComparison.OrdinalIgnoreCase))
                    {
                        project.ProjectItems.AddFromFile(filePath);
                        Logger.Info($"[NewFile] 已添加到项目: {Path.GetFileName(filePath)} → {project.Name}");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[NewFile] 添加到项目失败: {ex.Message}");
            }
        }
    }
}
