using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.Editing;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 编辑器内嵌 Diff 预览管理服务（preview-then-commit）。
    ///
    /// 单文件、单会话：预览阶段不修改 sourceBuffer，用户在 <see cref="IEditorDiffHost"/>
    /// 上逐块「保留 / 撤销」，或整体接受/撤销；写穿模式下通过
    /// <see cref="Editing.StagedEditWorkspace"/> 恢复/确认磁盘 Baseline。
    /// </summary>
    public class EditorDiffMarkerService
    {
        #region Singleton

        private static EditorDiffMarkerService? _instance;
        private static readonly object _instanceLock = new();

        public static EditorDiffMarkerService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new EditorDiffMarkerService();
                    }
                }
                return _instance;
            }
        }

        #endregion

        #region Fields

        /// <summary>
        /// 编辑器 Diff 宿主工厂 — 由 View 层在扩展初始化时注入。
        /// 服务只依赖 <see cref="IEditorDiffHost"/> 抽象，因此不必引用 View 命名空间；
        /// 未注入时内联预览不可用（<see cref="CreateInlineDiffPreview"/> 返回 null 并记日志）。
        /// </summary>
        public static IEditorDiffHostFactory? HostFactory { get; set; }

        private readonly InlineDiffSessionManager _sessionManager = new();
        private readonly Dictionary<ITextBuffer, IEditorDiffHost> _editorHosts = new();
        private readonly object _editorHostsLock = new();

        // ── 预览刷新防抖 ──
        // 写穿编辑会在极短时间内反复触发（同一文件多次写 / 多个步骤连续写）。
        // 每次刷新都会重建 IDifferenceBuffer + IWpfDifferenceViewer（UI 线程、毫秒级开销），
        // 密集触发时会把 UI 线程占满，表现为界面卡顿。这里按 buffer 合并刷新请求：
        // 防抖窗口内只保留「最后一次」待刷新内容，窗口结束后统一重建一次预览。
        private const int PreviewRefreshDebounceMs = 250;

        private sealed class PendingPreviewRefresh
        {
            public DispatcherTimer? Timer;
            public string FilePath = string.Empty;
        }

        private readonly Dictionary<ITextBuffer, PendingPreviewRefresh> _pendingPreviewRefreshes = new();

        // ── 待处理改动追踪（供聊天区「文件改动列表」驱动）──
        // 一轮编辑的多个文件共用同一个 StagedEditWorkspace；列表按 Workspace 的
        // 实时追踪集合渲染，某文件被保留/撤销后自然从列表消失。
        private readonly object _reviewLock = new();
        private Editing.StagedEditWorkspace? _reviewWorkspace;
        private int _reviewTurnUserMsgIndex = -1;
        private bool _disposed;

        /// <summary>
        /// 待处理改动集合发生变化（文件新增/确认/撤销）时触发。
        /// 供聊天区刷新文件改动列表；触发线程不固定，订阅方需自行切回 UI 线程。
        /// </summary>
        public event EventHandler? PendingChangesChanged;

        #endregion

        #region Public API — Diff Preview

        /// <summary>
        /// 创建 Inline Diff 预览会话（preview-then-commit）。
        /// 预览阶段不修改 sourceBuffer，用户确认后才通过 ProposalCommitCoordinator 提交。
        /// </summary>
        /// <param name="textView">目标 WPF 文本视图（缓冲区应仍为原始内容）</param>
        /// <param name="originalContent">修改前的原始代码</param>
        /// <param name="proposedContent">AI 建议的新代码</param>
        /// <param name="textChanges">结构化改动（可选，逐块撤销用）</param>
        /// <param name="workspace">写穿编辑工作区（可选，撤销时恢复磁盘 Baseline）</param>
        public InlineDiffSession? CreateInlineDiffPreview(
            IWpfTextView textView, string originalContent, string proposedContent,
            IReadOnlyList<ProposedTextChange>? textChanges = null,
            Editing.StagedEditWorkspace? workspace = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (textView == null)
                throw new ArgumentNullException(nameof(textView));

            if (string.IsNullOrEmpty(proposedContent) || originalContent == proposedContent)
                return null;

            // 宿主工厂由 View 层在扩展初始化时注入；未注入说明 UI 尚未就绪，
            // 此处直接跳过预览而不是抛异常，避免非 UI 宿主（测试/headless）调用时报错。
            var hostFactory = HostFactory;
            if (hostFactory == null)
            {
                Logger.Warn("[EditorDiff] 宿主工厂未注入，跳过内联 Diff 预览");
                return null;
            }

            // 获取文件路径
            string filePath = GetFilePathFromBuffer(textView.TextBuffer)
                ?? textView.TextBuffer.GetHashCode().ToString();

            // 构建 PreparedChangeSet
            var change = BuildChangeSet(
                textView, filePath, originalContent, proposedContent, textChanges);

            // ── 同一文档已有活跃写穿会话（AI 在预览期间再次编辑同一文件）→ 原地刷新 ──
            // 旧会话的 Baseline / Hunks 已过期，直接创建会被单文档单 Session 约束拒绝。
            if (workspace != null &&
                _sessionManager.TryGetSession(textView.TextBuffer, out var existingSession) &&
                existingSession != null)
            {
                RefreshWriteThroughSession(existingSession, change, workspace, textView.TextBuffer);
                Logger.Info($"[EditorDiff] Inline Diff Session 已刷新: {existingSession.SessionId.Substring(0, 8)} ({Path.GetFileName(filePath)})");
                return existingSession;
            }

            // 创建 Session
            var session = _sessionManager.CreateSession(textView, change);

            if (session == null)
            {
                Logger.Warn($"[EditorDiff] 无法创建 Session: {Path.GetFileName(filePath)}");
                return null;
            }

            // ── 写穿模式：关联 Workspace，撤销时恢复磁盘 Baseline ──
            if (workspace != null)
            {
                session.Workspace = workspace;
                AttachReviewWorkspace(workspace);
            }

            // 显示：先登记宿主，待编辑器首帧渲染后再挂载覆盖层，避免阻塞视图创建
            var editorHost = hostFactory.Create(textView);
            editorHost.Closed += (_, __) =>
            {
                lock (_editorHostsLock)
                {
                    if (_editorHosts.TryGetValue(textView.TextBuffer, out var current) &&
                        ReferenceEquals(current, editorHost))
                    {
                        _editorHosts.Remove(textView.TextBuffer);
                    }
                }
            };

            lock (_editorHostsLock)
            {
                _editorHosts[textView.TextBuffer] = editorHost;
            }

            textView.VisualElement.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (!textView.IsClosed && session.State == InlineDiffSessionState.Showing)
                        editorHost.Show(session);
                }),
                System.Windows.Threading.DispatcherPriority.Loaded);

            Logger.Info($"[EditorDiff] Inline Diff Session 已创建: {session.SessionId.Substring(0, 8)} ({Path.GetFileName(filePath)})");

            return session;
        }

        /// <summary>
        /// 构建写穿模式下的单文件变更提案（Baseline = 待处理基线，Proposed = 当前内容）。
        /// </summary>
        private static PreparedChangeSet BuildChangeSet(
            IWpfTextView textView, string filePath, string baseline, string proposed,
            IReadOnlyList<ProposedTextChange>? textChanges = null)
            => BuildChangeSet(
                textView.TextBuffer.ContentType.TypeName,
                textView.TextBuffer, filePath, baseline, proposed, textChanges);

        /// <summary>
        /// 构建写穿模式下的单文件变更提案（防抖刷新路径使用：已知 buffer，无 IWpfTextView）。
        /// </summary>
        private static PreparedChangeSet BuildChangeSet(
            string contentTypeName, ITextBuffer textBuffer, string filePath, string baseline,
            string proposed, IReadOnlyList<ProposedTextChange>? textChanges = null)
            => new PreparedChangeSet
            {
                FilePath = filePath,
                Operation = ProposedFileOperation.Modify,
                BaselineText = baseline,
                ProposedText = proposed,
                TextChanges = textChanges ?? Array.Empty<ProposedTextChange>(),
                ContentTypeName = contentTypeName,
                SaveBehavior = ProposalSaveBehavior.KeepDocumentDirty,
            };

        /// <summary>
        /// 原地刷新已有写穿会话：把撤销追踪移交新 Workspace、替换变更内容，
        /// 并通过宿主重建只读视图与 hunk 按钮。
        ///
        /// 本方法被 FileTracked / 步骤收尾 / 防抖刷新等多条路径调用，必须满足：
        /// 1. 幂等 —— 内容与 Workspace 均未变化时不再重建 Diff 视图；
        /// 2. 不重复订阅 —— Workspace 的 FileTracked 只订阅一次，否则每次刷新
        ///    都会让订阅数翻倍，后续一次写入就会触发指数级数量的预览重建。
        /// </summary>
        private void RefreshWriteThroughSession(
            InlineDiffSession session,
            PreparedChangeSet change,
            Editing.StagedEditWorkspace workspace,
            ITextBuffer buffer)
        {
            var oldWorkspace = session.Workspace;

            // ── 幂等短路：内容与追踪 Workspace 都没变 → 无需重建视图 ──
            // 重建一次的代价是「两个临时文件 + 两个文本缓冲区 + DifferenceBuffer +
            // IWpfDifferenceViewer」，全部在 UI 线程完成，重复调用会直接卡住界面。
            if (ReferenceEquals(oldWorkspace, workspace) &&
                string.Equals(session.Change.BaselineText, change.BaselineText, StringComparison.Ordinal) &&
                string.Equals(session.Change.ProposedText, change.ProposedText, StringComparison.Ordinal))
            {
                return;
            }

            if (oldWorkspace != null && !ReferenceEquals(oldWorkspace, workspace))
                oldWorkspace.DiscardFile(change.FilePath);

            session.ReplaceChange(change);
            session.Workspace = workspace;

            lock (_editorHostsLock)
            {
                if (_editorHosts.TryGetValue(buffer, out var editorHost))
                    editorHost.RefreshSession(session);
            }

            // 同一 Workspace 时 AttachReviewWorkspace 内部直接返回，不会重复订阅；
            // 换成新 Workspace 时才需要重新挂接（并退订旧的）。
            AttachReviewWorkspace(workspace);
        }

        /// <summary>
        /// 编辑器内嵌挂件：获取指定 buffer 的活跃 Session 及差异块（Hunks）。
        /// 返回 null 表示该 buffer 无活跃 Session。
        /// </summary>
        public InlineDiffSession? GetActiveSessionForBuffer(ITextBuffer buffer)
        {
            if (_sessionManager.TryGetSession(buffer, out var session))
                return session;
            return null;
        }

        /// <summary>
        /// 编辑器内嵌挂件：撤销指定 buffer 的某个差异块，并刷新编辑器。
        /// </summary>
        public bool RevertHunkForBuffer(ITextBuffer buffer, int hunkIndex)
        {
            var session = GetActiveSessionForBuffer(buffer);
            if (session?.Workspace == null) return false;

            bool ok = session.Workspace.RestoreSingleHunk(session.Change.FilePath, hunkIndex);
            if (ok)
            {
                Logger.Info($"[EditorDiff] 编辑器内嵌：已撤销块 [{hunkIndex}] ({System.IO.Path.GetFileName(session.Change.FilePath)})");
            }
            return ok;
        }

        /// <summary>
        /// 编辑器内嵌挂件：保留指定 buffer 的某个差异块（磁盘不变，仅确认该块）。
        /// </summary>
        public bool AcceptHunkForBuffer(ITextBuffer buffer, int hunkIndex)
        {
            var session = GetActiveSessionForBuffer(buffer);
            if (session?.Workspace == null) return false;

            bool ok = session.Workspace.AcceptSingleHunk(session.Change.FilePath, hunkIndex);
            if (ok)
            {
                Logger.Info($"[EditorDiff] 编辑器内嵌：已保留块 [{hunkIndex}] ({System.IO.Path.GetFileName(session.Change.FilePath)})");
            }
            return ok;
        }

        /// <summary>
        /// 编辑器内嵌挂件：保留全部。写穿模式下磁盘即最终内容，
        /// 只确认并清除撤销追踪（不重写文件）；非写穿模式走正常 Commit 流程。
        /// </summary>
        public void ConfirmAllForBuffer(ITextBuffer buffer)
        {
            var session = GetActiveSessionForBuffer(buffer);
            if (session == null) return;

            CancelPendingPreviewRefresh(buffer);
            CloseEditorHost(buffer);

            if (session.Workspace != null)
            {
                session.ConfirmWriteThrough();
            }
            else
            {
                _ = session.CommitAsync(System.Threading.CancellationToken.None);
            }
        }

        /// <summary>
        /// 编辑器内嵌挂件：撤销全部（恢复 Baseline 并关闭内嵌 Diff 覆盖层）。
        /// </summary>
        public void DismissSessionForBuffer(ITextBuffer buffer)
        {
            var session = GetActiveSessionForBuffer(buffer);
            if (session == null) return;

            CancelPendingPreviewRefresh(buffer);
            CloseEditorHost(buffer);
            session.Dismiss();
            RaisePendingChangesChanged();
        }

        #endregion

        #region Public API — Pending Change Tracking

        /// <summary>
        /// 登记本轮编辑的待处理 Workspace（Agent 每个文件完成写入时调用）。
        /// 同一轮的多个文件共用一个 Workspace，重复登记幂等。
        /// </summary>
        public void AttachReviewWorkspace(Editing.StagedEditWorkspace workspace)
        {
            if (workspace == null) return;

            Editing.StagedEditWorkspace? previous;
            lock (_reviewLock)
            {
                if (ReferenceEquals(_reviewWorkspace, workspace)) return;
                previous = _reviewWorkspace;
                DetachReviewWorkspaceLocked();
                _reviewWorkspace = workspace;
                workspace.FileTracked += OnReviewFileTracked;
            }
            // 新一轮编辑接管后，关闭上一轮残留的预览覆盖层（磁盘内容保持不变）。
            CloseReviewSessions(previous);
            // 上一轮未处理完的改动默认保留（磁盘已是最终内容），同时清理其撤销备份。
            try { previous?.ConfirmAll(); } catch (Exception ex) { Logger.Warn($"[EditorDiff] 清理上一轮 Review Workspace 失败: {ex.Message}"); }
            RaisePendingChangesChanged();
        }

        /// <summary>
        /// 记录本轮待处理改动归属的用户消息索引（会话切换清空时使用）。
        /// </summary>
        public void SetReviewTurn(int userMsgIndex)
        {
            lock (_reviewLock) { _reviewTurnUserMsgIndex = userMsgIndex; }
        }

        /// <summary>本轮待处理改动归属的用户消息索引；无活动轮时为 -1。</summary>
        public int ReviewTurnUserMsgIndex
        {
            get { lock (_reviewLock) return _reviewTurnUserMsgIndex; }
        }

        /// <summary>
        /// 当前仍有待处理改动的文件路径快照（相对/绝对取决于写入方，通常为绝对路径）。
        /// </summary>
        public IReadOnlyList<string> GetPendingFilePaths()
        {
            lock (_reviewLock)
            {
                return _reviewWorkspace != null
                    ? _reviewWorkspace.GetTrackedFilePaths()
                    : (IReadOnlyList<string>)Array.Empty<string>();
            }
        }

        /// <summary>指定文件当前是否仍有待处理改动。</summary>
        public bool HasPendingFile(string filePath)
        {
            lock (_reviewLock)
            {
                return _reviewWorkspace != null && _reviewWorkspace.HasTrackedFile(filePath);
            }
        }

        /// <summary>
        /// 指定文件「待处理差异」的真实增删行数（仅统计未保留/未撤销的块）。
        /// </summary>
        public (int Added, int Removed) GetPendingLineStats(string filePath)
        {
            lock (_reviewLock)
            {
                return _reviewWorkspace != null
                    ? _reviewWorkspace.GetPendingLineStats(filePath)
                    : (0, 0);
            }
        }

        /// <summary>
        /// 为指定文件打开（或聚焦）内嵌 Diff 预览。
        /// 从「文件改动列表」点击条目时调用：已打开的文件直接复用其活跃会话，
        /// 未打开的文件按需打开编辑器后再创建会话。
        /// </summary>
        public async Task<bool> OpenDiffPreviewForFileAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            Editing.StagedEditWorkspace? workspace;
            lock (_reviewLock) { workspace = _reviewWorkspace; }

            // 只显示「尚未处理」的块：已保留/已撤销的块并入基线，不再重复出现在预览里。
            string baseline = workspace != null && workspace.HasTrackedFile(filePath)
                ? workspace.BuildPendingOnlyBaseline(filePath)
                : (workspace?.GetBaseline(filePath) ?? string.Empty);
            string proposed = File.Exists(filePath)
                ? Utils.FileEncodingHelper.ReadAllText(filePath)
                : string.Empty;

            IWpfTextView? view = FindWpfTextViewForFile(filePath);
            if (view == null)
            {
                view = await OpenDocumentAndGetViewAsync(filePath);
            }
            if (view == null) return false;

            // 已有活跃会话 → 复用，但先确认覆盖层还挂在编辑器上
            // （切换文档标签会把可视树卸载/重载，Adorner 可能已被 WPF 丢弃）。
            if (_sessionManager.TryGetSession(view.TextBuffer, out var existing) && existing != null)
            {
                EnsureEditorHostOverlay(view.TextBuffer);
                return true;
            }

            var session = CreateInlineDiffPreview(
                view, baseline, proposed, textChanges: null, workspace: workspace);
            return session != null;
        }

        /// <summary>
        /// 全部应用：确认所有待处理文件（清除撤销追踪，保留磁盘内容）并收起预览。
        /// </summary>
        public void ApplyAllPending()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Editing.StagedEditWorkspace? workspace;
            lock (_reviewLock) { workspace = _reviewWorkspace; }

            // 先关闭本轮所有编辑器覆盖层，再确认文件，避免覆盖层挂在已确认的文件上。
            CloseReviewSessions(workspace);
            workspace?.ConfirmAll();
            RaisePendingChangesChanged();
        }

        /// <summary>
        /// 全部撤销：把所有待处理文件恢复到 Baseline（写回磁盘）并收起预览。
        /// </summary>
        public void RevertAllPending()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Editing.StagedEditWorkspace? workspace;
            lock (_reviewLock) { workspace = _reviewWorkspace; }

            CloseReviewSessions(workspace);
            workspace?.RestoreToBaseline();
            RaisePendingChangesChanged();
        }

        /// <summary>
        /// 清空待处理追踪（切换会话 / 新建 / 回退整轮后调用）。
        /// 不修改磁盘：调用方需自行决定是否先确认或撤销。
        /// </summary>
        public void ClearPendingTracking()
        {
            Editing.StagedEditWorkspace? workspace;
            lock (_reviewLock)
            {
                workspace = _reviewWorkspace;
                DetachReviewWorkspaceLocked();
            }
            CloseReviewSessions(workspace);
            try { workspace?.ConfirmAll(); } catch (Exception ex) { Logger.Warn($"[EditorDiff] 清理 Review Workspace 失败: {ex.Message}"); }
            RaisePendingChangesChanged();
        }

        /// <summary>
        /// 某文件被单独保留/撤销后主动通知列表刷新（逐块操作收尾时调用）。
        /// </summary>
        public void NotifyPendingChanged()
        {
            RaisePendingChangesChanged();
        }

        #endregion

        #region Private — Pending Change Tracking

        private void OnReviewFileTracked(string filePath)
        {
            RaisePendingChangesChanged();

            // 文件已在 IDE 打开时，恢复「被修改即自动弹出 Diff 预览」的既有行为；
            // 未打开的文件不主动打开，留在文件改动列表里由用户点击查看。
            TryAutoPreviewOpenFile(filePath);
        }

        /// <summary>
        /// 若目标文件此刻已在编辑器中打开，则立即为其弹出内嵌 Diff 预览。
        /// 已打开文档在 Agent 写穿后 buffer 已是新内容，这里用待处理块构建只读预览。
        /// 未打开 / 已有活跃会话 / 用户关闭了预览设置时静默跳过。
        /// </summary>
        public void TryAutoPreviewOpenFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            if (!(Settings.DeepSeekOptionsPage.Instance?.ShowDiffMarkersInEditor ?? true)) return;

            // Agent 在线程池线程写入，这里必须切回 UI 线程再创建宿主。
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    var view = FindWpfTextViewForFile(filePath);
                    if (view == null) return; // 未在 IDE 打开 → 不主动打开

                    Editing.StagedEditWorkspace? workspace;
                    lock (_reviewLock) { workspace = _reviewWorkspace; }
                    if (workspace == null || !workspace.HasTrackedFile(filePath)) return;

                    string baseline = workspace.BuildPendingOnlyBaseline(filePath);
                    string proposed = workspace.ReadFile(filePath);
                    if (string.Equals(baseline, proposed, StringComparison.Ordinal)) return;

                    // 同一文件再次被写入 → 原地刷新既有会话，保持预览与最新内容一致。
                    // 走防抖：密集写入（同一文件多次落盘 / 多步连续编辑）合并为一次重建，
                    // 否则每次都创建 IDifferenceBuffer + IWpfDifferenceViewer 会占满 UI 线程。
                    if (_sessionManager.TryGetSession(view.TextBuffer, out var existing) && existing != null)
                    {
                        SchedulePreviewRefresh(view.TextBuffer, filePath);
                        return;
                    }

                    CreateInlineDiffPreview(
                        view, baseline, proposed, textChanges: null, workspace: workspace);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[EditorDiff] 自动弹出 Diff 预览失败 ({Path.GetFileName(filePath)}): {ex.Message}");
                }
            });
        }

        private void DetachReviewWorkspaceLocked()
        {
            if (_reviewWorkspace != null)
            {
                _reviewWorkspace.FileTracked -= OnReviewFileTracked;
                _reviewWorkspace = null;
            }
            _reviewTurnUserMsgIndex = -1;
        }

        /// <summary>关闭属于指定 Workspace 的编辑器覆盖层并释放对应 Session。</summary>
        private void CloseReviewSessions(Editing.StagedEditWorkspace? workspace)
        {
            if (workspace == null) return;

            if (!ThreadHelper.CheckAccess())
            {
                // 宿主 Close 有 UI 线程亲和性，必须切回 UI 线程。
                // 这里用 RunAsync 而不是 Run：调用方可能正持有其它锁（例如会话切换路径），
                // 同步等待 UI 线程会与文档加载 / 编辑器写入互相阻塞，表现为「正在加载文档…」卡死。
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        CloseReviewSessionsCore(workspace);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[EditorDiff] 异步关闭预览失败: {ex.Message}");
                    }
                });
                return;
            }

            CloseReviewSessionsCore(workspace);
        }

        private void CloseReviewSessionsCore(Editing.StagedEditWorkspace workspace)
        {
            foreach (var session in _sessionManager.GetActiveSessions())
            {
                if (!ReferenceEquals(session.Workspace, workspace)) continue;
                if (session.SourceBuffer != null)
                    CancelPendingPreviewRefresh(session.SourceBuffer);
                try
                {
                    if (session.SourceBuffer != null)
                        CloseEditorHost(session.SourceBuffer);
                }
                catch (Exception ex) { Logger.Warn($"[EditorDiff] 关闭全部应用/撤销覆盖层失败: {ex.Message}"); }

                try { session.Dispose(); }
                catch (Exception ex) { Logger.Warn($"[EditorDiff] 释放全部应用/撤销会话失败: {ex.Message}"); }
            }
        }

        /// <summary>取消所有尚未落地的防抖刷新并释放在途定时器。</summary>
        private void CancelAllPendingPreviewRefreshes()
        {
            List<PendingPreviewRefresh> pending;
            lock (_editorHostsLock)
            {
                pending = new List<PendingPreviewRefresh>(_pendingPreviewRefreshes.Values);
                _pendingPreviewRefreshes.Clear();
            }

            foreach (var state in pending)
            {
                try { state.Timer?.Stop(); } catch { }
            }
        }

        /// <summary>
        /// 释放服务：取消在途防抖刷新、关闭所有编辑器覆盖层并停止会话追踪。
        /// 幂等；可从任意线程调用（关闭覆盖层有 UI 线程亲和性，这里自行切回）。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            CancelAllPendingPreviewRefreshes();

            if (!ThreadHelper.CheckAccess())
            {
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        DisposeCore();
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[EditorDiff] 异步释放失败: {ex.Message}");
                    }
                });
                return;
            }

            DisposeCore();
        }

        private void DisposeCore()
        {
            List<IEditorDiffHost> hosts;
            lock (_editorHostsLock)
            {
                hosts = new List<IEditorDiffHost>(_editorHosts.Values);
                _editorHosts.Clear();
            }

            foreach (var host in hosts)
            {
                try { host.Close(); }
                catch (Exception ex) { Logger.Warn($"[EditorDiff] 释放编辑器 Diff 宿主失败: {ex.Message}"); }
            }

            try { _sessionManager.Dispose(); }
            catch (Exception ex) { Logger.Warn($"[EditorDiff] 释放会话管理器失败: {ex.Message}"); }
        }

        /// <summary>
        /// 合并同一文件的连续预览刷新请求：防抖窗口内只重建一次 Diff 视图。
        /// 必须从 UI 线程调用（DispatcherTimer 有线程亲和性）。
        /// </summary>
        private void SchedulePreviewRefresh(ITextBuffer buffer, string filePath)
        {
            PendingPreviewRefresh state;
            lock (_editorHostsLock)
            {
                if (_disposed) return;

                if (!_pendingPreviewRefreshes.TryGetValue(buffer, out state!))
                {
                    state = new PendingPreviewRefresh();
                    state.Timer = new DispatcherTimer(
                        TimeSpan.FromMilliseconds(PreviewRefreshDebounceMs),
                        DispatcherPriority.Background,
                        OnPendingPreviewRefreshTick,
                        Dispatcher.CurrentDispatcher)
                    {
                        IsEnabled = false,
                    };
                    _pendingPreviewRefreshes[buffer] = state;
                }

                state.FilePath = filePath;
                state.Timer!.Stop();
                state.Timer.Start();
            }
        }

        /// <summary>取消某个 buffer 尚未落地的预览刷新（会话关闭 / 服务释放时调用）。</summary>
        private void CancelPendingPreviewRefresh(ITextBuffer buffer)
        {
            PendingPreviewRefresh? state = null;
            lock (_editorHostsLock)
            {
                if (_pendingPreviewRefreshes.TryGetValue(buffer, out state))
                {
                    _pendingPreviewRefreshes.Remove(buffer);
                }
            }

            try { state?.Timer?.Stop(); } catch { }
        }

        private void OnPendingPreviewRefreshTick(object? sender, EventArgs e)
        {
            if (sender is not DispatcherTimer timer)
            {
                return;
            }

            ITextBuffer? buffer = null;
            string filePath = string.Empty;
            lock (_editorHostsLock)
            {
                // 先定位该定时器对应的 buffer，再在枚举结束后移除条目
                // （在 foreach 中直接 Remove 依赖「立刻 break」，太隐晦）。
                foreach (var pair in _pendingPreviewRefreshes)
                {
                    if (!ReferenceEquals(pair.Value.Timer, timer))
                    {
                        continue;
                    }

                    buffer = pair.Key;
                    filePath = pair.Value.FilePath;
                    break;
                }

                if (buffer != null)
                    _pendingPreviewRefreshes.Remove(buffer);
            }

            try { timer.Stop(); } catch { }

            if (_disposed || buffer == null)
            {
                return;
            }

            // ── 落盘后再让预览跟上最新内容：只刷新仍存在且属于当前 Workspace 的会话 ──
            InlineDiffSession? session = null;
            Editing.StagedEditWorkspace? workspace;
            lock (_reviewLock) { workspace = _reviewWorkspace; }

            if (workspace == null || !_sessionManager.TryGetSession(buffer, out session) || session == null)
            {
                return;
            }

            if (!workspace.HasTrackedFile(filePath))
            {
                return;
            }

            try
            {
                string baseline = workspace.BuildPendingOnlyBaseline(filePath);
                string proposed = workspace.ReadFile(filePath);
                if (string.Equals(baseline, proposed, StringComparison.Ordinal))
                {
                    return;
                }

                RefreshWriteThroughSession(
                    session,
                    BuildChangeSet(buffer.ContentType.TypeName, buffer, filePath, baseline, proposed),
                    workspace,
                    buffer);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditorDiff] 防抖刷新 Diff 预览失败 ({Path.GetFileName(filePath)}): {ex.Message}");
            }
        }

        private void RaisePendingChangesChanged()
        {
            try { PendingChangesChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { Logger.Warn($"[EditorDiff] PendingChangesChanged 订阅异常: {ex.Message}"); }
        }

        private static IWpfTextView? FindWpfTextViewForFile(string filePath)
            => ToolWindows.TerminalWindowHelper.FindWpfTextViewForFile(filePath);

        private static async Task<IWpfTextView?> OpenDocumentAndGetViewAsync(string filePath)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                var openDoc = (Microsoft.VisualStudio.Shell.Interop.IVsUIShellOpenDocument?)
                    Package.GetGlobalService(typeof(Microsoft.VisualStudio.Shell.Interop.SVsUIShellOpenDocument));
                if (openDoc == null) return null;

                var logicalView = Microsoft.VisualStudio.VSConstants.LOGVIEWID_Primary;
                openDoc.OpenDocumentViaProject(
                    filePath, ref logicalView, out _, out _, out _,
                    out Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame frame);
                frame?.Show();

                // 等待编辑器首帧装载后再取视图
                for (int i = 0; i < 20; i++)
                {
                    var view = FindWpfTextViewForFile(filePath);
                    if (view != null) return view;
                    await Task.Delay(100);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditorDiff] 打开文档并获取视图失败 ({filePath}): {ex.Message}");
            }
            return null;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 从 ITextBuffer 获取关联的文件路径。
        /// </summary>
        private static string? GetFilePathFromBuffer(ITextBuffer buffer)
        {
            if (buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument textDoc))
                return textDoc.FilePath;
            return null;
        }

        /// <summary>
        /// 关闭编辑器内嵌 Diff 宿主（从 adornment 层移除）。
        /// </summary>
        private void CloseEditorHost(ITextBuffer buffer)
        {
            lock (_editorHostsLock)
            {
                if (_editorHosts.TryGetValue(buffer, out var host))
                {
                    host.Close();
                    _editorHosts.Remove(buffer);
                }
            }
        }

        /// <summary>
        /// 确保指定 buffer 的覆盖层仍在可视树上（标签切回 / 再次点击条目时调用）。
        /// </summary>
        private void EnsureEditorHostOverlay(ITextBuffer buffer)
        {
            try
            {
                lock (_editorHostsLock)
                {
                    if (_editorHosts.TryGetValue(buffer, out var host))
                        host.EnsureOverlayVisible();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EditorDiff] 重新挂载 Diff 覆盖层失败: {ex.Message}");
            }
        }

        #endregion
    }
}
