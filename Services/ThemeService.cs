using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.WebAssets;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using System;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 主题服务：检测 VS 主题、管理浅色/深色模式切换、生成主题感知 CSS。
    /// 单例模式，在 Package 初始化时创建。
    /// </summary>
    public class ThemeService : IDisposable
    {
        #region Singleton

        private static readonly object _lock = new();
        private static ThemeService? _instance;
        private static volatile bool _initialized;

        public static ThemeService Instance
        {
            get
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new ThemeService();
                        Logger.Warn("[ThemeService] Instance lazy-created BEFORE Initialize() — VS theme cache will be unset until Initialize() runs");
                    }
                    return _instance;
                }
            }
        }

        /// <summary>
        /// 初始化单例（在 UI 线程调用，订阅 VS 主题变更事件）。
        /// 幂等：即使 Instance 已被懒加载也会补做初始化。
        /// </summary>
        public static void Initialize()
        {
            lock (_lock)
            {
                if (_instance == null)
                {
                    _instance = new ThemeService();
                }
                if (!_initialized)
                {
                    _initialized = true;
                    _instance.SubscribeToVSThemeChanges();
                }
            }
        }

        #endregion

        #region VS Theme Detection

        #region Events

        /// <summary>
        /// 当实际渲染主题发生变化时触发（深色 ↔ 浅色切换）。
        /// 仅在 Auto 模式下响应 VS 主题变更；手动模式下仅在用户主动切换时触发。
        /// </summary>
        public event Action<bool>? ThemeChanged;

        #endregion

        #region Properties

        private ThemeMode _userThemeMode = ThemeMode.Auto;

        /// <summary>
        /// 缓存的 VS 浅色主题状态（线程安全）。
        /// 在 UI 线程初始化时设置，后续可从任意线程读取。
        /// </summary>
        private volatile bool _cachedIsVSThemeLight;

        /// <summary>
        /// 用户设置的主题模式。
        /// </summary>
        public ThemeMode UserThemeMode
        {
            get => _userThemeMode;
            set
            {
                if (_userThemeMode == value) return;
                _userThemeMode = value;
                OnThemeChanged();
            }
        }

        /// <summary>
        /// VS 当前是否为浅色主题（线程安全，使用缓存值）。
        /// 通过检查 VS 环境背景色判断（深色主题背景色接近黑色，浅色接近白色）。
        /// </summary>
        public bool IsVSThemeLight
        {
            get
            {
                // 首先尝试返回缓存值（任意线程安全）
                return _cachedIsVSThemeLight;
            }
        }

        /// <summary>
        /// 在 UI 线程上刷新 VS 主题缓存。
        /// 使用多个 VS 颜色键采样投票，比单一键更可靠。
        /// </summary>
        public void RefreshVSThemeCache()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Logger.Info($"[ThemeService] RefreshVSThemeCache START — UI thread");

                // ═══ 方法一：注册表检测（最可靠） ═══
                bool? registryResult = TryGetThemeFromRegistry();
                if (registryResult.HasValue)
                {
                    _cachedIsVSThemeLight = registryResult.Value;
                    Logger.Info($"[ThemeService] VS theme cache set from REGISTRY: IsLight={_cachedIsVSThemeLight}");
                    return;
                }

                // ═══ 方法二：API 颜色键多数投票 ═══
                Logger.Info("[ThemeService] Registry failed, trying API color keys...");
                int lightVotes = 0;
                int totalVotes = 0;

                // ToolWindow background
                try
                {
                    var c = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
                    double b = c.R * 0.299 + c.G * 0.587 + c.B * 0.114;
                    totalVotes++;
                    if (b > 128) lightVotes++;
                    Logger.Info($"[ThemeService] API ToolWindowBg: R={c.R} G={c.G} B={c.B} b={b:F0} -> {(b > 128 ? "LIGHT" : "DARK")}");
                }
                catch (Exception ex) { Logger.Warn($"[ThemeService] API ToolWindowBg FAILED: {ex.GetType().Name}: {ex.Message}"); }

                // AccentBorder
                try
                {
                    var c = VSColorTheme.GetThemedColor(EnvironmentColors.AccentBorderColorKey);
                    double b = c.R * 0.299 + c.G * 0.587 + c.B * 0.114;
                    totalVotes++;
                    if (b > 128) lightVotes++;
                    Logger.Info($"[ThemeService] API AccentBorder: R={c.R} G={c.G} B={c.B} b={b:F0} -> {(b > 128 ? "LIGHT" : "DARK")}");
                }
                catch (Exception ex) { Logger.Warn($"[ThemeService] API AccentBorder FAILED: {ex.GetType().Name}: {ex.Message}"); }

                // CommandBar
                try
                {
                    var c = VSColorTheme.GetThemedColor(EnvironmentColors.CommandBarGradientBeginColorKey);
                    double b = c.R * 0.299 + c.G * 0.587 + c.B * 0.114;
                    totalVotes++;
                    if (b > 128) lightVotes++;
                    Logger.Info($"[ThemeService] API CommandBar: R={c.R} G={c.G} B={c.B} b={b:F0} -> {(b > 128 ? "LIGHT" : "DARK")}");
                }
                catch (Exception ex) { Logger.Warn($"[ThemeService] API CommandBar FAILED: {ex.GetType().Name}: {ex.Message}"); }

                if (totalVotes > 0)
                {
                    _cachedIsVSThemeLight = lightVotes * 2 >= totalVotes;
                    Logger.Info($"[ThemeService] VS theme cache set from API VOTE: IsLight={_cachedIsVSThemeLight} (votes: {lightVotes}/{totalVotes})");
                }
                else
                {
                    Logger.Warn("[ThemeService] ALL detection methods FAILED — defaulting to DARK");
                    _cachedIsVSThemeLight = false;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[ThemeService] RefreshVSThemeCache CRASHED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                _cachedIsVSThemeLight = TryGetThemeFromRegistry() ?? false;
            }
        }

        /// <summary>
        /// 从 Windows 注册表读取 VS 主题设置。
        /// 返回 null 表示无法从注册表确定。
        /// </summary>
        private static bool? TryGetThemeFromRegistry()
        {
            try
            {
                const string basePath = @"Software\Microsoft\VisualStudio";
                using var vsKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(basePath);
                if (vsKey == null)
                {
                    Logger.Warn($"[ThemeService] Registry: base key '{basePath}' not found");
                    return null;
                }

                Logger.Info($"[ThemeService] Registry: scanning subkeys under {basePath}...");
                foreach (var subKeyName in vsKey.GetSubKeyNames())
                {
                    if (!subKeyName.StartsWith("17.") && !subKeyName.StartsWith("18."))
                        continue;

                    Logger.Info($"[ThemeService] Registry: trying VS version key '{subKeyName}'");

                    // 路径一：ApplicationPrivateSettings\Microsoft\VisualStudio\Theme\ColorTheme
                    var themePath = subKeyName + @"\ApplicationPrivateSettings\Microsoft\VisualStudio\Theme";
                    using var themeKey = vsKey.OpenSubKey(themePath);
                    if (themeKey != null)
                    {
                        var colorTheme = themeKey.GetValue("ColorTheme") as string;
                        var colorThemeId = themeKey.GetValue("ColorThemeId")?.ToString();
                        Logger.Info($"[ThemeService] Registry: {themePath}\\ColorTheme='{colorTheme ?? "null"}', ColorThemeId='{colorThemeId ?? "null"}'");

                        if (!string.IsNullOrEmpty(colorTheme))
                        {
                            bool? result = ParseThemeGuid(colorTheme!);
                            if (result.HasValue)
                            {
                                Logger.Info($"[ThemeService] Registry RESULT: IsLight={result.Value} (from ColorTheme)");
                                return result;
                            }
                        }
                    }
                    else
                    {
                        Logger.Warn($"[ThemeService] Registry: key '{themePath}' not found");
                    }

                    // 路径二：General\CurrentTheme (integer: 0=Blue, 1=Dark, 2=Light, 3=Dark+)
                    var generalPath = subKeyName + @"\General";
                    using var generalKey = vsKey.OpenSubKey(generalPath);
                    if (generalKey != null)
                    {
                        var currentTheme = generalKey.GetValue("CurrentTheme");
                        Logger.Info($"[ThemeService] Registry: {generalPath}\\CurrentTheme='{currentTheme ?? "null"}' (type={currentTheme?.GetType().Name ?? "null"})");

                        if (currentTheme is int themeInt)
                        {
                            // VS 2022: 0=Blue(Light), 1=Dark, 2=Light, 3=Dark(extra contrast)
                            bool isLight = themeInt == 0 || themeInt == 2;
                            Logger.Info($"[ThemeService] Registry RESULT: IsLight={isLight} (from CurrentTheme={themeInt})");
                            return isLight;
                        }
                    }
                    else
                    {
                        Logger.Warn($"[ThemeService] Registry: key '{generalPath}' not found");
                    }
                }

                Logger.Warn("[ThemeService] Registry: no VS 17.x/18.x keys found");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[ThemeService] Registry FAILED: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析 ColorTheme GUID 字符串。
        /// </summary>
        private static bool? ParseThemeGuid(string guidString)
        {
            // de3dbbcd-f642-433c-8353-8f1df4370aba = Light
            // a4d6a176-b948-4b29-8c66-53c97a1ed7d0 = Blue (also light)
            // 1ded0138-47ce-435e-84ef-9ec1f439b749 = Dark
            if (guidString.Contains("de3dbbcd") || guidString.Contains("a4d6a176"))
                return true;
            if (guidString.Contains("1ded0138"))
                return false;
            return null; // unknown GUID
        }

        /// <summary>
        /// 当前实际渲染是否为浅色模式（综合用户设置和 VS 主题）。
        /// </summary>
        public bool IsLight
        {
            get
            {
                bool result = _userThemeMode switch
                {
                    ThemeMode.Light => true,
                    ThemeMode.Dark => false,
                    _ => IsVSThemeLight
                };
                Logger.Info($"[ThemeService] IsLight accessed: Mode={_userThemeMode}, CachedVS={_cachedIsVSThemeLight}, Result={result}");
                return result;
            }
        }

        /// <summary>
        /// 当前有效的 Highlight.js CSS CDN 链接。
        /// </summary>
        public string HighlightJsCdnStyle
        {
            get => GetHighlightJsCdnStyle(IsLight);
        }

        /// <summary>按目标主题返回 Highlight.js 样式地址。</summary>
        public static string GetHighlightJsCdnStyle(bool isLight) => isLight
            ? "https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/styles/github.min.css"
            : "https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/styles/github-dark.min.css";

        /// <summary>
        /// 获取当前主题对应的页面 CSS（用于 WebView2）。
        /// </summary>
        public string PageCss => (IsLight ? LightPageCss : DarkPageCss)
            + ".icf{font-family:'Segoe Fluent Icons','Segoe MDL2 Assets';font-size:12px;line-height:1;"
            + "margin:0 4px 0 0;vertical-align:-1px;display:inline-block}";

        #endregion

        #region Constructor & VS Theme Subscription

        private ThemeService()
        {
        }

        private void SubscribeToVSThemeChanges()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Logger.Info($"[ThemeService] SubscribeToVSThemeChanges — on UI thread, refreshing cache...");
                RefreshVSThemeCache();
                VSColorTheme.ThemeChanged += OnVSThemeChanged;
                Logger.Info($"[ThemeService] Subscribed to VSColorTheme.ThemeChanged OK. Cached IsLight={_cachedIsVSThemeLight}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[ThemeService] SubscribeToVSThemeChanges FAILED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void OnVSThemeChanged(ThemeChangedEventArgs e)
        {
            Logger.Info($"[ThemeService] VS theme changed.");
            // 先刷新缓存，再通知
            RefreshVSThemeCache();
            if (_userThemeMode == ThemeMode.Auto)
            {
                OnThemeChanged();
            }
        }

        private void OnThemeChanged()
        {
            bool isLight = IsLight;
            Logger.Info($"[ThemeService] Theme changed: IsLight={isLight}, Mode={_userThemeMode}");

            // 保存设置
            try
            {
                var options = DeepSeekOptionsPage.Instance;
                if (options != null)
                {
                    options.ThemeMode = _userThemeMode;
                }
            }
            catch { }

            ThemeChanged?.Invoke(isLight);
        }

        #endregion

        #region CSS Definitions

        /// <summary>
        /// 深色主题 CSS。正文已外移至 <c>Resources\Chat\Css\dark.css</c>，
        /// 可独立维护并接受 CSS 校验；此处保留原访问方式。
        /// </summary>
        private static string DarkPageCss => WebAssetLoader.GetCss("dark");

        /// <summary>
        /// 浅色主题 CSS。正文已外移至 <c>Resources\Chat\Css\light.css</c>，
        /// 可独立维护并接受 CSS 校验；此处保留原访问方式。
        /// </summary>
        private static string LightPageCss => WebAssetLoader.GetCss("light");

        #endregion

        #region IDisposable

        public void Dispose()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                VSColorTheme.ThemeChanged -= OnVSThemeChanged;
            }
            catch { }
        }

        #endregion

        #endregion // VS Theme Detection
    }
}
