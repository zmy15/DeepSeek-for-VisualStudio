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
        agent.Context.ForwardedMessages.Should().NotBeNull();
        agent.Context.ForwardedMessages.Should().HaveCount(1);
        agent.Context.ForwardedMessages![0].Role.Should().Be("user");
        agent.Context.ForwardedMessages![0].ToolCalls.Should().BeNull();
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
    public async Task AskAgent_DirectExplorationBudget_BlocksCallBeyondLimitAndRequiresExplore()
    {
        // 预算内的调用次数与上限必须一致，避免测试与 AskDirectExplorationCallLimit 脱节。
        const int limit = BaseAgent.AskDirectExplorationCallLimit;

        // read_file 必须成功，否则连续 5 轮错误会先触发"连续错误"终止保护，测不到探索预算。
        string root = Path.Combine(Path.GetTempPath(), "ask-exploration-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // 多准备一个文件用于"超预算"的那次读取。
        string[] files = Enumerable.Range(0, limit + 1)
            .Select(i => ((char)('a' + i)) + ".txt")
            .ToArray();
        foreach (string file in files)
            // 内容必须各不相同：连续近似相同的读取会先触发"无进展"保护而测不到探索预算。
            File.WriteAllText(Path.Combine(root, file),
                "content of " + file + "\n" + new string('x', 40 + file[0]));

        try
        {
            // 前 limit 轮直探落在预算内，第 limit+1 轮被拦截并提示委派 Explore。
            // read_file 不会把相对路径拼到 workspaceRoot，故这里传绝对路径。
            var handler = new SequenceHttpMessageHandler(
                files.Select((f, i) => ToolCallSse("read_file",
                        "{\"filePath\":\"" + Path.Combine(root, f).Replace("\\", "\\\\") + "\"}",
                        "call_read_" + i))
                    .Concat(new[]
                    {
                        // 被拦截后模型改用 runSubagent；子代理复用父级 _apiService，
                        // 会消耗同一个脚本队列，故紧接一条子代理自身的回复。
                        ToolCallSse("runSubagent", "{\"agentName\":\"Explore\",\"prompt\":\"调查多个文件\"}", "call_sub"),
                        ContentSse("explore subagent findings"),
                        ContentSse("delegated"),
                    })
                    .ToArray());
            var agent = CreateAgent(handler);
            // ExploreAgent 非空是探索预算生效的前提之一（见 BaseAgent.canDelegateAskExploration）。
            // 这里注入一个空队列的实例即可：子代理实体由 BaseAgent 用父级 _apiService 重新构造。
            agent.ExploreAgent = new ExploreAgent(new DeepSeekApiService(
                new HttpClient(new SequenceHttpMessageHandler(Array.Empty<string>()))));

            var result = await agent.RunLoopAsync(
                new List<ChatApiMessage> { new() { Role = "user", Content = "调查多个文件" } },
                new List<string> { "read_file", "runSubagent" },
                CancellationToken.None,
                root);

            // 请求体是 JSON，中文会被转义成 \uXXXX，必须先解码才能匹配文案。
            string expectedLimitMessage = string.Format(
                LocalizationService.Instance["tool.ask.explorationLimitReached"], limit, limit);
            int blockedRound = handler.RequestBodies.FindIndex(b =>
                System.Text.RegularExpressions.Regex.Unescape(b).Contains(expectedLimitMessage));
            blockedRound.Should().BeGreaterThan(0, "超出预算的直接探索必须被拦截，并把提示回传给模型"
                + "（实际请求数 " + handler.RequestBodies.Count + "）");

            string blockedBody = System.Text.RegularExpressions.Regex.Unescape(handler.RequestBodies[blockedRound]);

            // 预算内的 limit 次 read_file 都真实执行成功（不许被误拦）。
            foreach (string f in files.Take(limit))
                blockedBody.Should().Contain("content of " + f);

            // 超预算那次的文件从未被读取过，说明它确实没执行。
            blockedBody.Should().NotContain("content of " + files[limit]);

            // 提示只发一次：它会留在对话历史里，但"新增"应恰好发生在一轮请求中。
            // 注意 Explore 子代理复用同一 handler，它自己的请求不含该提示，
            // 会让计数回落；因此只跟踪"至今见过的最多次数"是否被突破过。
            var bodies = handler.RequestBodies
                .Select(System.Text.RegularExpressions.Regex.Unescape).ToList();
            CountNoticeAdditions(bodies, expectedLimitMessage).Should()
                .Be(1, "该提示在单次 Ask 工具循环内只应新增一次"
                    + " | counts=" + string.Join(",", bodies.Select(b => CountOccurrences(b, expectedLimitMessage))));

            // 提示要求委派 Explore，模型据此调用 runSubagent。
            blockedBody.Should().Contain("runSubagent");

            // 委派成功：子代理结果进入历史，主循环正常收尾。
            string finalBody = System.Text.RegularExpressions.Regex.Unescape(
                handler.RequestBodies[handler.RequestBodies.Count - 1]);
            finalBody.Should().Contain("explore subagent findings");
            result.Should().Contain("delegated");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* 清理失败不影响断言 */ }
        }
    }

    /// <summary>
    /// 预算只是提醒而非硬上限：第 limit+1 次被拦并提示一次后，
    /// 后续直接探索调用应重新放行（不再拦截、不再重复提示）。
    /// </summary>
    [Fact]
    public async Task AskAgent_DirectExplorationBudget_AllowsCallsAfterSingleNotice()
    {
        const int limit = BaseAgent.AskDirectExplorationCallLimit;
        // 前 limit 次放行 + 1 次被拦 + 之后 2 次重新放行。
        int totalReads = limit + 3;

        string root = Path.Combine(Path.GetTempPath(), "ask-exploration-notice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string[] files = Enumerable.Range(0, totalReads)
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
                        "call_r" + i))
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

            string expectedNotice = string.Format(
                LocalizationService.Instance["tool.ask.explorationLimitReached"], limit, limit);
            var bodies = handler.RequestBodies
                .Select(System.Text.RegularExpressions.Regex.Unescape).ToList();

            // 提示只新增一次（之后残留在历史中属于正常）。
            CountNoticeAdditions(bodies, expectedNotice).Should()
                .Be(1, "该提示在单次 Ask 工具循环内只应新增一次");

            // 第 limit+1 个文件确实没被读取。
            bodies[limit + 1].Should().NotContain("content of " + files[limit]);

            // 提示之后的两次直探重新放行，内容出现在历史里。
            string last = bodies[bodies.Count - 1];
            last.Should().Contain("content of " + files[limit + 1]);
            last.Should().Contain("content of " + files[limit + 2]);

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
