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
        /// <summary>
        /// 控件曾因被卸出视觉树而需要恢复（VS 布局重建 / 启动结束调试等）。
        /// <para>
        /// 只在 <see cref="ChatWebView_Unloaded"/> 置位，作为 <see cref="ChatWebView_Loaded"/> 的
        /// 恢复触发条件。这样可避免「首次加载」与启动路径的初始化竞争：初次挂载时该标志为 false，
        /// 恢复逻辑不会介入，WebView2 的首次初始化仍由 LoadAndShowAsync 独占负责。
        /// </para>
        /// </summary>
        private bool _webViewReloadNeedsRestore;

        /// <summary>
        /// 当前控件实例的底层浏览器是否已随视觉树卸载而失效。
        /// <para>
        /// 与 <see cref="_webViewReloadNeedsRestore"/> 的区别：后者是「一次性触发标记」，
        /// 在 <c>Loaded</c> 时即被消费；本字段是**持续状态**，表示当前
        /// <see cref="ChatWebView"/> 实例已不可用，只有创建出全新控件实例
        /// （<see cref="InitializeChatWebView"/>）时才会清除。
        /// </para>
        /// <para>
        /// 存在的原因（实测）：控件卸载后其 <c>CoreWebView2</c> 引用仍在、属性访问不抛异常，
        /// 但导航已完全失效——日志显示 <c>NavigateToString</c> 调用后 2.5 秒
        /// <c>docUrl</c> 依旧是 <c>about:blank</c>。仅靠属性可访问性无法识别这种
        /// 「已死但引用残留」的实例，故用本字段作为实质判据。
        /// </para>
        /// </summary>
        private bool _webViewInstanceStale;

        /// <summary>
        /// 已尝试的连续重建次数。重建成功时归零；达到
        /// <see cref="MaxRebuildAttempts"/> 后放弃重试，避免无限循环耗尽 UI 线程。
        /// </summary>
        private int _rebuildAttemptCount;

        /// <summary>连续重建次数上限。超过即认定环境不可恢复，提示用户重启 VS。</summary>
        private const int MaxRebuildAttempts = 4;

        /// <summary>进行中的「重挂控件修复空白」任务；用于合并并发请求。</summary>
        private Task? _composeRecoveryTask;

        #region Private Methods - Rendering

        /// <summary>
        /// 探测 WebView2 控件当前是否仍可安全访问。
        /// <para>
        /// 控件被 WPF 视觉树卸载（关闭工具窗口 / VS 恢复布局 / 切换解决方案）后，
        /// WebView2 会释放底层 <c>CoreWebView2</c>；此后访问 <c>CoreWebView2</c> 属性或其成员
        /// 会抛 <see cref="InvalidOperationException"/>（内层 COMException 0x8007139F），
        /// 因此不能用「属性是否为 null」来判断控件是否还活着。
        /// </para>
        /// <para>
        /// <b>实测教训</b>：仅靠「属性可访问 + Parent 非 null」仍会把**已死但引用尚存**的
        /// 实例判为可用。日志证据：控件卸载后探测判定 <c>core=可用</c>，
        /// 但随后 <c>NavigateToString</c> 完全无效（2.5 秒后 <c>docUrl</c> 仍为
        /// <c>about:blank</c>）。因此这里追加一项实质判据——
        /// 若该控件实例**经历过卸载**（<see cref="_webViewReloadNeedsRestore"/> 为 true），
        /// 则其底层浏览器必然已释放，直接判定不可用，交由重建路径处理，
        /// 不再浪费时间做注定无效的导航尝试。
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

            // ── 实质判据：经历过卸载的控件实例，其浏览器已释放 ──
            // 这类实例的属性访问不会抛异常，但导航已完全无效；必须交给重建路径。
            if (_webViewInstanceStale)
            {
                LogWebViewGoneOnce(ChatWebView, "控件实例已随视觉树卸载而失效（引用残留但导航无效）");
                return false;
            }

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

        /// <summary>
        /// WebView2 控件重新加载到视觉树：记录日志并触发界面恢复。
        /// <para>
        /// 这是修复「调试后聊天窗口变空白」的关键入口。VS 在启动/结束调试时会卸载并重新挂载
        /// 工具窗口内容；控件一旦被卸出视觉树，WebView2 即释放底层 <c>CoreWebView2</c>，
        /// 重新挂回时控件内部是个空白浏览器实例，此前没有任何代码负责重新灌入历史消息，
        /// 于是界面表现为「空窗」，只有重启 VS（走全新初始化路径）才能恢复。
        /// </para>
        /// <para>
        /// 恢复是 fire-and-forget 的：本处理器在 WPF 布局阶段同步执行，不能阻塞。
        /// 实际恢复工作经 <c>Dispatcher.BeginInvoke</c> 推到布局完成后执行，
        /// 并通过 <see cref="RestoreWebViewAfterReloadAsync"/> 的合并逻辑避免重复重建。
        /// </para>
        /// </summary>
        private void ChatWebView_Loaded(object sender, RoutedEventArgs e)
        {
            LogWebViewLifecycle("webview.loaded", sender as DependencyObject);

            // ── 仅在「曾经被卸载过」时触发恢复 ──
            // 初次挂载（启动路径）不满足该条件，WebView2 的首次初始化仍由
            // LoadAndShowAsync 独占负责，恢复逻辑不介入，避免与之竞争。
            if (!_webViewReloadNeedsRestore)
            {
                Logger.Debug("[WebViewLifecycle] webview.loaded 为首次挂载，跳过恢复逻辑（由启动路径初始化）");
                return;
            }

            _webViewReloadNeedsRestore = false;
            ScheduleWebViewRestoreAfterReload();
        }

        /// <summary>
        /// WebView2 控件被卸出视觉树：记录日志并作废「已初始化」状态。
        /// <para>
        /// 控件脱离视觉树后底层 <c>CoreWebView2</c> 会被释放（HRESULT 0x8007139F）。
        /// 此处必须把 <c>_webViewInitialized</c> 复位为 false，否则后续
        /// <c>LoadAndShowCoreAsync</c> 会因该标志仍为 true 而走「已初始化，直接刷新页面内容」
        /// 分支，跳过必要的重新初始化，导致恢复失败。
        /// </para>
        /// </summary>
        private void ChatWebView_Unloaded(object sender, RoutedEventArgs e)
        {
            LogWebViewLifecycle("webview.unloaded", sender as DependencyObject);

            // ── 标记「本次卸载后需要恢复」 ──
            // 供 Loaded 处理器判断这是否为一次真实的重载往返。
            _webViewReloadNeedsRestore = true;

            // ── 标记当前控件实例已失效（持续状态，直到创建新控件才清除）──
            // 这是关键判据：卸载后 CoreWebView2 引用仍在、属性访问正常，
            // 但导航已无效（实测 NavigateToString 后 docUrl 仍是 about:blank）。
            _webViewInstanceStale = true;

            // ── 作废渲染状态 ──
            // 控件已脱离视觉树，CoreWebView2 随之释放，页面 DOM 一并消失。
            // 这些标志若不复位，恢复路径会误判「页面仍然完好」而跳过重新灌入历史消息，
            // 结果就是重新挂载后看到一个空白窗口。
            _webViewInitialized = false;
            _pageReady = false;
            _browserInitialized = false;

            Logger.Info("[WebViewLifecycle] webview.unloaded 已作废初始化与页面就绪状态，等待重新挂载后恢复");
        }

        /// <summary>
        /// WebView2 控件可见性变化：记录日志，并在重新可见时触发一次重绘。
        /// <para>
        /// 这是「调试后聊天窗口变空白」的修复入口。根因（经实测确认）：
        /// 可见性翻转后 D3D 合成表面与 WPF 合成器的关联失效，WPF 认为该区域
        /// 无需重绘，界面就一直停留在旧背景上——表现为**整块纯背景色空白**。
        /// </para>
        /// <para>
        /// 关键证据：此时页面内容完整（<c>_pageReady</c> 为 true、HTML 有上百 KB）、
        /// <c>CoreWebView2</c> 可访问、<c>IsFrontBufferAvailable</c> 亦为 true，
        /// 代码能观测到的每个指标都正常；而用户手动点一下窗口位置按钮、
        /// 触发一次系统级重绘，内容立即恢复。可见丢失的是「合成到屏幕」这最后一跳，
        /// 而非页面内容或浏览器实例。
        /// </para>
        /// <para>
        /// 因此这里在「重新可见」时主动施加重绘刺激（见
        /// <see cref="RepaintChatWebViewAsync"/>），而不是去重建浏览器。
        /// </para>
        /// </summary>
        private void ChatWebView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            LogWebViewLifecycle($"webview.isVisibleChanged(new={e.NewValue})", sender as DependencyObject);

            // 隐藏：不出帧属正常状态，必须清零计数。
            // 否则「先异常 → 隐藏 → 再可见」会把隐藏前后的两次判定累加，
            // 导致仅凭一次未经确认的异常就误触发重挂控件。
            if (e.NewValue is not bool visible || !visible)
            {
                return;
            }

            ScheduleCompositionHealthCheck();
        }

        /// <summary>
        /// 宿主可见性变化：记录日志，并在重新可见时同样触发重绘。
        /// <para>
        /// 实测日志中 <c>host.isVisibleChanged</c> 与 <c>webview.isVisibleChanged</c> 总是成对出现，
        /// 这里一并挂上处理以保证任一入口都能触发恢复。
        /// </para>
        /// </summary>
        private void ChatWebViewHost_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            LogWebViewLifecycle($"host.isVisibleChanged(new={e.NewValue})", sender as DependencyObject);

            // 与控件侧一致：隐藏时清零停摆计数，避免跨隐藏前后累加导致误判
            if (e.NewValue is not bool visible || !visible)
            {
                return;
            }

            ScheduleCompositionHealthCheck();
        }

        /// <summary>
        /// 排入一次合成健康检查。
        /// 延迟到布局完成后执行：可见性翻转之后的首次布局可能尚未把尺寸算好，
        /// 此刻读取 FrontBuffer 状态或重挂控件都会误判。
        /// </summary>
        private void ScheduleCompositionHealthCheck()
        {
            if (_disposed)
                return;

            try
            {
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => { _ = CheckCompositionHealthAsync(); }));
            }
            catch (Exception ex)
            {
                Logger.Debug($"[WebViewLifecycle] 排入合成健康检查失败: {ex.GetType().Name}");
            }
        }

        /// <summary>
        /// 检查聊天区域是否需要重绘；需要时按级别施加刺激。
        /// <para>
        /// 说明：此处曾以 <c>D3DImage.IsFrontBufferAvailable</c> 作为「合成失效」判据，
        /// 但实测证明该值在空白发生时**始终为 true**（<c>frontBufferChanged</c> 零触发）——
        /// 「前端缓冲区可用」与「画面已合成到屏幕」是两件事。
        /// 故现已移除该判据及相关订阅，改为在可见性恢复时直接施加重绘刺激。
        /// </para>
        /// </summary>
        private async Task CheckCompositionHealthAsync()
        {
            if (_disposed)
                return;

            try
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_disposed) return;

                // 控件不可见或未加载时不处理：隐藏期间本就不出帧，属正常状态
                if (!IsVisible || !IsLoaded)
                {
                    return;
                }

                // 页面未就绪说明是在初始化过程中，交由初始化路径负责
                if (!_pageReady)
                {
                    return;
                }

                // ── 恢复 ──
                // 根因（经实测确认）：控件虽一切状态正常（DOM 已就绪、可见、已加载、
                // 尺寸正常），但其 D3D 合成链路会静默停摆，产出 0 个像素——
                // 表现为整块纯背景色空白。该失效无法通过任何状态量观测
                // （IsFrontBufferAvailable 始终为 true），只能靠采样渲染像素识别。
                await RepaintChatWebViewAsync();
            }
            catch (Exception ex)
            {
                Logger.Error($"[WebViewLifecycle] 合成健康检查失败: {ex.GetType().Name}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 采样控件实际渲染出的非背景像素占比（百分比）。
        /// <para>
        /// 这是判断「控件到底有没有画出内容」的唯一可靠手段。此前依赖
        /// <c>_pageReady</c>/<c>docUrl</c> 等 DOM 侧指标，会出现「页面完全正常、
        /// 控件却一个像素都没画」的盲区（实测该情形覆盖率恰为 0.0%）。
        /// </para>
        /// </summary>
        /// <returns>覆盖率百分比；无法采样时返回 -1。</returns>
        private double SampleRenderedCoverage() => SampleRenderedPixels().Coverage;

        /// <summary>采样渲染像素，返回覆盖率与可读细节。</summary>
        private (double Coverage, string Detail) SampleRenderedPixels()
        {
            var webView = ChatWebView;
            if (webView == null)
                return (-1, "<无控件>");

            try
            {
                double w = webView.ActualWidth;
                double h = webView.ActualHeight;
                if (w < 1 || h < 1)
                    return (-1, $"<尺寸为 0: {w:F0}x{h:F0}>");

                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)w, (int)h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(webView);

                int stride = rtb.PixelWidth * 4;
                var pixels = new byte[stride * rtb.PixelHeight];
                rtb.CopyPixels(pixels, stride, 0);

                // 以左上角像素为背景基准，统计与背景差异明显的像素比例
                byte bgB = pixels[0], bgG = pixels[1], bgR = pixels[2];
                long nonBg = 0;
                long total = (long)rtb.PixelWidth * rtb.PixelHeight;

                for (int i = 0; i < pixels.Length; i += 4)
                {
                    int diff = Math.Abs(pixels[i] - bgB)
                             + Math.Abs(pixels[i + 1] - bgG)
                             + Math.Abs(pixels[i + 2] - bgR);
                    if (diff > 24) nonBg++;
                }

                double pct = total > 0 ? (nonBg * 100.0 / total) : 0;
                return (pct, $"(非背景 {nonBg}/{total}, bg=#{bgR:X2}{bgG:X2}{bgB:X2})");
            }
            catch (Exception ex)
            {
                return (-1, $"<采样失败:{ex.GetType().Name}>");
            }
        }

        /// <summary>
        /// 分级触发聊天区域的 WPF 重绘，修复「内容在但未画到屏幕」的空白。
        /// <para>
        /// 级别（由轻到重，逐级尝试）：
        /// <list type="number">
        /// <item>让 WebView2 控件自身失效重绘（<c>InvalidateVisual</c>）——成本几乎为零，不闪屏。</item>
        /// <item>让宿主与控件一并失效重绘，覆盖宿主裁剪区域未更新的情况。</item>
        /// <item>重挂控件（复用 <see cref="RecoverCompositionByReattachAsync"/>）——前两级无效时才用，
        /// 会重建 D3D 捕获链路，代价更高但不丢页面内容。</item>
        /// </list>
        /// </para>
        /// <para>
        /// 修复的成功与否无法从代码侧观测（所有状态量都正常），因此这里不做判定，
        /// 只是逐级施加足够强的重绘刺激；每级都记录日志以便日后定位实际生效的级别。
        /// </para>
        /// </summary>
        private async Task RepaintChatWebViewAsync()
        {
            var webView = ChatWebView;
            if (webView == null)
                return;

            try
            {
                // ── 1. 控件自身失效重绘 ──
                webView.InvalidateVisual();

                // ── 2. 宿主与控件一并失效，并请求一次布局重算 ──
                // 宿主 ContentControl 有自己的裁剪区域；只让控件失效时，
                // 宿主的陈旧内容有时会继续遮挡（与 issue #31 同源的 WPF 合成层 z-order 问题）。
                ChatWebViewHost.InvalidateVisual();
                webView.InvalidateMeasure();
                webView.InvalidateArrange();

                // 把重绘推到下一轮渲染优先级，确保在本次布局完成后真正执行
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                if (_disposed) return;

                // ── 验证重绘是否真的产出画面 ──
                // 关键：不假设「重绘必然有效」，而是实测非背景像素占比。
                // 该失效的特点是 DOM 侧完全正常、状态量全部正常，但控件产出 0 个像素，
                // 单纯 InvalidateVisual 无法恢复，必须重建合成链路。
                double coverage = SampleRenderedCoverage();
                if (coverage >= 0 && coverage < RenderedCoverageThreshold)
                {
                    Logger.Warn($"[WebViewLifecycle] 重绘后仍无画面（coverage={coverage:F1}%），"
                        + "重建合成链路以恢复显示");
                    await RecoverCompositionByReattachAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[WebViewLifecycle] 重绘聊天区域失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 判定「控件是否真的画出了内容」的像素覆盖率阈值（百分比）。
        /// 低于该值即认定合成输出已断，需要重建捕获链路。
        /// 实测：故障时覆盖率恰为 0.0%，正常时可达 20%~70%，阈值取 1% 留有充分余量。
        /// </summary>
        private const double RenderedCoverageThreshold = 1.0;

        /// <summary>
        /// 通过「摘除控件再挂回」重建 D3D 捕获链路。
        /// <para>
        /// 刻意不销毁控件、不重建 <c>CoreWebView2</c>：页面 DOM 与历史消息都还在，
        /// 只需让库重新创建 D3D 设备、帧池与捕获会话。
        /// 重挂会触发控件重新加载并重新协商尺寸，从而重建整条合成链路。
        /// </para>
        /// <para>
        /// 与 <see cref="RebuildWebViewCoreAsync"/> 的区别：后者用于控件真正失效
        /// （被视觉树卸载、浏览器进程退出）的场景，需要重新灌入历史消息；
        /// 本方法用于「控件活着但画不出来」，代价小得多，且不会丢失页面状态。
        /// </para>
        /// </summary>
        private async Task RecoverCompositionByReattachAsync()
        {
            // 合并并发请求：可见性事件可能密集到达
            var inFlight = _composeRecoveryTask;
            if (inFlight != null && !inFlight.IsCompleted)
            {
                Logger.Debug("[WebViewLifecycle] compose.reattach | 已有重挂任务在进行，复用");
                await inFlight;
                return;
            }

            var task = RecoverCompositionByReattachCoreAsync();
            _composeRecoveryTask = task;
            try
            {
                await task;
            }
            finally
            {
                if (ReferenceEquals(_composeRecoveryTask, task))
                    _composeRecoveryTask = null;
            }
        }

        private async Task RecoverCompositionByReattachCoreAsync()
        {
            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_disposed)
                return;

            var webView = ChatWebView;
            if (webView == null)
                return;

            try
            {
                // ── 摘除再挂回 ──
                // 摘除期间控件脱离视觉树，库会释放捕获会话；重新挂回时按当前尺寸
                // 重新创建 D3D 设备与帧池，从而重建整条合成链路。
                // 这是比重绘更强的手段，用于前两级重绘无效的场景。
                ChatWebViewHost.Content = null;
                ChatWebViewHost.Content = webView;

                // 复位计数：给新链路一个干净的观察窗口

                // ── 重挂后补一次重绘，确保新链路立刻出画面 ──
                await Task.Delay(150);
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_disposed) return;

                try
                {
                    webView.InvalidateVisual();
                    ChatWebViewHost.InvalidateVisual();
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[WebViewLifecycle] 重挂后重绘失败: {ex.GetType().Name}");
                }

                // ── 复查：确认画面真的回来了 ──
                // 若重挂仍无法恢复，说明连重建捕获链路也救不回来，记录以便诊断。
                await Task.Delay(1200);
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_disposed) return;

                double after = SampleRenderedCoverage();
                if (after >= 0 && after < RenderedCoverageThreshold)
                {
                    Logger.Error($"[WebViewLifecycle] 重建合成链路后仍未恢复显示（coverage={after:F1}%），"
                        + "需改用窗口式 WebView2");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[WebViewLifecycle] 重挂控件恢复合成链路失败: {ex.GetType().Name}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 排入一次「重载后恢复」任务。
        /// 用 Dispatcher 延迟到布局完成后执行：Loaded 期间控件尺寸可能仍为 0，
        /// 此刻直接重建会与 WebView2CompositionControl 的 SizeChanged 竞争。
        /// </summary>
        private void ScheduleWebViewRestoreAfterReload()
        {
            if (_disposed)
                return;

            try
            {
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => { _ = RestoreWebViewAfterReloadAsync(); }));
            }
            catch (Exception ex)
            {
                Logger.Debug($"[WebViewLifecycle] 排入重载恢复任务失败: {ex.GetType().Name}");
            }
        }

        /// <summary>
        /// 控件重新挂载后恢复聊天界面。
        /// <para>
        /// 主要路径（实测确认）：<c>webview.unloaded</c> 后控件实例的底层浏览器已释放，
        /// 但引用仍在、属性访问不抛异常，导航则完全无效
        /// （日志证据：<c>NavigateToString</c> 调用 2.5 秒后 <c>docUrl</c> 仍是 <c>about:blank</c>）。
        /// 这类实例由 <see cref="TryGetLiveCoreWebView"/> 通过
        /// <c>_webViewInstanceStale</c> 识别为不可用，于是直接走重建——
        /// 创建全新控件与 <c>CoreWebView2</c> 环境并全量重绘历史消息。
        /// </para>
        /// <para>
        /// 其余两种情形：
        /// <list type="bullet">
        /// <item>控件存活且页面内容完好（仅是一次可见性往返）——什么都不做。</item>
        /// <item>控件存活但页面尚未就绪——重新灌入历史消息（并延迟复查，必要时转重建）。</item>
        /// </list>
        /// </para>
        /// </summary>
        private async Task RestoreWebViewAfterReloadAsync()
        {
            if (_disposed)
                return;

            try
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_disposed) return;

                // ── 情形 3：控件失效 → 重建（内部会全量重绘历史消息）──
                // 卸载过的控件实例必然走到这里：属性可访问，但导航无效，重建是唯一出路。
                if (!TryGetLiveCoreWebView(out var core))
                {
                    Logger.Info("[WebViewLifecycle] reload.restore | 控件不可用，转入重建路径");
                    if (await RebuildWebViewIfNeededAsync() && TryGetLiveCoreWebView(out _))
                        Logger.Info("[WebViewLifecycle] reload.restore | 重建成功，历史消息已重绘");
                    else
                        Logger.Warn("[WebViewLifecycle] reload.restore | 重建未成功，界面可能仍为空白");

                    return;
                }

                // ── 情形 2：控件存活但页面尚未就绪（内容为空）──
                // _pageReady 由页面 JS 发出 __pageReady__ 信号置位；控件被卸载后必然为 false，
                // 说明当前 DOM 是空页面，需要重新灌入历史消息。
                if (!_pageReady || !_browserInitialized)
                {
                    _browserInitialized = false;
                    _lastRenderedMessagesLength = 0;
                    RebuildMessagesHtml();
                    UpdateBrowser();
                    return;
                }

                // ── 情形 1：页面完好，仅是一次可见性往返 ──
                Logger.Debug("[WebViewLifecycle] reload.restore | 页面内容完好，无需恢复");
            }
            catch (Exception ex)
            {
                Logger.Error($"[WebViewLifecycle] 重载后恢复聊天界面失败: {ex.GetType().Name}: {ex.Message}", ex);
            }
        }


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

            // ── 初始化进行中：绝不能在此时重建 ──
            // 启动路径正在对当前控件执行 EnsureCoreWebView2Async。此刻 CoreWebView2 尚为 null，
            // 上面的存活探测必然失败，但这并不代表控件已失效。
            // 若此时重建，会 Dispose 掉正在初始化的控件并把 _webViewInitializationTask 置空，
            // 导致库内部 InitializeController 抛 NullReferenceException(0x80004003)。
            // 等待其完成，再由调用方重新判断状态。
            var initializationTask = _webViewInitializationTask;
            if (initializationTask != null && !initializationTask.IsCompleted)
            {
                Logger.Info("[WebViewLifecycle] rebuild.deferred | 初始化正在进行，跳过本次重建");
                try
                {
                    await initializationTask;
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[Render] 等待进行中的初始化完成时忽略异常: {ex.GetType().Name}");
                }

                // 初始化可能已让控件恢复可用
                return TryGetLiveCoreWebView(out _);
            }

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

            // ── 防重建风暴 ──
            // 正常情况下重建会创建全新控件实例并清除失效标记，不会连续触发。
            // 但若重建本身反复失败（环境不可用、运行时被破坏等），
            // 恢复路径会不断回来重试。这里限流：超过阈值后放弃并保持现状，
            // 避免无限循环耗尽 UI 线程。
            if (_rebuildAttemptCount >= MaxRebuildAttempts)
            {
                Logger.Error($"[WebViewLifecycle] rebuild.abort | 连续重建已达 {_rebuildAttemptCount} 次仍未恢复，"
                    + "停止重试以避免无限循环；请重启 Visual Studio");
                return false;
            }
            _rebuildAttemptCount++;

            Logger.Warn($"[WebViewLifecycle] rebuild.start | attempt={_rebuildAttemptCount}/{MaxRebuildAttempts} "
                + $"| staleView={ChatWebView?.GetHashCode() ?? 0} "
                + $"| hostContent={(ChatWebViewHost.Content == null ? "null(已摘除)" : "存在")} "
                + "| 控件已随视觉树卸载而释放，开始重建以恢复聊天界面");

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

                // 重建成功：清除失效告警去重标记与重建计数，
                // 使下一次真的失效时能再次告警、并重新获得完整的重试预算。
                _webViewGoneLoggedInstance = null;
                _rebuildAttemptCount = 0;
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
                    {
                        Logger.Warn("[WebViewLifecycle] 控件不可用且重建失败，跳过本次渲染");
                        return;
                    }
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
                {
                    Logger.Warn("[WebViewLifecycle] 全量刷新前控件已失效，跳过本次渲染");
                    return;
                }

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
                ChatWebView.CoreWebView2.ProcessFailed += CoreWebView2_ProcessFailed;
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

            // 会话切换 / 新建 / 清空 / 重启恢复都会走到这里：
            // 先丢弃上一会话残留的待处理追踪，再刷新文件改动列表（通常为空 → 隐藏）。
            EditorDiffMarkerService.Instance.ClearPendingTracking();
            RefreshChangeSummary();
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
            string? reasoningContent,
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
        /// 页面重建窗口内被丢弃的终态渲染快照，等 <c>__pageReady__</c> 后补发。
        /// </summary>
        private readonly struct PendingStreamEnd
        {
            public PendingStreamEnd(int messageIndex, Models.ChatMessage message, string fullContent, string reasoningContent, string? footerHtml)
            {
                MessageIndex = messageIndex;
                Message = message;
                FullContent = fullContent;
                ReasoningContent = reasoningContent;
                FooterHtml = footerHtml;
            }

            public int MessageIndex { get; }

            /// <summary>被渲染的消息实例：用于重放前校验，防止跨会话把旧内容注入同下标气泡。</summary>
            public Models.ChatMessage Message { get; }

            public string FullContent { get; }
            public string ReasoningContent { get; }
            public string? FooterHtml { get; }
        }

        private readonly object _pendingStreamEndLock = new();
        private PendingStreamEnd? _pendingStreamEnd;

        /// <summary>
        /// 通过 PostWebMessageAsString 非阻塞推送流式完成（含 Markdown 渲染 HTML）。
        /// 若消息过大导致 PostWebMessageAsString 失败，自动降级为全量页面刷新。
        /// 页面尚在重建时不能静默丢弃，否则气泡会永远停在占位/纯文本状态。
        /// </summary>
        private void PostStreamEnd(int messageIndex, string fullContent, string reasoningContent, string? extraFooterHtml = null)
        {
            if (ChatWebView?.CoreWebView2 == null || !_pageReady)
            {
                // ── 页面重建窗口（主题/语言切换、WebView 重建、会话切换）──
                // 此前的实现直接 return：这条气泡会停在占位或纯文本状态，既没有 Markdown
                // 也没有重试/复制按钮，直到下一次全量重建才恢复。改为缓存最后一次终态渲染，
                // 由 __pageReady__ 处理器补发（后到覆盖先到，重复补发幂等）。
                Models.ChatMessage? message = null;
                lock (_lock)
                {
                    if (messageIndex >= 0 && messageIndex < _messages.Count)
                        message = _messages[messageIndex];
                }
                if (message != null)
                {
                    lock (_pendingStreamEndLock)
                    {
                        _pendingStreamEnd = new PendingStreamEnd(
                            messageIndex,
                            message,
                            fullContent ?? string.Empty,
                            reasoningContent ?? string.Empty,
                            extraFooterHtml);
                    }
                    Logger.Info($"[Render] 页面未就绪，已缓存 streamEnd 等待重放 (idx={messageIndex})");
                }
                return;
            }

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
        /// 页面就绪后补发重建窗口内被丢弃的 <c>streamEnd</c>。
        /// 幂等：重复补发只是用同一份最终 HTML 再覆盖一次。
        /// </summary>
        private void TryReplayPendingStreamEnd()
        {
            // 由 CoreWebView2_WebMessageReceived 的 __pageReady__ 分支调用，必须在 UI 线程。
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            PendingStreamEnd? pending;
            lock (_pendingStreamEndLock)
            {
                pending = _pendingStreamEnd;
                _pendingStreamEnd = null;
            }

            if (!pending.HasValue)
                return;

            var snapshot = pending.Value;

            // 校验：期间可能已切换/清空会话，同一下标可能已指向另一条消息。
            bool stillCurrent;
            lock (_lock)
            {
                stillCurrent = snapshot.MessageIndex >= 0
                    && snapshot.MessageIndex < _messages.Count
                    && ReferenceEquals(_messages[snapshot.MessageIndex], snapshot.Message);
            }
            if (!stillCurrent)
            {
                Logger.Info("[Render] 缓存的 streamEnd 已过期（会话已切换），丢弃");
                return;
            }

            Logger.Info($"[Render] 页面就绪，补发被丢弃的 streamEnd (idx={snapshot.MessageIndex})");
            try
            {
                PostStreamEnd(snapshot.MessageIndex, snapshot.FullContent, snapshot.ReasoningContent, snapshot.FooterHtml);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Render] 补发 streamEnd 失败: {ex.Message}");
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

        /// <summary>
        /// 浏览器进程失败/退出处理。
        /// <para>
        /// 此前完全没有订阅该事件，浏览器进程在调试期间被杀时表现为静默白屏，
        /// 日志里没有任何线索。这里显式记录失败类型，并在浏览器进程已终止时
        /// 排入恢复任务（重建控件即可拉起新的浏览器进程）。
        /// </para>
        /// </summary>
        private void CoreWebView2_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
        {
            try
            {
                Logger.Warn($"[WebViewLifecycle] processFailed | kind={e.ProcessFailedKind} "
                    + $"| reason={e.Reason} | exitCode={e.ExitCode}");

                // 仅当承载页面的浏览器进程真的没了才重建；
                // 渲染/GPU 子进程失败时 Chromium 会自行恢复，无需重建整个控件。
                if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited)
                    return;

                if (_disposed)
                    return;

                Logger.Warn("[WebViewLifecycle] 浏览器进程已退出，排入恢复任务");

                // 作废状态：浏览器进程已随控件释放，必须走重建路径
                _webViewInitialized = false;
                _pageReady = false;
                _browserInitialized = false;

                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => { _ = RestoreWebViewAfterReloadAsync(); }));
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Render] 处理 ProcessFailed 时忽略异常: {ex.GetType().Name}");
            }
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
