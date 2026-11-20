using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.Evaluation;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services.Evaluation
{
    public class SessionEvaluationExporterTests : IDisposable
    {
        private readonly string _dir;

        public SessionEvaluationExporterTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ds-session-eval-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void BuildAndWriteArchive_CreatesManifestAndOneFilePerTurn()
        {
            var (tree, messages) = BuildConversation();
            var apiHistory = new List<ChatApiMessage>
            {
                new() { Role = "user", Content = "first request" },
                new() { Role = "assistant", Content = "first response" },
                new() { Role = "user", Content = "second request" },
                new() { Role = "assistant", Content = "second response" },
            };

            var built = SessionEvaluationExporter.Build(new SessionEvaluationExporter.Request
            {
                Session = new ChatSession { Id = "session-123", Title = "Session" },
                Tree = tree,
                VisibleMessages = messages,
                ApiHistory = apiHistory,
                LatestExecutionMetrics = new AgentTurnMetrics
                {
                    Turn = 1,
                    InputTokens = 100,
                    OutputTokens = 20,
                },
                SessionTelemetryJson = "{\"turn_count\":2,\"input_tokens\":300}",
                Model = "deepseek-flash",
                SolutionPath = @"C:\work\Sample.sln",
            });

            built.Manifest.Schema.Should().Be("deepseek-vs.session-evaluation/1");
            built.Manifest.TurnCount.Should().Be(2);
            built.Turns.Should().HaveCount(2);
            built.Turns[0].FileName.Should().Be("turns/turn-001.json");
            built.Turns[1].FileName.Should().Be("turns/turn-002.json");

            string archivePath = Path.Combine(_dir, "session.zip");
            SessionEvaluationExporter.WriteArchive(built, archivePath).Should().Be(archivePath);

            using var archive = ZipFile.OpenRead(archivePath);
            archive.Entries.Select(entry => entry.FullName).Should().BeEquivalentTo(
                "manifest.json",
                "turns/turn-001.json",
                "turns/turn-002.json");

            using var reader = new StreamReader(
                archive.GetEntry("manifest.json")!.Open());
            using JsonDocument manifest = JsonDocument.Parse(reader.ReadToEnd());
            manifest.RootElement.GetProperty("turn_count").GetInt32().Should().Be(2);
            manifest.RootElement.GetProperty("session_telemetry")
                .GetProperty("turn_count").GetInt32().Should().Be(2);

            using var turnReader = new StreamReader(
                archive.GetEntry("turns/turn-002.json")!.Open());
            using JsonDocument turn = JsonDocument.Parse(turnReader.ReadToEnd());
            turn.RootElement.GetProperty("turn")
                .GetProperty("user").GetProperty("content")
                .GetString().Should().Be("second request");
        }

        private static (ConversationTree Tree, List<ChatMessage> Messages) BuildConversation()
        {
            var tree = new ConversationTree();
            var messages = new List<ChatMessage>();

            var user1 = new ChatMessage { Role = "user", Content = "first request" };
            tree.AddChildMessage(user1);
            messages.Add(user1);
            var assistant1 = new ChatMessage { Role = "assistant", Content = "first response" };
            tree.AddChildMessage(assistant1);
            messages.Add(assistant1);

            var user2 = new ChatMessage { Role = "user", Content = "second request" };
            tree.AddChildMessage(user2);
            messages.Add(user2);
            var assistant2 = new ChatMessage { Role = "assistant", Content = "second response" };
            tree.AddChildMessage(assistant2);
            messages.Add(assistant2);

            return (tree, messages);
        }
    }
}
