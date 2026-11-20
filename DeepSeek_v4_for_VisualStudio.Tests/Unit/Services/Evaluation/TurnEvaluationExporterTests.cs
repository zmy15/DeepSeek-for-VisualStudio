using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.Evaluation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services.Evaluation
{
    public class TurnEvaluationExporterTests : IDisposable
    {
        private readonly string _dir;

        public TurnEvaluationExporterTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ds-eval-export-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void Build_CreatesJudgeReadyTurnWithToolTraceAndMetrics()
        {
            var (tree, messages) = BuildConversation();
            var apiHistory = new List<ChatApiMessage>
            {
                new()
                {
                    Role = "user",
                    Content = "Current user requirement: fix the error",
                },
                new()
                {
                    Role = "assistant",
                    Content = "Reading the file.",
                    ToolCalls = new List<ToolCall>
                    {
                        new()
                        {
                            Id = "call-1",
                            Function = new ToolCallFunction
                            {
                                Name = "read_file",
                                Arguments = "{\"path\":\"a.cs\"}",
                            },
                        },
                    },
                },
                new()
                {
                    Role = "tool",
                    ToolCallId = "call-1",
                    Name = "read_file",
                    Content = "class A {}",
                },
                new()
                {
                    Role = "assistant",
                    Content = "Fixed.",
                },
            };
            var metrics = new AgentTurnMetrics
            {
                Turn = 1,
                TtftMs = 300,
                DurationMs = 1200,
                InputTokens = 100,
                OutputTokens = 20,
                CacheHitTokens = 80,
                CacheMissTokens = 20,
            };

            var document = TurnEvaluationExporter.Build(new TurnEvaluationExporter.Request
            {
                Session = new ChatSession { Id = "session-1", Title = "Fix" },
                Tree = tree,
                VisibleMessages = messages,
                AssistantMessageIndex = 1,
                ApiHistory = apiHistory,
                LatestExecutionMetrics = metrics,
                SessionTelemetryJson =
                    "{\"turn_count\":2,\"tool_call_count\":3,\"input_tokens\":250," +
                    "\"output_tokens\":50,\"cache_hit_rate\":0.8}",
                Model = "test-model",
                SolutionPath = @"C:\work\Sample.sln",
                ContextDebugJson = "{\"tokens\":{\"estimated\":100}}",
            });

            document.Turn.User.Content.Should().Be("fix the error");
            document.Turn.Assistant.Content.Should().Be("Fixed.");
            document.Turn.TurnIndex.Should().Be(1);
            document.Conversation.TurnMessages.Should().HaveCount(4);
            document.Conversation.ToolTrace.Should().ContainSingle();
            document.Conversation.ToolTrace[0].ToolName.Should().Be("read_file");
            document.Conversation.ToolTrace[0].Result.Should().Be("class A {}");
            document.Conversation.ToolTrace[0].ResultClassification.Should().Be("Success");
            document.Conversation.ContextDebug.Should().NotBeNull();
            document.Metrics.Should().NotBeNull();
            document.Metrics!.TtftMs.Should().Be(300);
            document.SessionTelemetry.Should().NotBeNull();
            document.SessionTelemetry!.Value.GetProperty("turn_count").GetInt32().Should().Be(2);
            document.Source.WorkspaceName.Should().Be("Sample");
        }

        [Fact]
        public void Build_ForOlderTurn_StopsBeforeNextVisibleUserTurn()
        {
            var (tree, messages) = BuildConversation(includeSecondTurn: true);
            var apiHistory = new List<ChatApiMessage>
            {
                new() { Role = "user", Content = "Current user requirement: fix the error" },
                new() { Role = "assistant", Content = "Fixed first." },
                new() { Role = "user", Content = "Current user requirement: explain the fix" },
                new() { Role = "assistant", Content = "Explanation." },
            };

            var document = TurnEvaluationExporter.Build(new TurnEvaluationExporter.Request
            {
                Session = new ChatSession { Id = "session-2" },
                Tree = tree,
                VisibleMessages = messages,
                AssistantMessageIndex = 1,
                ApiHistory = apiHistory,
            });

            document.Turn.TurnIndex.Should().Be(1);
            document.Conversation.BeforeTurn.Should().BeEmpty();
            document.Conversation.TurnMessages.Should().HaveCount(2);
            document.Conversation.TurnMessages.Last().Content.Should().Be("Fixed first.");
            document.Metrics.Should().BeNull();
        }

        [Fact]
        public void SerializeAndWrite_UseStableSnakeCaseSchemaWithoutBom()
        {
            var (tree, messages) = BuildConversation();
            var document = TurnEvaluationExporter.Build(new TurnEvaluationExporter.Request
            {
                Session = new ChatSession { Id = "session-3" },
                Tree = tree,
                VisibleMessages = messages,
                AssistantMessageIndex = 1,
                ApiHistory = new List<ChatApiMessage>
                {
                    new() { Role = "user", Content = "fix the error" },
                    new() { Role = "assistant", Content = "Fixed." },
                },
            });

            string json = TurnEvaluationExporter.Serialize(document);
            json.Should().Contain("\"schema\": \"deepseek-vs.turn-evaluation/1\"");
            json.Should().Contain("\"turn_messages\"");
            json.Should().Contain("\"judge_contract\"");

            string path = Path.Combine(_dir, "turn.json");
            TurnEvaluationExporter.Write(document, path).Should().Be(path);
            byte[] bytes = File.ReadAllBytes(path);
            bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF });
            Encoding.UTF8.GetString(bytes).Should().Contain("\"schema\"");
        }

        private static (ConversationTree Tree, List<ChatMessage> Messages) BuildConversation(
            bool includeSecondTurn = false)
        {
            var tree = new ConversationTree();
            var messages = new List<ChatMessage>();

            var user = new ChatMessage
            {
                Role = "user",
                Content = "fix the error",
                Timestamp = new DateTime(2026, 1, 1, 10, 0, 0),
            };
            tree.AddChildMessage(user);
            messages.Add(user);

            var assistant = new ChatMessage
            {
                Role = "assistant",
                Content = "Fixed.",
                TimelineContent = "TOOL read_file",
                ReasoningContent = "I inspected the file.",
                Timestamp = new DateTime(2026, 1, 1, 10, 0, 1),
                AgentType = AgentType.Ask,
                ToolCallCount = 1,
            };
            tree.AddChildMessage(assistant);
            messages.Add(assistant);

            if (includeSecondTurn)
            {
                var user2 = new ChatMessage { Role = "user", Content = "explain the fix" };
                tree.AddChildMessage(user2);
                messages.Add(user2);

                var assistant2 = new ChatMessage { Role = "assistant", Content = "Explanation." };
                tree.AddChildMessage(assistant2);
                messages.Add(assistant2);
            }

            return (tree, messages);
        }
    }
}
