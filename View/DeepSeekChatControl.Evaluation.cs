using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.Evaluation;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// Export the complete active session as a ZIP of per-turn Judge records.
    /// </summary>
    public partial class DeepSeekChatControl
    {
        private bool IsEvaluationExportEnabled()
            => _options?.EnableEvaluationExport == true;

        private void ExportSessionEvaluation(string sessionId)
        {
            try
            {
                if (!IsEvaluationExportEnabled())
                {
                    StatusLabel.Text = LocalizationService.Instance["evaluation.exportDisabled"];
                    return;
                }

                SessionEvaluationExporter.Request? request = null;

                lock (_lock)
                {
                    if (_activeSession != null
                        && string.Equals(_activeSession.Id, sessionId, StringComparison.Ordinal))
                    {
                        if (!_messages.Any(message =>
                                string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                                && !message.IsStreaming))
                        {
                            StatusLabel.Text = LocalizationService.Instance["evaluation.sessionNotReady"];
                            return;
                        }

                        request = new SessionEvaluationExporter.Request
                        {
                            Session = _activeSession,
                            Tree = _tree,
                            VisibleMessages = new List<ChatMessage>(_messages),
                            ApiHistory = _contextManager.GetFullContext(),
                            LatestExecutionMetrics = _latestTurnMetrics,
                            SessionTelemetryJson = SerializeSessionTelemetry(
                                _activeSession,
                                _messages),
                            Model = GetEffectiveModel(),
                            SolutionPath = _solutionPath,
                            ContextDebugJson = BuildContextDebugJson(),
                        };
                    }
                }

                if (request == null)
                {
                    ChatSession? storedSession = _sessionsContainer?.Sessions
                        .FirstOrDefault(item => string.Equals(item.Id, sessionId, StringComparison.Ordinal));
                    if (storedSession == null)
                    {
                        StatusLabel.Text = LocalizationService.Instance["evaluation.exportFailed"];
                        return;
                    }
                    request = BuildStoredSessionExportRequest(storedSession);
                }

                SessionEvaluationExporter.BuildResult archive =
                    SessionEvaluationExporter.Build(request);

                Directory.CreateDirectory(StoragePaths.EvaluationsDirectory);
                var dialog = new SaveFileDialog
                {
                    AddExtension = true,
                    DefaultExt = ".zip",
                    Filter = "Judge session ZIP (*.zip)|*.zip|All files (*.*)|*.*",
                    FileName = SessionEvaluationExporter.BuildDefaultFileName(request.Session),
                    InitialDirectory = StoragePaths.EvaluationsDirectory,
                    Title = LocalizationService.Instance["evaluation.saveDialogTitle"],
                    OverwritePrompt = true,
                };

                if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                    return;

                string path = SessionEvaluationExporter.WriteArchive(archive, dialog.FileName);
                StatusLabel.Text = string.Format(
                    LocalizationService.Instance["evaluation.exportSucceeded"],
                    path);
                Logger.Info($"[JudgeExport] Session exported: {path} ({archive.Manifest.TurnCount} turns)");
            }
            catch (Exception ex)
            {
                StatusLabel.Text = string.Format(
                    LocalizationService.Instance["evaluation.exportFailedWithMessage"],
                    ex.Message);
                Logger.Error($"[JudgeExport] Export failed: {ex.Message}", ex);
            }
        }

        private SessionEvaluationExporter.Request BuildStoredSessionExportRequest(
            ChatSession session)
        {
            ConversationTree? tree = null;
            List<ChatMessage> visibleMessages;

            if (!string.IsNullOrWhiteSpace(session.TreeDataJson))
            {
                try
                {
                    TreePersistenceData? data = JsonSerializer.Deserialize<TreePersistenceData>(
                        session.TreeDataJson!,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (data != null)
                    {
                        tree = ConversationTree.Deserialize(data);
                        visibleMessages = tree.GetActiveMessages();
                    }
                    else
                    {
                        visibleMessages = BuildVisibleMessagesFromApiHistory(session.ApiHistory);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[JudgeExport] 历史会话树解析失败，回退 ApiHistory: {ex.Message}");
                    visibleMessages = BuildVisibleMessagesFromApiHistory(session.ApiHistory);
                }
            }
            else
            {
                visibleMessages = BuildVisibleMessagesFromApiHistory(session.ApiHistory);
            }

            return new SessionEvaluationExporter.Request
            {
                Session = session,
                Tree = tree,
                VisibleMessages = visibleMessages,
                ApiHistory = session.ApiHistory?.ToList() ?? new List<ChatApiMessage>(),
                LatestExecutionMetrics = null,
                SessionTelemetryJson = SerializeSessionTelemetry(session, visibleMessages),
                Model = GetEffectiveModel(),
                SolutionPath = _solutionPath,
                ContextDebugJson = null,
            };
        }

        private static List<ChatMessage> BuildVisibleMessagesFromApiHistory(
            IEnumerable<ChatApiMessage>? history)
        {
            return (history ?? Enumerable.Empty<ChatApiMessage>())
                .Where(message =>
                    string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                .Select(message => new ChatMessage
                {
                    Role = message.Role,
                    Content = message.Content ?? string.Empty,
                    ReasoningContent = message.ReasoningContent ?? string.Empty,
                })
                .ToList();
        }

        private static string? SerializeSessionTelemetry(
            ChatSession? session,
            IReadOnlyList<ChatMessage> visibleMessages)
        {
            if (session == null) return null;
            var options = new JsonSerializerOptions
            {
                DefaultIgnoreCondition =
                    System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            };
            if (session.EvaluationTelemetry != null)
            {
                return JsonSerializer.Serialize(
                    session.EvaluationTelemetry,
                    options);
            }

            int userTurnCount = visibleMessages.Count(message =>
                string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));
            long cacheHit = Math.Max(0, session.CumulativeCacheHitTokens);
            long cacheMiss = Math.Max(0, session.CumulativeCacheMissTokens);
            long cacheTotal = cacheHit + cacheMiss;
            if (userTurnCount == 0
                && session.CumulativePromptTokens == 0
                && session.CumulativeCompletionTokens == 0
                && cacheTotal == 0)
            {
                return null;
            }

            var legacy = new
            {
                schema = "deepseek-vs.session-telemetry/1",
                source = "legacy_cumulative",
                turn_count = userTurnCount,
                user_turn_count = userTurnCount,
                model_turn_count = (int?)null,
                tool_call_count = (int?)null,
                input_tokens = session.CumulativePromptTokens,
                output_tokens = session.CumulativeCompletionTokens,
                cache_hit_tokens = cacheHit,
                cache_miss_tokens = cacheMiss,
                cache_hit_rate = cacheTotal > 0 ? (double?)cacheHit / cacheTotal : null,
                duration_ms = (long?)null,
                first_turn_ttft_ms = (long?)null,
                stream_retries = (int?)null,
                runs = Array.Empty<object>(),
            };
            return JsonSerializer.Serialize(
                legacy,
                options);
        }
    }
}
