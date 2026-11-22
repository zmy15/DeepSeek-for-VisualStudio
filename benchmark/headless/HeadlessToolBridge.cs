using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BenchmarkRunner
{
    /// <summary>
    /// Benchmark-owned tool implementations. This class is intentionally outside
    /// the Visual Studio extension source tree.
    /// </summary>
    internal sealed partial class HeadlessToolBridge
    {
        private static readonly HashSet<string> OverrideNames = new(
            new[]
            {
                "get_errors",
                "symbol_search",
                "get_file_symbols",
                "get_file_comments",
                "read_file",
                "create_directory",
                "delete_file",
                "create_file",
                "replace_string_in_file",
                "multi_replace_string_in_file",
                "apply_patch",
                "run_in_terminal",
                "get_terminal_output",
                "fetch_webpage",
            },
            StringComparer.OrdinalIgnoreCase);

        private readonly string _workspaceRoot;
        private readonly HeadlessBuildService _buildService;

        public HeadlessToolBridge(
            string workspaceRoot,
            HeadlessBuildService buildService)
        {
            _workspaceRoot = Path.GetFullPath(workspaceRoot);
            _buildService = buildService;
        }

        public IReadOnlyList<string> ToolNames => OverrideNames
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        public bool Handles(string toolName)
            => OverrideNames.Contains(toolName);

        public Task<string> ExecuteAsync(
            string toolName,
            Dictionary<string, object> args,
            CancellationToken cancellationToken)
        {
            return toolName.ToLowerInvariant() switch
            {
                "get_errors" => ExecuteGetErrorsAsync(args, cancellationToken),
                "symbol_search" => ExecuteSymbolSearchAsync(args, cancellationToken),
                "get_file_symbols" => ExecuteFileSymbolsAsync(args, cancellationToken),
                "get_file_comments" => ExecuteFileCommentsAsync(args, cancellationToken),
                "read_file" => ExecuteReadFileAsync(args, cancellationToken),
                "create_directory" => ExecuteCreateDirectoryAsync(args, cancellationToken),
                "delete_file" => ExecuteDeleteFileAsync(args, cancellationToken),
                "create_file" => ExecuteCreateFileAsync(args, cancellationToken),
                "replace_string_in_file" => ExecuteReplaceStringAsync(args, cancellationToken),
                "multi_replace_string_in_file" => ExecuteMultiReplaceStringAsync(args, cancellationToken),
                "apply_patch" => ExecuteApplyPatchAsync(args, cancellationToken),
                "run_in_terminal" => ExecuteTerminalAsync(args, cancellationToken),
                "get_terminal_output" => ExecuteGetTerminalOutputAsync(args, cancellationToken),
                "fetch_webpage" => Task.FromResult(
                    "Error: fetch_webpage is disabled in the headless benchmark."),
                _ => Task.FromResult($"Error: unknown benchmark tool: {toolName}"),
            };
        }

        private string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;
            string resolved = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(
                    _workspaceRoot,
                    path.Replace('/', Path.DirectorySeparatorChar)));
            string root = _workspaceRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (!string.Equals(resolved, root, StringComparison.OrdinalIgnoreCase)
                && !resolved.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Path escapes the benchmark workspace: {path}");
            }
            return resolved;
        }

        private static string GetString(
            Dictionary<string, object> args,
            string key)
        {
            if (!args.TryGetValue(key, out object? value) || value == null)
                return string.Empty;
            if (value is JsonElement element)
            {
                return element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString() ?? string.Empty,
                    JsonValueKind.Null => string.Empty,
                    _ => element.ToString(),
                };
            }
            return Convert.ToString(value) ?? string.Empty;
        }

        private static int GetInt(
            Dictionary<string, object> args,
            string key,
            int defaultValue)
        {
            if (!args.TryGetValue(key, out object? value) || value == null)
                return defaultValue;
            if (value is JsonElement element
                && element.ValueKind == JsonValueKind.Number
                && element.TryGetInt32(out int parsed))
            {
                return parsed;
            }
            return int.TryParse(Convert.ToString(value), out parsed)
                ? parsed
                : defaultValue;
        }

        private static bool GetBool(
            Dictionary<string, object> args,
            string key,
            bool defaultValue = false)
        {
            if (!args.TryGetValue(key, out object? value) || value == null)
                return defaultValue;
            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.True)
                    return true;
                if (element.ValueKind == JsonValueKind.False)
                    return false;
            }
            return bool.TryParse(Convert.ToString(value), out bool parsed)
                ? parsed
                : defaultValue;
        }

        private static IEnumerable<JsonElement> GetArray(
            Dictionary<string, object> args,
            string key)
        {
            if (!args.TryGetValue(key, out object? value)
                || value is not JsonElement element
                || element.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<JsonElement>();
            }
            return element.EnumerateArray().ToArray();
        }

        private static string EscapeCell(string? value)
        {
            return (value ?? string.Empty)
                .Replace("|", "\\|")
                .Replace("\r\n", "<br>")
                .Replace("\r", "<br>")
                .Replace("\n", "<br>");
        }

        private string RelativePath(string path)
        {
            string root = _workspaceRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(root.Length)
                : fullPath;
        }
    }
}
