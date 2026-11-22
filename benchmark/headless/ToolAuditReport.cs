using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BenchmarkRunner
{
    internal enum ToolAuditStatus
    {
        Works,
        HeadlessAdapted,
        RequiresVisualStudio,
        RequiresModel,
        RequiresInteractiveDesktop,
        Disabled,
        Failed,
    }

    internal sealed class ToolAuditResult
    {
        public string Tool { get; set; } = string.Empty;
        public ToolAuditStatus Status { get; set; }
        public string Summary { get; set; } = string.Empty;
        public string Evidence { get; set; } = string.Empty;
        public long DurationMs { get; set; }
    }

    internal static class ToolAuditReport
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static bool HasFailures(IEnumerable<ToolAuditResult> results)
            => results.Any(result => result.Status == ToolAuditStatus.Failed);

        public static string RenderMarkdown(
            string sourceWorkspace,
            string auditWorkspace,
            IReadOnlyList<ToolAuditResult> results)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# Headless tool audit");
            builder.AppendLine();
            builder.AppendLine($"- Source workspace: `{sourceWorkspace}`");
            builder.AppendLine($"- Isolated copy: `{auditWorkspace}`");
            builder.AppendLine($"- Tools checked: {results.Count}");
            builder.AppendLine();
            builder.AppendLine("| Tool | Status | Result | Evidence |");
            builder.AppendLine("|---|---|---|---|");
            foreach (ToolAuditResult result in results.OrderBy(
                item => item.Tool,
                StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine(
                    $"| `{Escape(result.Tool)}` | `{ToLabel(result.Status)}` | "
                    + $"{Escape(result.Summary)} | {Escape(result.Evidence)} |");
            }

            builder.AppendLine();
            builder.AppendLine("Status legend:");
            builder.AppendLine("- `works`: executed through its normal backend.");
            builder.AppendLine("- `headless_adapted`: executed through a CLI/source fallback.");
            builder.AppendLine("- `requires_vs`: intentionally not executed because the capability needs Visual Studio.");
            builder.AppendLine("- `requires_model`: dispatch path exists, but a model-backed subagent is required.");
            builder.AppendLine("- `requires_desktop`: needs an interactive desktop or browser.");
            builder.AppendLine("- `disabled`: not registered because its backend/configuration is absent.");
            builder.AppendLine("- `failed`: executed and returned an unexpected error.");
            return builder.ToString().TrimEnd();
        }

        public static void WriteJson(
            string path,
            string sourceWorkspace,
            string auditWorkspace,
            IReadOnlyList<ToolAuditResult> results)
        {
            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(
                fullPath,
                JsonSerializer.Serialize(
                    new
                    {
                        schema = "deepseek-vs.headless-tool-audit/1",
                        generated_at_utc = DateTime.UtcNow.ToString("O"),
                        source_workspace = sourceWorkspace,
                        audit_workspace = auditWorkspace,
                        tools = results
                            .OrderBy(item => item.Tool, StringComparer.OrdinalIgnoreCase)
                            .Select(item => new
                            {
                                tool = item.Tool,
                                status = ToLabel(item.Status),
                                summary = item.Summary,
                                evidence = item.Evidence,
                                duration_ms = item.DurationMs,
                            })
                            .ToArray(),
                    },
                    JsonOptions),
                new UTF8Encoding(false));
        }

        private static string ToLabel(ToolAuditStatus status)
        {
            return status switch
            {
                ToolAuditStatus.Works => "works",
                ToolAuditStatus.HeadlessAdapted => "headless_adapted",
                ToolAuditStatus.RequiresVisualStudio => "requires_vs",
                ToolAuditStatus.RequiresModel => "requires_model",
                ToolAuditStatus.RequiresInteractiveDesktop => "requires_desktop",
                ToolAuditStatus.Disabled => "disabled",
                _ => "failed",
            };
        }

        private static string Escape(string? value)
        {
            return (value ?? string.Empty)
                .Replace("|", "\\|")
                .Replace("\r\n", "<br>")
                .Replace("\r", "<br>")
                .Replace("\n", "<br>");
        }
    }
}
