using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Settings;
#pragma warning disable VSEXTPREVIEW_SETTINGS // Settings API is experimental (Phase 1.5 Step2b)

namespace DeepSeek_v4_for_VisualStudio
{
    /// <summary>
    /// P2 Step2b：在 VS2026 Unified Settings 中声明非敏感设置子集。
    ///
    /// 这些设置项将出现在 工具→设置 的新版界面中（可搜索、即时生效、自动渲染）。
    /// ApiKey 等敏感字段不在此处 —— 保持 DPAPI 私有存储。
    /// 运行时消费仍通过 DeepSeekOptionsPage.Instance（单一事实源）；
    /// 本声明的价值是「可见性」—— 用户在新设置界面能发现并调整这些行为。
    /// </summary>
    [VisualStudioContribution]
    internal static class DeepSeekUnifiedSettings
    {
        [VisualStudioContribution]
        internal static SettingCategory GeneralCategory { get; } =
            new("deepseekGeneral", "DeepSeek Chat 设置")
            {
                Description = "常用行为开关与预算（完整选项请见 工具→选项→DeepSeek Chat）",
                GenerateObserverClass = true,
            };

        // ── 行为开关 ──

        [VisualStudioContribution]
        internal static Setting.Boolean ThinkingEnabled { get; } =
            new("deepseekThinking", "深度思考模式", GeneralCategory, defaultValue: true)
            {
                Description = "启用后模型将进行更深入的推理后再回答。",
            };

        [VisualStudioContribution]
        internal static Setting.Boolean EnableWebSearch { get; } =
            new("deepseekWebSearch", "联网搜索", GeneralCategory, defaultValue: true)
            {
                Description = "启用后 Agent 可使用搜索引擎获取最新信息。",
            };

        [VisualStudioContribution]
        internal static Setting.Boolean ShowContextStats { get; } =
            new("deepseekContextStats", "上下文统计指示器", GeneralCategory, defaultValue: true)
            {
                Description = "在状态栏显示当前 Token 使用量。",
            };

        [VisualStudioContribution]
        internal static Setting.Boolean EnableIdeContextInjection { get; } =
            new("deepseekIdeContext", "注入编辑器上下文", GeneralCategory, defaultValue: true)
            {
                Description = "每条消息自动携带活动文件、选区、光标位置与诊断摘要。",
            };

        [VisualStudioContribution]
        internal static Setting.Boolean EnableTelemetryExport { get; } =
            new("deepseekTelemetryExport", "导出会话指标", GeneralCategory, defaultValue: true)
            {
                Description = "每次会话结束后导出 JSON 指标到本地 telemetry 目录。",
            };

        [VisualStudioContribution]
        internal static Setting.Boolean EnableAutoCompression { get; } =
            new("deepseekAutoCompression", "自动压缩长对话", GeneralCategory, defaultValue: true)
            {
                Description = "Token 使用率超阈值时自动压缩早期对话以保持响应质量。",
            };

        // ── 数值 ──

        [VisualStudioContribution]
        internal static Setting.Integer TokenBudget { get; } =
            new("deepseekTokenBudget", "Token 预算", GeneralCategory, defaultValue: 900_000)
            {
                Description = "单次会话的 Token 上限。",
            };
    }
}
