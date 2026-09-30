using DeepSeek_v4_for_VisualStudio.Services.Agents;
using System.Net;
using System.Net.Http;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Tests.Integration;

public class AgentFactoryTests
{
    [Fact]
    public void Constructor_WithValidApiService_CreatesSuccessfully()
    {
        var apiService = new DeepSeekApiService("test-key");

        var factory = new AgentFactory(apiService);

        factory.Should().NotBeNull();
    }

    [Fact]
    public void AskAgent_IsCreatedLazily()
    {
        var apiService = new DeepSeekApiService("test-key");
        var factory = new AgentFactory(apiService);

        var agent = factory.AskAgent;

        agent.Should().NotBeNull();
        agent.Definition.Type.Should().Be(AgentType.Ask);
    }

    [Fact]
    public void ExploreAgent_IsCreatedLazily()
    {
        var apiService = new DeepSeekApiService("test-key");
        var factory = new AgentFactory(apiService);

        var agent = factory.ExploreAgent;

        agent.Should().NotBeNull();
    }

    [Fact]
    public void PlanAgent_IsCreatedLazily()
    {
        var apiService = new DeepSeekApiService("test-key");
        var factory = new AgentFactory(apiService);

        var agent = factory.PlanAgent;

        agent.Should().NotBeNull();
    }

    [Fact]
    public void EditAgent_IsCreatedLazily()
    {
        var apiService = new DeepSeekApiService("test-key");
        var factory = new AgentFactory(apiService);

        var agent = factory.EditAgent;

        agent.Should().NotBeNull();
    }

    [Fact]
    public void GetAgent_ByType_ReturnsCorrectAgent()
    {
        var apiService = new DeepSeekApiService("test-key");
        var factory = new AgentFactory(apiService);

        var askAgent = factory.GetAgent(AgentType.Ask);
        var editAgent = factory.GetAgent(AgentType.Edit);

        askAgent.Should().BeOfType<AskAgent>();
        editAgent.Should().BeOfType<EditAgent>();
    }

    [Fact]
    public void UpdateMcpManager_WithNull_DoesNotThrow()
    {
        var apiService = new DeepSeekApiService("test-key");
        var factory = new AgentFactory(apiService);

        var act = () => factory.UpdateMcpManager(null!);

        act.Should().NotThrow();
    }

    [Fact]
    public async Task ExecuteAsync_AskAgent_ReturnsResult()
    {
        var sseLines = new[]
        {
            "data: {\"id\":\"chatcmpl-99\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Mocked response\"}}]}\n",
            "data: [DONE]\n",
        };

        var handler = new TestHttpMessageHandler(sseLines, HttpStatusCode.OK);
        var httpClient = new HttpClient(handler);
        var apiService = new DeepSeekApiService(httpClient);
        var factory = new AgentFactory(apiService);

        var context = new AgentContext
        {
            SolutionPath = @"F:\Test\Test.sln",
            CancellationToken = CancellationToken.None,
        };

        var askAgent = factory.AskAgent;
        var result = await askAgent.ExecuteAsync("Test question", context);

        result.Should().NotBeNull();
    }

    /// <summary>
    /// 需求：两个截图工具（capture_window / capture_webpage）必须在所有 Agent 的白名单里
    /// 可用，并保持对称——不能让某个 Agent 出现「能截网页却截不了窗口」这类漂移。
    /// 「仅视觉模型提供」是另一层门控，由 BuiltInToolService.IsToolAvailableForCurrentModel
    /// 在工具定义下发前过滤，与本测试无关。
    /// </summary>
    [Theory]
    [InlineData(AgentType.Ask)]
    [InlineData(AgentType.Explore)]
    [InlineData(AgentType.Plan)]
    [InlineData(AgentType.Edit)]
    [InlineData(AgentType.Build)]
    public void AllAgents_ExposeBothCaptureTools(AgentType agentType)
    {
        var apiService = new DeepSeekApiService("test-key");
        var factory = new AgentFactory(apiService);

        var agent = factory.GetAgent(agentType);

        agent.Definition.AllowedTools.Should().Contain("capture_window");
        agent.Definition.AllowedTools.Should().Contain("capture_webpage");
    }
}
