using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class ResponseFormatCompatibilityTests
{
    private const string ChatOkResponse =
        """{"choices":[{"message":{"role":"assistant","content":"{}"}}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";

    [Fact]
    public async Task CompleteAsync_DeepSeekOfficial_IncludesJsonObjectResponseFormat()
    {
        var handler = new CapturingHandler(ChatOkResponse);
        using var service = new DeepSeekApiService(
            new HttpClient(handler),
            "deepseek-v4-pro",
            baseUrl: "https://api.deepseek.com");

        await service.CompleteAsync(
            new List<ChatApiMessage> { new() { Role = "user", Content = "return json" } },
            responseFormat: "json_object");

        handler.RequestBody.Should().Contain("\"response_format\":{\"type\":\"json_object\"}");
    }

    [Fact]
    public async Task CompleteAsync_NonDeepSeekCustomModel_OmitsJsonObjectResponseFormat()
    {
        var handler = new CapturingHandler(ChatOkResponse);
        using var service = new DeepSeekApiService(
            new HttpClient(handler),
            "qwen3-max",
            baseUrl: "https://relay.example.com/v1",
            isCustom: true);

        await service.CompleteAsync(
            new List<ChatApiMessage> { new() { Role = "user", Content = "return json" } },
            responseFormat: "json_object");

        handler.RequestBody.Should().NotContain("\"response_format\"");
    }

    [Fact]
    public async Task CompleteAsync_DeepSeekModelOnCustomEndpoint_IncludesJsonObjectResponseFormat()
    {
        var handler = new CapturingHandler(ChatOkResponse);
        using var service = new DeepSeekApiService(
            new HttpClient(handler),
            "deepseek-v4-flash",
            baseUrl: "https://relay.example.com/v1",
            isCustom: true);

        await service.CompleteAsync(
            new List<ChatApiMessage> { new() { Role = "user", Content = "return json" } },
            responseFormat: "json_object");

        handler.RequestBody.Should().Contain("\"response_format\":{\"type\":\"json_object\"}");
    }

    [Fact]
    public async Task ChatStreamAsync_NonDeepSeekCustomModel_OmitsJsonObjectResponseFormat()
    {
        var handler = new CapturingHandler(
            "data: {\"choices\":[{\"delta\":{\"content\":\"{}\"}}]}\n\ndata: [DONE]\n\n",
            "text/event-stream");
        using var service = new DeepSeekApiService(
            new HttpClient(handler),
            "qwen3-max",
            baseUrl: "https://relay.example.com/v1",
            isCustom: true);

        await foreach (var _ in service.ChatStreamAsync(
            new List<ChatApiMessage> { new() { Role = "user", Content = "return json" } },
            responseFormat: "json_object"))
        {
        }

        handler.RequestBody.Should().NotContain("\"response_format\"");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _responseJson;
        private readonly string _mediaType;

        public CapturingHandler(string responseJson, string mediaType = "application/json")
        {
            _responseJson = responseJson;
            _mediaType = mediaType;
        }

        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson, Encoding.UTF8, _mediaType),
            };
        }
    }
}
