using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BenchmarkRunner
{
    internal sealed class UserSimulationConfig
    {
        public bool Enabled { get; private set; }

        public string Objective { get; private set; } = string.Empty;

        public string Persona { get; private set; } = string.Empty;

        public int MaxTurns { get; private set; } = 4;

        public static UserSimulationConfig FromCase(JsonElement caseElement, int? maxTurnsOverride)
        {
            if (!caseElement.TryGetProperty("user_simulation", out JsonElement config)
                || config.ValueKind != JsonValueKind.Object)
            {
                return new UserSimulationConfig();
            }

            int maxTurns = ReadInteger(config, "max_turns", 4);
            if (maxTurnsOverride.HasValue)
                maxTurns = maxTurnsOverride.Value;
            if (maxTurns < 2 || maxTurns > 12)
                throw new InvalidOperationException(
                    "user_simulation.max_turns must be between 2 and 12.");

            string objective = ReadString(config, "objective");
            if (string.IsNullOrWhiteSpace(objective))
                throw new InvalidOperationException(
                    "user_simulation.objective is required.");

            return new UserSimulationConfig
            {
                Enabled = true,
                Objective = objective,
                Persona = ReadString(config, "persona"),
                MaxTurns = maxTurns,
            };
        }

        private static string ReadString(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }

        private static int ReadInteger(JsonElement root, string name, int fallback)
        {
            return root.TryGetProperty(name, out JsonElement value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out int parsed)
                ? parsed
                : fallback;
        }
    }

    internal sealed class UserSimulator : IDisposable
    {
        private readonly DeepSeekApiService _apiService;
        private readonly string _model;

        public UserSimulator(string apiKey, string model, string baseUrl)
        {
            _model = model;
            _apiService = new DeepSeekApiService(
                apiKey,
                model,
                requestTimeoutSeconds: 120,
                baseUrl: baseUrl);
            _apiService.ConfigureThinking(false);
        }

        public long PromptTokens => _apiService.TotalPromptTokens;

        public long CompletionTokens => _apiService.TotalCompletionTokens;

        public long CacheHitTokens => _apiService.TotalCacheHitTokens;

        public long CacheMissTokens => _apiService.TotalCacheMissTokens;

        public double CacheHitRate => _apiService.TotalCacheHitRate;

        public string Model => _model;

        public async Task<string?> NextMessageAsync(
            UserSimulationConfig config,
            IReadOnlyList<(string User, string Assistant)> history,
            CancellationToken cancellationToken)
        {
            string conversation = history.Count == 0
                ? "(no turns yet)"
                : string.Join(
                    "\n\n",
                    history.Select((turn, index) =>
                        $"Turn {index + 1}\nUser: {turn.User}\nAgent: {turn.Assistant}"));
            var messages = new[]
            {
                new ChatApiMessage
                {
                    Role = "system",
                    Content =
                        "You simulate a software developer using a coding agent. "
                        + "Stay in the user role. Never solve the task, reveal hidden tests, "
                        + "or mention an evaluation rubric. Ask for clarification when needed, "
                        + "challenge unsupported claims, and converge toward the stated objective. "
                        + "Return only compact JSON: {\"done\":boolean,\"message\":string}. "
                        + "Set done=true only when the task is fully handled or the agent is "
                        + "blocked in a way the user cannot resolve.",
                },
                new ChatApiMessage
                {
                    Role = "user",
                    Content =
                        "Objective:\n" + config.Objective
                        + "\n\nPersona:\n"
                        + (string.IsNullOrWhiteSpace(config.Persona)
                            ? "A pragmatic developer who gives feedback when the agent asks."
                            : config.Persona)
                        + "\n\nConversation so far:\n" + conversation
                        + "\n\nDecide the single next user message. "
                        + "If another turn is needed, make it specific and natural.",
                },
            };

            string raw = await _apiService.CompleteAsync(messages, cancellationToken);
            return ParseNextMessage(raw);
        }

        public async Task<string?> AnswerQuestionsAsync(
            UserSimulationConfig config,
            IReadOnlyList<AgentQuestion> questions,
            CancellationToken cancellationToken)
        {
            string questionJson = JsonSerializer.Serialize(
                questions.Select(question => new
                {
                    header = question.Header,
                    question = question.Question,
                    options = question.Options?.Select(option => option.Label).ToArray(),
                    multiSelect = question.MultiSelect,
                    allowFreeformInput = question.AllowFreeformInput,
                }));
            var messages = new[]
            {
                new ChatApiMessage
                {
                    Role = "system",
                    Content =
                        "You answer clarification questions as the end user. "
                        + "Do not solve the coding task or reveal evaluation details. "
                        + "Return only a JSON array with exactly one object per question. "
                        + "Each object must be "
                        + "{\"selectedOptions\":string[],\"freeText\":string,\"skipped\":boolean}. "
                        + "Use only option labels supplied by the Agent.",
                },
                new ChatApiMessage
                {
                    Role = "user",
                    Content =
                        "Objective:\n" + config.Objective
                        + "\n\nPersona:\n"
                        + (string.IsNullOrWhiteSpace(config.Persona)
                            ? "A pragmatic developer."
                            : config.Persona)
                        + "\n\nQuestions:\n" + questionJson,
                },
            };
            return await _apiService.CompleteAsync(messages, cancellationToken);
        }

        public static string? ParseNextMessage(string raw)
        {
            string text = (raw ?? string.Empty).Trim();
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                int firstNewline = text.IndexOf('\n');
                int lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (firstNewline >= 0 && lastFence > firstNewline)
                    text = text.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
            }

            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
                text = text.Substring(start, end - start + 1);

            try
            {
                using JsonDocument document = JsonDocument.Parse(text);
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("done", out JsonElement done)
                    && done.ValueKind == JsonValueKind.True)
                    return null;
                if (root.TryGetProperty("message", out JsonElement message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    string value = message.GetString()?.Trim() ?? string.Empty;
                    return value.Length == 0 ? null : value;
                }
            }
            catch (JsonException)
            {
                // Fall through to a plain-text recovery path.
            }

            return text.Length == 0 ? null : text;
        }

        public void Dispose()
        {
            _apiService.Dispose();
        }
    }
}
