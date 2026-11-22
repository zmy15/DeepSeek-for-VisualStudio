using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.IO;
using System.Text.Json;

namespace BenchmarkRunner
{
    internal sealed class HarnessProfile
    {
        public string Id { get; private set; } = "baseline";

        public string Description { get; private set; } = "Ask agent with automatic handoffs.";

        public AgentType InitialAgent { get; private set; } = AgentType.Ask;

        public bool AutoFollowHandoffs { get; private set; } = true;

        public int MaxHandoffDepth { get; private set; } = 10;

        public TaskSize PreClassifiedTaskSize { get; private set; } = TaskSize.Small;

        public bool ThinkingEnabled { get; private set; }

        public string ThinkingEffort { get; private set; } = "high";

        public static HarnessProfile Load(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return new HarnessProfile();

            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Harness profile not found.", fullPath);

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fullPath));
            JsonElement root = document.RootElement;
            var profile = new HarnessProfile
            {
                Id = ReadString(root, "id", "baseline"),
                Description = ReadString(root, "description", string.Empty),
                InitialAgent = ParseEnum(
                    ReadString(root, "initial_agent", "Ask"),
                    AgentType.Ask,
                    "initial_agent"),
                AutoFollowHandoffs = ReadBoolean(root, "auto_follow_handoffs", true),
                MaxHandoffDepth = ReadInteger(root, "max_handoff_depth", 10),
                PreClassifiedTaskSize = ParseEnum(
                    ReadString(root, "pre_classified_task_size", "Small"),
                    TaskSize.Small,
                    "pre_classified_task_size"),
                ThinkingEnabled = ReadBoolean(root, "thinking_enabled", false),
                ThinkingEffort = ReadString(root, "thinking_effort", "high"),
            };

            if (profile.MaxHandoffDepth < 0 || profile.MaxHandoffDepth > 32)
                throw new InvalidOperationException(
                    "harness profile max_handoff_depth must be between 0 and 32.");

            return profile;
        }

        private static string ReadString(JsonElement root, string name, string fallback)
        {
            return root.TryGetProperty(name, out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!
                : fallback;
        }

        private static bool ReadBoolean(JsonElement root, string name, bool fallback)
        {
            return root.TryGetProperty(name, out JsonElement value)
                && (value.ValueKind == JsonValueKind.True
                    || value.ValueKind == JsonValueKind.False)
                ? value.GetBoolean()
                : fallback;
        }

        private static int ReadInteger(JsonElement root, string name, int fallback)
        {
            return root.TryGetProperty(name, out JsonElement value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out int result)
                ? result
                : fallback;
        }

        private static T ParseEnum<T>(string value, T fallback, string name)
            where T : struct, Enum
        {
            return Enum.TryParse(value, ignoreCase: true, out T parsed)
                ? parsed
                : throw new InvalidOperationException(
                    $"Unknown harness profile {name}: {value}");
        }
    }
}
