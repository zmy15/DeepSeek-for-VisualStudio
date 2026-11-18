using System;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>
    /// Values owned by the quick settings dialog. Applying this object deliberately
    /// writes only common settings so advanced tuning remains in the legacy page.
    /// </summary>
    internal sealed record QuickSettingsValues(
        string ApiKey,
        string ModelChoice,
        bool IsThinkingEnabled,
        string ReasoningEffort,
        bool EnableWebSearch,
        string SearchProvider,
        string ApprovalMode,
        string ThemeMode,
        string Language,
        bool AutoCompleteEnabled,
        bool ShowDiffMarkersInEditor,
        string OcrEngine,
        string OfficialOcrPurpose)
    {
        public static QuickSettingsValues Capture(DeepSeekOptionsPage options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            return new QuickSettingsValues(
                options.ApiKey ?? string.Empty,
                options.GetSelectedModelChoice(),
                options.IsThinkingEnabled,
                options.ReasoningEffort ?? "high",
                options.EnableWebSearch,
                options.SearchProvider ?? DeepSeekOptionsPage.DefaultSearchProvider,
                options.ApprovalMode ?? "SmartBlock",
                options.ThemeModeString,
                options.Language ?? "auto",
                options.AutoCompleteEnabled,
                options.ShowDiffMarkersInEditor,
                options.OcrEngine ?? "Windows Built-in",
                options.OfficialOcrPurpose ?? DeepSeekOptionsPage.DefaultOfficialOcrPurpose);
        }

        public void ApplyTo(DeepSeekOptionsPage options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            options.ApiKey = (ApiKey ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(ModelChoice))
                options.SetSelectedModelChoice(ModelChoice);

            options.IsThinkingEnabled = IsThinkingEnabled;
            options.ReasoningEffort = string.IsNullOrWhiteSpace(ReasoningEffort)
                ? "high"
                : ReasoningEffort;
            options.EnableWebSearch = EnableWebSearch;
            options.SearchProvider = string.IsNullOrWhiteSpace(SearchProvider)
                ? DeepSeekOptionsPage.DefaultSearchProvider
                : SearchProvider;
            options.ApprovalMode = string.IsNullOrWhiteSpace(ApprovalMode)
                ? "SmartBlock"
                : ApprovalMode;
            options.ThemeModeString = string.IsNullOrWhiteSpace(ThemeMode)
                ? "Auto"
                : ThemeMode;
            options.Language = string.IsNullOrWhiteSpace(Language)
                ? "auto"
                : Language;
            options.AutoCompleteEnabled = AutoCompleteEnabled;
            options.ShowDiffMarkersInEditor = ShowDiffMarkersInEditor;
            options.OcrEngine = string.IsNullOrWhiteSpace(OcrEngine)
                ? "Windows Built-in"
                : OcrEngine;
            options.OfficialOcrPurpose = DeepSeekOptionsPage.NormalizeOfficialOcrPurpose(
                OfficialOcrPurpose);
        }

        public override string ToString() => "QuickSettingsValues { redacted }";
    }
}
