using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Settings;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Settings;

public class CustomEndpointSettingsTests
{
    [Fact]
    public void FormatCustomEndpoints_ProtectsKeysAndRoundTripsMultipleEndpoints()
    {
        var original = new[]
        {
            new CustomEndpointConfig(
                "endpoint-a", "Provider A", "https://a.example.com/v1", "sk-a",
                new[] { "model-a", "shared-model" }),
            new CustomEndpointConfig(
                "endpoint-b", "Provider B", "https://b.example.com/v1", "sk-b",
                new[] { "model-b", "shared-model" }),
        };

        string json = DeepSeekOptionsPage.FormatCustomEndpoints(original);
        json.Should().NotContain("sk-a");
        json.Should().NotContain("sk-b");

        var parsed = DeepSeekOptionsPage.ParseCustomEndpoints(json);
        parsed.Should().HaveCount(2);
        parsed.Select(endpoint => ApiKeyProtection.Unprotect(endpoint.ApiKey))
            .Should().Equal("sk-a", "sk-b");
        parsed.SelectMany(endpoint => endpoint.Models)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Should().BeEquivalentTo("model-a", "model-b", "shared-model");
    }

    [Fact]
    public void NormalizeCustomEndpoints_DeduplicatesIdsAndNames()
    {
        var endpoints = DeepSeekOptionsPage.NormalizeCustomEndpoints(new[]
        {
            new CustomEndpointConfig(
                "same-id", "Provider", "https://a.example.com/v1", "sk-a",
                new[] { "model-a" }),
            new CustomEndpointConfig(
                "same-id", "Provider", "https://b.example.com/v1", "sk-b",
                new[] { "model-b" }),
        });

        endpoints.Should().HaveCount(2);
        endpoints[0].Id.Should().Be("same-id");
        endpoints[1].Id.Should().NotBe("same-id");
        endpoints[1].Name.Should().Be("Provider (2)");
    }

    [Fact]
    public void Resolver_RoutesSharedModelToSelectedEndpoint()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: null,
            officialApiKey: "sk-official",
            customApiKey: "ignored",
            selectedModel: "deepseek-chat",
            customModels: "shared-model",
            activeCustomModel: "shared-model",
            activeModelSource: "custom",
            customEndpoints: new[]
            {
                new CustomEndpointConfig(
                    "endpoint-a", "Provider A", "https://a.example.com/v1", "sk-a",
                    new[] { "shared-model" }),
                new CustomEndpointConfig(
                    "endpoint-b", "Provider B", "https://b.example.com/v1", "sk-b",
                    new[] { "shared-model" }),
            },
            activeCustomEndpointId: "endpoint-b");

        config.IsCustom.Should().BeTrue();
        config.EndpointId.Should().Be("endpoint-b");
        config.EndpointName.Should().Be("Provider B");
        config.BaseUrl.Should().Be("https://b.example.com/v1");
        config.ApiKey.Should().Be("sk-b");
        config.Model.Should().Be("shared-model");
    }

}
