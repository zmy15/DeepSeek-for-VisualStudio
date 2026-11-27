using System;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>
    /// 运行期热更新所依赖的设置快照。用于判断一次设置变更真正影响了哪些子系统。
    /// </summary>
    internal sealed record RuntimeSettingsSnapshot(
        string ApiKey,
        string CustomApiKey,
        string ApiBaseUrl,
        string SelectedModel,
        string CustomModelName,
        string CustomVisionModels,
        string CustomModelMetadata,
        string ActiveCustomModel,
        string ActiveModelSource,
        int TokenBudgetPercent,
        string ModelMaxTokenLimits,
        bool IsThinkingEnabled,
        string ReasoningEffort,
        string OcrEngine,
        bool EnableWebSearch,
        string SearchProvider,
        string BaiduApiKey,
        string WebFetchMode,
        string BingApiKey,
        string ApprovalMode,
        bool EnableAutoSkillRouting,
        string AppendMessageMode,
        int InputBoxHeight,
        int BottomAreaScalePercent,
        int WebView2ZoomPercent,
        bool EnableEvaluationExport = false,
        string CustomEndpointsData = "",
        string ActiveCustomEndpointId = "",
        string OfficialOcrPurpose = "",
        bool EnableAutoCompression = true,
        int CompressionThreshold = 85,
        int PreserveRecentTurns = 3,
        int LlmTimeoutSeconds = 300)
    {
        public static RuntimeSettingsSnapshot Capture(DeepSeekOptionsPage options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            return new RuntimeSettingsSnapshot(
                options.ApiKey ?? string.Empty,
                options.CustomApiKey ?? string.Empty,
                options.ApiBaseUrl ?? string.Empty,
                options.SelectedModel ?? string.Empty,
                options.CustomModelName ?? string.Empty,
                options.CustomVisionModels ?? string.Empty,
                options.CustomModelMetadata ?? string.Empty,
                options.ActiveCustomModel ?? string.Empty,
                options.ActiveModelSource ?? string.Empty,
                options.TokenBudget,
                options.ModelMaxTokenLimits ?? string.Empty,
                options.IsThinkingEnabled,
                options.ReasoningEffort ?? string.Empty,
                options.OcrEngine ?? string.Empty,
                options.EnableWebSearch,
                options.SearchProvider ?? string.Empty,
                options.BaiduApiKey ?? string.Empty,
                options.WebFetchMode ?? DeepSeekOptionsPage.DefaultWebFetchMode,
                options.BingApiKey ?? string.Empty,
                options.ApprovalMode ?? string.Empty,
                options.EnableAutoSkillRouting,
                options.AppendMessageMode.ToString(),
                options.InputBoxHeight,
                options.BottomAreaScalePercent,
                options.WebView2ZoomPercent,
                options.EnableEvaluationExport,
                options.CustomEndpointsData ?? string.Empty,
                options.ActiveCustomEndpointId ?? string.Empty,
                options.OfficialOcrPurpose ?? string.Empty,
                options.EnableAutoCompression,
                options.CompressionThreshold,
                options.PreserveRecentTurns,
                options.LlmTimeoutSeconds);
        }

        public override string ToString() => "RuntimeSettingsSnapshot { redacted }";
    }

    /// <summary>两个设置快照之间的差异，按需要执行的热更新类别分组。</summary>
    internal readonly record struct RuntimeSettingsChangeSet(
        bool EndpointChanged,
        bool OfficialApiKeyChanged,
        bool ModelControlsChanged,
        bool ContextChanged,
        bool ThinkingChanged,
        bool ApprovalChanged,
        bool AutoSkillRoutingChanged,
        bool OcrChanged,
        bool WebSearchChanged,
        bool LayoutChanged,
        bool DeveloperChanged,
        bool CompressionChanged = false,
        bool LlmTimeoutChanged = false)
    {
        public bool HasChanges =>
            EndpointChanged ||
            OfficialApiKeyChanged ||
            ModelControlsChanged ||
            ContextChanged ||
            ThinkingChanged ||
            ApprovalChanged ||
            AutoSkillRoutingChanged ||
            OcrChanged ||
            WebSearchChanged ||
            LayoutChanged ||
            DeveloperChanged ||
            CompressionChanged ||
            LlmTimeoutChanged;

        public static RuntimeSettingsChangeSet All { get; } = new(
            EndpointChanged: true,
            OfficialApiKeyChanged: true,
            ModelControlsChanged: true,
            ContextChanged: true,
            ThinkingChanged: true,
            ApprovalChanged: true,
            AutoSkillRoutingChanged: true,
            OcrChanged: true,
            WebSearchChanged: true,
            LayoutChanged: true,
            DeveloperChanged: true,
            CompressionChanged: true,
            LlmTimeoutChanged: true);

        public static RuntimeSettingsChangeSet Between(
            RuntimeSettingsSnapshot previous,
            RuntimeSettingsSnapshot current)
        {
            bool modelControlsChanged =
                Changed(previous.SelectedModel, current.SelectedModel) ||
                Changed(previous.CustomModelName, current.CustomModelName) ||
                Changed(previous.CustomVisionModels, current.CustomVisionModels) ||
                Changed(previous.CustomModelMetadata, current.CustomModelMetadata) ||
                Changed(previous.ActiveCustomModel, current.ActiveCustomModel) ||
                Changed(previous.CustomEndpointsData, current.CustomEndpointsData) ||
                Changed(previous.ActiveModelSource, current.ActiveModelSource) ||
                Changed(previous.ActiveCustomEndpointId, current.ActiveCustomEndpointId);
            bool contextChanged =
                previous.TokenBudgetPercent != current.TokenBudgetPercent ||
                Changed(previous.ModelMaxTokenLimits, current.ModelMaxTokenLimits);
            bool compressionChanged =
                previous.EnableAutoCompression != current.EnableAutoCompression ||
                previous.CompressionThreshold != current.CompressionThreshold ||
                previous.PreserveRecentTurns != current.PreserveRecentTurns;

            return new RuntimeSettingsChangeSet(
                EndpointChanged:
                    Changed(previous.ApiKey, current.ApiKey) ||
                    Changed(previous.CustomApiKey, current.CustomApiKey) ||
                    Changed(previous.ApiBaseUrl, current.ApiBaseUrl) ||
                    Changed(previous.CustomEndpointsData, current.CustomEndpointsData) ||
                    modelControlsChanged,
                OfficialApiKeyChanged: Changed(previous.ApiKey, current.ApiKey),
                ModelControlsChanged: modelControlsChanged,
                ContextChanged: contextChanged,
                ThinkingChanged:
                    previous.IsThinkingEnabled != current.IsThinkingEnabled ||
                    Changed(previous.ReasoningEffort, current.ReasoningEffort),
                ApprovalChanged: Changed(previous.ApprovalMode, current.ApprovalMode),
                AutoSkillRoutingChanged:
                    previous.EnableAutoSkillRouting != current.EnableAutoSkillRouting,
                OcrChanged:
                    Changed(previous.OcrEngine, current.OcrEngine) ||
                    Changed(previous.OfficialOcrPurpose, current.OfficialOcrPurpose),
                WebSearchChanged:
                    previous.EnableWebSearch != current.EnableWebSearch ||
                    Changed(previous.SearchProvider, current.SearchProvider) ||
                    Changed(previous.WebFetchMode, current.WebFetchMode) ||
                    Changed(previous.BaiduApiKey, current.BaiduApiKey) ||
                    Changed(previous.BingApiKey, current.BingApiKey),
                LayoutChanged:
                    previous.InputBoxHeight != current.InputBoxHeight ||
                    previous.BottomAreaScalePercent != current.BottomAreaScalePercent ||
                    previous.WebView2ZoomPercent != current.WebView2ZoomPercent,
                DeveloperChanged:
                    previous.EnableEvaluationExport != current.EnableEvaluationExport,
                CompressionChanged: compressionChanged,
                LlmTimeoutChanged:
                    previous.LlmTimeoutSeconds != current.LlmTimeoutSeconds);
        }

        private static bool Changed(string left, string right)
            => !string.Equals(left, right, StringComparison.Ordinal);
    }
}
