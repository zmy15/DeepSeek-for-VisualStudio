using DeepSeek_v4_for_VisualStudio.Models;
using System;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Models
{
    public class SessionEvaluationTelemetryTests
    {
        [Fact]
        public void TryAddRun_AggregatesAllUserTurnsAndPreventsDuplicateSessionIds()
        {
            var aggregate = new SessionEvaluationTelemetry();
            var first = BuildRun(
                "run-1",
                inputTokens: 100,
                outputTokens: 10,
                cacheHit: 80,
                cacheMiss: 20,
                durationMs: 500,
                toolCalls: 2,
                ttft: 120);
            var second = BuildRun(
                "run-2",
                inputTokens: 200,
                outputTokens: 30,
                cacheHit: 150,
                cacheMiss: 50,
                durationMs: 900,
                toolCalls: 3,
                ttft: 180);

            aggregate.TryAddRun(first).Should().BeTrue();
            aggregate.TryAddRun(second).Should().BeTrue();
            aggregate.TryAddRun(second).Should().BeFalse();

            aggregate.TurnCount.Should().Be(2);
            aggregate.UserTurnCount.Should().Be(2);
            aggregate.ModelTurnCount.Should().Be(4);
            aggregate.ToolCallCount.Should().Be(5);
            aggregate.InputTokens.Should().Be(300);
            aggregate.OutputTokens.Should().Be(40);
            aggregate.CacheHitRate.Should().BeApproximately(230.0 / 300.0, 0.0001);
            aggregate.DurationMs.Should().Be(1400);
            aggregate.FirstTurnTtftMs.Should().Be(120);
            aggregate.StreamRetries.Should().Be(4);
            aggregate.Runs.Should().HaveCount(2);
        }

        private static AgentSessionMetrics BuildRun(
            string sessionId,
            int inputTokens,
            int outputTokens,
            int cacheHit,
            int cacheMiss,
            long durationMs,
            int toolCalls,
            long ttft)
        {
            var run = new AgentSessionMetrics
            {
                SessionId = sessionId,
                DurationMs = durationMs,
                Result = AgentSessionResult.Success,
            };
            run.Turns.Add(new AgentTurnMetrics
            {
                Turn = 1,
                TtftMs = ttft,
                DurationMs = durationMs / 2,
                InputTokens = inputTokens / 2,
                OutputTokens = outputTokens / 2,
                CacheHitTokens = cacheHit / 2,
                CacheMissTokens = cacheMiss / 2,
                StreamRetries = 1,
            });
            run.Turns.Add(new AgentTurnMetrics
            {
                Turn = 2,
                TtftMs = ttft + 10,
                DurationMs = durationMs / 2,
                InputTokens = inputTokens - inputTokens / 2,
                OutputTokens = outputTokens - outputTokens / 2,
                CacheHitTokens = cacheHit - cacheHit / 2,
                CacheMissTokens = cacheMiss - cacheMiss / 2,
                StreamRetries = 1,
            });

            for (int i = 0; i < toolCalls; i++)
            {
                run.Turns[i % 2].Tools.Add(new ToolCallMetric
                {
                    Turn = i % 2 + 1,
                    ToolName = "read_file",
                    Success = true,
                });
            }
            return run;
        }
    }
}
