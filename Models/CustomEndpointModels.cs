using System;
using System.Collections.Generic;
using System.Linq;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    /// <summary>
    /// One OpenAI-compatible custom endpoint and the models configured for it.
    /// </summary>
    public sealed class CustomEndpointConfig
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = string.Empty;

        public string BaseUrl { get; set; } = string.Empty;

        public string ApiKey { get; set; } = string.Empty;

        public List<string> Models { get; set; } = new();

        public CustomEndpointConfig()
        {
        }

        public CustomEndpointConfig(
            string id,
            string name,
            string baseUrl,
            string apiKey,
            IEnumerable<string>? models = null)
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim();
            Name = name ?? string.Empty;
            BaseUrl = baseUrl ?? string.Empty;
            ApiKey = apiKey ?? string.Empty;
            Models = models?
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => model.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
        }

        public CustomEndpointConfig Clone()
            => new(Id, Name, BaseUrl, ApiKey, Models);
    }

    /// <summary>
    /// A concrete model exposed by one custom endpoint.
    /// </summary>
    public sealed record CustomEndpointModelEntry(
        string EndpointId,
        string EndpointName,
        string Model);
}
