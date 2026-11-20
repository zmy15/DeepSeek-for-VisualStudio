using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.Evaluation;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// Export one visible user/assistant turn as a self-contained LLM-as-a-Judge record.
    /// </summary>
    public partial class DeepSeekChatControl
    {
        private void ExportTurnEvaluation(int assistantMessageIndex)
        {
            try
            {
                ChatSession? session;
                ConversationTree? tree;
                List<ChatMessage> visibleMessages;
                List<ChatApiMessage> apiHistory;
                AgentTurnMetrics? metrics;
                string? sessionTelemetry;
                string? contextDebug;
                int lastAssistantIndex;

                lock (_lock)
                {
                    if (assistantMessageIndex < 0 || assistantMessageIndex >= _messages.Count)
                        return;

                    ChatMessage assistant = _messages[assistantMessageIndex];
                    if (!string.Equals(assistant.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                        || assistant.IsStreaming)
                    {
                        StatusLabel.Text = LocalizationService.Instance["evaluation.turnNotReady"];
                        return;
                    }

                    session = _activeSession;
                    tree = _tree;
                    visibleMessages = new List<ChatMessage>(_messages);
                    apiHistory = _contextManager.GetFullContext();
                    lastAssistantIndex = -1;
                    for (int i = visibleMessages.Count - 1; i >= 0; i--)
                    {
                        if (string.Equals(visibleMessages[i].Role, "assistant", StringComparison.OrdinalIgnoreCase))
                        {
                            lastAssistantIndex = i;
                            break;
                        }
                    }
                    metrics = assistantMessageIndex == lastAssistantIndex ? _latestTurnMetrics : null;
                    sessionTelemetry = assistantMessageIndex == lastAssistantIndex
                        ? _latestCompletedTelemetryJson
                        : null;
                    contextDebug = BuildContextDebugJson();
                }

                if (session == null)
                {
                    StatusLabel.Text = LocalizationService.Instance["evaluation.exportFailed"];
                    return;
                }

                int turnIndex = 0;
                for (int i = 0; i <= assistantMessageIndex && i < visibleMessages.Count; i++)
                {
                    if (string.Equals(visibleMessages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
                        turnIndex++;
                }

                TurnEvaluationExport document = TurnEvaluationExporter.Build(
                    new TurnEvaluationExporter.Request
                    {
                        Session = session,
                        Tree = tree,
                        VisibleMessages = visibleMessages,
                        AssistantMessageIndex = assistantMessageIndex,
                        ApiHistory = apiHistory,
                        LatestExecutionMetrics = metrics,
                        SessionTelemetryJson = sessionTelemetry,
                        Model = GetEffectiveModel(),
                        SolutionPath = _solutionPath,
                        ContextDebugJson = contextDebug,
                    });

                Directory.CreateDirectory(StoragePaths.EvaluationsDirectory);
                var dialog = new SaveFileDialog
                {
                    AddExtension = true,
                    DefaultExt = ".json",
                    Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
                    FileName = TurnEvaluationExporter.BuildDefaultFileName(session, Math.Max(1, turnIndex)),
                    InitialDirectory = StoragePaths.EvaluationsDirectory,
                    Title = LocalizationService.Instance["evaluation.saveDialogTitle"],
                    OverwritePrompt = true,
                };

                if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                    return;

                string path = TurnEvaluationExporter.Write(document, dialog.FileName);
                StatusLabel.Text = string.Format(
                    LocalizationService.Instance["evaluation.exportSucceeded"],
                    path);
                Logger.Info($"[JudgeExport] Turn exported: {path}");
            }
            catch (Exception ex)
            {
                StatusLabel.Text = string.Format(
                    LocalizationService.Instance["evaluation.exportFailedWithMessage"],
                    ex.Message);
                Logger.Error($"[JudgeExport] Export failed: {ex.Message}", ex);
            }
        }
    }
}
