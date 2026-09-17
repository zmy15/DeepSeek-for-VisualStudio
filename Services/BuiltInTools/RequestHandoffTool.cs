using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// request_handoff 工具 — Agent 间移交的专用 JSON 格式入口。
    /// 
    /// 当 Agent 需要将整个任务移交给另一个 Agent 时，调用此工具声明移交意图。
    /// 与 runSubagent 的区别：request_handoff 是完整控制权移交，
    /// runSubagent 是子任务委派（调用方等待结果继续）。
    /// 
    /// 移交格式 (JSON):
    /// {
    ///   "targetAgent": "Edit|Ask|Plan|Build|Explore",
    ///   "reason": "简短说明为什么移交",
    ///   "taskDescription": "给目标 Agent 的完整任务描述"
    /// }
    /// </summary>
    public class RequestHandoffTool : BuiltInToolBase
    {
        private readonly Func<HandoffRequest, Task> _handoffHandler;

        /// <summary>
        /// 创建 RequestHandoffTool 实例。
        /// </summary>
        /// <param name="handoffHandler">
        /// 移交处理器：接收 HandoffRequest，将其存储为 Agent 的待处理移交。
        /// 回调由 BaseAgent 注入。
        /// </param>
        public RequestHandoffTool(Func<HandoffRequest, Task> handoffHandler)
        {
            _handoffHandler = handoffHandler ?? throw new ArgumentNullException(nameof(handoffHandler));
        }

        public override string Name => "request_handoff";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "request_handoff",
                    Description = L["tool.request_handoff.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            targetAgent = new
                            {
                                type = "string",
                                @enum = new[] { "Edit", "Ask", "Plan", "Build", "Explore" },
                                description = LocalizationService.Instance["tool.requestHandoff.param.targetAgent"]
                            },
                            reason = new
                            {
                                type = "string",
                                description = LocalizationService.Instance["tool.requestHandoff.param.reason"]
                            },
                            taskDescription = new
                            {
                                type = "string",
                                description = LocalizationService.Instance["tool.requestHandoff.param.taskDescription"]
                            },
                            editSteps = new
                            {
                                type = "array",
                                maxItems = 4,
                                description = LocalizationService.Instance["tool.requestHandoff.param.editSteps"],
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        title = new
                                        {
                                            type = "string",
                                            description = LocalizationService.Instance["tool.requestHandoff.param.editSteps.item.title"]
                                        },
                                        description = new
                                        {
                                            type = "string",
                                            description = LocalizationService.Instance["tool.requestHandoff.param.editSteps.item.description"]
                                        }
                                    },
                                    required = new[] { "title" }
                                }
                            },
                            gitState = new
                            {
                                type = "object",
                                description = LocalizationService.Instance["tool.requestHandoff.param.gitState"],
                                properties = new
                                {
                                    branch = new { type = "string", description = LocalizationService.Instance["tool.requestHandoff.param.gitState.branch"] },
                                    headSha = new { type = "string", description = LocalizationService.Instance["tool.requestHandoff.param.gitState.headSha"] },
                                    isClean = new { type = "boolean", description = LocalizationService.Instance["tool.requestHandoff.param.gitState.isClean"] },
                                    refs = new
                                    {
                                        type = "object",
                                        additionalProperties = new { type = "string" },
                                        description = LocalizationService.Instance["tool.requestHandoff.param.gitState.refs"]
                                    }
                                }
                            }
                        },
                        required = new[] { "targetAgent", "reason", "taskDescription" }
                    }
                }
            };
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string targetAgentStr = GetStringArg(args, "targetAgent");
            string reason = GetStringArg(args, "reason");
            string taskDescription = GetStringArg(args, "taskDescription");
            if (string.IsNullOrWhiteSpace(targetAgentStr))
                return "Error: request_handoff: 缺少 targetAgent 参数。可选值: Edit, Ask, Plan, Build, Explore";

            if (string.IsNullOrWhiteSpace(taskDescription))
                return "Error: request_handoff: 缺少 taskDescription 参数。请描述目标 Agent 需要执行的任务。";

            // ── 解析可选 editSteps → List<AgentStep>?（防御式：非法项跳过、空标题过滤、超限截断）──
            List<AgentStep>? editSteps = null;
            if (args.TryGetValue("editSteps", out var editStepsNode) && editStepsNode.ValueKind == JsonValueKind.Array)
            {
                var parsedSteps = new List<AgentStep>();
                foreach (var item in editStepsNode.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string stepTitle = item.TryGetProperty("title", out var titleNode) && titleNode.ValueKind == JsonValueKind.String
                        ? (titleNode.GetString() ?? string.Empty).Trim()
                        : string.Empty;
                    if (string.IsNullOrEmpty(stepTitle)) continue;   // 空白标题项跳过
                    string stepDesc = item.TryGetProperty("description", out var descNode) && descNode.ValueKind == JsonValueKind.String
                        ? (descNode.GetString() ?? string.Empty).Trim()
                        : string.Empty;
                    parsedSteps.Add(new AgentStep
                    {
                        Index = parsedSteps.Count + 1,               // 顺序重排（从 1 开始）
                        Title = stepTitle,
                        Description = stepDesc,
                        Status = AgentStepStatus.Pending,
                        RequiresApproval = false,
                    });
                    if (parsedSteps.Count >= 4) break;               // 防御性截断：超出 4 步不报错
                }
                if (parsedSteps.Count > 0) editSteps = parsedSteps;  // 无有效步骤 → 保持 null
            }

            // ── 解析可选 gitState → AgentGitStateSnapshot?（移交方已核实的 Git 状态）──
            AgentGitStateSnapshot? gitState = null;
            if (args.TryGetValue("gitState", out var gitNode) && gitNode.ValueKind == JsonValueKind.Object)
            {
                gitState = new AgentGitStateSnapshot
                {
                    Branch = GetStringFromNode(gitNode, "branch"),
                    HeadSha = GetStringFromNode(gitNode, "headSha"),
                    IsClean = gitNode.TryGetProperty("isClean", out var cleanNode)
                        && cleanNode.ValueKind == JsonValueKind.True,
                    CapturedAtUtc = DateTime.UtcNow,
                };

                if (gitNode.TryGetProperty("refs", out var refsNode) && refsNode.ValueKind == JsonValueKind.Object)
                {
                    var refs = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var prop in refsNode.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            string? value = prop.Value.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                                refs[prop.Name] = value!;
                        }
                    }
                    if (refs.Count > 0) gitState.Refs = refs;
                }

                // 全空快照视为未提供，避免目标 Agent 收到无意义的状态块。
                if (string.IsNullOrWhiteSpace(gitState.Branch)
                    && string.IsNullOrWhiteSpace(gitState.HeadSha)
                    && (gitState.Refs == null || gitState.Refs.Count == 0))
                {
                    gitState = null;
                }
            }

            // 解析目标 Agent 类型
            AgentType targetAgent = targetAgentStr.ToLowerInvariant() switch
            {
                "edit" => AgentType.Edit,
                "ask" => AgentType.Ask,
                "plan" => AgentType.Plan,
                "build" => AgentType.Build,
                "explore" => AgentType.Explore,
                _ => AgentType.Ask
            };

            var request = new HandoffRequest
            {
                SourceAgent = AgentType.Ask, // 由 BaseAgent 在执行时覆写
                TargetAgent = targetAgent,
                Reason = reason,
                TaskDescription = taskDescription,
                AutoSend = true,
                EditSteps = editSteps,
                GitState = gitState,
            };

            Logger.Info($"[RequestHandoff] {targetAgentStr} ← {reason.Truncate(80)}");

            await _handoffHandler(request);

            return LocalizationService.Instance.Format("tool.requestHandoff.handoffRequested", targetAgentStr, reason);
        }

        private static string GetStringFromNode(JsonElement node, string key)
        {
            if (node.TryGetProperty(key, out var element))
            {
                if (element.ValueKind == JsonValueKind.String)
                    return element.GetString() ?? string.Empty;
                if (element.ValueKind == JsonValueKind.Null)
                    return string.Empty;
                return element.ToString();
            }
            return string.Empty;
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string target = GetStringArg(args, "targetAgent") ?? "?";
            string reason = GetStringArg(args, "reason") ?? "移交任务";
            return LocalizationService.Instance.Format("tool.requestHandoff.handoffTo", target, reason);
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return "移交完成";
            if (toolResult.StartsWith("HANDOFF_REQUESTED", StringComparison.Ordinal)) return LocalizationService.Instance["tool.requestHandoff.completed"];
            return toolResult;
        }
    }
}
