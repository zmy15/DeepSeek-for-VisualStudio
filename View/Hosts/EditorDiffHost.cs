using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Controls.Primitives;

namespace DeepSeek_v4_for_VisualStudio.View.Hosts
{
    /// <summary>
    /// 编辑器内嵌 Diff 宿主。将 InlineDiffHostControl（含 VS 原生 IWpfDifferenceViewer）
    /// 通过 WPF Adorner 直接覆盖在编辑器 VisualElement 上，替代浮动窗口，实现 Copilot 风格的
    /// 内联 diff 预览：同时显示原始代码和修改后代码，自动红绿着色，支持逐块撤销/保留。
    ///
    /// Adorner 与被装饰元素（编辑器）天然同位置同尺寸，避免手动计算 viewport 坐标导致的错位。
    /// </summary>
    public sealed class EditorDiffHost : IDiffHost, IEditorDiffHost
    {
        /// <summary>
        /// 兜底 adornment 层名（仅当 AdornerLayer 不可用时使用），
        /// 在 DiffPreviewAdornmentFactory 以 MEF AdornmentLayerDefinition 注册。
        /// </summary>
        public const string OverlayLayerName = "DeepSeekEditorDiffOverlay";

        private readonly IWpfTextView _textView;
        private InlineDiffHostControl? _hostControl;
        private EditorOverlayAdorner? _adorner;
        private IAdornmentLayer? _fallbackLayer;
        private Border? _fallbackContainer;
        private bool _isShown;
        private bool _loadedHandlerAttached;
        private bool _unloadedHandlerAttached;
        private bool _visibilityHandlerAttached;
        private bool _overlayRetryHandlerAttached;

        /// <summary>宿主关闭后触发（供服务清理 _editorHosts 索引）。</summary>
        public event EventHandler? Closed;

        public EditorDiffHost(IWpfTextView textView)
        {
            _textView = textView ?? throw new ArgumentNullException(nameof(textView));
        }

        public void Show(InlineDiffSession session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_isShown) Close();

            _hostControl = new InlineDiffHostControl();
            _hostControl.SetViewerHandle(session.ViewerHandle);
            // diff 各子视图的缩放跟随宿主编辑器页面的 ZoomLevel
            // 必须在 SetViewerHandle 之后赋值：控件在注入 viewer 时才会应用缩放，
            // 先设 HostTextView 会因为 _viewer 仍为空而丢掉首次同步。
            _hostControl.HostTextView = _textView;

            _hostControl.OnAccept = () => FinishSession(session);

            _hostControl.OnUndo = () =>
            {
                session.Dismiss();
                Close();
                EditorDiffMarkerService.Instance.NotifyPendingChanged();
            };

            WireHunkHandlers(session);

            _isShown = true;
            AttachHostOverlay();
            Logger.Info($"[EditorDiffHost] Inline diff shown: {session.SessionId.Substring(0, 8)}");
        }

        public void Activate() { }

        /// <summary>
        /// 确保覆盖层处于挂载状态（点列表条目重新打开、标签切回时调用）。
        /// 若 Adorner 已被 WPF 随可视树重载丢弃，则重新挂载；
        /// 仍挂在当前 AdornerLayer 上时保持不变，避免闪烁。
        /// </summary>
        public void EnsureOverlayVisible()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_isShown || _hostControl == null) return;

            var visualElement = _textView.VisualElement;
            if (visualElement == null) return;

            if (_adorner != null)
            {
                var layer = AdornerLayer.GetAdornerLayer(visualElement);
                var adorners = layer?.GetAdorners(visualElement);
                if (adorners != null)
                {
                    foreach (var adorner in adorners)
                    {
                        if (ReferenceEquals(adorner, _adorner)) return; // 仍在层里
                    }
                }
            }
            else if (_fallbackLayer != null && _fallbackContainer != null)
            {
                return; // fallback 覆盖层仍在
            }

            ClearHostOverlay();
            AttachHostOverlay();
        }

        /// <summary>
        /// 绑定 hunk 撤销/保留回调并装载当前待处理块。
        /// 回调内通过 session.Workspace 动态解析，Workspace 刷新后仍然有效。
        /// </summary>
        private void WireHunkHandlers(InlineDiffSession session)
        {
            if (_hostControl == null || session.Workspace == null) return;

            var hunks = session.Workspace.GetHunks(session.Change.FilePath);
            _hostControl.SetHunks(hunks, session.Change.FilePath);

            _hostControl.OnRevertHunk = hunkIndex =>
            {
                if (!session.Workspace!.RestoreSingleHunk(session.Change.FilePath, hunkIndex))
                    return;

                Logger.Info($"[EditorDiffHost] Reverted hunk [{hunkIndex}]: {System.IO.Path.GetFileName(session.Change.FilePath)}");

                // ── 所有块均已处理（保留/撤销）→ 确认并自动关闭 ──
                if (!session.Workspace.HasPendingHunks(session.Change.FilePath))
                {
                    FinishSession(session);
                    return;
                }

                EditorDiffMarkerService.Instance.NotifyPendingChanged();
                RebuildViewerForPendingHunks(session);
            };

            _hostControl.OnKeepHunk = hunkIndex =>
            {
                if (!session.Workspace!.AcceptSingleHunk(session.Change.FilePath, hunkIndex))
                    return;

                Logger.Info($"[EditorDiffHost] Kept hunk [{hunkIndex}]: {System.IO.Path.GetFileName(session.Change.FilePath)}");

                // ── 所有块均已处理（保留/撤销）→ 确认并自动关闭 ──
                if (!session.Workspace.HasPendingHunks(session.Change.FilePath))
                {
                    FinishSession(session);
                    return;
                }

                EditorDiffMarkerService.Instance.NotifyPendingChanged();
                RebuildViewerForPendingHunks(session);
            };
        }

        /// <summary>
        /// 原地刷新会话（AI 在预览期间再次编辑同一文件时调用）：
        /// 重建只读 Diff 视图并刷新 hunk 按钮，预览立即反映最新编辑。
        /// </summary>
        public void RefreshSession(InlineDiffSession session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_hostControl == null) return;

            try
            {
                WireHunkHandlers(session);

                var oldHandle = session.ViewerHandle;
                session.ReplaceViewerHandle(
                    DiffViewerService.Instance.CreateReadOnlyPreview(
                        session.Change.BaselineText,
                        session.Change.ProposedText,
                        session.Change.ContentTypeName,
                        DifferenceViewMode.Inline));
                _hostControl.SetViewerHandle(session.ViewerHandle);
                try { oldHandle.Dispose(); } catch { /* 旧视图释放失败不影响刷新 */ }
            }
            catch (Exception ex)
            {
                Logger.Error($"[EditorDiffHost] 刷新 Diff 视图失败: {ex.Message}", ex);
            }
        }

        private void AttachHostOverlay()
        {
            var visualElement = _textView.VisualElement;

            // 无论如何都武装 editor 生命周期回调：
            // 切换文档标签时 VS 会卸载 / 重新加载编辑器的可视树，
            // Adorner 随宿主一起被移除；切回来必须能重新挂载，否则 diff 预览会消失。
            EnsureLifecycleHandlersAttached(visualElement);

            var adornerLayer = AdornerLayer.GetAdornerLayer(visualElement);
            if (adornerLayer != null)
            {
                _adorner = new EditorOverlayAdorner(visualElement, _hostControl!);
                adornerLayer.Add(_adorner);
                DetachOverlayRetryHandlers(visualElement);
                return;
            }

            visualElement.LayoutUpdated += OnVisualElementLayoutUpdated;
            _overlayRetryHandlerAttached = true;

            if (!visualElement.IsLoaded)
            {
                return;
            }

            ShowFallbackOverlay();
        }

        /// <summary>
        /// 武装编辑器生命周期回调（幂等）：Loaded / Unloaded / IsVisibleChanged。
        /// 标签切换会触发 Unloaded → Loaded，切回时必须重新挂载覆盖层。
        /// </summary>
        private void EnsureLifecycleHandlersAttached(FrameworkElement visualElement)
        {
            if (!_loadedHandlerAttached)
            {
                visualElement.Loaded += OnVisualElementLoaded;
                _loadedHandlerAttached = true;
            }
            if (!_unloadedHandlerAttached)
            {
                visualElement.Unloaded += OnVisualElementUnloaded;
                _unloadedHandlerAttached = true;
            }
            if (!_visibilityHandlerAttached)
            {
                visualElement.IsVisibleChanged += OnVisualElementIsVisibleChanged;
                _visibilityHandlerAttached = true;
            }
        }

        private void OnVisualElementLoaded(object sender, RoutedEventArgs e)
        {
            if (!_isShown) return;

            // 文档标签切回 → 可视树重新加载：清掉旧引用并重新挂载覆盖层。
            ClearHostOverlay();
            AttachHostOverlay();
        }

        /// <summary>
        /// 文档标签切走时 VS 会卸载编辑器可视树，Adorner 随之失效。
        /// 这里只清理已失效的挂载引用，保留 _hostControl 与 _isShown，
        /// 等 Loaded / IsVisibleChanged 时再重新挂载。
        /// </summary>
        private void OnVisualElementUnloaded(object sender, RoutedEventArgs e)
        {
            if (!_isShown) return;
            ClearHostOverlay();
        }

        /// <summary>
        /// 某些 VS 版本切标签只改可见性、不触发 Unloaded/Loaded：
        /// 重新可见且当前没有挂载时补挂一次。
        /// </summary>
        private void OnVisualElementIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!_isShown) return;
            if (e.NewValue is not bool visible || !visible) return;
            if (_adorner != null || _fallbackLayer != null) return;

            ClearHostOverlay();
            AttachHostOverlay();
        }

        private void OnVisualElementLayoutUpdated(object? sender, EventArgs e)
        {
            if (!_overlayRetryHandlerAttached) return;

            var visualElement = _textView.VisualElement;
            visualElement.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (!_isShown || !_overlayRetryHandlerAttached) return;
                    if (AdornerLayer.GetAdornerLayer(visualElement) == null) return;

                    ClearHostOverlay();
                    AttachHostOverlay();
                }),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ClearHostOverlay()
        {
            if (_adorner != null)
            {
                var adornerLayer = AdornerLayer.GetAdornerLayer(_textView.VisualElement);
                _adorner.Detach();
                adornerLayer?.Remove(_adorner);
                _adorner.DetachChild();
                _adorner = null;
            }

            if (_fallbackLayer != null)
            {
                if (_textView.VisualElement is FrameworkElement frameworkElement)
                    frameworkElement.SizeChanged -= OnFallbackSizeChanged;
                _textView.LayoutChanged -= OnHostLayoutChanged;
                _fallbackContainer.Child = null;
                _fallbackLayer.RemoveAllAdornments();
                _fallbackLayer = null;
                _fallbackContainer = null;
            }
        }

        private void ShowFallbackOverlay()
        {
            _fallbackLayer = _textView.GetAdornmentLayer(OverlayLayerName);
            _fallbackContainer = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
                Child = _hostControl,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0),
                Padding = new Thickness(0),
            };
            _hostControl!.HorizontalAlignment = HorizontalAlignment.Stretch;
            _hostControl.VerticalAlignment = VerticalAlignment.Stretch;

            if (_textView.VisualElement is FrameworkElement frameworkElement)
            {
                frameworkElement.SizeChanged += OnFallbackSizeChanged;
                _textView.LayoutChanged += OnHostLayoutChanged;
                UpdateFallbackOverlayBounds(frameworkElement);
            }
            else
            {
                _fallbackContainer.Width = _textView.ViewportWidth;
                _fallbackContainer.Height = _textView.ViewportHeight;
                Canvas.SetLeft(_fallbackContainer, 0);
                Canvas.SetTop(_fallbackContainer, 0);
            }

            _fallbackLayer.AddAdornment(
                AdornmentPositioningBehavior.ViewportRelative,
                null, null, _fallbackContainer, null);
        }

        private void OnFallbackSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is FrameworkElement frameworkElement)
                UpdateFallbackOverlayBounds(frameworkElement);
        }

        private void OnHostLayoutChanged(object? sender, TextViewLayoutChangedEventArgs e)
        {
            if (_textView.VisualElement is FrameworkElement frameworkElement)
                UpdateFallbackOverlayBounds(frameworkElement);
        }

        private void UpdateFallbackOverlayBounds(FrameworkElement visualElement)
        {
            if (_fallbackContainer == null || visualElement.ActualWidth <= 0 || visualElement.ActualHeight <= 0)
                return;

            double verticalScrollBarWidth = 0;
            double horizontalScrollBarHeight = 0;
            CollectScrollBarSizes(
                visualElement,
                ref verticalScrollBarWidth,
                ref horizontalScrollBarHeight);

            _fallbackContainer.Width = visualElement.ActualWidth;
            _fallbackContainer.Height = visualElement.ActualHeight;

            // ViewportRelative 的原点在视口左上；补偿行号区与滚动条，才能覆盖整个编辑器视觉元素。
            double leftReserve = Math.Max(
                0,
                visualElement.ActualWidth - _textView.ViewportWidth - verticalScrollBarWidth);
            double topReserve = Math.Max(
                0,
                visualElement.ActualHeight - _textView.ViewportHeight - horizontalScrollBarHeight);
            Canvas.SetLeft(_fallbackContainer, -leftReserve);
            Canvas.SetTop(_fallbackContainer, -topReserve);
        }

        private static void CollectScrollBarSizes(
            DependencyObject element,
            ref double verticalScrollBarWidth,
            ref double horizontalScrollBarHeight)
        {
            if (element == null) return;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            {
                var child = VisualTreeHelper.GetChild(element, i);
                if (child is ScrollBar scrollBar && scrollBar.Visibility == Visibility.Visible)
                {
                    if (scrollBar.Orientation == Orientation.Vertical)
                        verticalScrollBarWidth = Math.Max(verticalScrollBarWidth, scrollBar.ActualWidth);
                    else
                        horizontalScrollBarHeight = Math.Max(horizontalScrollBarHeight, scrollBar.ActualHeight);
                }

                CollectScrollBarSizes(
                    child,
                    ref verticalScrollBarWidth,
                    ref horizontalScrollBarHeight);
            }
        }

        /// <summary>
        /// 结束会话：写穿模式确认磁盘内容（清除撤销追踪），
        /// 普通模式走提交流程；随后关闭编辑器覆盖层。
        /// </summary>
        private void FinishSession(InlineDiffSession session)
        {
            if (session.Workspace != null)
                session.ConfirmWriteThrough();
            else
                _ = session.CommitAsync(System.Threading.CancellationToken.None);
            Close();
            EditorDiffMarkerService.Instance.NotifyPendingChanged();
        }

        /// <summary>
        /// 逐块保留/撤销后重建 Diff 视图。
        /// 左侧使用「仅含待处理块」的显示基线（已保留块并入新基线），
        /// 因此已保留/已撤销的块不再红绿高亮，视图中只剩待处理块及其按钮。
        /// </summary>
        private void RebuildViewerForPendingHunks(InlineDiffSession session)
        {
            try
            {
                var workspace = session.Workspace!;
                var updated = workspace.GetHunks(session.Change.FilePath);
                _hostControl!.RefreshHunks(updated, session.Change.FilePath);

                string currentContent = workspace.ReadFile(session.Change.FilePath);
                string displayBaseline = workspace.BuildPendingOnlyBaseline(session.Change.FilePath);

                var oldHandle = session.ViewerHandle;
                session.ReplaceViewerHandle(
                    DiffViewerService.Instance.CreateReadOnlyPreview(
                        displayBaseline, currentContent, session.Change.ContentTypeName));
                _hostControl.SetViewerHandle(session.ViewerHandle);
                try { oldHandle.Dispose(); } catch { }
            }
            catch (Exception ex)
            {
                Logger.Error($"[EditorDiffHost] 重建 Diff 视图失败: {ex.Message}", ex);
            }
        }

        public void Close()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_isShown) return;

            if (_adorner != null)
            {
                var adornerLayer = AdornerLayer.GetAdornerLayer(_textView.VisualElement);
                _adorner.Detach();
                adornerLayer?.Remove(_adorner);
                _adorner.DetachChild();
                _adorner = null;
            }

            if (_loadedHandlerAttached)
            {
                _textView.VisualElement.Loaded -= OnVisualElementLoaded;
                _loadedHandlerAttached = false;
            }

            if (_unloadedHandlerAttached)
            {
                _textView.VisualElement.Unloaded -= OnVisualElementUnloaded;
                _unloadedHandlerAttached = false;
            }

            if (_visibilityHandlerAttached)
            {
                _textView.VisualElement.IsVisibleChanged -= OnVisualElementIsVisibleChanged;
                _visibilityHandlerAttached = false;
            }

            DetachOverlayRetryHandlers(_textView.VisualElement);

            if (_fallbackLayer != null)
            {
                if (_textView.VisualElement is FrameworkElement frameworkElement)
                    frameworkElement.SizeChanged -= OnFallbackSizeChanged;
                _textView.LayoutChanged -= OnHostLayoutChanged;
                _fallbackContainer.Child = null;
                _fallbackLayer.RemoveAllAdornments();
                _fallbackLayer = null;
                _fallbackContainer = null;
            }

            _hostControl?.DisposeZoomSync();
            _hostControl?.DetachViewer();
            _hostControl = null;
            _isShown = false;
            Logger.Info("[EditorDiffHost] Inline diff closed");
            try { Closed?.Invoke(this, EventArgs.Empty); } catch { }
        }

        private void DetachOverlayRetryHandlers(System.Windows.UIElement visualElement)
        {
            visualElement.LayoutUpdated -= OnVisualElementLayoutUpdated;
            _overlayRetryHandlerAttached = false;
        }
    }

    /// <summary>
    /// 覆盖整个被装饰元素（编辑器）的 Adorner，子元素填满编辑器区域。
    /// </summary>
    internal sealed class EditorOverlayAdorner : Adorner
    {
        private readonly UIElement _child;
        private FrameworkElement? _sizeSource;
        private bool _childAttached;

        public EditorOverlayAdorner(UIElement adornedElement, UIElement child)
            : base(adornedElement)
        {
            _child = child ?? throw new ArgumentNullException(nameof(child));
            AddVisualChild(child);
            AddLogicalChild(child);
            _childAttached = true;

            if (AdornedElement is FrameworkElement frameworkElement)
            {
                _sizeSource = frameworkElement;
                frameworkElement.SizeChanged += OnAdornedSizeChanged;
            }
        }

        public void Detach()
        {
            if (_sizeSource == null) return;

            _sizeSource.SizeChanged -= OnAdornedSizeChanged;
            _sizeSource = null;
        }

        public void DetachChild()
        {
            if (!_childAttached) return;

            RemoveVisualChild(_child);
            RemoveLogicalChild(_child);
            _childAttached = false;
        }

        protected override int VisualChildrenCount => _childAttached ? 1 : 0;

        protected override Visual GetVisualChild(int index) => _child;

        // 始终以被装饰元素（编辑器）的实际渲染尺寸为准，忽略外部约束，
        // 避免覆盖层被测量成整个窗口尺寸而溢出编辑器边界。
        protected override Size MeasureOverride(Size constraint)
        {
            var size = AdornedElement?.RenderSize ?? _child.DesiredSize;
            _child.Measure(size);
            return size;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var size = AdornedElement?.RenderSize ?? finalSize;
            _child.Arrange(new Rect(0, 0, size.Width, size.Height));
            return size;
        }

        private void OnAdornedSizeChanged(object sender, SizeChangedEventArgs e)
        {
            InvalidateMeasure();
            InvalidateArrange();
        }

        protected override void OnRender(DrawingContext drawingContext) { }
    }
}
