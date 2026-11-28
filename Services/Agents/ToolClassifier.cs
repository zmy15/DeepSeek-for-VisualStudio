using System;
using System.IO;
using System.Text.Json;
using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// 工具分类判定（从 <see cref="BaseAgent"/> 下沉）：读写属性、交互性、超时档位、参数路径提取与 OCR 抑制。
    /// 全部为无状态纯函数；<see cref="BaseAgent"/> 上保留 <c>NormalizeToolName</c> 与两个 OCR 判定的转发，
    /// 以维持 5 个子类与既有测试的编译契约。
    /// </summary>
    internal static class ToolClassifier
    {
        /// <summary>
        /// 判断工具是否为文件修改类工具（可能修改用户文件）。
        /// </summary>
        /// <param name="toolName">工具名。</param>
        /// <returns>属于文件修改类工具时返回 <c>true</c>。</returns>
        internal static bool IsFileModifyingTool(string toolName)
        {
            return toolName is "replace_string_in_file"
                or "multi_replace_string_in_file"
                or "create_file"
                or "apply_patch";
        }

        /// <summary>
        /// 判断工具是否为文件访问类工具（读/写/搜索/列表，涉及文件路径）。
        /// 用于越权检测——AI 访问项目外路径时需用户审批。
        /// </summary>
        /// <param name="toolName">工具名。</param>
        /// <returns>涉及文件路径访问时返回 <c>true</c>。</returns>
        internal static bool IsFileAccessingTool(string toolName)
        {
            return toolName is "read_file"
                or "list_dir"
                or "create_file"
                or "delete_file"
                or "replace_string_in_file"
                or "multi_replace_string_in_file"
                or "create_directory"
                or "file_search"
                or "grep_search"
                or "apply_patch";
        }

        /// <summary>
        /// 获取工具操作的中文名称（用于审批提示）。
        /// </summary>
        /// <param name="toolName">工具名。</param>
        /// <returns>本地化后的操作名；未知工具返回通用"访问"文案。</returns>
        internal static string GetToolOperationName(string toolName)
        {
            var L = LocalizationService.Instance;
            return toolName switch
            {
                "read_file" => L["agent.op.read"],
                "list_dir" => L["agent.op.listDir"],
                "create_file" => L["agent.op.createFile"],
                "delete_file" => L["agent.op.deleteFile"],
                "replace_string_in_file" => L["agent.op.modify"],
                "multi_replace_string_in_file" => L["agent.op.batchModify"],
                "create_directory" => L["agent.op.createDirectory"],
                "file_search" => L["agent.op.searchFiles"],
                "grep_search" => L["agent.op.searchContent"],
                "apply_patch" => L["agent.op.applyPatch"],
                _ => L["agent.op.access"]
            };
        }

        /// <summary>
        /// 判断目标路径是否在工作区之外。
        /// 支持绝对路径和相对路径判断。
        /// </summary>
        /// <param name="targetPath">待判定的目标路径。</param>
        /// <param name="workspaceRoot">工作区根目录。</param>
        /// <returns>目标路径位于工作区之外时返回 <c>true</c>。</returns>
        internal static bool IsPathOutsideWorkspace(string targetPath, string workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(workspaceRoot))
                return false;

            // ── 规范化路径 ──
            string normalizedTarget = targetPath.Replace('/', '\\').Trim();
            string normalizedRoot = workspaceRoot.Replace('/', '\\').Trim().TrimEnd('\\') + '\\';

            // ── 绝对路径：直接比较前缀 ──
            if (System.IO.Path.IsPathRooted(normalizedTarget))
            {
                string fullTarget = System.IO.Path.GetFullPath(normalizedTarget).TrimEnd('\\') + '\\';
                string fullRoot = System.IO.Path.GetFullPath(normalizedRoot).TrimEnd('\\') + '\\';
                return !fullTarget.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }

            // ── 相对路径：以 ../ 或 ..\ 开头视为项目外 ──
            if (normalizedTarget.StartsWith(".."))
                return true;

            // ── 纯相对路径（如 "src/file.cs"）：视为项目内 ──
            return false;
        }

        /// <summary>
        /// 从各种工具参数中提取文件/目录路径。
        /// 比 ExtractFilePathFromToolArgs 更通用，覆盖 file_search/grep_search 的 includePattern 等。
        /// </summary>
        /// <param name="toolName">工具名，用于区分 file_search 的 query 语义。</param>
        /// <param name="argumentsJson">工具参数 JSON 文本。</param>
        /// <returns>提取到的路径；无法确定或为相对 pattern 时返回 <c>null</c>。</returns>
        internal static string? ExtractAnyPathFromToolArgs(string toolName, string argumentsJson)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
                var root = doc.RootElement;

                // ── 通用 filePath 参数 ──
                if (root.TryGetProperty("filePath", out var fpProp))
                    return fpProp.GetString();

                // ── list_dir / create_directory 的 path/dirPath ──
                if (root.TryGetProperty("path", out var pProp))
                    return pProp.GetString();
                if (root.TryGetProperty("dirPath", out var dpProp))
                    return dpProp.GetString();

                // ── file_search / grep_search 的 includePattern（可能是绝对路径）──
                if (root.TryGetProperty("includePattern", out var ipProp))
                {
                    string pattern = ipProp.GetString() ?? string.Empty;
                    // 如果 pattern 是绝对路径（如 "F:\\project\\src\\**"），提取目录部分
                    if (System.IO.Path.IsPathRooted(pattern))
                    {
                        // 去掉 glob 通配符部分，取目录
                        int globIdx = pattern.IndexOfAny(new[] { '*', '?' });
                        string dirPart = globIdx >= 0 ? pattern.Substring(0, globIdx) : pattern;
                        string? dir = System.IO.Path.GetDirectoryName(dirPart.TrimEnd('/', '\\'));
                        return dir ?? dirPart;
                    }
                    return null; // 相对 pattern，不检查
                }

                // ── file_search 的 query 参数也可能是绝对 glob 路径 ──
                // 例如 query="F:\\outside\\**\\*.cs"，Path.Combine 会因绝对路径直接跳转到项目外
                if (toolName == "file_search" && root.TryGetProperty("query", out var qProp))
                {
                    string query = qProp.GetString() ?? string.Empty;
                    if (System.IO.Path.IsPathRooted(query))
                    {
                        int globIdx = query.IndexOfAny(new[] { '*', '?' });
                        string dirPart = globIdx >= 0 ? query.Substring(0, globIdx) : query;
                        string? dir = System.IO.Path.GetDirectoryName(dirPart.TrimEnd('/', '\\'));
                        return dir ?? dirPart;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 判断工具是否需要用户交互（不设工具层超时）。
        /// 构建类工具也在此列，因为其执行时间不可预测，超时由 BuildService 内部自行管理。
        /// read_file / list_dir 等只读工具也包含在内，因为它们可能触发项目外路径权限弹窗，
        /// 用户响应时间不可预测，硬超时会导致权限拒绝结果丢失。
        /// </summary>
        /// <param name="toolName">工具名。</param>
        /// <returns>需要用户交互时返回 <c>true</c>。</returns>
        internal static bool IsInteractiveTool(string toolName)
        {
            return toolName is "run_in_terminal"
                or "get_terminal_output"
                or "delete_file"
                or "VisualStudio_askQuestions"
                or "replace_string_in_file"
                or "multi_replace_string_in_file"
                or "create_file"
                or "apply_patch"
                or "build_solution"          // 构建时间不可预测，由 BuildService 内部控制超时
                or "run_tests"               // 测试运行时间不可预测，由 TestService 内部控制超时
                or "read_file"               // 可能触发项目外路径权限弹窗
                or "list_dir"                // 同上
                or "create_directory"        // 同上
                or "file_search"             // 同上
                or "grep_search"             // 同上
                or "git"                     // 写操作需要用户审批
                or "runSubagent";            // 子代理可能需要用户审批（如 Explore 用 git）
        }

        /// <summary>
        /// 工具名规范化：把历史别名 <c>askQuestions</c> 统一为 <c>VisualStudio_askQuestions</c>。
        /// </summary>
        /// <param name="toolName">原始工具名。</param>
        /// <returns>规范化后的工具名。</returns>
        internal static string NormalizeToolName(string toolName)
        {
            if (string.Equals(toolName, "askQuestions", StringComparison.OrdinalIgnoreCase))
                return "VisualStudio_askQuestions";

            return toolName;
        }

        /// <summary>
        /// 根据工具类型返回合适的超时时间（仅对非交互式、非构建类工具生效）。
        /// 交互式工具和构建类工具由 IsInteractiveTool 直接跳过超时。
        /// 分档策略见 <see cref="Services.Tools.ToolTimeoutPolicy"/>（P2，序号 22）。
        /// </summary>
        /// <param name="toolName">工具名。</param>
        /// <returns>该工具的超时时长。</returns>
        internal static TimeSpan GetToolTimeout(string toolName)
        {
            return Services.Tools.ToolTimeoutPolicy.GetTimeout(toolName);
        }

        /// <summary>
        /// 判断工具是否为写操作（编辑/创建/删除文件）。
        /// </summary>
        /// <param name="toolName">工具名。</param>
        /// <returns>为写操作工具时返回 <c>true</c>。</returns>
        internal static bool IsWriteTool(string toolName)
        {
            return toolName switch
            {
                "write_file" => true,
                "create_file" => true,
                "replace_string_in_file" => true,
                "multi_replace_string_in_file" => true,
                "delete_file" => true,
                "apply_patch" => true,
                "create_directory" => true,
                _ => false,
            };
        }

        /// <summary>判断工具是否属于 Ask Agent 的直接代码库探索工具。</summary>
        /// <param name="toolName">工具名。</param>
        /// <returns>属于直接探索工具时返回 <c>true</c>。</returns>
        internal static bool IsDirectExplorationTool(string toolName)
        {
            return NormalizeToolName(toolName) switch
            {
                "read_file" or "grep_search" or "file_search" or "list_dir" or "symbol_search" or "get_file_symbols" => true,
                _ => false,
            };
        }

        /// <summary>
        /// 判断视觉模型下是否应禁用 OCR 工具（执行层判定）。
        /// 视觉模型直接读图，OCR 始终禁用。
        /// </summary>
        /// <param name="isVisionModel">当前端点是否为视觉模型。</param>
        /// <param name="userMessage">用户消息（保留参数以兼容既有调用）。</param>
        /// <returns>视觉模型时返回 <c>true</c>。</returns>
        internal static bool ShouldSuppressOcrTools(bool isVisionModel, string? userMessage)
            => isVisionModel;

        /// <summary>
        /// 视觉模型下的 OCR 工具禁用判定。
        ///
        /// 该判定作用于<b>执行层</b>，而不是工具集：OCR 工具始终随完整工具集发送，
        /// 以保持请求前缀稳定（前缀缓存），视觉模型调用时直接拒绝并返回指引。
        /// 视觉模型直接读图，OCR 始终禁用。
        /// </summary>
        /// <param name="toolName">被调用的工具名。</param>
        /// <param name="isVisionModel">当前端点是否为视觉模型。</param>
        /// <param name="userMessage">用户消息。</param>
        /// <returns>应拒绝执行时返回 <c>true</c>。</returns>
        internal static bool ShouldBlockOcrToolExecution(
            string toolName, bool isVisionModel, string? userMessage)
            => ShouldSuppressOcrTools(isVisionModel, userMessage)
               && OcrToolSupport.IsOcrToolName(toolName);
    }
}
