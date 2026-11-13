using System;
using System.Collections.Generic;
using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Utils;
using System.Linq;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>解析后的端点配置（官方 DeepSeek 或自定义端点二选一）。
    /// <see cref="DeepSeekEndpointConfig.IsVision"/> 由用户勾选的视觉模型名单权威判定。</summary>
    public sealed record DeepSeekEndpointConfig(
        string ApiKey, string Model, string? BaseUrl, bool IsCustom,
        bool IsVision = false, string EndpointId = "", string EndpointName = "");

    /// <summary>
    /// 端点来源解析器 — DeepSeek 官方与一个或多个自定义端点配置分离：
    /// 自定义条目按 ActiveCustomEndpointId 路由到对应 Base URL、密钥与模型；
    /// 没有自定义端点时回退 DeepSeek 官方密钥与当前选中的接口模型。
    /// 解析结果同时携带 IsVision（视觉能力权威判定）：官方与自定义模型
    /// 都由用户在 VisionModelPickerEditor 中手动勾选。
    /// </summary>
    public static class DeepSeekEndpointResolver
    {
        public static DeepSeekEndpointConfig Resolve(DeepSeekOptionsPage? options)
            => options == null
                ? new DeepSeekEndpointConfig(string.Empty, string.Empty, null, false)
                : Resolve(
                    options.ApiBaseUrl,
                    ApiKeyProtection.Unprotect(options.ApiKey),
                    ApiKeyProtection.Unprotect(options.CustomApiKey),
                    options.SelectedModel,
                    options.CustomModelName,
                    options.ActiveCustomModel,
                    options.ActiveModelSource,
                    options.GetVisionModels(),
                    customEndpoints: options.GetCustomEndpoints(),
                    activeCustomEndpointId: options.ActiveCustomEndpointId);

        /// <summary>
        /// 原始参数重载（测试友好，不依赖 DialogPage 实例化）。
        /// </summary>
        public static DeepSeekEndpointConfig Resolve(
            string? apiBaseUrl,
            string officialApiKey,
            string customApiKey,
            string? selectedModel,
            string? customModels,
            string? activeCustomModel = "",
            string? activeModelSource = "auto",
            IReadOnlyList<string>? visionModels = null,
            IReadOnlyList<string>? officialModels = null,
            IReadOnlyList<CustomEndpointConfig>? customEndpoints = null,
            string? activeCustomEndpointId = null)
        {
            var baseUrl = (apiBaseUrl ?? string.Empty).Trim();
            var sourcePreference = (activeModelSource ?? "auto").Trim().ToLowerInvariant();
            var endpoints = DeepSeekOptionsPage.NormalizeCustomEndpoints(customEndpoints);
            if (endpoints.Count == 0 && baseUrl.Length > 0)
            {
                endpoints = new[]
                {
                    new CustomEndpointConfig(
                        "legacy-custom-endpoint",
                        DeepSeekOptionsPage.InferCustomEndpointName(baseUrl),
                        baseUrl,
                        customApiKey,
                        DeepSeekOptionsPage.ParseCustomModels(customModels)),
                };
            }

            var isCustom = sourcePreference switch
            {
                "custom" => true,
                "official" => false,
                _ => endpoints.Count > 0, // auto：跟随端点配置
            };

            // 自定义模式必须有端点；缺失时回退官方（配置不一致的兜底）
            if (isCustom && endpoints.Count == 0)
            {
                isCustom = false;
                Logger.Warn("[Resolver] 模型来源为 custom 但未配置 API 端点，回退 DeepSeek 官方");
            }

            if (isCustom)
            {
                var selection = ResolveCustomEndpointSelection(
                    endpoints,
                    activeCustomEndpointId,
                    activeCustomModel);
                string model = selection.Model;
                bool isVision = (visionModels ?? Array.Empty<string>())
                    .Any(v => string.Equals(v, model, StringComparison.OrdinalIgnoreCase));

                return new DeepSeekEndpointConfig(
                    selection.Endpoint.ApiKey,
                    model,
                    selection.Endpoint.BaseUrl,
                    true,
                    isVision,
                    selection.Endpoint.Id,
                    selection.Endpoint.Name);
            }

            var officialModel = CoalesceOfficialModel(
                    selectedModel,
                    officialModels ?? OfficialModelCatalogService.GetModels());

            // ── 视觉能力判断收敛点 ──
            // 官方模型以 /models 的 input_modalities 声明为权威值（接口未提供时才回退用户名单）；
            // 自定义模型仍由用户手动勾选（端点不一定提供元数据）。
            bool officialIsVision = ResolveOfficialVision(officialModel, visionModels);

            return new DeepSeekEndpointConfig(
                officialApiKey,
                officialModel,
                null,
                false,
                officialIsVision);
        }

        /// <summary>
        /// 官方模型的视觉能力：接口声明了输入模态就以它为准（用户无需手填），
        /// 接口未提供模态信息时才回退到用户手填名单。
        /// </summary>
        private static bool ResolveOfficialVision(
            string model, IReadOnlyList<string>? visionModels)
        {
            var metadata = OfficialModelCatalogService.TryGetMetadata(model);
            if (metadata is { HasModalityInfo: true })
                return metadata.SupportsImageInput;

            return (visionModels ?? Array.Empty<string>())
                .Any(v => string.Equals(v, model, StringComparison.OrdinalIgnoreCase));
        }

        private static (CustomEndpointConfig Endpoint, string Model)
            ResolveCustomEndpointSelection(
                IReadOnlyList<CustomEndpointConfig> endpoints,
                string? activeEndpointId,
                string? activeCustomModel)
        {
            string activeId = (activeEndpointId ?? string.Empty).Trim();
            string activeModel = (activeCustomModel ?? string.Empty).Trim();

            var endpoint = endpoints.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, activeId, StringComparison.Ordinal));
            if (endpoint != null)
            {
                var match = endpoint.Models.FirstOrDefault(model =>
                    string.Equals(model, activeModel, StringComparison.OrdinalIgnoreCase));
                return (endpoint, match ?? endpoint.Models.FirstOrDefault() ?? activeModel);
            }

            if (activeModel.Length > 0)
            {
                endpoint = endpoints.FirstOrDefault(candidate =>
                    candidate.Models.Any(model =>
                        string.Equals(model, activeModel, StringComparison.OrdinalIgnoreCase)));
                if (endpoint != null)
                {
                    var match = endpoint.Models.First(model =>
                        string.Equals(model, activeModel, StringComparison.OrdinalIgnoreCase));
                    return (endpoint, match);
                }
            }

            endpoint = endpoints.FirstOrDefault(candidate => candidate.Models.Count > 0)
                ?? endpoints[0];
            return (endpoint, endpoint.Models.FirstOrDefault() ?? activeModel);
        }

        private static string CoalesceOfficialModel(
            string? selectedModel,
            IReadOnlyList<string> officialModels)
        {
            var model = (selectedModel ?? string.Empty).Trim();
            if (model.Length > 0 &&
                (officialModels.Count == 0 ||
                 officialModels.Any(item => string.Equals(item, model, StringComparison.OrdinalIgnoreCase))))
                return model;

            return officialModels.FirstOrDefault() ?? string.Empty;
        }
    }
}
