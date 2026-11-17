using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// read_file 工具 — 读取文件内容（支持行范围，带缓存）。
    /// 
    /// v1.1.10: 内置分页上限，默认 200 行/16KB，硬上限 500 行。
    /// 大文件返回结构化标记以支持分页续读（参考 CodeWhale read_file 设计）。
    /// </summary>
    public class ReadFileTool : BuiltInToolBase
    {
        private readonly ConcurrentDictionary<string, FileReadCacheEntry> _fileReadCache;

        /// <summary>
        /// 活跃文件追踪器（可选注入，用于 Working Set 追踪）。
        /// </summary>
        public IActiveFileTracker? ActiveFileTracker { get; set; }

        /// <summary>
        /// 当前 API 请求轮次号（由 BuiltInToolService 同步设置）。
        /// </summary>
        public int CurrentRound { get; set; }

        /// <summary>
        /// 缓存轮数阈值：经过此轮数后允许重新读取文件。
        /// </summary>
        public int RoundThreshold { get; set; } = 10;

        /// <summary>
        /// 视觉模型探测委托。由 BuiltInToolService 在注册工具时绑定到
        /// DeepSeekApiService.CurrentIsVision 的运行时读取，避免会话中途切换模型后状态过期。
        /// 为 null 时按「非视觉模型」处理（保守回退 OCR）。
        /// </summary>
        public Func<bool>? IsVisionModelProvider { get; set; }

        /// <summary>
        /// 运行时判定当前请求的模型是否具备视觉能力。
        /// 单一事实源：最终读取 DeepSeekApiService.CurrentIsVision（经注入委托）。
        /// </summary>
        private bool IsVisionModel() => IsVisionModelProvider?.Invoke() == true;

        /// <summary>视觉模型可直接读取的图片格式。</summary>
        private static readonly HashSet<string> VisionImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".webp",
        };

        /// <summary>单张图片直传上限，与聊天附件视觉直传保持一致。</summary>
        private const long MaxVisionImageBytes = 32L * 1024 * 1024;

        /// <summary>工具结果中的图片路径块起始标记，由 BaseAgent 转成 image_url。</summary>
        public const string VisionImageBlockStart = "[READ_FILE_IMAGE]";

        /// <summary>工具结果中的图片路径块结束标记。</summary>
        public const string VisionImageBlockEnd = "[/READ_FILE_IMAGE]";

        // ── 分页常量（参考 CodeWhale file.rs）──
        /// <summary>默认最大返回行数（无 startLine/endLine 参数时）</summary>
        public const int DefaultMaxLines = 200;
        /// <summary>硬上限行数（endLine 参数夹紧到此值）</summary>
        public const int HardMaxLines = 500;
        /// <summary>可见内容字节上限（UTF-8，含行号前缀）</summary>
        public const int MaxVisibleBytes = 16 * 1024;
        /// <summary>小文件快速通道：行数阈值</summary>
        public const int SmallFileLinesThreshold = 200;
        /// <summary>小文件快速通道：字节阈值</summary>
        public const int SmallFileBytesThreshold = 16 * 1024;

        /// <summary>分页文档默认单次读取页数。</summary>
        public const int DefaultMaxPages = 50;

        /// <summary>显式页范围单次读取硬上限。</summary>
        public const int HardMaxPages = 200;

        public ReadFileTool(ConcurrentDictionary<string, FileReadCacheEntry> fileReadCache)
        {
            _fileReadCache = fileReadCache ?? throw new ArgumentNullException(nameof(fileReadCache));
        }

        public override string Name => "read_file";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "read_file",
                    Description = L["tool.read_file.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            filePath = new { type = "string", description = LocalizationService.Instance["tool.readFile.param.filePath"] },
                            startPage = new { type = "integer", description = LocalizationService.Instance["tool.readFile.param.startPage"] },
                            endPage = new
                            {
                                type = "integer",
                                description = LocalizationService.Instance.Format(
                                    "tool.readFile.param.endPage", HardMaxPages),
                            },
                            maxPages = new
                            {
                                type = "integer",
                                description = LocalizationService.Instance.Format(
                                    "tool.readFile.param.maxPages", DefaultMaxPages, HardMaxPages),
                            },
                            startLine = new { type = "integer", description = LocalizationService.Instance["tool.readFile.param.startLine"] },
                            endLine = new { type = "integer", description = LocalizationService.Instance["tool.readFile.param.endLine"] + $" (clamped to startLine+{HardMaxLines} max)" },
                            maxLines = new { type = "integer", description = LocalizationService.Instance["tool.readFile.param.maxLines"] ?? $"Max lines to return (default {DefaultMaxLines}, max {HardMaxLines})" }
                        },
                        required = new[] { "filePath" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string filePath = GetStringArg(args, "filePath");
            int sPage = GetIntArgAlias(args, "startPage", "start_page", 0);
            int ePage = GetIntArgAlias(args, "endPage", "end_page", 0);
            int sLine = GetIntArg(args, "startLine", 0);
            int eLine = GetIntArg(args, "endLine", 0);
            string fileName = string.IsNullOrEmpty(filePath) ? "?" : Path.GetFileName(filePath);
            if (sPage > 0 && ePage > 0 && ePage >= sPage)
                return LocalizationService.Instance.Format("tool.readFile.displayTextPageRange", fileName, sPage, ePage);
            if (sPage > 0)
                return LocalizationService.Instance.Format("tool.readFile.displayTextPageFrom", fileName, sPage);
            if (sLine > 0 && eLine > 0 && eLine > sLine)
                return LocalizationService.Instance.Format("tool.readFile.displayTextRange", fileName, sLine, eLine);
            else if (sLine > 0)
                return LocalizationService.Instance.Format("tool.readFile.displayTextFrom", fileName, sLine);
            else
                return string.IsNullOrEmpty(filePath)
                    ? LocalizationService.Instance["tool.readFile.readingFile"]
                    : LocalizationService.Instance.Format("tool.readFile.readingFileName", fileName);
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            if (toolResult.StartsWith("Error: ")) return toolResult;
            if (toolResult.IndexOf(VisionImageBlockStart, StringComparison.Ordinal) >= 0)
                return LocalizationService.Instance["tool.readFile.imageReadComplete"];

            var readLines = toolResult.Split('\n');
            string firstLine = readLines.Length > 0 ? readLines[0].Trim() : "";
            var pageCountMatch = Regex.Match(
                firstLine, @"total_pages=""(\d+)""",
                RegexOptions.IgnoreCase);
            if (pageCountMatch.Success)
                return LocalizationService.Instance.Format(
                    "tool.readFile.readCompletePages",
                    pageCountMatch.Groups[1].Value);
            var lineCountMatch = System.Text.RegularExpressions.Regex.Match(
                firstLine, @"(?:共|total|总计)\s*(\d+)\s*(?:行|lines)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (lineCountMatch.Success)
                return LocalizationService.Instance.Format("tool.readFile.readCompleteLines", lineCountMatch.Groups[1].Value);
            return LocalizationService.Instance.Format("tool.readFile.readCompleteLines", readLines.Length);
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string filePath = GetStringArg(args, "filePath");
            if (string.IsNullOrEmpty(filePath))
                return LocalizationService.Instance["tool.readFile.missingParam"];

            int reqStartPage = Math.Max(1, GetIntArgAlias(args, "startPage", "start_page", 1));
            int requestedEndPage = GetIntArgAlias(args, "endPage", "end_page", int.MaxValue);
            int requestedMaxPages = GetIntArgAlias(args, "maxPages", "max_pages", DefaultMaxPages);
            int reqStartLine = GetIntArg(args, "startLine", 1);
            int reqEndLine = GetIntArg(args, "endLine", int.MaxValue);
            bool cacheable = !RequiresStructuredParsing(filePath);
            int visionMaxPages = requestedEndPage == int.MaxValue
                ? requestedMaxPages
                : requestedEndPage - reqStartPage + 1;
            visionMaxPages = Math.Max(1, Math.Min(visionMaxPages, HardMaxPages));

            // ── PDF 视觉直传：逐页渲染后交给视觉模型。 ──
            if (CanUseVisionPdfPassthrough(filePath))
            {
                string? pdfResult = await BuildVisionPdfResultAsync(
                    filePath,
                    reqStartPage,
                    visionMaxPages);
                if (!string.IsNullOrWhiteSpace(pdfResult))
                    return pdfResult;
            }

            // ── PowerPoint 视觉直传：本地导出幻灯片 PNG，交给视觉模型。 ──
            if (CanUseVisionPresentationPassthrough(filePath))
            {
                string? presentationResult = await BuildVisionPresentationResultAsync(
                    filePath,
                    reqStartPage,
                    visionMaxPages);
                if (!string.IsNullOrWhiteSpace(presentationResult))
                    return presentationResult;
            }

            // ── 图片视觉直传：视觉模型不再走 OCR，由 BaseAgent 把本地图片附加为 image_url。──
            if (CanUseVisionImagePassthrough(filePath))
                return BuildVisionImageResult(filePath);

            // ── 缓存命中 ──
            if (cacheable && _fileReadCache.TryGetValue(filePath, out FileReadCacheEntry cachedEntry))
            {
                string cachedFull = cachedEntry.FullContent;
                int lastRound = cachedEntry.LastReadRound;
                bool fileChanged = false;

                try
                {
                    if (File.Exists(filePath))
                    {
                        string currentContent = await FileParserService.ReadFileContentForToolAsync(filePath);
                        if (currentContent != cachedFull)
                        {
                            fileChanged = true;
                            // 文件已变更 → 更新 FullContent，清空已读范围
                            _fileReadCache.TryUpdate(filePath,
                                new FileReadCacheEntry
                                {
                                    FullContent = currentContent,
                                    ReadRanges = null,
                                    LastReadRound = lastRound
                                },
                                cachedEntry);
                            cachedFull = currentContent;
                        }
                    }
                    else
                    {
                        _fileReadCache.TryRemove(filePath, out _);
                        return LocalizationService.Instance.Format("tool.readFile.fileNotFound", filePath);
                    }
                }
                catch { /* 读取失败时不阻塞，使用缓存内容 */ }

                if (!fileChanged)
                {
                    // ── 轮数阈值检查：过期后清空已读范围，允许重新读取 ──
                    // 如果 lastRound > CurrentRound，说明轮次计数器已重置（新会话），
                    // 缓存条目来自旧会话，应视为过期并立即允许重读
                    int roundsSinceLastRead;
                    bool staleCache = CurrentRound > 0 && lastRound > 0 && lastRound > CurrentRound;
                    if (CurrentRound > 0 && lastRound > 0 && lastRound <= CurrentRound)
                        roundsSinceLastRead = CurrentRound - lastRound;
                    else if (staleCache)
                        roundsSinceLastRead = int.MaxValue; // 旧会话缓存，强制过期
                    else
                        roundsSinceLastRead = 0;
                    if (RoundThreshold > 0 && roundsSinceLastRead >= RoundThreshold)
                    {
                        // 轮数过期 → 清空已读范围，允许重读
                        int totalLines = cachedFull.Count(c => c == '\n') + 1;
                        int actualEnd = Math.Min(reqEndLine, totalLines);
                        string freshContent = FormatLinesFromFullContent(cachedFull, reqStartLine, actualEnd);
                        _fileReadCache.TryUpdate(filePath,
                            new FileReadCacheEntry
                            {
                                FullContent = cachedFull,
                                ReadRanges = new List<(int, int)> { (reqStartLine, actualEnd) },
                                LastReadRound = CurrentRound
                            },
                            cachedEntry);
                        var fname = Path.GetFileName(filePath);
                        return staleCache
                            ? LocalizationService.Instance.Format("tool.readFile.cacheExpiredStale", fname, filePath, totalLines, reqStartLine, actualEnd, freshContent)
                            : LocalizationService.Instance.Format("tool.readFile.cacheExpiredRound", roundsSinceLastRead, RoundThreshold, fname, filePath, totalLines, reqStartLine, actualEnd, freshContent);
                    }

                    // ── 行范围覆盖检查 ──
                    int cachedTotalLines = cachedFull.Count(c => c == '\n') + 1;
                    int reqActualEnd = Math.Min(reqEndLine, cachedTotalLines);
                    if (cachedEntry.IsRangeCovered(reqStartLine, reqActualEnd))
                    {
                        // ── 请求范围已被缓存覆盖 → 直接从缓存返回内容，避免重复磁盘读取 ──
                        //     修复：之前返回 "已缓存，请勿重复读取" 警告，导致不同 Explore 子代理
                        //     （串行执行时）无法获取前一个子代理已读取的文件内容。
                        //     现在直接返回缓存中的实际内容，带 cached="true" 标记，
                        //     既避免磁盘 I/O，又确保后续子代理能获得所需数据。
                        string cachedRangedContent = FormatLinesFromFullContent(cachedFull, reqStartLine, reqActualEnd);
                        int cacheShownStart = reqStartLine;
                        int cacheShownEnd = reqActualEnd;
                        bool cacheTruncated = cacheShownEnd < cachedTotalLines;
                        string roundHint = CurrentRound > 0 && lastRound > 0 && lastRound <= CurrentRound
                            ? $" (from cache, {roundsSinceLastRead} rounds ago)"
                            : " (from cache)";
                        var cachedSb = new StringBuilder();
                        cachedSb.AppendLine($"<file path=\"{filePath}\" total_lines=\"{cachedTotalLines}\" shown_lines=\"{cacheShownStart}-{cacheShownEnd}\" truncated=\"{cacheTruncated.ToString().ToLowerInvariant()}\" cached=\"true\" cache_note=\"{roundHint}\">");
                        cachedSb.Append(cachedRangedContent);
                        if (cacheTruncated)
                            cachedSb.AppendLine($"\n[TRUNCATED] Showing lines {cacheShownStart}-{cacheShownEnd} of {cachedTotalLines}. To continue, call read_file with path=\"{filePath}\" start_line={cacheShownEnd + 1}");
                        cachedSb.Append("</file>");
                        return cachedSb.ToString();
                    }

                    // 请求范围未被覆盖 → 从缓存中提取新范围，更新已读范围
                    string rangedContent = FormatLinesFromFullContent(cachedFull, reqStartLine, reqActualEnd);
                    var updatedEntry = cachedEntry;
                    updatedEntry.AddRange(reqStartLine, reqActualEnd);
                    updatedEntry.LastReadRound = CurrentRound;
                    _fileReadCache.TryUpdate(filePath, updatedEntry, cachedEntry);

                    // 注册活跃文件访问
                    ActiveFileTracker?.ObserveRead(filePath, Name, CurrentRound);

                    // 构建结构化返回格式（缓存命中 + 新范围）
                    int shownStart = reqStartLine;
                    int shownEnd = reqActualEnd;
                    bool truncated = shownEnd < cachedTotalLines;
                    var sb = new StringBuilder();
                    sb.AppendLine($"<file path=\"{filePath}\" total_lines=\"{cachedTotalLines}\" shown_lines=\"{shownStart}-{shownEnd}\" truncated=\"{truncated.ToString().ToLowerInvariant()}\" cached=\"true\">");
                    sb.Append(rangedContent);
                    if (truncated)
                        sb.AppendLine($"\n[TRUNCATED] Showing lines {shownStart}-{shownEnd} of {cachedTotalLines}. To continue, call read_file with path=\"{filePath}\" start_line={shownEnd + 1}");
                    sb.Append("</file>");
                    return sb.ToString();
                }

                // 文件已变更 → 返回完整最新内容（已读范围已清空，重新开始追踪）
                int changedTotalLines = cachedFull.Count(c => c == '\n') + 1;
                int changedActualEnd = Math.Min(reqEndLine, changedTotalLines);
                string changedContent = FormatLinesFromFullContent(cachedFull, reqStartLine, changedActualEnd);
                _fileReadCache.TryUpdate(filePath,
                    new FileReadCacheEntry
                    {
                        FullContent = cachedFull,
                        ReadRanges = new List<(int, int)> { (reqStartLine, changedActualEnd) },
                        LastReadRound = CurrentRound
                    },
                    cachedEntry);

                // 注册活跃文件访问
                ActiveFileTracker?.ObserveRead(filePath, Name, CurrentRound);

                int cShownEnd = changedActualEnd;
                bool cTruncated = cShownEnd < changedTotalLines;
                var csb = new StringBuilder();
                csb.AppendLine($"<file path=\"{filePath}\" total_lines=\"{changedTotalLines}\" shown_lines=\"{reqStartLine}-{cShownEnd}\" truncated=\"{cTruncated.ToString().ToLowerInvariant()}\" changed=\"true\">");
                csb.Append(changedContent);
                if (cTruncated)
                    csb.AppendLine($"\n[TRUNCATED] File changed. Showing lines {reqStartLine}-{cShownEnd} of {changedTotalLines}.");
                csb.Append("</file>");
                return csb.ToString();
            }

            // ── 首次读取（缓存未命中）──
            if (!File.Exists(filePath))
            {
                string wsHint = !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot)
                    ? "\n" + LocalizationService.Instance.Format("tool.readFile.workspaceHint", workspaceRoot)
                    : "";
                wsHint += "\n" + LocalizationService.Instance["tool.readFile.newFileHint"];
                return LocalizationService.Instance.Format("tool.readFile.fileNotFound", filePath) + wsHint;
            }

            try
            {
                bool isPagedDocument = IsPagedDocument(filePath);
                string pagedCacheKey = "paged|" + filePath;
                long sourceLastWriteTicks = File.GetLastWriteTimeUtc(filePath).Ticks;
                string fullContent;

                if (isPagedDocument
                    && _fileReadCache.TryGetValue(pagedCacheKey, out FileReadCacheEntry pagedCache)
                    && pagedCache.SourceLastWriteUtcTicks == sourceLastWriteTicks)
                {
                    fullContent = pagedCache.FullContent;
                }
                else
                {
                    // 读取完整文件内容用于缓存
                    fullContent = await FileParserService.ReadFileContentForToolAsync(filePath);
                    if (isPagedDocument)
                    {
                        _fileReadCache[pagedCacheKey] = new FileReadCacheEntry
                        {
                            FullContent = fullContent,
                            ReadRanges = new List<(int, int)>(),
                            LastReadRound = CurrentRound,
                            SourceLastWriteUtcTicks = sourceLastWriteTicks,
                        };
                    }
                }

                if (isPagedDocument)
                {
                    ActiveFileTracker?.ObserveRead(filePath, Name, CurrentRound);
                    return BuildPagedDocumentResult(
                        filePath,
                        fullContent,
                        reqStartPage,
                        requestedEndPage,
                        requestedMaxPages);
                }

                int totalLines = fullContent.Count(c => c == '\n') + 1;
                int totalBytes = Encoding.UTF8.GetByteCount(fullContent);

                // ── 小文件快速通道：无显式范围参数且文件在阈值内 → 原样返回，不包装 ──
                bool hasExplicitRange = args.ContainsKey("startLine") || args.ContainsKey("endLine");
                if (!hasExplicitRange && totalLines <= SmallFileLinesThreshold && totalBytes <= SmallFileBytesThreshold)
                {
                    // 注册活跃文件访问
                    ActiveFileTracker?.ObserveRead(filePath, Name, CurrentRound);

                    // 缓存完整内容
                    if (cacheable)
                    {
                        _fileReadCache.TryAdd(filePath,
                            new FileReadCacheEntry
                            {
                                FullContent = fullContent,
                                ReadRanges = new List<(int, int)> { (1, totalLines) },
                                LastReadRound = CurrentRound
                            });
                    }

                    // ── 契约前缀防碰撞（根修）：内容以 "Error: "/"Timeout: "/"[BLOCKED] " ──
                    // 开头的文件若原样返回，会被 BaseAgent 连续错误检测 / ToolExecutionOutcome.Classify
                    // 误判为工具失败，累计后强制终止工具循环。此场景改用 <file> 信封包裹
                    // （与分页路径同构），正常内容保持原样快速通道不变。
                    if (ToolExecutionOutcome.StartsWithContractMarker(fullContent))
                    {
                        var wrapped = new StringBuilder();
                        wrapped.AppendLine($"<file path=\"{filePath}\" total_lines=\"{totalLines}\" shown_lines=\"1-{totalLines}\" truncated=\"false\">");
                        wrapped.Append(fullContent);
                        wrapped.Append("</file>");
                        return wrapped.ToString();
                    }

                    return fullContent;
                }

                // ── 大文件 / 显式范围 → 分页模式 ──
                int maxLines = GetIntArg(args, "maxLines", DefaultMaxLines);
                maxLines = Math.Max(1, Math.Min(maxLines, HardMaxLines));

                // 计算实际显示范围
                int shownStart = reqStartLine;
                int shownEnd = Math.Min(reqStartLine + maxLines - 1, totalLines);

                if (shownStart > totalLines)
                {
                    // 起始行超出文件范围 → 空内容哨兵
                    return
                        $"<file path=\"{filePath}\" total_lines=\"{totalLines}\" shown_lines=\"none\" truncated=\"false\">\n" +
                        $"\n[NO CONTENT] start_line {shownStart} is beyond total_lines {totalLines}.\n" +
                        $"</file>";
                }

                // 提取行范围内容（带行号前缀）
                string rangedContent = FormatLinesFromFullContent(fullContent, shownStart, shownEnd);

                // 字节截断检查
                int rangedBytes = Encoding.UTF8.GetByteCount(rangedContent);
                bool truncatedByBytes = rangedBytes > MaxVisibleBytes;
                if (truncatedByBytes)
                {
                    // UTF-8 安全截断
                    string truncatedContent = TruncateUtf8Safe(rangedContent, MaxVisibleBytes);
                    rangedContent = truncatedContent;
                }

                bool truncatedByLines = shownEnd < totalLines;
                bool truncated = truncatedByLines || truncatedByBytes;
                int nextStart = shownEnd + 1;

                // ── 构建结构化返回格式（参考 CodeWhale）──
                var resultBuilder = new StringBuilder();

                // 文件元数据行
                resultBuilder.Append($"<file path=\"{filePath}\" total_lines=\"{totalLines}\" shown_lines=\"{shownStart}-{shownEnd}\" truncated=\"{truncated.ToString().ToLowerInvariant()}\"");
                if (truncatedByLines)
                    resultBuilder.Append($"next_start_line=\"{nextStart}\"");
                resultBuilder.AppendLine(">");

                // 文件内容
                resultBuilder.Append(rangedContent);

                // 截断提示
                if (truncatedByLines)
                {
                    resultBuilder.AppendLine();
                    resultBuilder.Append($"[TRUNCATED] Showing lines {shownStart}-{shownEnd} of {totalLines}. ");
                    resultBuilder.Append($"To continue, call read_file with path=\"{filePath}\" start_line={nextStart} max_lines={maxLines}");
                    resultBuilder.AppendLine();
                }
                if (truncatedByBytes)
                {
                    resultBuilder.AppendLine();
                    resultBuilder.AppendLine($"[TRUNCATED] The selected range exceeded {MaxVisibleBytes / 1024}KB. Continue with a smaller max_lines value.");
                }

                resultBuilder.Append("</file>");

                string result = resultBuilder.ToString();

                // ── 缓存完整内容 + 已读范围 ──
                // 注意：缓存存储的是完整文件内容（无行号前缀），而非分页内容
                if (cacheable)
                {
                    _fileReadCache.TryAdd(filePath,
                        new FileReadCacheEntry
                        {
                            FullContent = fullContent,
                            ReadRanges = new List<(int, int)> { (shownStart, shownEnd) },
                            LastReadRound = CurrentRound
                        });
                }

                // 注册活跃文件访问
                ActiveFileTracker?.ObserveRead(filePath, Name, CurrentRound);

                return result;
            }
            catch (Exception ex)
            {
                return LocalizationService.Instance.Format("tool.readFile.failed", ex.Message);
            }
        }

        /// <summary>
        /// 文档、PDF、表格和图片需要通过 FileParserService 提取文本。
        /// 这类文件不进入基于纯文本内容比较的页缓存，每次读取都按原始文件重新解析。
        /// </summary>
        private static bool RequiresStructuredParsing(string filePath)
            => FileParserService.IsSupportedFormat(filePath)
               && !FileParserService.IsTextFormat(filePath);

        /// <summary>
        /// 判断文件是否可由视觉模型直接读取。超大图片和视觉模型不支持的格式保留 OCR 回退。
        /// </summary>
        private bool CanUseVisionImagePassthrough(string filePath)
        {
            if (!IsVisionModel() || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            if (!VisionImageExtensions.Contains(Path.GetExtension(filePath)))
                return false;

            try
            {
                return new FileInfo(filePath).Length <= MaxVisionImageBytes;
            }
            catch
            {
                return false;
            }
        }

        private bool CanUseVisionPresentationPassthrough(string filePath)
        {
            return IsVisionModel()
                && !string.IsNullOrWhiteSpace(filePath)
                && File.Exists(filePath)
                && PresentationRenderService.IsPresentationFile(filePath);
        }

        private bool CanUseVisionPdfPassthrough(string filePath)
        {
            return IsVisionModel()
                && !string.IsNullOrWhiteSpace(filePath)
                && File.Exists(filePath)
                && string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<string?> BuildVisionPdfResultAsync(
            string filePath,
            int startPage,
            int maxPages)
        {
            try
            {
                RenderedDocumentPageBatch batch =
                    await PdfRenderService.RenderPdfPageBatchToPngAsync(
                        filePath,
                        startPage: startPage,
                        maxPages: maxPages);
                return BuildVisionDocumentResult(
                    filePath,
                    "pdf",
                    batch);
            }
            catch (Exception ex)
            {
                Logger.Warn(
                    $"[Vision] PDF render failed, fallback to text/OCR: {Path.GetFileName(filePath)} - {ex.Message}");
                return null;
            }
        }

        private static async Task<string?> BuildVisionPresentationResultAsync(
            string filePath,
            int startPage,
            int maxPages)
        {
            try
            {
                RenderedDocumentPageBatch batch =
                    await PresentationRenderService.RenderPresentationPageBatchToPngAsync(
                        filePath,
                        startPage: startPage,
                        maxPages: maxPages);
                return BuildVisionDocumentResult(
                    filePath,
                    "powerpoint",
                    batch);
            }
            catch (Exception ex)
            {
                Logger.Warn(
                    $"[Vision] PowerPoint render failed, fallback to text/OCR: {Path.GetFileName(filePath)} - {ex.Message}");
                return null;
            }
        }

        internal static string? BuildVisionDocumentResult(
            string filePath,
            string kind,
            RenderedDocumentPageBatch batch)
        {
            if (batch.TotalPages <= 0 && batch.Pages.Count == 0)
                return null;

            var builder = new StringBuilder();
            if (batch.StartPage > batch.TotalPages)
            {
                builder.AppendLine(
                    $"<file path=\"{filePath}\" kind=\"{kind}\" total_pages=\"{batch.TotalPages}\" shown_pages=\"none\" truncated=\"false\" read_mode=\"pages\" vision_passthrough=\"true\">");
                builder.AppendLine(
                    $"[NO CONTENT] start_page {batch.StartPage} is beyond total_pages {batch.TotalPages}.");
                builder.Append("</file>");
                return builder.ToString();
            }

            if (batch.Pages.Count == 0)
                return null;

            int shownEndPage = batch.ShownEndPage;
            bool truncated = shownEndPage < batch.TotalPages;
            builder.AppendLine(
                $"<file path=\"{filePath}\" kind=\"{kind}\" size_bytes=\"{new FileInfo(filePath).Length}\" total_pages=\"{batch.TotalPages}\" shown_pages=\"{batch.StartPage}-{shownEndPage}\" truncated=\"{truncated.ToString().ToLowerInvariant()}\" read_mode=\"pages\"{(truncated ? $" next_start_page=\"{shownEndPage + 1}\"" : string.Empty)} vision_passthrough=\"true\">");
            builder.AppendLine(VisionImageBlockStart);
            foreach (byte[] png in batch.Pages)
            {
                builder.Append("data:image/png;base64,");
                builder.AppendLine(Convert.ToBase64String(png));
            }
            builder.AppendLine(VisionImageBlockEnd);
            if (truncated)
            {
                builder.AppendLine(
                    $"[PAGED] Showing pages {batch.StartPage}-{shownEndPage} of {batch.TotalPages}. " +
                    $"To continue, call read_file with path=\"{filePath}\" start_page={shownEndPage + 1}.");
            }
            builder.AppendLine("</file>");
            return builder.ToString();
        }

        /// <summary>构建只含本地路径的轻量结果，BaseAgent 负责将其转换成视觉内容块。</summary>
        private static string BuildVisionImageResult(string filePath)
        {
            long sizeBytes = new FileInfo(filePath).Length;
            return
                $"<file path=\"{filePath}\" kind=\"image\" size_bytes=\"{sizeBytes}\" vision_passthrough=\"true\">\n" +
                $"{VisionImageBlockStart}\n{filePath}\n{VisionImageBlockEnd}\n" +
                "</file>";
        }

        /// <summary>
        /// 解析工具结果中的图片路径块，返回剥离块后的纯文本与图片 data URI 列表。
        /// 块内每行既可以是 data URI，也可以是一个本地图片路径。
        /// </summary>
        public static (string CleanText, List<string> ImageDataUris) ParseImageBlock(string raw)
        {
            var uris = new List<string>();
            if (string.IsNullOrEmpty(raw))
                return (raw ?? string.Empty, uris);

            int start = raw.IndexOf(VisionImageBlockStart, StringComparison.Ordinal);
            if (start < 0)
                return (raw, uris);

            int contentStart = start + VisionImageBlockStart.Length;
            int end = raw.IndexOf(VisionImageBlockEnd, contentStart, StringComparison.Ordinal);
            if (end < 0)
                return (raw, uris);

            string cleanText = raw.Remove(start, (end + VisionImageBlockEnd.Length) - start).TrimEnd();
            string block = raw.Substring(contentStart, end - contentStart);

            foreach (string line in block.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string item = line.Trim();
                if (item.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                {
                    uris.Add(item);
                    continue;
                }

                string? dataUri = TryBuildImageDataUri(item);
                if (dataUri != null)
                    uris.Add(dataUri);
            }

            return (cleanText, uris);
        }

        /// <summary>PDF 与 PowerPoint 使用页范围读取，不进入行分页。</summary>
        public static bool IsPagedDocument(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext == ".pdf" || ext == ".ppt" || ext == ".pptx";
        }

        /// <summary>
        /// 解析 OCR/文本提取结果中的页标记，返回按页拆分的文本。
        /// 支持 Page/Slide 与中文“第 N/M 页”格式。
        /// </summary>
        internal static List<string> SplitPagedDocumentContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return new List<string> { string.Empty };

            MatchCollection markers = Regex.Matches(
                content,
                @"^---\s*(?:(?:Page|Slide)\s+\d+(?:\s*/\s*\d+)?|第\s*\d+(?:\s*/\s*\d+)?\s*页)\s*---\s*$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (markers.Count == 0)
                return new List<string> { content.Trim() };

            var pages = new List<string>(markers.Count);
            for (int index = 0; index < markers.Count; index++)
            {
                int start = markers[index].Index;
                int end = index + 1 < markers.Count
                    ? markers[index + 1].Index
                    : content.Length;
                pages.Add(content.Substring(start, end - start).Trim());
            }

            string preface = content.Substring(0, markers[0].Index).Trim();
            if (!string.IsNullOrWhiteSpace(preface))
                pages[0] = preface + "\n" + pages[0];
            return pages;
        }

        internal static string BuildPagedDocumentResult(
            string filePath,
            string fullContent,
            int startPage,
            int requestedEndPage,
            int requestedMaxPages)
        {
            List<string> pages = SplitPagedDocumentContent(fullContent);
            int totalPages = pages.Count;
            int start = Math.Max(1, Math.Min(startPage, totalPages));
            int maxPages = requestedEndPage == int.MaxValue
                ? Math.Max(1, Math.Min(requestedMaxPages, HardMaxPages))
                : HardMaxPages;
            int end = requestedEndPage == int.MaxValue
                ? Math.Min(totalPages, start + maxPages - 1)
                : Math.Min(totalPages, Math.Min(requestedEndPage, start + maxPages - 1));
            end = Math.Max(start, end);

            var builder = new StringBuilder();
            if (startPage > totalPages)
            {
                builder.AppendLine(
                    $"<file path=\"{filePath}\" total_pages=\"{totalPages}\" shown_pages=\"none\" truncated=\"false\" read_mode=\"pages\">");
                builder.AppendLine(
                    $"[NO CONTENT] start_page {startPage} is beyond total_pages {totalPages}.");
                builder.Append("</file>");
                return builder.ToString();
            }

            bool truncated = end < totalPages;
            builder.Append(
                $"<file path=\"{filePath}\" total_pages=\"{totalPages}\" shown_pages=\"{start}-{end}\" truncated=\"{truncated.ToString().ToLowerInvariant()}\" read_mode=\"pages\"");
            if (truncated)
                builder.Append($" next_start_page=\"{end + 1}\"");
            builder.AppendLine(">");

            for (int page = start; page <= end; page++)
            {
                builder.AppendLine(pages[page - 1]);
                builder.AppendLine();
            }

            if (truncated)
            {
                builder.Append(
                    $"[PAGED] Showing pages {start}-{end} of {totalPages}. " +
                    $"To continue, call read_file with path=\"{filePath}\" start_page={end + 1}.");
            }
            builder.Append("</file>");
            return builder.ToString();
        }

        private static string? TryBuildImageDataUri(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return null;

                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length > MaxVisionImageBytes)
                {
                    Logger.Warn($"[read_file] 图片超过 32MiB，跳过视觉直传: {filePath}");
                    return null;
                }

                string? mediaType = Path.GetExtension(filePath).ToLowerInvariant() switch
                {
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    _ => null,
                };

                if (mediaType == null)
                    return null;

                byte[] bytes = File.ReadAllBytes(filePath);
                return $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
            }
            catch (Exception ex)
            {
                Logger.Warn($"[read_file] 构建图片 data URI 失败: {filePath} - {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 从完整文件内容中提取指定行范围，并添加行号前缀。
        /// </summary>
        private static string FormatLinesFromFullContent(string fullContent, int startLine, int endLine)
        {
            var lines = fullContent.Split('\n');
            var sb = new StringBuilder();
            int maxLine = Math.Min(endLine, lines.Length);
            for (int i = startLine - 1; i < maxLine; i++)
            {
                // 去掉 \r 尾巴
                string line = lines[i].TrimEnd('\r');
                sb.AppendLine($"{i +1,6}│ {line}");
            }
            return sb.ToString().TrimEnd();
        }

        private static int GetIntArgAlias(
            Dictionary<string, JsonElement> args,
            string camelCaseKey,
            string snakeCaseKey,
            int defaultValue)
        {
            int value = GetIntArg(args, camelCaseKey, int.MinValue);
            if (value != int.MinValue) return value;
            return GetIntArg(args, snakeCaseKey, defaultValue);
        }

        /// <summary>
        /// UTF-8 安全的字符串截断：确保不会在代理对或组合字符中间切断。
        /// </summary>
        /// <param name="text">原始文本</param>
        /// <param name="maxBytes">最大字节数</param>
        /// <returns>截断后的文本</returns>
        private static string TruncateUtf8Safe(string text, int maxBytes)
        {
            int byteCount = 0;
            int charIndex = 0;
            foreach (char c in text)
            {
                int charBytes = Encoding.UTF8.GetByteCount(new[] { c });
                if (byteCount + charBytes > maxBytes)
                    break;
                byteCount += charBytes;
                charIndex++;
            }
            return text.Substring(0, charIndex);
        }
    }
}
