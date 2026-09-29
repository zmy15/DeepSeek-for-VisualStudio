using DeepSeek_v4_for_VisualStudio.Services.Agents;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 回归测试：Ask Agent 生成变更总结时的润色调用必须是"无工具"调用。
/// 此前该路径使用空白名单 + 完整工具集，模型看到 read_file 后调用，
/// 被白名单拦截并终止循环，导致总结被替换成"白名单外工具调用"警告。
/// </summary>
public class AskAgentSummaryPolishingTests
{
    [Fact]
    public async Task ToolLoop_WithEmptyWhitelist_SendsToolChoiceNone()
    {
        var sseLines = new[]
        {
            "data: {\"id\":\"chatcmpl-guard-1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"summary text\"}}]}\n",
            "data: [DONE]\n",
        };

        var handler = new CapturingHttpMessageHandler(sseLines);
        var apiService = new DeepSeekApiService(new HttpClient(handler));
        var agent = new EmptyWhitelistToolLoopAgent(apiService)
        {
            BuiltInTools = new BuiltInToolService(),
        };

        var result = await agent.RunLoopAsync(
            new List<ChatApiMessage> { new() { Role = "user", Content = "总结" } },
            CancellationToken.None);

        result.Should().Contain("summary text");
        handler.RequestBodies.Should().ContainSingle()
            .Which.Should().Contain("\"tool_choice\":\"none\"");
    }

    [Fact]
    public async Task ExecuteAsync_SummaryHandoff_IgnoresToolCallsAndReturnsSummary()
    {
        // 模拟模型先尝试调用 read_file，随后输出正常总结文本。
        var sseLines = new[]
        {
            "data: {\"id\":\"chatcmpl-summary-1\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_read\",\"type\":\"function\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"filePath\\\":\\\"C:\\\\proj\\\\A.cs\\\"}\"}}]}}]}\n",
            "data: {\"id\":\"chatcmpl-summary-1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"已完成功能 X，修改了 A.cs 与 B.cs。\"}}]}\n",
            "data: [DONE]\n",
        };

        var handler = new CapturingHttpMessageHandler(sseLines);
        var apiService = new DeepSeekApiService(new HttpClient(handler));
        var agent = new AskAgent(apiService)
        {
            BuiltInTools = new BuiltInToolService(),
        };

        var context = new AgentContext
        {
            ActivePlan = new AgentTaskPlan
            {
                Title = "实现功能 X",
                IsCompleted = true,
                ChangedFiles =
                {
                    new FileChangeSummary
                    {
                        FilePath = @"C:\proj\A.cs",
                        LinesAdded = 10,
                        BriefDescription = "实现功能 X",
                    },
                },
            },
        };

        var result = await agent.ExecuteAsync("请根据上文生成变更总结，不要调用工具", context);

        result.Success.Should().BeTrue();
        result.Content.Should().Contain("已完成功能 X");
        result.Content.Should().NotContain("白名单外工具调用");

        // 润色请求必须显式禁用工具调用，而不是只靠客户端空白名单拦截。
        handler.RequestBodies.Should().ContainSingle()
            .Which.Should().Contain("\"tool_choice\":\"none\"");
    }

    [Fact]
    public async Task ExecuteAsync_SummaryHandoff_WithForwardedMessages_UsesFullContextAndTailSignature()
    {
        // 模拟 Handoff 完整上下文（含历史轮次与来源 Agent 末尾身份提示词），验证润色请求：
        // 1) 完整保留 ForwardedMessages 前缀；2) 末尾恒为 [user 待润色草稿][system 润色指令]。
        var sseLines = new[]
        {
            "data: {\"id\":\"chatcmpl-fwd-1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"最终总结文本\"}}]}\n",
            "data: [DONE]\n",
        };

        var handler = new CapturingHttpMessageHandler(sseLines);
        var apiService = new DeepSeekApiService(new HttpClient(handler));
        var agent = new AskAgent(apiService)
        {
            BuiltInTools = new BuiltInToolService(),
        };

        var context = new AgentContext
        {
            IsExplicitRoute = true,
            ExplicitRouteTarget = AgentType.Ask,
            ForwardedMessages = new List<ChatApiMessage>
            {
                new() { Role = "system", Content = "SharedImmutablePrefix" },
                new() { Role = "user", Content = "把 A.cs 改成异步实现" },
                new() { Role = "assistant", Content = "已修改 A.cs。" },
                new() { Role = "system", Content = "来源 Agent 末尾身份提示词" },
            },
            ActivePlan = new AgentTaskPlan
            {
                Title = "实现功能 X",
                IsCompleted = true,
                ChangedFiles =
                {
                    new FileChangeSummary
                    {
                        FilePath = @"C:\proj\A.cs",
                        LinesAdded = 10,
                        BriefDescription = "实现功能 X",
                    },
                },
            },
        };

        // 模拟真实 Handoff：orchestrator 会先把 AgentContext 注入 BaseAgent.Context，
        // BuildContextAwareMessages 才能读取 ForwardedMessages 完整前缀。
        agent.Context = context;

        var result = await agent.ExecuteAsync("请根据上文生成变更总结", context);

        result.Success.Should().BeTrue();
        result.Content.Should().Contain("最终总结文本");

        var body = handler.RequestBodies.Should().ContainSingle().Which;
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();

        // 完整转发前缀保留（含历史轮次），末位来源身份提示词由基类移除
        messages.Should().Contain(m => m.GetProperty("role").GetString() == "user"
            && m.GetProperty("content").GetString() == "把 A.cs 改成异步实现");
        messages.Should().Contain(m => m.GetProperty("role").GetString() == "assistant"
            && m.GetProperty("content").GetString() == "已修改 A.cs。");
        messages.Should().NotContain(m => m.GetProperty("content").GetString() == "来源 Agent 末尾身份提示词");

        // 尾部签名：[user 待润色草稿][system 润色指令]，doNotHandoff 路由指令不尾随
        // net472 无 System.Index，不能用 ^n 索引语法，改用显式 Count 索引
        messages[messages.Count - 1].GetProperty("role").GetString().Should().Be("system");
        messages[messages.Count - 1].GetProperty("content").GetString().Should().Be(AiPrompts.SummaryPolishSystemPrompt);
        messages[messages.Count - 2].GetProperty("role").GetString().Should().Be("user");
        messages[messages.Count - 2].GetProperty("content").GetString().Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// 暴露 CallAiWithToolLoopAsync 以验证空白名单兜底行为。
    /// </summary>
    private sealed class EmptyWhitelistToolLoopAgent : BaseAgent
    {
        public EmptyWhitelistToolLoopAgent(DeepSeekApiService apiService) : base(apiService, AgentType.Ask)
        {
        }

        public Task<string> RunLoopAsync(List<ChatApiMessage> messages, CancellationToken ct)
            => CallAiWithToolLoopAsync(messages, null, ct, toolWhitelist: new List<string>());

        protected override AgentDefinition CreateDefinition(AgentType agentType)
        {
            return new AgentDefinition
            {
                Type = AgentType.Ask,
                Name = "Ask",
                AllowedTools = new List<string>(AskAgent.AskTools),
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
