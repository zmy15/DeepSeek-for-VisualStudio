using System.Reflection;
using System.Threading;
using System.Windows.Input;
using DeepSeek_v4_for_VisualStudio.View;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.View;

/// <summary>
/// 回归测试：修复「调试后聊天窗口变空白」。
/// <para>
/// 缺陷背景：VS 在启动/结束调试时会翻转工具窗口的可见性。实测日志证实该场景下
/// <c>unloaded</c> 从不发生——控件与页面 DOM 都还在（<c>_pageReady</c> 仍为 true），
/// 但 <c>WebView2CompositionControl</c> 的 D3D 合成链路（<c>GraphicsItemD3DImage</c>
/// 内部的帧池与捕获会话）会静默停摆，画面表现为整块纯背景色空白。
/// </para>
/// <para>
/// 本测试直接调用控件的生命周期处理器，验证各标志位的复位契约：
/// <c>Unloaded</c> 作废渲染状态并置位恢复标记，<c>Loaded</c> 消费该标记，
/// 可见性变化时正确维护停摆计数。
/// </para>
/// <para>
/// <see cref="DeepSeekChatControl"/> 派生自 WPF <c>UserControl</c>，构造需要 STA 线程；
/// 测试统一在专用 STA 线程上构造并释放控件（释放是必需的，见 RunOnStaThread 注释）。
/// </para>
/// </summary>
public class WebViewReloadRecoveryTests
{
    [Fact]
    public void Constructor_DoesNotRegisterHostPasteCommand()
    {
        RunOnStaThread(control =>
        {
            control.CommandBindings
                .OfType<CommandBinding>()
                .Should().NotContain(
                    binding => binding.Command == ApplicationCommands.Paste,
                    "宿主级 Paste 命令会把 WebView2 textarea 的原生文本粘贴吞掉");
        });
    }

    /// <summary>
    /// 在专用 STA 线程上构造控件、执行测试体，然后必定释放控件。
    /// <para>
    /// 释放这一步是必需的，不是可选的清理：控件构造时会向全局单例
    /// LocalizationService.LanguageChanged 订阅一个同步回调（内部执行 Dispatcher.Invoke）。
    /// 本线程从不运行消息循环，若控件在此线程上构造却不退订，后续任何调用 SetLanguage 的测试
    /// （如 LocalizedAttributesTests）都会在 LanguageChanged?.Invoke 上永久阻塞——
    /// 表现为整个测试运行挂起、且失败数为 0。
    /// </para>
    /// </summary>
    private static void RunOnStaThread(Action<DeepSeekChatControl> testBody)
    {
        Exception? captured = null;

        var thread = new Thread(() =>
        {
            DeepSeekChatControl? control = null;
            try
            {
                control = new DeepSeekChatControl();
                testBody(control);
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                // 必须解绑全局单例上的订阅，否则会毒化后续测试（见方法注释）
                try { control?.Dispose(); }
                catch { /* 释放异常不影响测试结论 */ }
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        // 控件构造与标志位操作为纯内存行为；30 秒足以完成，
        // 超时说明发生死锁，直接失败而非让整个测试运行永久挂起。
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("STA 测试线程应在超时前完成");

        if (captured != null)
            throw new InvalidOperationException("STA 线程内断言失败", captured);
    }

    /// <summary>读取控件实例上的私有实例字段。</summary>
    private static T GetField<T>(DeepSeekChatControl control, string name)
    {
        var field = typeof(DeepSeekChatControl).GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull($"字段 {name} 应存在于 DeepSeekChatControl");
        return (T)field!.GetValue(control)!;
    }

    /// <summary>写入控件实例上的私有实例字段。</summary>
    private static void SetField(DeepSeekChatControl control, string name, object? value)
    {
        var field = typeof(DeepSeekChatControl).GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull($"字段 {name} 应存在于 DeepSeekChatControl");
        field!.SetValue(control, value);
    }

    /// <summary>调用控件的私有生命周期处理器（RoutedEventArgs 版本）。</summary>
    private static void InvokeHandler(DeepSeekChatControl control, string methodName)
    {
        var method = typeof(DeepSeekChatControl).GetMethod(
            methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull($"方法 {methodName} 应存在于 DeepSeekChatControl");
        method!.Invoke(control, new object?[] { control, new System.Windows.RoutedEventArgs() });
    }

    /// <summary>调用控件的私有可见性处理器（DependencyPropertyChangedEventArgs 版本）。</summary>
    private static void InvokeVisibilityHandler(
        DeepSeekChatControl control, string methodName, bool newValue)
    {
        var method = typeof(DeepSeekChatControl).GetMethod(
            methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull($"方法 {methodName} 应存在于 DeepSeekChatControl");

        var args = new System.Windows.DependencyPropertyChangedEventArgs(
            System.Windows.UIElement.VisibilityProperty, !newValue, newValue);

        method!.Invoke(control, new object?[] { control, args });
    }

    // ── 失效实例识别（本次修复的核心）────────────────────────────────
    // 实测教训：控件卸载后 CoreWebView2 引用仍在、属性访问不抛异常，
    // 但 NavigateToString 完全无效（2.5 秒后 docUrl 仍为 about:blank）。
    // 因此必须以「实例是否经历过卸载」作为实质判据，而非属性可访问性。

    /// <summary>调用 TryGetLiveCoreWebView 并返回其结果。</summary>
    private static bool InvokeTryGetLiveCoreWebView(DeepSeekChatControl control)
    {
        var method = typeof(DeepSeekChatControl).GetMethod(
            "TryGetLiveCoreWebView", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull("TryGetLiveCoreWebView 应存在于 DeepSeekChatControl");

        object?[] args = { null };
        return (bool)method!.Invoke(control, args)!;
    }

    [Fact]
    public void Unloaded_ShouldMarkInstanceStale()
    {
        RunOnStaThread(control =>
        {
            GetField<bool>(control, "_webViewInstanceStale").Should().BeFalse(
                "新建控件不应处于失效状态");

            InvokeHandler(control, "ChatWebView_Unloaded");

            // 这是关键状态：卸载后实例的底层浏览器已释放，
            // 必须持续标记为失效（直到创建新控件），否则恢复路径会做无效的导航尝试。
            GetField<bool>(control, "_webViewInstanceStale").Should().BeTrue(
                "卸载后的控件实例必须被标记为失效");
        });
    }

    [Fact]
    public void TryGetLiveCoreWebView_ShouldRejectStaleInstance()
    {
        RunOnStaThread(control =>
        {
            // 标记为失效后，存活探测必须拒绝该实例——
            // 即使 CoreWebView2 属性仍可访问（这正是实测中误导我们的地方）。
            SetField(control, "_webViewInstanceStale", true);

            InvokeTryGetLiveCoreWebView(control).Should().BeFalse(
                "经历过卸载的控件实例必须被判为不可用，交由重建路径处理");
        });
    }

    [Fact]
    public void RebuildAttemptLimit_ShouldBeBounded()
    {
        // 重建风暴防护：连续重建次数必须有上限，否则会无限循环耗尽 UI 线程
        var field = typeof(DeepSeekChatControl).GetField(
            "MaxRebuildAttempts",
            BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull("MaxRebuildAttempts 常量应存在");

        int max = (int)field!.GetRawConstantValue()!;
        max.Should().BeGreaterThan(0);
        max.Should().BeLessThanOrEqualTo(10, "重试上限过大等于没有防护");
    }

    [Fact]
    public void NavigateToStringLimit_ShouldMatchWebView2Contract()
    {
        var field = typeof(DeepSeekChatControl).GetField(
            "WebViewNavigateToStringMaxBytes",
            BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull("WebViewNavigateToStringMaxBytes 常量应存在");

        int maxBytes = (int)field!.GetRawConstantValue()!;
        maxBytes.Should().Be(2 * 1024 * 1024,
            "CoreWebView2.NavigateToString 的官方上限是 2 MB");
    }

    [Fact]
    public void BuildNavigableInitialPageHtml_ReducesWindowToFitLimit()
    {
        RunOnStaThread(control =>
        {
            var messages = GetField<List<ChatMessage>>(control, "_messages");
            messages.Clear();
            for (int i = 0; i < 4; i++)
            {
                messages.Add(new ChatMessage
                {
                    Role = "user",
                    Content = $"MESSAGE_{i}_" + new string('x', 80_000),
                });
            }

            string emptyPage = ChatHtmlService.BuildInitialPageFromMessagesHtml(string.Empty);
            int maxBytes = System.Text.Encoding.UTF8.GetByteCount(emptyPage) + 120_000;

            string html = control.BuildNavigableInitialPageHtml(maxBytes);

            System.Text.Encoding.UTF8.GetByteCount(html).Should().BeLessOrEqualTo(maxBytes);
            html.Should().Contain("MESSAGE_3_", "至少必须保留最新消息可见");
            GetField<int>(control, "_renderWindowStart").Should().BeGreaterThan(
                0, "超限时应缩小窗口并保留加载更早消息的入口");
        });
    }

    // ── 卸载/重载路径的渲染状态复位契约 ──────────────────────────────

    [Fact]
    public void Unloaded_ShouldInvalidateRenderingStateAndArmRestore()
    {
        RunOnStaThread(control =>
        {
            // 模拟「一切就绪」的调试前状态
            SetField(control, "_webViewInitialized", true);
            SetField(control, "_pageReady", true);
            SetField(control, "_browserInitialized", true);
            SetField(control, "_webViewReloadNeedsRestore", false);

            InvokeHandler(control, "ChatWebView_Unloaded");

            // 控件脱离视觉树后 CoreWebView2 与页面 DOM 均已消失，
            // 这些标志若不复位，恢复路径会误判「页面仍然完好」而跳过重绘 → 空白窗口
            GetField<bool>(control, "_webViewInitialized").Should().BeFalse();
            GetField<bool>(control, "_pageReady").Should().BeFalse();
            GetField<bool>(control, "_browserInitialized").Should().BeFalse();

            // 置位恢复标记，供 Loaded 判断这是一次真实的重载往返
            GetField<bool>(control, "_webViewReloadNeedsRestore").Should().BeTrue();
        });
    }

    [Fact]
    public void Loaded_WhenNeverUnloaded_ShouldNotArmRestore()
    {
        RunOnStaThread(control =>
        {
            // 首次挂载（启动路径）：恢复逻辑绝不能介入，
            // 否则会与 LoadAndShowAsync 的首次初始化竞争，创建出重复控件
            GetField<bool>(control, "_webViewReloadNeedsRestore").Should().BeFalse();

            InvokeHandler(control, "ChatWebView_Loaded");

            GetField<bool>(control, "_webViewReloadNeedsRestore").Should().BeFalse(
                "首次挂载不满足重载条件，不应排入恢复任务");
        });
    }

    [Fact]
    public void Loaded_AfterUnloaded_ShouldConsumeRestoreFlag()
    {
        RunOnStaThread(control =>
        {
            InvokeHandler(control, "ChatWebView_Unloaded");
            GetField<bool>(control, "_webViewReloadNeedsRestore").Should().BeTrue();

            InvokeHandler(control, "ChatWebView_Loaded");

            // 标记被消费：同一次卸载-加载往返只触发一次恢复，
            // 避免 VS 布局抖动时反复重建控件
            GetField<bool>(control, "_webViewReloadNeedsRestore").Should().BeFalse();
        });
    }

    // ── 合成链路失效（真正的空白窗口成因）──────────────────────────────

    [Fact]
    public void IsVisibleChanged_ShouldNotThrow()
    {
        RunOnStaThread(control =>
        {
            // 变为可见时排入合成健康检查（这是空白恢复的入口）。
            // 未挂载到视觉树的控件 IsVisible 为 false，判定阶段会直接返回；
            // 隐藏时同样应安全返回。这里验证两个入口都不抛异常。
            InvokeVisibilityHandler(control, "ChatWebView_IsVisibleChanged", newValue: true);
            InvokeVisibilityHandler(control, "ChatWebView_IsVisibleChanged", newValue: false);
            InvokeVisibilityHandler(control, "ChatWebViewHost_IsVisibleChanged", newValue: true);
            InvokeVisibilityHandler(control, "ChatWebViewHost_IsVisibleChanged", newValue: false);
        });
    }

    [Fact]
    public void RenderedCoverageThreshold_ShouldBeSmallButPositive()
    {
        // 该阈值是「控件是否真的画出内容」的判定线，是本次修复的核心判据。
        // 实测：故障时覆盖率为 0.0%（全为背景色），正常时可达 20%~70%，
        // 因此阈值必须为正以区分「完全无画面」，又必须足够小以避免误判。
        var field = typeof(DeepSeekChatControl).GetField(
            "RenderedCoverageThreshold",
            BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull("RenderedCoverageThreshold 常量应存在");

        double threshold = (double)field!.GetRawConstantValue()!;
        threshold.Should().BeGreaterThan(0, "阈值必须为正，否则无法区分完全无画面的情形");
        threshold.Should().BeLessThan(20, "阈值过大会把正常渲染误判为空白");
    }

    [Fact]
    public void SampleRenderedCoverage_ShouldBeSafeWhenNoWebView()
    {
        RunOnStaThread(control =>
        {
            // 控件尚未初始化浏览器时采样不应抛异常，且应返回负值表示「无法判定」。
            // 负值是刻意的：调用方据此跳过判定，避免把「测不出来」误当作「画面正常」。
            var method = typeof(DeepSeekChatControl).GetMethod(
                "SampleRenderedCoverage", BindingFlags.Instance | BindingFlags.NonPublic);
            method.Should().NotBeNull("SampleRenderedCoverage 应存在于 DeepSeekChatControl");

            double coverage = (double)method!.Invoke(control, null)!;
            coverage.Should().BeNegative("无法采样时应返回负值，供调用方跳过判定");
        });
    }

    // ── 全局语言事件不得阻塞语言切换（历史缺陷：整个测试运行挂起）──────────

    /// <summary>
    /// 回归测试：在「从不运行消息循环」的 STA 线程上构造控件后，
    /// 语言切换必须仍能返回，而不是永久阻塞在 <c>LanguageChanged</c> 回调上。
    /// <para>
    /// 这正是截图中「1800 个测试跑到一半卡住、失败数为 0」的成因：
    /// 控件构造时向全局单例订阅了语言变更回调，回调内部同步等待该 STA 线程的
    /// Dispatcher（<c>Dispatcher.Invoke</c>）；而该线程从不泵消息，
    /// 于是后续任何 <c>SetLanguage</c> 都永久卡死。
    /// </para>
    /// <para>
    /// 这里刻意<b>不</b>在断言前释放控件，以覆盖「已释放才安全」之外的场景：
    /// 修复后处理器改用异步封送（<c>BeginInvoke</c>），无人泵消息时只是排队，
    /// 因此即便控件仍活着也不会拖住调用方。
    /// </para>
    /// </summary>
    [Fact]
    public void SetLanguage_WhenControlThreadNeverPumpsMessages_DoesNotHang()
    {
        DeepSeekChatControl? control = null;

        var staThread = new Thread(() => control = new DeepSeekChatControl())
        {
            IsBackground = true,
        };
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("控件构造应在超时前完成");

        try
        {
            var original = LocalizationService.Instance.CurrentLanguage;

            // 语言切换放在独立线程上并施加超时：
            // 若回归，这里会超时失败，而不是让整个测试运行无声挂起。
            var switched = System.Threading.Tasks.Task.Run(() =>
            {
                LocalizationService.Instance.SetLanguage("en");
                LocalizationService.Instance.SetLanguage("zh-CN");
            });

            switched.Wait(TimeSpan.FromSeconds(20))
                .Should().BeTrue("语言切换不得因订阅者同步等待消息泵而永久阻塞");

            LocalizationService.Instance.CurrentLanguage.Should().Be("zh-CN");
            LocalizationService.Instance.SetLanguage(original);
        }
        finally
        {
            // 退订全局事件，避免污染后续测试
            try { control?.Dispose(); } catch { /* 释放异常不影响测试结论 */ }
        }
    }
}
