using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.EditTools;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Editing
{
    /// <summary>
    /// 未打开磁盘文件的提交目标。
    /// 提交前创建备份，写入后校验，失败时恢复备份。
    /// 保留原文件的编码和换行风格。
    /// </summary>
    public sealed class FileCommitTarget : IProposalCommitTarget
    {
        private string? _backupPath;

        public Task<PreflightResult> PreflightAsync(
            PreparedChangeSet change, CancellationToken cancellationToken)
        {
            if (!File.Exists(change.FilePath))
            {
                return Task.FromResult(PreflightResult.Fail(
                    "目标文件已不存在。", ConflictLevel.FileDeleted));
            }

            // 计算磁盘文件当前哈希
            string diskHash;
            try
            {
                diskHash = ComputeSha256(change.FilePath);
            }
            catch (Exception ex)
            {
                return Task.FromResult(PreflightResult.Fail(
                    $"无法读取目标文件: {ex.Message}", ConflictLevel.ContentChanged));
            }

            if (!string.Equals(diskHash, change.BaselineHash, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(PreflightResult.Fail(
                    "文件在预览期间已被外部修改。请重新生成提案。",
                    ConflictLevel.ContentChanged));
            }

            // 时间戳辅助校验（可选）
            if (change.BaselineLastWriteTimeUtc.HasValue)
            {
                var currentWriteTime = File.GetLastWriteTimeUtc(change.FilePath);
                if (currentWriteTime != change.BaselineLastWriteTimeUtc.Value)
                {
                    // 时间戳不一致但哈希相同 = 可接受（可能是构建工具触碰）
                    Logger.Info($"[FileTarget] 时间戳变化但哈希一致: {Path.GetFileName(change.FilePath)}");
                }
            }

            return Task.FromResult(PreflightResult.Ok());
        }

        public async Task<ApplyResult> CommitAsync(
            PreparedChangeSet change, CancellationToken cancellationToken)
        {
            try
            {
                // 1. 创建备份
                _backupPath = BackupService.CreateBackup(change.FilePath);
                if (_backupPath == null && File.Exists(change.FilePath))
                {
                    return ApplyResult.Failed(change.FilePath, "无法创建备份文件");
                }

                // 2. 写入（保持原文件编码，保留原换行符）
                var content = EditStringMatcher.NormalizeToCrLf(change.ProposedText);
                await Task.Run(() => FileEncodingHelper.WriteAllText(change.FilePath, content, null),
                    cancellationToken);

                // 3. 验证写入
                string writtenContent = await Task.Run(
                    () => File.ReadAllText(change.FilePath), cancellationToken);

                if (!string.Equals(
                    EditStringMatcher.NormalizeToCrLf(writtenContent),
                    content,
                    StringComparison.Ordinal))
                {
                    // 写入不一致 → 恢复备份
                    BackupService.RestoreFromBackup(change.FilePath, _backupPath);
                    _backupPath = null;
                    return ApplyResult.Failed(change.FilePath, "写入后校验失败：磁盘内容与预期不一致");
                }

                // 4. 清理备份
                BackupService.CleanupBackup(_backupPath);
                _backupPath = null;

                Logger.Info($"[FileTarget] 已提交: {Path.GetFileName(change.FilePath)}");
                return ApplyResult.Ok(change.FilePath);
            }
            catch (Exception ex)
            {
                Logger.Error($"[FileTarget] 提交失败: {change.FilePath} — {ex.Message}", ex);

                // 失败恢复
                if (_backupPath != null)
                {
                    BackupService.RestoreFromBackup(change.FilePath, _backupPath);
                    _backupPath = null;
                }

                return ApplyResult.Failed(change.FilePath, ex.Message);
            }
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            // FileCommitTarget 不维护长期状态，回滚由 BackupService 处理
            return Task.CompletedTask;
        }

        private static string ComputeSha256(string filePath)
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            byte[] hashBytes = sha256.ComputeHash(stream);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }
    }
}
