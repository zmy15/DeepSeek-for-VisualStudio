using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// 会话管理：创建、切换、删除、清空会话，以及会话持久化。
    /// </summary>
    public partial class DeepSeekChatControl
    {
        #region Private Methods - Session Management

        /// <summary>
        /// 创建新会话的内部方法（不保存）。
        /// </summary>
        private ChatSession CreateNewSessionInternal()
        {
            ResetConversationElapsedTimer();
            ResetActiveAgentToAsk();
            _builtInToolService?.ResetConversationState();

            // ── 初始化新树 ──
            _tree = new ConversationTree();
            Logger.Info("[Tree] 新会话 → ConversationTree 已初始化");

            // ── 重置 AI 标题生成状态 ──
            _pendingAiTitle = false;
            _firstUserMessageForTitle = null;

            return new ChatSession
            {
                Id = Guid.NewGuid().ToString("N"),
                // 标题留空：展示层回退为本地化的「新对话」文案，
                // 从而与语言切换解耦（不再把语言写进会话数据）。
                Title = string.Empty,
                CreatedAt = DateTime.Now,
                LastActiveAt = DateTime.Now,
            };
        }

        /// <summary>
        /// 将当前活跃会话上下文同步到内置工具服务。
        /// MemoryTool 依赖 CurrentSessionId 定位会话记忆目录；漏同步会导致写入 session\_default 共享桶。
        /// </summary>
        private void SyncActiveSessionToBuiltInTools()
        {
            if (_builtInToolService == null) return;
            _builtInToolService.CurrentSessionId = _activeSession?.Id;
            _builtInToolService.CurrentSolutionPath = _solutionPath;
            _builtInToolService.ResetConversationState();
        }

        /// <summary>
        /// 删除指定会话的记忆目录（session\{sessionId}\）。异步执行、异常全部捕获。
        /// </summary>
        private void TryDeleteSessionMemory(string? sessionId)
        {
            var memoryService = _memoryService;
            if (memoryService == null || string.IsNullOrWhiteSpace(sessionId)) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    // path 传空字符串 → 删除整个 session\{sessionId}\ 目录（MemoryService.DeleteAsync 递归删除）
                    await memoryService.DeleteAsync(MemoryScope.Session, "", sessionId, _solutionPath);
                    Logger.Info($"[Memory] 已删除会话记忆: {sessionId}");
                }
                catch (System.IO.FileNotFoundException) { /* 无记忆目录，属正常 */ }
                catch (Exception ex) { Logger.Warn($"[Memory] 删除会话记忆失败: {ex.Message}"); }
            });
        }

        /// <summary>
        /// 保存当前活跃会话到容器并持久化。
        /// TreeDataJson 为唯一权威数据源（含所有 user/assistant 消息）。
        /// ApiHistory 保留（含 tool/system 消息，树结构不包含这些角色）。
        /// </summary>
        private void SaveCurrentSession()
        {
            if (_activeSession == null || _sessionsContainer == null) return;

            // ── 树状结构序列化 ──
            if (_tree != null)
            {
                try
                {
                    var treeData = _tree.Serialize();
                    string treeJson = System.Text.Json.JsonSerializer.Serialize(treeData,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
                    _activeSession.TreeDataJson = treeJson;

                    // ── 诊断日志：记录树状态 ──
                    var activePath = _tree.GetActivePath();
                    int userMsgCount = activePath.Count(n => n.Message?.Role == "user");
                    int assistantMsgCount = activePath.Count(n => n.Message?.Role == "assistant");
                    Logger.Info($"[Tree] 保存: 总节点={_tree.TotalNodeCount}, 活跃路径={activePath.Count}节点 (用户={userMsgCount}, 助手={assistantMsgCount}), JSON长度={treeJson.Length}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[Tree] 序列化失败: {ex.Message}", ex);
                }
            }
            else
            {
                Logger.Warn("[Tree] 保存时 _tree 为 null，仅保存 ApiHistory");
            }

            // ── ApiHistory 始终保存（含 tool/system 消息，树结构不包含）──
            _activeSession.ApiHistory = _contextManager.GetFullContext();
            _activeSession.CompressedSummaries = _contextManager.GetCompressedSummariesSnapshot();

            // ── 持久化累计 Cache 统计（重启后恢复显示）──
            if (_apiService != null)
            {
                _activeSession.CumulativeCacheHitTokens = _apiService.TotalCacheHitTokens;
                _activeSession.CumulativeCacheMissTokens = _apiService.TotalCacheMissTokens;
                _activeSession.CumulativePromptTokens = _apiService.TotalPromptTokens;
                _activeSession.CumulativeCompletionTokens = _apiService.TotalCompletionTokens;
                _activeSession.CumulativeCostYuan = _apiService.TotalSessionCostYuan;
                _activeSession.CumulativeCostUsd = _apiService.TotalSessionCostUsd;
            }

            // ── 持久化官方 Search / Fetch 调用次数（按会话，重启后恢复显示）──
            if (_webSearchService != null)
            {
                _activeSession.CumulativeOfficialSearchCount = _webSearchService.OfficialSearchSuccessCount;
                _activeSession.CumulativeOfficialFetchCount = _webSearchService.OfficialFetchSuccessCount;
            }

            _sessionsContainer.ActiveSessionId = _activeSession.Id;
            ChatPersistenceService.SaveSessions(_solutionPath, _sessionsContainer);
        }

        /// <summary>
        /// 任务开始时在会话上写入「执行中」标记并落盘。
        /// <para>
        /// 目的：VS 被强杀、或窗口在任务执行中被重建时，磁盘上能明确看出「这一轮没有收尾」，
        /// 下次加载据此提示「上次执行被中断」，而不是把半截对话当成完整历史静默继续。
        /// Handoff 链共用同一次任务，标记已存在时不重复写入。
        /// </para>
        /// </summary>
        private void BeginActiveRunMarker()
        {
            if (_activeSession == null) return;
            if (_activeSession.ActiveRun != null) return;

            _activeSession.ActiveRun = new SessionActiveRun
            {
                TurnId = _currentAgentTurnId ?? string.Empty,
                AgentType = (_activeAgent?.Definition.Type ?? AgentType.Ask).ToString(),
                StartedAt = DateTime.Now,
            };

            SaveCurrentSession();
            Logger.Info($"[任务标记] 已写入执行中标记: agent={_activeSession.ActiveRun.AgentType}, " +
                $"turn={_activeSession.ActiveRun.TurnId}");
        }

        /// <summary>
        /// 任务正常收尾（含取消/异常）时清除「执行中」标记并落盘。
        /// <para>
        /// 清除范围是容器内的全部会话，而不只是 <see cref="_activeSession"/>：
        /// 任务执行中切换会话时，<see cref="_activeSession"/> 已经换成了新会话，
        /// 而标记仍挂在旧会话对象上；只清当前会话会留下一个永久的「未收尾」标记，
        /// 让下次加载误报「上次执行被中断」。
        /// </para>
        /// </summary>
        private void CompleteActiveRunMarker()
        {
            bool changed = false;

            if (_activeSession?.ActiveRun != null)
            {
                _activeSession.ActiveRun = null;
                changed = true;
            }

            if (_sessionsContainer != null)
            {
                foreach (var session in _sessionsContainer.Sessions)
                {
                    if (session?.ActiveRun == null) continue;
                    session.ActiveRun = null;
                    changed = true;
                }
            }

            if (!changed) return;

            SaveCurrentSession();
            Logger.Info("[任务标记] 已清除执行中标记");
        }

        /// <summary>
        /// 清除所有消息上持久化的 HandoffJson 并落盘。
        /// <para>
        /// Handoff 一旦开始执行，「继续执行」按钮就不再成立。若继续留在会话里，
        /// 任务执行期间一旦发生解决方案重载/窗口重建，恢复逻辑会把它重新注入成可点击按钮，
        /// 用户再点一次就会对同一批文件并发启动第二个 Agent。
        /// </para>
        /// </summary>
        private void ClearPersistedHandoffJson()
        {
            int cleared = 0;
            lock (_lock)
            {
                foreach (var msg in _messages)
                {
                    if (string.IsNullOrEmpty(msg.HandoffJson)) continue;
                    msg.HandoffJson = null;
                    cleared++;
                }
            }

            if (cleared == 0) return;

            SaveCurrentSession();
            Logger.Info($"[AgentHandoff] 已清除 {cleared} 条消息上的持久化 HandoffJson");
        }

        /// <summary>
        /// 根据第一条用户消息，标记等待 AI 生成会话标题。
        /// 首轮对话完成后，由流式响应结束逻辑调用 GenerateAiTitleAsync 实际生成标题。
        /// </summary>
        private void AutoTitleSession()
        {
            if (_activeSession == null) return;
            // 判据为「标题尚未生成」而非「标题等于当前语言的『新对话』」：
            // 后者在语言切换后会失效（旧缺陷：中文下建的会话切到英文后不再自动生成标题）。
            if (!_activeSession.HasEmptyTitle) return;

            var firstUserMsg = _messages.FirstOrDefault(m => m.Role == "user");
            if (firstUserMsg == null || string.IsNullOrWhiteSpace(firstUserMsg.Content))
                return;

            // 标记等待 AI 生成标题，存储第一条用户消息供后续摘要使用
            _pendingAiTitle = true;
            _firstUserMessageForTitle = firstUserMsg.Content.Trim();
            Logger.Info($"[AI标题] 已标记等待生成，首条用户消息长度: {_firstUserMessageForTitle.Length}");
        }

        /// <summary>
        /// 使用 AI 根据首轮对话内容生成会话标题。
        /// 在首轮助手回复完成后由流式响应结束逻辑调用。
        /// </summary>
        /// <param name="firstUserMessage">第一条用户消息内容</param>
        /// <param name="firstAssistantReply">第一条助手回复内容</param>
        private async Task GenerateAiTitleAsync(string firstUserMessage, string firstAssistantReply)
        {
            try
            {
                if (_activeAgent == null) return;
                if (_activeSession == null) return;
                if (!_activeSession.HasEmptyTitle) return;

                string userSnippet = firstUserMessage;
                string assistantSnippet = firstAssistantReply;

                // ── 标题 prompt：文案来自语言包，分段换行由 AiPrompts 保证 ──
                // 历史缺陷：「用户：/助手：/标题：」之间的换行全部依赖资源文件的 JSON 转义
                // （\n\n），转义缺失时两段内容直接粘连成「用户：<问题>助手：<回复>」，
                // 模型无法分辨问题与回复的边界。BuildSessionTitlePrompt 会在占位符后
                // 补足换行（已有换行则保持原样），因此资源文件怎么写都不会再退化。
                var prompt = AiPrompts.BuildSessionTitlePrompt(userSnippet, assistantSnippet);

                // ── 标题请求不发 system 提示词 ──
                // 历史缺陷（v1.1.11 引入）：此处曾前置 AiPrompts.SharedImmutablePrefix 以蹭
                // Prompt Cache，结果把一个「只做 ≤20 字摘要」的请求变成了完整 Agent 请求——
                // 「Windows/PowerShell 终端规则 + Agent Handoff 规则」整段被发出去，既与任务无关
                // （模型可能被误导去执行终端/探索行为），又白付一次输入 token。
                // 标题 prompt 自身已自洽（含「不要使用工具」约束），无需 system。
                // 附带好处：messages[0] 不再是 SharedImmutablePrefix，该请求因此不会进入
                // PrefixCacheManager 的基准检查/pin，不会用标题请求的短上下文污染前缀基准。
                var messages = new List<ChatApiMessage>
                {
                    new ChatApiMessage { Role = "user", Content = prompt }
                };

                Logger.Info("[AI标题] 正在调用 API 生成标题…");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                // ── toolChoice:"none" 防止 AI 调用工具；thinkingEnabled:false 防止思考模型只输出推理而 content 为空 ──
                // 标题生成使用当前生效模型，避免依赖任何硬编码模型名。
                string titleModel = GetEffectiveModel();
                if (string.IsNullOrWhiteSpace(titleModel))
                {
                    Logger.Warn("[AI标题] 当前未选择模型，回退到截取模式");
                    FallbackAutoTitle(firstUserMessage);
                    return;
                }
                string rawTitle = await _activeAgent.CallAiWithMessagesAsync(messages, cts.Token, maxTokens: 128, temperature: 0.3, toolChoice: "none", model: titleModel, thinkingEnabled: false);

                if (string.IsNullOrWhiteSpace(rawTitle))
                {
                    Logger.Warn($"[AI标题] API 返回空标题 (raw='{rawTitle ?? "<null>"}')，回退到截取模式");
                    FallbackAutoTitle(firstUserMessage);
                    return;
                }

                // 清理标题：去除引号、换行、首尾空白
                string title = rawTitle.Trim().Trim('"', '\'', '「', '」', '《', '》', '"', '"');

                if (string.IsNullOrWhiteSpace(title))
                {
                    Logger.Warn($"[AI标题] 清理后标题为空 (raw='{rawTitle}', trimmed='{title}')，回退到截取模式");
                    FallbackAutoTitle(firstUserMessage);
                    return;
                }

                // 期间标题已被生成（或用户改写）→ 不覆盖
                if (!_activeSession.HasEmptyTitle)
                {
                    Logger.Info($"[AI标题] 标题已被更新为 '{_activeSession.Title}'，跳过覆盖");
                    return;
                }

                _activeSession.Title = title;
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                PopulateSessionComboBox();
                SaveCurrentSession();
                Logger.Info($"[AI标题] 会话标题已更新为: {title} (raw='{rawTitle}')");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[AI标题] 生成失败，回退到截取模式: {ex.Message}");
                FallbackAutoTitle(firstUserMessage);
            }
            finally
            {
                _pendingAiTitle = false;
                _firstUserMessageForTitle = null;
            }
        }

        /// <summary>
        /// 回退标题生成：截取第一条用户消息的前30个字符作为标题。
        /// 当 AI 标题生成失败时使用。
        /// </summary>
        private void FallbackAutoTitle(string firstUserMessage)
        {
            if (_activeSession == null) return;
            if (!_activeSession.HasEmptyTitle) return;

            string title = firstUserMessage.Trim();
            int newlineIdx = title.IndexOf('\n');
            if (newlineIdx > 0)
                title = title.Substring(0, newlineIdx).Trim();
            // RAG-MARK: no-truncate — 不再截断回退标题

            _activeSession.Title = title;
            PopulateSessionComboBox();
            SaveCurrentSession();
            Logger.Info($"[AI标题] 回退标题: {title}");
        }

        /// <summary>
        /// 仅在用户真正提交消息时刷新会话活跃时间；切换/查看会话不应改变排序。
        /// 注意：本方法只更新内存值，持久化依赖后续 SaveCurrentSession() 序列化时
        /// 携带该值（LastActiveAt 已不再由 SaveCurrentSession 主动刷新）。
        /// </summary>
        private void TouchCurrentSessionLastActive()
        {
            if (_activeSession == null) return;
            _activeSession.LastActiveAt = DateTime.Now;
            PopulateSessionComboBox();
        }

        /// <summary>
        /// 切换到指定会话。
        /// </summary>
        #pragma warning disable VSTHRD100 // async void 用于会话切换（从事件处理程序调用），异常已在方法内处理
        private async void SwitchToSession(ChatSession session)
        {
            ResetConversationElapsedTimer();
            _discardContextOnNextSend = false;
            _contextManager.ClearConversationResetNotice();
            try
            {
                if (session == null || session == _activeSession) return;

                lock (_lock)
                {
                    if (_isGenerating)
                    {
                        CancelStreaming();
                        _isGenerating = false;
                    }
                }

                _pendingAppendMessages.Clear();
                RefreshAppendQueuePanel();
                UpdateButtonsState();

                // 保存当前会话
                SaveCurrentSession();

                // ── 会话切换时清理计划面板相关状态 ──
                // DOM 将全量重建，所有计划面板需要重新创建
                _createdPlanIds.Clear();

                lock (_lock)
                {
                    // 切换到新会话
                    _activeSession = session;
                    if (_sessionsContainer != null)
                        _sessionsContainer.ActiveSessionId = _activeSession.Id;
                    ResetActiveAgentToAsk();

                    // ── 同步当前会话上下文到内置工具服务（MemoryTool 需要）──
                    SyncActiveSessionToBuiltInTools();

                    // ── 重置 AI 标题生成状态（切换到的会话可能已有标题） ──
                    _pendingAiTitle = false;
                    _firstUserMessageForTitle = null;

                    // 清空并加载消息
                    _messages.Clear();
                    _contextManager.Clear();
                    _messagesHtml.Clear();
                    _lastRenderedMessagesLength = 0;
                    ClearEditTransientState();
                }

                // ── 切换会话时重置并恢复累计 Token/费用统计 ──
                _apiService?.ResetAccumulatedStats();
                RestoreCacheStatsFromSession();

                // ── 从 TreeData 恢复树状结构（UI 展示用）──
                if (!string.IsNullOrWhiteSpace(_activeSession.TreeDataJson))
                {
                    try
                    {
                        var treeData = System.Text.Json.JsonSerializer.Deserialize<TreePersistenceData>(
                            _activeSession.TreeDataJson,
                            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
                        if (treeData != null)
                        {
                            _tree = ConversationTree.Deserialize(treeData);
                            SyncMessagesFromTree();
                            Logger.Info($"[Tree] 从 TreeData 恢复 (节点数: {treeData.Nodes.Count})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[Tree] TreeData 反序列化失败: {ex.Message}");
                        _tree = null;
                    }
                }
                else
                {
                    _tree = null;
                }

                // ── 从 ApiHistory 恢复完整上下文（权威数据源，含 tool_calls/reasoning/system）──
                if (_activeSession.ApiHistory.Count > 0)
                {
                    try
                    {
                        _contextManager.RestoreFullContext(_activeSession.ApiHistory);
                        _contextManager.RestoreCompressedSummaries(_activeSession.CompressedSummaries);
                        Logger.Info($"[Context] SwitchToSession 从 ApiHistory 恢复上下文成功 ({_activeSession.ApiHistory.Count} 条消息)");

                        if (_contextManager.TurnCount == 0 && _tree != null)
                            RebuildContextFromTree();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[Context] SwitchToSession 从 ApiHistory 恢复上下文失败，回退到树重建: {ex.Message}", ex);
                        RebuildContextFromTree();
                    }
                }
                else
                {
                    RebuildContextFromTree();
                }

                // ── 恢复系统级上下文（RestoreFullContext/Clear 会清空 system prompt / memory / skill）──
                await RestoreSystemContextAsync();

                // ── 若无对话消息（新会话/空会话），补上欢迎语 ──
                bool hasMessages;
                lock (_lock) { hasMessages = _messages.Count > 0; }
                if (!hasMessages)
                {
                    var welcomeMsg = new ChatMessage
                    {
                        Role = "assistant",
                        Content = WelcomeMessage,
                        Timestamp = DateTime.Now,
                        IsRendered = true,
                    };
                    lock (_lock)
                    {
                        _messages.Add(welcomeMsg);
                        // 欢迎消息不加入树结构（仅 UI 展示）
                    }
                }

                // 更新下拉框选中项
                PopulateSessionComboBox();

                // 完整刷新浏览器
                RebuildMessagesHtml();
                _browserInitialized = false;
                _pageReady = false;  // 重置页面就绪标志，等待新页面加载完成
                UpdateBrowser();

                // ── 刷新余额/消耗显示，确保 Token 计数反映当前会话 ──
                RefreshConsumptionDisplay();

                // 页面刷新后重建持久化任务面板
                _ = RebuildPanelsWhenPageReadyAsync();

                // ── 持久化 ActiveSessionId：异常退出后重启仍恢复到刚切换的会话 ──
                if (_sessionsContainer != null)
                    ChatPersistenceService.SaveSessions(_solutionPath, _sessionsContainer);

                Logger.Info($"切换到会话: {_activeSession.Title}");
            }
            catch (Exception ex)
            {
                Logger.Error($"SwitchToSession 异常: {ex.Message}", ex);
                try
                {
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = LocalizationService.Instance.Format("status.sessionSwitchFailed", ex.Message);
                }
                catch { }
            }
        }
        #pragma warning restore VSTHRD100

        /// <summary>
        /// 填充会话下拉框，保持选中当前活跃会话。
        /// </summary>
        private void PopulateSessionComboBox()
        {
            if (_sessionsContainer == null) return;

            // 交互原则：当前会话钉在顶部，其余按最后活跃时间倒序，
            // 避免用户悬停选择时列表因活跃时间变化而重排跳动。
            var sortedSessions = _sessionsContainer.Sessions
                .OrderByDescending(s => _activeSession != null && s.Id == _activeSession.Id)
                .ThenByDescending(s => s.LastActiveAt)
                .ToList();

            // 程序化填充期间抑制 SelectionChanged，避免瞬时 null/重置触发误切换
            _suppressSessionSelection = true;
            try
            {
                SessionComboBox.ItemsSource = null;
                SessionComboBox.ItemsSource = sortedSessions;

                if (_activeSession != null)
                {
                    SessionComboBox.SelectedItem = sortedSessions.FirstOrDefault(s => s.Id == _activeSession.Id);
                }
            }
            finally
            {
                _suppressSessionSelection = false;
            }

            // ── P-B：同步 Copilot 式历史浮层与标题 ──
            RefreshHistoryUI();
        }

        /// <summary>历史浮层条目视图模型。</summary>
        internal sealed class HistoryItemViewModel
        {
            public string Id { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string LastActiveText { get; set; } = string.Empty;
            public bool IsCurrent { get; set; }
            public string DeleteVisibility => IsCurrent ? "Collapsed" : "Visible";
        }

        /// <summary>
        /// 会话列表的相对时间文案。必须走本地化 —— 此前硬编码中文，
        /// 导致英文界面下仍显示「N 天前」。
        /// </summary>
        private static string FormatRelativeTime(DateTime t)
        {
            var L = LocalizationService.Instance;
            var span = DateTime.Now - t;
            if (span.TotalMinutes < 1) return L["session.relative.justNow"];
            if (span.TotalMinutes < 60) return L.Format("session.relative.minutesAgo", (int)span.TotalMinutes);
            if (span.TotalHours < 24) return L.Format("session.relative.hoursAgo", (int)span.TotalHours);
            if (span.TotalDays < 30) return L.Format("session.relative.daysAgo", (int)span.TotalDays);
            // 超过 30 天直接显示日期：yyyy-MM-dd 与语言无关
            return t.ToString("yyyy-MM-dd");
        }

        /// <summary>
        /// 会话标题的展示文案：标题为空（尚未生成）时回退为本地化的「新对话」。
        /// 标题本身不存储本地化文案，因此在语言切换后此处会自然显示当前语言。
        /// </summary>
        private static string GetSessionDisplayTitle(ChatSession? session)
        {
            if (session == null) return string.Empty;
            return session.HasEmptyTitle
                ? LocalizationService.Instance["session.new"]
                : session.Title;
        }

        /// <summary>刷新历史浮层列表与当前会话标题。</summary>
        private void RefreshHistoryUI()
        {
            try
            {
                if (CurrentSessionTitle != null)
                    CurrentSessionTitle.Text = GetSessionDisplayTitle(_activeSession);

                if (HistoryListBox == null || _sessionsContainer == null) return;

                var items = _sessionsContainer.Sessions
                    .OrderByDescending(s => _activeSession != null && s.Id == _activeSession.Id)
                    .ThenByDescending(s => s.LastActiveAt)
                    .Select(s => new HistoryItemViewModel
                    {
                        Id = s.Id,
                        Title = GetSessionDisplayTitle(s),
                        LastActiveText = FormatRelativeTime(s.LastActiveAt),
                        IsCurrent = _activeSession != null && s.Id == _activeSession.Id,
                    })
                    .ToList();

                _suppressSessionSelection = true;
                try
                {
                    HistoryListBox.ItemsSource = items;
                }
                finally
                {
                    _suppressSessionSelection = false;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[History] 刷新失败: {ex.Message}");
            }
        }

        private void HistoryToggleButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshHistoryUI();
            HistoryPopup.IsOpen = !HistoryPopup.IsOpen;
        }

        /// <summary>
        /// 历史浮层 DataTemplate 内删除按钮的 Loaded 钩子：
        /// 模板实例化时套用本地化 ToolTip（XAML 静态值仅为设计时占位）。
        /// </summary>
        private void HistoryDeleteButton_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn)
                btn.ToolTip = LocalizationService.Instance["input.deleteSessionItemTip"];
        }

        private void HistoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSessionSelection) return;
            if (HistoryListBox.SelectedItem is HistoryItemViewModel vm && _sessionsContainer != null)
            {
                var target = _sessionsContainer.Sessions.FirstOrDefault(s => s.Id == vm.Id);
                if (target != null && target != _activeSession)
                {
                    SwitchToSession(target);
                }
            }
            HistoryPopup.IsOpen = false;
        }

        private void DeleteHistoryItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender is System.Windows.Controls.Button btn ? btn.Tag as string : null) is not string id)
                return;

            if (_activeSession != null && _activeSession.Id == id)
            {
                DeleteCurrentSession();   // 复用既有确认+清理流程
                RefreshHistoryUI();
                return;
            }

            var result = System.Windows.MessageBox.Show(
                LocalizationService.Instance["chat.confirmDeleteConversation"] ?? "删除此对话？",
                LocalizationService.Instance["chat.deleteConversation"] ?? "删除对话",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (result != System.Windows.MessageBoxResult.Yes) return;

            if (_sessionsContainer != null)
            {
                var target = _sessionsContainer.Sessions.FirstOrDefault(s => s.Id == id);
                if (target != null)
                {
                    _sessionsContainer.Sessions.Remove(target);
                    TryDeleteSessionMemory(target.Id);   // 新增：清理会话记忆
                }
                ChatPersistenceService.SaveSessions(_solutionPath, _sessionsContainer);
            }
            RefreshHistoryUI();
        }

        /// <summary>
        /// 创建新对话（"新对话" 按钮点击）。
        /// </summary>
        private void CreateNewChat()
        {
            try
            {
                _discardContextOnNextSend = false;
                _contextManager.ClearConversationResetNotice();
                lock (_lock)
                {
                    // 停止当前生成
                    if (_isGenerating)
                    {
                        CancelStreaming();
                        _isGenerating = false;
                    }
                }

                _pendingAppendMessages.Clear();
                RefreshAppendQueuePanel();
                UpdateButtonsState();

                // 保存当前会话
                SaveCurrentSession();

                // 创建新会话
                _activeSession = CreateNewSessionInternal();
                if (_sessionsContainer == null)
                    _sessionsContainer = new SessionsContainer { SolutionPath = _solutionPath ?? "(unsaved)" };
                _sessionsContainer.Sessions.Add(_activeSession);
                _sessionsContainer.ActiveSessionId = _activeSession.Id;

                // ── 同步当前会话上下文到内置工具服务 ──
                SyncActiveSessionToBuiltInTools();

                lock (_lock)
                {
                    // 清空并添加欢迎语
                    _messages.Clear();
                    _contextManager.Clear();
                    _messagesHtml.Clear();
                    _lastRenderedMessagesLength = 0;
                    ClearEditTransientState();
                }

                var welcomeMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = WelcomeMessage,
                    Timestamp = DateTime.Now,
                    IsRendered = true,
                };
                lock (_lock)
                {
                    _messages.Add(welcomeMsg);
                    // 欢迎消息不加入树结构（仅 UI 展示）
                }

                // ── 重置累计 Token/费用统计（新会话从零开始）──
                _apiService?.ResetAccumulatedStats();

            // 更新下拉框
            PopulateSessionComboBox();

            // 持久化
            ChatPersistenceService.SaveSessions(_solutionPath, _sessionsContainer);

            // 重置搜索额度状态（新会话可能额度已恢复）
            _webSearchService?.ResetQuotaState();
            // 重置官方 Search / Fetch 的按次计费用量统计
            _webSearchService?.ResetOfficialToolStats();

            // 清空附件列表
            ClearAttachedFiles();

            // 刷新浏览器
            RebuildMessagesHtml();
            _browserInitialized = false;
            UpdateBrowser();

            // ── 刷新余额/消耗显示，确保新会话 token 计数归零 ──
            RefreshConsumptionDisplay();

            InputTextBox.Focus();
            Logger.Info(LocalizationService.Instance["session.createNew"]);
            }
            catch (Exception ex)
            {
                Logger.Error($"CreateNewChat 异常: {ex.Message}", ex);
                StatusLabel.Text = string.Format(LocalizationService.Instance["session.createNew"] + ": {0}", ex.Message);
            }
        }

        /// <summary>
        /// 在删除当前会话后挑选接替的会话：取「最近活跃」的一个。
        /// <para>
        /// 必须与历史浮层的展示顺序一致（见 <see cref="RefreshHistoryUI"/>：
        /// 按 <c>LastActiveAt</c> 降序）。<c>Sessions</c> 本身按创建顺序追加（最旧在前），
        /// 直接取首元素会跳到最旧的会话，与用户看到的顺序相反。
        /// </para>
        /// <para><c>CreatedAt</c> 作为次序稳定的兜底，避免活跃时间相同时结果不定。</para>
        /// <para>可见性放宽到 internal 以便单元测试覆盖。</para>
        /// </summary>
        internal static ChatSession? SelectFallbackSession(IEnumerable<ChatSession>? sessions)
        {
            if (sessions == null) return null;

            return sessions
                .Where(s => s != null)
                .OrderByDescending(s => s.LastActiveAt)
                .ThenByDescending(s => s.CreatedAt)
                .FirstOrDefault();
        }

        /// <summary>
        /// 删除当前会话。
        /// </summary>
        private void DeleteCurrentSession()
        {
            try
            {
                if (_sessionsContainer == null || _activeSession == null) return;
                if (_sessionsContainer.Sessions.Count <= 1)
                {
                    // 最后一个会话不能删除，清空即可
                    ClearCurrentSessionMessages();
                    return;
                }

                lock (_lock)
                {
                    if (_isGenerating)
                    {
                        CancelStreaming();
                        _isGenerating = false;
                    }
                }

                _pendingAppendMessages.Clear();
                RefreshAppendQueuePanel();
                UpdateButtonsState();

                string deletedTitle = _activeSession.Title;
                string deletedSessionId = _activeSession.Id;
                _sessionsContainer.Sessions.Remove(_activeSession);
                TryDeleteSessionMemory(deletedSessionId);

                // 切换到「最近活跃」的会话，而非列表中的第一个。
                // _sessionsContainer.Sessions 是按创建顺序追加的（最旧在前），
                // 而历史浮层按 LastActiveAt 降序展示 —— 若此处取 FirstOrDefault()，
                // 删除当前会话后会跳到**最旧**的会话，与用户在列表中看到的顺序相反。
                _activeSession = SelectFallbackSession(_sessionsContainer.Sessions);
                _sessionsContainer.ActiveSessionId = _activeSession?.Id;

                ResetActiveAgentToAsk();
                SyncActiveSessionToBuiltInTools();

                lock (_lock)
                {
                    // 加载新会话消息
                    _messages.Clear();
                    _contextManager.Clear();
                    _messagesHtml.Clear();
                    _lastRenderedMessagesLength = 0;
                    ClearEditTransientState();
                }

                if (_activeSession != null)
                {
                    // ── 从 TreeData 恢复树状结构（UI 展示用）──
                    if (!string.IsNullOrWhiteSpace(_activeSession.TreeDataJson))
                    {
                        try
                        {
                            var treeData = System.Text.Json.JsonSerializer.Deserialize<TreePersistenceData>(
                                _activeSession.TreeDataJson,
                                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
                            if (treeData != null)
                            {
                                _tree = ConversationTree.Deserialize(treeData);
                                SyncMessagesFromTree();
                                Logger.Info($"[Tree] DeleteCurrentSession: 从 TreeData 恢复 (节点数: {treeData.Nodes.Count})");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"[Tree] DeleteCurrentSession TreeData 反序列化失败: {ex.Message}");
                        }
                    }

                    // ── 从 ApiHistory 恢复完整上下文（权威数据源）──
                    if (_activeSession.ApiHistory.Count > 0)
                    {
                        try
                        {
                            _contextManager.RestoreFullContext(_activeSession.ApiHistory);
                            _contextManager.RestoreCompressedSummaries(_activeSession.CompressedSummaries);
                            Logger.Info($"[Context] DeleteCurrentSession 从 ApiHistory 恢复上下文成功 ({_activeSession.ApiHistory.Count} 条消息)");

                            if (_contextManager.TurnCount == 0 && _tree != null)
                                RebuildContextFromTree();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"[Context] DeleteCurrentSession 从 ApiHistory 恢复上下文失败，回退到树重建: {ex.Message}", ex);
                            RebuildContextFromTree();
                        }
                    }
                    else
                    {
                        RebuildContextFromTree();
                    }
                }

                // ── 若无对话消息（空会话），补上欢迎语 ──
                if (_messages.Count == 0)
                {
                    var welcomeMsg = new ChatMessage
                    {
                        Role = "assistant",
                        Content = WelcomeMessage,
                        Timestamp = DateTime.Now,
                        IsRendered = true,
                    };
                    _messages.Add(welcomeMsg);
                }

                // 更新下拉框并持久化
                PopulateSessionComboBox();
                ChatPersistenceService.SaveSessions(_solutionPath, _sessionsContainer);

                // 刷新浏览器
                RebuildMessagesHtml();
                _browserInitialized = false;
                UpdateBrowser();

                Logger.Info($"已删除会话: {deletedTitle}");
            }
            catch (Exception ex)
            {
                Logger.Error($"DeleteCurrentSession 异常: {ex.Message}", ex);
                    StatusLabel.Text = LocalizationService.Instance.Format("status.sessionDeleteFailed", ex.Message);
            }
        }

        /// <summary>
        /// 清空当前会话的消息（保留会话本身）。
        /// </summary>
        private void ClearCurrentSessionMessages()
        {
            try
            {
                _discardContextOnNextSend = false;
                _contextManager.ClearConversationResetNotice();
                ResetConversationElapsedTimer();
                _pendingAppendMessages.Clear();
                RefreshAppendQueuePanel();
                // ── 重置累计 Token / 费用计数器 ──
                _apiService?.ResetAccumulatedStats();
                // 官方 Search / Fetch 调用次数同属本会话累计，清空消息时一并归零
                _webSearchService?.ResetOfficialToolStats();

                // ── 聊天区即将重建：清空审批显示队列，移除残留审批卡片 ──
                ResetApprovalQueue();

                ResetActiveAgentToAsk();
                _builtInToolService?.ResetConversationState();

                // ── 重置树 ──
                _tree = new ConversationTree();
                Logger.Info("[Tree] 清空会话 → ConversationTree 已重置");

                lock (_lock)
                {
                    _messages.Clear();
                    _contextManager.Clear();
                    _messagesHtml.Clear();
                    _lastRenderedMessagesLength = 0;
                    ClearEditTransientState();
                }

                if (_activeSession != null)
                {
                    _activeSession.ApiHistory.Clear();
                    _activeSession.TreeDataJson = null;
                    // 重置为空标题，展示层回退为本地化文案
                    _activeSession.Title = string.Empty;
                }

                // 文件改动列表随会话清空：丢弃上一会话残留的待处理追踪
                EditorDiffMarkerService.Instance.ClearPendingTracking();
                RefreshChangeSummary();

                var welcomeMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = WelcomeMessage,
                    Timestamp = DateTime.Now,
                    IsRendered = true,
                };
                lock (_lock)
                {
                    _messages.Add(welcomeMsg);
                }

                PopulateSessionComboBox();
                SaveCurrentSession();

                // 清空附件列表
                ClearAttachedFiles();

                RebuildMessagesHtml();
                _browserInitialized = false;
                UpdateBrowser();
                Logger.Info(LocalizationService.Instance["session.clearConfirm"]);
            }
            catch (Exception ex)
            {
                Logger.Error($"ClearCurrentSessionMessages 异常: {ex.Message}", ex);
                StatusLabel.Text = LocalizationService.Instance.Format("status.messageSaveFailed", ex.Message);
            }
        }

        #endregion
    }
}
