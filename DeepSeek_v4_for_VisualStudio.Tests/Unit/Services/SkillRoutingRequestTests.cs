using DeepSeek_v4_for_VisualStudio.Services.Agents;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class SkillRoutingRequestTests
{
    [Fact]
    public async Task CallAiWithMessagesAsync_WhenToolsDisabled_OmitsToolFields()
    {
        var sseLines = new[]
        {
            "data: {\"id\":\"chatcmpl-route-1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"{\\\"skill\\\":null}\"}}]}\n",
            "data: [DONE]\n",
        };

        var handler = new CapturingHttpMessageHandler(sseLines);
        var apiService = new DeepSeekApiService(new HttpClient(handler));
        var agent = new RoutingTestAgent(apiService)
        {
            BuiltInTools = new BuiltInToolService(),
        };

        var response = await agent.CallAiWithMessagesAsync(
            new List<ChatApiMessage>
            {
                new() { Role = "system", Content = "route" },
                new() { Role = "user", Content = "test" },
            },
            CancellationToken.None,
            toolChoice: "none",
            responseFormat: "json_object",
            includeTools: false);

        response.Should().Be("{\"skill\":null}");

        var body = handler.RequestBodies.Should().ContainSingle().Subject;
        body.Should().NotContain("\"tools\":");
        body.Should().NotContain("\"tool_choice\":");
    }

    private sealed class RoutingTestAgent : BaseAgent
    {
        public RoutingTestAgent(DeepSeekApiService apiService) : base(apiService, AgentType.Ask)
        {
        }

        protected override AgentDefinition CreateDefinition(AgentType agentType)
        {
            return new AgentDefinition
            {
                Type = AgentType.Ask,
                Name = "Ask",
                AllowedTools = new List<string>(),
                SystemPrompt = "test",
            };
        }

        public override Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context)
        {
            return Task.FromResult(new AgentResult { Content = userMessage });
        }
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        private readonly string[] _sseLines;

        public List<string> RequestBodies { get; } = new();

        public CapturingHttpMessageHandler(string[] sseLines)
        {
            _sseLines = sseLines;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync();
            RequestBodies.Add(body);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Join("", _sseLines), Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
