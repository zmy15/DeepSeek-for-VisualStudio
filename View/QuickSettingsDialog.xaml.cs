using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// Compact settings surface for the options users change most often.
    /// Advanced tuning remains in the original Tools > Options page.
    /// </summary>
    public partial class QuickSettingsDialog : Window
    {
        private readonly DeepSeekOptionsPage _options;
        private readonly Action _openAdvancedSettings;
        private readonly Action<Window> _openMcpSettings;
        private bool _isLoading;

        public QuickSettingsDialog(
            DeepSeekOptionsPage options,
            Action openAdvancedSettings,
            Action<Window> openMcpSettings)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _openAdvancedSettings = openAdvancedSettings ?? throw new ArgumentNullException(nameof(openAdvancedSettings));
            _openMcpSettings = openMcpSettings ?? throw new ArgumentNullException(nameof(openMcpSettings));

            InitializeComponent();

            if (ThemeService.Instance.IsLight)
                ApplyLightPalette();

            ApplyLocalization();
            LoadSettings();
        }

        private void ApplyLocalization()
        {
            var L = LocalizationService.Instance;

            Title = L["settings.quick.title"];
            TitleLabel.Text = L["settings.quick.title"];
            SubtitleLabel.Text = L["settings.quick.subtitle"];

            ConnectionSectionLabel.Text = L["settings.quick.connection"];
            ConnectionHintLabel.Text = L["settings.quick.connectionHint"];
            ApiKeyLabel.Text = L["settings.quick.apiKeyLabel"];
            ApiKeyHintLabel.Text = L["settings.quick.apiKeyHint"];
            GetApiKeyButton.Content = L["settings.quick.getApiKey"];

            ModelSectionLabel.Text = L["settings.quick.model"];
            ModelHintLabel.Text = L["settings.quick.modelHint"];
            ModelLabel.Text = L["settings.quick.modelLabel"];
            ThinkingCheckBox.Content = L["settings.quick.thinking"];
            ReasoningLabel.Text = L["settings.quick.reasoningLabel"];

            ToolsSectionLabel.Text = L["settings.quick.tools"];
            ToolsHintLabel.Text = L["settings.quick.toolsHint"];
            WebSearchLabel.Text = L["settings.enableWebSearch.displayName"];
            WebSearchCheckBox.Content = L["settings.quick.enabled"];
            ApprovalLabel.Text = L["settings.approvalMode.displayName"];
            McpLabel.Text = L["settings.quick.mcpLabel"];
            OpenMcpSettingsButton.Content = L["settings.quick.manageMcp"];

            OcrSectionLabel.Text = L["settings.quick.ocr"];
            OcrHintLabel.Text = L["settings.quick.ocrHint"];
            OcrEngineLabel.Text = L["settings.quick.ocrEngineLabel"];
            OcrPurposeLabel.Text = L["settings.quick.ocrPurposeLabel"];

            AppearanceSectionLabel.Text = L["settings.quick.appearance"];
            AppearanceHintLabel.Text = L["settings.quick.appearanceHint"];
            LanguageLabel.Text = L["settings.quick.languageLabel"];
            ThemeLabel.Text = L["settings.themeMode.displayName"];

            EditorSectionLabel.Text = L["settings.quick.editor"];
            EditorHintLabel.Text = L["settings.quick.editorHint"];
            AutoCompleteCheckBox.Content = L["settings.autocompleteEnabled.displayName"];
            DiffMarkersCheckBox.Content = L["settings.showDiffMarkers.displayName"];

            AdvancedHintLabel.Text = L["settings.quick.advancedHint"];
            AdvancedSettingsButton.Content = L["settings.quick.advanced"];
            SaveHintLabel.Text = L["settings.quick.saveHint"];
            CancelButton.Content = L["general.cancel"];
            SaveButton.Content = L["general.save"];
        }

        private void LoadSettings()
        {
            _isLoading = true;
            try
            {
                var values = QuickSettingsValues.Capture(_options);

                ApiKeyPasswordBox.Password = values.ApiKey;
                UpdateApiKeyStatus();

                var modelChoices = BuildModelChoices(values.ModelChoice);
                ModelComboBox.ItemsSource = modelChoices;
                ModelComboBox.DisplayMemberPath = nameof(ModelChoiceItem.Display);
                ModelComboBox.SelectedItem = modelChoices.FirstOrDefault(choice =>
                    string.Equals(choice.Display, values.ModelChoice, StringComparison.OrdinalIgnoreCase))
                    ?? modelChoices.FirstOrDefault();

                var efforts = ResolveEffortLevels(
                    ModelComboBox.SelectedItem as ModelChoiceItem,
                    values.ReasoningEffort);
                EffortComboBox.ItemsSource = efforts;
                EffortComboBox.SelectedItem = efforts.FirstOrDefault(level =>
                    string.Equals(level, values.ReasoningEffort, StringComparison.OrdinalIgnoreCase))
                    ?? efforts.FirstOrDefault();

                ThinkingCheckBox.IsChecked = values.IsThinkingEnabled;
                WebSearchCheckBox.IsChecked = values.EnableWebSearch;

                var searchProviders = BuildSearchProviderChoices();
                BindChoices(SearchProviderComboBox, searchProviders, values.SearchProvider);

                var approvalModes = BuildApprovalModeChoices();
                BindChoices(ApprovalModeComboBox, approvalModes, values.ApprovalMode);

                var languages = BuildLanguageChoices();
                BindChoices(LanguageComboBox, languages, values.Language);

                var themes = BuildThemeChoices();
                BindChoices(ThemeComboBox, themes, values.ThemeMode);

                AutoCompleteCheckBox.IsChecked = values.AutoCompleteEnabled;
                DiffMarkersCheckBox.IsChecked = values.ShowDiffMarkersInEditor;
                UpdateSearchProviderAvailability();
                RefreshOcrControls(
                    values.OcrEngine,
                    values.OfficialOcrPurpose);
            }
            finally
            {
                _isLoading = false;
            }
        }

        private IReadOnlyList<ModelChoiceItem> BuildModelChoices(string selectedDisplay)
        {
            var choices = new List<ModelChoiceItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visionModels = _options.GetVisionModels()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var metadata in OfficialModelCatalogService.GetMetadata())
            {
                if (!seen.Add(metadata.Display))
                    continue;

                bool isVision = metadata.HasModalityInfo
                    ? metadata.SupportsImageInput
                    : visionModels.Contains(metadata.Id);
                choices.Add(new ModelChoiceItem(
                    metadata.Display,
                    metadata.Id,
                    false,
                    string.Empty,
                    isVision));
            }

            var customMetadata = DeepSeekOptionsPage.ParseCustomModelMetadata(
                _options.CustomModelMetadata);
            string customSuffix = LocalizationService.Instance["chat.model.customSuffix"];
            foreach (var entry in _options.GetCustomModelEntries())
            {
                customMetadata.TryGetValue(entry.Model, out var extra);
                string label = DeepSeekOptionsPage.FormatModelChoice(
                    entry.Model,
                    extra?.DisplayName);
                string display = string.IsNullOrWhiteSpace(entry.EndpointName)
                    ? label + customSuffix
                    : $"{label} · {entry.EndpointName}{customSuffix}";
                if (!seen.Add(display))
                    continue;

                choices.Add(new ModelChoiceItem(
                    display,
                    entry.Model,
                    true,
                    entry.EndpointId,
                    visionModels.Contains(entry.Model)));
            }

            if (!string.IsNullOrWhiteSpace(selectedDisplay) &&
                !seen.Contains(selectedDisplay))
            {
                var config = DeepSeekEndpointResolver.Resolve(_options);
                choices.Add(new ModelChoiceItem(
                    selectedDisplay,
                    config.Model,
                    config.IsCustom,
                    config.EndpointId,
                    config.IsVision));
            }

            return choices;
        }

        private IReadOnlyList<string> ResolveEffortLevels(
            ModelChoiceItem? modelChoice,
            string? preferredEffort)
        {
            var levels = new List<string>();
            string selectedModelName = modelChoice?.Model ?? string.Empty;
            var official = OfficialModelCatalogService.GetMetadata()
                .FirstOrDefault(model =>
                    !string.IsNullOrWhiteSpace(selectedModelName) &&
                    string.Equals(
                        model.Id,
                        selectedModelName,
                        StringComparison.OrdinalIgnoreCase));
            if (official != null)
            {
                levels.AddRange(official.EffortLevelsOrEmpty);
            }
            else if (modelChoice?.IsCustom == true)
            {
                var customMetadata = DeepSeekOptionsPage.ParseCustomModelMetadata(
                    _options.CustomModelMetadata);
                foreach (var entry in _options.GetCustomModelEntries())
                {
                    if (modelChoice.IsCustom &&
                        !string.Equals(
                            entry.EndpointId,
                            modelChoice.EndpointId,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    customMetadata.TryGetValue(entry.Model, out var extra);
                    bool matches = string.Equals(
                            entry.Model,
                            modelChoice.Model,
                            StringComparison.OrdinalIgnoreCase);
                    if (!matches)
                        continue;

                    if (extra?.EffortLevels != null)
                        levels.AddRange(extra.EffortLevels);
                    break;
                }
            }

            if (levels.Count == 0)
            {
                string currentModel = DeepSeekEndpointResolver.Resolve(_options).Model;
                levels.AddRange(OfficialModelCatalogService.GetEffortLevels(currentModel));
            }

            if (levels.Count == 0)
                levels.AddRange(DeepSeekOptionsPage.DefaultReasoningEfforts);

            if (!string.IsNullOrWhiteSpace(preferredEffort) &&
                !levels.Contains(preferredEffort, StringComparer.OrdinalIgnoreCase))
            {
                levels.Add(preferredEffort!);
            }

            return levels.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private IReadOnlyList<LocalizedChoice> BuildSearchProviderChoices()
        {
            var L = LocalizationService.Instance;
            return new[]
            {
                new LocalizedChoice("DeepSeek", "DeepSeek"),
                new LocalizedChoice("Baidu", "Baidu"),
                new LocalizedChoice("Bing", "Bing"),
                new LocalizedChoice(
                    "DuckDuckGo",
                    L["websearch.searchEngine.duckduckgo"]),
            };
        }

        private IReadOnlyList<LocalizedChoice> BuildApprovalModeChoices()
        {
            var L = LocalizationService.Instance;
            return new[]
            {
                new LocalizedChoice("SmartBlock", L["approval.smartBlock"]),
                new LocalizedChoice("BlockAll", L["approval.blockAll"]),
                new LocalizedChoice("AllowAll", L["approval.allowAll"]),
            };
        }

        private IReadOnlyList<LocalizedChoice> BuildLanguageChoices()
        {
            var L = LocalizationService.Instance;
            return new[]
            {
                new LocalizedChoice("auto", L["skills.help.typeAuto"]),
                new LocalizedChoice("zh-CN", L["settings.quick.languageChinese"]),
                new LocalizedChoice("en", "English"),
            };
        }

        private IReadOnlyList<LocalizedChoice> BuildThemeChoices()
        {
            var L = LocalizationService.Instance;
            return new[]
            {
                new LocalizedChoice("Auto", L["settings.quick.themeAuto"]),
                new LocalizedChoice("Dark", L["theme.dark"]),
                new LocalizedChoice("Light", L["theme.light"]),
            };
        }

        private static void BindChoices(
            ComboBox comboBox,
            IReadOnlyList<LocalizedChoice> choices,
            string selectedValue)
        {
            comboBox.ItemsSource = choices;
            comboBox.DisplayMemberPath = nameof(LocalizedChoice.Display);
            comboBox.SelectedValuePath = nameof(LocalizedChoice.Value);
            comboBox.SelectedValue = selectedValue;
            if (comboBox.SelectedIndex < 0 && comboBox.Items.Count > 0)
                comboBox.SelectedIndex = 0;
        }

        private static string GetSelectedChoiceValue(ComboBox comboBox, string fallback)
            => comboBox.SelectedValue as string ?? fallback;

        private void ApiKeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            UpdateApiKeyStatus();
            if (!_isLoading)
                RefreshOcrControls();
        }

        private void UpdateApiKeyStatus()
        {
            bool configured = !string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password);
            ApiKeyStatusLabel.Text = configured
                ? LocalizationService.Instance["settings.quick.apiKeyConfigured"]
                : LocalizationService.Instance["settings.quick.apiKeyNotConfigured"];
            ApiKeyStatusLabel.Foreground = new SolidColorBrush(
                configured
                    ? Color.FromRgb(0x4E, 0xC9, 0xB0)
                    : Color.FromRgb(0xC5, 0x86, 0xC0));
        }

        private void WebSearchCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
            => UpdateSearchProviderAvailability();

        private IReadOnlyList<LocalizedChoice> BuildOcrEngineChoices(
            ModelChoiceItem? selectedModel)
        {
            var L = LocalizationService.Instance;
            var choices = new List<LocalizedChoice>
            {
                new("Windows Built-in", L["settings.quick.ocrWindows"]),
            };
            if (Enum.GetNames(typeof(OcrEngineType))
                .Any(name => string.Equals(name, "PaddleOCR", StringComparison.Ordinal)))
            {
                choices.Add(new LocalizedChoice(
                    "PaddleOCR-Sharp",
                    L["settings.quick.ocrPaddle"]));
            }

            bool isCustom = selectedModel?.IsCustom
                ?? DeepSeekEndpointResolver.Resolve(_options).IsCustom;
            if (!isCustom && !string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password))
            {
                choices.Add(new LocalizedChoice(
                    "DeepSeek Official",
                    L["settings.quick.ocrOfficial"]));
            }
            return choices;
        }

        private IReadOnlyList<LocalizedChoice> BuildOcrPurposeChoices()
        {
            var L = LocalizationService.Instance;
            return new[]
            {
                new LocalizedChoice(
                    DeepSeekOcrClient.TextRecognitionPurpose,
                    L["settings.quick.ocrPurposeText"]),
                new LocalizedChoice(
                    DeepSeekOcrClient.RichLayoutRecognitionPurpose,
                    L["settings.quick.ocrPurposeRich"]),
            };
        }

        private void RefreshOcrControls(
            string? preferredEngine = null,
            string? preferredPurpose = null)
        {
            if (OcrSection == null || ModelComboBox == null)
                return;

            var selectedModel = ModelComboBox.SelectedItem as ModelChoiceItem;
            bool isVision = selectedModel?.IsVision
                ?? DeepSeekEndpointResolver.Resolve(_options).IsVision;
            OcrSection.Visibility = isVision
                ? Visibility.Collapsed
                : Visibility.Visible;
            if (isVision)
                return;

            string engine = preferredEngine
                ?? (OcrEngineComboBox.SelectedValue as string)
                ?? _options.OcrEngine;
            BindChoices(
                OcrEngineComboBox,
                BuildOcrEngineChoices(selectedModel),
                engine);

            string purpose = preferredPurpose
                ?? (OcrPurposeComboBox.SelectedValue as string)
                ?? _options.OfficialOcrPurpose;
            BindChoices(
                OcrPurposeComboBox,
                BuildOcrPurposeChoices(),
                purpose);
            UpdateOcrPurposeVisibility();
        }

        private void UpdateOcrPurposeVisibility()
        {
            if (OcrPurposeRow == null || OcrEngineComboBox == null)
                return;

            bool isOfficialOcr = string.Equals(
                OcrEngineComboBox.SelectedValue as string,
                "DeepSeek Official",
                StringComparison.Ordinal);
            OcrPurposeRow.Visibility = isOfficialOcr && OcrSection.Visibility == Visibility.Visible
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void ModelComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (_isLoading || EffortComboBox == null)
                return;

            string? currentEffort = EffortComboBox.SelectedItem as string;
            var selectedModel = ModelComboBox.SelectedItem as ModelChoiceItem;
            var efforts = ResolveEffortLevels(
                selectedModel,
                currentEffort);
            EffortComboBox.ItemsSource = efforts;
            EffortComboBox.SelectedItem = efforts.FirstOrDefault(level =>
                string.Equals(level, currentEffort, StringComparison.OrdinalIgnoreCase))
                ?? efforts.FirstOrDefault(
                    level => string.Equals(level, "high", StringComparison.OrdinalIgnoreCase))
                ?? efforts.FirstOrDefault();
            RefreshOcrControls();
        }

        private void OcrEngineComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
            => UpdateOcrPurposeVisibility();

        private void UpdateSearchProviderAvailability()
        {
            if (SearchProviderComboBox == null)
                return;

            SearchProviderComboBox.IsEnabled = WebSearchCheckBox.IsChecked == true;
        }

        private void OpenMcpSettingsButton_Click(object sender, RoutedEventArgs e)
            => _openMcpSettings(this);

        private void GetApiKeyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://platform.deepseek.com/api_keys",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Quick Settings] 打开 API Key 页面失败: {ex.Message}");
            }
        }

        private void AdvancedSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
            _openAdvancedSettings();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var values = new QuickSettingsValues(
                    ApiKey: ApiKeyPasswordBox.Password,
                    ModelChoice: (ModelComboBox.SelectedItem as ModelChoiceItem)?.Display
                        ?? string.Empty,
                    IsThinkingEnabled: ThinkingCheckBox.IsChecked == true,
                    ReasoningEffort: EffortComboBox.SelectedItem as string ?? "high",
                    EnableWebSearch: WebSearchCheckBox.IsChecked == true,
                    SearchProvider: GetSelectedChoiceValue(
                        SearchProviderComboBox,
                        DeepSeekOptionsPage.DefaultSearchProvider),
                    ApprovalMode: GetSelectedChoiceValue(
                        ApprovalModeComboBox,
                        "SmartBlock"),
                    ThemeMode: GetSelectedChoiceValue(ThemeComboBox, "Auto"),
                    Language: GetSelectedChoiceValue(LanguageComboBox, "auto"),
                    AutoCompleteEnabled: AutoCompleteCheckBox.IsChecked == true,
                    ShowDiffMarkersInEditor: DiffMarkersCheckBox.IsChecked == true,
                    OcrEngine: GetSelectedChoiceValue(
                        OcrEngineComboBox,
                        "Windows Built-in"),
                    OfficialOcrPurpose: GetSelectedChoiceValue(
                        OcrPurposeComboBox,
                        DeepSeekOptionsPage.DefaultOfficialOcrPurpose));

                values.ApplyTo(_options);
                _options.SaveSettingsToStorage();
                _options.ApplyRuntimeHotUpdates();
                UnifiedSettingsSync.PushFromPage(_options);

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                Logger.Error($"[Quick Settings] 保存设置失败: {ex.Message}", ex);
                MessageBox.Show(
                    this,
                    string.Format(
                        LocalizationService.Instance["settings.quick.saveFailed"],
                        ex.Message),
                    LocalizationService.Instance["settings.quick.title"],
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ApplyLightPalette()
        {
            Resources["QuickComboBackground"] = CreateFrozenBrush(0xFF, 0xFF, 0xFF);
            Resources["QuickComboBorder"] = CreateFrozenBrush(0xC2, 0xC2, 0xC2);
            Resources["QuickComboHover"] = CreateFrozenBrush(0xEE, 0xEE, 0xEE);
            Resources["QuickComboPopup"] = CreateFrozenBrush(0xFF, 0xFF, 0xFF);
            Resources["QuickComboSelection"] = CreateFrozenBrush(0xCC, 0xE4, 0xF7);
            Resources["QuickComboArrow"] = CreateFrozenBrush(0x60, 0x60, 0x60);

            var map = new Dictionary<Color, Color>
            {
                [Color.FromRgb(0x1F, 0x1F, 0x1F)] = Color.FromRgb(0xF3, 0xF3, 0xF3),
                [Color.FromRgb(0x25, 0x25, 0x26)] = Color.FromRgb(0xEA, 0xEA, 0xEA),
                [Color.FromRgb(0x2D, 0x2D, 0x2D)] = Color.FromRgb(0xFF, 0xFF, 0xFF),
                [Color.FromRgb(0x34, 0x34, 0x34)] = Color.FromRgb(0xD0, 0xD0, 0xD0),
                [Color.FromRgb(0x4A, 0x4A, 0x4A)] = Color.FromRgb(0xC2, 0xC2, 0xC2),
                [Color.FromRgb(0xF5, 0xF5, 0xF5)] = Color.FromRgb(0x1D, 0x1D, 0x1D),
                [Color.FromRgb(0xF0, 0xF0, 0xF0)] = Color.FromRgb(0x20, 0x20, 0x20),
                [Color.FromRgb(0xE8, 0xE8, 0xE8)] = Color.FromRgb(0x20, 0x20, 0x20),
                [Color.FromRgb(0xD4, 0xD4, 0xD4)] = Color.FromRgb(0x28, 0x28, 0x28),
                [Color.FromRgb(0xAF, 0xAF, 0xAF)] = Color.FromRgb(0x55, 0x55, 0x55),
                [Color.FromRgb(0x8C, 0x8C, 0x8C)] = Color.FromRgb(0x60, 0x60, 0x60),
                [Color.FromRgb(0x77, 0x77, 0x77)] = Color.FromRgb(0x6A, 0x6A, 0x6A),
            };

            Swap(this, Control.BackgroundProperty, map);
            Swap(this, Control.ForegroundProperty, map);
            RewriteBrushes(this, map);
        }

        private static SolidColorBrush CreateFrozenBrush(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        private static void RewriteBrushes(
            DependencyObject root,
            IReadOnlyDictionary<Color, Color> map)
        {
            foreach (object? child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is not DependencyObject dependencyObject)
                    continue;

                Swap(dependencyObject, Control.BackgroundProperty, map);
                Swap(dependencyObject, Control.BorderBrushProperty, map);
                Swap(dependencyObject, Control.ForegroundProperty, map);
                Swap(dependencyObject, System.Windows.Documents.TextElement.ForegroundProperty, map);
                Swap(dependencyObject, System.Windows.Documents.TextElement.BackgroundProperty, map);
                RewriteBrushes(dependencyObject, map);
            }
        }

        private static void Swap(
            DependencyObject target,
            DependencyProperty property,
            IReadOnlyDictionary<Color, Color> map)
        {
            if (target.GetValue(property) is SolidColorBrush brush &&
                map.TryGetValue(brush.Color, out Color light))
            {
                var replacement = new SolidColorBrush(light);
                replacement.Freeze();
                target.SetValue(property, replacement);
            }
        }

        private sealed record ModelChoiceItem(
            string Display,
            string Model,
            bool IsCustom,
            string EndpointId,
            bool IsVision);

        private sealed record LocalizedChoice(string Value, string Display);
    }
}
