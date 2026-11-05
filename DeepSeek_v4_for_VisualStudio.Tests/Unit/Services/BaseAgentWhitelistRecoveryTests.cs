using DeepSeek_v4_for_VisualStudio.Services.Agents;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 白名单拒绝恢复行为测试：首次拒绝必须让模型看到错误并纠正；
/// 重复拒绝才终止工具循环。
/// </summary>
public class BaseAgentWhitelistRecoveryTests
{
    [Fact]
    public async Task FirstWhitelistRejection_AllowsModelToRecoverWithText()
    {
        var handler = new SequenceHttpMessageHandler(new[]
        {
            ToolCallSse("run_in_terminal", "{\"command\":\"python -c print(1)\"}"),
            ContentSse("当前 Agent 无法执行终端命令。"),
        });
        var agent = CreateAgent(handler);

        var result = await agent.RunLoopAsync(
            new List<ChatApiMessage> { new() { Role = "user", Content = "运行 Python" } },
            new List<string> { "read_file" },
            CancellationToken.None);

        result.Should().Contain("当前 Agent 无法执行终端命令");
        result.Should().NotContain("白名单外工具调用重复发生");
        handler.RequestBodies.Should().HaveCount(2);
    }

    [Fact]
    public async Task FirstWhitelistRejection_AllowsHandoffInSameRound()
    {
        var handler = new SequenceHttpMessageHandler(new[]
        {
            MultipleToolCallsSse(
                ("run_in_terminal", "{\"command\":\"python -c print(1)\"}"),
                ("request_handoff", "{\"targetAgent\":\"Edit\",\"reason\":\"需要终端\",\"taskDescription\":\"运行 Python 并返回输出\"}")),
        });
        var agent = CreateAgent(handler);
        agent.Context = new AgentContext();

        var result = await agent.RunLoopAsync(
            new List<ChatApiMessage> { new() { Role = "user", Content = "运行 Python" } },
            new List<string> { "request_handoff" },
            CancellationToken.None);

        result.Should().Contain("任务已移交给 Edit Agent");
        result.Should().NotContain("白名单外工具调用重复发生");
        handler.RequestBodies.Should().HaveCount(1);
        agent.PendingHandoffRequest.Should().NotBeNull();
        agent.PendingHandoffRequest!.TargetAgent.Should().Be(AgentType.Edit);
        // 线上 58 号请求回归：request_handoff 的转发前缀必须包含本轮完整的
        // assistant(tool_calls) + tool 结果，才能与 ContextManager 的 canonical history
        // 逐条对齐；否则后续记忆提取会在 handoff 位置分叉并造成整段 Prompt Cache miss。
        agent.Context.ForwardedMessages.Should().NotBeNull();
        agent.Context.ForwardedMessages.Should().HaveCount(4,
            "转发前缀必须包含本轮 user、assistant(tool_calls) 以及全部 tool 结果");
        agent.Context.ForwardedMessages.Should().Contain(m => m.Role == "user");

        var handoffAssistant = agent.Context.ForwardedMessages!.Single(m =>
            m.Role == "assistant"
            && m.ToolCalls != null
            && m.ToolCalls.Any(tc => tc.Function.Name == "request_handoff"));
        var handoffCall = handoffAssistant.ToolCalls!.Single(tc => tc.Function.Name == "request_handoff");

        var handoffResult = agent.Context.ForwardedMessages!.Single(m =>
            m.Role == "tool" && m.ToolCallId == handoffCall.Id);
        handoffResult.Content.Should().StartWith("HANDOFF_REQUESTED");
    }

    [Fact]
    public async Task RepeatedWhitelistRejection_TerminatesToolLoop()
    {
        var responses = Enumerable.Repeat(
            ToolCallSse("run_in_terminal", "{\"command\":\"python -c print(1)\"}"),
            5).ToArray();
        var handler = new SequenceHttpMessageHandler(responses);
        var agent = CreateAgent(handler);

        var result = await agent.RunLoopAsync(
            new List<ChatApiMessage> { new() { Role = "user", Content = "运行 Python" } },
            new List<string> { "read_file" },
            CancellationToken.None);

        result.Should().Contain("连续 5 轮调用白名单外工具");
        handler.RequestBodies.Should().HaveCount(5);
    }

    [Fact]
    public async Task WhitelistCompliantRound_ResetsWhitelistRejectionCounter()
    {
        var handler = new SequenceHttpMessageHandler(new[]
        {
            ToolCallSse("run_in_terminal", "{\"command\":\"python -c print(1)\"}"),
            ToolCallSse("read_file", "{\"filePath\":\"C:\\\\test\\\\file.txt\"}"),
            ToolCallSse("run_in_terminal", "{\"command\":\"python -c print(1)\"}"),
            ContentSse("再次说明无法执行终端命令。"),
        });
        var agent = CreateAgent(handler);

        var result = await agent.RunLoopAsync(
            new List<ChatApiMessage> { new() { Role = "user", Content = "运行 Python" } },
            new List<string> { "read_file" },
            CancellationToken.None);

        result.Should().Contain("再次说明无法执行终端命令");
        result.Should().NotContain("连续 5 轮调用白名单外工具");
        handler.RequestBodies.Should().HaveCount(4);
    }

    [Fact]
    public async Task AskAgent_DirectExploration_AppendsHintAfterActualResult_WithoutBlocking()
    {
        const int threshold = BaseAgent.AskDirectExplorationHintThreshold;
        int totalReads = threshold + 2;

        string root = Path.Combine(Path.GetTempPath(), "ask-exploration-hint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string[] files = Enumerable.Range(0, totalReads)
            .Select(i => "file" + i + ".txt")
            .ToArray();
        foreach (string file in files)
            File.WriteAllText(Path.Combine(root, file),
                "content of " + file + "\n" + new string('x', 40 + file.Length));

        try
        {
            var handler = new SequenceHttpMessageHandler(
                files.Select((f, i) => ToolCallSse("read_file",
                        "{\"filePath\":\"" + Path.Combine(root, f).Replace("\\", "\\\\") + "\"}",
                        "call_hint_" + i))
                    .Concat(new[] { ContentSse("done exploring") })
                    .ToArray());
            var agent = CreateAgent(handler);
            agent.ExploreAgent = new ExploreAgent(new DeepSeekApiService(
                new HttpClient(new SequenceHttpMessageHandler(Array.Empty<string>()))));

            var result = await agent.RunLoopAsync(
                new List<ChatApiMessage> { new() { Role = "user", Content = "调查多个文件" } },
                new List<string> { "read_file", "runSubagent" },
                CancellationToken.None,
                root);

            string expectedHint = LocalizationService.Instance["tool.ask.explorationHint"];
            expectedHint.Should().NotContain(threshold.ToString(),
                "提示文案不应暴露具体次数或上限");

            var bodies = handler.RequestBodies
                .Select(System.Text.RegularExpressions.Regex.Unescape).ToList();
            int hintBodyIndex = bodies.FindIndex(b => b.Contains(expectedHint, StringComparison.Ordinal));
            hintBodyIndex.Should().BeGreaterThan(0);

            string hintBody = bodies[hintBodyIndex];
            hintBody.Should().Contain("content of " + files[threshold],
                "触发提示的这次探索仍必须真实执行并返回工具结果");
            hintBody.Should().Contain(expectedHint);

            CountNoticeAdditions(bodies, expectedHint).Should()
                .Be(1, "该提示在单次 Ask 工具循环内只应追加一次");

            // 提示不会拦截后续直探：第 threshold+2 次读取仍真实执行。
            bodies[bodies.Count - 1].Should().Contain("content of " + files[threshold + 1]);
            result.Should().Contain("done exploring");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* 清理失败不影响断言 */ }
        }
    }

    /// <summary>
    /// 阈值以内只执行探索，不追加任何提示或拦截。
    /// </summary>
    [Fact]
    public async Task AskAgent_DirectExploration_FirstThresholdCallsDoNotShowHint()
    {
        const int threshold = BaseAgent.AskDirectExplorationHintThreshold;

        string root = Path.Combine(Path.GetTempPath(), "ask-exploration-no-hint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string[] files = Enumerable.Range(0, threshold)
            .Select(i => "file" + i + ".txt")
            .ToArray();
        foreach (string file in files)
            File.WriteAllText(Path.Combine(root, file),
                "content of " + file + "\n" + new string('y', 40 + file.Length));

        try
        {
            var handler = new SequenceHttpMessageHandler(
                files.Select((f, i) => ToolCallSse("read_file",
                        "{\"filePath\":\"" + Path.Combine(root, f).Replace("\\", "\\\\") + "\"}",
                        "call_no_hint_" + i))
                    .Concat(new[] { ContentSse("done exploring") })
                    .ToArray());
            var agent = CreateAgent(handler);
            agent.ExploreAgent = new ExploreAgent(new DeepSeekApiService(
                new HttpClient(new SequenceHttpMessageHandler(Array.Empty<string>()))));

            var result = await agent.RunLoopAsync(
                new List<ChatApiMessage> { new() { Role = "user", Content = "调查多个文件" } },
                new List<string> { "read_file", "runSubagent" },
                CancellationToken.None,
                root);

            string expectedHint = LocalizationService.Instance["tool.ask.explorationHint"];
            var bodies = handler.RequestBodies
                .Select(System.Text.RegularExpressions.Regex.Unescape).ToList();
            bodies.Should().NotContain(b => b.Contains(expectedHint, StringComparison.Ordinal));

            foreach (string file in files)
                bodies[bodies.Count - 1].Should().Contain("content of " + file);

            result.Should().Contain("done exploring");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* 清理失败不影响断言 */ }
        }
    }

    /// <summary>
    /// 统计"提示新增"发生的轮次数。
    /// 提示一旦插入就会留在对话历史中，之后每轮请求都会包含它；
    /// 而 Explore 子代理复用同一 handler，它自己的请求不含该提示，
    /// 会让出现次数回落，故按"历史峰值被突破"的次数计，而非逐轮比较。
    /// </summary>
    private static int CountNoticeAdditions(IEnumerable<string> bodies, string notice)
    {
        int peak = 0, additions = 0;
        foreach (string body in bodies)
        {
            int now = CountOccurrences(body, notice);
            if (now > peak)
            {
                peak = now;
                additions++;
            }
        }
        return additions;
    }

    /// <summary>统计子串出现次数（提示会残留在历史里，用于判断"新增"轮次）。</summary>
    private static int CountOccurrences(string text, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        int count = 0, i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    private static RecoveryTestAgent CreateAgent(SequenceHttpMessageHandler handler)
    {
        var apiService = new DeepSeekApiService(new HttpClient(handler));
        return new RecoveryTestAgent(apiService)
        {
            BuiltInTools = new BuiltInToolService(),
        };
    }

    private static string ContentSse(string content) =>
        "data: {\"id\":\"test\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" +
        content.Replace("\"", "\\\"") + "\"}}]}\n\ndata: [DONE]\n";

    private static string ToolCallSse(string name, string arguments) =>
        ToolCallSse(name, arguments, "call_1");

    private static string ToolCallSse(string name, string arguments, string callId) =>
        "data: {\"id\":\"test\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[" +
        "{\"index\":0,\"id\":\"" + callId + "\",\"type\":\"function\",\"function\":{\"name\":\"" + name +
        "\",\"arguments\":\"" + arguments.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}}]}}]}\n\ndata: [DONE]\n";

    private static string MultipleToolCallsSse(params (string Name, string Arguments)[] calls)
    {
        var callJson = string.Join(",", calls.Select((call, index) =>
            "{\"index\":" + index + ",\"id\":\"call_" + (index + 1) +
            "\",\"type\":\"function\",\"function\":{\"name\":\"" + call.Name +
            "\",\"arguments\":\"" + call.Arguments.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}}"));

        return "data: {\"id\":\"test\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[" +
            callJson + "]}}]}\n\ndata: [DONE]\n";
    }

    private sealed class RecoveryTestAgent : BaseAgent
    {
        public RecoveryTestAgent(DeepSeekApiService apiService) : base(apiService, AgentType.Ask)
        {
        }

        public Task<string> RunLoopAsync(
            List<ChatApiMessage> messages,
            List<string> whitelist,
            CancellationToken ct,
            string? workspaceRoot = null)
            => CallAiWithToolLoopAsync(messages, workspaceRoot, ct, toolWhitelist: whitelist);

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
            => Task.FromResult(new AgentResult { Content = userMessage });
    }

    private sealed class SequenceHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public List<string> RequestBodies { get; } = new();

        public SequenceHttpMessageHandler(IEnumerable<string> responses)
        {
            _responses = new Queue<string>(responses);
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
                Content = new StringContent(
                    _responses.Count > 0 ? _responses.Dequeue() : ContentSse("no response"),
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        }
    }
}
