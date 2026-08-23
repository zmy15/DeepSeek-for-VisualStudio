using System;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    /// <summary>
    /// 工具结果类别（P2-B，序号 21：内部结构化 / 外部兼容字符串）。
    /// </summary>
    public enum ToolResultKind
    {
        /// <summary>执行成功</summary>
        Success = 0,

        /// <summary>工具级失败（Error: 前缀约定）</summary>
        ToolError = 1,

        /// <summary>超时被终止（Timeout: 前缀约定，归入 System 故障族）</summary>
        Timeout = 2,
    }

    /// <summary>
    /// 工具执行结果的类型化包装（P2-B 渐进迁移第一步）：
    /// 对外契约仍是 <see cref="Output"/> 字符串（喂给 LLM 的内容不变），
    /// 内部消费方（遥测 / 未来 Benchmark / UI 汇总）通过 <see cref="Kind"/> 与
    /// <see cref="Success"/> 获得机器可读语义，不再各自解析 emoji 约定。
    /// 旧工具无需任何改动 —— 分类在 BaseAgent 包装层统一完成。
    /// </summary>
    public sealed class ToolExecutionOutcome
    {
        public ToolResultKind Kind { get; init; }

        public bool Success => Kind == ToolResultKind.Success;

        /// <summary>原始结果字符串（与既有 LLM 契约完全一致）</summary>
        public string Output { get; init; } = string.Empty;

        public string ToolName { get; init; } = string.Empty;

        public long DurationMs { get; init; }

        public static ToolExecutionOutcome FromRaw(string toolName, string raw, long durationMs)
            => new()
            {
                Kind = Classify(raw),
                Output = raw ?? string.Empty,
                ToolName = toolName ?? string.Empty,
                DurationMs = durationMs,
            };

        /// <summary>
        /// 结果字符串约定的唯一权威解析点：
        /// Error: = 工具错误；Timeout: = 超时；其余 = 成功。
        /// </summary>
        public static ToolResultKind Classify(string? output)
        {
            if (string.IsNullOrEmpty(output)) return ToolResultKind.Success;
            if (output.StartsWith("Timeout: ", StringComparison.Ordinal)) return ToolResultKind.Timeout;
            if (output.StartsWith("Error: ", StringComparison.Ordinal)) return ToolResultKind.ToolError;
            return ToolResultKind.Success;
        }
    }
}
