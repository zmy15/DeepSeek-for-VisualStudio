using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace BenchmarkRunner
{
    /// <summary>
    /// Handles Agent clarification requests without fabricating a user answer.
    /// Fixed-script cases skip unanswered questions; simulated-user cases ask
    /// the simulator to answer from the public objective.
    /// </summary>
    internal sealed class AgentQuestionPolicy
    {
        private readonly UserSimulationConfig _simulation;
        private readonly UserSimulator? _simulator;
        private int _turnQuestionRequests;
        private int _turnSkippedAnswers;
        private int _turnSimulatedAnswers;

        public AgentQuestionPolicy(
            UserSimulationConfig simulation,
            UserSimulator? simulator)
        {
            _simulation = simulation;
            _simulator = simulator;
        }

        public bool ShouldBlockHandoff => _turnSkippedAnswers > 0;

        public void BeginTurn()
        {
            _turnQuestionRequests = 0;
            _turnSkippedAnswers = 0;
            _turnSimulatedAnswers = 0;
        }

        public string Answer(AgentQuestionRequest request)
        {
            _turnQuestionRequests++;
            if (_simulation.Enabled && _simulator != null)
            {
                try
                {
                    string? simulated = _simulator
                        .AnswerQuestionsAsync(
                            _simulation,
                            request.Questions,
                            System.Threading.CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    if (IsValidAnswerArray(simulated, request.Questions.Count))
                    {
                        _turnSimulatedAnswers++;
                        return simulated!;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"  simulated question failed: {ex.Message}");
                }
            }

            _turnSkippedAnswers++;
            return BuildSkippedAnswers(request.Questions.Count);
        }

        public Dictionary<string, object?> TurnSummary()
        {
            return new Dictionary<string, object?>
            {
                ["mode"] = _simulation.Enabled
                    ? "user_simulator"
                    : "skip_unanswered",
                ["requests"] = _turnQuestionRequests,
                ["simulated_answers"] = _turnSimulatedAnswers,
                ["skipped_answers"] = _turnSkippedAnswers,
                ["handoff_blocked"] = ShouldBlockHandoff,
            };
        }

        private static string BuildSkippedAnswers(int count)
        {
            var answers = Enumerable.Range(0, Math.Max(0, count))
                .Select(_ => new Dictionary<string, object?>
                {
                    ["selectedOptions"] = Array.Empty<string>(),
                    ["freeText"] = string.Empty,
                    ["skipped"] = true,
                })
                .ToArray();
            return JsonSerializer.Serialize(answers);
        }

        private static bool IsValidAnswerArray(string? json, int expectedCount)
        {
            if (string.IsNullOrWhiteSpace(json))
                return false;
            try
            {
                using JsonDocument document = JsonDocument.Parse(json!);
                return document.RootElement.ValueKind == JsonValueKind.Array
                    && document.RootElement.GetArrayLength() == expectedCount;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
