using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.Agents;
using DeepSeek_v4_for_VisualStudio.Services.Providers;
using System.Net;
using System.Net.Http;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class DeepSeekConversationTitleTests
{
    [Fact]
    public async Task OfficialChatStream_RequestsTitleAndYieldsTitleChunk()
    {
        const string sse =
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"answer\"}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"title\":\"官方标题\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new CapturingHttpMessageHandler(sse);
        using var provider = new DeepSeekProvider(
            new HttpClient(handler),
            model: "deepseek-flash");

        var chunks = new List<string>();
        await foreach (string chunk in provider.ChatStreamAsync(
            new[] { new ChatApiMessage { Role = "user", Content = "你好" } },
            requestConversationTitle: true))
        {
            chunks.Add(chunk);
        }

        handler.RequestBodies.Should().ContainSingle()
            .Which.Should().Contain("\"title\":{\"enabled\":true}");
        chunks.Should().Contain("[TITLE]官方标题");
        string.Concat(chunks).Should().Contain("answer");
    }

    [Fact]
    public async Task CustomEndpoint_DoesNotReceiveOfficialTitleField()
    {
        const string sse =
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"answer\"}}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new CapturingHttpMessageHandler(sse);
        using var provider = new DeepSeekProvider(
            new HttpClient(handler),
            model: "deepseek-flash",
            baseUrl: "https://relay.example.com/v1",
            isCustom: true);

        await foreach (string _ in provider.ChatStreamAsync(
            new[] { new ChatApiMessage { Role = "user", Content = "你好" } },
            requestConversationTitle: true))
        {
        }

        handler.RequestBodies.Should().ContainSingle()
            .Which.Should().NotContain("\"title\":");
    }

    [Fact]
    public async Task AgentToolLoop_RequestsTitleOnlyOnFirstMainRequestAndDispatchesIt()
    {
        const string sse =
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"answer\"}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"title\":\"首个请求标题\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new CapturingHttpMessageHandler(sse);
        var apiService = new DeepSeekApiService(new HttpClient(handler));
        var agent = new TitleProbeAgent(apiService);
        string? receivedTitle = null;
        int completedWithoutTitle = 0;
        agent.Context = new AgentContext
        {
            RequestConversationTitle = true,
            OnConversationTitleReceived = title => receivedTitle = title,
            OnConversationTitleRequestCompleted = () => completedWithoutTitle++,
        };

        string result = await agent.RunLoopAsync(
            new List<ChatApiMessage> { new() { Role = "user", Content = "生成标题" } },
            CancellationToken.None);

        result.Should().Contain("answer");
        receivedTitle.Should().Be("首个请求标题");
        completedWithoutTitle.Should().Be(0);
        agent.Context.RequestConversationTitle.Should().BeFalse();
        handler.RequestBodies.Should().ContainSingle()
            .Which.Should().Contain("\"title\":{\"enabled\":true}");
    }

    [Fact]
    public async Task AgentToolLoop_DoesNotReuseTitleFlag_AndCompletesFallbackWhenTitleMissing()
    {
        const string sse =
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"answer\"}}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new CapturingHttpMessageHandler(sse);
        var apiService = new DeepSeekApiService(new HttpClient(handler));
        var agent = new TitleProbeAgent(apiService);
        string? receivedTitle = null;
        int completedWithoutTitle = 0;
        agent.Context = new AgentContext
        {
            RequestConversationTitle = true,
            OnConversationTitleReceived = title => receivedTitle = title,
            OnConversationTitleRequestCompleted = () => completedWithoutTitle++,
        };
        var messages = new List<ChatApiMessage> { new() { Role = "user", Content = "生成标题" } };

        await agent.RunLoopAsync(messages, CancellationToken.None);
        await agent.RunLoopAsync(messages, CancellationToken.None);

        receivedTitle.Should().BeNull();
        completedWithoutTitle.Should().Be(1);
        handler.RequestBodies.Should().HaveCount(2);
        handler.RequestBodies[0].Should().Contain("\"title\":{\"enabled\":true}");
        handler.RequestBodies[1].Should().NotContain("\"title\":");
    }

    private sealed class TitleProbeAgent : BaseAgent
    {
        public TitleProbeAgent(DeepSeekApiService apiService) : base(apiService, AgentType.Ask)
        {
        }

        public Task<string> RunLoopAsync(List<ChatApiMessage> messages, CancellationToken ct)
            => CallAiWithToolLoopAsync(messages, null, ct, toolWhitelist: new List<string>());

        protected override AgentDefinition CreateDefinition(AgentType agentType)
        {
            return new AgentDefinition
            {
                Type = AgentType.Ask,
                Name = "TitleProbe",
                AllowedTools = new List<string>(),
                SystemPrompt = "test",
            };
        }

        public override Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context)
            => Task.FromResult(new AgentResult { Content = userMessage });
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _sse;

        public CapturingHttpMessageHandler(string sse)
        {
            _sse = sse;
        }

        public List<string> RequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync();
            RequestBodies.Add(body);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_sse, Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
