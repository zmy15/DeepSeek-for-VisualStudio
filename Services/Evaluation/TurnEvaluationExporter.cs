using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepSeek_v4_for_VisualStudio.Services.Evaluation
{
    /// <summary>
    /// Builds a stable, self-contained JSON document for one completed chat turn.
    /// The exporter is UI-independent so it can be covered by unit tests.
    /// </summary>
    public static class TurnEvaluationExporter
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
            public int AssistantMessageIndex { get; set; } = -1;
            public IReadOnlyList<ChatApiMessage> ApiHistory { get; set; } = Array.Empty<ChatApiMessage>();
            public AgentTurnMetrics? LatestExecutionMetrics { get; set; }
            public string? SessionTelemetryJson { get; set; }
            public string? Model { get; set; }
            public string? SolutionPath { get; set; }
            public string? ContextDebugJson { get; set; }
        }

        public static TurnEvaluationExport Build(Request request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.AssistantMessageIndex < 0
                || request.AssistantMessageIndex >= request.VisibleMessages.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request.AssistantMessageIndex),
                    "Assistant message index is outside the visible message list.");
            }

            ChatMessage assistant = request.VisibleMessages[request.AssistantMessageIndex];
            if (!string.Equals(assistant.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Evaluation export requires an assistant message.");
            }

            ConvNode? assistantNode = FindAssistantNode(request.Tree, assistant);
            ConvNode? userNode = request.Tree == null
                ? null
                : request.Tree.FindNearestUserAncestor(assistantNode ?? new ConvNode
                {
                    Id = assistant.NodeId,
                    Message = assistant,
                });

            ChatMessage? user = userNode?.Message
                ?? FindPreviousUser(request.VisibleMessages, request.AssistantMessageIndex);
            if (user == null)
            {
                throw new InvalidOperationException("No user message belongs to this assistant turn.");
            }

            int turnIndex = CountUserTurns(request.VisibleMessages, request.AssistantMessageIndex);
            List<ChatApiMessage> fullHistory = request.ApiHistory?.ToList() ?? new List<ChatApiMessage>();
            int turnStart = FindTurnStart(fullHistory, user.Content);
            int turnEnd = FindTurnEnd(fullHistory, request.VisibleMessages, user, turnStart);

            var document = new TurnEvaluationExport
            {
                Source = new EvaluationExportSource
                {
                    ExtensionVersion = typeof(TurnEvaluationExporter).Assembly.GetName().Version?.ToString(),
                    SessionId = request.Session.Id,
                    SessionTitle = request.Session.Title,
                    Model = request.Model,
                    WorkspaceName = GetWorkspaceName(request.SolutionPath),
                    SolutionPath = request.SolutionPath,
                },
                Turn = new EvaluationTurn
                {
                    TurnIndex = turnIndex,
                    TurnId = assistant.TurnId,
                    UserNodeId = user.NodeId,
                    AssistantNodeId = assistant.NodeId,
                    User = BuildUser(user),
                    Assistant = BuildAssistant(assistant),
                    Branch = new EvaluationBranch
                    {
                        SiblingIndex = assistant.SiblingIndex,
                        SiblingCount = assistant.SiblingCount,
                        ForkReason = assistant.ForkReason,
                    },
                },
                Conversation = new EvaluationConversation
                {
                    FullApiHistory = fullHistory.Select(ToEvaluationApiMessage).ToList(),
                    BeforeTurn = fullHistory.Take(turnStart).Select(ToEvaluationApiMessage).ToList(),
                    TurnMessages = fullHistory
                        .Skip(turnStart)
                        .Take(Math.Max(0, turnEnd - turnStart))
                        .Select(ToEvaluationApiMessage)
                        .ToList(),
                    ToolTrace = BuildToolTrace(fullHistory, turnStart, turnEnd),
                    ContextDebug = ParseJson(request.ContextDebugJson),
                },
                Metrics = ToMetrics(request.LatestExecutionMetrics),
                SessionTelemetry = ParseJson(request.SessionTelemetryJson),
            };

            return document;
        }

        public static string Serialize(TurnEvaluationExport document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            return JsonSerializer.Serialize(document, JsonOptions);
        }

        public static string Write(TurnEvaluationExport document, string filePath)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("A target file path is required.", nameof(filePath));

            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(filePath, Serialize(document), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return filePath;
        }

        public static string BuildDefaultFileName(ChatSession session, int turnIndex)
        {
            string sessionPart = string.IsNullOrWhiteSpace(session?.Id)
                ? "session"
                : session.Id.Substring(0, Math.Min(8, session.Id.Length));
            return $"judge-turn_{DateTime.Now:yyyyMMdd_HHmmss}_{sessionPart}_t{turnIndex:000}.json";
        }

        private static EvaluationUserMessage BuildUser(ChatMessage message)
        {
            return new EvaluationUserMessage
            {
                Content = message.Content ?? string.Empty,
                Timestamp = message.Timestamp,
                AttachedFiles = (message.AttachedFiles ?? new List<FileParseResult>())
                    .Select(file => new EvaluationAttachment
                    {
                        FileName = file.FileName,
                        Extension = file.FileExtension,
                        Content = file.Content,
                        Success = file.Success,
                        Truncated = file.Truncated,
                        Error = file.Error,
                    })
                    .ToList(),
                AttachedImageNames = (message.AttachedImageFileNames ?? new List<string>()).ToList(),
                ChangedFiles = ToFileChanges(message.ChangedFiles),
            };
        }

        private static EvaluationAssistantMessage BuildAssistant(ChatMessage message)
        {
            return new EvaluationAssistantMessage
            {
                Content = message.Content ?? string.Empty,
                TimelineContent = message.TimelineContent ?? string.Empty,
                ReasoningContent = message.ReasoningContent ?? string.Empty,
                Timestamp = message.Timestamp,
                AgentType = message.AgentType?.ToString(),
                Incomplete = message.IsIncomplete,
                ToolCallCount = message.ToolCallCount,
                ChangedFiles = ToFileChanges(message.ChangedFiles),
                Plan = ParseJson(message.PlanJson),
                PendingHandoff = ParseJson(message.HandoffJson),
            };
        }

        private static List<EvaluationFileChange> ToFileChanges(IEnumerable<FileChangeSummary>? files)
        {
            if (files == null) return new List<EvaluationFileChange>();
            return files
                .Where(file => file != null)
                .Select(file => new EvaluationFileChange
                {
                    Path = file.FilePath ?? string.Empty,
                    LinesAdded = file.LinesAdded,
                    LinesRemoved = file.LinesRemoved,
                    Description = file.BriefDescription,
                })
                .ToList();
        }

        private static EvaluationApiMessage ToEvaluationApiMessage(ChatApiMessage message)
        {
            return new EvaluationApiMessage
            {
                Role = message.Role,
                Content = message.Content,
                ReasoningContent = message.ReasoningContent,
                ToolCallId = message.ToolCallId,
                Name = message.Name,
                ToolCalls = (message.ToolCalls ?? new List<ToolCall>())
                    .Select(call => new EvaluationToolCall
                    {
                        Id = call.Id,
                        Type = call.Type,
                        Name = call.Function?.Name ?? string.Empty,
                        Arguments = call.Function?.Arguments ?? string.Empty,
                    })
                    .ToList(),
                MultimodalParts = (message.MultimodalContent ?? new List<ChatContentPart>())
                    .Select(part => new EvaluationContentPart
                    {
                        Type = part.Type,
                        Text = part.Text,
                        FileName = part.File?.Filename,
                        FileId = part.File?.FileId,
                        ImageUrlPresent = part.ImageUrl != null,
                    })
                    .ToList(),
            };
        }

        private static List<EvaluationToolTraceItem> BuildToolTrace(
            IReadOnlyList<ChatApiMessage> history,
            int start,
            int end)
        {
            var toolResults = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = start; i < end && i < history.Count; i++)
            {
                var message = history[i];
                if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(message.ToolCallId))
                {
                    toolResults[message.ToolCallId!] = message.Content ?? string.Empty;
                }
            }

            var trace = new List<EvaluationToolTraceItem>();
            for (int i = start; i < end && i < history.Count; i++)
            {
                var message = history[i];
                if (!string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    || message.ToolCalls == null)
                {
                    continue;
                }

                foreach (var call in message.ToolCalls)
                {
                    string result = toolResults.TryGetValue(call.Id, out string? value) ? value : string.Empty;
                    trace.Add(new EvaluationToolTraceItem
                    {
                        CallId = call.Id,
                        ToolName = call.Function?.Name ?? string.Empty,
                        Arguments = call.Function?.Arguments ?? string.Empty,
                        Result = toolResults.ContainsKey(call.Id) ? result : null,
                        ResultClassification = toolResults.ContainsKey(call.Id)
                            ? ToolExecutionOutcome.Classify(result).ToString()
                            : "Missing",
                    });
                }
            }
            return trace;
        }

        private static EvaluationMetrics? ToMetrics(AgentTurnMetrics? metrics)
        {
            if (metrics == null) return null;
            return new EvaluationMetrics
            {
                Turn = metrics.Turn,
                TtftMs = metrics.TtftMs,
                DurationMs = metrics.DurationMs,
                InputTokens = metrics.InputTokens,
                OutputTokens = metrics.OutputTokens,
                CacheHitTokens = metrics.CacheHitTokens,
                CacheMissTokens = metrics.CacheMissTokens,
                DecodeTokensPerSecond = metrics.DecodeTokensPerSecond,
                EndToEndTokensPerSecond = metrics.EndToEndTokensPerSecond,
                StreamRetries = metrics.StreamRetries,
                TerminatedReason = metrics.TerminatedReason,
            };
        }

        private static ConvNode? FindAssistantNode(ConversationTree? tree, ChatMessage assistant)
        {
            if (tree == null) return null;
            if (!string.IsNullOrEmpty(assistant.NodeId))
            {
                ConvNode? byId = tree.FindNode(assistant.NodeId);
                if (byId != null) return byId;
            }
            return tree.GetActivePath().FirstOrDefault(node => ReferenceEquals(node.Message, assistant));
        }

        private static ChatMessage? FindPreviousUser(
            IReadOnlyList<ChatMessage> messages,
            int assistantIndex)
        {
            for (int i = Math.Min(assistantIndex - 1, messages.Count - 1); i >= 0; i--)
            {
                if (string.Equals(messages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
                    return messages[i];
            }
            return null;
        }

        private static int CountUserTurns(IReadOnlyList<ChatMessage> messages, int assistantIndex)
        {
            int count = 0;
            for (int i = 0; i <= assistantIndex && i < messages.Count; i++)
            {
                if (string.Equals(messages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            return count;
        }

        private static int FindTurnStart(IReadOnlyList<ChatApiMessage> history, string? userContent)
        {
            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (string.Equals(history[i].Role, "user", StringComparison.OrdinalIgnoreCase)
                    && ContentMatches(history[i].Content, userContent))
                {
                    return i;
                }
            }

            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (string.Equals(history[i].Role, "user", StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return 0;
        }

        private static int FindTurnEnd(
            IReadOnlyList<ChatApiMessage> history,
            IReadOnlyList<ChatMessage> visibleMessages,
            ChatMessage currentUser,
            int turnStart)
        {
            int currentUserIndex = -1;
            for (int i = 0; i < visibleMessages.Count; i++)
            {
                if (ReferenceEquals(visibleMessages[i], currentUser))
                {
                    currentUserIndex = i;
                    break;
                }
            }

            string? nextUserContent = null;
            if (currentUserIndex >= 0)
            {
                for (int i = currentUserIndex + 1; i < visibleMessages.Count; i++)
                {
                    if (string.Equals(visibleMessages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
                    {
                        nextUserContent = visibleMessages[i].Content;
                        break;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(nextUserContent))
            {
                for (int i = turnStart + 1; i < history.Count; i++)
                {
                    if (string.Equals(history[i].Role, "user", StringComparison.OrdinalIgnoreCase)
                        && ContentMatches(history[i].Content, nextUserContent))
                    {
                        return i;
                    }
                }
            }

            return history.Count;
        }

        private static bool ContentMatches(string? apiContent, string? visibleContent)
        {
            if (string.IsNullOrWhiteSpace(visibleContent)) return false;
            if (string.IsNullOrEmpty(apiContent)) return false;
            return apiContent.IndexOf(visibleContent.Trim(), StringComparison.Ordinal) >= 0;
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

        private static string? GetWorkspaceName(string? solutionPath)
        {
            if (string.IsNullOrWhiteSpace(solutionPath)) return null;
            try
            {
                if (File.Exists(solutionPath))
                    return Path.GetFileNameWithoutExtension(solutionPath);
                string extension = Path.GetExtension(solutionPath);
                if (extension.StartsWith(".sln", StringComparison.OrdinalIgnoreCase))
                    return Path.GetFileNameWithoutExtension(solutionPath);
                return new DirectoryInfo(solutionPath).Name;
            }
            catch
            {
                return null;
            }
        }
    }
}
