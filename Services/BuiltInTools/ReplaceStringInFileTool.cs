using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.EditTools;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// replace_string_in_file 工具 — 在文件中精确替换字符串。
    /// </summary>
    public class ReplaceStringInFileTool : BuiltInToolBase
    {
        /// <summary>
        /// StagedEditWorkspace 引用（可选注入）。
        /// 设置后，读取/写入 Workspace 而非磁盘（由 Agent 结束统一提交）。
        /// </summary>
        public Services.Editing.StagedEditWorkspace? Workspace { get; set; }

        /// <summary>
        /// 按文件路径的读写锁，防止并行工具调用对同一文件产生竞态条件（IOException: 文件正由另一进程使用）。
        /// </summary>
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(
            StringComparer.OrdinalIgnoreCase);

        public override string Name => "replace_string_in_file";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "replace_string_in_file",
                    Description = L["tool.replace_string_in_file.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            filePath = new { type = "string", description = LocalizationService.Instance["tool.replaceString.param.filePath"] },
                            oldString = new { type = "string", description = LocalizationService.Instance["tool.replaceString.param.oldString"] },
                            newString = new { type = "string", description = LocalizationService.Instance["tool.replaceString.param.newString"] },
                            lineRange = new
                            {
                                type = "string",
                                description = LocalizationService.Instance["tool.replaceString.param.lineRange"]
                            }
                        },
                        required = new[] { "filePath", "oldString", "newString" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string editPath = GetStringArg(args, "filePath");
            string editFile = string.IsNullOrEmpty(editPath) ? "?" : Path.GetFileName(editPath);
            return LocalizationService.Instance.Format("tool.replaceString.displayText", editFile);
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            if (toolResult.StartsWith("Error: ")) return toolResult;
            if (toolResult.Contains("成功") || toolResult.Contains("success"))
                return LocalizationService.Instance["tool.replaceString.editComplete"];
            return LocalizationService.Instance["tool.replaceString.editDone"];
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            return await ApplyReplacementAsync(args, workspaceRoot);
        }

        internal async Task<string> ApplyReplacementAsync(
            Dictionary<string, JsonElement> args,
            string? workspaceRoot)
        {
            string filePath = GetStringArg(args, "filePath");
            string oldString = GetStringArg(args, "oldString");
            string newString = GetStringArg(args, "newString");
            string lineRange = GetStringArg(args, "lineRange");

            if (string.IsNullOrEmpty(filePath))
                return LocalizationService.Instance["tool.replaceString.missingFilePath"];
            if (string.IsNullOrEmpty(oldString))
                return LocalizationService.Instance["tool.replaceString.missingOldString"];

            filePath = ResolvePath(filePath, workspaceRoot);

            // Workspace 模式下文件必须已暂存或已存在
            if (Workspace == null && !File.Exists(filePath))
                return LocalizationService.Instance.Format("tool.replaceString.fileNotFound", filePath) +
                    "\n" + await BuildCurrentStateSnapshotAsync(filePath);

            // 按文件加锁，防止并行工具调用对同一文件产生竞态条件
            SemaphoreSlim fileLock = _fileLocks.GetOrAdd(filePath, _ => new SemaphoreSlim(1, 1));
            await fileLock.WaitAsync();
            try
            {
                string content = Workspace != null
                    ? Workspace.ReadFile(filePath)
                    : FileEncodingHelper.ReadAllText(filePath);
                string normalizedContent = content.Replace("\r\n", "\n").Replace("\r", "\n");
                string normalizedOld = oldString.Replace("\r\n", "\n").Replace("\r", "\n");
                string normalizedNew = newString.Replace("\r\n", "\n").Replace("\r", "\n");

                var rangeResult = ResolveSearchRange(normalizedContent, lineRange);
                if (!rangeResult.Success)
                    return rangeResult.Error!;

                var occurrence = FindOccurrencesInRange(
                    normalizedContent,
                    normalizedOld,
                    rangeResult.StartOffset,
                    rangeResult.EndOffset);
                if (occurrence.FirstIndex < 0)
                    return LocalizationService.Instance.Format("tool.replaceString.textNotFound", Path.GetFileName(filePath)) +
                        "\n" + await BuildCurrentStateSnapshotAsync(filePath);

                if (occurrence.SecondIndex >= 0)
                    return LocalizationService.Instance.Format(
                        "tool.replaceString.multipleMatches",
                        occurrence.FirstIndex,
                        occurrence.SecondIndex) +
                        "\n" + await BuildCurrentStateSnapshotAsync(filePath);

                int index = occurrence.FirstIndex;
                string newContent = normalizedContent.Substring(0, index)
                    + normalizedNew
                    + normalizedContent.Substring(index + normalizedOld.Length);

                // 保留文件原本的换行风格（此前无条件转 CRLF，会把 LF 文件整体改写）
                newContent = LineEndingHelper.Conform(newContent, LineEndingHelper.Detect(content));

                // ── Workspace 模式：写入 Workspace，不备份不落盘 ──
                if (Workspace != null)
                {
                    Workspace.WriteFile(filePath, newContent);
                    return LocalizationService.Instance.Format(
                        "tool.replaceString.replaced", Path.GetFileName(filePath));
                }

                // ── 写入前备份 ──
                string? backupPath = BackupService.CreateBackup(filePath);

                // 已打开文档 → buffer+编辑器 Save；未打开 → 裸写盘
                bool writtenViaBuffer = false;
                try
                {
                    writtenViaBuffer = await EditBufferApplier.TryWriteOpenDocumentAsync(filePath, newContent);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[ReplaceString] buffer 写入失败，回退磁盘: {Path.GetFileName(filePath)} — {ex.Message}");
                }

                if (!writtenViaBuffer)
                    FileEncodingHelper.WriteAllText(filePath, newContent, workspaceRoot);

                // ── 写入成功 → 清理备份 ──
                BackupService.CleanupBackup(backupPath);

                return LocalizationService.Instance.Format(
                    "tool.replaceString.replaced", Path.GetFileName(filePath));
            }
            catch (Exception ex)
            {
                return LocalizationService.Instance.Format("tool.replaceString.failed", ex.Message);
            }
            finally
            {
                fileLock.Release();
            }
        }

        internal async Task<string?> ReadCurrentContentAsync(string filePath)
        {
            if (Workspace != null)
            {
                string stagedContent = Workspace.ReadFile(filePath);
                if (File.Exists(filePath) || stagedContent.Length > 0)
                    return stagedContent;
                return null;
            }

            return File.Exists(filePath)
                ? await Task.Run(() => FileEncodingHelper.ReadAllText(filePath))
                : null;
        }

        internal static (bool Success, int StartOffset, int EndOffset, string? Error)
            ResolveSearchRange(string content, string lineRange)
        {
            if (string.IsNullOrWhiteSpace(lineRange))
                return (true, 0, content.Length, null);

            string[] parts = lineRange.Split('-');
            if (parts.Length > 2)
            {
                return (false, 0, 0,
                    LocalizationService.Instance.Format(
                        "tool.replaceString.invalidLineRange", lineRange));
            }

            if (!int.TryParse(parts[0].Trim(), out int startLine))
            {
                return (false, 0, 0,
                    LocalizationService.Instance.Format(
                        "tool.replaceString.invalidLineRange", lineRange));
            }

            int endLine = startLine;
            if (parts.Length == 2
                && !int.TryParse(parts[1].Trim(), out endLine))
            {
                return (false, 0, 0,
                    LocalizationService.Instance.Format(
                        "tool.replaceString.invalidLineRange", lineRange));
            }
            if (startLine < 1 || endLine < startLine)
            {
                return (false, 0, 0,
                    LocalizationService.Instance.Format(
                        "tool.replaceString.invalidLineRange", lineRange));
            }

            string[] lines = content.Split('\n');
            if (startLine > lines.Length || endLine > lines.Length)
            {
                return (false, 0, 0,
                    LocalizationService.Instance.Format(
                        "tool.replaceString.lineRangeOutOfBounds",
                        lineRange,
                        lines.Length));
            }

            int startOffset = 0;
            for (int i = 0; i < startLine - 1; i++)
                startOffset += lines[i].Length + 1;

            int endOffset = startOffset;
            for (int i = startLine - 1; i < endLine; i++)
            {
                endOffset += lines[i].Length;
                if (i < endLine - 1)
                    endOffset++;
            }

            return (true, startOffset, endOffset, null);
        }

        internal static (int FirstIndex, int SecondIndex) FindOccurrencesInRange(
            string content,
            string searchText,
            int startOffset,
            int endOffset)
        {
            int firstIndex = -1;
            int secondIndex = -1;
            int searchStart = startOffset;

            while (searchStart <= endOffset - searchText.Length)
            {
                int index = content.IndexOf(
                    searchText, searchStart, StringComparison.Ordinal);
                if (index < 0 || index + searchText.Length > endOffset)
                    break;

                if (firstIndex < 0)
                {
                    firstIndex = index;
                }
                else
                {
                    secondIndex = index;
                    break;
                }

                searchStart = index + 1;
            }

            return (firstIndex, secondIndex);
        }

        internal async Task<string> BuildCurrentStateSnapshotAsync(string filePath)
        {
            string? actualContent = await ReadCurrentContentAsync(filePath);
            return ExpectedContentVerifier.BuildCurrentStateMessage(actualContent);
        }

        /// <summary>
        /// 将文件恢复到批量编辑前的原始内容（批量失败时原子回滚）。
        /// Workspace 模式写回 Workspace；磁盘模式走与替换相同的 buffer/落盘路径。
        /// </summary>
        internal async Task RestoreFileContentAsync(string filePath, string originalContent)
        {
            // 回滚到原文：沿用原文自身的换行风格（此前无条件转 CRLF）
            string normalized = LineEndingHelper.Conform(
                originalContent,
                LineEndingHelper.Detect(originalContent));

            if (Workspace != null)
            {
                Workspace.WriteFile(filePath, normalized);
                return;
            }

            string? backupPath = BackupService.CreateBackup(filePath);
            bool writtenViaBuffer = false;
            try
            {
                writtenViaBuffer = await EditBufferApplier.TryWriteOpenDocumentAsync(filePath, normalized);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[ReplaceString] 回滚 buffer 写入失败，回退磁盘: {Path.GetFileName(filePath)} — {ex.Message}");
            }

            if (!writtenViaBuffer)
                FileEncodingHelper.WriteAllText(filePath, normalized, null);
            BackupService.CleanupBackup(backupPath);
        }

    }
}
