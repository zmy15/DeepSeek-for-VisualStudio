using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Tests.Fixtures;

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
    public void SetCustomEndpoints_PersistsMultipleEndpointsAndUnprotectsKeys()
    {
        var page = DeepSeekOptionsPageTestFactory.Create();

        page.SetCustomEndpoints(new[]
        {
            new CustomEndpointConfig(
                "endpoint-a", "Provider A", "https://a.example.com/v1", "sk-a",
                new[] { "model-a", "shared-model" }),
            new CustomEndpointConfig(
                "endpoint-b", "Provider B", "https://b.example.com/v1", "sk-b",
                new[] { "model-b", "shared-model" }),
        });

        page.CustomEndpointsData.Should().NotContain("sk-a");
        page.CustomEndpointsData.Should().NotContain("sk-b");
        page.ApiBaseUrl.Should().Be("https://a.example.com/v1");
        page.CustomApiKey.Should().Be("sk-a");
        page.ActiveCustomEndpointId.Should().Be("endpoint-a");

        var endpoints = page.GetCustomEndpoints();
        endpoints.Select(endpoint => endpoint.ApiKey)
            .Should().Equal("sk-a", "sk-b");
        page.GetCustomModels().Should().BeEquivalentTo(
            "model-a", "model-b", "shared-model");
    }

    [Fact]
    public void GetCustomEndpoints_UsesLegacySingleEndpointWhenJsonIsEmpty()
    {
        var page = DeepSeekOptionsPageTestFactory.Create();
        page.ApiBaseUrl = "https://legacy.example.com/v1";
        page.CustomApiKey = "sk-legacy";
        page.CustomModelName = "legacy-a; legacy-b";
        page.CustomEndpointsData = string.Empty;

        var endpoint = page.GetCustomEndpoints().Should().ContainSingle().Subject;

        endpoint.Id.Should().Be("legacy-custom-endpoint");
        endpoint.Name.Should().Be("legacy.example.com");
        endpoint.ApiKey.Should().Be("sk-legacy");
        endpoint.Models.Should().Equal("legacy-a", "legacy-b");
    }

    [Fact]
    public void GetCustomEndpoints_InvalidJsonFallsBackToLegacyConfiguration()
    {
        var page = DeepSeekOptionsPageTestFactory.Create();
        page.ApiBaseUrl = "https://legacy.example.com/v1";
        page.CustomApiKey = "sk-legacy";
        page.CustomModelName = "legacy-model";
        page.CustomEndpointsData = "{not valid json";

        page.GetCustomEndpoints().Should().ContainSingle()
            .Which.Models.Should().Equal("legacy-model");
    }

    [Fact]
    public void GetCustomEndpoints_AppliesVisibleFirstEndpointOverrides()
    {
        var page = DeepSeekOptionsPageTestFactory.Create();
        page.SetCustomEndpoints(new[]
        {
            new CustomEndpointConfig(
                "endpoint-a", "Provider A", "https://old.example.com/v1", "sk-old",
                new[] { "model-a" }),
            new CustomEndpointConfig(
                "endpoint-b", "Provider B", "https://b.example.com/v1", "sk-b",
                new[] { "model-b" }),
        });

        page.ApiBaseUrl = "https://new.example.com/v1";
        page.CustomApiKey = "sk-new";

        var first = page.GetCustomEndpoints()[0];
        first.BaseUrl.Should().Be("https://new.example.com/v1");
        first.ApiKey.Should().Be("sk-new");
    }

    [Fact]
    public void GetCustomModelEntries_PreservesSharedModelPerEndpoint()
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

        var sharedEntries = page.GetCustomModelEntries()
            .Where(entry => entry.Model == "shared-model")
            .ToList();

        sharedEntries.Should().HaveCount(2);
        sharedEntries.Select(entry => entry.EndpointId)
            .Should().Equal("endpoint-a", "endpoint-b");
    }

    [Fact]
    public void FindCustomEndpointForModel_PrefersExactEndpointThenModelMatch()
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

        page.FindCustomEndpointForModel("endpoint-b", "shared-model")!
            .Id.Should().Be("endpoint-b");
        page.FindCustomEndpointForModel("missing", "shared-model")!
            .Id.Should().Be("endpoint-a");
        page.FindCustomEndpointForModel(string.Empty, "missing-model")!
            .Id.Should().Be("endpoint-a");
    }

    [Fact]
    public void ModelChoices_SelectSharedModelOnSpecificEndpoint()
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

        string choice = page.GetModelChoices()
            .Single(item => item.Contains("Provider B", StringComparison.Ordinal));
        page.SetSelectedModelChoice(choice);

        page.ActiveCustomEndpointId.Should().Be("endpoint-b");
        page.ActiveCustomModel.Should().Be("shared-model");
        page.ActiveModelSource.Should().Be("custom");
        page.GetSelectedModelChoice().Should().Contain("Provider B");
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
