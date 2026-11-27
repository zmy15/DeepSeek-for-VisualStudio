using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using DeepSeek_v4_for_VisualStudio.Utils;
using DeepSeek_v4_for_VisualStudio.View;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// <see cref="BaseAgent"/> 的 工具遥测包装与权限交互 分部实现。
    /// 承载该职责域的成员，使主文件聚焦于 Agent 执行编排。
    /// </summary>
    public abstract partial class BaseAgent
    {
        /// <summary>
        /// 带遥测的工具执行包装（P0 可观测性）：
        /// 记录每次调用的名称、总耗时（含超时等待）与成败。
        /// 成败判定沿用结果字符串约定：Error: （错误）/ Timeout: （超时）前缀视为失败。
        /// </summary>
        private async Task<string> ExecuteToolWithTelemetryAsync(
            Services.Telemetry.AgentMetricsCollector? metrics,
            int round,
            ToolCall tc,
            string? workspaceRoot,
            CancellationToken ct,
            TimeSpan timeout)
        {
            bool mutationLockHeld = false;
            if (IsFileModifyingTool(NormalizeToolName(tc.Function.Name)))
            {
                await FileMutationGate.WaitAsync(ct).ConfigureAwait(false);
                mutationLockHeld = true;
            }

            try
            {
                return await ExecuteToolWithTelemetryCoreAsync(
                    metrics, round, tc, workspaceRoot, ct, timeout).ConfigureAwait(false);
            }
            finally
            {
                if (mutationLockHeld)
                    FileMutationGate.Release();
            }
        }

        private async Task<string> ExecuteToolWithTelemetryCoreAsync(
            Services.Telemetry.AgentMetricsCollector? metrics,
            int round,
            ToolCall tc,
            string? workspaceRoot,
            CancellationToken ct,
            TimeSpan timeout)
        {
            if (metrics == null)
                return await ExecuteToolWithTimeoutAsync(tc, workspaceRoot, ct, timeout).ConfigureAwait(false);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            string result;
            try
            {
                result = await ExecuteToolWithTimeoutAsync(tc, workspaceRoot, ct, timeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                sw.Stop();
                metrics.RecordToolCall(round, tc.Function.Name, sw.ElapsedMilliseconds, success: false, ex.Message);
                throw;
            }
            sw.Stop();

            // ── P2-B：结果分类唯一权威点（Error: /Timeout: 约定集中在 ToolExecutionOutcome）──
            var outcome = Models.ToolExecutionOutcome.FromRaw(tc.Function.Name, result, sw.ElapsedMilliseconds);
            metrics.RecordToolCall(round, tc.Function.Name, sw.ElapsedMilliseconds, outcome.Success,
                outcome.Success ? null : result.Truncate(200));
            return result;
        }

        private static async Task ObserveSubagentCompletionAsync(Task<AgentResult> task, string traceId)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Logger.Info($"[Subagent:{traceId}] 超时取消已生效");
            }
            catch (Exception ex)
            {
                Logger.Error($"[Subagent:{traceId}] 超时后子代理仍抛出异常: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 尝试从工具调用中提取文件路径并注册到 ActiveFileTracker。
        /// 解析工具参数 JSON 中的 filePath/path 字段，判断读写类型。
        /// </summary>
        private void TryTrackActiveFileAccess(ToolCall tc, string? workspaceRoot)
        {
            try
            {
                var tracker = BuiltInTools?.ActiveFileTracker;
                if (tracker == null) return;

                string toolName = tc.Function.Name;
                string filePath = ExtractFilePathFromToolArgs(toolName, tc.Function.Arguments) ?? string.Empty;
                if (string.IsNullOrEmpty(filePath)) return;

                // 转换为绝对路径
                string resolvedPath = ResolveToAbsolutePath(filePath, workspaceRoot);

                int currentTurn = Context?.ContextManager?.TurnCount ?? 0;

                if (IsWriteTool(toolName))
                    tracker.ObserveWrite(resolvedPath, toolName, currentTurn);
                else
                    tracker.ObserveRead(resolvedPath, toolName, currentTurn);
            }
            catch
            {
                // 追踪失败不阻塞工具执行
            }
        }

        /// <summary>
        /// 将路径解析为绝对路径。
        /// </summary>
        private static string ResolveToAbsolutePath(string filePath, string? workspaceRoot)
            => AgentPathResolver.ResolveToAbsolutePath(filePath, workspaceRoot);

        /// <summary>
        /// 裁剪工具结果（Agent 工具循环内部使用）。
        /// 与 ConversationContextManager.AddToolResult 使用同一裁剪器实例，
        /// 确保 messages 列表和 _entries 历史中的内容一致。
        /// </summary>
        private string CompactToolResultForAgent(string toolName, string rawResult)
        {
            try
            {
                var compactor = BuiltInTools?.ToolResultCompactor;
                if (compactor == null || string.IsNullOrEmpty(rawResult))
                    return rawResult ?? string.Empty;

                string model = Context?.ContextManager?.CurrentModel ?? "deepseek-v4";
                return compactor.CompactToolResultForContext(toolName, rawResult, model);
            }
            catch
            {
                return rawResult ?? string.Empty;
            }
        }

        /// <summary>
        /// 构建工具结果消息。视觉工具（fetch_webpage / capture_window / capture_webpage / read_file）
        /// 会把图片作为 image_url 内容块附加；否则只返回纯文本。
        /// </summary>
        private static ChatApiMessage BuildToolResultMessage(
            string toolCallId, string toolName, string contextResult, List<string>? imageUrls)
        {
            var message = new ChatApiMessage
            {
                Role = "tool",
                Content = contextResult,
                ToolCallId = toolCallId,
                Name = toolName,
            };

            if (imageUrls is { Count: > 0 })
            {
                var parts = new List<ChatContentPart>
                {
                    new ChatContentPart { Type = "text", Text = contextResult },
                };
                foreach (string u in imageUrls)
                {
                    parts.Add(new ChatContentPart
                    {
                        Type = "image_url",
                        ImageUrl = new ChatImageUrl { Url = u },
                    });
                }
                message.MultimodalContent = parts;
            }

            return message;
        }

        #region Permission

        /// <summary>
        /// 请求用户许可执行某个操作。
        /// </summary>
        /// <param name="title">简短标题（如"确认修改项目文件: foo.csproj"）</param>
        /// <param name="command">具体操作描述（告诉用户将要做什么）</param>
        /// <param name="actionType">操作类型</param>
        /// <param name="detail">可选额外详情（如文件写入时展示变更内容预览）</param>
        /// <param name="purpose">操作目的（告诉用户为什么要执行此操作）</param>
        public async Task<bool> RequestPermissionAsync(string title, string command, string actionType = "command", string detail = "", string purpose = "", CancellationToken ct = default)
        {
            var request = new AgentPermissionRequest
            {
                Title = title,
                Command = command,
                ActionType = actionType,
                Detail = detail,
                Purpose = purpose,
                ResponseTcs = new TaskCompletionSource<bool>(),
            };

            // 将请求注册到并发字典（支持多工具并行场景）
            _pendingPermissions[request.RequestId] = request;

            // ── 注册取消回调：停止按钮按下时自动拒绝权限，避免永久阻塞 ──
            CancellationTokenRegistration ctr = default;
            if (ct.CanBeCanceled)
                ctr = ct.Register(() => request.ResponseTcs?.TrySetResult(false));

            try
            {
                PermissionRequested?.Invoke(request);
                AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.waitingPermission"], title));

                bool approved = await request.ResponseTcs.Task;
                // 清理：如果该请求仍在字典中则移除（可能已被 RespondToPermission 移除）
                _pendingPermissions.TryRemove(request.RequestId, out _);
                AddLog("INFO", $"{LocalizationService.Instance["agent.log.permissionResult"]}: {(approved ? " 允许" : "Error: 拒绝")} → {title}");
                return approved;
            }
            finally
            {
                ctr.Dispose();
            }
        }

        /// <summary>
        /// 请求用户批准终端命令执行（显示命令详情和说明）。
        /// </summary>
        /// <param name="command">待执行的命令字符串</param>
        /// <param name="explanation">命令用途说明（告诉用户这条命令在做什么，如"编译项目检查错误"）</param>
        /// <param name="purpose">操作目的（告诉用户为什么要执行，如"验证代码修改后能否正常编译"）</param>
        /// <returns>true 表示用户允许执行，false 表示跳过</returns>
        public async Task<bool> RequestTerminalApprovalAsync(string command, string explanation, string purpose = "", CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(command))
                return false;

            var request = new AgentPermissionRequest
            {
                Title = LocalizationService.Instance["agent.approval.runCommandTitle"],
                Command = command,
                ActionType = "terminal_command",
                Purpose = purpose,
                FilePaths = new List<string> { explanation ?? string.Empty },
                ResponseTcs = new TaskCompletionSource<bool>(),
            };

            // 将请求注册到并发字典（支持多工具并行场景）
            _pendingPermissions[request.RequestId] = request;

            // ── 注册取消回调 ──
            CancellationTokenRegistration ctr = default;
            if (ct.CanBeCanceled)
                ctr = ct.Register(() => request.ResponseTcs?.TrySetResult(false));

            try
            {
                PermissionRequested?.Invoke(request);
                AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.waitingTerminalApproval"], command));

                bool approved = await request.ResponseTcs.Task;
                // 清理
                _pendingPermissions.TryRemove(request.RequestId, out _);
                AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.terminalApprovalResult"],
                    approved ? LocalizationService.Instance["agent.approval.allowed"]
                        : LocalizationService.Instance["agent.approval.skipped"], command));
                return approved;
            }
            finally
            {
                ctr.Dispose();
            }
        }

        /// <summary>
        /// 按 RequestId 查找待处理的权限请求（不删除）。
        /// </summary>
        public AgentPermissionRequest? TryGetPendingPermission(string requestId)
        {
            _pendingPermissions.TryGetValue(requestId, out var request);
            return request;
        }

        /// <summary>
        /// 响应权限请求。
        /// 支持并发权限请求：从 _pendingPermissions 字典中按 RequestId 精确查找并解析。
        /// </summary>
        public void RespondToPermission(string requestId, bool approved)
        {
            // 从并发安全字典中查找（支持多工具并行场景）
            if (_pendingPermissions.TryRemove(requestId, out var request))
            {
                request.ResponseTcs?.TrySetResult(approved);
            }
        }

        /// <summary>
        /// 向用户提问并等待回答（VisualStudio_askQuestions 工具实现）。
        /// 使用 TaskCompletionSource 等待用户在 WebView 中提交答案。
        /// </summary>
        /// <param name="questionsJson">问题列表的 JSON 字符串</param>
        /// <returns>用户答案的 JSON 字符串，或超时/取消时的空字符串</returns>
        public async Task<string> RequestAskQuestionsAsync(string questionsJson, CancellationToken ct = default)
        {
            try
            {
                var questions = System.Text.Json.JsonSerializer.Deserialize<List<AgentQuestion>>(
                    questionsJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
                if (questions == null || questions.Count == 0)
                    return LocalizationService.Instance["service.baseAgent.emptyQuestions"];

                var request = new AgentQuestionRequest
                {
                    Questions = questions,
                    ResponseTcs = new TaskCompletionSource<string>(),
                };

                // 将请求注册到并发字典（支持多工具并行场景）
                _pendingQuestions[request.RequestId] = request;

                // ── 注册取消回调：停止按钮按下时自动返回空答案，避免永久阻塞 ──
                CancellationTokenRegistration ctr = default;
                if (ct.CanBeCanceled)
                    ctr = ct.Register(() => request.ResponseTcs?.TrySetResult("[]"));

                try
                {
                    // ── 诊断：记录事件订阅状态 ──
                    int handlerCount = QuestionsRequested?.GetInvocationList().Length ?? 0;
                    Logger.Info($"[Agent:{Definition.Name}] QuestionsRequested 事件订阅数: {handlerCount}, RequestId={request.RequestId}");

                    if (handlerCount == 0)
                    {
                        // ── 无 UI 处理器订阅 → 无法向用户展示问题，立即返回空答案避免永久阻塞 ──
                        Logger.Warn($"[Agent:{Definition.Name}]  QuestionsRequested 事件无订阅者！" +
                            $"无法向用户展示 {questions.Count} 个问题。请检查 Agent 事件绑定链。" +
                            $"当前活跃 Agent: {Definition.Type}, RequestId={request.RequestId}");
                        _pendingQuestions.TryRemove(request.RequestId, out _);
                        return "[]"; // 返回空答案，让 AI 知道用户未回答
                    }

                    QuestionsRequested!.Invoke(request);
                    AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.waitingAnswers"],
                        questions.Count, questions[0].Header));

                    // 无限等待用户回答（不设超时），用户提交或跳过时通过 ResponseTcs 唤醒
                    string answers = await request.ResponseTcs.Task;

                    // 清理
                    _pendingQuestions.TryRemove(request.RequestId, out _);
                    AddLog("INFO", string.Format(LocalizationService.Instance["agent.log.answersReceived"],
                        answers));
                    return answers;
                }
                finally
                {
                    ctr.Dispose();
                }
            }
            catch (Exception ex)
            {
                return LocalizationService.Instance.Format("agent.approval.askQuestionsFailed", ex.Message);
            }
        }

        /// <summary>
        /// 按 RequestId 查找待处理的提问请求（不删除）。
        /// </summary>
        public AgentQuestionRequest? TryGetPendingQuestion(string requestId)
        {
            _pendingQuestions.TryGetValue(requestId, out var request);
            return request;
        }

        /// <summary>
        /// 响应用户提问回答。
        /// 支持并发提问请求：从 _pendingQuestions 字典中按 RequestId 精确查找并解析。
        /// </summary>
        public void RespondToQuestions(string requestId, string answersJson)
        {
            // 从并发安全字典中查找（支持多工具并行场景）
            if (_pendingQuestions.TryRemove(requestId, out var request))
            {
                request.ResponseTcs?.TrySetResult(answersJson);
            }
        }

        /// <summary>
        /// 请求用户确认文件删除操作。
        /// 会中断当前执行流，在 WebView 中渲染确认按钮，等待用户响应。
        /// </summary>
        /// <param name="filePaths">待删除的文件绝对路径列表</param>
        /// <param name="reason">删除原因说明（告诉用户为什么删除这些文件）</param>
        /// <param name="purpose">操作目的（告诉用户删除后能达到什么效果）</param>
        /// <returns>true 表示用户确认删除，false 表示取消</returns>
        public async Task<bool> RequestFileDeleteConfirmationAsync(List<string> filePaths, string reason = "", string purpose = "", CancellationToken ct = default)
        {
            if (filePaths == null || filePaths.Count == 0)
                return false;

            var fileNames = filePaths.Select(p => System.IO.Path.GetFileName(p)).ToList();
            string title = filePaths.Count == 1
                ? LocalizationService.Instance.Format("agent.approval.deleteFileSingle", fileNames[0])
                : LocalizationService.Instance.Format("agent.approval.deleteFilesMultiple", filePaths.Count);
            string command = !string.IsNullOrEmpty(reason) ? reason : string.Join("\n", filePaths);

            var request = new AgentPermissionRequest
            {
                Title = title,
                Command = command,
                ActionType = "file_delete",
                Purpose = purpose,
                FilePaths = new List<string>(filePaths),
                ResponseTcs = new TaskCompletionSource<bool>(),
            };

            // 将请求注册到并发字典（支持多工具并行场景）
            _pendingPermissions[request.RequestId] = request;

            // ── 注册取消回调 ──
            CancellationTokenRegistration ctr = default;
            if (ct.CanBeCanceled)
                ctr = ct.Register(() => request.ResponseTcs?.TrySetResult(false));

            try
            {
                PermissionRequested?.Invoke(request);
                AddLog("INFO", $"{LocalizationService.Instance["agent.log.waitingDeleteConfirm"]}: {title}");

                bool approved = await request.ResponseTcs.Task;
                // 清理
                _pendingPermissions.TryRemove(request.RequestId, out _);
                AddLog("INFO", LocalizationService.Instance.Format("agent.log.fileDeleteConfirm", approved ? " 确认删除" : "Error: 取消", title));
                return approved;
            }
            finally
            {
                ctr.Dispose();
            }
        }

        #endregion
    }
}
