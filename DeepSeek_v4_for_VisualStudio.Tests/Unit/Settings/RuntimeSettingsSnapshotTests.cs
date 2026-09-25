using DeepSeek_v4_for_VisualStudio.Settings;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Settings;

public class RuntimeSettingsSnapshotTests
{
    [Fact]
    public void ApprovalChange_DoesNotInvalidateUnrelatedSubsystems()
    {
        var previous = CreateSnapshot();
        var current = previous with { ApprovalMode = "AllowAll" };

        var changes = RuntimeSettingsChangeSet.Between(previous, current);

        changes.ApprovalChanged.Should().BeTrue();
        changes.HasChanges.Should().BeTrue();
        changes.EndpointChanged.Should().BeFalse();
        changes.ModelControlsChanged.Should().BeFalse();
        changes.ThinkingChanged.Should().BeFalse();
        changes.OcrChanged.Should().BeFalse();
        changes.WebSearchChanged.Should().BeFalse();
        changes.LayoutChanged.Should().BeFalse();
    }

    [Fact]
    public void OcrChange_DoesNotResetEndpointOrWebSearch()
    {
        var previous = CreateSnapshot();
        var current = previous with { OcrEngine = "PaddleOCR-Sharp" };

        var changes = RuntimeSettingsChangeSet.Between(previous, current);

        changes.OcrChanged.Should().BeTrue();
        changes.EndpointChanged.Should().BeFalse();
        changes.WebSearchChanged.Should().BeFalse();
    }

    [Fact]
    public void ModelChange_RefreshesEndpointAndModelControlsOnly()
    {
        var previous = CreateSnapshot();
        var current = previous with { ActiveCustomModel = "deepseek-v4-flash-vision-exp" };

        var changes = RuntimeSettingsChangeSet.Between(previous, current);

        changes.EndpointChanged.Should().BeTrue();
        changes.ModelControlsChanged.Should().BeTrue();
        changes.OfficialApiKeyChanged.Should().BeFalse();
        changes.OcrChanged.Should().BeFalse();
        changes.WebSearchChanged.Should().BeFalse();
    }

    [Fact]
    public void ContextBudgetChange_RefreshesContextOnly()
    {
        var previous = CreateSnapshot();
        var current = previous with
        {
            TokenBudgetPercent = 80,
            ModelMaxTokenLimits = "deepseek-chat=128000",
        };

        var changes = RuntimeSettingsChangeSet.Between(previous, current);

        changes.ContextChanged.Should().BeTrue();
        changes.HasChanges.Should().BeTrue();
        changes.EndpointChanged.Should().BeFalse();
        changes.ModelControlsChanged.Should().BeFalse();
        changes.ThinkingChanged.Should().BeFalse();
        changes.OcrChanged.Should().BeFalse();
        changes.WebSearchChanged.Should().BeFalse();
        changes.LayoutChanged.Should().BeFalse();
    }

    [Fact]
    public void SearchKeyChange_RefreshesWebSearchOnly()
    {
        var previous = CreateSnapshot();
        var current = previous with { BingApiKey = "new-key" };

        var changes = RuntimeSettingsChangeSet.Between(previous, current);

        changes.WebSearchChanged.Should().BeTrue();
        changes.EndpointChanged.Should().BeFalse();
        changes.OcrChanged.Should().BeFalse();
    }

    [Fact]
    public void AutoSkillRoutingChange_RefreshesOnlySkillRouting()
    {
        var previous = CreateSnapshot();
        var current = previous with { EnableAutoSkillRouting = true };

        var changes = RuntimeSettingsChangeSet.Between(previous, current);

        changes.AutoSkillRoutingChanged.Should().BeTrue();
        changes.HasChanges.Should().BeTrue();
        changes.EndpointChanged.Should().BeFalse();
        changes.ModelControlsChanged.Should().BeFalse();
        changes.ThinkingChanged.Should().BeFalse();
        changes.ApprovalChanged.Should().BeFalse();
        changes.OcrChanged.Should().BeFalse();
        changes.WebSearchChanged.Should().BeFalse();
        changes.LayoutChanged.Should().BeFalse();
    }

    private static RuntimeSettingsSnapshot CreateSnapshot()
        => new(
            ApiKey: "official-key",
            CustomApiKey: "custom-key",
            ApiBaseUrl: "https://example.test/v1",
            SelectedModel: "deepseek-chat",
            CustomModelName: "deepseek-chat",
            CustomVisionModels: string.Empty,
            ActiveCustomModel: "deepseek-chat",
            ActiveModelSource: "custom",
            TokenBudgetPercent: 90,
            ModelMaxTokenLimits: "deepseek-chat=1000000",
            IsThinkingEnabled: true,
            ReasoningEffort: "high",
            OcrEngine: "Windows Built-in",
            EnableWebSearch: false,
            SearchProvider: "DuckDuckGo",
            BaiduApiKey: string.Empty,
            BingApiKey: string.Empty,
            ApprovalMode: "SmartBlock",
            EnableAutoSkillRouting: false,
            AppendMessageMode: "Queue",
            InputBoxHeight: 50,
            BottomAreaScalePercent: 100,
            WebView2ZoomPercent: 100);
}
