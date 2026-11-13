using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.View;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.View;

public class ModelListItemTests
{
    [Fact]
    public void CustomItem_DisplayIncludesEndpointNameAndCustomSuffix()
    {
        var item = DeepSeekChatControl.ModelListItem.Custom(
            "shared-model",
            displayName: "Shared Display",
            endpointId: "endpoint-b",
            endpointName: "Provider B");

        item.Source.Should().Be(DeepSeekChatControl.ModelListItem.EntrySource.Custom);
        item.EndpointId.Should().Be("endpoint-b");
        item.EndpointName.Should().Be("Provider B");
        item.Display.Should().Be(
            "Shared Display · Provider B" +
            LocalizationService.Instance["chat.model.customSuffix"]);
    }

    [Fact]
    public void OfficialItem_DisplayUsesDisplayNameWithoutCustomSuffix()
    {
        var item = DeepSeekChatControl.ModelListItem.Official(
            "deepseek-chat",
            displayName: "DeepSeek Chat");

        item.Source.Should().Be(DeepSeekChatControl.ModelListItem.EntrySource.Official);
        item.EndpointId.Should().BeEmpty();
        item.Display.Should().Be("DeepSeek Chat");
    }
}
