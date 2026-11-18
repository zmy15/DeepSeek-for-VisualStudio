using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using System.Net;
using System.Net.Http;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class ResponseFormatOmissionTests
{
    [Fact]
    public async Task CompleteAsync_DoesNotSendResponseFormat()
    {
        var handler = new CapturingHandler(
            """{"choices":[{"message":{"role":"assistant","content":"{}"}}]}""");
        using var service = new DeepSeekApiService(
            new HttpClient(handler),
            "deepseek-v4-pro",
            baseUrl: "https://api.deepseek.com");

        await service.CompleteAsync(
            new List<ChatApiMessage> { new() { Role = "user", Content = "return json" } });

        handler.RequestBody.Should().NotContain("\"response_format\"");
    }

    [Fact]
    public async Task ChatStreamAsync_DoesNotSendResponseFormat()
    {
        var handler = new CapturingHandler(
            "data: {\"choices\":[{\"delta\":{\"content\":\"{}\"}}]}\n\ndata: [DONE]\n\n",
            "text/event-stream");
        using var service = new DeepSeekApiService(
            new HttpClient(handler),
            "deepseek-v4-pro",
            baseUrl: "https://api.deepseek.com");

        await foreach (var _ in service.ChatStreamAsync(
            new List<ChatApiMessage> { new() { Role = "user", Content = "return json" } }))
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
