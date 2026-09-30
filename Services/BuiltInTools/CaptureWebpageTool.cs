using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// capture_webpage tool - renders a URL in an off-screen browser and saves the
    /// result as a PNG for the vision model to inspect.
    ///
    /// Complements fetch_webpage: that tool returns markup/text, this one returns what
    /// the page actually looks like after layout and scripting.
    ///
    /// The result text ends with a [CAPTURE_IMAGE]...[/CAPTURE_IMAGE] block holding the
    /// local PNG path. BaseAgent parses that block, strips it from the text, and - when a
    /// vision model is active - forwards the image as a data URI. Non-vision models never
    /// see this tool at all (see BuiltInToolService.IsToolAvailableForCurrentModel).
    /// </summary>
    public class CaptureWebpageTool : BuiltInToolBase
    {
        public override string Name => "capture_webpage";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "capture_webpage",
                    Description = L["tool.capture_webpage.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            url = new
                            {
                                type = "string",
                                description = L["tool.captureWebpage.param.url"]
                            },
                            full_page = new
                            {
                                type = "boolean",
                                description = L["tool.captureWebpage.param.fullPage"]
                            },
                            viewport_width = new
                            {
                                type = "integer",
                                description = L["tool.captureWebpage.param.viewportWidth"]
                            },
                            max_width = new
                            {
                                type = "integer",
                                description = L["tool.captureWebpage.param.maxWidth"]
                            },
                            save_path = new
                            {
                                type = "string",
                                description = L["tool.captureWebpage.param.savePath"]
                            }
                        },
                        required = new[] { "url" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string url = GetStringArg(args, "url");
            return string.IsNullOrWhiteSpace(url)
                ? L["tool.captureWebpage.capturing"]
                : L.Format("tool.captureWebpage.capturingUrl", TruncateText(url, 80));
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return L["tool.common.noResult"];
            if (toolResult.StartsWith("Error: ")) return toolResult;
            return L["tool.captureWebpage.complete"];
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string url = GetStringArg(args, "url");
            if (string.IsNullOrWhiteSpace(url))
                return L["tool.captureWebpage.missingUrl"];

            if (!WebPageCaptureService.TryNormalizeUrl(url, out string normalizedUrl))
                return L.Format("tool.captureWebpage.invalidUrl", TruncateText(url, 120));

            bool fullPage = GetBoolArg(args, "full_page", true);
            int viewportWidth = GetIntArg(args, "viewport_width", 0);
            int maxWidth = GetIntArg(args, "max_width", 0);
            string savePathArg = GetStringArg(args, "save_path");

            try
            {
                var request = new WebPageCaptureRequest
                {
                    Url = normalizedUrl,
                    FullPage = fullPage,
                    ViewportWidth = viewportWidth,
                    MaxWidth = maxWidth,
                    SavePath = savePathArg,
                };

                WebPageCaptureResult result = await WebPageCaptureService.Instance
                    .CaptureAsync(request, CancellationToken);

                var sb = new StringBuilder();
                sb.AppendLine(L.Format(
                    "tool.captureWebpage.captured",
                    string.IsNullOrWhiteSpace(result.PageTitle) ? result.FinalUrl : result.PageTitle,
                    result.Width,
                    result.Height));
                sb.AppendLine($"- {L["tool.captureWebpage.url"]}: {result.FinalUrl}");
                sb.AppendLine($"- {L["tool.captureWebpage.savePath"]}: {result.SavePath}");
                sb.AppendLine($"- {L["tool.captureWebpage.method"]}: {result.Method}");
                sb.AppendLine(result.FullPage
                    ? L["tool.captureWebpage.fullPageNote"]
                    : L["tool.captureWebpage.viewportNote"]);
                sb.AppendLine();
                sb.AppendLine(CaptureWindowTool.CaptureImageBlockStart);
                sb.AppendLine(result.SavePath);
                sb.AppendLine(CaptureWindowTool.CaptureImageBlockEnd);
                return sb.ToString().TrimEnd();
            }
            catch (OperationCanceledException)
            {
                Logger.Info("[capture_webpage] Cancelled by user");
                return L["tool.captureWebpage.cancelled"];
            }
            catch (Exception ex)
            {
                Logger.Error($"[capture_webpage] Capture failed: {ex.Message}", ex);
                return L.Format("tool.captureWebpage.failed", ex.Message);
            }
        }
    }
}
