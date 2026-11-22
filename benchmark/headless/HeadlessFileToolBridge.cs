using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.EditTools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ProductionPatchTool = DeepSeek_v4_for_VisualStudio.Services.EditTools.ApplyPatchTool;

namespace BenchmarkRunner
{
    internal sealed partial class HeadlessToolBridge
    {
        private static readonly MethodInfo ApplySinglePatchMethod =
            typeof(ProductionPatchTool).GetMethod(
                "ApplySinglePatch",
                BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "ApplySinglePatch was not found.");

        private async Task<string> ExecuteGetErrorsAsync(
            Dictionary<string, object> args,
            CancellationToken cancellationToken)
        {
            List<ErrorListItem> items =
                await _buildService.GetAllErrorsAsync(cancellationToken);
            var requestedFiles = GetArray(args, "filePaths")
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => ResolvePath(item.GetString() ?? string.Empty))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<ErrorListItem> errors = items
                .Where(item => !string.Equals(
                    item.Category,
                    "warning",
                    StringComparison.OrdinalIgnoreCase))
                .Where(item => requestedFiles.Count == 0
                    || (!string.IsNullOrWhiteSpace(item.FileName)
                        && requestedFiles.Contains(Path.GetFullPath(item.FileName))))
                .ToList();
            if (errors.Count == 0)
                return "Build error check: no errors detected.";

            var builder = new StringBuilder();
            builder.AppendLine($"Build error check: found {errors.Count} error(s).");
            builder.AppendLine();
            builder.AppendLine("| # | Description | File | Line | Column | Code |");
            builder.AppendLine("|---|---|---|---|---|---|");
            for (int index = 0; index < errors.Count; index++)
            {
                ErrorListItem error = errors[index];
                builder.AppendLine(
                    $"| {index + 1} | {EscapeCell(error.Description)} | "
                    + $"{EscapeCell(Path.GetFileName(error.FileName ?? "-"))} | "
                    + $"{error.Line} | {error.Column} | "
                    + $"{EscapeCell(error.ErrorCode ?? "-")} |");
            }
            return builder.ToString().TrimEnd();
        }

        private Task<string> ExecuteReadFileAsync(
            Dictionary<string, object> args,
            CancellationToken cancellationToken)
        {
            string path = ResolvePath(GetString(args, "filePath"));
            if (!File.Exists(path))
                return Task.FromResult($"Error: file not found: {path}");

            int start = Math.Max(1, GetInt(args, "startLine", 1));
            int end = GetInt(args, "endLine", int.MaxValue);
            string[] lines = File.ReadAllLines(path);
            int actualEnd = Math.Min(lines.Length, end);
            var builder = new StringBuilder();
            builder.AppendLine($"File: {path}");
            builder.AppendLine($"Lines: {start}-{actualEnd} of {lines.Length}");
            builder.AppendLine();
            for (int index = start - 1; index < actualEnd; index++)
                builder.AppendLine($"{index + 1,6}| {lines[index]}");
            return Task.FromResult(builder.ToString().TrimEnd());
        }

        private Task<string> ExecuteCreateDirectoryAsync(
            Dictionary<string, object> args,
            CancellationToken cancellationToken)
        {
            string path = ResolvePath(GetString(args, "dirPath"));
            if (string.IsNullOrWhiteSpace(path))
                return Task.FromResult("Error: create_directory: missing dirPath");
            Directory.CreateDirectory(path);
            return Task.FromResult($"Created directory: {path}");
        }

        private Task<string> ExecuteCreateFileAsync(
            Dictionary<string, object> args,
            CancellationToken cancellationToken)
        {
            string path = ResolvePath(GetString(args, "filePath"));
            if (string.IsNullOrWhiteSpace(path))
                return Task.FromResult("Error: create_file: missing filePath");
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(
                path,
                GetString(args, "content"),
                new UTF8Encoding(false));
            return Task.FromResult($"Created file: {path}");
        }

        private Task<string> ExecuteDeleteFileAsync(
            Dictionary<string, object> args,
            CancellationToken cancellationToken)
        {
            string path = ResolvePath(GetString(args, "filePath"));
            if (!File.Exists(path))
                return Task.FromResult($"Error: file not found: {path}");
            File.Delete(path);
            return Task.FromResult($"Deleted file: {path}");
        }

        private Task<string> ExecuteReplaceStringAsync(
            Dictionary<string, object> args,
            CancellationToken cancellationToken)
        {
            string path = ResolvePath(GetString(args, "filePath"));
            string oldValue = GetString(args, "oldString");
            string newValue = GetString(args, "newString");
            return Task.FromResult(ApplySingleReplacement(path, oldValue, newValue));
        }

        private Task<string> ExecuteMultiReplaceStringAsync(
            Dictionary<string, object> args,
            CancellationToken cancellationToken)
        {
            var replacements = GetArray(args, "replacements").ToList();
            if (replacements.Count == 0)
                return Task.FromResult("Error: multi_replace: no replacements");

            string? targetPath = null;
            foreach (JsonElement replacement in replacements)
            {
                if (replacement.ValueKind != JsonValueKind.Object)
                    return Task.FromResult("Error: multi_replace: invalid item");
                string path = ResolvePath(GetElementString(replacement, "filePath"));
                if (targetPath == null)
                    targetPath = path;
                else if (!string.Equals(targetPath, path, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult("Error: multi_replace requires one target file");
            }

            foreach (JsonElement replacement in replacements)
            {
                string result = ApplySingleReplacement(
                    targetPath!,
                    GetElementString(replacement, "oldString"),
                    GetElementString(replacement, "newString"));
                if (result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(result);
            }
            return Task.FromResult(
                $"multi_replace_string_in_file: success {replacements.Count}, fail 0");
        }

        private Task<string> ExecuteApplyPatchAsync(
            Dictionary<string, object> args,
            CancellationToken cancellationToken)
        {
            string patchText = GetString(args, "patch");
            if (string.IsNullOrWhiteSpace(patchText))
                return Task.FromResult("Error: apply_patch: missing patch");

            List<PatchOperation> patches;
            try { patches = ProductionPatchTool.ParsePatches(patchText); }
            catch (Exception ex)
            {
                return Task.FromResult($"Error: apply_patch parse failed: {ex.Message}");
            }
            if (patches.Count == 0)
                return Task.FromResult("Error: apply_patch: no patch block");

            string expected = GetString(args, "expected").Trim();
            string[] anchors = expected
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => value.StartsWith("@", StringComparison.Ordinal))
                .Select(value => value.Substring(1).Trim())
                .ToArray();

            foreach (PatchOperation patch in patches)
            {
                if (anchors.Length == 1)
                {
                    foreach (PatchHunk hunk in patch.Hunks)
                        hunk.ExpectedAnchor = anchors[0];
                }
                else if (anchors.Length == patch.Hunks.Count)
                {
                    for (int index = 0; index < patch.Hunks.Count; index++)
                        patch.Hunks[index].ExpectedAnchor = anchors[index];
                }

                string target = ResolvePath(
                    string.IsNullOrWhiteSpace(patch.MoveToPath)
                        ? patch.FilePath
                        : patch.MoveToPath);
                if (patch.Action == PatchFileAction.Delete)
                {
                    if (File.Exists(target))
                        File.Delete(target);
                    continue;
                }

                string current = File.Exists(target)
                    ? File.ReadAllText(target)
                    : string.Empty;
                EditApplyResult result;
                try
                {
                    result = (EditApplyResult)ApplySinglePatchMethod.Invoke(
                        null,
                        new object[] { patch, target, current })!;
                }
                catch (TargetInvocationException ex)
                {
                    return Task.FromResult(
                        $"Error: apply_patch failed: {ex.InnerException?.Message ?? ex.Message}");
                }
                if (!result.Success)
                    return Task.FromResult(
                        $"Error: apply_patch failed: {result.ErrorMessage ?? "hunk mismatch"}");
                if (result.FinalContent != null)
                {
                    string? directory = Path.GetDirectoryName(target);
                    if (!string.IsNullOrWhiteSpace(directory))
                        Directory.CreateDirectory(directory);
                    File.WriteAllText(
                        target,
                        result.FinalContent,
                        new UTF8Encoding(false));
                }
            }
            return Task.FromResult("Patch applied and verified.");
        }

        private Task<string> ExecuteSymbolSearchAsync(
            Dictionary<string, object> args,
            CancellationToken cancellationToken)
        {
            string query = GetString(args, "query");
            if (string.IsNullOrWhiteSpace(query))
                return Task.FromResult("Error: symbol_search: missing query");
            int maxResults = Math.Max(1, GetInt(args, "maxResults", 30));
            var builder = new StringBuilder();
            builder.AppendLine($"Search \"{query}\"");
            builder.AppendLine();
            builder.AppendLine("| # | Kind | Name | File | Line | Signature |");
            builder.AppendLine("|---|---|---|---|---|---|");
            int count = 0;
            foreach (string file in HeadlessSourceIndex.EnumerateSourceFiles(_workspaceRoot))
            {
                foreach (HeadlessSourceSymbol symbol in HeadlessSourceIndex.ScanFile(file))
                {
                    if (!HeadlessSourceIndex.MatchesSymbol(symbol.Name, query))
                        continue;
                    count++;
                    builder.AppendLine(
                        $"| {count} | {symbol.Kind} | `{EscapeCell(symbol.Name)}` | "
                        + $"`{EscapeCell(RelativePath(file))}` | "
                        + $"{symbol.Line} | `{EscapeCell(symbol.Signature)}` |");
                    if (count >= maxResults)
                        break;
                }
                if (count >= maxResults)
                    break;
            }
            if (count == 0)
                builder.AppendLine("(No matching symbols found.)");
            return Task.FromResult(builder.ToString().TrimEnd());
        }

        private Task<string> ExecuteFileSymbolsAsync(
            Dictionary<string, object> args,
            CancellationToken cancellationToken)
        {
            string path = ResolvePath(GetString(args, "filePath"));
            if (!File.Exists(path))
                return Task.FromResult($"Error: file not found: {path}");
            int maxResults = Math.Max(1, GetInt(args, "maxResults", 500));
            List<HeadlessSourceSymbol> symbols =
                HeadlessSourceIndex.ScanFile(path, maxResults);
            var builder = new StringBuilder();
            builder.AppendLine(
                $"File symbols: `{RelativePath(path)}` - {symbols.Count} definition(s)");
            builder.AppendLine();
            builder.AppendLine("| # | Kind | Name | Signature | Line |");
            builder.AppendLine("|---|---|---|---|---|");
            for (int index = 0; index < symbols.Count; index++)
            {
                HeadlessSourceSymbol symbol = symbols[index];
                builder.AppendLine(
                    $"| {index + 1} | {symbol.Kind} | `{EscapeCell(symbol.Name)}` | "
                    + $"`{EscapeCell(symbol.Signature)}` | {symbol.Line} |");
            }
            if (symbols.Count == 0)
                builder.AppendLine("(No symbol definitions recognized.)");
            return Task.FromResult(builder.ToString().TrimEnd());
        }

        private Task<string> ExecuteFileCommentsAsync(
            Dictionary<string, object> args,
            CancellationToken cancellationToken)
        {
            string path = ResolvePath(GetString(args, "filePath"));
            if (!File.Exists(path))
                return Task.FromResult($"Error: file not found: {path}");
            string filter = GetString(args, "symbolName");
            int maxResults = Math.Max(1, GetInt(args, "maxResults", 200));
            string[] lines = File.ReadAllLines(path);
            List<HeadlessSourceSymbol> symbols =
                HeadlessSourceIndex.ScanLines(path, lines, maxResults * 4);
            var entries = new List<(HeadlessSourceSymbol Symbol, string Comment)>();
            foreach (HeadlessSourceSymbol symbol in symbols)
            {
                if (!HeadlessSourceIndex.MatchesSymbol(symbol.Name, filter))
                    continue;
                string? comment = HeadlessSourceIndex.ExtractAttachedComment(
                    lines,
                    symbol.Line);
                if (string.IsNullOrWhiteSpace(comment))
                    continue;
                entries.Add((symbol, comment));
                if (entries.Count >= maxResults)
                    break;
            }
            if (entries.Count == 0)
                return Task.FromResult(
                    $"No documentation comments found. Use read_file for `{path}`.");
            var builder = new StringBuilder();
            builder.AppendLine(
                $"Code symbol comments: `{RelativePath(path)}` - {entries.Count} symbol(s)");
            builder.AppendLine();
            foreach ((HeadlessSourceSymbol symbol, string comment) in entries)
            {
                builder.AppendLine(
                    $"### {symbol.Kind} `{symbol.Name}` - line {symbol.Line}");
                builder.AppendLine();
                builder.AppendLine(comment);
                builder.AppendLine();
            }
            return Task.FromResult(builder.ToString().TrimEnd());
        }

        private static string ApplySingleReplacement(
            string path,
            string oldValue,
            string newValue)
        {
            if (!File.Exists(path))
                return $"Error: file not found: {path}";
            if (string.IsNullOrEmpty(oldValue))
                return "Error: replace_string_in_file: missing oldString";
            string content = File.ReadAllText(path);
            int first = content.IndexOf(oldValue, StringComparison.Ordinal);
            if (first < 0)
                return $"Error: replacement text not found in {Path.GetFileName(path)}";
            int second = content.IndexOf(
                oldValue,
                first + oldValue.Length,
                StringComparison.Ordinal);
            if (second >= 0)
                return $"Error: replacement text occurs more than once in {Path.GetFileName(path)}";
            string updated = content.Substring(0, first)
                + newValue
                + content.Substring(first + oldValue.Length);
            File.WriteAllText(path, updated, new UTF8Encoding(false));
            return $"Replaced: {Path.GetFileName(path)}";
        }

        private static string GetElementString(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out JsonElement value))
                return string.Empty;
            return value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : value.ToString();
        }
    }
}
