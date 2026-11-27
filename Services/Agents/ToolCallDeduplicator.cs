using System.Collections.Generic;
using System.Text.Json;
using DeepSeek_v4_for_VisualStudio.Models;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// 同一批次工具调用的去重与参数规范化工具（从 <see cref="BaseAgent"/> 下沉）。
    /// 全部为无状态静态方法；<see cref="BaseAgent"/> 上保留同名转发以维持既有测试的编译契约。
    /// </summary>
    internal static class ToolCallDeduplicator
    {
        /// <summary>
        /// 对同一批次中的工具调用进行去重。
        /// 相同函数名 + 相同参数（规范化 JSON）的调用只保留第一个，
        /// 后续重复调用映射到第一个的结果。
        /// </summary>
        /// <param name="toolCalls">本批次工具调用列表。</param>
        /// <returns>
        /// dedupedIndices: 去重后需要实际执行的 toolCalls 索引列表。
        /// dedupMapping: 源索引 → 重复索引列表的映射（用于将结果复制到重复调用）。
        /// </returns>
        internal static (List<int> dedupedIndices, Dictionary<int, List<int>> dedupMapping) DeduplicateToolCalls(
            List<ToolCall> toolCalls)
        {
            var dedupedIndices = new List<int>();
            var dedupMapping = new Dictionary<int, List<int>>();
            // key: "functionName|normalizedArgs" → 第一个出现的索引
            var seen = new Dictionary<string, int>();

            for (int i = 0; i < toolCalls.Count; i++)
            {
                var tc = toolCalls[i];
                string normalizedArgs = NormalizeToolArguments(tc.Function.Arguments);
                string key = $"{tc.Function.Name}|{normalizedArgs}";

                if (seen.TryGetValue(key, out int firstIdx))
                {
                    // 重复调用：映射到第一个出现的索引
                    if (!dedupMapping.ContainsKey(firstIdx))
                        dedupMapping[firstIdx] = new List<int>();
                    dedupMapping[firstIdx].Add(i);
                }
                else
                {
                    seen[key] = i;
                    dedupedIndices.Add(i);
                }
            }

            return (dedupedIndices, dedupMapping);
        }

        /// <summary>
        /// 规范化工具参数 JSON：移除空格/换行等无意义差异，
        /// 确保相同语义的参数能匹配到同一个去重 key。
        /// </summary>
        /// <param name="argumentsJson">工具参数 JSON 文本。</param>
        /// <returns>紧凑序列化后的参数文本；解析失败时返回去除首尾空白的原文。</returns>
        internal static string NormalizeToolArguments(string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return string.Empty;

            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                // 重新序列化，使用紧凑格式（无缩进、无多余空格）
                return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
                {
                    WriteIndented = false
                });
            }
            catch
            {
                // JSON 解析失败时，回退到原始字符串（去除首尾空白）
                return argumentsJson.Trim();
            }
        }

        /// <summary>
        /// 从工具参数 JSON 中提取目标文件路径（支持多种字段名和集合类型）。
        /// 覆盖所有文件相关工具的参数模式。
        /// </summary>
        /// <param name="toolName">工具名（保留参数以便后续按工具特化）。</param>
        /// <param name="argumentsJson">工具参数 JSON 文本。</param>
        /// <returns>提取到的首个路径；未找到时返回 <c>null</c>。</returns>
        internal static string? ExtractFilePathFromToolArgs(string toolName, string argumentsJson)
        {
            if (string.IsNullOrEmpty(argumentsJson)) return null;

            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                var root = doc.RootElement;

                // ── 单文件路径字段（优先级从高到低）──
                string[] singlePathKeys = { "filePath", "path", "file", "dirPath", "target" };
                foreach (var key in singlePathKeys)
                {
                    if (root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                    {
                        string? val = prop.GetString();
                        if (!string.IsNullOrWhiteSpace(val)) return val;
                    }
                }

                // ── 多文件路径数组（取第一个为代表）──
                string[] arrayPathKeys = { "paths", "files", "targets", "filePaths" };
                foreach (var key in arrayPathKeys)
                {
                    if (root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                    {
                        var first = arr[0];
                        if (first.ValueKind == JsonValueKind.String)
                        {
                            string? val = first.GetString();
                            if (!string.IsNullOrWhiteSpace(val)) return val;
                        }
                    }
                }

                // ── 编辑类工具的 special 字段 ──
                // replace_string_in_file: oldFilePath → 返回 filePath（主文件）
                // apply_patch: filePath 已被覆盖
                // create_directory: dirPath 已被覆盖
            }
            catch { }
            return null;
        }
    }
}
