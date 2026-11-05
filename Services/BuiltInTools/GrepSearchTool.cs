using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
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
    /// grep_search 工具 — 正则/文本内容全局搜索。
    /// </summary>
    public class GrepSearchTool : BuiltInToolBase
    {
        /// <summary>排除目录（引用 SharedConstants）</summary>
        private static readonly HashSet<string> ExcludeDirs = SharedConstants.ExcludedDirectories;

        public override string Name => "grep_search";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "grep_search",
                    Description = L["tool.grep_search.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string", description = LocalizationService.Instance["tool.grepSearch.param.query"] },
                            isRegexp = new { type = "boolean", description = LocalizationService.Instance["tool.grepSearch.param.isRegexp"] },
                            path = new { type = "string", description = LocalizationService.Instance["tool.grepSearch.param.path"] },
                            includePattern = new { type = "string", description = LocalizationService.Instance["tool.grepSearch.param.includePattern"] },
                            maxResults = new { type = "integer", description = LocalizationService.Instance["tool.grepSearch.param.maxResults"] }
                        },
                        required = new[] { "query", "isRegexp" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string grepQuery = GetStringArg(args, "query");
            string incPattern = GetStringArg(args, "includePattern");
            string grepDesc = string.IsNullOrEmpty(grepQuery)
                ? LocalizationService.Instance["tool.grepSearch.searching"]
                : LocalizationService.Instance.Format("tool.grepSearch.searchingQuery", grepQuery);
            if (!string.IsNullOrEmpty(incPattern))
                grepDesc += LocalizationService.Instance.Format("tool.grepSearch.inPattern", incPattern);
            return grepDesc;
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            if (toolResult.StartsWith("Error: ")) return toolResult;

            var gsFirstLine = toolResult.Split('\n')[0];
            var gsMatch = Regex.Match(
                gsFirstLine, @"(?:找到|Found|found)\s*(\d+|>\d+)\s*(?:处|个)?\s*(?:匹配|matches?)",
                RegexOptions.IgnoreCase);
            if (gsMatch.Success)
                return $" {gsMatch.Value.Trim()}";
            return LocalizationService.Instance["tool.grepSearch.complete"];
        }

        public override Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            workspaceRoot = NormalizeWorkspaceRoot(workspaceRoot);
            string query = GetStringArg(args, "query");
            if (string.IsNullOrEmpty(query))
                return Task.FromResult(LocalizationService.Instance["tool.grepSearch.missingQuery"]);

            bool isRegexp = GetBoolArg(args, "isRegexp");
            string? includePattern = GetStringArg(args, "includePattern");
            string? scopePath = GetStringArg(args, "path");
            int maxResults = Math.Max(1, GetIntArg(args, "maxResults", 30));

            string searchRoot = workspaceRoot ?? Directory.GetCurrentDirectory();
            if (!Directory.Exists(searchRoot))
                return Task.FromResult(LocalizationService.Instance.Format("tool.grepSearch.workspaceNotExist", searchRoot));

            try
            {
                var results = new List<string>();
                var searchOption = SearchOption.AllDirectories;
                string searchDir = searchRoot;
                string? scopedFile = null;

                if (!string.IsNullOrWhiteSpace(scopePath))
                {
                    // 与 read_file 保持一致：工具层接受工作区外的绝对路径；
                    // 是否允许越界由 BaseAgent 的统一权限审批层决定。
                    string resolvedScope = Path.GetFullPath(ResolvePath(scopePath, workspaceRoot));

                    if (File.Exists(resolvedScope))
                    {
                        scopedFile = resolvedScope;
                        searchDir = Path.GetDirectoryName(resolvedScope) ?? searchRoot;
                    }
                    else if (Directory.Exists(resolvedScope))
                    {
                        searchDir = resolvedScope;
                    }
                    else
                    {
                        return Task.FromResult(LocalizationService.Instance.Format(
                            "tool.grepSearch.pathNotExist", scopePath));
                    }
                }

                string fileGlob = string.IsNullOrEmpty(includePattern) ? "*.*" : includePattern;
                string cleanGlob = fileGlob.Replace("**/", "").Replace("**", "");

                if (scopedFile == null && (cleanGlob.Contains('/') || cleanGlob.Contains('\\')))
                {
                    int lastSlash = cleanGlob.LastIndexOfAny(new[] { '/', '\\' });
                    string subDir = cleanGlob.Substring(0, lastSlash);
                    cleanGlob = cleanGlob.Substring(lastSlash + 1);
                    string candidateDir = Path.Combine(searchDir, subDir);
                    string resolvedDir = Path.GetFullPath(candidateDir);
                    if (IsWithinDirectory(resolvedDir, Path.GetFullPath(searchDir)) && Directory.Exists(resolvedDir))
                    {
                        searchDir = resolvedDir;
                    }
                }
                if (string.IsNullOrEmpty(cleanGlob))
                    cleanGlob = "*.*";

                var filePatterns = ExpandBraceGlob(cleanGlob);
                if (filePatterns.Count == 0
                    || filePatterns.Any(p => p.IndexOfAny(new[] { '{', '}' }) >= 0))
                {
                    return Task.FromResult(LocalizationService.Instance.Format(
                        "tool.grepSearch.invalidIncludePattern",
                        includePattern ?? cleanGlob,
                        string.Empty));
                }

                const int maxFilesToSearch = 5000;
                IReadOnlyList<string> filesToSearch;
                if (scopedFile != null)
                {
                    filesToSearch = new[] { scopedFile };
                }
                else
                {
                    try
                    {
                        var matchedFiles = new List<string>();
                        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (string pattern in filePatterns)
                        {
                            foreach (string file in Directory.EnumerateFiles(searchDir, pattern, searchOption))
                            {
                                if (IsExcludedDirectory(Path.GetDirectoryName(file) ?? ""))
                                    continue;
                                if (seenFiles.Add(file))
                                    matchedFiles.Add(file);
                                if (matchedFiles.Count >= maxFilesToSearch)
                                    break;
                            }
                            if (matchedFiles.Count >= maxFilesToSearch)
                                break;
                        }
                        filesToSearch = matchedFiles;
                    }
                    catch (Exception ex)
                    {
                        return Task.FromResult(LocalizationService.Instance.Format(
                            "tool.grepSearch.invalidIncludePattern",
                            includePattern ?? cleanGlob,
                            ex.Message));
                    }
                }

                Regex? regex = null;
                if (isRegexp)
                {
                    try { regex = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100)); }
                    catch { return Task.FromResult(LocalizationService.Instance.Format("tool.grepSearch.invalidRegex", query)); }
                }

                foreach (var file in filesToSearch)
                {
                    if (results.Count > maxResults) break;

                    try
                    {
                        var lines = FileEncodingHelper.ReadAllLines(file);
                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (results.Count > maxResults) break;

                            bool match;
                            if (isRegexp && regex != null)
                                match = regex.IsMatch(lines[i]);
                            else
                                match = lines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

                            if (match)
                            {
                                string relativePath = file;
                                if (file.StartsWith(searchRoot, StringComparison.OrdinalIgnoreCase))
                                    relativePath = file.Substring(searchRoot.Length).TrimStart('\\', '/');
                                results.Add($"{relativePath}:{i + 1}: {lines[i].Trim()}");
                                if (results.Count > maxResults)
                                    break;
                            }
                        }
                    }
                    catch { /* 跳过无法读取的文件 */ }
                }

                bool truncated = results.Count > maxResults;
                if (truncated)
                    results.RemoveRange(maxResults, results.Count - maxResults);

                var sb = new StringBuilder();
                string resultCount = truncated ? $">{maxResults}" : results.Count.ToString();
                sb.AppendLine(LocalizationService.Instance.Format("tool.grepSearch.resultHeader", query, resultCount));
                sb.AppendLine();
                if (results.Count == 0)
                    sb.AppendLine(LocalizationService.Instance["tool.grepSearch.noMatches"]);
                else
                {
                    foreach (var r in results)
                        sb.AppendLine($"- `{r}`");
                    if (truncated)
                    {
                        sb.AppendLine();
                        sb.AppendLine(LocalizationService.Instance.Format(
                            "tool.grepSearch.truncatedNotice", maxResults));
                    }
                }

                return Task.FromResult(sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return Task.FromResult(LocalizationService.Instance.Format("tool.grepSearch.failed", ex.Message));
            }
        }

        private static bool IsWithinDirectory(string path, string directory)
        {
            string normalizedPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedDirectory = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.Equals(normalizedPath, normalizedDirectory, StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith(
                    normalizedDirectory + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 展开 common glob 中的花括号候选，例如 **/*.{cs,csproj}。
        /// 支持多层组合；不合法或未闭合的花括号由调用方明确报错。
        /// </summary>
        internal static IReadOnlyList<string> ExpandBraceGlob(string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
                return new[] { pattern ?? string.Empty };

            var current = new List<string> { pattern };
            for (int depth = 0; depth < 8; depth++)
            {
                var next = new List<string>();
                bool expanded = false;

                foreach (string candidate in current)
                {
                    Match match = Regex.Match(candidate, @"\{([^{}]*)\}");
                    if (!match.Success)
                    {
                        next.Add(candidate);
                        continue;
                    }

                    string[] choices = match.Groups[1].Value
                        .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(c => c.Trim())
                        .Where(c => c.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (choices.Length == 0)
                    {
                        next.Add(candidate);
                        continue;
                    }

                    expanded = true;
                    foreach (string choice in choices)
                    {
                        next.Add(
                            candidate.Substring(0, match.Index)
                            + choice
                            + candidate.Substring(match.Index + match.Length));
                    }
                }

                current = next;
                if (!expanded)
                    break;
            }

            return current
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// 检查目录是否属于排除列表（bin, obj, node_modules 等）。
        /// 提取为独立方法，供枚举阶段（.Take 之前）过滤使用。
        /// </summary>
        private static bool IsExcludedDirectory(string dirName)
        {
            if (string.IsNullOrEmpty(dirName)) return false;
            return ExcludeDirs.Any(d =>
                dirName.IndexOf(Path.DirectorySeparatorChar + d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0
                || dirName.EndsWith(Path.DirectorySeparatorChar + d, StringComparison.OrdinalIgnoreCase)
                || dirName.Equals(d, StringComparison.OrdinalIgnoreCase));
        }
    }
}
