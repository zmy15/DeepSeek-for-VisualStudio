using DeepSeek_v4_for_VisualStudio.Models;
using EnvDTE;
using DeepSeek_v4_for_VisualStudio.Utils;
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
    /// get_file_symbols 工具 — 使用 Visual Studio CodeModel 返回单个文件中的符号定义。
    /// CodeModel 由 VS 语言服务提供，不自行解析源码。
    /// </summary>
    public class FileSymbolsTool : BuiltInToolBase
    {
        private const int DefaultMaxResults = 500;
        private const int HardMaxResults = 2000;
        private const int MaxSignatureLength = 240;

        public override string Name => "get_file_symbols";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "get_file_symbols",
                    Description = L["tool.fileSymbols.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            filePath = new { type = "string", description = LocalizationService.Instance["tool.fileSymbols.param.filePath"] },
                            maxResults = new { type = "integer", description = LocalizationService.Instance["tool.fileSymbols.param.maxResults"] }
                        },
                        required = new[] { "filePath" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string filePath = GetStringArg(args, "filePath");
            string fileName = string.IsNullOrEmpty(filePath) ? "?" : Path.GetFileName(filePath);
            return LocalizationService.Instance.Format("tool.fileSymbols.displayText", fileName);
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            if (toolResult.StartsWith("Error: ")) return toolResult;
            return LocalizationService.Instance["tool.fileSymbols.complete"];
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string filePath = GetStringArg(args, "filePath");
            if (string.IsNullOrEmpty(filePath))
                return LocalizationService.Instance["tool.fileSymbols.missingParam"];

            filePath = ResolvePath(filePath, workspaceRoot);
            if (!File.Exists(filePath))
                return LocalizationService.Instance.Format("tool.fileSymbols.fileNotFound", Path.GetFileName(filePath));

            int maxResults = GetIntArg(args, "maxResults", DefaultMaxResults);
            maxResults = Math.Max(1, Math.Min(maxResults, HardMaxResults));

            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(CancellationToken);

                var dte = (DTE?)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
                if (dte?.Solution == null)
                    return LocalizationService.Instance.Format("tool.fileSymbols.codeModelFailed", "DTE/Solution unavailable");

                ProjectItem? projectItem = dte.Solution.FindProjectItem(filePath);
                if (projectItem == null)
                    return LocalizationService.Instance.Format("tool.fileSymbols.fileNotInProject", filePath);

                FileCodeModel? fileCodeModel = projectItem.FileCodeModel;
                if (fileCodeModel == null)
                    return LocalizationService.Instance.Format("tool.fileSymbols.codeModelUnavailable", filePath);

                var symbols = new List<SymbolInfo>();
                TraverseCodeElements(fileCodeModel.CodeElements, symbols, maxResults);

                return FormatOutput(filePath, workspaceRoot, symbols);
            }
            catch (Exception ex)
            {
                return LocalizationService.Instance.Format("tool.fileSymbols.codeModelFailed", ex.Message);
            }
        }

        private static void TraverseCodeElements(
            CodeElements elements,
            List<SymbolInfo> symbols,
            int maxResults)
        {
            if (symbols.Count >= maxResults)
                return;

            foreach (CodeElement element in elements)
            {
                if (symbols.Count >= maxResults)
                    return;

                try
                {
                    string? kind = MapKind(element.Kind.ToString());
                    if (kind != null)
                    {
                        int line = GetStartLine(element);
                        string signature = GetSignature(element);
                        string name = GetElementName(element);

                        symbols.Add(new SymbolInfo(kind, name, signature, line));
                    }

                    CodeElements? members = GetMembers(element);
                    if (members != null)
                        TraverseCodeElements(members, symbols, maxResults);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[FileSymbolsTool] 跳过无法读取的 CodeElement: {ex.Message}");
                }
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

        private static string GetSignature(CodeElement element)
        {
            try
            {
                TextPoint point = element.GetStartPoint(vsCMPart.vsCMPartWholeWithAttributes);
                return point.CreateEditPoint().GetLines(point.Line, point.Line + 1).Trim();
            }
            catch
            {
                return string.Empty;
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

        private static int GetStartLine(CodeElement element)
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
                "vsCMElementUnion" => "union",
                "vsCMElementDefine" => "define",
                _ => null,
            };
        }

        private static string FormatOutput(string filePath, string? workspaceRoot, List<SymbolInfo> symbols)
        {
            string displayPath = filePath;
            if (!string.IsNullOrEmpty(workspaceRoot))
            {
                string root = Path.GetFullPath(workspaceRoot);
                if (filePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    displayPath = filePath.Substring(root.Length).TrimStart('\\', '/');
            }

            var ordered = symbols
                .GroupBy(s => $"{s.Line}|{s.Kind}|{s.Name}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(s => s.Line)
                .ThenBy(s => s.Kind, StringComparer.Ordinal)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine(LocalizationService.Instance.Format("tool.fileSymbols.resultHeader", displayPath, ordered.Count));
            sb.AppendLine();

            if (ordered.Count == 0)
            {
                sb.AppendLine(LocalizationService.Instance["tool.fileSymbols.noMatches"]);
                return sb.ToString().TrimEnd();
            }

            sb.AppendLine($"| # | {LocalizationService.Instance["tool.fileSymbols.tableType"]} | {LocalizationService.Instance["tool.fileSymbols.tableName"]} | {LocalizationService.Instance["tool.fileSymbols.tableSignature"]} | {LocalizationService.Instance["tool.fileSymbols.tableLine"]} |");
            sb.AppendLine("|---|------|------|-----------|----|");

            for (int i = 0; i < ordered.Count; i++)
            {
                SymbolInfo symbol = ordered[i];
                string signature = TruncateText(symbol.Signature, MaxSignatureLength);
                sb.AppendLine($"| {i + 1} | {symbol.Kind} | `{EscapeMarkdown(symbol.Name)}` | `{EscapeMarkdown(signature)}` | {symbol.Line} |");
            }

            return sb.ToString().TrimEnd();
        }

        private static string EscapeMarkdown(string value)
        {
            return value.Replace("\\", "\\\\").Replace("|", "\\|").Replace("`", "\\`");
        }

        private sealed record SymbolInfo(string Kind, string Name, string Signature, int Line);
    }
}
