using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// 获取当前本地时间、UTC、星期、时区与 Unix 时间戳。
    /// 时间由 Agent 按需查询，不再改写用户输入。
    /// </summary>
    public sealed class GetCurrentTimeTool : BuiltInToolBase
    {
        private readonly Func<DateTimeOffset> _nowProvider;

        public GetCurrentTimeTool()
            : this(() => DateTimeOffset.Now)
        {
        }

        internal GetCurrentTimeTool(Func<DateTimeOffset> nowProvider)
        {
            _nowProvider = nowProvider ?? throw new ArgumentNullException(nameof(nowProvider));
        }

        public override string Name => "get_current_time";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = Name,
                    Description = L["tool.get_current_time.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new { },
                        required = Array.Empty<string>(),
                    },
                },
            };
        }

        public override Task<string> ExecuteAsync(
            Dictionary<string, JsonElement> args,
            string? workspaceRoot)
        {
            DateTimeOffset now = _nowProvider();
            TimeZoneInfo zone = TimeZoneInfo.Local;
            string timeZoneOffset = FormatOffset(now.Offset);

            string result = string.Join(
                Environment.NewLine,
                L["tool.getCurrentTime.header"],
                L.Format(
                    "tool.getCurrentTime.local",
                    now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)),
                L.Format(
                    "tool.getCurrentTime.utc",
                    now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
                L.Format(
                    "tool.getCurrentTime.dayOfWeek",
                    now.DayOfWeek.ToString()),
                L.Format(
                    "tool.getCurrentTime.timeZone",
                    zone.Id,
                    timeZoneOffset),
                L.Format(
                    "tool.getCurrentTime.unixTime",
                    now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));

            return Task.FromResult(result);
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args) =>
            L["tool.getCurrentTime.displayText"];

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

            return L["tool.getCurrentTime.resultSummary"];
        }

        private static string FormatOffset(TimeSpan offset)
        {
            string sign = offset < TimeSpan.Zero ? "-" : "+";
            TimeSpan absolute = offset.Duration();
            return $"{sign}{absolute.Hours:00}:{absolute.Minutes:00}";
        }
    }
}
