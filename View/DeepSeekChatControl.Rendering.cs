using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// WebView2 渲染相关方法：增量/全量页面刷新、流式更新、HTML 构建。
    /// </summary>
    public partial class DeepSeekChatControl
    {
        private bool _suppressWebViewZoomPersistence;
        /// <summary>进行中的 WebView2 重建任务；用于合并并发重建请求，null 表示当前没有重建在进行。</summary>
        private Task<bool>? _webViewRebuildTask;
        /// <summary>已告警失效的 WebView2 控件实例哈希码；null 表示尚未告警（重建成功后重置）。</summary>
        private int? _webViewGoneLoggedInstance;

        #region Private Methods - Rendering

        /// <summary>
        /// 探测 WebView2 控件当前是否仍可安全访问。
        /// <para>
        /// 控件被 WPF 视觉树卸载（关闭工具窗口 / VS 恢复布局 / 切换解决方案）后，
        /// WebView2 会释放底层 <c>CoreWebView2</c>；此后访问 <c>CoreWebView2</c> 属性或其成员
        /// 会抛 <see cref="InvalidOperationException"/>（内层 COMException 0x8007139F），
        /// 因此不能用「属性是否为 null」来判断控件是否还活着。
        /// </para>
        /// </summary>
        /// <param name="core">探测到的可用 <see cref="CoreWebView2"/> 实例；控件不可用时为 null。</param>
        /// <returns>控件及其浏览器实例均可用时返回 true，否则返回 false。</returns>
        private bool TryGetLiveCoreWebView(out CoreWebView2? core)
        {
            core = null;

            // 宿主控件自身已被释放，后续一切访问都不安全
            if (_disposed)
                return false;

            var webView = ChatWebView;
            if (webView == null)
            {
                LogWebViewGoneOnce(webView, "ChatWebView 字段为 null");
                return false;
            }

            try
            {
                // 已从视觉树摘除的控件不再持有可用的浏览器实例。
                // 该判据用于兜住「CoreWebView2 返回失效缓存实例而不抛异常」的情况。
                if (webView.Parent == null)
                {
                    LogWebViewGoneOnce(webView, "控件已脱离视觉树 (Parent == null)");
                    return false;
                }

                var current = webView.CoreWebView2;
                if (current == null)
                    return false;

                core = current;
                return true;
            }
            catch (InvalidOperationException ex)
            {
                // 典型消息：CoreWebView2 members cannot be accessed after the WebView2 control is disposed.
                // 注：ObjectDisposedException 派生自 InvalidOperationException，同样由此分支覆盖。
                LogWebViewGoneOnce(webView, $"访问 CoreWebView2 抛出 {ex.GetType().Name}");
                return false;
            }
            catch (COMException ex)
            {
                // HRESULT 0x8007139F：组或资源状态不正确（浏览器进程/环境已不在可用状态）
                LogWebViewGoneOnce(webView, $"COMException 0x{ex.HResult:X8}");
                return false;
            }
        }

        /// <summary>
        /// 控件失效诊断日志（带去重）。
        /// 同一控件实例只在首次判定失效时以 Warn 记录，后续重复判定降为 Debug，
        /// 避免流式渲染期间刷出数百条日志掩盖真实问题。
        /// </summary>
        /// <param name="webView">失效的 WebView2 控件；可为 null。</param>
        /// <param name="reason">失效判据说明。</param>
        private void LogWebViewGoneOnce(Microsoft.Web.WebView2.Wpf.WebView2CompositionControl? webView, string reason)
        {
            int instanceId = webView == null ? 0 : webView.GetHashCode();

            if (_webViewGoneLoggedInstance == instanceId)
            {
                Logger.Debug($"[WebViewLifecycle] 控件仍处于失效状态: {reason}");
                return;
            }

            _webViewGoneLoggedInstance = instanceId;
            Logger.Warn($"[WebViewLifecycle] webview.gone | view={instanceId} | reason={reason} | hostContent={(ChatWebViewHost.Content == null ? "null(已摘除)" : "存在")} | controlLoaded={IsLoaded} | controlVisible={IsVisible}");
        }

        /// <summary>
        /// 判断异常是否属于「WebView2 控件已失效」这一类。
        /// 控件被视觉树卸载后这类异常必然出现，属预期情况，不能用 Error 级别刷屏。
        /// </summary>
        /// <param name="ex">待判定的异常。</param>
        /// <returns>属于控件失效类异常返回 true，否则返回 false。</returns>
        private static bool IsWebViewGoneException(Exception ex)
        {
            if (ex is ObjectDisposedException)
                return true;

            if (ex is InvalidOperationException)
                return true;

            // HRESULT 0x8007139F：组或资源状态不正确（浏览器环境已随控件释放）
            if (ex is COMException comEx && comEx.HResult == unchecked((int)0x8007139F))
                return true;

            return false;
        }

        /// <summary>
        /// 记录 WebView2 相关控件的视觉树生命周期事件。
        /// 用途：定位「控件何时、由哪个事件被卸出视觉树」——控件一旦脱离视觉树，
        /// WebView2 会释放底层 CoreWebView2（HRESULT 0x8007139F），这是界面变白屏的根因线索。
        /// </summary>
        /// <param name="stage">阶段标识（如 webview.unloaded）。</param>
        /// <param name="source">触发事件的控件；可为 null。</param>
        private void LogWebViewLifecycle(string stage, DependencyObject? source)
        {
            try
            {
                string viewId = source == null ? "null" : source.GetHashCode().ToString();

                // 控件当前的视觉树状态：Parent 为 null 说明已被摘除
                string parentState;
                try
                {
                    parentState = ChatWebView?.Parent == null ? "null" : ChatWebView.Parent.GetHashCode().ToString();
                }
                catch (Exception ex)
                {
                    parentState = $"<访问异常:{ex.GetType().Name}>";
                }

                // 宿主 Content 状态：用于区分「控件被移出宿主」与「宿主自身被卸载」
                string hostContentState;
                try
                {
                    hostContentState = ChatWebViewHost.Content == null ? "null(已摘除)" : "存在";
                }
                catch
                {
                    hostContentState = "<访问失败>";
                }

                Logger.Info($"[WebViewLifecycle] {stage} | source={viewId} | controlVisible={IsVisible} | controlLoaded={IsLoaded} | webViewParent={parentState} | hostContent={hostContentState}");
            }
            catch (Exception ex)
            {
                // 诊断日志本身绝不能影响控件生命周期行为
                Logger.Debug($"[WebViewLifecycle] 记录 {stage} 失败: {ex.GetType().Name}");
            }
        }

        /// <summary>DeepSeekChatControl 加载到视觉树时记录日志。</summary>
        private void ChatWebControl_Loaded(object sender, RoutedEventArgs e) => LogWebViewLifecycle("control.loaded", sender as DependencyObject);

        /// <summary>DeepSeekChatControl 被卸出视觉树时记录日志（工具窗口关闭/布局重建的关键证据）。</summary>
        private void ChatWebControl_Unloaded(object sender, RoutedEventArgs e) => LogWebViewLifecycle("control.unloaded", sender as DependencyObject);

        /// <summary>WebView2 宿主 ContentControl 加载时记录日志。</summary>
        private void ChatWebViewHost_Loaded(object sender, RoutedEventArgs e) => LogWebViewLifecycle("host.loaded", sender as DependencyObject);

        /// <summary>WebView2 宿主 ContentControl 被卸出视觉树时记录日志。</summary>
        private void ChatWebViewHost_Unloaded(object sender, RoutedEventArgs e) => LogWebViewLifecycle("host.unloaded", sender as DependencyObject);

        /// <summary>WebView2 宿主可见性变化时记录日志（自动隐藏/取消自动隐藏会走到这里）。</summary>
        private void ChatWebViewHost_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => LogWebViewLifecycle($"host.isVisibleChanged(new={e.NewValue})", sender as DependencyObject);

        /// <summary>WebView2 控件加载到视觉树时记录日志。</summary>
        private void ChatWebView_Loaded(object sender, RoutedEventArgs e) => LogWebViewLifecycle("webview.loaded", sender as DependencyObject);

        /// <summary>WebView2 控件被卸出视觉树时记录日志——此时底层 CoreWebView2 即将/已经释放。</summary>
        private void ChatWebView_Unloaded(object sender, RoutedEventArgs e) => LogWebViewLifecycle("webview.unloaded", sender as DependencyObject);

        /// <summary>WebView2 控件可见性变化时记录日志。</summary>
        private void ChatWebView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => LogWebViewLifecycle($"webview.isVisibleChanged(new={e.NewValue})", sender as DependencyObject);

        /// <summary>
        /// 解绑 WebView2 控件上的事件订阅。
        /// 用于控件重建与宿主释放场景，避免旧控件继续回调已失效的处理器。
        /// </summary>
        /// <param name="view">要解绑事件的 WebView2 控件；为 null 时直接返回。</param>
        private void DetachChatWebViewEvents(Microsoft.Web.WebView2.Wpf.WebView2CompositionControl? view)
        {
            if (view == null)
                return;

            try
            {
                view.CoreWebView2InitializationCompleted -= ChatWebView_CoreWebView2InitializationCompleted;
                view.PreviewKeyDown -= ChatWebView_PreviewKeyDown;
                view.KeyDown -= ChatWebView_PreviewKeyDown;
                view.ZoomFactorChanged -= ChatWebView_ZoomFactorChanged;
                view.Loaded -= ChatWebView_Loaded;
                view.Unloaded -= ChatWebView_Unloaded;
                view.IsVisibleChanged -= ChatWebView_IsVisibleChanged;
            }
            catch (Exception ex)
            {
                // 控件可能已进入不可用状态，解绑失败不影响后续重建
                Logger.Debug($"[Render] 解绑 WebView2 事件时忽略异常: {ex.GetType().Name}");
            }
        }

        /// <summary>
        /// 检查 WebView2 是否可用；不可用时尝试重建控件以恢复界面显示。
        /// 并发调用会复用同一个重建任务，避免重复创建控件。
        /// </summary>
        /// <returns>重建后控件可用返回 true；宿主已释放或重建失败返回 false。</returns>
        private async Task<bool> RebuildWebViewIfNeededAsync()
        {
            if (_disposed)
                return false;

            // 控件仍然可用：无需重建
            if (TryGetLiveCoreWebView(out _))
                return true;

            // ── 合并并发重建请求 ──
            // 流式更新、主题变更、切换解决方案可能同时发现控件失效；
            // 若各自重建会创建多个控件实例并重复挂载，这里统一复用同一任务。
            var inFlight = _webViewRebuildTask;
            if (inFlight != null)
                return await inFlight;

            var rebuildTask = RebuildWebViewCoreAsync();
            _webViewRebuildTask = rebuildTask;
            try
            {
                return await rebuildTask;
            }
            finally
            {
                if (ReferenceEquals(_webViewRebuildTask, rebuildTask))
                    _webViewRebuildTask = null;
            }
        }

        /// <summary>
        /// 重建已被 WebView2 释放的聊天浏览器控件。
        /// 流程：摘除并释放失效控件 → 重新创建控件 → 重建 CoreWebView2 → 重置渲染状态并全量重绘历史消息。
        /// </summary>
        /// <returns>重建成功返回 true；失败返回 false。</returns>
        private async Task<bool> RebuildWebViewCoreAsync()
        {
            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_disposed)
                return false;

            Logger.Warn($"[WebViewLifecycle] rebuild.start | staleView={ChatWebView?.GetHashCode() ?? 0} | hostContent={(ChatWebViewHost.Content == null ? "null(已摘除)" : "存在")} | 控件已随视觉树卸载而释放，开始重建以恢复聊天界面");

            try
            {
                // ── 1. 摘除失效控件 ──
                // 控件虽已不可用，但字段仍持有引用；先解绑事件并释放，避免残留订阅。
                var stale = ChatWebView;
                if (stale != null)
                {
                    DetachChatWebViewEvents(stale);
                    ChatWebViewHost.Content = null;

                    try
                    {
                        stale.Dispose();
                    }
                    catch (Exception ex)
                    {
                        // 失效控件释放失败无碍后续重建，仅记录调试信息
                        Logger.Debug($"[Render] 释放失效 WebView2 控件时忽略异常: {ex.GetType().Name}");
                    }
                }

                // ── 2. 作废旧控件的初始化状态 ──
                // 旧任务与环境均绑定到已释放的控件，必须丢弃后重新建立，
                // 否则 InitializeWebViewAsync 会因幂等守卫而直接跳过初始化。
                _webViewInitializationTask = null;
                _webView2Environment = null;

                // ── 3. 重新创建控件与 CoreWebView2 环境 ──
                // 抑制 InitializationCompleted 中的渲染：首次绘制由本方法末尾的全量刷新显式接管，
                // 否则会出现「空白页覆盖已有内容」的双重导航。
                _suppressWebViewUpdate = true;
                InitializeChatWebView();
                bool initSuccess = await InitializeWebViewAsync();
                _webViewInitialized = initSuccess;

                if (!initSuccess)
                {
                    Logger.Error("[Render] WebView2 重建失败，聊天界面暂时无法恢复");
                    return false;
                }

                // ── 4. 重置渲染状态并全量重绘 ──
                // 新控件是全新页面，必须走全量路径；_lastRenderedMessagesLength 归零
                // 可避免把旧页面已渲染的长度当作「已显示内容」而漏掉历史消息。
                _browserInitialized = false;
                _lastRenderedMessagesLength = 0;
                _pageReady = false;
                lock (_lock) { _createdPlanIds.Clear(); }

                RebuildMessagesHtml();

                if (!TryGetLiveCoreWebView(out var core))
                {
                    Logger.Error("[Render] WebView2 重建后控件仍不可用");
                    return false;
                }

                string restoredHtml = ChatHtmlService.BuildInitialPageFromMessagesHtml(_messagesHtml.ToString());
                core!.NavigateToString(restoredHtml);
                _browserInitialized = true;
                _lastRenderedMessagesLength = _messagesHtml.Length;

                // 重建成功：清除失效告警去重标记，使下一次失效能再次告警
                _webViewGoneLoggedInstance = null;
                Logger.Info($"[WebViewLifecycle] rebuild.success | newView={ChatWebView?.GetHashCode() ?? 0} | hostContent={(ChatWebViewHost.Content == null ? "null(已摘除)" : "存在")}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[Render] 重建 WebView2 失败: {ex.GetType().Name}: {ex.Message}", ex);
                return false;
            }
            finally
            {
                // 无论成功与否都复位抑制标志，避免后续渲染被永久抑制
                _suppressWebViewUpdate = false;
            }
        }

        /// <summary>
        /// 增量更新浏览器内容。
        /// 对标 ucChat.UpdateBrowser()：首次使用 NavigateToString，
        /// 后续通过 ExecuteScriptAsync 调用 window.__appendMessageHtml 增量追加。
        /// 若检测到控件已被释放，会先尝试重建，保证界面能自行恢复显示。
        /// </summary>
        #pragma warning disable VSTHRD100 // async void 模式用于浏览器更新（fire-and-forget），异常已在方法内处理
        private async void UpdateBrowser()
        {
            if (_disposed)
                return;

            try
            {
                // ── 控件失效（被视觉树卸载）时先重建，再继续本次渲染 ──
                if (!TryGetLiveCoreWebView(out var core))
                {
                    if (!await RebuildWebViewIfNeededAsync() || !TryGetLiveCoreWebView(out core))
                        return;
                }

                string allMessages = _messagesHtml.ToString();

                // ── 增量更新路径 ──
                if (_browserInitialized && allMessages.Length > _lastRenderedMessagesLength)
                {
                    string delta = allMessages.Substring(_lastRenderedMessagesLength);
                    string jsFragment = System.Text.Json.JsonSerializer.Serialize(delta);

                    // ── 在 await 之前推进 _lastRenderedMessagesLength，防止并发 UpdateBrowser
                    //     调用读到旧值导致 delta 计算重复（产生两个用户气泡）──
                    _lastRenderedMessagesLength = allMessages.Length;

                    try
                    {
                        string script = $"window.__appendMessageHtml({jsFragment});";
                        await core!.ExecuteScriptAsync(script);
                        return;
                    }
                    catch
                    {
                        // ExecuteScriptAsync 失败时改用 PostWebMessageAsString 追加（避免 NavigateToString 全量刷新导致页面闪烁/滚动）
                        try
                        {
                            string appendJson = $"{{\"type\":\"appendHtml\",\"html\":{jsFragment}}}";
                            core!.PostWebMessageAsString(appendJson);
                            return;
                        }
                        catch
                        {
                            // PostWebMessageAsString 也失败时才回退到全量刷新（极少情况）
                            // 重置 _lastRenderedMessagesLength，因为增量内容未成功渲染
                            // 全量刷新会重建整个页面，重置为当前 _messagesHtml 实际长度
                        }
                    }
                }

                // ── 全量刷新路径（首次初始化、增量失败回退、或 _lastRenderedMessagesLength 被并发调用重置后）──
                // 需要重新读取 _messagesHtml 长度，因为并发调用可能已追加新内容
                string allMessagesNow = _messagesHtml.ToString();
                string html = ChatHtmlService.BuildInitialPageFromMessagesHtml(allMessagesNow);

                // 上面的 await 期间控件可能再次失效，这里重新探测，避免访问已释放的 CoreWebView2
                if (!TryGetLiveCoreWebView(out var liveCore))
                    return;

                liveCore!.NavigateToString(html);
                _browserInitialized = true;
                _lastRenderedMessagesLength = allMessagesNow.Length;
            }
            catch (Exception ex) when (IsWebViewGoneException(ex))
            {
                // 控件在渲染过程中被释放属预期情况：降级为调试日志，由后续调用触发重建即可，
                // 避免此前 779 条 ERROR 刷屏并掩盖真实问题。
                Logger.Debug($"[Render] 渲染时控件已释放，已跳过本次更新: {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[Render] UpdateBrowser 异常: {ex.Message}", ex);
            }
        }
        #pragma warning restore VSTHRD100

        /// <summary>
        /// 构建消息 HTML 片段并追加到 _messagesHtml，然后更新浏览器。
        /// </summary>
        private void AddMessagesHtml(
            string role,
            string content,
            string? reasoningContent = null,
            List<FileParseResult>? attachedFiles = null,
            List<string>? attachedImageDataUris = null,
            List<string>? attachedImageFileNames = null,
            List<string>? attachedImagePaths = null,
            int messageIndex = -1,
            bool isHtml = false)
        {
            // 自动推断消息索引
            if (messageIndex < 0)
                messageIndex = _messages.Count - 1;

            if (role == "user")
            {
                _messagesHtml.Append(ChatHtmlService.BuildUserMessageHtml(
                    content,
                    attachedFiles,
                    messageIndex,
                    attachedImageDataUris,
                    attachedImageFileNames,
                    attachedImagePaths));
            }
            else
            {
                var tempMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = content,
                    ReasoningContent = reasoningContent ?? string.Empty,
                    IsStreaming = false,
                    IsHtml = isHtml,
                };
                _messagesHtml.Append(ChatHtmlService.BuildAssistantMessageHtml(tempMsg, _messages.Count - 1));
            }
        }

        /// <summary>
        /// CoreWebView2 初始化完成回调。
        /// </summary>
        private void ChatWebView_CoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                Logger.Info("[Render] CoreWebView2InitializationCompleted: 成功");
                _webViewInitialized = true;
                ChatWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                ChatWebView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
                ChatWebView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
                ChatWebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
                ChatWebView.ZoomFactorChanged += ChatWebView_ZoomFactorChanged;

                ApplyPersistedWebView2Zoom();

                // ── 自定义右键菜单：保留复制、全选等常用操作 ──
                ChatWebView.CoreWebView2.ContextMenuRequested += (cmSender, cmArgs) =>
                {
                    cmArgs.MenuItems.Clear();

                    cmArgs.MenuItems.Add(ChatWebView.CoreWebView2.Environment.CreateContextMenuItem(
                        LocalizationService.Instance["contextMenu.copy"], null, CoreWebView2ContextMenuItemKind.Command));
                    cmArgs.MenuItems[0].CustomItemSelected += (_, _) =>
                        ChatWebView.CoreWebView2.ExecuteScriptAsync("document.execCommand('copy');");

                    cmArgs.MenuItems.Add(ChatWebView.CoreWebView2.Environment.CreateContextMenuItem(
                        LocalizationService.Instance["contextMenu.selectAll"], null, CoreWebView2ContextMenuItemKind.Command));
                    cmArgs.MenuItems[1].CustomItemSelected += (_, _) =>
                        ChatWebView.CoreWebView2.ExecuteScriptAsync("document.execCommand('selectAll');");
                };

                // ── 禁用 WebView2 状态栏（左下角），避免暴露 vs-navigate:// 等内部链接 URL ──
                ChatWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;

                // ── 禁用 F12 开发者工具 ──
                ChatWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;

                // ── 构建初始 HTML 内容 ──
                // 如果 LoadAndShowAsync 已接管首次渲染，跳过此处的 UpdateBrowser
                // 以避免先后两次 NavigateToString 导致页面被空白覆盖
                RebuildMessagesHtml();
                if (!_suppressWebViewUpdate)
                {
                    _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                    {
                        await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        // ── 清除可能由之前隐式初始化失败残留的错误消息 ──
                        StatusLabel.Text = LocalizationService.Instance["status.ready"];
                        UpdateBrowser();
                    });
                }
                else
                {
                    Logger.Info("[Render] 跳过 CoreWebView2InitializationCompleted 中的 UpdateBrowser（由 LoadAndShowAsync 接管）");
                }
            }
            else
            {
                // ── 仅当尚未显式初始化成功时才显示错误 ──
                // 避免隐式初始化失败的错误消息覆盖后续显式初始化的成功状态
                if (!_webViewInitialized)
                {
                    Logger.Error($"[Render] CoreWebView2 初始化失败: {e.InitializationException?.Message}", e.InitializationException);
                    _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                    {
                        await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        StatusLabel.Text = LocalizationService.Instance.Format("status.webviewInitFailed", e.InitializationException?.Message);
                    });
                }
                else
                {
                    Logger.Warn($"[Render] CoreWebView2 初始化完成事件报告失败，但 WebView2 已标记为已初始化，忽略: {e.InitializationException?.Message}");
                }
            }
        }

        /// <summary>
        /// 根据 _messages 列表重建 _messagesHtml。
        private int _renderWindowStart;
        private const int RenderWindowBatchSize = 40;

        /// <summary>
        /// 根据 _messages 列表重建 _messagesHtml。
        /// P1 性能：仅渲染最近 RenderWindowBatchSize 条（窗口化），
        /// 更早的历史通过「加载更早的消息」按批前插，避免长会话全量重建压力。
        /// </summary>
        private void RebuildMessagesHtml()
        {
            _messagesHtml.Clear();
            _renderWindowStart = Math.Max(0, _messages.Count - RenderWindowBatchSize);
            if (_renderWindowStart > 0)
                _messagesHtml.Append(ChatHtmlService.BuildLoadEarlierButtonHtml(_renderWindowStart));
            AppendMessageRange(_messagesHtml, _renderWindowStart, _messages.Count);
            _lastRenderedMessagesLength = 0;
        }

        /// <summary>构建 [from, to) 区间消息 HTML（含分支导航）。</summary>
        private void AppendMessageRange(System.Text.StringBuilder sb, int from, int toExclusive)
        {
            for (int i = from; i < toExclusive; i++)
            {
                var msg = _messages[i];
                if (msg.Role == "user")
                {
                    sb.Append(ChatHtmlService.BuildUserMessageHtml(
                        msg.Content ?? string.Empty,
                        msg.AttachedFiles.Count > 0 ? msg.AttachedFiles : null,
                        i,
                        msg.AttachedImageDataUris.Count > 0 ? msg.AttachedImageDataUris : null,
                        msg.AttachedImageFileNames.Count > 0 ? msg.AttachedImageFileNames : null,
                        msg.AttachedImagePaths.Count > 0 ? msg.AttachedImagePaths : null));
                    if (msg.SiblingCount > 1)
                    {
                        sb.Append(ChatHtmlService.BuildBranchNavHtml(msg, i));
                    }
                    else
                    {
                        int nextIdx = i + 1;
                        if (nextIdx < _messages.Count)
                        {
                            var nextMsg = _messages[nextIdx];
                            if (nextMsg.Role == "assistant" && nextMsg.SiblingCount > 1)
                                sb.Append(ChatHtmlService.BuildBranchNavHtml(nextMsg, nextIdx));
                        }
                    }
                }
                else
                {
                    sb.Append(ChatHtmlService.BuildAssistantMessageHtml(msg, i));
                }
            }
        }

        /// <summary>
        /// 「加载更早的消息」：把上一批历史前插到 DOM 顶部（不影响流式增量管线）。
        /// </summary>
        private async System.Threading.Tasks.Task LoadEarlierMessagesAsync()
        {
            try
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ChatWebView.CoreWebView2 == null || _renderWindowStart <= 0) return;

                int newStart = Math.Max(0, _renderWindowStart - RenderWindowBatchSize);
                var sb = new System.Text.StringBuilder();
                if (_renderWindowStart > newStart)
                {
                    sb.Append(ChatHtmlService.BuildLoadEarlierButtonHtml(newStart));
                    AppendMessageRange(sb, newStart, _renderWindowStart);
                }
                string json = ChatHtmlService.BuildPrependOlderJson(sb.ToString(), newStart > 0, _renderWindowStart - newStart);
                ChatWebView.CoreWebView2.PostWebMessageAsString(json);
                _renderWindowStart = newStart;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[History] 加载更早消息失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 通过 PostWebMessageAsString 非阻塞推送流式更新（高性能路径）。
        /// PostWebMessageAsString 不等待 JS 执行完成，不阻塞 UI 线程。
        /// JS 侧通过 requestAnimationFrame 批量处理 DOM 更新。
        /// </summary>
        private void PostStreamingUpdate(
            int messageIndex,
            string? content,
            string reasoningContent,
            bool isComplete,
            string? statusText = null,
            string? reasoningDelta = null,
            string? contentDelta = null)
        {
            if (ChatWebView.CoreWebView2 == null || !_pageReady) return;

            try
            {
                string json = ChatHtmlService.BuildStreamUpdateJson(
                    messageIndex,
                    content,
                    reasoningContent,
                    isComplete,
                    statusText,
                    reasoningDelta,
                    contentDelta);
                ChatWebView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Render] PostStreamingUpdate 异常: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 通过 PostWebMessageAsString 非阻塞推送流式完成（含 Markdown 渲染 HTML）。
        /// 若消息过大导致 PostWebMessageAsString 失败，自动降级为全量页面刷新。
        /// </summary>
        private void PostStreamEnd(int messageIndex, string fullContent, string reasoningContent, string? extraFooterHtml = null)
        {
            if (ChatWebView.CoreWebView2 == null || !_pageReady) return;

            try
            {
                string? timelineContent = null;
                string? turnId = null;
                bool isProcessMessage = false;
                int toolCallCount = 0;
                lock (_lock)
                {
                    if (messageIndex >= 0 && messageIndex < _messages.Count)
                    {
                        var finalizedMsg = _messages[messageIndex];
                        timelineContent = finalizedMsg.TimelineContent;
                        turnId = finalizedMsg.TurnId;
                        isProcessMessage = finalizedMsg.IsProcessMessage;
                        toolCallCount = finalizedMsg.ToolCallCount;
                    }
                }
                string json = ChatHtmlService.BuildStreamEndJson(
                    messageIndex,
                    fullContent,
                    reasoningContent,
                    extraFooterHtml,
                    timelineContent,
                    turnId,
                    isProcessMessage,
                    toolCallCount);
                ChatWebView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                // ── 降级方案：PostWebMessageAsString 可能因消息过大（>1MB）或 JSON 转义问题失败，
                //     回退到全量页面刷新（NavigateToString），确保 Markdown 正确渲染 ──
                Logger.Error($"[Render] PostStreamEnd 发送失败 (内容长度: {fullContent?.Length ?? 0}), 回退到全量刷新: {ex.Message}", ex);

                try
                {
                    // 更新对应消息的最终内容，标记流式结束，触发 RebuildMessagesHtml 重新渲染
                    lock (_lock)
                    {
                        if (messageIndex >= 0 && messageIndex < _messages.Count)
                        {
                            var msg = _messages[messageIndex];
                            msg.Content = fullContent ?? string.Empty;
                            msg.ReasoningContent = reasoningContent ?? string.Empty;
                            msg.IsStreaming = false;
                            msg.IsHtml = false; // 让 RebuildMessagesHtml 重新走 Markdown → HTML 渲染
                        }
                    }
                    RebuildMessagesHtml();
                    UpdateBrowser();
                }
                catch (Exception fallbackEx)
                {
                    Logger.Error($"[Render] PostStreamEnd 全量刷新降级也失败: {fallbackEx.Message}", fallbackEx);
                }
            }
        }

        /// <summary>
        /// 保存 WebView2 页面缩放比例并在每次导航后恢复。
        /// </summary>
        private double GetPersistedWebView2ZoomFactor()
        {
            int percent = DeepSeekOptionsPage.NormalizeWebView2ZoomPercent(
                _options?.WebView2ZoomPercent ?? DeepSeekOptionsPage.DefaultWebView2ZoomPercent);
            return percent / 100.0;
        }

        private void ApplyPersistedWebView2Zoom()
        {
            if (ChatWebView?.CoreWebView2 == null) return;

            try
            {
                _suppressWebViewZoomPersistence = true;
                ChatWebView.ZoomFactor = GetPersistedWebView2ZoomFactor();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Render] 恢复 WebView2 缩放失败: {ex.Message}");
            }
            finally
            {
                _suppressWebViewZoomPersistence = false;
            }
        }

        private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => CoreWebView2_NavigationCompleted(sender, e));
                return;
            }

            ApplyPersistedWebView2Zoom();
        }

        private void ChatWebView_ZoomFactorChanged(object? sender, EventArgs e)
        {
            if (_suppressWebViewZoomPersistence || ChatWebView == null) return;

            try
            {
                int percent = DeepSeekOptionsPage.NormalizeWebView2ZoomPercent(
                    (int)Math.Round(ChatWebView.ZoomFactor * 100));
                if (percent == (_options?.WebView2ZoomPercent ?? DeepSeekOptionsPage.DefaultWebView2ZoomPercent))
                    return;

                if (_options != null)
                {
                    _options.WebView2ZoomPercent = percent;
                    try { _options.SaveSettingsToStorage(); } catch { }
                }
                else if (DeepSeekOptionsPage.Instance != null)
                {
                    DeepSeekOptionsPage.Instance.WebView2ZoomPercent = percent;
                    try { DeepSeekOptionsPage.Instance.SaveSettingsToStorage(); } catch { }
                }

                Logger.Info($"[Render] WebView2 缩放已保存: {percent}%");
                RecordRuntimeSettingsApplied();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Render] 保存 WebView2 缩放失败: {ex.Message}");
            }
        }

        // ── 批处理流式更新已在 DeepSeekChatControl.xaml.cs 中实现（BatchStreamingUpdate 方法）──

        #endregion
    }
}
