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
        string ActiveCustomModel,
        string ActiveModelSource,
        bool IsThinkingEnabled,
        string ReasoningEffort,
        string OcrEngine,
        bool EnableWebSearch,
        string SearchProvider,
        string BaiduApiKey,
        string BingApiKey,
        string ApprovalMode,
        bool EnableAutoSkillRouting,
        string AppendMessageMode,
        int InputBoxHeight,
        int BottomAreaScalePercent,
        int WebView2ZoomPercent)
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
                options.ActiveCustomModel ?? string.Empty,
                options.ActiveModelSource ?? string.Empty,
                options.IsThinkingEnabled,
                options.ReasoningEffort ?? string.Empty,
                options.OcrEngine ?? string.Empty,
                options.EnableWebSearch,
                options.SearchProvider ?? string.Empty,
                options.BaiduApiKey ?? string.Empty,
                options.BingApiKey ?? string.Empty,
                options.ApprovalMode ?? string.Empty,
                options.EnableAutoSkillRouting,
                options.AppendMessageMode.ToString(),
                options.InputBoxHeight,
                options.BottomAreaScalePercent,
                options.WebView2ZoomPercent);
        }

        public override string ToString() => "RuntimeSettingsSnapshot { redacted }";
    }

    /// <summary>两个设置快照之间的差异，按需要执行的热更新类别分组。</summary>
    internal readonly record struct RuntimeSettingsChangeSet(
        bool EndpointChanged,
        bool OfficialApiKeyChanged,
        bool ModelControlsChanged,
        bool ThinkingChanged,
        bool ApprovalChanged,
        bool AutoSkillRoutingChanged,
        bool OcrChanged,
        bool WebSearchChanged,
        bool LayoutChanged)
    {
        public bool HasChanges =>
            EndpointChanged ||
            OfficialApiKeyChanged ||
            ModelControlsChanged ||
            ThinkingChanged ||
            ApprovalChanged ||
            AutoSkillRoutingChanged ||
            OcrChanged ||
            WebSearchChanged ||
            LayoutChanged;

        public static RuntimeSettingsChangeSet All { get; } = new(
            EndpointChanged: true,
            OfficialApiKeyChanged: true,
            ModelControlsChanged: true,
            ThinkingChanged: true,
            ApprovalChanged: true,
            AutoSkillRoutingChanged: true,
            OcrChanged: true,
            WebSearchChanged: true,
            LayoutChanged: true);

        public static RuntimeSettingsChangeSet Between(
            RuntimeSettingsSnapshot previous,
            RuntimeSettingsSnapshot current)
        {
            bool modelControlsChanged =
                Changed(previous.SelectedModel, current.SelectedModel) ||
                Changed(previous.CustomModelName, current.CustomModelName) ||
                Changed(previous.CustomVisionModels, current.CustomVisionModels) ||
                Changed(previous.ActiveCustomModel, current.ActiveCustomModel) ||
                Changed(previous.ActiveModelSource, current.ActiveModelSource);

            return new RuntimeSettingsChangeSet(
                EndpointChanged:
                    Changed(previous.ApiKey, current.ApiKey) ||
                    Changed(previous.CustomApiKey, current.CustomApiKey) ||
                    Changed(previous.ApiBaseUrl, current.ApiBaseUrl) ||
                    modelControlsChanged,
                OfficialApiKeyChanged: Changed(previous.ApiKey, current.ApiKey),
                ModelControlsChanged: modelControlsChanged,
                ThinkingChanged:
                    previous.IsThinkingEnabled != current.IsThinkingEnabled ||
                    Changed(previous.ReasoningEffort, current.ReasoningEffort),
                ApprovalChanged: Changed(previous.ApprovalMode, current.ApprovalMode),
                AutoSkillRoutingChanged:
                    previous.EnableAutoSkillRouting != current.EnableAutoSkillRouting,
                OcrChanged: Changed(previous.OcrEngine, current.OcrEngine),
                WebSearchChanged:
                    previous.EnableWebSearch != current.EnableWebSearch ||
                    Changed(previous.SearchProvider, current.SearchProvider) ||
                    Changed(previous.BaiduApiKey, current.BaiduApiKey) ||
                    Changed(previous.BingApiKey, current.BingApiKey),
                LayoutChanged:
                    previous.InputBoxHeight != current.InputBoxHeight ||
                    previous.BottomAreaScalePercent != current.BottomAreaScalePercent ||
                    previous.WebView2ZoomPercent != current.WebView2ZoomPercent);
        }

        private static bool Changed(string left, string right)
            => !string.Equals(left, right, StringComparison.Ordinal);
    }
}
