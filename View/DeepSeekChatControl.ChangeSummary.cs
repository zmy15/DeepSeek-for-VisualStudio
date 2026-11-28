using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// 文件改动列表（聊天区与状态行之间）。
    ///
    /// 数据来源是 <see cref="EditorDiffMarkerService"/> 当前登记的待处理 Workspace：
    /// Agent 每改完一个文件就会把该文件登记进来，因此列表在编辑过程中即时出现，
    /// 而不是等整轮结束。用户在 Diff 预览里逐块「保留 / 撤销」，或点击
    /// 「全部应用 / 全部撤销」后，对应文件会从列表移除；全部处理完列表自动隐藏。
    /// </summary>
    public partial class DeepSeekChatControl
    {
        /// <summary>每个文件一条的展示模型。</summary>
        private sealed class ChangeFileRow
        {
            public string FilePath { get; init; } = string.Empty;
            public string FileName { get; init; } = string.Empty;
            public int LinesAdded { get; init; }
            public int LinesRemoved { get; init; }
            public string AddedText => LinesAdded > 0 ? $"+{LinesAdded}" : string.Empty;
            public string RemovedText => LinesRemoved > 0 ? $"-{LinesRemoved}" : string.Empty;
        }

        private bool _changeSummaryExpanded;
        private bool _changeSummarySubscribed;
        private int _changeSummaryRefreshVersion;

        /// <summary>订阅待处理改动变更事件（控件初始化时调用一次）。</summary>
        private void SubscribeChangeSummary()
        {
            if (_changeSummarySubscribed) return;
            _changeSummarySubscribed = true;
            EditorDiffMarkerService.Instance.PendingChangesChanged += OnPendingChangesChanged;
        }

        /// <summary>退订待处理改动变更事件（控件释放时调用）。</summary>
        private void UnsubscribeChangeSummary()
        {
            if (!_changeSummarySubscribed) return;
            _changeSummarySubscribed = false;
            try { EditorDiffMarkerService.Instance.PendingChangesChanged -= OnPendingChangesChanged; }
            catch { }
        }

        private void OnPendingChangesChanged(object? sender, EventArgs e) => RefreshChangeSummary();

        /// <summary>
        /// 会话上下文真正发生变化时清理上一会话的待处理追踪。
        /// WebView 重绘、主题切换等纯渲染动作不得调用本方法。
        /// </summary>
        private void ResetFileChangeTrackingForConversationSwitch()
        {
            EditorDiffMarkerService.Instance.ClearPendingTracking();
            // ClearPendingTracking 会触发 PendingChangesChanged；仅在未订阅时手动兜底。
            if (!_changeSummarySubscribed)
                RefreshChangeSummary();
        }

        /// <summary>
        /// 重新计算并刷新文件改动列表。可从任意线程调用；WPF 操作会切回 UI 线程。
        ///
        /// 行数据的计算（读盘 + diff）放到线程池执行：编辑过程中每次文件写入与
        /// 每次逐块操作都会触发刷新，若在 UI 线程同步读盘 / 跑 diff，会长时间占住
        /// UI 线程，导致此时在 IDE 打开文档卡在「正在加载文档…」。
        /// </summary>
        private void RefreshChangeSummary()
        {
            if (_disposed) return;

            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(new Action(RefreshChangeSummary));
                return;
            }

            if (ChangeSummaryArea == null) return;

            // 只应用最新一次结果：连续的变更事件（多文件连续写入）无需排队重算。
            int version = ++_changeSummaryRefreshVersion;
            _ = Task.Run(() =>
            {
                List<ChangeFileRow> rows;
                try
                {
                    rows = BuildChangeFileRows();
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[ChangeSummary] 计算文件改动列表失败: {ex.Message}");
                    rows = new List<ChangeFileRow>();
                }

                if (_disposed) return;
                try
                {
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_disposed || version != _changeSummaryRefreshVersion) return;
                        ApplyChangeSummaryRows(rows);
                    }));
                }
                catch (Exception ex)
                {
                    // 控件已卸载 / Dispatcher 已关闭：忽略这次刷新
                    Logger.Debug($"[ChangeSummary] 刷新调度失败: {ex.GetType().Name}");
                }
            });
        }

        /// <summary>把后台算好的行数据应用到 WPF 控件（必须在 UI 线程调用）。</summary>
        private void ApplyChangeSummaryRows(List<ChangeFileRow> files)
        {
            if (ChangeSummaryArea == null) return;

            if (files.Count == 0)
            {
                ChangeSummaryArea.Visibility = Visibility.Collapsed;
                ChangeFileListBox.ItemsSource = null;
                return;
            }

            int totalAdded = 0;
            int totalRemoved = 0;
            foreach (var f in files)
            {
                totalAdded += f.LinesAdded;
                totalRemoved += f.LinesRemoved;
            }

            ChangeSummaryArea.Visibility = Visibility.Visible;
            ChangeSummaryTitle.Text = string.Format(
                LocalizationService.Instance["changeSummary.files"], files.Count);
            ChangeSummaryAdded.Text = totalAdded > 0 ? $"+{totalAdded}" : string.Empty;
            ChangeSummaryRemoved.Text = totalRemoved > 0 ? $"-{totalRemoved}" : string.Empty;
            ApplyChangeSummaryExpandedState();

            ChangeFileListBox.ItemsSource = files;
        }

        /// <summary>
        /// 从待处理 Workspace 读取仍有未处理差异的文件及其真实增删行数。
        /// 只保留未保留 / 未撤销的块，因此用户在 Diff 里处理完一个文件，
        /// 该文件（或它的剩余计数）会实时从列表消失 / 收敛。
        /// </summary>
        private List<ChangeFileRow> BuildChangeFileRows()
        {
            var rows = new List<ChangeFileRow>();
            var service = EditorDiffMarkerService.Instance;

            foreach (var path in service.GetPendingFilePaths())
            {
                if (string.IsNullOrWhiteSpace(path)) continue;

                var stats = service.GetPendingLineStats(path);
                if (stats.Added <= 0 && stats.Removed <= 0) continue;

                rows.Add(new ChangeFileRow
                {
                    FilePath = path,
                    FileName = Path.GetFileName(path),
                    LinesAdded = stats.Added,
                    LinesRemoved = stats.Removed,
                });
            }

            rows.Sort((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase));
            return rows;
        }

        /// <summary>按展开状态同步明细列表与箭头方向。</summary>
        private void ApplyChangeSummaryExpandedState()
        {
            if (ChangeFileListBox != null)
                ChangeFileListBox.Visibility = _changeSummaryExpanded ? Visibility.Visible : Visibility.Collapsed;
            if (ChangeSummaryChevron != null)
                ChangeSummaryChevron.Text = _changeSummaryExpanded ? "\uE70D" : "\uE76C";
        }

        /// <summary>
        /// 清除某轮登记的文件改动（该轮改动已被回退时调用）。
        /// 注意：这里只清理随消息持久化的历史记录；列表本身由待处理 Workspace 驱动。
        /// </summary>
        private void ClearRecordedFileChanges(int userMsgIndex)
        {
            lock (_lock)
            {
                if (userMsgIndex >= 0 && userMsgIndex < _messages.Count)
                    _messages[userMsgIndex].ChangedFiles = new List<FileChangeSummary>();
            }
        }

        private void ChangeSummaryHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // 点击「全部应用 / 全部撤销」按钮时不要连带展开 / 收起。
            if (IsWithinButton(e.OriginalSource as DependencyObject)) return;

            _changeSummaryExpanded = !_changeSummaryExpanded;
            ApplyChangeSummaryExpandedState();
        }

        private static bool IsWithinButton(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is Button) return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private async void ChangeFileListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ChangeFileListBox.SelectedItem is not ChangeFileRow row) return;

            // 立即清空选中，避免同一行再次点击无响应
            ChangeFileListBox.SelectedItem = null;

            if (string.IsNullOrWhiteSpace(row.FilePath)) return;

            // ── 先打开 / 聚焦文件，再确保该文件的内嵌 Diff 预览已弹出 ──
            // 逐块保留 / 撤销完成后，Diff 宿主会通知待处理集合刷新，
            // 列表随之移除已经全部处理完的文件。
            try
            {
                await NavigateToFileAsync(row.FilePath);
                await EditorDiffMarkerService.Instance.OpenDiffPreviewForFileAsync(row.FilePath);
            }
            catch (Exception ex)
            {
                // async void 事件没有上层 Task 边界；这里必须兜住，
                // 否则文件已删除或 VS 编辑器状态变化会直接进入未处理异常通道。
                Logger.Warn($"[ChangeSummary] 打开文件预览失败 ({row.FilePath}): {ex.Message}");
            }
        }

        private void ChangeSummaryApplyAllButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            try
            {
                EditorDiffMarkerService.Instance.ApplyAllPending();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[ChangeSummary] 全部应用失败: {ex.Message}");
            }
            RefreshChangeSummary();
        }

        private void ChangeSummaryRevertAllButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            try
            {
                EditorDiffMarkerService.Instance.RevertAllPending();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[ChangeSummary] 全部撤销失败: {ex.Message}");
            }
            RefreshChangeSummary();
        }
    }
}
