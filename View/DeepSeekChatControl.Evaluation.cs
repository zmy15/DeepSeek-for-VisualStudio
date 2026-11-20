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
                            SessionTelemetryJson = _latestCompletedTelemetryJson,
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
                SessionTelemetryJson = null,
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
    }
}
