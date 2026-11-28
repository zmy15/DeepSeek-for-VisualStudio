using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// <see cref="BaseAgent"/> 的 工具集与白名单 分部实现。
    /// 承载该职责域的成员，使主文件聚焦于 Agent 执行编排。
    /// </summary>
    public abstract partial class BaseAgent
    {
        /// <summary>Ask Agent 直接探索达到该次数后，追加一次非强制建议。</summary>
        internal const int AskDirectExplorationHintThreshold = 8;

        // ════════════════════════════════════════════════════════════════
        // MCP 工具自动分类：根据工具名前缀/关键词判断读写属性，
        // 使 MCP 导入的外部工具能自动匹配到合适的 Agent。
        // ════════════════════════════════════════════════════════════════

        /// <summary>已知内置工具名（MCP 同名工具会覆盖内置，不同名工具按分类注入）</summary>
        protected static readonly HashSet<string> KnownBuiltInToolNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "list_dir", "read_file", "file_search", "grep_search", "symbol_search", "get_file_symbols", "get_file_comments", "get_errors", "get_current_time",
            "fetch_webpage", "web_search", "capture_window", "capture_webpage", "build_solution",
            "run_tests",
            "replace_string_in_file", "multi_replace_string_in_file",
            "create_file", "delete_file", "apply_patch", "create_directory", "run_in_terminal",
            "get_terminal_output", "list_skills", "load_skill", "read_skill_resource",
            "VisualStudio_askQuestions", "runSubagent", "request_handoff", "update_task_progress",
            "git", "memory"
        };

        /// <summary>
        /// 文本生成阶段的只读工具白名单。Plan 与 Ask 的总结阶段共用。
        /// 提示词会要求模型不要主动调用工具，但误调用时仍可通过这些只读工具正常收尾。
        /// 含 runSubagent：这些阶段本身可能需要委派 Explore 补充探索。
        /// </summary>
        protected static List<string> CreateReadOnlyTextPhaseToolWhitelist()
        {
            return new List<string>
            {
                "runSubagent",
                "read_file",
                "grep_search",
                "file_search",
                "symbol_search",
                "get_file_symbols",
                "list_dir",
                "get_current_time",
                "memory",
            };
        }

        /// <summary>
        /// 终态收尾阶段的只读白名单（记忆判断、总结润色等子任务专用）。
        /// 相对 <see cref="CreateReadOnlyTextPhaseToolWhitelist"/> 去掉了 runSubagent：
        /// 这些是一次性的判定/润色子任务，不需要二次委派子代理再探索一遍代码库，
        /// 否则误调用时可能触发一次完整的 Explore（额外耗时与 token 开销）。
        /// </summary>
        protected static List<string> CreateReadOnlyFinalTaskToolWhitelist()
        {
            return new List<string>
            {
                "read_file",
                "grep_search",
                "file_search",
                "symbol_search",
                "get_file_symbols",
                "list_dir",
                "get_current_time",
                "memory",
            };
        }

        /// <summary>读类 MCP 工具名前缀/关键词（分配给 ExploreAgent）</summary>
        private static readonly string[] ReadMcpPatterns =
        {
            "get_", "list_", "read_", "search_", "find_", "query_", "fetch_", "describe_",
            "browse_", "view_", "show_", "check_", "lookup_", "scan_", "export_", "download_",
            "pull_", "status_", "log_", "diff_", "print_", "display_"
        };

        /// <summary>单词语义读类工具名（不含下划线的完整匹配）</summary>
        private static readonly HashSet<string> ReadSingleWordNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "ocr", "recognize", "parse", "analyze", "validate", "resolve",
            "inspect", "identify", "detect", "ping", "whoami"
        };

        /// <summary>写类 MCP 工具名前缀/关键词（分配给 EditAgent / BuildAgent）</summary>
        private static readonly string[] WriteMcpPatterns =
        {
            "create_", "delete_", "update_", "write_", "run_", "execute_", "build_", "deploy_",
            "push_", "commit_", "apply_", "set_", "add_", "remove_", "edit_", "modify_",
            "install_", "publish_", "start_", "stop_", "restart_", "config_", "patch_",
            "merge_", "upload_", "save_", "insert_", "replace_"
        };

        /// <summary>
        /// 对 MCP 工具进行分类并返回应自动注入到指定 Agent 白名单的工具名。
        /// 仅返回不与内置工具重名的 MCP 工具。
        /// </summary>
        /// <param name="mcpManager">MCP 管理器（null 时返回空列表）</param>
        /// <param name="agentType">目标 Agent 类型</param>
        /// <returns>应自动注入的 MCP 工具名列表</returns>
        protected static List<string> GetAutoMcpToolNames(McpManagerService? mcpManager, AgentType agentType)
        {
            var result = new List<string>();
            if (mcpManager == null || mcpManager.AllTools.Count == 0)
                return result;

            bool wantRead = agentType == AgentType.Explore;
            bool wantWrite = agentType == AgentType.Edit || agentType == AgentType.Build;

            if (!wantRead && !wantWrite)
                return result; // Ask / Plan 不自动注入 MCP 工具

            foreach (var tool in mcpManager.AllTools)
            {
                // 跳过与内置工具同名的（已由 BuiltInToolService 统一管理，MCP 同名会覆盖内置）
                if (KnownBuiltInToolNames.Contains(tool.Name))
                    continue;

                bool isRead = IsReadMcpTool(tool);
                bool isWrite = IsWriteMcpTool(tool);

                if (wantRead && isRead)
                    result.Add(tool.Name);
                else if (wantWrite && isWrite)
                    result.Add(tool.Name);
                else if (wantRead && !isWrite)
                    // 只读 Agent：无法判断时默认归为只读（安全）
                    result.Add(tool.Name);
                // 写 Agent 仅在明确匹配写模式时才加入，无法判断时不加入（安全优先）
                // 如果 isRead && isWrite 同时为 true → 两个 Agent 都加上
            }

            return result;
        }

        /// <summary>
        /// 判断视觉模型下是否应禁用 OCR 工具（执行层判定）。
        /// 视觉模型直接读图，OCR 始终禁用。
        /// </summary>
        internal static bool ShouldSuppressOcrTools(bool isVisionModel, string? userMessage)
            => ToolClassifier.ShouldSuppressOcrTools(isVisionModel, userMessage);

        /// <summary>
        /// 构建完整工具集（不过滤白名单），用于 DeepSeek Prefix Cache 稳定。
        /// 常规工具调用统一发送此完整工具集，保持 tools JSON 不变。
        /// 明确不需要工具的辅助调用可通过 includeTools=false 完全省略工具字段。
        /// 工具调用由客户端按 Agent 白名单拦截。
        /// 
        /// 双重合并：优先通过 BuiltInToolService 获取（已合并内置+MCP），
        /// 同时兜底直接查询 McpManager 确保 MCP 工具不遗漏。
        /// </summary>
        protected List<ToolDefinition> BuildFullToolSet()
        {
            var fullSet = new List<ToolDefinition>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ── MCP 定义只取一次：既用于兜底合并，也避免重复构建定义 ──
            var mcpDefs = McpManager != null && McpManager.AllTools.Count > 0
                ? McpManager.GetToolDefinitions()
                : null;

            if (BuiltInTools != null)
            {
                foreach (var def in BuiltInTools.GetFullToolDefinitions())
                {
                    // 注意：视觉模型的 OCR 工具不在此处剔除。
                    // 工具集必须保持稳定（缺失/新增都会改写请求前缀，打断 DeepSeek Prefix Cache），
                    // 视觉模式下改为在执行层禁用：模型仍能看到工具，但调用会被拒绝并收到明确指引。
                    fullSet.Add(def);
                    seenNames.Add(def.Function.Name);
                }
            }

            // ──  兜底合并：直接从 McpManager 获取 MCP 工具（防止 BuiltInToolService 的
            //    _mcpManager 引用因初始化时序问题而过时）──
            if (mcpDefs != null)
            {
                var addedNames = new List<string>();
                foreach (var def in mcpDefs)
                {
                    if (seenNames.Contains(def.Function.Name))
                        continue;

                    fullSet.Add(def);
                    seenNames.Add(def.Function.Name);
                    addedNames.Add(def.Function.Name);
                }
                if (addedNames.Count > 0)
                {
                    Logger.Info($"[Agent:{Definition.Name}] 兜底合并了 {addedNames.Count} 个 MCP 工具" +
                        $"（BuiltInToolService 未返回）: {string.Join(", ", addedNames)}");
                }
            }

            return fullSet;
        }

        /// <summary>
        /// 视觉模型下的 OCR 工具禁用判定。
        ///
        /// 该判定作用于<b>执行层</b>，而不是工具集：OCR 工具始终随完整工具集发送，
        /// 以保持请求前缀稳定（前缀缓存），视觉模型调用时直接拒绝并返回指引。
        /// 视觉模型直接读图，OCR 始终禁用。
        /// </summary>
        internal static bool ShouldBlockOcrToolExecution(
            string toolName, bool isVisionModel, string? userMessage)
            => ToolClassifier.ShouldBlockOcrToolExecution(toolName, isVisionModel, userMessage);

        /// <summary>
        /// 获取当前会话的完整工具集（缓存，避免重复构建）。
        /// 在 MCP 工具变更时调用 <see cref="InvalidateFullToolSetCache"/> 刷新。
        /// </summary>
        private List<ToolDefinition>? _cachedFullToolSet;

        /// <summary>
        /// 获取或构建缓存的完整工具集。
        /// </summary>
        protected List<ToolDefinition> GetFullToolSet()
        {
            if (_cachedFullToolSet == null)
            {
                _cachedFullToolSet = BuildFullToolSet();
            }
            return _cachedFullToolSet;
        }

        /// <summary>
        /// 使缓存的完整工具集失效（MCP 工具变更时调用）。
        /// </summary>
        public void InvalidateFullToolSetCache()
        {
            _cachedFullToolSet = null;
            Logger.Info($"[Agent:{Definition.Name}] 完整工具集缓存已失效（MCP 工具变更）");
        }

        private static bool IsReadMcpTool(McpTool tool)
        {
            // 前缀匹配
            foreach (var pattern in ReadMcpPatterns)
            {
                if (tool.Name.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            // 单词语义匹配（不含下划线的完整工具名）
            if (ReadSingleWordNames.Contains(tool.Name))
                return true;
            // 描述关键词辅助判断
            if (!string.IsNullOrEmpty(tool.Description))
            {
                var desc = tool.Description;
                if (desc.Contains("read", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("get", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("list", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("query", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("search", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("fetch", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("view", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("retrieve", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("recognize", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("ocr", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("scan", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("parse", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("extract", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("analyze", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("detect", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("identify", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("inspect", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("validate", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("resolve", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("lookup", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsWriteMcpTool(McpTool tool)
        {
            foreach (var pattern in WriteMcpPatterns)
            {
                if (tool.Name.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            // 描述关键词辅助判断
            if (!string.IsNullOrEmpty(tool.Description))
            {
                var desc = tool.Description;
                if (desc.Contains("create", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("delete", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("update", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("write", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("run", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("execute", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("modify", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("change", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("install", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("deploy", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("publish", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("push", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
