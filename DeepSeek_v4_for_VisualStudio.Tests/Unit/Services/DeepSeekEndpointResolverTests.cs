using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using System.Net.Http;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// DeepSeek 官方与自定义端点配置分离的来源解析。
/// </summary>
[Collection("OfficialModelCatalogService")]
public class DeepSeekEndpointResolverTests
{
    [Fact]
    public void Resolve_NullOptions_ReturnsEmptyOfficial()
    {
        var config = DeepSeekEndpointResolver.Resolve(null);

        config.IsCustom.Should().BeFalse();
        config.ApiKey.Should().BeEmpty();
        config.Model.Should().BeEmpty();
        config.BaseUrl.Should().BeNull();
    }

    [Fact]
    public void Resolve_NoBaseUrl_UsesOfficialKeyAndSelectedModel()
    {
        // 自定义配置已填但端点为空 → 不生效
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: null,
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "kimi-k3");

        config.IsCustom.Should().BeFalse();
        config.ApiKey.Should().Be("sk-official");
        config.Model.Should().Be("deepseek-v4-flash");
        config.BaseUrl.Should().BeNull();
    }

    [Fact]
    public void Resolve_WithBaseUrl_SwitchesToCustomKeyAndModel()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "https://relay.example.com/v1",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "kimi-k3");

        config.IsCustom.Should().BeTrue();
        config.ApiKey.Should().Be("sk-custom");
        config.Model.Should().Be("kimi-k3");
        config.BaseUrl.Should().Be("https://relay.example.com/v1");
    }

    [Fact]
    public void Resolve_CustomWithEmptyModelList_ReturnsEmptyModel()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "https://relay.example.com/v1",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "");

        config.IsCustom.Should().BeTrue();
        config.Model.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_WhitespaceBaseUrl_TreatedAsOfficial()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "   ",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "",
            customModels: "kimi-k3",
            officialModels: Array.Empty<string>());

        config.IsCustom.Should().BeFalse();
        config.ApiKey.Should().Be("sk-official");
        config.Model.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_ExplicitOfficial_OverridesCustomEndpoint()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "https://relay.example.com/v1",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "kimi-k3",
            activeModelSource: "official");

        config.IsCustom.Should().BeFalse();
        config.ApiKey.Should().Be("sk-official");
        config.Model.Should().Be("deepseek-v4-flash");
        config.BaseUrl.Should().BeNull();
    }

    [Fact]
    public void Resolve_ExplicitCustomWithoutBaseUrl_FallsBackToOfficial()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: null,
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "kimi-k3",
            activeModelSource: "custom");

        config.IsCustom.Should().BeFalse();
        config.ApiKey.Should().Be("sk-official");
        config.Model.Should().Be("deepseek-v4-flash");
    }

    [Fact]
    public void Resolve_ExplicitCustomWithBaseUrl_UsesCustomConfig()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "https://relay.example.com/v1",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "kimi-k3",
            activeModelSource: "custom");

        config.IsCustom.Should().BeTrue();
        config.ApiKey.Should().Be("sk-custom");
        config.Model.Should().Be("kimi-k3");
        config.BaseUrl.Should().Be("https://relay.example.com/v1");
    }

    [Fact]
    public void Resolve_CustomModelList_UsesActiveModel()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "https://relay.example.com/v1",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "kimi-k3; glm-5.3-flash; qwen3.8-max",
            activeCustomModel: "glm-5.3-flash");

        config.IsCustom.Should().BeTrue();
        config.Model.Should().Be("glm-5.3-flash");
    }

    [Fact]
    public void Resolve_MultipleCustomEndpoints_UsesSelectedEndpoint()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: null,
            officialApiKey: "sk-official",
            customApiKey: "ignored",
            selectedModel: "deepseek-v4-flash",
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
        config.BaseUrl.Should().Be("https://b.example.com/v1");
        config.ApiKey.Should().Be("sk-b");
        config.Model.Should().Be("shared-model");
    }

    [Fact]
    public void Resolve_InactiveCustomModel_FallsBackToFirstListItem()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "https://relay.example.com/v1",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "kimi-k3, glm-5.3-flash",
            activeCustomModel: "removed-model");

        config.IsCustom.Should().BeTrue();
        config.Model.Should().Be("kimi-k3");
    }

    // ── IsVision 视觉能力权威判定 ──
    // 官方模型以 /models 的 input_modalities 为权威值；自定义模型由用户手动勾选。

    [Fact]
    public void Resolve_CustomVisionList_MarksActiveModelAsVision()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "https://relay.example.com/v1",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "gpt-4o; kimi-k3",
            activeCustomModel: "gpt-4o",
            visionModels: new[] { "gpt-4o" });

        config.IsCustom.Should().BeTrue();
        config.IsVision.Should().BeTrue();
    }

    [Fact]
    public void Resolve_CustomVisionList_ActiveNotMarked_NotVision()
    {
        // 名单不含当前激活模型 → 按纯文本模型处理
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "https://relay.example.com/v1",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "gpt-4o; kimi-k3",
            activeCustomModel: "kimi-k3",
            visionModels: new[] { "gpt-4o" });

        config.IsCustom.Should().BeTrue();
        config.Model.Should().Be("kimi-k3");
        config.IsVision.Should().BeFalse();
    }

    [Fact]
    public void Resolve_CustomVisionListEmpty_NotVision()
    {
        // visionModels 为 null（未配置）→ 模型默认按纯文本处理
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "https://relay.example.com/v1",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "gpt-4o; kimi-k3",
            activeCustomModel: "gpt-4o",
            visionModels: null);

        config.IsCustom.Should().BeTrue();
        config.IsVision.Should().BeFalse();
    }

    [Fact]
    public void Resolve_CustomVisionList_IgnoreCase()
    {
        // 名单大小写与激活模型不一致时仍应命中（与 ParseCustomModels 的 OrdinalIgnoreCase 语义一致）
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "https://relay.example.com/v1",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "gpt-4o; kimi-k3",
            activeCustomModel: "gpt-4o",
            visionModels: new[] { "GPT-4O" });

        config.IsCustom.Should().BeTrue();
        config.IsVision.Should().BeTrue();
    }

    [Fact]
    public void Resolve_OfficialModel_VisionFromUserListWhenMetadataMissing()
    {
        // 接口未提供 input_modalities（元数据缺失）时，官方模型沿用用户勾选名单
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: null,
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash-vision-exp",
            customModels: "gpt-4o",
            visionModels: new[] { "deepseek-v4-flash-vision-exp" });

        config.IsCustom.Should().BeFalse();
        config.IsVision.Should().BeTrue();

        var notChecked = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: null,
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash-vision-exp",
            customModels: "gpt-4o");

        notChecked.IsVision.Should().BeFalse();
    }

    [Fact]
    public void Resolve_OfficialNonVisionModel_NotVision()
    {
        // 官方模型未在视觉名单中、接口也无模态声明时按纯文本处理
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: null,
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "gpt-4o",
            visionModels: new[] { "gpt-4o" });

        config.IsCustom.Should().BeFalse();
        config.IsVision.Should().BeFalse();
    }

    [Fact]
    public void Resolve_OfficialModel_VisionFromDeclaredModalities()
    {
        OfficialModelCatalogService.ResetForTests();
        var handler = new StubHandler(
            """
            {"data":[
              {"id":"deepseek-flash","input_modalities":["text","image"]},
              {"id":"deepseek-v4-pro","input_modalities":["text"]}
            ]}
            """);
        OfficialModelCatalogService.RefreshAsync("sk-test", default, handler).GetAwaiter().GetResult();

        var vision = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: null,
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-flash",
            customModels: "",
            visionModels: null);
        vision.IsVision.Should().BeTrue();

        // 用户名单与接口声明冲突时，以接口为准（无需用户手填）。
        var textOnly = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: null,
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-pro",
            customModels: "",
            visionModels: new[] { "deepseek-v4-pro" });
        textOnly.IsVision.Should().BeFalse();

        OfficialModelCatalogService.ResetForTests();
    }

    [Fact]
    public void Resolve_CustomVisionFallbackFirstItem_MarksFallbackAsVision()
    {
        // 激活模型失效回退到列表第一项时，视觉判定应跟随回退后的生效模型
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: "https://relay.example.com/v1",
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-flash",
            customModels: "gpt-4o, kimi-k3",
            activeCustomModel: "removed-model",
            visionModels: new[] { "gpt-4o" });

        config.IsCustom.Should().BeTrue();
        config.Model.Should().Be("gpt-4o");
        config.IsVision.Should().BeTrue();
    }

    // ── 官方模型列表改为 GET /models 动态获取后的校验 ──

    [Fact]
    public void Resolve_RemoteOfficialModel_IsAccepted()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: null,
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-exp-1228",
            customModels: "kimi-k3",
            officialModels: new[] { "deepseek-v4-exp-1228", "deepseek-v4-pro" });

        config.Model.Should().Be("deepseek-v4-exp-1228");
    }

    [Fact]
    public void Resolve_RemoteOfficialModel_ManualVisionOnly()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: null,
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-vision-exp",
            customModels: "kimi-k3",
            officialModels: new[] { "deepseek-v4-vision-exp" },
            visionModels: new[] { "deepseek-v4-vision-exp" });

        config.IsVision.Should().BeTrue();
    }

    [Fact]
    public void Resolve_RemovedOfficialModel_FallsBackToFirstRemoteModel()
    {
        var config = DeepSeekEndpointResolver.Resolve(
            apiBaseUrl: null,
            officialApiKey: "sk-official",
            customApiKey: "sk-custom",
            selectedModel: "deepseek-v4-removed",
            customModels: "kimi-k3",
            officialModels: new[] { "deepseek-v4-current" });

        config.Model.Should().Be("deepseek-v4-current");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;

        public StubHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
    }
}
