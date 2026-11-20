using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepSeek_v4_for_VisualStudio.Services.Evaluation
{
    /// <summary>
    /// Builds one session archive containing a manifest and one self-contained JSON file per user turn.
    /// </summary>
    public static class SessionEvaluationExporter
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public sealed class Request
        {
            public ChatSession Session { get; set; } = new();
            public ConversationTree? Tree { get; set; }
            public IReadOnlyList<ChatMessage> VisibleMessages { get; set; } = Array.Empty<ChatMessage>();
            public IReadOnlyList<ChatApiMessage> ApiHistory { get; set; } = Array.Empty<ChatApiMessage>();
            public AgentTurnMetrics? LatestExecutionMetrics { get; set; }
            public string? SessionTelemetryJson { get; set; }
            public string? Model { get; set; }
            public string? SolutionPath { get; set; }
            public string? ContextDebugJson { get; set; }
        }

        public sealed class BuildResult
        {
            public SessionEvaluationManifest Manifest { get; set; } = new();
            public List<TurnFile> Turns { get; set; } = new();
        }

        public sealed class TurnFile
        {
            public string FileName { get; set; } = string.Empty;
            public TurnEvaluationExport Document { get; set; } = new();
        }

        public static BuildResult Build(Request request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var assistantIndices = FindCompletedAssistantIndices(request.VisibleMessages);
            if (assistantIndices.Count == 0)
                throw new InvalidOperationException("No completed assistant turns are available for export.");

            var result = new BuildResult
            {
                Manifest = new SessionEvaluationManifest
                {
                    Source = BuildSource(request),
                    SessionTelemetry = ParseJson(request.SessionTelemetryJson),
                },
            };

            int turnIndex = 0;
            foreach (int assistantIndex in assistantIndices)
            {
                turnIndex++;
                ChatMessage assistant = request.VisibleMessages[assistantIndex];
                ChatMessage? user = FindPreviousUser(request.VisibleMessages, assistantIndex);
                if (user == null) continue;

                string fileName = $"turns/turn-{turnIndex:000}.json";
                TurnEvaluationExport document = TurnEvaluationExporter.Build(
                    new TurnEvaluationExporter.Request
                    {
                        Session = request.Session,
                        Tree = request.Tree,
                        VisibleMessages = request.VisibleMessages,
                        AssistantMessageIndex = assistantIndex,
                        ApiHistory = request.ApiHistory,
                        LatestExecutionMetrics = assistantIndex == assistantIndices.Last()
                            ? request.LatestExecutionMetrics
                            : null,
                        SessionTelemetryJson = null,
                        Model = request.Model,
                        SolutionPath = request.SolutionPath,
                        ContextDebugJson = request.ContextDebugJson,
                    });

                result.Turns.Add(new TurnFile
                {
                    FileName = fileName,
                    Document = document,
                });
                result.Manifest.Turns.Add(new SessionEvaluationTurnEntry
                {
                    TurnIndex = turnIndex,
                    TurnId = assistant.TurnId,
                    File = fileName,
                    UserPrompt = Truncate(user.Content, 300),
                    AssistantPreview = Truncate(assistant.Content, 300),
                    AgentType = assistant.AgentType?.ToString(),
                    Incomplete = assistant.IsIncomplete,
                    ToolCallCount = assistant.ToolCallCount,
                    ChangedFileCount = assistant.ChangedFiles?.Count ?? 0,
                });
            }

            result.Manifest.TurnCount = result.Turns.Count;
            return result;
        }

        public static string WriteArchive(BuildResult result, string filePath)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("A target ZIP path is required.", nameof(filePath));

            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using var file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            WriteEntry(
                archive,
                "manifest.json",
                JsonSerializer.Serialize(result.Manifest, JsonOptions));

            foreach (TurnFile turn in result.Turns)
            {
                WriteEntry(
                    archive,
                    turn.FileName,
                    TurnEvaluationExporter.Serialize(turn.Document));
            }

            return filePath;
        }

        public static string BuildDefaultFileName(ChatSession session)
        {
            string sessionPart = string.IsNullOrWhiteSpace(session?.Id)
                ? "session"
                : session.Id.Substring(0, Math.Min(8, session.Id.Length));
            return $"judge-session_{DateTime.Now:yyyyMMdd_HHmmss}_{sessionPart}.zip";
        }

        private static EvaluationExportSource BuildSource(Request request)
        {
            string? workspaceName = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(request.SolutionPath))
                {
                    workspaceName = File.Exists(request.SolutionPath)
                        ? Path.GetFileNameWithoutExtension(request.SolutionPath)
                        : new DirectoryInfo(request.SolutionPath).Name;
                }
            }
            catch
            {
                workspaceName = null;
            }

            return new EvaluationExportSource
            {
                ExtensionVersion = typeof(SessionEvaluationExporter).Assembly.GetName().Version?.ToString(),
                SessionId = request.Session.Id,
                SessionTitle = request.Session.Title,
                Model = request.Model,
                WorkspaceName = workspaceName,
                SolutionPath = request.SolutionPath,
            };
        }

        private static List<int> FindCompletedAssistantIndices(IReadOnlyList<ChatMessage> messages)
        {
            var indices = new List<int>();
            bool hasUserSinceAssistant = false;
            for (int i = 0; i < messages.Count; i++)
            {
                ChatMessage message = messages[i];
                if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    hasUserSinceAssistant = true;
                }
                else if (hasUserSinceAssistant
                    && string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    && !message.IsStreaming)
                {
                    indices.Add(i);
                    hasUserSinceAssistant = false;
                }
            }
            return indices;
        }

        private static ChatMessage? FindPreviousUser(IReadOnlyList<ChatMessage> messages, int assistantIndex)
        {
            for (int i = Math.Min(assistantIndex - 1, messages.Count - 1); i >= 0; i--)
            {
                if (string.Equals(messages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
                    return messages[i];
            }
            return null;
        }

        private static void WriteEntry(ZipArchive archive, string name, string content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(content);
        }

        private static string Truncate(string? value, int max)
        {
            string text = value ?? string.Empty;
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }

        private static JsonElement? ParseJson(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                using JsonDocument document = JsonDocument.Parse(raw);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
