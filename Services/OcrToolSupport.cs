using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// OCR 相关工具的公共支持逻辑 — 提供工具名识别与调用参数预处理能力。
    /// 本类型不依赖任何 UI 组件，可由 Agent 编排层直接复用；原先实现位于 View 层的聊天控件中，
    /// 下沉到服务层是为了消除业务层对 UI 命名空间的反向依赖。
    /// </summary>
    internal static class OcrToolSupport
    {
        /// <summary>
        /// OCR 工具名关键字集合 — 用于按子串（忽略大小写）判定工具是否属于 OCR 类别。
        /// 提升为静态字段避免每次调用都重新分配数组。
        /// </summary>
        private static readonly string[] OcrToolKeywords =
        {
            "ocr",
            "recognize_text",
            "paddle_ocr",
            "ocr_image",
            "image_to_text",
            "read_text"
        };

        /// <summary>
        /// 判断指定工具名是否属于 OCR 类工具。
        /// </summary>
        /// <param name="toolName">工具名称，例如 ocr_image、mcp_paddle_ocr。</param>
        /// <returns>工具名包含任一 OCR 关键字（忽略大小写）时返回 true，否则返回 false。</returns>
        internal static bool IsOcrToolName(string toolName)
        {
            return OcrToolKeywords.Any(k => toolName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// 预处理 OCR 工具参数：AI 可能传文件名而非 base64，自动转换。
        /// 同时记录 OCR 调用参数格式提醒（input_data 必需，output_mode/file_type 可选）。
        /// </summary>
        /// <param name="toolName">工具名称，内部会先用 <see cref="IsOcrToolName"/> 做类型判定。</param>
        /// <param name="argumentsJson">工具调用的原始 JSON 参数串。</param>
        /// <returns>预处理后的 JSON 参数串；非 OCR 工具、参数为空或预处理失败时原样返回入参。</returns>
        /// <remarks>
        /// 方法内部吞掉全部异常并写入 <see cref="Logger"/>，不向调用方抛出，
        /// 以保证 OCR 预处理失败不会中断工具调用链路。
        /// </remarks>
        internal static string SanitizeOcrToolArguments(string toolName, string argumentsJson)
        {
            if (!IsOcrToolName(toolName))
                return argumentsJson;

            // ── OCR 参数格式提醒 ──
            Logger.Info($"[OCR]  调用 OCR 工具 `{toolName}`，期望参数格式:\n" +
                "  • input_data (必需): 文件路径、URL 或 Base64 字符串\n" +
                "  • output_mode (可选, 默认 \"simple\"): \"simple\" | \"detailed\"\n" +
                "  • file_type (可选): \"image\" | \"pdf\" | null（input_data 为 URL 时必需）");

            try
            {
                var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson);
                if (args == null || args.Count == 0)
                {
                    Logger.Warn($"[OCR]  OCR 工具 `{toolName}` 未收到任何参数！请确保传入 input_data。");
                    return argumentsJson;
                }

                var imageParamNames = new[] { "input_data", "input", "data", "image", "image_base64", "base64", "image_data", "image_path", "file_path", "path", "file_url", "url", "image_url" };
                string? imageKey = null;
                string? imageValue = null;

                foreach (var kvp in args)
                {
                    if (imageParamNames.Any(n => string.Equals(kvp.Key, n, StringComparison.OrdinalIgnoreCase)))
                    {
                        imageKey = kvp.Key;
                        imageValue = kvp.Value.GetString();
                        break;
                    }
                }

                if (imageKey == null || string.IsNullOrWhiteSpace(imageValue))
                    return argumentsJson;

                // 粗略判断是否已经是 base64：长度足够大、无空白、且只含 base64 字符集。
                // 已内联的数据无需重复读取文件，直接放行可避免对大体积字符串做无谓 IO。
                bool isProbablyBase64 = imageValue.Length > 200
                    && !imageValue.Contains(' ')
                    && !imageValue.Contains('\n')
                    && imageValue.All(c => char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=');

                if (isProbablyBase64)
                    return argumentsJson;

                // ── URL 检测：如果是 URL 且未提供 file_type，发出提醒 ──
                bool isUrl = imageValue.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                          || imageValue.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                if (isUrl)
                {
                    bool hasFileType = args.ContainsKey("file_type") || args.ContainsKey("type");
                    if (!hasFileType)
                    {
                        Logger.Warn($"[OCR]  input_data 为 URL 但未提供 file_type 参数！\n" +
                            "  • 请补充 \"file_type\": \"image\" 或 \"file_type\": \"pdf\"\n" +
                            $"  • 当前 URL: {imageValue.Substring(0, Math.Min(80, imageValue.Length))}...");
                    }
                }

                if (File.Exists(imageValue))
                {
                    try
                    {
                        byte[] fileBytes = File.ReadAllBytes(imageValue);
                        string base64 = Convert.ToBase64String(fileBytes);
                        args[imageKey] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(base64));
                        return JsonSerializer.Serialize(args);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[OCR-Sanitize] 读取本地文件失败: {ex.Message}");
                        return argumentsJson;
                    }
                }

                // 模型常传入裸文件名（如 clipboard 截图），此处按约定目录按文件名回查。
                string fileName = Path.GetFileName(imageValue);
                var searchDirs = new List<string>();

                string clipboardDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepSeekVS", "temp", "clipboard");
                if (Directory.Exists(clipboardDir)) searchDirs.Add(clipboardDir);

                string contextDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepSeekVS", "temp", "context");
                if (Directory.Exists(contextDir)) searchDirs.Add(contextDir);

                string? resolvedPath = null;
                foreach (var dir in searchDirs.Distinct())
                {
                    string candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate)) { resolvedPath = candidate; break; }
                    try
                    {
                        var matches = Directory.GetFiles(dir, fileName, SearchOption.TopDirectoryOnly);
                        if (matches.Length > 0) { resolvedPath = matches[0]; break; }
                    }
                    // 目录不可读时忽略该目录，继续尝试下一个候选目录。
                    catch { }
                }

                if (resolvedPath != null)
                {
                    byte[] fileBytes = File.ReadAllBytes(resolvedPath);
                    string base64 = Convert.ToBase64String(fileBytes);
                    args[imageKey] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(base64));
                    return JsonSerializer.Serialize(args);
                }

                Logger.Warn($"[OCR-Sanitize] 无法解析文件 '{imageValue}'");
                return argumentsJson;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[OCR-Sanitize] 参数预处理异常: {ex.Message}");
                return argumentsJson;
            }
        }
    }
}
