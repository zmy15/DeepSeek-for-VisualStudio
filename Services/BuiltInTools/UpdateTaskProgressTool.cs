using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// update_task_progress 工具 —— Edit Agent 专属的任务面板实时同步入口。
    /// <para>
    /// 替代此前「解析 AI 正文声明自动标记后续步骤完成」的正则推断方式：
    /// 那个机制无任何客观校验，连构建/测试步骤也会被静默跳过。
    /// 改为显式工具调用后，每一次状态变更都有明确的调用记录，可审计、可回放。
    /// </para>
    /// <para>
    /// 调用格式 (JSON):
    /// {
    ///   "steps": [
    ///     { "index": 2, "status": "completed", "summary": "已添加工具类" }
    ///   ]
    /// }
    /// </para>
    /// </summary>
    public class UpdateTaskProgressTool : BuiltInToolBase
    {
        /// <summary>
        /// 进度更新处理器：接收解析后的更新请求，由 Edit Agent 注入，
        /// 用于把状态写回 <see cref="AgentTaskPlan"/> 并触发任务面板刷新。
        /// </summary>
        private readonly Func<TaskProgressUpdate, Task> _progressHandler;

        /// <summary>
        /// 创建 UpdateTaskProgressTool 实例。
        /// </summary>
        /// <param name="progressHandler">
        /// 进度处理器：接收 <see cref="TaskProgressUpdate"/> 并应用到当前计划。
        /// 回调由 Edit Agent 在执行计划前注入。
        /// </param>
        /// <exception cref="ArgumentNullException">当 <paramref name="progressHandler"/> 为 null 时抛出。</exception>
        public UpdateTaskProgressTool(Func<TaskProgressUpdate, Task> progressHandler)
        {
            _progressHandler = progressHandler ?? throw new ArgumentNullException(nameof(progressHandler));
        }

        /// <summary>工具名称，与 Edit Agent 的 AllowedTools 白名单一致。</summary>
        public override string Name => "update_task_progress";

        /// <summary>
        /// 获取工具的 OpenAI Function Calling 定义。
        /// steps 数组不设上限：计划步骤数已不受 4 步限制，一次同步多个步骤属正常用法。
        /// </summary>
        /// <returns>工具定义对象。</returns>
        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "update_task_progress",
                    Description = L["tool.updateTaskProgress.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            steps = new
                            {
                                type = "array",
                                description = LocalizationService.Instance["tool.updateTaskProgress.param.steps"],
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        index = new
                                        {
                                            type = "integer",
                                            description = LocalizationService.Instance["tool.updateTaskProgress.param.steps.item.index"]
                                        },
                                        status = new
                                        {
                                            type = "string",
                                            @enum = new[] { "pending", "inProgress", "waitingApproval", "completed", "skipped", "failed" },
                                            description = LocalizationService.Instance["tool.updateTaskProgress.param.steps.item.status"]
                                        },
                                        summary = new
                                        {
                                            type = "string",
                                            description = LocalizationService.Instance["tool.updateTaskProgress.param.steps.item.summary"]
                                        },
                                        title = new
                                        {
                                            type = "string",
                                            description = LocalizationService.Instance["tool.updateTaskProgress.param.steps.item.title"]
                                        }
                                    },
                                    required = new[] { "index", "status" }
                                }
                            }
                        },
                        required = new[] { "steps" }
                    }
                }
            };
        }

        /// <summary>
        /// 解析参数并把更新请求交给注入的处理器。
        /// </summary>
        /// <param name="args">工具调用参数，要求包含 steps 数组。</param>
        /// <param name="workspaceRoot">工作区根目录（本工具不涉及文件路径，忽略）。</param>
        /// <returns>面向模型的执行结果文本（成功条数或失败原因）。</returns>
        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            if (!args.TryGetValue("steps", out var stepsNode) || stepsNode.ValueKind != JsonValueKind.Array)
                return L["tool.updateTaskProgress.missingSteps"];

            var update = new TaskProgressUpdate();
            var seenIndices = new HashSet<int>();

            foreach (var item in stepsNode.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                int index = 0;
                if (item.TryGetProperty("index", out var idxNode) && idxNode.ValueKind == JsonValueKind.Number)
                {
                    // 非法数字（越界/小数）按无效项跳过，不中断其余更新
                    if (!idxNode.TryGetInt32(out index)) continue;
                }
                if (index <= 0) continue;

                string statusText = item.TryGetProperty("status", out var statusNode)
                    && statusNode.ValueKind == JsonValueKind.String
                        ? statusNode.GetString() ?? string.Empty
                        : string.Empty;
                if (!TryParseStatus(statusText, out var status)) continue;

                string summary = item.TryGetProperty("summary", out var sumNode)
                    && sumNode.ValueKind == JsonValueKind.String
                        ? (sumNode.GetString() ?? string.Empty).Trim()
                        : string.Empty;
                string title = item.TryGetProperty("title", out var titleNode)
                    && titleNode.ValueKind == JsonValueKind.String
                        ? (titleNode.GetString() ?? string.Empty).Trim()
                        : string.Empty;

                // 同一 index 重复出现时后者覆盖前者：以模型最后的表述为准
                if (!seenIndices.Add(index))
                    update.Items.RemoveAll(i => i.Index == index);

                update.Items.Add(new TaskProgressItem
                {
                    Index = index,
                    Status = status,
                    Summary = string.IsNullOrEmpty(summary) ? null : summary,
                    Title = string.IsNullOrEmpty(title) ? null : title,
                });
            }

            if (update.Items.Count == 0)
                return L["tool.updateTaskProgress.noValidItems"];

            try
            {
                await _progressHandler(update);
                Logger.Info($"[TaskProgress] 已同步 {update.Items.Count} 个步骤状态: "
                    + string.Join(", ", update.Items.ConvertAll(i => $"{i.Index}={i.Status}")));
                return L.Format("tool.updateTaskProgress.applied", update.Items.Count);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[TaskProgress] 应用任务面板进度失败: {ex.Message}");
                return L.Format("tool.updateTaskProgress.failed", ex.Message);
            }
        }

        /// <summary>
        /// 把模型给出的状态字符串解析为 <see cref="AgentStepStatus"/>。
        /// 同时接受 camelCase、下划线与连字符写法（in_progress / in-progress），
        /// 因为不同模型对枚举参数的拼写习惯不一致。
        /// </summary>
        /// <param name="text">状态文本。</param>
        /// <param name="status">解析成功时返回对应状态。</param>
        /// <returns>解析成功返回 true；未知状态返回 false（调用方跳过该项）。</returns>
        internal static bool TryParseStatus(string? text, out AgentStepStatus status)
        {
            status = AgentStepStatus.Completed;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string normalized = text.Trim()
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty)
                .ToLowerInvariant();

            switch (normalized)
            {
                case "pending":
                    status = AgentStepStatus.Pending;
                    return true;
                case "inprogress":
                case "running":
                case "active":
                    status = AgentStepStatus.InProgress;
                    return true;
                case "waitingapproval":
                case "waiting":
                    status = AgentStepStatus.WaitingApproval;
                    return true;
                case "completed":
                case "done":
                case "complete":
                case "finished":
                    status = AgentStepStatus.Completed;
                    return true;
                case "skipped":
                case "skip":
                    status = AgentStepStatus.Skipped;
                    return true;
                case "failed":
                case "fail":
                case "error":
                    status = AgentStepStatus.Failed;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>生成人类可读的工具调用描述（聊天 UI 用）。</summary>
        /// <param name="args">工具调用参数。</param>
        /// <returns>形如「同步任务面板 (3 项)」的文本。</returns>
        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            int count = 0;
            if (args.TryGetValue("steps", out var stepsNode) && stepsNode.ValueKind == JsonValueKind.Array)
                count = stepsNode.GetArrayLength();

            return count > 0
                ? L.Format("tool.updateTaskProgress.displayText", count)
                : L["tool.updateTaskProgress.displayTextGeneric"];
        }

        /// <summary>生成执行结果的简短摘要（聊天 UI 用）。</summary>
        /// <param name="toolResult">工具返回的原始结果文本。</param>
        /// <returns>本地化摘要文本。</returns>
        public override string GetResultSummary(string toolResult) => L["tool.updateTaskProgress.resultSummary"];
    }
}
