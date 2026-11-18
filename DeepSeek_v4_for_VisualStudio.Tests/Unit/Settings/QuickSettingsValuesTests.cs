using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Tests.Fixtures;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Settings;

public class QuickSettingsValuesTests
{
    [Fact]
    public void ApplyTo_UpdatesCommonSettings()
    {
        var options = DeepSeekOptionsPageTestFactory.Create();

        var values = new QuickSettingsValues(
            ApiKey: "  official-key  ",
            ModelChoice: "deepseek-chat",
            IsThinkingEnabled: false,
            ReasoningEffort: "max",
            EnableWebSearch: false,
            SearchProvider: "DuckDuckGo",
            ApprovalMode: "AllowAll",
            ThemeMode: "Light",
            Language: "en",
            AutoCompleteEnabled: true,
            ShowDiffMarkersInEditor: false,
            OcrEngine: "DeepSeek Official",
            OfficialOcrPurpose: DeepSeekOcrClient.RichLayoutRecognitionPurpose,
            AppendMessageMode: "Guidance",
            InputBoxHeight: 72,
            BottomAreaScalePercent: 120,
            WebView2ZoomPercent: 130);

        values.ApplyTo(options);

        options.ApiKey.Should().Be("official-key");
        options.SelectedModel.Should().Be("deepseek-chat");
        options.IsThinkingEnabled.Should().BeFalse();
        options.ReasoningEffort.Should().Be("max");
        options.EnableWebSearch.Should().BeFalse();
        options.SearchProvider.Should().Be("DuckDuckGo");
        options.ApprovalMode.Should().Be("AllowAll");
        options.ThemeMode.Should().Be(ThemeMode.Light);
        options.Language.Should().Be("en");
        options.AutoCompleteEnabled.Should().BeTrue();
        options.ShowDiffMarkersInEditor.Should().BeFalse();
        options.OcrEngine.Should().Be("DeepSeek Official");
        options.OfficialOcrPurpose.Should().Be(
            DeepSeekOcrClient.RichLayoutRecognitionPurpose);
        options.AppendMessageMode.Should().Be(AppendMessageMode.Guidance);
        options.InputBoxHeight.Should().Be(72);
        options.BottomAreaScalePercent.Should().Be(120);
        options.WebView2ZoomPercent.Should().Be(130);
    }

    [Fact]
    public void ApplyTo_PreservesAdvancedTuningSettings()
    {
        var options = DeepSeekOptionsPageTestFactory.Create();
        options.TokenBudget = 72;
        options.CompressionThreshold = 64;
        options.PreserveRecentTurns = 7;
        options.MaxToolCallRounds = 321;
        options.AgentMaxDepth = 6;
        options.EnableApiDebugDump = true;
        options.CustomApiKey = "custom-key";
        options.ApiBaseUrl = "https://example.test/v1";
        options.OcrEngine = "Windows Built-in";
        options.OfficialOcrPurpose = DeepSeekOptionsPage.DefaultOfficialOcrPurpose;

        var values = new QuickSettingsValues(
            ApiKey: "official-key",
            ModelChoice: string.Empty,
            IsThinkingEnabled: true,
            ReasoningEffort: "high",
            EnableWebSearch: true,
            SearchProvider: "DeepSeek",
            ApprovalMode: "SmartBlock",
            ThemeMode: "Auto",
            Language: "auto",
            AutoCompleteEnabled: false,
            ShowDiffMarkersInEditor: true,
            OcrEngine: "PaddleOCR-Sharp",
            OfficialOcrPurpose: DeepSeekOptionsPage.DefaultOfficialOcrPurpose,
            AppendMessageMode: "Queue",
            InputBoxHeight: 50,
            BottomAreaScalePercent: 100,
            WebView2ZoomPercent: 100);

        values.ApplyTo(options);

        options.TokenBudget.Should().Be(72);
        options.CompressionThreshold.Should().Be(64);
        options.PreserveRecentTurns.Should().Be(7);
        options.MaxToolCallRounds.Should().Be(321);
        options.AgentMaxDepth.Should().Be(6);
        options.EnableApiDebugDump.Should().BeTrue();
        options.CustomApiKey.Should().Be("custom-key");
        options.ApiBaseUrl.Should().Be("https://example.test/v1");
        options.OcrEngine.Should().Be("PaddleOCR-Sharp");
    }
}
