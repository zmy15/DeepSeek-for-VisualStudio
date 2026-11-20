using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.View;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.View
{
    public class EvaluationTelemetrySerializationTests
    {
        [Fact]
        public void LegacySessionTelemetry_UsesManifestUserTurnCountAndCumulativeUsage()
        {
            var method = typeof(DeepSeekChatControl).GetMethod(
                "SerializeSessionTelemetry",
                BindingFlags.NonPublic | BindingFlags.Static);
            method.Should().NotBeNull();

            var session = new ChatSession
            {
                CumulativePromptTokens = 1000,
                CumulativeCompletionTokens = 100,
                CumulativeCacheHitTokens = 750,
                CumulativeCacheMissTokens = 250,
            };
            var messages = new List<ChatMessage>
            {
                new() { Role = "user", Content = "one" },
                new() { Role = "assistant", Content = "one response" },
                new() { Role = "user", Content = "two" },
                new() { Role = "assistant", Content = "two response" },
            };

            string json = (string)method!.Invoke(null, new object[] { session, messages })!;
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            root.GetProperty("source").GetString().Should().Be("legacy_cumulative");
            root.GetProperty("turn_count").GetInt32().Should().Be(2);
            root.GetProperty("user_turn_count").GetInt32().Should().Be(2);
            root.GetProperty("input_tokens").GetInt64().Should().Be(1000);
            root.GetProperty("cache_hit_rate").GetDouble().Should().BeApproximately(0.75, 0.0001);
        }
    }
}
