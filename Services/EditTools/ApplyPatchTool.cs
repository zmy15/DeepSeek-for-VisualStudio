using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.EditTools
{
    /// <summary>
    /// apply_patch 工具实现 — OpenAI 风格的 *** Begin Patch / *** End Patch 格式。
    /// 支持 Add / Delete / Update / Move 四种操作。
    /// 
    /// 核心流程（参考 OpenAI Codex apply_patch.py）:
    /// 1. 解析 patch 文本 → PatchOperation（含 Hunks）
    /// 2. 每个 Hunk → FileChunk（delLines / insLines）
    /// 3. 行级上下文匹配定位
    /// 4. 文件重建（遍历原始行，应用 Chunks 删除/插入）
    /// 
    /// 参考: vscode-copilot-chat applyPatchTool.tsx + applyPatch/parser.ts
    /// </summary>
    public class ApplyPatchTool : AbstractEditTool
    {
        private readonly EditFileHealing? _healing;

        protected override string ToolName => "apply_patch";

        // ── Patch 格式正则 ──
        private static readonly Regex PatchBlockRegex = new(
            @"\*\*\*\s*Begin\s*Patch\s*\r?\n(?<body>.*?)\r?\n\s*\*\*\*\s*End\s*Patch",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        private static readonly Regex PatchFileHeaderRegex = new(
            @"\*\*\*\s*(Update|Add|Delete)\s*File\s*:\s*(?<path>[^\r\n]+)",
            RegexOptions.IgnoreCase);

        private static readonly Regex PatchMoveHeaderRegex = new(
            @"\*\*\*\s*Move\s*to\s*:\s*(?<path>[^\r\n]+)",
            RegexOptions.IgnoreCase);

        private static readonly Regex PatchContextMarkerRegex = new(
            @"^@@\s*(?<marker>.*)$", RegexOptions.Multiline);

        private static readonly Regex PatchLineRegex = new(
            @"^([\s\-\+])(.*)$");

        private const int ExpectedAnchorScopeRadius = 300;

        public ApplyPatchTool(DeepSeekApiService apiService, string workspaceRoot)
            : base(apiService, workspaceRoot)
        {
            _healing = new EditFileHealing(apiService);
        }

        /// <summary>
        /// 从 AI 输出中解析所有 Patch 操作（静态方法，无需实例）。
        /// </summary>
        public static List<PatchOperation> ParsePatches(string aiOutput)
        {
            var patches = new List<PatchOperation>();
            if (string.IsNullOrWhiteSpace(aiOutput)) return patches;

            var matches = PatchBlockRegex.Matches(aiOutput);
            foreach (Match match in matches)
            {
                string body = match.Groups["body"].Value;
                patches.AddRange(ParsePatchBody(body));
            }

            return patches;
        }

        /// <summary>
        /// 解析 Patch 块体内容。
        /// </summary>
        private static List<PatchOperation> ParsePatchBody(string body)
        {
            var patches = new List<PatchOperation>();
            var fileMatches = PatchFileHeaderRegex.Matches(body);
            var segmentStarts = new List<int>();
            for (int i = 0; i < fileMatches.Count; i++)
            {
                int start = fileMatches[i].Index;
                if (start > 0)
                {
                    int previousLineEnd = start;
                    while (previousLineEnd > 0
                        && (body[previousLineEnd - 1] == '\n'
                            || body[previousLineEnd - 1] == '\r'))
                    {
                        previousLineEnd--;
                    }

                    int previousLineStart = previousLineEnd > 0
                        ? body.LastIndexOf('\n', previousLineEnd - 1) + 1
                        : 0;
                    string previousLine = body.Substring(
                        previousLineStart,
                        previousLineEnd - previousLineStart).Trim();
                    if (previousLine.StartsWith("*** Move to:", StringComparison.OrdinalIgnoreCase))
                        start = previousLineStart;
                }
                segmentStarts.Add(start);
            }

            for (int i = 0; i < fileMatches.Count; i++)
            {
                int start = segmentStarts[i];
                int end = i + 1 < fileMatches.Count
                    ? segmentStarts[i + 1]
                    : body.Length;
                var patch = ParsePatchSegment(
                    body.Substring(start, end - start));
                if (patch != null)
                    patches.Add(patch);
            }

            return patches;
        }

        private static PatchOperation? ParsePatchSegment(string segment)
        {
            var fileMatch = PatchFileHeaderRegex.Match(segment);
            if (!fileMatch.Success) return null;

            var patch = new PatchOperation
            {
                RawText = segment,
                Action = fileMatch.Groups[1].Value.ToLowerInvariant() switch
                {
                    "update" => PatchFileAction.Update,
                    "add" => PatchFileAction.Add,
                    "delete" => PatchFileAction.Delete,
                    _ => PatchFileAction.Update,
                },
                FilePath = fileMatch.Groups["path"].Value.Trim(),
            };

            var moveMatch = PatchMoveHeaderRegex.Match(segment);
            if (moveMatch.Success)
                patch.MoveToPath = moveMatch.Groups["path"].Value.Trim();

            var lines = segment.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            PatchHunk? currentHunk = null;

            foreach (var rawLine in lines)
            {
                string line = rawLine.TrimEnd('\r');

                // ── 跳过文件头行（已在上面解析）──
                if (line.StartsWith("*** Update") || line.StartsWith("*** Add") ||
                    line.StartsWith("*** Delete") || line.StartsWith("*** Move"))
                    continue;

                // ── *** End of File 标记：标记当前 Hunk 为文件末尾 ──
                if (line.TrimStart().StartsWith("*** End of File"))
                {
                    if (currentHunk != null)
                        currentHunk.IsEof = true;
                    continue;
                }

                // ── 空行且尚未开始 Hunk → 跳过 ──
                if (string.IsNullOrWhiteSpace(line) && currentHunk == null)
                    continue;

                // ── @@ 主标记：新 Hunk 开始 ──
                var ctxMatch = PatchContextMarkerRegex.Match(line);
                if (ctxMatch.Success)
                {
                    if (currentHunk != null)
                        patch.Hunks.Add(currentHunk);

                    currentHunk = new PatchHunk
                    {
                        ContextMarkers = new List<string> { ctxMatch.Groups["marker"].Value.Trim() },
                        RawText = line + "\n",
                    };
                    continue;
                }

                // ── 次级 @@ 标记 ──
                if (currentHunk != null && line.TrimStart().StartsWith("@@"))
                {
                    string marker = line.TrimStart().Substring(2).Trim();
                    currentHunk.ContextMarkers.Add(marker);
                    currentHunk.RawText += line + "\n";
                    continue;
                }

                // ── 标准 Patch 行（- / + / 空格前缀）──
                var plMatch = PatchLineRegex.Match(line);
                if (currentHunk != null && plMatch.Success)
                {
                    currentHunk.Lines.Add(new PatchLine
                    {
                        Type = plMatch.Groups[1].Value[0],
                        Text = plMatch.Groups[2].Value,
                    });
                    currentHunk.RawText += line + "\n";
                }
                // ── FuzzMerge: 行缺少有效前缀（AI 偶尔漏写 -/+/空格）──
                // 参考: parser.ts peek_next_section 的 tolerate invalid lines 逻辑
                // 当作上下文行处理，确保 patch 不因格式小错而中断
                // 注意：如果 AI 本意是 + 行但漏了前缀，此行会从插入行变为上下文行，
                // 可能导致编辑内容静默丢失。记录告警以便后续诊断。
                else if (currentHunk != null && !string.IsNullOrWhiteSpace(line))
                {
                    Logger.Warn($"[FuzzMerge] Hunk 中的行缺少有效前缀（-/+/空格），已当作上下文行: \"{line.Trim().Truncate(80)}\"");
                    currentHunk.Lines.Add(new PatchLine
                    {
                        Type = ' ',
                        Text = line,
                    });
                    currentHunk.RawText += line + "\n";
                }
            }

            if (currentHunk != null)
                patch.Hunks.Add(currentHunk);

            return patch;
        }

        /// <summary>
        /// 执行 Patch 编辑（支持 Add / Update / Delete / Move）。
        /// 返回每个 Patch 操作的应用结果。
        /// </summary>
        public async Task<List<EditApplyResult>> ExecutePatchesAsync(
            List<PatchOperation> patches, CancellationToken ct)
        {
            // ── 事务性备份追踪：记录 文件路径 → 备份路径 ──
            var results = new List<EditApplyResult>();
            var fileState = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var backups = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var workspaceSnapshots = new Dictionary<string, (bool Existed, string Content)>(
                StringComparer.OrdinalIgnoreCase);
            bool anyFailed = false;

            foreach (var patch in patches)
            {
                if (ct.IsCancellationRequested) break;

                string resolvedPath = EditPatchService.ResolvePath(patch.FilePath, WorkspaceRoot);

                // ── 获取当前内容：优先 Workspace，其次磁盘 ──
                if (!fileState.ContainsKey(resolvedPath))
                {
                    string original;
                    if (Workspace != null)
                    {
                        original = Workspace.ReadFile(resolvedPath);
                        if (!workspaceSnapshots.ContainsKey(resolvedPath))
                        {
                            workspaceSnapshots[resolvedPath] = (
                                File.Exists(resolvedPath), original);
                        }
                    }
                    else
                    {
                        original = File.Exists(resolvedPath)
                            ? await Task.Run(() => FileEncodingHelper.ReadAllText(resolvedPath), ct)
                            : string.Empty;
                    }
                    fileState[resolvedPath] = original;

                    // ── 首次接触文件时创建备份（仅直接写盘模式）──
                    if (Workspace == null && !backups.ContainsKey(resolvedPath))
                    {
                        backups[resolvedPath] = BackupService.CreateBackup(resolvedPath);
                    }
                }

                string currentContent = fileState[resolvedPath];

                EditApplyResult result;

                if (Workspace != null)
                {
                    // ── Workspace 模式：写穿落盘 + 撤销追踪 ──
                    result = ApplySinglePatch(patch, resolvedPath, currentContent);

                    if (result.Success && !string.IsNullOrEmpty(result.FinalContent))
                    {
                        string normalizedContent = EditStringMatcher.NormalizeToCrLf(result.FinalContent);
                        // WriteFile 内部对已打开文档走 buffer+编辑器 Save 统一写入，buffer 已同步，
                        // 不再调用 ApplyEditsToOpenDocumentAsync 二次打补丁（那正是把 buffer 弄脏的元凶）。
                        Workspace.WriteFile(resolvedPath, normalizedContent);
                    }
                }
                else
                {
                    // ── 直接写盘模式：含 Healing + 写入后校验 ──
                    result = await ApplyWithValidationAsync(
                        patch, resolvedPath, currentContent, backups, ct);
                }

                if (result.Success && result.PostWriteValidationPassed != false)
                {
                    // ── 更新内存状态 ──
                    if (!string.IsNullOrEmpty(result.FinalContent))
                        fileState[resolvedPath] = result.FinalContent;
                }

                results.Add(result);

                // ── 追踪失败状态 ──
                if (!result.Success)
                    anyFailed = true;
            }
            // ── 事务提交/回滚 ──
            if (Workspace == null)
            {
                if (anyFailed)
                {
                    Logger.Warn("[Transaction] 部分 patch 应用失败，回滚所有已修改文件");
                    BackupService.RollbackAll(backups);
                }
                else
                {
                    foreach (var kvp in backups)
                        BackupService.CleanupBackup(kvp.Value);
                }
            }
            else if (anyFailed)
            {
                Logger.Warn("[Transaction] Workspace 模式部分 patch 失败，恢复本次调用前快照");
                foreach (var kvp in workspaceSnapshots)
                {
                    if (kvp.Value.Existed)
                        Workspace.WriteFile(kvp.Key, kvp.Value.Content);
                    else if (File.Exists(kvp.Key))
                        File.Delete(kvp.Key);
                }
            }

            return results;
        }

        /// <summary>
        /// 应用单个 Patch 到文件（基于 Chunk 重建）。可以静态调用，无需实例。
        /// </summary>
        internal static EditApplyResult ApplySinglePatch(
            PatchOperation patch, string filePath, string fileContent)
        {
            var result = new EditApplyResult
            {
                FilePath = filePath,
                OperationType = EditOperationType.ApplyPatch,
            };

            // ── Add / Delete / Move ──
            if (patch.Action == PatchFileAction.Add)
                return ApplyCreateFromPatch(patch, filePath);
            if (patch.Action == PatchFileAction.Delete)
                return ApplyDeleteFile(filePath);
            if (!string.IsNullOrEmpty(patch.MoveToPath))
                return ApplyMoveFile(filePath, patch.MoveToPath);

            if (!File.Exists(filePath))
            {
                result.Success = false;
                result.ErrorMessage = LocalizationService.Instance.Format("tool.edit.applyPatch.fileNotExist", filePath);
                return result;
            }

            // ── 磁盘内容一致性校验：检测文件是否在 AI 读取后被修改 ──
            var currentDiskContent = FileEncodingHelper.ReadAllText(filePath);
            if (!string.Equals(currentDiskContent, fileContent, StringComparison.Ordinal))
            {
                // 文件在 patch 准备期间被外部修改 → 提取上下文行做快速验证
                var allContextLines = patch.Hunks
                    .SelectMany(h => h.Lines.Where(l => l.Type == ' ').Select(l => l.Text))
                    .ToArray();
                if (!EditStringMatcher.VerifyContentFreshness(currentDiskContent, allContextLines))
                {
                    result.Success = false;
                    result.ErrorMessage = string.Format(
                        "File '{0}' has been modified since you last read it. Please re-read the file with read_file and try your edit again.",
                        Path.GetFileName(filePath));
                    return result;
                }
                // 内容过时但上下文仍在 → 使用最新磁盘内容继续
                fileContent = currentDiskContent;
            }

            var fileLines = fileContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var chunks = new List<(FileChunk chunk, string[] contextLines)>();
            var failedHunks = new List<PatchHunk>();
            var failedHunkIndexes = new List<int>();

            // ── 幂等保护：补丁可能已经写入但上一次结果校验失败/超时。
            // 若每个 hunk 的“最终序列”都已按顺序存在，就返回成功且不重复修改。
            if (IsPatchAlreadyApplied(patch, fileLines))
            {
                if (patch.Hunks.Any(h => !string.IsNullOrEmpty(h.ExpectedAnchor)))
                {
                    string? anchoredError = ValidateAnchoredPatchResult(
                        patch, fileLines, filePath);
                    if (anchoredError != null)
                    {
                        result.Success = false;
                        result.ErrorMessage = anchoredError;
                        return result;
                    }
                }

                result.Success = true;
                result.FinalContent = EditStringMatcher.NormalizeToCrLf(fileContent);
                return result;
            }

            // ── 阶段 1：Hunk → FileChunk ──
            for (int hi = 0; hi < patch.Hunks.Count; hi++)
            {
                var hunk = patch.Hunks[hi];
                var (chunk, contextArray) = HunkToChunk(hunk);
                if (chunk == null)
                {
                    failedHunks.Add(hunk);
                    failedHunkIndexes.Add(hi);
                    continue;
                }
                chunks.Add((chunk, contextArray));
            }

            if (failedHunks.Count > 0)
            {
                result.Success = false;
                result.FailedHunks = failedHunks;
                result.ErrorMessage = BuildFailedHunkMessage(
                    "tool.edit.applyPatch.hunkParseFailedDetailed",
                    failedHunkIndexes,
                    patch,
                    result.FailedHunks);
                return result;
            }

            // ── expected 位置锚点：必须在原文件中精确唯一 ──
            if (patch.Hunks.Any(h => string.IsNullOrEmpty(h.ExpectedAnchor)))
            {
                result.Success = false;
                result.ErrorMessage = LocalizationService.Instance[
                    "tool.edit.applyPatch.locationAnchorRequired"];
                return result;
            }

            var anchorLineByHunk = new Dictionary<int, int>();
            for (int hi = 0; hi < patch.Hunks.Count; hi++)
            {
                string anchor = patch.Hunks[hi].ExpectedAnchor!;

                int count = CountExactOccurrences(fileLines, anchor);
                if (count != 1)
                {
                    result.Success = false;
                    result.ErrorMessage = LocalizationService.Instance.Format(
                        count == 0
                            ? "tool.edit.applyPatch.locationAnchorMissing"
                            : "tool.edit.applyPatch.locationAnchorNotUnique",
                        anchor, count, Path.GetFileName(filePath));
                    return result;
                }

                anchorLineByHunk[hi] = Array.FindIndex(
                    fileLines,
                    line => line.IndexOf(anchor, StringComparison.Ordinal) >= 0);
            }

            // ── 阶段 2：上下文匹配 ──
            int searchStartLine = 0;
            for (int ci = 0; ci < chunks.Count; ci++)
            {
                var (chunk, contextLines) = chunks[ci];
                int expectedAnchorLine = anchorLineByHunk[ci];

                // ── 处理空上下文（全部为 + 行，无实际上下文）──
                if (contextLines.Length == 0)
                {
                    if (patch.Hunks[ci].IsEof)
                    {
                        chunk.OrigIndex += fileLines.Length;
                        searchStartLine = fileLines.Length;
                        continue;
                    }

                    result.Success = false;
                    result.ErrorMessage = LocalizationService.Instance[
                        "tool.edit.applyPatch.pureInsertionNeedsContext"];
                    return result;
                }

                int matchedLine = FindUniqueExactContextMatchNearAnchor(
                    fileLines, contextLines, expectedAnchorLine,
                    minimumStartLine: searchStartLine);

                if (matchedLine < 0)
                {
                    int fallbackStart = Math.Max(
                        searchStartLine,
                        expectedAnchorLine - ExpectedAnchorScopeRadius);
                    int fallbackMatch = EditStringMatcher.MatchContextInFileLines(
                        fileLines, contextLines, fallbackStart, out _);
                    if (fallbackMatch >= 0
                        && Math.Abs(fallbackMatch - expectedAnchorLine)
                            <= ExpectedAnchorScopeRadius)
                    {
                        matchedLine = fallbackMatch;
                    }
                }

                if (matchedLine < 0)
                {
                    failedHunks.Add(patch.Hunks[ci]);
                    failedHunkIndexes.Add(ci);
                    continue;
                }

                if (!IsMarkerNearPosition(
                    fileLines,
                    patch.Hunks[ci].ContextMarkers,
                    matchedLine,
                    contextLines.Length))
                {
                    // expected 唯一锚点与精确上下文已经确认位置；@@ 只是辅助信号，
                    // 长方法中标记可能远离实际修改点，不能据此硬失败。
                    Logger.Warn(
                        $"[ApplyPatch] @@ 标记距离修改点较远，但 expected 锚点已确认位置: " +
                        string.Join(" | ", patch.Hunks[ci].ContextMarkers));
                }

                // ── 匹配后验证：检查 DelLines 是否与匹配位置的实际内容一致 ──
                // 防御模糊匹配误匹配到错误位置（如匹配到方法签名而非实际调用点）
                if (!VerifyDeletionLinesMatchForSegments(chunk, fileLines, matchedLine, contextLines))
                {
                    failedHunks.Add(patch.Hunks[ci]);
                    failedHunkIndexes.Add(ci);
                    continue;
                }

                // ── 纯插入验证：无删除行时，验证插入位置的前后上下文与文件一致 ──
                if (!VerifyInsertContextForSegments(chunk, fileLines, matchedLine, contextLines))
                {
                    failedHunks.Add(patch.Hunks[ci]);
                    failedHunkIndexes.Add(ci);
                    continue;
                }

                chunk.OrigIndex += matchedLine;
                searchStartLine = matchedLine + contextLines.Length;

                // ── 缩进适配：AI 输出的缩进可能与目标文件不一致 ──
                // 参考: parser.ts transformIndentation + additionalIndentation 逻辑
                AdaptChunkIndentation(chunk, contextLines, fileLines, matchedLine);

                // ── 去重防御：移除 InsLines 尾部与原始文件后置上下文重复的闭合符号 ──
                // ReconstructFile 会保留原始文件的后续上下文行。如果 AI 在 + 行中
                // 包含了 } ) ] 等闭合符号，而原始文件中同样的闭合符号也被保留，
                // 就会产生 } } 重复。此处在匹配完成后，对比 InsLines 尾部的闭合
                // 符号与原始文件将被保留的行，移除重复部分。
                foreach (var segment in chunk.Segments)
                    TrimTrailingDuplicateClosingTokens(segment, chunk.OrigIndex + segment.Offset, fileLines);
            }

            if (failedHunks.Count > 0)
            {
                result.Success = false;
                result.FailedHunks = failedHunks;
                result.ErrorMessage = BuildFailedHunkMessage(
                    "tool.edit.applyPatch.hunkMatchFailedDetailed",
                    failedHunkIndexes,
                    patch,
                    result.FailedHunks);
                return result;
            }

            // ── 填充 AppliedEdits（每个独立编辑段计为 1 个编辑点）──
            foreach (var (chunk, contextLines) in chunks)
            {
                foreach (var segment in chunk.Segments)
                {
                    int absoluteStart = chunk.OrigIndex + segment.Offset;
                    result.AppliedEdits.Add(new TextEditOperation
                    {
                        StartLine = absoluteStart,
                        StartColumn = 0,
                        EndLine = absoluteStart + segment.DelLines.Count,
                        EndColumn = 0,
                        NewText = string.Join("\n", segment.InsLines),
                        MatchedText = contextLines.Length > 0
                            ? string.Join("\n", contextLines.Take(3)) + (contextLines.Length > 3 ? "..." : "")
                            : string.Empty,
                        MatchLevelUsed = MatchLevel.Exact,
                    });
                }
            }

            // ── 阶段 3：文件重建 ──
            string reconstructed = ReconstructFile(
                fileLines,
                BuildReconstructionChunks(chunks));
            result.FinalContent = EditStringMatcher.NormalizeToCrLf(reconstructed);

            if (patch.Hunks.Any(h => !string.IsNullOrEmpty(h.ExpectedAnchor)))
            {
                var finalLines = result.FinalContent.Split(
                    new[] { "\r\n", "\n" }, StringSplitOptions.None);
                string? anchoredResultError = ValidateAnchoredPatchResult(
                    patch, finalLines, filePath);
                if (anchoredResultError != null)
                {
                    result.Success = false;
                    result.ErrorMessage = anchoredResultError;
                    return result;
                }
            }

            var finalLinesForStructure = result.FinalContent.Split(
                new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var structureErrors = ValidateStructureDelta(fileLines, finalLinesForStructure);
            if (structureErrors.Count > 0)
            {
                result.Success = false;
                result.ValidationErrors = structureErrors;
                result.PostWriteValidationPassed = false;
                result.ErrorMessage = LocalizationService.Instance.Format(
                    "tool.edit.applyPatch.structureValidationFailed",
                    Path.GetFileName(filePath),
                    string.Join("; ", structureErrors));
                return result;
            }

            result.Success = true;

            // ── v1.1.11: 记录 patch 原文 + hunk 概要（诊断用）──
            LogPatchDetails(filePath, fileContent, result.FinalContent,
                result.AppliedEdits, patch);

            // ── 日志：记录应用前后的内容（前后各 10 行上下文）──
            LogAppliedChanges(filePath, fileContent, result.FinalContent, result.AppliedEdits);

            return result;
        }

        private async Task<EditApplyResult> ApplySinglePatchAsync(PatchOperation patch, string filePath, string fileContent, CancellationToken ct)
        {
            return await Task.Run(() => ApplySinglePatch(patch, filePath, fileContent), ct);
        }

        #region Chunk-based 文件重建

        /// <summary>
        /// 将 PatchHunk 转换为 FileChunk + 上下文行数组。
        /// </summary>
        internal static (FileChunk? chunk, string[] contextLines) HunkToChunk(PatchHunk hunk)
        {
            var contextLines = new List<string>();
            var allDelLines = new List<string>();
            var allInsLines = new List<string>();
            var segments = new List<FileChunkSegment>();
            int originalLineOffset = 0;
            int currentSegmentOffset = -1;
            var currentDelLines = new List<string>();
            var currentInsLines = new List<string>();
            bool hasChanges = false;

            void FlushSegment()
            {
                if (currentSegmentOffset < 0) return;
                if (currentDelLines.Count == 0 && currentInsLines.Count == 0) return;

                segments.Add(new FileChunkSegment
                {
                    Offset = currentSegmentOffset,
                    DelLines = new List<string>(currentDelLines),
                    InsLines = new List<string>(currentInsLines),
                });
                allDelLines.AddRange(currentDelLines);
                allInsLines.AddRange(currentInsLines);
                currentSegmentOffset = -1;
                currentDelLines.Clear();
                currentInsLines.Clear();
            }

            foreach (var line in hunk.Lines)
            {
                switch (line.Type)
                {
                    case ' ':
                        FlushSegment();
                        contextLines.Add(line.Text);
                        originalLineOffset++;
                        break;
                    case '-':
                        hasChanges = true;
                        if (currentSegmentOffset < 0) currentSegmentOffset = originalLineOffset;
                        currentDelLines.Add(line.Text);
                        contextLines.Add(line.Text);
                        originalLineOffset++;
                        break;
                    case '+':
                        hasChanges = true;
                        if (currentSegmentOffset < 0) currentSegmentOffset = originalLineOffset;
                        currentInsLines.Add(line.Text);
                        break;
                }
            }
            FlushSegment();

            if (!hasChanges)
                return (null, Array.Empty<string>());

            // 保留旧字段供兼容和诊断使用。实际重建使用 Segments。
            var chunk = new FileChunk
            {
                OrigIndex = 0,
                DelLines = allDelLines,
                InsLines = allInsLines,
                Segments = segments,
            };

            return (chunk, contextLines.ToArray());
        }

        internal static int FindUniqueExactContextMatchNearAnchor(
            string[] fileLines,
            string[] contextLines,
            int anchorLine,
            bool ignoreWhitespace = false,
            int minimumStartLine = 0)
        {
            if (contextLines.Length == 0)
                return anchorLine;

            int searchStart = Math.Max(
                Math.Max(0, minimumStartLine),
                anchorLine - ExpectedAnchorScopeRadius);
            int searchEnd = Math.Min(
                fileLines.Length - contextLines.Length,
                anchorLine + ExpectedAnchorScopeRadius);
            if (searchEnd < searchStart)
                return -1;

            int bestMatch = -1;
            int bestDistance = int.MaxValue;
            bool ambiguous = false;
            for (int start = searchStart; start <= searchEnd; start++)
            {
                bool matched = true;
                for (int offset = 0; offset < contextLines.Length; offset++)
                {
                    string actual = EditStringMatcher.NormalizeUnicode(
                        fileLines[start + offset]);
                    string expected = EditStringMatcher.NormalizeUnicode(
                        contextLines[offset]);
                    if (ignoreWhitespace)
                    {
                        actual = actual.Trim();
                        expected = expected.Trim();
                    }

                    if (!string.Equals(
                        actual,
                        expected,
                        StringComparison.Ordinal))
                    {
                        matched = false;
                        break;
                    }
                }

                if (!matched)
                    continue;

                int distance = Math.Abs(start - anchorLine);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestMatch = start;
                    ambiguous = false;
                }
                else if (distance == bestDistance)
                {
                    ambiguous = true;
                }
            }

            return ambiguous ? -1 : bestMatch;
        }

        private static string? ValidateAnchoredPatchResult(
            PatchOperation patch,
            string[] finalLines,
            string filePath)
        {
            for (int hi = 0; hi < patch.Hunks.Count; hi++)
            {
                var hunk = patch.Hunks[hi];
                string? anchor = hunk.ExpectedAnchor;
                if (string.IsNullOrEmpty(anchor))
                    continue;

                int anchorCount = CountExactOccurrences(
                    finalLines, anchor);
                if (anchorCount != 1)
                {
                    return LocalizationService.Instance.Format(
                        anchorCount == 0
                            ? "tool.edit.applyPatch.locationAnchorMissingAfterPatch"
                            : "tool.edit.applyPatch.locationAnchorNotUniqueAfterPatch",
                        anchor, anchorCount, Path.GetFileName(filePath));
                }

                int anchorLine = Array.FindIndex(
                    finalLines,
                    line => line.IndexOf(anchor, StringComparison.Ordinal) >= 0);
                string[] finalSequence = hunk.Lines
                    .Where(line => line.Type != '-')
                    .Select(line => line.Text)
                    .ToArray();
                if (finalSequence.Length == 0)
                    continue;

                int match = FindUniqueExactContextMatchNearAnchor(
                    finalLines, finalSequence, anchorLine,
                    ignoreWhitespace: true);
                if (match < 0)
                {
                    return LocalizationService.Instance.Format(
                        "tool.edit.applyPatch.postImageNotAtAnchor",
                        hi + 1,
                        anchor,
                        Path.GetFileName(filePath));
                }
            }

            return null;
        }

        private static string BuildFailedHunkMessage(
            string localizationKey,
            IReadOnlyList<int> failedHunkIndexes,
            PatchOperation patch,
            IReadOnlyList<PatchHunk> failedHunks)
        {
            var details = new List<string>();
            foreach (int index in failedHunkIndexes.Distinct())
            {
                var hunk = patch.Hunks[index];
                string marker = hunk.ContextMarkers.Count > 0
                    ? string.Join(" | ", hunk.ContextMarkers.Take(3))
                    : hunk.Lines
                        .FirstOrDefault(line => line.Type != '+')?.Text
                        ?.Trim() ?? string.Empty;
                if (marker.Length > 80)
                    marker = marker.Substring(0, 80) + "...";

                details.Add(string.IsNullOrEmpty(marker)
                    ? $"#{index + 1}"
                    : $"#{index + 1} (@@ {marker})");
            }

            return LocalizationService.Instance.Format(
                localizationKey,
                failedHunks.Count,
                patch.Hunks.Count,
                string.Join(", ", details));
        }

        private static int CountExactOccurrences(
            IReadOnlyList<string> lines,
            string anchor)
        {
            int count = 0;
            foreach (string line in lines)
            {
                if (line.IndexOf(anchor, StringComparison.Ordinal) >= 0)
                    count++;
            }
            return count;
        }

        internal static List<string> ValidateStructureDelta(
            string[] beforeLines,
            string[] afterLines)
        {
            var before = AnalyzeStructure(string.Join("\n", beforeLines));
            var after = AnalyzeStructure(string.Join("\n", afterLines));
            var errors = new List<string>();

            // 原文件本身就允许天然不平衡；只有修改前该分隔符平衡时，
            // 才要求本次修改保持增量平衡，避免误伤修复半截代码的合法补丁。
            if (before.CurlyOpen == before.CurlyClose)
            {
                AddDelimiterDeltaError(errors, "{}",
                    after.CurlyOpen - before.CurlyOpen,
                    after.CurlyClose - before.CurlyClose);
            }
            if (before.ParenOpen == before.ParenClose)
            {
                AddDelimiterDeltaError(errors, "()",
                    after.ParenOpen - before.ParenOpen,
                    after.ParenClose - before.ParenClose);
            }
            if (before.SquareOpen == before.SquareClose)
            {
                AddDelimiterDeltaError(errors, "[]",
                    after.SquareOpen - before.SquareOpen,
                    after.SquareClose - before.SquareClose);
            }

            if (!before.HasComplexStringSyntax
                && !after.HasComplexStringSyntax
                && before.EndsInBalancedState
                && !after.EndsInBalancedState)
            {
                errors.Add(LocalizationService.Instance[
                    "tool.edit.applyPatch.quoteBalanceRegression"]);
            }

            return errors;
        }

        private static void AddDelimiterDeltaError(
            List<string> errors,
            string pair,
            int openDelta,
            int closeDelta)
        {
            if (openDelta == closeDelta)
                return;

            errors.Add(LocalizationService.Instance.Format(
                "tool.edit.applyPatch.delimiterDelta",
                pair,
                openDelta,
                closeDelta));
        }

        private static StructureScanResult AnalyzeStructure(string content)
        {
            var result = new StructureScanResult();
            char stringQuote = '\0';
            bool inBlockComment = false;
            bool inLineComment = false;

            for (int i = 0; i < content.Length; i++)
            {
                char c = content[i];
                char next = i + 1 < content.Length ? content[i + 1] : '\0';

                if (inLineComment)
                {
                    if (c == '\n')
                        inLineComment = false;
                    continue;
                }

                if (inBlockComment)
                {
                    if (c == '*' && next == '/')
                    {
                        inBlockComment = false;
                        i++;
                    }
                    continue;
                }

                if (stringQuote != '\0')
                {
                    if (c == '\\')
                    {
                        i++;
                        continue;
                    }
                    if (c == stringQuote)
                        stringQuote = '\0';
                    continue;
                }

                if (c == '/' && next == '/')
                {
                    inLineComment = true;
                    i++;
                    continue;
                }
                if (c == '/' && next == '*')
                {
                    inBlockComment = true;
                    i++;
                    continue;
                }
                if (c == '"' || c == '\'' || c == '`')
                {
                    stringQuote = c;
                    continue;
                }

                switch (c)
                {
                    case '{': result.CurlyOpen++; break;
                    case '}': result.CurlyClose++; break;
                    case '(': result.ParenOpen++; break;
                    case ')': result.ParenClose++; break;
                    case '[': result.SquareOpen++; break;
                    case ']': result.SquareClose++; break;
                }
            }

            result.EndsInBalancedState = stringQuote == '\0'
                && !inBlockComment;
            result.HasComplexStringSyntax =
                content.IndexOf("\"\"\"", StringComparison.Ordinal) >= 0
                || content.IndexOf("@\"", StringComparison.Ordinal) >= 0
                || content.IndexOf("@'", StringComparison.Ordinal) >= 0;
            return result;
        }

        private sealed class StructureScanResult
        {
            public int CurlyOpen { get; set; }
            public int CurlyClose { get; set; }
            public int ParenOpen { get; set; }
            public int ParenClose { get; set; }
            public int SquareOpen { get; set; }
            public int SquareClose { get; set; }
            public bool EndsInBalancedState { get; set; }
            public bool HasComplexStringSyntax { get; set; }
        }

        private static bool IsPatchAlreadyApplied(
            PatchOperation patch,
            string[] fileLines)
        {
            if (patch.Action != PatchFileAction.Update
                || !string.IsNullOrEmpty(patch.MoveToPath))
                return false;

            int searchStartLine = 0;
            foreach (var hunk in patch.Hunks)
            {
                // Hunk 的最终序列 = 上下文 + 新增行，去掉待删除行。
                // 它已经完整出现时，重复应用必然是 no-op。
                var finalLines = hunk.Lines
                    .Where(l => l.Type != '-')
                    .Select(l => l.Text)
                    .ToArray();
                if (finalLines.Length == 0)
                    return false;

                int matchedLine = -1;
                for (int start = searchStartLine;
                    start <= fileLines.Length - finalLines.Length;
                    start++)
                {
                    bool matched = true;
                    for (int offset = 0; offset < finalLines.Length; offset++)
                    {
                        if (!string.Equals(
                            fileLines[start + offset],
                            finalLines[offset],
                            StringComparison.Ordinal))
                        {
                            matched = false;
                            break;
                        }
                    }

                    if (matched)
                    {
                        matchedLine = start;
                        break;
                    }
                }

                if (matchedLine < 0)
                    return false;

                searchStartLine = matchedLine + finalLines.Length;
            }

            return true;
        }

        /// <summary>
        /// 移除 InsLines 中与原始文件后置上下文重复的闭合符号。
        ///
        /// ReconstructFile 会保留原始文件的后续上下文行。如果 AI 在 + 行中包含了
        /// } ) ] 等闭合符号，而这些符号在原始文件的后置上下文中已存在且将被保留，
        /// 就会产生重复闭合符号（如 }} 或 )); 等）。
        ///
        /// 此方法在上下文匹配完成后（已知 chunk.OrigIndex 的精确位置），
        /// 从 InsLines 尾部向前扫描闭合符号行（跳过空行/空白行），
        /// 与原始文件将被保留的后置上下文逐行对比，移除匹配的重复项。
        /// </summary>
        /// <param name="segment">已匹配定位的独立编辑段</param>
        /// <param name="baseIndex">编辑段在原始文件中的绝对起始位置</param>
        /// <param name="fileLines">原始文件行数组</param>
        internal static void TrimTrailingDuplicateClosingTokens(
            FileChunkSegment segment, int baseIndex, string[] fileLines)
        {
            if (segment.InsLines.Count == 0)
                return;

            int postChangeStart = baseIndex + segment.DelLines.Count;
            if (postChangeStart >= fileLines.Length)
                return;

            // ── 从 InsLines 尾部向前收集闭合符号行（跳过空行/空白行）──
            // 构建 (insIndex, closingLine) 列表，按 insIndex 升序（从尾到头收集后反转）
            var closingEntries = new List<(int insIndex, string closingLine)>();
            for (int i = segment.InsLines.Count - 1; i >= 0; i--)
            {
                string line = segment.InsLines[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue; // 跳过空行/空白行，继续向上扫描
                if (IsClosingToken(line))
                    closingEntries.Add((i, line.Trim()));
                else
                    break; // 遇到非闭合符号的非空行 → 停止（保护实质性代码）
            }
            closingEntries.Reverse(); // 恢复为从上到下的顺序

            if (closingEntries.Count == 0)
                return;

            // A segment that contains only closing tokens is likely an intentional
            // structural brace (for example, closing a method before the class brace).
            // Only strip a duplicate when it trails substantive inserted code.
            if (segment.InsLines
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .All(IsClosingToken))
                return;

            // ── 与原始文件后置上下文逐行对比 ──
            int removeCount = 0;
            for (int j = 0; j < closingEntries.Count && (postChangeStart + j) < fileLines.Length; j++)
            {
                string origLine = fileLines[postChangeStart + j].Trim();
                if (string.IsNullOrWhiteSpace(origLine))
                    continue; // 原始文件也是空行，跳过继续对比

                if (string.Equals(closingEntries[j].closingLine, origLine, StringComparison.Ordinal))
                    removeCount++;
                else
                    break; // 不匹配 → 后续的闭合符号是 AI 有意添加的，停止
            }

            // ── 从尾部移除匹配的闭合符号行 ──
            if (removeCount > 0)
            {
                // 只移除尾部连续匹配的闭合符号行（保留中间不匹配的）
                var indicesToRemove = new HashSet<int>();
                for (int j = 0; j < removeCount; j++)
                    indicesToRemove.Add(closingEntries[j].insIndex);

                // 从后向前移除，避免索引偏移
                for (int i = segment.InsLines.Count - 1; i >= 0; i--)
                {
                    if (indicesToRemove.Contains(i))
                        segment.InsLines.RemoveAt(i);
                }
            }
        }

        internal static bool IsClosingToken(string line)
        {
            string trimmed = line.Trim();
            return trimmed switch
            {
                ")" or "}" or "]" => true,
                ");" or "};" or "]);" => true,
                "end" or "endif" or "fi" or "done" or "esac" => true,
                _ => trimmed.Length <= 2 && (trimmed == ")" || trimmed == "}" || trimmed == "]"),
            };
        }

        /// <summary>
        /// 检查 @@ 标记文本是否出现在匹配位置附近。
        /// 位置锚点仍为主约束；该辅助用于 healing 和诊断定位。
        /// </summary>
        internal static bool IsMarkerNearPosition(
            string[] fileLines, List<string> contextMarkers,
            int matchedLine, int contextLength, int searchWindow = -1)
        {
            if (contextMarkers == null || contextMarkers.Count == 0) return true;

            if (searchWindow < 0)
                searchWindow = Math.Max(10, Math.Min(50, contextLength + 5));

            int windowStart = Math.Max(0, matchedLine - searchWindow);
            int windowEnd = Math.Min(fileLines.Length, matchedLine + contextLength + searchWindow);

            foreach (var marker in contextMarkers)
            {
                if (string.IsNullOrEmpty(marker)) continue;
                string normalized = EditStringMatcher.NormalizeUnicode(marker);
                for (int i = windowStart; i < windowEnd; i++)
                {
                    if (EditStringMatcher.NormalizeUnicode(fileLines[i])
                        .Contains(normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 匹配后验证：检查 DelLines 是否与文件匹配位置的实际内容一致。
        /// 防御模糊匹配误匹配到错误位置——即使上下文行相似，要删除的行也必须与
        /// 文件对应位置的行高度相似（≥ 75% 行匹配，或全部非空行匹配）。
        /// </summary>
        /// <param name="chunk">待应用的 Chunk（含 DelLines）</param>
        /// <param name="fileLines">文件行数组</param>
        /// <param name="matchedLine">上下文匹配到的文件行号（0-based）</param>
        /// <param name="contextLines">用于匹配的上下文行（含 DelLines）</param>
        /// <returns>true 表示删除行验证通过</returns>
        internal static bool VerifyDeletionLinesMatch(
            FileChunk chunk, string[] fileLines, int matchedLine, string[] contextLines)
        {
            if (chunk.DelLines.Count == 0) return true; // 纯新增 → 无需验证

            // ── 找出 DelLines 在 contextLines 中的偏移位置 ──
            // contextLines 包含 space 行 + minus 行（HunkToChunk 混合两者）
            // DelLines 在 contextLines 中连续出现
            int delOffsetInCtx = -1;
            for (int i = 0; i <= contextLines.Length - chunk.DelLines.Count; i++)
            {
                bool match = true;
                for (int j = 0; j < chunk.DelLines.Count; j++)
                {
                    if (!string.Equals(contextLines[i + j], chunk.DelLines[j], StringComparison.Ordinal))
                    {
                        match = false;
                        break;
                    }
                }
                if (match) { delOffsetInCtx = i; break; }
            }

            if (delOffsetInCtx < 0) return true; // 无法定位 → 跳过验证（可能是纯新增或特殊情况）

            int fileDelStart = matchedLine + delOffsetInCtx;

            // ── 验证：DelLines 与文件对应行比较 ──
            int matchCount = 0;
            int nonEmptyCount = 0;
            for (int j = 0; j < chunk.DelLines.Count; j++)
            {
                int fileLineIdx = fileDelStart + j;
                if (fileLineIdx >= fileLines.Length) break;

                string delLine = chunk.DelLines[j];
                string fileLine = fileLines[fileLineIdx];

                // 跳过纯空白行
                if (string.IsNullOrWhiteSpace(delLine) && string.IsNullOrWhiteSpace(fileLine))
                {
                    matchCount++;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(delLine)) continue;

                nonEmptyCount++;

                // ── 多级比较 ──
                string delTrimmed = EditStringMatcher.NormalizeUnicode(delLine).Trim();
                string fileTrimmed = EditStringMatcher.NormalizeUnicode(fileLine).Trim();

                if (string.Equals(delTrimmed, fileTrimmed, StringComparison.Ordinal))
                {
                    matchCount++;
                }
                else
                {
                    // Levenshtein 容错：允许轻微差异（如变量名变化）
                    int dist = LevenshteinDistanceExtensions.LevenshteinDistance(delTrimmed, fileTrimmed);
                    double similarity = 1.0 - (double)dist / Math.Max(delTrimmed.Length, fileTrimmed.Length);
                    if (similarity >= 0.85 && delTrimmed.Length >= 10) // 长行允许 15% 差异
                        matchCount++;
                }
            }

            // ── 判断：≥ 75% 的非空行匹配，或全部行匹配 ──
            bool passed = nonEmptyCount == 0 || matchCount >= Math.Max(1, (int)Math.Ceiling(nonEmptyCount * 0.75));

            if (!passed)
            {
                // Logger.LogToFile("applypatch",
                //     $"[ApplyPatch]  删除行验证失败: {matchCount}/{nonEmptyCount} 行匹配 (阈值=75%)，匹配位置行={fileDelStart + 1}");
            }

            return passed;
        }

        /// <summary>
        /// 基于 Segment 的删除行验证。旧版 Method 只支持单个连续删除块，
        /// 现在对每个编辑段单独验证，避免同一 Hunk 内多个 - 块时误判位置。
        /// </summary>
        internal static bool VerifyDeletionLinesMatchForSegments(
            FileChunk chunk, string[] fileLines, int matchedLine, string[] contextLines)
        {
            if (chunk.Segments == null || chunk.Segments.Count == 0)
                return VerifyDeletionLinesMatch(chunk, fileLines, matchedLine, contextLines);

            foreach (var segment in chunk.Segments)
            {
                if (segment.DelLines.Count == 0)
                    continue;

                int fileDelStart = matchedLine + segment.Offset;
                if (!VerifyDeletionBlock(
                        segment.DelLines, fileLines, fileDelStart))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool VerifyDeletionBlock(
            List<string> delLines, string[] fileLines, int fileDelStart)
        {
            int matchCount = 0;
            int nonEmptyCount = 0;

            for (int j = 0; j < delLines.Count; j++)
            {
                int fileLineIdx = fileDelStart + j;
                if (fileLineIdx >= fileLines.Length) break;

                string delLine = delLines[j];
                string fileLine = fileLines[fileLineIdx];

                if (string.IsNullOrWhiteSpace(delLine) && string.IsNullOrWhiteSpace(fileLine))
                {
                    matchCount++;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(delLine)) continue;

                nonEmptyCount++;
                string delTrimmed = EditStringMatcher.NormalizeUnicode(delLine).Trim();
                string fileTrimmed = EditStringMatcher.NormalizeUnicode(fileLine).Trim();

                if (string.Equals(delTrimmed, fileTrimmed, StringComparison.Ordinal))
                {
                    matchCount++;
                }
                else
                {
                    int dist = LevenshteinDistanceExtensions.LevenshteinDistance(delTrimmed, fileTrimmed);
                    double similarity = 1.0 - (double)dist / Math.Max(delTrimmed.Length, fileTrimmed.Length);
                    if (similarity >= 0.85 && delTrimmed.Length >= 10)
                        matchCount++;
                }
            }

            return nonEmptyCount == 0
                || matchCount >= Math.Max(1, (int)Math.Ceiling(nonEmptyCount * 0.75));
        }

        /// <summary>
        /// 对每个无删除的 Segment 验证插入位置上下文。
        /// </summary>
        private static bool VerifyInsertContextForSegments(
            FileChunk chunk, string[] fileLines, int matchedLine, string[] contextLines)
        {
            if (chunk.Segments == null || chunk.Segments.Count == 0)
                return true;

            foreach (var segment in chunk.Segments)
            {
                if (segment.DelLines.Count > 0 || segment.InsLines.Count == 0)
                    continue;

                if (!VerifyInsertContext(
                        fileLines, matchedLine, segment.Offset, contextLines))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 纯插入验证：无删除行时，验证插入位置的上下文行在文件中实际存在。
        /// 防御模糊匹配将插入定位到错误位置（如匹配到相似的 } 行）。
        /// 取插入点前后的上下文行（各最多 3 行），验证它们与文件对应位置一致。
        /// </summary>
        private static bool VerifyInsertContext(
            string[] fileLines, int matchedLine, int origIndex, string[] contextLines)
        {
            // 取插入点之前的上下文行（contextLines 中 origIndex 之前的行）
            int preCtxCount = Math.Min(origIndex, 3);
            int matchCount = 0;
            int checkCount = 0;

            if (preCtxCount > 0)
            {
                for (int i = 0; i < preCtxCount; i++)
                {
                    int ctxIdx = origIndex - preCtxCount + i;
                    int fileIdx = matchedLine + ctxIdx;
                    if (fileIdx >= fileLines.Length) return false;
                    string ctxLine = contextLines[ctxIdx].Trim();
                    string fileLine = fileLines[fileIdx].Trim();
                    // 跳过纯空行/注释行（区分度太低）
                    if (string.IsNullOrWhiteSpace(ctxLine) || ctxLine.StartsWith("//") || ctxLine.StartsWith("#") || ctxLine.StartsWith("--"))
                        continue;
                    checkCount++;
                    if (string.Equals(ctxLine, fileLine, StringComparison.Ordinal))
                        matchCount++;
                    else if (ctxLine.Length >= 10)
                    {
                        // 允许模糊匹配（与上下文匹配的 Levenshtein 对齐）
                        int dist = LevenshteinDistanceExtensions.LevenshteinDistance(ctxLine, fileLine);
                        double similarity = 1.0 - (double)dist / Math.Max(ctxLine.Length, fileLine.Length);
                        if (similarity >= 0.85) matchCount++;
                    }
                }
            }

            // 取插入点之后的上下文行
            int postCtxStart = origIndex;
            int postCtxCount = Math.Min(contextLines.Length - postCtxStart, 3);
            int postMatchCount = 0;
            int postCheckCount = 0;

            if (postCtxCount > 0)
            {
                for (int i = 0; i < postCtxCount; i++)
                {
                    int ctxIdx = postCtxStart + i;
                    int fileIdx = matchedLine + ctxIdx;
                    if (fileIdx >= fileLines.Length) return false;
                    string ctxLine = contextLines[ctxIdx].Trim();
                    string fileLine = fileLines[fileIdx].Trim();
                    if (string.IsNullOrWhiteSpace(ctxLine) || ctxLine.StartsWith("//") || ctxLine.StartsWith("#") || ctxLine.StartsWith("--"))
                        continue;
                    postCheckCount++;
                    if (string.Equals(ctxLine, fileLine, StringComparison.Ordinal))
                        postMatchCount++;
                    else if (ctxLine.Length >= 10)
                    {
                        int dist = LevenshteinDistanceExtensions.LevenshteinDistance(ctxLine, fileLine);
                        double similarity = 1.0 - (double)dist / Math.Max(ctxLine.Length, fileLine.Length);
                        if (similarity >= 0.85) postMatchCount++;
                    }
                }
            }

            // ── 判断：≥ 75% 的非跳过行匹配 ──
            bool prePassed = checkCount == 0 || matchCount >= Math.Ceiling(checkCount * 0.75);
            bool postPassed = postCheckCount == 0 || postMatchCount >= Math.Ceiling(postCheckCount * 0.75);

            if (!prePassed || !postPassed)
            {
                // Logger.LogToFile("applypatch",
                //     $"[ApplyPatch]  插入位置验证失败: 前置={matchCount}/{checkCount}, 后置={postMatchCount}/{postCheckCount} (阈值=75%)");
            }

            return prePassed && postPassed;
        }

        /// <summary>
        /// 文件重建：遍历原始行，按 Chunk 列表删除旧行、插入新行。
        /// 保留原始文件的尾部空行数。
        /// 参考: parser.ts _get_updated_file + applyPatchTool.tsx trailing empty line preservation
        ///
        /// 重叠处理：当多个 Chunk 的 [OrigIndex, OrigIndex+DelLines) 区间重叠时，
        /// 合并为一组处理 — 计算删除区间的并集（union），在每个 Chunk 的相对偏移处
        /// 交织插入其 InsLines。原始区间内的所有行均被跳过。
        /// </summary>
        internal static string ReconstructFile(string[] originalLines, List<FileChunk> chunks)
        {
            if (chunks.Count == 0)
                return string.Join("\n", originalLines);

            var sorted = chunks.OrderBy(c => c.OrigIndex).ToList();

            // ── 验证 Chunk 范围 ──
            foreach (var chunk in sorted)
            {
                if (chunk.OrigIndex > originalLines.Length)
                    throw new InvalidOperationException(
                        LocalizationService.Instance.Format("tool.edit.applyPatch.chunkOutOfRange", chunk.OrigIndex, originalLines.Length));
            }

            // ── 将重叠 Chunk 分组 ──
            var groups = new List<List<FileChunk>>();
            foreach (var chunk in sorted)
            {
                if (groups.Count == 0)
                {
                    groups.Add(new List<FileChunk> { chunk });
                }
                else
                {
                    var lastGroup = groups[groups.Count - 1];
                    var lastChunk = lastGroup[lastGroup.Count - 1];
                    int lastChunkEnd = lastChunk.OrigIndex + lastChunk.DelLines.Count;

                    if (chunk.OrigIndex < lastChunkEnd)
                    {
                        // 重叠：加入当前组
                        lastGroup.Add(chunk);
                    }
                    else
                    {
                        // 不重叠：新建组
                        groups.Add(new List<FileChunk> { chunk });
                    }
                }
            }

            // ── 按组重建文件 ──
            var destLines = new List<string>();
            int origIdx = 0;

            foreach (var group in groups)
            {
                var firstChunk = group[0];

                // 复制组前的原始行
                if (firstChunk.OrigIndex > origIdx)
                {
                    destLines.AddRange(originalLines.Skip(origIdx).Take(firstChunk.OrigIndex - origIdx));
                }

                if (group.Count == 1)
                {
                    // ── 单 Chunk 快速路径 ──
                    destLines.AddRange(group[0].InsLines);
                    origIdx = group[0].OrigIndex + group[0].DelLines.Count;
                }
                else
                {
                    // ── 多 Chunk 重叠组：计算删除区间并集，按偏移量交织插入 ──
                    int delStart = group.Min(c => c.OrigIndex);
                    int delEnd = group.Max(c => c.OrigIndex + c.DelLines.Count);

                    // 构建插入映射：相对偏移 → 待插入行列表（保留各 chunk 插入顺序）
                    var insertMap = new SortedDictionary<int, List<string>>();
                    foreach (var c in group)
                    {
                        int offset = c.OrigIndex - delStart;
                        if (!insertMap.ContainsKey(offset))
                            insertMap[offset] = new List<string>();
                        insertMap[offset].AddRange(c.InsLines);
                    }

                    // 遍历删除区间，在每个偏移量处交织插入
                    for (int i = 0; i < delEnd - delStart; i++)
                    {
                        if (insertMap.TryGetValue(i, out var insLines))
                            destLines.AddRange(insLines);
                        // 删除区间内的原始行全部跳过（union 语义）
                    }

                    origIdx = delEnd;
                }
            }

            // 复制尾部剩余行
            if (origIdx < originalLines.Length)
                destLines.AddRange(originalLines.Skip(origIdx));

            // ── 保留原始文件尾部空行数 ──
            // 参考: applyPatchTool.tsx generateUpdateTextDocumentEdit 的 trailing empty line 逻辑
            int origTrailingEmpty = CountTrailingEmptyLines(originalLines);
            int newTrailingEmpty = CountTrailingEmptyLines(destLines);
            for (int i = newTrailingEmpty; i < origTrailingEmpty; i++)
                destLines.Add(string.Empty);

            return string.Join("\n", destLines);
        }

        /// <summary>
        /// 统计字符串数组尾部的空行数。
        /// </summary>
        internal static int CountTrailingEmptyLines(IList<string> lines)
        {
            int count = 0;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (string.IsNullOrEmpty(lines[i].Trim()))
                    count++;
                else
                    break;
            }
            return count;
        }

        /// <summary>
        /// 统计字符串数组尾部的空行数（string[] 重载）。
        /// </summary>
        private static int CountTrailingEmptyLines(string[] lines) => CountTrailingEmptyLines((IList<string>)lines);

        /// <summary>
        /// 将包含多个 Segment 的原始 Chunk 展开为独立的重建 Chunk。
        /// 旧版重建器只接收单个 OrigIndex/DelLines/InsLines，因此这里保留旧接口，
        /// 但每个 Segment 都获得自己在原始文件中的精确位置。
        /// </summary>
        private static List<FileChunk> BuildReconstructionChunks(
            List<(FileChunk chunk, string[] contextLines)> chunks)
        {
            var reconstructionChunks = new List<FileChunk>();

            foreach (var (chunk, _) in chunks)
            {
                if (chunk.Segments == null || chunk.Segments.Count == 0)
                {
                    reconstructionChunks.Add(chunk);
                    continue;
                }

                foreach (var segment in chunk.Segments)
                {
                    reconstructionChunks.Add(new FileChunk
                    {
                        OrigIndex = chunk.OrigIndex + segment.Offset,
                        DelLines = segment.DelLines,
                        InsLines = segment.InsLines,
                    });
                }
            }

            return reconstructionChunks;
        }

        /// <summary>
        /// 缩进适配：将 AI 输出的缩进调整为与目标文件一致。
        /// 参考: parser.ts computeIndentLevel2 + transformIndentation + additionalIndentation
        ///
        /// 缩进单位统一：检测目标文件的缩进风格（tab 或 space），按同一种单位计算
        /// delta 并应用。避免 tab 按 4-space 换算后直接用 tab 字符重复导致过度缩进。
        /// </summary>
        internal static void AdaptChunkIndentation(
            FileChunk chunk, string[] contextLines, string[] fileLines, int matchedLine)
        {
            if (chunk.Segments.Count == 0 || contextLines.Length == 0 || matchedLine >= fileLines.Length)
                return;

            // ── 检测目标文件的缩进风格 ──
            bool targetUsesTabs = DetectFileIndentStyle(fileLines, matchedLine);
            string indentUnit = targetUsesTabs ? "\t" : " ";

            // ── 计算 AI 输出的缩进层级（基于第一个上下文行）──
            string firstCtx = contextLines[0];
            int srcIndent = GetIndentLevel(firstCtx, targetUsesTabs);

            // ── 计算目标文件的缩进层级（基于匹配行）──
            string matchedFileLine = fileLines[matchedLine];
            int targetIndent = GetIndentLevel(matchedFileLine, targetUsesTabs);

            // ── 缩进差值（统一单位：tab 或 space）──
            int indentDelta = targetIndent - srcIndent;
            if (indentDelta <= 0) return;

            // ── 使用目标文件的缩进单位构建附加缩进 ──
            string additionalIndent = new string(indentUnit[0], indentDelta);

            foreach (var segment in chunk.Segments)
            {
                for (int i = 0; i < segment.InsLines.Count; i++)
                {
                    if (!string.IsNullOrWhiteSpace(segment.InsLines[i]))
                        segment.InsLines[i] = additionalIndent + segment.InsLines[i];
                }
            }
        }

        /// <summary>
        /// 检测目标文件在匹配位置附近的缩进风格（tab 优先）。
        /// 扫描匹配行及其附近的非空行，统计首字符是 tab 还是 space。
        /// </summary>
        internal static bool DetectFileIndentStyle(string[] fileLines, int matchedLine)
        {
            int start = Math.Max(0, matchedLine - 5);
            int end = Math.Min(fileLines.Length, matchedLine + 10);
            int tabCount = 0, spaceCount = 0;

            for (int i = start; i < end; i++)
            {
                string line = fileLines[i];
                if (string.IsNullOrEmpty(line)) continue;
                if (line[0] == '\t') tabCount++;
                else if (line[0] == ' ') spaceCount++;
            }

            // 有 tab 的行数 >= 有 space 的行数 → 判定为 tab 风格
            return tabCount >= spaceCount && tabCount > 0;
        }

        /// <summary>
        /// 计算行的缩进级别。tabAsSpace: true 时制表符计为 1（tab 单位），
        /// false 时制表符按 4 空格计（传统行为）。
        /// </summary>
        internal static int GetIndentLevel(string line, bool tabAsSpace = false)
        {
            int level = 0;
            foreach (char c in line)
            {
                if (c == ' ') level++;
                else if (c == '\t') level += tabAsSpace ? 1 : 4;
                else break;
            }
            return level;
        }

        #endregion

        #region Add / Delete / Move

        internal static EditApplyResult ApplyCreateFromPatch(PatchOperation patch, string filePath)
        {
            var result = new EditApplyResult
            {
                FilePath = filePath,
                OperationType = EditOperationType.CreateFile,
            };

            var sb = new StringBuilder();
            foreach (var hunk in patch.Hunks)
            {
                foreach (var line in hunk.Lines)
                {
                    if (line.Type == '+' || line.Type == ' ')
                        sb.AppendLine(line.Text);
                }
            }

            result.FinalContent = EditStringMatcher.NormalizeToCrLf(sb.ToString());
            result.Success = true;

            // ── 日志：记录新建文件的内容 ──
            // Logger.LogToFile("applypatch", $"[ApplyPatch]  新建文件: {filePath}\n内容 ({result.FinalContent.Length} 字符):\n{GetTruncatedContent(result.FinalContent, 20)}");

            return result;
        }

        internal static EditApplyResult ApplyDeleteFile(string filePath)
        {
            var result = new EditApplyResult
            {
                FilePath = filePath,
                OperationType = EditOperationType.DeleteFile,
            };

            try
            {
                if (File.Exists(filePath))
                {
                    // ── 日志：记录删除前的文件内容（前 20 行）──
                    try
                    {
                        string beforeContent = FileEncodingHelper.ReadAllText(filePath);
                        // Logger.LogToFile("applypatch", $"[ApplyPatch]  删除文件: {filePath}\n删除前内容（前20行）:\n{GetTruncatedContent(beforeContent, 20)}");
                    }
                    catch { /* 读取失败不影响主流程 */ }

                    File.Delete(filePath);
                    result.Success = true;
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = LocalizationService.Instance.Format("tool.edit.applyPatch.fileNotExist", filePath);
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = LocalizationService.Instance.Format("tool.edit.applyPatch.deleteFailed", ex.Message);
            }

            return result;
        }

        internal static EditApplyResult ApplyMoveFile(string sourcePath, string destPath)
        {
            var result = new EditApplyResult
            {
                FilePath = sourcePath,
                OperationType = EditOperationType.MoveFile,
            };

            try
            {
                if (!File.Exists(sourcePath))
                {
                    result.Success = false;
                    result.ErrorMessage = LocalizationService.Instance.Format("tool.edit.applyPatch.sourceNotExist", sourcePath);
                    return result;
                }

                string? destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                if (File.Exists(destPath))
                    File.Delete(destPath);

                File.Move(sourcePath, destPath);
                result.Success = true;

                // ── 日志：记录移动操作 ──
                Logger.LogToFile("applypatch", $"[ApplyPatch]  移动文件: {sourcePath} → {destPath}");
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = LocalizationService.Instance.Format("tool.edit.applyPatch.moveFailed", ex.Message);
            }

            return result;
        }

        #endregion

        #region AbstractEditTool 实现

        protected override Task<bool> GenerateEditForFileAsync(
            PreparedEdit prepared, string fileContent, CancellationToken ct)
        {
            // apply_patch 不使用此方法（直接使用 ExecutePatchesAsync）
            throw new NotSupportedException("ApplyPatchTool 使用 ExecutePatchesAsync 而非 GenerateEditForFileAsync");
        }

        #endregion

        #region 写入后校验与回退重改

        /// <summary>
        /// 写入磁盘后对修改区域进行结构校验。
        /// 返回 null 表示通过，否则返回错误信息列表。
        /// </summary>
        private static List<string>? ValidateWrittenContent(
            string filePath, string writtenContent,
            List<TextEditOperation> appliedEdits)
        {
            if (appliedEdits == null || appliedEdits.Count == 0) return null;

            try
            {
                string diskContent = FileEncodingHelper.ReadAllText(filePath);

                // ── v1.1.11: 仅校验磁盘写入一致性，结构完整性检查交由
                //     EditAgent.编辑后健全性检查统一处理，避免重复校验。
                if (!string.Equals(writtenContent, diskContent, StringComparison.Ordinal))
                    return new List<string> { "磁盘内容与预期输出不一致（可能存在并发写入）" };

                return null;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Validate] 校验异常: {filePath} — {ex.Message}");
                return new List<string> { $"校验过程异常: {ex.Message}" };
            }
        }

        /// <summary>
        /// 带校验回退的 Patch 应用循环。
        /// 最多重试 2 次（含首次），每次失败后回退备份并调用 Healing 修正。
        /// </summary>
        private async Task<EditApplyResult> ApplyWithValidationAsync(
            PatchOperation patch, string resolvedPath, string currentContent,
            Dictionary<string, string?> backups, CancellationToken ct)
        {
            const int maxRetries = 2;
            EditApplyResult? lastResult = null;
            var healingPatch = patch;
            string?[] expectedAnchors = patch.Hunks
                .Select(h => h.ExpectedAnchor)
                .ToArray();

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var result = await ApplySinglePatchAsync(healingPatch, resolvedPath, currentContent, ct);
                lastResult = result;

                if (!result.Success)
                {
                    if (result.FailedHunks != null && result.FailedHunks.Count > 0)
                    {
                        var healingRequest = new HealingRequest
                        {
                            FilePath = resolvedPath,
                            CurrentFileContent = currentContent,
                            OriginalOperationType = EditOperationType.ApplyPatch,
                            FailedPatch = healingPatch,
                            FailureReason = result.ErrorMessage ?? "匹配失败",
                            FailedContextDetails = result.FailedHunks
                                .Select(h => $"Hunk ({string.Join(", ", h.ContextMarkers.Take(3))})")
                                .ToList(),
                        };

                        var healingResponse = await _healing!.HealAsync(healingRequest, ct);
                        if (healingResponse?.Success == true && healingResponse.CorrectedPatch != null)
                        {
                            Logger.Info($"[Validate] Healing 成功 (attempt {attempt + 1})，重试应用");
                            healingPatch = healingResponse.CorrectedPatch;
                            CopyExpectedAnchors(healingPatch, expectedAnchors);
                            continue;
                        }
                    }
                    return result;
                }

                if (!string.IsNullOrEmpty(result.FinalContent))
                {
                    // 已打开文档 → buffer+编辑器 Save；未打开 → 裸写盘（校验从磁盘读回，两条路径均覆盖）
                    string contentToWrite = EditStringMatcher.NormalizeToCrLf(result.FinalContent);
                    bool writtenViaBuffer = await EditBufferApplier.TryWriteOpenDocumentAsync(
                        resolvedPath, contentToWrite);
                    if (!writtenViaBuffer)
                        await Task.Run(() => FileEncodingHelper.WriteAllText(resolvedPath, contentToWrite, WorkspaceRoot), ct);

                    var validationErrors = ValidateWrittenContent(
                        resolvedPath, result.FinalContent, result.AppliedEdits);

                    if (validationErrors == null)
                    {
                        result.PostWriteValidationPassed = true;
                        Logger.Info($"[Validate]  校验通过: {Path.GetFileName(resolvedPath)}");
                        return result;
                    }

                    result.PostWriteValidationPassed = false;
                    result.ValidationErrors = validationErrors;
                    Logger.Warn($"[Validate] Error: 校验失败 (attempt {attempt + 1}): {Path.GetFileName(resolvedPath)}\n " +
                        string.Join("\n  ", validationErrors));

                    if (attempt < maxRetries - 1)
                    {
                        if (backups.TryGetValue(resolvedPath, out var backupPath))
                        {
                            BackupService.RestoreFromBackup(resolvedPath, backupPath);
                            backups[resolvedPath] = BackupService.CreateBackup(resolvedPath);
                            currentContent = File.Exists(resolvedPath)
                                ? FileEncodingHelper.ReadAllText(resolvedPath) : string.Empty;
                            Logger.Info($"[Validate] 已回退 {Path.GetFileName(resolvedPath)}，准备重试");
                        }

                        var healingRequest = new HealingRequest
                        {
                            FilePath = resolvedPath,
                            CurrentFileContent = currentContent,
                            OriginalOperationType = EditOperationType.ApplyPatch,
                            FailedPatch = healingPatch,
                            FailureReason = $"写入后校验失败:\n{string.Join("\n", validationErrors)}",
                            FailedContextDetails = validationErrors,
                        };

                        var healingResponse = await _healing!.HealAsync(healingRequest, ct);
                        if (healingResponse?.Success == true && healingResponse.CorrectedPatch != null)
                        {
                            healingPatch = healingResponse.CorrectedPatch;
                            CopyExpectedAnchors(healingPatch, expectedAnchors);
                            continue;
                        }
                        else
                        {
                            Logger.Warn($"[Validate] Healing 无法修正校验问题，停止重试");
                        }
                    }
                }

                return result;
            }

            return lastResult ?? new EditApplyResult
            {
                FilePath = resolvedPath,
                Success = false,
                ErrorMessage = "达到最大重试次数",
            };
        }

        private static void CopyExpectedAnchors(
            PatchOperation patch,
            IReadOnlyList<string?> expectedAnchors)
        {
            for (int i = 0; i < patch.Hunks.Count && i < expectedAnchors.Count; i++)
                patch.Hunks[i].ExpectedAnchor = expectedAnchors[i];
        }

        #endregion

        #region ApplyPatch 日志
        /// 记录补丁应用前后的文件内容（前后各 10 行上下文）。
        /// </summary>
        private static void LogAppliedChanges(string filePath, string beforeContent, string afterContent,
            List<TextEditOperation> appliedEdits)
        {
            try
            {
                if (appliedEdits == null || appliedEdits.Count == 0)
                {
                    Logger.LogToFile("applypatch", $"[ApplyPatch]  已应用补丁: {Path.GetFileName(filePath)}（无编辑点详情）");
                    return;
                }

                // ── 计算编辑区域的行范围 ──
                int minLine = int.MaxValue;
                int maxLine = int.MinValue;
                foreach (var edit in appliedEdits)
                {
                    if (edit.StartLine < minLine) minLine = edit.StartLine;
                    if (edit.EndLine > maxLine) maxLine = edit.EndLine;
                }
                if (minLine == int.MaxValue || maxLine == int.MinValue) return;

                var beforeLines = beforeContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                var afterLines = afterContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

                int contextBefore = 10;
                int contextAfter = 10;
                int beforeStart = Math.Max(0, minLine - contextBefore);
                int beforeEnd = Math.Min(beforeLines.Length - 1, maxLine + contextAfter);
                int afterStart = Math.Max(0, minLine - contextBefore);
                int insertedLines = appliedEdits.Sum(e =>
                    string.IsNullOrEmpty(e.NewText)
                        ? 0
                        : e.NewText.Split('\n').Length);
                int deletedLines = appliedEdits.Sum(e =>
                    Math.Max(0, e.EndLine - e.StartLine));
                int afterExtra = Math.Max(0, insertedLines - deletedLines);
                int afterEnd = Math.Min(afterLines.Length - 1, beforeEnd + afterExtra);

                var sb = new StringBuilder();
                sb.AppendLine($"[ApplyPatch]  {Path.GetFileName(filePath)} 修改区域 (编辑点={appliedEdits.Count}, 行 {minLine + 1}-{maxLine + 1}):");

                // ── 修改前 ──
                sb.AppendLine($"  ── 修改前 (行 {beforeStart + 1}-{beforeEnd + 1}/{beforeLines.Length}) ──");
                for (int i = beforeStart; i <= beforeEnd && i < beforeLines.Length; i++)
                {
                    string marker = (i >= minLine && i <= maxLine) ? "" : " ";
                    sb.AppendLine($"  {marker} {i + 1,5}: {beforeLines[i]}");
                }

                // ── 修改后 ──
                sb.AppendLine($"  ── 修改后 (行 {afterStart + 1}-{afterEnd + 1}/{afterLines.Length}) ──");
                for (int i = afterStart; i <= afterEnd && i < afterLines.Length; i++)
                {
                    sb.AppendLine($"    {i + 1,5}: {afterLines[i]}");
                }

                Logger.LogToFile("applypatch", sb.ToString());
            }
            catch (Exception ex)
            {
                Logger.LogToFile("applypatch", $"[ApplyPatch] 记录应用变更时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// v1.1.11: 记录原始 patch 内容和解析后的 hunk 详情（用于诊断）。
        /// 注意：before/after 上下文由 LogAppliedChanges 负责，此处不重复。
        /// </summary>
        private static void LogPatchDetails(string filePath, string beforeContent,
            string afterContent, List<TextEditOperation> appliedEdits, PatchOperation patch)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"[ApplyPatch]  {Path.GetFileName(filePath)} — Patch 详情 ({patch.Hunks.Count} hunks)");

                // ── 1. 原始 Patch 内容 ──
                sb.AppendLine($"  ── 原始 Patch ({patch.RawText?.Length ?? 0} 字符) ──");
                sb.AppendLine(patch.RawText ?? "(空)");

                // ── 2. 解析后的 Hunks 概要 ──
                sb.AppendLine($"  ── 解析后的 Hunks ({patch.Hunks.Count} 个) ──");
                for (int hi = 0; hi < patch.Hunks.Count; hi++)
                {
                    var hunk = patch.Hunks[hi];
                    int ctxCount = hunk.Lines.Count(l => l.Type == ' ');
                    int delCount = hunk.Lines.Count(l => l.Type == '-');
                    int insCount = hunk.Lines.Count(l => l.Type == '+');
                    sb.AppendLine($"Hunk[{hi}]: 上下文={ctxCount}, 删除={delCount}, 插入={insCount}");
                    if (hunk.ContextMarkers != null && hunk.ContextMarkers.Count > 0)
                        sb.AppendLine($"      @@: {string.Join(" | ", hunk.ContextMarkers)}");
                    sb.AppendLine(hunk.RawText ?? string.Empty);
                }

                Logger.LogToFile("applypatch", sb.ToString());
            }
            catch (Exception ex)
            {
                Logger.LogToFile("applypatch", $"[ApplyPatch] 记录 Patch 详情时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 截断内容用于日志输出（取前 N 行，超长则标注）。
        /// </summary>
        private static string GetTruncatedContent(string content, int maxLines)
        {
            if (string.IsNullOrEmpty(content)) return "(空)";
            return content;
        }

        #endregion
    }
}
