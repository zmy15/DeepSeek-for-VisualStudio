using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DeepSeek_v4_for_VisualStudio.CodeCompletion
{
    /// <summary>
    /// 内联预测管理器。管理单个 <see cref="IWpfTextView"/> 的幽灵文本代码补全。
    /// 使用 <see cref="GhostTextTagger"/> 渲染灰色装饰文本，
    /// 通过 <see cref="DeepSeekApiService"/> 调用 DeepSeek API 获取预测。
    /// </summary>
    internal class InlinePredictionManager
    {
        #region Constants

        private const int MAX_CACHE_SIZE = 10;
        private const int FIM_MAX_TOKENS = 256;

        #endregion

        #region Properties

        private readonly DeepSeekOptionsPage options;
        private readonly IWpfTextView view;
        private readonly ITextStructureNavigator structureNavigator;
        private readonly ConcurrentDictionary<string, string> cache = new();
        private readonly DispatcherTimer typingTimer;
        private readonly CompletionSettingsMonitor settingsMonitor;

        /// <summary>
        /// 是否直接用纯文本花括号扫描取上下文（C/C++）。
        /// C/C++ 的导航器由 VC 语言服务实现，代码不完整时会抛 COMException(E_FAIL)，
        /// 只能退化成极短的窗口；直接走扫描器可稳定拿到"函数签名 → 右括号"的完整代码。
        /// 其他语言仍优先用导航器，失败时同样降级到该扫描器（见 <see cref="GetCompletionContext"/>）。
        /// </summary>
        private readonly bool useTextBasedContext;

        private CancellationTokenSource? cancellationTokenSource;
        private bool showingAutoComplete;
        private bool suppressNextSuggestion;
        private bool isCompletionEnabled;
        private bool isViewClosed;
        private bool completionRequestedWhileBusy;

        #endregion

        #region Constructors

        /// <summary>
        /// 初始化 <see cref="InlinePredictionManager"/> 实例，绑定到指定文本视图。
        /// </summary>
        /// <param name="options">扩展选项页。</param>
        /// <param name="view">要绑定的文本视图。</param>
        /// <param name="structureNavigator">文本结构导航器，用于获取方法/块级上下文边界。</param>
        public InlinePredictionManager(DeepSeekOptionsPage options, IWpfTextView view, ITextStructureNavigator structureNavigator)
        {
            this.options = options;
            this.view = view;
            this.structureNavigator = structureNavigator;
            this.useTextBasedContext = IsCppContentType(view);

            settingsMonitor = new CompletionSettingsMonitor(GetCurrentOptions);
            settingsMonitor.Changed += OnCompletionSettingsChanged;

            typingTimer = new DispatcherTimer();
            typingTimer.Tick += TypingTimer_Tick;

            this.view.TextBuffer.Changed += TextBuffer_Changed;
            this.view.Caret.PositionChanged += Caret_PositionChanged;
            this.view.Closed += OnViewClosed;

            ApplyCompletionSettings(
                settingsMonitor.Current,
                triggerWhenEnabled: false,
                logStatus: true);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 重启打字防抖定时器，允许触发新的预测请求（如用户按 Enter 后）。
        /// </summary>
        public void RestartTimer()
        {
            if (isViewClosed || !isCompletionEnabled)
            {
                return;
            }

            typingTimer.Stop();
            typingTimer.Start();
        }

        /// <summary>
        /// 通知建议已被接受。当连续补全选项关闭时，
        /// 下一次缓冲区变更不会触发新的预测请求。
        /// </summary>
        public void NotifySuggestionAccepted()
        {
            if (!settingsMonitor.Current.ContinueAfterAccept)
            {
                suppressNextSuggestion = true;
            }
        }

        /// <summary>
        /// 将光标周围的代码发送给 DeepSeek API，
        /// 获取预测后在编辑器中显示为内联幽灵文本。
        /// </summary>
        public async Task ShowAutocompleteAsync()
        {
            try
            {
                if (!isCompletionEnabled)
                {
                    return;
                }

                if (showingAutoComplete)
                {
                    return;
                }

                showingAutoComplete = true;
                cancellationTokenSource?.Cancel();
                cancellationTokenSource?.Dispose();
                cancellationTokenSource = new();

                CleanCache();

                int caretPosition = view.Caret.Position.BufferPosition.Position;

                string filePath = string.Empty;
                if (view.TextDataModel.DocumentBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument textDocument))
                {
                    filePath = textDocument.FilePath;
                }

                (string codeUp, string codeDown) = GetCompletionContext(caretPosition);

                string codeUpNormalized = RemoveBlankLines(NormalizeLineBreaks(codeUp)).Trim();
                string codeDownNormalized = RemoveBlankLines(NormalizeLineBreaks(codeDown)).Trim();

                string cacheKey = $"{filePath}:{codeUpNormalized}|{codeDownNormalized}";

                if (cache.TryGetValue(cacheKey, out string cachedPrediction))
                {
                    Logger.Info(LocalizationService.Instance["autocomplete.cacheHit"]);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationTokenSource.Token);

                    DisplayPrediction(cachedPrediction);
                    return;
                }

                // ── Call DeepSeek FIM API for completion ──
                int totalContextLen = codeUpNormalized.Length + codeDownNormalized.Length;
                Logger.Info(string.Format(LocalizationService.Instance["autocomplete.requestingApi"], totalContextLen));
                string? prediction = await GetPredictionFromApiAsync(codeUpNormalized, codeDownNormalized, cancellationTokenSource.Token);

                if (cancellationTokenSource.Token.IsCancellationRequested || string.IsNullOrWhiteSpace(prediction))
                {
                    Logger.Info(LocalizationService.Instance["autocomplete.apiEmpty"]);
                    return;
                }

                if (!isCompletionEnabled)
                {
                    return;
                }

                prediction = FormatPrediction(prediction!);

                if (string.IsNullOrWhiteSpace(prediction))
                {
                    return;
                }

                cache[cacheKey] = prediction;

                Logger.Info(string.Format(LocalizationService.Instance["autocomplete.predictionDone"], prediction.Length, cache.Count));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationTokenSource.Token);

                if (!isCompletionEnabled)
                {
                    return;
                }

                DisplayPrediction(prediction);
            }
            catch (OperationCanceledException)
            {
                Logger.Info(LocalizationService.Instance["autocomplete.taskCancelled"]);
            }
            catch (Exception ex)
            {
                Logger.Error(string.Format(LocalizationService.Instance["autocomplete.showAutocompleteError"], ex.Message), ex);
            }
            finally
            {
                showingAutoComplete = false;
                if (completionRequestedWhileBusy)
                {
                    completionRequestedWhileBusy = false;
                    RestartTimer();
                }
            }
        }

        #endregion

        #region Private Methods - Prediction

        /// <summary>
        /// 调用 DeepSeek FIM（Fill-In-the-Middle）API 获取代码补全预测。
        /// 使用 beta/completions 端点，以 prompt/suffix 模式替代 chat/completions。
        /// </summary>
        private async Task<string?> GetPredictionFromApiAsync(string prompt, string suffix, CancellationToken cancellationToken)
        {
            try
            {
                var config = DeepSeekEndpointResolver.Resolve(GetCurrentOptions());
                if (config.IsCustom || string.IsNullOrEmpty(config.ApiKey))
                {
                    return null;
                }

                using var apiService = new DeepSeekApiService(
                    config.ApiKey,
                    config.Model,
                    baseUrl: config.BaseUrl,
                    isVision: config.IsVision,
                    isCustom: config.IsCustom);

                // FIM 补全：temperature=0 确保确定性输出，适合代码补全
                string result = await apiService.FimCompletionAsync(
                    prompt,
                    string.IsNullOrEmpty(suffix) ? null : suffix,
                    FIM_MAX_TOKENS,
                    cancellationToken);
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(string.Format(LocalizationService.Instance["autocomplete.apiError"], ex.Message), ex);
                return null;
            }
        }

        /// <summary>
        /// 使用 <see cref="GhostTextTagger"/> 显示预测为幽灵文本。必须在 UI 线程调用。
        /// </summary>
        private void DisplayPrediction(string prediction)
        {
            if (!isCompletionEnabled)
            {
                return;
            }

            if (view.Properties.TryGetProperty(GhostTextTagger.TaggerKey, out GhostTextTagger tagger))
            {
                int caretPosition = view.Caret.Position.BufferPosition.Position;
                tagger.SetSuggestion(prediction, caretPosition);
            }
        }

        /// <summary>
        /// 判断视图内容类型是否为 C/C++（VS 中 "C/C++" 是独立内容类型，不继承 code）。
        /// 内容类型读取失败时按非 C/C++ 处理，保持原有导航器路径。
        /// </summary>
        private static bool IsCppContentType(IWpfTextView textView)
        {
            try
            {
                return textView.TextBuffer.ContentType.IsOfType(DeepSeekCompletionContentTypes.Cpp);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 取光标处的 FIM 上下文（前缀 / 后缀）。
        /// C/C++ 直接走纯文本扫描；其他语言优先用语言服务导航器，
        /// 导航器失败（COMException / E_FAIL）时**降级**为同一套纯文本扫描，
        /// 而不是退回"光标前后各 500 字符"的硬窗口。
        /// </summary>
        private (string CodeUp, string CodeDown) GetCompletionContext(int caretPosition)
        {
            ITextSnapshot snapshot = view.TextBuffer.CurrentSnapshot;

            if (!useTextBasedContext &&
                TryGetNavigatorContext(snapshot, caretPosition, out string navigatorUp, out string navigatorDown))
            {
                return (navigatorUp, navigatorDown);
            }

            (int contextStart, int contextEnd) = GetTextBasedContextRange(snapshot, caretPosition);

            return (snapshot.GetText(contextStart, caretPosition - contextStart),
                    snapshot.GetText(caretPosition, contextEnd - caretPosition));
        }

        /// <summary>
        /// 用语言服务导航器取上下文；失败时返回 <c>false</c>，由调用方降级到纯文本扫描。
        /// </summary>
        private bool TryGetNavigatorContext(
            ITextSnapshot snapshot, int caretPosition, out string codeUp, out string codeDown)
        {
            codeUp = string.Empty;
            codeDown = string.Empty;

            SnapshotSpan enclosingSpan = GetEnclosingStructureSpan(snapshot, caretPosition, out bool degraded);

            // 导航器抛异常，或没给出覆盖光标的结构块（空 span / 起点在光标之后）→ 视为失败
            if (degraded || enclosingSpan.IsEmpty || enclosingSpan.Start.Position >= caretPosition)
            {
                return false;
            }

            int contextStart = enclosingSpan.Start.Position;
            codeUp = snapshot.GetText(contextStart, caretPosition - contextStart);

            int contextEnd = Math.Min(snapshot.Length, enclosingSpan.End.Position);
            codeDown = contextEnd > caretPosition
                ? snapshot.GetText(caretPosition, contextEnd - caretPosition)
                : string.Empty;

            return true;
        }

        /// <summary>
        /// C/C++ 的上下文区间：用 <see cref="CppBlockContextScanner"/> 做纯文本花括号扫描，
        /// 不调用语言服务，因此不会出现 E_FAIL 退化，能稳定拿到"函数签名 → 右括号"的完整代码。
        /// </summary>
        private (int Start, int End) GetTextBasedContextRange(ITextSnapshot snapshot, int caretPosition)
        {
            string text = snapshot.GetText();

            return CppBlockContextScanner.GetContext(text, caretPosition);
        }

        /// <summary>
        /// 使用 <see cref="ITextStructureNavigator"/> 迭代获取光标所在的结构块。
        /// 从光标位置开始，逐层向外扩展到方法/类级别（最多 5 层），
        /// 为 FIM 补全提供足够的方法级上下文。
        /// 注意：C/C++ 不走这条路径（VC 语言服务会抛 E_FAIL，见 <see cref="CppBlockContextScanner"/>）；
        /// 其他语言一旦失败，<paramref name="degraded"/> 置为 <c>true</c> 且异常只记 Debug，
        /// 由调用方降级为纯文本扫描，避免中断补全流程。
        /// </summary>
        private SnapshotSpan GetEnclosingStructureSpan(
            ITextSnapshot snapshot, int caretPosition, out bool degraded)
        {
            const int MaxNestingLevels = 5;

            degraded = false;
            var currentSpan = new SnapshotSpan(snapshot, caretPosition, 0);

            for (int level = 0; level < MaxNestingLevels; level++)
            {
                try
                {
                    SnapshotSpan enclosing = structureNavigator.GetSpanOfEnclosing(currentSpan);
                    if (enclosing.IsEmpty || enclosing == currentSpan)
                    {
                        break; // no more enclosing structure found
                    }

                    currentSpan = enclosing;
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    // C++ 语言服务在代码语法不完整时会抛 E_FAIL 等 COM 异常。
                    // 这是可预期的降级信号，不是缺陷：日志降到 Debug，上下文改由纯文本扫描补齐。
                    Logger.Debug($"[补全] GetSpanOfEnclosing COM 异常 (level={level})，降级为纯文本扫描: {ex.Message}");
                    degraded = true;
                    break;
                }
                catch (Exception ex)
                {
                    // 其他异常同样降级处理，不中断补全流程
                    Logger.Debug($"[补全] GetSpanOfEnclosing 异常 (level={level})，降级为纯文本扫描: {ex.Message}");
                    degraded = true;
                    break;
                }
            }

            return currentSpan;
        }

        /// <summary>
        /// 清理 FIM API 返回的预测文本。
        /// FIM 端点直接返回补全内容，无需剥离前缀/后缀。
        /// </summary>
        private static string FormatPrediction(string prediction)
        {
            prediction = prediction?.Trim() ?? string.Empty;

            // Remove code block markers if present
            prediction = Regex.Replace(prediction, @"^```[\w]*\s*", "");
            prediction = Regex.Replace(prediction, @"\s*```$", "");

            return RemoveBlankLines(prediction).Trim();
        }

        #endregion

        #region Private Methods - Utility

        /// <summary>
        /// 将换行符统一为 Environment.NewLine。
        /// </summary>
        private static string NormalizeLineBreaks(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            return text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine);
        }

        /// <summary>
        /// 移除连续空行，最多保留两个空行。
        /// </summary>
        private static string RemoveBlankLines(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            return Regex.Replace(text, @"(\s*\r?\n){3,}", Environment.NewLine + Environment.NewLine);
        }

        /// <summary>
        /// 缓存超限时清理最旧的条目（保留最近一半）。
        /// </summary>
        private void CleanCache()
        {
            if (cache.Count > MAX_CACHE_SIZE)
            {
                List<string> keysToRemove = cache.Keys.Take(cache.Count - (MAX_CACHE_SIZE / 2)).ToList();

                foreach (string key in keysToRemove)
                {
                    cache.TryRemove(key, out _);
                }
            }
        }

        #endregion

        #region Private Methods - Event Handlers

        private DeepSeekOptionsPage GetCurrentOptions()
            => DeepSeekOptionsPage.Instance ?? options;

        private void OnCompletionSettingsChanged(CompletionRuntimeSettings settings)
        {
            if (isViewClosed)
            {
                return;
            }

            ApplyCompletionSettings(
                settings,
                triggerWhenEnabled: true,
                logStatus: false);
        }

        /// <summary>
        /// Applies the latest completion settings to this live view.
        /// The view is bound once, so enable/disable transitions must be handled here
        /// rather than relying on a package or text-view restart.
        /// </summary>
        private void ApplyCompletionSettings(
            CompletionRuntimeSettings settings,
            bool triggerWhenEnabled,
            bool logStatus)
        {
            bool wasEnabled = isCompletionEnabled;
            isCompletionEnabled = settings.Enabled;
            typingTimer.Interval = TimeSpan.FromMilliseconds(settings.DelayMilliseconds);

            if (!settings.Enabled)
            {
                typingTimer.Stop();
                cancellationTokenSource?.Cancel();
                suppressNextSuggestion = false;
                completionRequestedWhileBusy = false;

                if (view.Properties.TryGetProperty(GhostTextTagger.TaggerKey, out GhostTextTagger tagger))
                {
                    tagger.ClearSuggestion();
                }
            }
            else if (!wasEnabled && triggerWhenEnabled)
            {
                RestartTimer();
            }

            if (logStatus || wasEnabled != settings.Enabled)
            {
                string status = settings.Enabled
                    ? LocalizationService.Instance["autocomplete.statusEnabled"]
                    : LocalizationService.Instance["autocomplete.statusDisabled"];
                Logger.Info(string.Format(LocalizationService.Instance["autocomplete.status"], status));
            }
        }

        /// <summary>
        /// 用户移动光标时清除当前幽灵文本，确保预测不会停留在错误位置。
        /// </summary>
        private void Caret_PositionChanged(object sender, CaretPositionChangedEventArgs e)
        {
            if (view.Properties.TryGetProperty(GhostTextTagger.TaggerKey, out GhostTextTagger tagger))
            {
                tagger.ClearSuggestion();
            }
        }

        /// <summary>
        /// 用户编辑缓冲区时重启打字防抖定时器，并清除当前幽灵文本。
        /// </summary>
        private void TextBuffer_Changed(object sender, TextContentChangedEventArgs e)
        {
            if (view.Properties.TryGetProperty(GhostTextTagger.TaggerKey, out GhostTextTagger tagger))
            {
                tagger.ClearSuggestion();
            }

            if (!isCompletionEnabled)
            {
                return;
            }

            if (suppressNextSuggestion)
            {
                suppressNextSuggestion = false;
                return;
            }

            typingTimer.Stop();
            typingTimer.Start();
        }

        /// <summary>
        /// 打字防抖到期时触发：请求新的代码预测。
        /// </summary>
#pragma warning disable VSTHRD100 // async void 是 WPF 事件处理器的必要模式，异常已在内部捕获
        private async void TypingTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                typingTimer.Stop();

                if (!isCompletionEnabled)
                {
                    return;
                }

                if (showingAutoComplete)
                {
                    completionRequestedWhileBusy = true;
                }
                else
                {
                    Logger.Info(LocalizationService.Instance["autocomplete.debounceTriggered"]);
                    await ShowAutocompleteAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(string.Format(LocalizationService.Instance["autocomplete.timerTickError"], ex.Message), ex);
            }
        }
#pragma warning restore VSTHRD100

        /// <summary>
        /// 视图关闭时清理所有资源和事件订阅。
        /// </summary>
        private void OnViewClosed(object sender, EventArgs e)
        {
            try
            {
                isViewClosed = true;
                settingsMonitor.Changed -= OnCompletionSettingsChanged;
                settingsMonitor.Dispose();

                view.TextBuffer.Changed -= TextBuffer_Changed;
                view.Caret.PositionChanged -= Caret_PositionChanged;
                view.Closed -= OnViewClosed;

                typingTimer.Stop();
                typingTimer.Tick -= TypingTimer_Tick;
                completionRequestedWhileBusy = false;

                cancellationTokenSource?.Cancel();
                cancellationTokenSource?.Dispose();
                cancellationTokenSource = null!;

                if (view.Properties.TryGetProperty(GhostTextTagger.TaggerKey, out GhostTextTagger tagger))
                {
                    tagger.ClearSuggestion();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(string.Format(LocalizationService.Instance["autocomplete.viewClosedError"], ex.Message), ex);
            }
        }

        #endregion
    }
}
