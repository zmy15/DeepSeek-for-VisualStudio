using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Tests.Fixtures;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Settings;

public class ModelCatalogMappingTests
{
    [Fact]
    public void BuildRows_TwoEndpointsWithSameModel_CreatesTwoCustomRows()
    {
        var page = DeepSeekOptionsPageTestFactory.Create();
        page.SetCustomEndpoints(new[]
        {
            new CustomEndpointConfig(
                "endpoint-a", "Provider A", "https://a.example.com/v1", "sk-a",
                new[] { "shared-model" }),
            new CustomEndpointConfig(
                "endpoint-b", "Provider B", "https://b.example.com/v1", "sk-b",
                new[] { "shared-model" }),
        });

        var rows = ModelCatalogMapping.BuildRows(page)
            .Where(row => !row.IsOfficial && row.Model == "shared-model")
            .ToList();

        rows.Should().HaveCount(2);
        rows.Select(row => row.EndpointId).Should().Equal("endpoint-a", "endpoint-b");
        rows.Select(row => row.EndpointName).Should().Equal("Provider A", "Provider B");
    }

    [Fact]
    public void BuildRows_LegacyEndpoint_AssignsSyntheticEndpointId()
    {
        var page = DeepSeekOptionsPageTestFactory.Create();
        page.ApiBaseUrl = "https://legacy.example.com/v1";
        page.CustomApiKey = "sk-legacy";
        page.CustomModelName = "legacy-model";
        page.CustomEndpointsData = string.Empty;

        var row = ModelCatalogMapping.BuildRows(page)
            .Single(item => item.Model == "legacy-model");

        row.IsOfficial.Should().BeFalse();
        row.EndpointId.Should().Be("legacy-custom-endpoint");
        row.EndpointName.Should().Be("legacy.example.com");
    }

    [Fact]
    public void ApplyRows_UpdatesModelsPerEndpointAndPreservesEndpointCredentials()
    {
        var page = DeepSeekOptionsPageTestFactory.Create();
        var endpoints = new[]
        {
            new CustomEndpointConfig(
                "endpoint-a", "Provider A", "https://a.example.com/v1", "sk-a",
                new[] { "old-a" }),
            new CustomEndpointConfig(
                "endpoint-b", "Provider B", "https://b.example.com/v1", "sk-b",
                new[] { "old-b" }),
        };
        page.SetCustomEndpoints(endpoints);

        ModelCatalogMapping.ApplyRows(
            page,
            new[]
            {
                new ModelCatalogRow(
                    "new-a", 0, false, EndpointId: "endpoint-a", EndpointName: "Provider A"),
                new ModelCatalogRow(
                    "new-b", 0, false, EndpointId: "endpoint-b", EndpointName: "Provider B"),
                new ModelCatalogRow(
                    "shared", 0, false, EndpointId: "endpoint-b", EndpointName: "Provider B"),
            },
            endpoints);

        var updated = page.GetCustomEndpoints();
        updated.Should().HaveCount(2);
        updated[0].Models.Should().Equal("new-a");
        updated[1].Models.Should().Equal("new-b", "shared");
        updated.Select(endpoint => endpoint.ApiKey).Should().Equal("sk-a", "sk-b");
        page.GetCustomModels().Should().BeEquivalentTo("new-a", "new-b", "shared");
    }

    [Fact]
    public void ApplyRows_EmptyEndpointList_ClearsPersistedEndpointConfiguration()
    {
        var page = DeepSeekOptionsPageTestFactory.Create();
        page.SetCustomEndpoints(new[]
        {
            new CustomEndpointConfig(
                "endpoint-a", "Provider A", "https://a.example.com/v1", "sk-a",
                new[] { "model-a" }),
        });

        ModelCatalogMapping.ApplyRows(
            page,
            Array.Empty<ModelCatalogRow>(),
            Array.Empty<CustomEndpointConfig>());

        page.CustomEndpointsData.Should().BeEmpty();
        page.ActiveCustomEndpointId.Should().BeEmpty();
        page.ApiBaseUrl.Should().BeEmpty();
        page.CustomApiKey.Should().BeEmpty();
        page.CustomModelName.Should().BeEmpty();
    }
}
