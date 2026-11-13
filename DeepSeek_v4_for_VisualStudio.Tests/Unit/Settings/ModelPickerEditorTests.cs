using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Tests.Fixtures;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Settings;

public class ModelPickerEditorTests
{
    [Fact]
    public void ApplyModelsToEndpoint_UpdatesOnlySelectedEndpoint()
    {
        var page = DeepSeekOptionsPageTestFactory.Create();
        page.SetCustomEndpoints(new[]
        {
            new CustomEndpointConfig(
                "endpoint-a", "Provider A", "https://a.example.com/v1", "sk-a",
                new[] { "model-a" }),
            new CustomEndpointConfig(
                "endpoint-b", "Provider B", "https://b.example.com/v1", "sk-b",
                new[] { "model-b" }),
        });

        ModelPickerEditor.ApplyModelsToEndpoint(
            page,
            "endpoint-b",
            new[] { "model-b2", "model-b3" });

        var endpoints = page.GetCustomEndpoints();
        endpoints[0].Models.Should().Equal("model-a");
        endpoints[1].Models.Should().Equal("model-b", "model-b2", "model-b3");
        endpoints.Select(endpoint => endpoint.ApiKey).Should().Equal("sk-a", "sk-b");
        page.ActiveCustomEndpointId.Should().Be("endpoint-b");
        page.ActiveCustomModel.Should().Be("model-b3");
        page.ActiveModelSource.Should().Be("custom");
    }

    [Fact]
    public void ApplyModelsToEndpoint_CreatesLegacyEndpointWhenOnlyBaseUrlExists()
    {
        var page = DeepSeekOptionsPageTestFactory.Create();
        page.ApiBaseUrl = "https://legacy.example.com/v1";
        page.CustomApiKey = "sk-legacy";
        page.CustomModelName = "legacy-model";
        page.CustomEndpointsData = string.Empty;

        ModelPickerEditor.ApplyModelsToEndpoint(
            page,
            targetEndpointId: null,
            new[] { "legacy-model-2" });

        var endpoint = page.GetCustomEndpoints().Should().ContainSingle().Subject;
        endpoint.Id.Should().Be("legacy-custom-endpoint");
        endpoint.Models.Should().Equal("legacy-model", "legacy-model-2");
        endpoint.ApiKey.Should().Be("sk-legacy");
        page.ActiveCustomEndpointId.Should().Be("legacy-custom-endpoint");
        page.ActiveCustomModel.Should().Be("legacy-model-2");
    }

    [Fact]
    public void ApplyModelsToEndpoint_WithoutEndpointFallsBackToLegacyModelList()
    {
        var page = DeepSeekOptionsPageTestFactory.Create();
        page.CustomModelName = "legacy-model";
        page.CustomEndpointsData = string.Empty;

        ModelPickerEditor.ApplyModelsToEndpoint(
            page,
            targetEndpointId: null,
            new[] { "legacy-model-2" });

        page.GetCustomEndpoints().Should().BeEmpty();
        page.GetCustomModels().Should().Equal("legacy-model", "legacy-model-2");
        page.ActiveCustomModel.Should().Be("legacy-model-2");
        page.ActiveModelSource.Should().Be("custom");
    }
}
