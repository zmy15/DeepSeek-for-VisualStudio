using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// get_file_comments 工具 — 使用 VS CodeModel 定位代码符号，只返回紧邻声明的文档注释。
    /// 不返回方法体或实现代码；没有可用注释时引导调用 read_file。
    /// </summary>
    public sealed class GetFileCommentsTool : BuiltInToolBase
    {
        private const int DefaultMaxResults = 200;
        private const int HardMaxResults = 1000;
        private const int MaxCommentChars = 6000;
        private const int MaxOutputChars = 48000;
        private const int MaxNameChars = 240;

        public override string Name => "get_file_comments";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = Name,
                    Description = L["tool.get_file_comments.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            filePath = new
                            {
                                type = "string",
                                description = L["tool.getFileComments.param.filePath"],
                            },
                            maxResults = new
                            {
                                type = "integer",
                                description = L["tool.getFileComments.param.maxResults"],
                            },
                            symbolName = new
                            {
                                type = "string",
                                description = L["tool.getFileComments.param.symbolName"],
                            },
                        },
                        required = new[] { "filePath" },
                    },
                },
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string filePath = GetStringArg(args, "filePath");
            string symbolName = NormalizeSymbolFilter(GetStringArg(args, "symbolName"));
            string fileName = string.IsNullOrEmpty(filePath) ? "?" : Path.GetFileName(filePath);
            return string.IsNullOrEmpty(symbolName)
                ? L.Format("tool.getFileComments.displayText", fileName)
                : L.Format("tool.getFileComments.displayTextWithSymbol", fileName, symbolName);
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrWhiteSpace(toolResult))
                return L["tool.common.noResult"];
            if (toolResult.StartsWith("Error: ", StringComparison.Ordinal)
                || toolResult.StartsWith("Timeout: ", StringComparison.Ordinal)
                || toolResult.StartsWith("[BLOCKED] ", StringComparison.Ordinal))
            {
                return toolResult;
            }

            if (toolResult.IndexOf("read_file", StringComparison.OrdinalIgnoreCase) >= 0)
                return L["tool.getFileComments.noCommentsSummary"];

            return L["tool.getFileComments.complete"];
        }

        public override async Task<string> ExecuteAsync(
            Dictionary<string, JsonElement> args,
            string? workspaceRoot)
        {
            string filePath = GetStringArg(args, "filePath");
            if (string.IsNullOrWhiteSpace(filePath))
                return L["tool.getFileComments.missingParam"];

            filePath = ResolvePath(filePath, workspaceRoot);
            if (!File.Exists(filePath))
                return L.Format("tool.getFileComments.fileNotFound", Path.GetFileName(filePath));

            int maxResults = GetIntArg(args, "maxResults", DefaultMaxResults);
            maxResults = Math.Max(1, Math.Min(maxResults, HardMaxResults));
            string symbolName = NormalizeSymbolFilter(GetStringArg(args, "symbolName"));

            string[] lines;
            try
            {
                lines = await Task.Run(
                    () => FileEncodingHelper.ReadAllLines(filePath),
                    CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return L.Format("tool.getFileComments.readFailed", ex.Message);
            }

            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(CancellationToken);

                var dte = (DTE?)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
                if (dte?.Solution == null)
                    return BuildNoCommentsResult(filePath);

                ProjectItem? projectItem = dte.Solution.FindProjectItem(filePath);
                if (projectItem == null)
                    return L.Format("tool.getFileComments.fileNotInProject", Path.GetFileName(filePath));

                FileCodeModel? fileCodeModel = projectItem.FileCodeModel;
                if (fileCodeModel == null)
                    return L.Format("tool.getFileComments.codeModelUnavailable", Path.GetFileName(filePath));

                var comments = new List<FileCommentEntry>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                TraverseCodeElements(
                    fileCodeModel.CodeElements,
                    lines,
                    comments,
                    seen,
                    maxResults,
                    symbolName);

                string displayPath = GetDisplayPath(filePath, workspaceRoot);
                if (comments.Count == 0)
                    return string.IsNullOrEmpty(symbolName)
                        ? BuildNoCommentsResult(displayPath)
                        : BuildSymbolNoCommentsResult(displayPath, symbolName);

                return FormatCommentsOutput(
                    displayPath,
                    comments,
                    comments.Count >= maxResults,
                    symbolName);
            }
            catch (Exception ex)
            {
                return L.Format("tool.getFileComments.codeModelFailed", ex.Message);
            }
        }

        private static void TraverseCodeElements(
            CodeElements elements,
            string[] lines,
            List<FileCommentEntry> comments,
            HashSet<string> seen,
            int maxResults,
            string symbolName)
        {
            if (comments.Count >= maxResults)
                return;

            foreach (CodeElement element in elements)
            {
                if (comments.Count >= maxResults)
                    return;

                try
                {
                    string? kind = MapKind(element.Kind.ToString());
                    if (kind != null)
                    {
                        string name = GetElementName(element);
                        if (MatchesSymbolName(element.Name, name, symbolName))
                        {
                            int declarationLine = GetDeclarationLine(element);
                            string? comment = ExtractAttachedComment(lines, declarationLine);
                            if (comment == null)
                            {
                                int rawStartLine = GetRawStartLine(element);
                                if (rawStartLine != declarationLine)
                                    comment = ExtractAttachedComment(lines, rawStartLine);
                                if (rawStartLine > 0)
                                    declarationLine = rawStartLine;
                            }

                            if (!string.IsNullOrWhiteSpace(comment))
                            {
                                string key = $"{declarationLine}|{kind}|{name}";
                                if (seen.Add(key))
                                {
                                    comments.Add(new FileCommentEntry(
                                        kind,
                                        TruncateText(name, MaxNameChars),
                                        declarationLine,
                                        comment));
                                }
                            }
                        }
                    }

                    CodeElements? members = GetMembers(element);
                    if (members != null)
                        TraverseCodeElements(
                            members,
                            lines,
                            comments,
                            seen,
                            maxResults,
                            symbolName);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[GetFileCommentsTool] 跳过无法读取的 CodeElement: {ex.Message}");
                }
            }
        }

        internal static string? ExtractAttachedComment(string[] lines, int declarationLine)
        {
            if (lines.Length == 0 || declarationLine <= 1)
                return null;

            int index = Math.Min(lines.Length - 1, declarationLine - 2);
            var rawLines = new List<string>();
            bool inBlockComment = false;

            for (; index >= 0; index--)
            {
                string raw = lines[index];
                string trimmed = raw.Trim();

                if (inBlockComment)
                {
                    rawLines.Add(raw);
                    if (trimmed.StartsWith("/*", StringComparison.Ordinal))
                        break;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(trimmed))
                    break;

                if (trimmed.EndsWith("*/", StringComparison.Ordinal))
                {
                    rawLines.Add(raw);
                    if (!trimmed.StartsWith("/*", StringComparison.Ordinal))
                        inBlockComment = true;
                    else
                        break;
                    continue;
                }

                if (IsLineComment(trimmed))
                {
                    rawLines.Add(raw);
                    continue;
                }

                break;
            }

            if (rawLines.Count == 0)
                return null;

            rawLines.Reverse();
            string normalized = NormalizeCommentBlock(rawLines);
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        internal static string FormatCommentsOutput(
            string displayPath,
            IReadOnlyList<FileCommentEntry> entries,
            bool reachedLimit = false,
            string? symbolName = null)
        {
            if (entries.Count == 0)
                return BuildNoCommentsResult(displayPath);

            var ordered = entries
                .OrderBy(e => e.Line)
                .ThenBy(e => e.Kind, StringComparer.Ordinal)
                .ThenBy(e => e.Name, StringComparer.Ordinal)
                .ToList();

            var body = new StringBuilder();
            int included = 0;
            bool truncated = reachedLimit;

            foreach (FileCommentEntry entry in ordered)
            {
                string comment = TruncateText(entry.Comment, MaxCommentChars);
                string section = $"### {entry.Kind} `{EscapeInlineCode(entry.Name)}` - line {entry.Line}"
                    + Environment.NewLine + Environment.NewLine
                    + comment
                    + Environment.NewLine + Environment.NewLine;

                if (body.Length + section.Length > MaxOutputChars)
                {
                    truncated = true;
                    break;
                }

                body.Append(section);
                included++;
            }

            var sb = new StringBuilder();
            sb.AppendLine(string.IsNullOrEmpty(symbolName)
                ? L.Format("tool.getFileComments.resultHeader", displayPath, included)
                : L.Format("tool.getFileComments.resultHeaderWithSymbol", displayPath, symbolName, included));
            sb.AppendLine();
            sb.Append(body);

            if (truncated)
            {
                sb.AppendLine();
                sb.AppendLine(L["tool.getFileComments.truncated"]);
            }

            return sb.ToString().TrimEnd();
        }

        internal static string BuildNoCommentsResult(string displayPath) =>
            L.Format("tool.getFileComments.noComments", displayPath);

        internal static string BuildSymbolNoCommentsResult(string displayPath, string symbolName) =>
            L.Format("tool.getFileComments.symbolNoComments", displayPath, symbolName);

        internal static string NormalizeSymbolFilter(string? symbolName)
        {
            string normalized = symbolName?.Trim() ?? string.Empty;
            int parenthesis = normalized.IndexOf('(');
            if (parenthesis > 0)
                normalized = normalized.Substring(0, parenthesis).Trim();
            return normalized.TrimEnd('.');
        }

        internal static bool MatchesSymbolName(
            string? simpleName,
            string? fullName,
            string? symbolFilter)
        {
            string filter = NormalizeSymbolFilter(symbolFilter);
            if (filter.Length == 0)
                return true;

            string simple = simpleName?.Trim() ?? string.Empty;
            string full = fullName?.Trim() ?? simple;
            return string.Equals(simple, filter, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, filter, StringComparison.OrdinalIgnoreCase)
                || full.EndsWith("." + filter, StringComparison.OrdinalIgnoreCase)
                || full.EndsWith("::" + filter, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeCommentBlock(IReadOnlyList<string> rawLines)
        {
            var normalized = new List<string>();
            bool previousWasBlank = false;

            foreach (string raw in rawLines)
            {
                string line = raw.Trim();
                if (line.StartsWith("///", StringComparison.Ordinal))
                    line = line.Substring(3);
                else if (line.StartsWith("//", StringComparison.Ordinal))
                    line = line.Substring(2);
                else if (line.StartsWith("'''", StringComparison.Ordinal))
                    line = line.Substring(3);
                else if (line.StartsWith("'", StringComparison.Ordinal))
                    line = line.Substring(1);
                else if (line.StartsWith("/**", StringComparison.Ordinal))
                    line = line.Substring(3);
                else if (line.StartsWith("/*", StringComparison.Ordinal))
                    line = line.Substring(2);

                if (line.EndsWith("*/", StringComparison.Ordinal))
                    line = line.Substring(0, line.Length - 2);

                line = line.Trim();
                if (line.StartsWith("*", StringComparison.Ordinal)
                    && (line.Length == 1 || char.IsWhiteSpace(line[1])))
                {
                    line = line.Substring(1).Trim();
                }

                if (line.Length == 0)
                {
                    if (!previousWasBlank && normalized.Count > 0)
                        normalized.Add(string.Empty);
                    previousWasBlank = true;
                    continue;
                }

                normalized.Add(line);
                previousWasBlank = false;
            }

            while (normalized.Count > 0 && string.IsNullOrWhiteSpace(normalized[normalized.Count - 1]))
                normalized.RemoveAt(normalized.Count - 1);

            return string.Join(Environment.NewLine, normalized);
        }

        private static bool IsLineComment(string trimmed)
        {
            return trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("'", StringComparison.Ordinal);
        }

        private static int GetDeclarationLine(CodeElement element)
        {
            try
            {
                return element.GetStartPoint(vsCMPart.vsCMPartWholeWithAttributes).Line;
            }
            catch
            {
                try { return element.StartPoint.Line; }
                catch { return 0; }
            }
        }

        private static string GetElementName(CodeElement element)
        {
            try
            {
                return string.IsNullOrWhiteSpace(element.FullName)
                    ? element.Name
                    : element.FullName;
            }
            catch
            {
                return element.Name;
            }
        }

        private static int GetRawStartLine(CodeElement element)
        {
            try
            {
                return element.StartPoint.Line;
            }
            catch
            {
                return 0;
            }
        }

        private static CodeElements? GetMembers(CodeElement element)
        {
            return element switch
            {
                CodeNamespace ns => ns.Members,
                CodeClass cls => cls.Members,
                CodeInterface iface => iface.Members,
                CodeStruct str => str.Members,
                CodeEnum en => en.Members,
                _ => null,
            };
        }

        private static string? MapKind(string vsKind)
        {
            return vsKind switch
            {
                "vsCMElementNamespace" => "namespace",
                "vsCMElementClass" => "class",
                "vsCMElementInterface" => "interface",
                "vsCMElementStruct" => "struct",
                "vsCMElementEnum" => "enum",
                "vsCMElementDelegate" => "delegate",
                "vsCMElementFunction" => "method",
                "vsCMElementProperty" => "property",
                "vsCMElementVariable" => "field",
                "vsCMElementEvent" => "event",
                _ => null,
            };
        }

        private static string GetDisplayPath(string filePath, string? workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot))
                return filePath;

            try
            {
                string root = Path.GetFullPath(workspaceRoot);
                if (filePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return filePath.Substring(root.Length).TrimStart('\\', '/');
            }
            catch
            {
            }

            return filePath;
        }

        private static string EscapeInlineCode(string value) =>
            value.Replace("`", "\\`");

        internal sealed record FileCommentEntry(
            string Kind,
            string Name,
            int Line,
            string Comment);
    }
}
