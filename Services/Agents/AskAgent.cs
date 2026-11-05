using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// Ask Agent — 纯问答代理。
    /// 
    /// 职责：
    /// - 回答技术问题
    /// - 解释代码逻辑
    /// - 讨论方案和架构
    /// - 绝不修改任何文件
    /// 
    /// 这是默认的 fallback Agent，当用户意图不是代码修改时使用。
    /// </summary>
    public class AskAgent : BaseAgent
    {
        public AskAgent(DeepSeekApiService apiService) : base(apiService, AgentType.Ask) { }

        // ExploreAgent 引用由基类 BaseAgent.ExploreAgent 提供，
        // 由 AgentFactory 统一注入。不再使用 new 隐藏基类属性，
        // 避免 BaseAgent.ExecuteToolAsync 中 ExploreHandler 注入失败。

        #region Agent Definition

        /// <summary>
        /// Ask Agent 工具集 — 保留基础代码库读取能力和子代理委派能力。
        /// 简单查找（类/方法/文件/内容）直接用内置工具；
        /// 深度多步骤探索才委派给 ExploreAgent。
        /// </summary>
        public static readonly string[] AskTools = new[]
        {
            // ── 简单搜索与读取（无需委派 Explore）──
            "symbol_search",      // 符号搜索（类/方法/接口/属性定义）
            "get_file_symbols",   // 单文件符号定义列表
            "file_search",        // Glob 文件名搜索
            "grep_search",        // 文本/正则内容搜索
            "read_file",          // 读取文件
            "list_dir",           // 浏览目录结构
            "get_errors",         // 获取编译错误
            // ── 终端（只读：运行时禁止修改文件）──
            "run_in_terminal",    // 执行终端命令（仅放行不修改文件的命令）
            "get_terminal_output",// 获取异步终端输出
            // ── 用户交互 ──
            "VisualStudio_askQuestions",  // 向用户提问澄清
            // ── 委派与联网 ──
            "runSubagent",        // 深度探索任务委派给 ExploreAgent
            "web_search",         // 联网搜索（需在设置中开启）
            "fetch_webpage",      // 抓取指定网页内容
            "capture_window",     // 捕获指定窗口截图（视觉模型直接查看）
            "capture_webpage",    // 离屏渲染指定 URL 并截图（视觉模型直接查看）
            "request_handoff",    // 移交任务给其他 Agent
            "memory",             // 记忆管理
            "git",                // Git 只读操作（运行时禁止写操作）
        };

        protected override AgentDefinition CreateDefinition(AgentType agentType)
        {
            return new AgentDefinition
            {
                Type = AgentType.Ask,
                Name = "Ask",
                AllowedTools = new List<string>(AskTools),
                SystemPrompt = BuildSystemPrompt(),
            };
        }

        /// <summary>
        /// 构建 Ask Agent 专属系统提示词。
        /// 
        /// 说明：Git/终端只读边界、各工具的参数与使用条件已下沉到对应工具的
        /// description（见 tool.git.desc / tool.run_in_terminal.desc 等），
        /// 这里只保留角色定位、探索策略、记忆、执行类请求的边界与移交规则，
        /// 避免与工具描述重复而浪费 token。
        /// </summary>
        private static string BuildSystemPrompt()
        {
            return LocalizationService.Instance["agent.ask.systemPromptFragment"]
                + AiPrompts.AskAgentPromptFragment
                + LocalizationService.Instance["agent.ask.editStepsInstruction"]
                + AiPrompts.AgentConclusionStopRule;
        }

        #endregion

        #region Execute

        /// <summary>
        /// Ask Agent 执行入口。
        /// 支持两种模式：
        /// 1. 普通问答：直接将用户问题发送给 AI 并返回回答。
        /// 2. 摘要生成：接收 Edit Agent 的 Handoff，生成代码变更总结。
        /// </summary>
        public override async Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context)
        {
            // ── 清理上次执行的移交状态，防止跨调用污染 ──
            PendingHandoffRequest = null;

            // ── Edit/Build 的终态总结统一走 summary-only 路径 ──
            if (context.IsSummaryOnlyHandoff)
            {
                return await ExecuteSummaryOnlyAsync(userMessage, context);
            }

            // ── 提示词回显只进日志，不进 UI 时间线 ──
            // askStarted 的占位符是整段用户消息/Handoff 提示词，最长可达数 KB。
            // 它以 INFO 级别发出，会被时间线接住并渲染进过程折叠块，造成两个问题：
            //   ① 界面泄漏大段内部提示词（含「自由生成面向用户的最终总结」等脚手架文案）；
            //   ② 这行内容只存在于时间线、不属于模型回答，会把折叠块撑得很大。
            // 因此改为直接写日志文件，不经过 AddLog（AddLog 会广播给 UI 订阅者）。
            Logger.Info(string.Format(LocalizationService.Instance["agent.log.askStarted"],
                SummarizeForLog(userMessage)));

            var result = new AgentResult
            {
                Success = true,
            };

            try
            {
                var ct = context.CancellationToken;

                // ── 标准多轮对话：ContextManager 中已包含本次新增的原始 user；
                //    不再移除/包装为尾部 [用户问题]，解决方案路径与文件上下文
                //    由 volatile context 和原始 user 轮次承载。 ──
                var contextManager = Context?.ContextManager;
                bool useSessionHistory = contextManager != null && !contextManager.IsEmpty;
                bool isChainBackContinuation = context.IsChainBackContinuation;
                string contextualPrompt = isChainBackContinuation
                    ? userMessage
                    : useSessionHistory
                        ? string.Empty
                        : BuildContextualPrompt(userMessage, context);
                var messages = BuildContextAwareMessages(
                    Definition.SystemPrompt,
                    contextualPrompt,
                    maxRecentTurns: int.MaxValue,
                    deduplicateCurrentUser: useSessionHistory && !isChainBackContinuation,
                    persistVolatileToHistory: useSessionHistory);

                // ── 使用工具调用循环（支持 runSubagent 委派探索任务 + request_handoff 移交）──
                string workspaceRoot = GetWorkspaceRoot(context);
                var thinkingBuilder = new StringBuilder();
                string aiResponse = await CallAiWithToolLoopAsync(
                    messages,
                    workspaceRoot,
                    ct,
                    onThinking: (thinking) =>
                    {
                        thinkingBuilder.Append(thinking);
                        context.OnThinkingChunk?.Invoke(thinking);
                    },
                    onContent: (content) =>
                    {
                        context.OnContentChunk?.Invoke(content);
                    },
                    onToolCall: (toolSummary) =>
                    {
                        AddLog("TOOL", toolSummary);
                    });

                // ── 保存推理内容，供 UI 渲染思考面板 ──
                if (thinkingBuilder.Length > 0)
                    result.ReasoningContent = thinkingBuilder.ToString();

                // ── 检查 AI 是否通过 request_handoff 请求了移交 ──
                if (PendingHandoffRequest != null)
                {
                    result.Handoff = ConvertHandoffRequestToHandoff(PendingHandoffRequest);
                    result.Content = aiResponse;
                    AddLog("INFO", $"移交 → {PendingHandoffRequest.TargetAgent}");
                }
                else
                {
                    result.Content = aiResponse;
                }

                AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.askDone"], aiResponse.Length));
                result.Logs.AddRange(_logs);
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = LocalizationService.Instance["agent.log.askCancelled"];
                AddLog("WARN", LocalizationService.Instance["agent.log.askCancelled"]);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                AddLog("ERROR", string.Format(LocalizationService.Instance["agent.log.askFailed"], ex.Message));
            }

            return result;
        }

        /// <summary>
        /// 执行不可继续移交的终态总结。工具被完全禁用，避免 Build→Ask 在总结阶段
        /// 再次把任务交回 Build 而形成跨 Agent 循环。
        /// </summary>
        private async Task<AgentResult> ExecuteSummaryOnlyAsync(string userMessage, AgentContext context)
        {
            var L = LocalizationService.Instance;
            // 同 ExecuteAsync：提示词回显只写日志文件，不广播给 UI 时间线。
            Logger.Info(string.Format(L["agent.log.askStarted"], SummarizeForLog(userMessage)));

            var result = new AgentResult
            {
                Success = true,
            };

            try
            {
                var messages = BuildContextAwareMessages(
                    L["agent.summaryOnlySystemPrompt"],
                    userMessage,
                    maxRecentTurns: int.MaxValue);

                var thinkingBuilder = new StringBuilder();
                string aiResponse = await CallAiWithToolLoopAsync(
                    messages,
                    GetWorkspaceRoot(context),
                    context.CancellationToken,
                    toolWhitelist: CreateReadOnlyTextPhaseToolWhitelist(),
                    toolChoiceOverride: "auto",
                    noToolsReminderAfterFirstToolRound: L["agent.summaryOnlyNoMoreToolsAfterToolRound"],
                    onThinking: (thinking) =>
                    {
                        thinkingBuilder.Append(thinking);
                        context.OnThinkingChunk?.Invoke(thinking);
                    },
                    onContent: (content) =>
                    {
                        context.OnContentChunk?.Invoke(content);
                    },
                    onToolCall: (toolSummary) => AddLog("TOOL", toolSummary));

                if (thinkingBuilder.Length > 0)
                    result.ReasoningContent = thinkingBuilder.ToString();

                aiResponse = StripToolCallMarkers(aiResponse);
                result.Content = aiResponse;
                AddLog("INFO", string.Format(L["agent.log.askDone"], aiResponse.Length));
                result.Logs.AddRange(_logs);
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = LocalizationService.Instance["agent.log.askCancelled"];
                AddLog("WARN", LocalizationService.Instance["agent.log.askCancelled"]);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                AddLog("ERROR", string.Format(LocalizationService.Instance["agent.log.askFailed"], ex.Message));
            }

            return result;
        }

        /// <summary>
        /// 构建包含文件和项目上下文的 prompt。
        /// </summary>
        private static string BuildContextualPrompt(string userMessage, AgentContext context)
        {
            var sb = new StringBuilder();
            var L = LocalizationService.Instance;

            if (!string.IsNullOrEmpty(context.FileContext))
            {
                sb.AppendLine(L["system.contextFileContent"]);
                sb.AppendLine(context.FileContext);
                sb.AppendLine();
            }

            sb.AppendLine(userMessage);
            return sb.ToString();
        }

        /// <summary>
        /// 剥离 AI 输出中意外包含的工具调用标记和思考过程文本。
        /// 当 toolChoice="none" 时 AI 仍可能输出工具调用意图文本。
        /// 处理 DSML invoke/parameter、管道分隔 tool_calls、以及中文思考前缀。
        /// </summary>
        private static string StripToolCallMarkers(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            // 统一的 DSML/工具调用块清理。总结正文不应删除普通属性文本，
            // 因此关闭 BaseAgent 的“孤立属性”兜底清理。
            text = StripDsmlContent(text, removeResidualAttributes: false);

            // 移除未实际调用工具时残留的中文意图前缀。
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"^\s*(?:让我(?:先)?(?:查看|检查|读取|确认)|我先(?:查看|检查|读取|确认|核实)|我需要(?:先)?(?:查看|检查|读取|确认|核实)).*?[。.!?\n]",
                string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);

            return text.Trim();
        }

        #endregion
    }
}
