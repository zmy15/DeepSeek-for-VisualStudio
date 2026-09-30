using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.Web.WebView2.Core;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// Description of a single web page capture request.
    /// </summary>
    public sealed class WebPageCaptureRequest
    {
        /// <summary>Absolute http/https URL to render.</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>Capture the whole document instead of the visible viewport.</summary>
        public bool FullPage { get; set; } = true;

        /// <summary>Viewport width in pixels. Zero selects the default.</summary>
        public int ViewportWidth { get; set; }

        /// <summary>Viewport height in pixels. Zero selects the default.</summary>
        public int ViewportHeight { get; set; }

        /// <summary>Optional maximum output width. Zero means only the long-edge cap applies.</summary>
        public int MaxWidth { get; set; }

        /// <summary>Destination PNG path. Empty selects a file in the capture temp folder.</summary>
        public string SavePath { get; set; } = string.Empty;

        /// <summary>Navigation timeout in milliseconds. Zero selects the default.</summary>
        public int NavigationTimeoutMs { get; set; }

        /// <summary>Extra delay after navigation completes, in milliseconds.</summary>
        public int SettleDelayMs { get; set; }
    }

    /// <summary>
    /// Outcome of a web page capture.
    /// </summary>
    public sealed class WebPageCaptureResult
    {
        public string SavePath { get; set; } = string.Empty;
        public string PageTitle { get; set; } = string.Empty;
        public string FinalUrl { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public int SourceWidth { get; set; }
        public int SourceHeight { get; set; }
        public bool FullPage { get; set; }
    }

    /// <summary>
    /// Renders a web page in an off-screen WebView2 host and captures it as a PNG.
    ///
    /// Design notes (all verified empirically against the Chromium 147 fixed-version
    /// runtime this extension ships):
    ///  - A dedicated STA thread owns a message pump plus a hidden host window placed
    ///    far off-screen. The window must be SHOWN (not merely created) and must stay
    ///    visible to the compositor, otherwise Chromium throttles rendering and the
    ///    capture comes back blank. Parking it off-screen keeps it invisible to the user.
    ///  - Full-page capture uses the DevTools protocol (Page.captureScreenshot with
    ///    captureBeyondViewport) which returns the whole document in one image.
    ///    CapturePreviewAsync is the viewport-only fallback.
    ///  - The WebView2 environment prefers a fixed-version runtime placed next to the
    ///    assembly (that folder exists in the build output but is NOT packaged into the
    ///    VSIX), so in a deployed extension this falls back to the Evergreen runtime -
    ///    the same dependency the chat pane already has.
    /// </summary>
    public sealed class WebPageCaptureService
    {
        /// <summary>Process-wide instance: one STA thread and one browser environment are shared.</summary>
        public static WebPageCaptureService Instance { get; } = new WebPageCaptureService();

        public const int DefaultViewportWidth = 1280;
        public const int DefaultViewportHeight = 900;
        public const int DefaultMaxLongEdgePx = 2048;
        public const int DefaultNavigationTimeoutMs = 30000;
        public const int DefaultSettleDelayMs = 700;

        private const int MinViewportWidth = 320;
        private const int MinViewportHeight = 240;
        private const int MaxViewportEdge = 4096;

        /// <summary>Chromium flags that stop off-screen windows from being de-prioritised.</summary>
        private const string AntiThrottleArguments =
            "--disable-backgrounding-occluded-windows --disable-renderer-backgrounding --disable-background-timer-throttling";

        /// <summary>Off-screen parking position: far outside any real monitor layout.</summary>
        private const int OffscreenCoordinate = -20000;

        private readonly object _gate = new object();
        private readonly SemaphoreSlim _captureLock = new SemaphoreSlim(1, 1);

        private Thread? _worker;
        private Form? _host;
        private CoreWebView2Environment? _environment;
        private string? _environmentError;

        private WebPageCaptureService() { }

        /// <summary>
        /// Normalize user input into an absolute http/https URL.
        /// A missing scheme is treated as https. Other schemes are rejected.
        /// </summary>
        public static bool TryNormalizeUrl(string? raw, out string normalized)
        {
            normalized = string.Empty;

            string candidate = (raw ?? string.Empty).Trim();
            if (candidate.Length == 0)
                return false;

            if (candidate.IndexOf("://", StringComparison.Ordinal) < 0)
                candidate = "https://" + candidate;

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
                return false;

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return false;

            normalized = uri.ToString();
            return true;
        }

        /// <summary>Render the page and write a PNG.</summary>
        public async Task<WebPageCaptureResult> CaptureAsync(WebPageCaptureRequest request, CancellationToken ct)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (!TryNormalizeUrl(request.Url, out string normalizedUrl))
                throw new ArgumentException("A valid absolute http/https url is required.", nameof(request));

            EnsureWorker();

            await _captureLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Form host = _host ?? throw new InvalidOperationException("Capture host window is not available.");

                var completion = new TaskCompletionSource<WebPageCaptureResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                // Marshal onto the capture thread's message pump. Every WebView2 call below
                // must stay on that thread, which is why the awaits inside do not use
                // ConfigureAwait(false).
                host.BeginInvoke((Action)(async () =>
                {
                    try
                    {
                        completion.TrySetResult(await CaptureCoreAsync(normalizedUrl, request, ct));
                    }
                    catch (OperationCanceledException)
                    {
                        completion.TrySetCanceled();
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                }));

                return await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                _captureLock.Release();
            }
        }

        #region Worker thread

        /// <summary>
        /// Start the STA capture thread on first use. The thread stays alive for the
        /// lifetime of the process and is marked background so it never blocks shutdown.
        /// </summary>
        private void EnsureWorker()
        {
            lock (_gate)
            {
                if (_worker != null)
                    return;

                using var ready = new ManualResetEventSlim(false);
                Exception? startupError = null;

                var thread = new Thread(() =>
                {
                    try
                    {
                        var form = new Form
                        {
                            StartPosition = FormStartPosition.Manual,
                            Location = new Point(OffscreenCoordinate, OffscreenCoordinate),
                            ClientSize = new Size(DefaultViewportWidth, DefaultViewportHeight),
                            ShowInTaskbar = false,
                            FormBorderStyle = FormBorderStyle.None,
                            AutoScaleMode = AutoScaleMode.None,
                            MinimizeBox = false,
                            MaximizeBox = false,
                            Text = "DeepSeekVS-WebCapture",
                        };

                        _host = form;

                        // Show() is required: a merely created (never shown) window is
                        // treated as occluded and Chromium stops producing frames.
                        form.Show();

                        ready.Set();
                        Application.Run();
                    }
                    catch (Exception ex)
                    {
                        startupError = ex;
                        ready.Set();
                    }
                })
                {
                    IsBackground = true,
                    Name = "DeepSeekVS-WebCapture",
                };

                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();

                if (!ready.Wait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("Timed out starting the web capture thread.");

                if (startupError != null)
                    throw new InvalidOperationException("Failed to start the web capture thread.", startupError);

                _worker = thread;
                Logger.Info("[webcapture] Capture thread started");
            }
        }

        #endregion

        #region Environment

        private async Task<CoreWebView2Environment> GetEnvironmentAsync()
        {
            if (_environment != null)
                return _environment;

            if (_environmentError != null)
                throw new InvalidOperationException(_environmentError);

            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = AntiThrottleArguments,
            };

            string? browserFolder = ResolveBundledRuntimeFolder();
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeepSeekVS", "WebCapture");

            try
            {
                Directory.CreateDirectory(userDataFolder);

                // A dedicated user data folder keeps this environment independent from the
                // chat pane's WebView2 profile.
                _environment = await CoreWebView2Environment.CreateAsync(browserFolder, userDataFolder, options);
                Logger.Info($"[webcapture] WebView2 environment ready: {_environment.BrowserVersionString}"
                            + (browserFolder == null ? " (Evergreen)" : " (bundled fixed version)"));
                return _environment;
            }
            catch (Exception ex)
            {
                _environmentError = ex.Message;
                Logger.Error($"[webcapture] Failed to create WebView2 environment: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Locate a fixed-version runtime placed next to the extension assembly.
        /// Returns null (Evergreen fallback) when absent, which is the normal case for a
        /// VSIX-installed extension because the runtime folder is not packaged.
        /// </summary>
        private static string? ResolveBundledRuntimeFolder()
        {
            try
            {
                string baseDir = Path.GetDirectoryName(typeof(WebPageCaptureService).Assembly.Location) ?? string.Empty;
                if (baseDir.Length == 0)
                    return null;

                string candidate = Path.Combine(baseDir, "WebView2");
                return File.Exists(Path.Combine(candidate, "msedgewebview2.exe")) ? candidate : null;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[webcapture] Bundled runtime probe failed: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Capture

        private async Task<WebPageCaptureResult> CaptureCoreAsync(
            string url, WebPageCaptureRequest request, CancellationToken ct)
        {
            CoreWebView2Environment environment = await GetEnvironmentAsync();

            Form host = _host ?? throw new InvalidOperationException("Capture host window is not available.");

            int viewportWidth = Clamp(
                request.ViewportWidth > 0 ? request.ViewportWidth : DefaultViewportWidth,
                MinViewportWidth, MaxViewportEdge);
            int viewportHeight = Clamp(
                request.ViewportHeight > 0 ? request.ViewportHeight : DefaultViewportHeight,
                MinViewportHeight, MaxViewportEdge);

            host.ClientSize = new Size(viewportWidth, viewportHeight);

            CoreWebView2Controller? controller = null;
            try
            {
                controller = await environment.CreateCoreWebView2ControllerAsync(host.Handle);
                controller.Bounds = new Rectangle(0, 0, viewportWidth, viewportHeight);
                controller.IsVisible = true;

                CoreWebView2 core = controller.CoreWebView2;

                // DevTools being disabled does not block CallDevToolsProtocolMethodAsync
                // (verified), so keep the surface locked down.
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                // Modal script dialogs would otherwise block rendering indefinitely.
                core.Settings.AreDefaultScriptDialogsEnabled = false;

                await NavigateAsync(core, url, request, ct);

                int settleDelay = request.SettleDelayMs > 0 ? request.SettleDelayMs : DefaultSettleDelayMs;
                await Task.Delay(settleDelay, ct);

                if (request.FullPage)
                    await TriggerLazyLoadAsync(core);

                string pageTitle = await ReadScriptStringAsync(core, "document.title");
                string finalUrl = await ReadScriptStringAsync(core, "location.href");
                if (string.IsNullOrWhiteSpace(finalUrl))
                    finalUrl = url;

                (byte[] pngBytes, string method) = await CapturePngAsync(core, request.FullPage);

                string savePath = ResolveSavePath(request.SavePath);
                ScaleAndSavePng(pngBytes, savePath, request.MaxWidth,
                    out int width, out int height, out int sourceWidth, out int sourceHeight);

                Logger.Info($"[webcapture] Captured {finalUrl} via {method}: "
                            + $"{sourceWidth}x{sourceHeight} -> {width}x{height}");

                return new WebPageCaptureResult
                {
                    SavePath = savePath,
                    PageTitle = pageTitle,
                    FinalUrl = finalUrl,
                    Method = method,
                    Width = width,
                    Height = height,
                    SourceWidth = sourceWidth,
                    SourceHeight = sourceHeight,
                    FullPage = request.FullPage,
                };
            }
            finally
            {
                if (controller != null)
                {
                    try { controller.Close(); }
                    catch (Exception ex) { Logger.Warn($"[webcapture] controller.Close failed: {ex.Message}"); }
                }
            }
        }

        private static async Task NavigateAsync(
            CoreWebView2 core, string url, WebPageCaptureRequest request, CancellationToken ct)
        {
            int timeoutMs = request.NavigationTimeoutMs > 0
                ? request.NavigationTimeoutMs
                : DefaultNavigationTimeoutMs;

            var navigated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = (s, e) =>
                navigated.TrySetResult(e.IsSuccess);

            core.NavigationCompleted += handler;
            try
            {
                core.Navigate(url);

                using (ct.Register(() => navigated.TrySetCanceled(ct)))
                {
                    Task timeout = Task.Delay(timeoutMs, ct);
                    Task finished = await Task.WhenAny(navigated.Task, timeout);

                    if (finished == timeout && !navigated.Task.IsCompleted)
                        throw new TimeoutException($"Navigation timed out after {timeoutMs} ms.");

                    bool ok = await navigated.Task;
                    if (!ok)
                        throw new InvalidOperationException("The page failed to load.");
                }
            }
            finally
            {
                core.NavigationCompleted -= handler;
            }
        }

        /// <summary>
        /// Walk down the document so lazy-loaded images below the fold actually load
        /// before a full-page capture, then return to the top. Best effort only.
        /// </summary>
        private static async Task TriggerLazyLoadAsync(CoreWebView2 core)
        {
            try
            {
                int height = await ReadScriptIntAsync(core, "document.documentElement.scrollHeight");
                int viewport = await ReadScriptIntAsync(core, "window.innerHeight");
                if (height <= viewport || viewport <= 0)
                    return;

                int steps = Math.Min(12, (height + viewport - 1) / viewport);
                for (int i = 1; i <= steps; i++)
                {
                    int y = (int)((long)height * i / steps);
                    await core.ExecuteScriptAsync(
                        "window.scrollTo(0," + y.ToString(CultureInfo.InvariantCulture) + ")");
                    await Task.Delay(80);
                }

                await core.ExecuteScriptAsync("window.scrollTo(0,0)");
                await Task.Delay(150);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[webcapture] Lazy-load scroll pass skipped: {ex.Message}");
            }
        }

        /// <summary>
        /// Produce the PNG bytes. Full-page capture goes through the DevTools protocol and
        /// falls back to a viewport capture if that channel is unavailable.
        /// </summary>
        private static async Task<(byte[] Png, string Method)> CapturePngAsync(CoreWebView2 core, bool fullPage)
        {
            if (fullPage)
            {
                try
                {
                    string json = await core.CallDevToolsProtocolMethodAsync(
                        "Page.captureScreenshot",
                        "{\"format\":\"png\",\"captureBeyondViewport\":true}");

                    return (ExtractCdpImageData(json), "cdp-fullpage");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[webcapture] Full-page CDP capture failed, using viewport: {ex.Message}");
                }
            }

            using (var stream = new MemoryStream())
            {
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                return (stream.ToArray(), fullPage ? "viewport-fallback" : "viewport");
            }
        }

        private static byte[] ExtractCdpImageData(string cdpJson)
        {
            using JsonDocument document = JsonDocument.Parse(cdpJson);
            if (document.RootElement.TryGetProperty("data", out JsonElement data)
                && data.ValueKind == JsonValueKind.String)
            {
                string? base64 = data.GetString();
                if (!string.IsNullOrEmpty(base64))
                    return Convert.FromBase64String(base64);
            }

            throw new InvalidOperationException("DevTools protocol returned no image data.");
        }

        #endregion

        #region Script helpers

        private static async Task<string> ReadScriptStringAsync(CoreWebView2 core, string script)
        {
            try
            {
                string json = await core.ExecuteScriptAsync(script);
                if (json.Length >= 2 && json[0] == '"' && json[json.Length - 1] == '"')
                    return JsonSerializer.Deserialize<string>(json) ?? string.Empty;
                return json == "null" ? string.Empty : json;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[webcapture] Script '{script}' failed: {ex.Message}");
                return string.Empty;
            }
        }

        private static async Task<int> ReadScriptIntAsync(CoreWebView2 core, string script)
        {
            string raw = await ReadScriptStringAsync(core, script);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
        }

        #endregion

        #region Output

        private static string ResolveSavePath(string requestedPath)
        {
            if (!string.IsNullOrWhiteSpace(requestedPath))
            {
                string full = Path.GetFullPath(requestedPath);
                string? directory = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                return full;
            }

            Directory.CreateDirectory(BuiltInTools.CaptureWindowTool.CaptureTempDir);
            return Path.Combine(
                BuiltInTools.CaptureWindowTool.CaptureTempDir,
                $"webpage_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        }

        /// <summary>
        /// Proportionally downscale (bounded by maxWidth and the long-edge cap) and save as PNG.
        /// </summary>
        private static void ScaleAndSavePng(
            byte[] pngBytes, string savePath, int maxWidth,
            out int width, out int height, out int sourceWidth, out int sourceHeight)
        {
            using var source = new MemoryStream(pngBytes);
            using var bitmap = new Bitmap(source);

            sourceWidth = bitmap.Width;
            sourceHeight = bitmap.Height;

            double scale = 1.0;
            if (maxWidth > 0 && sourceWidth > maxWidth)
                scale = Math.Min(scale, (double)maxWidth / sourceWidth);

            int longEdge = Math.Max(sourceWidth, sourceHeight);
            if (longEdge > DefaultMaxLongEdgePx)
                scale = Math.Min(scale, (double)DefaultMaxLongEdgePx / longEdge);

            if (scale >= 1.0)
            {
                bitmap.Save(savePath, ImageFormat.Png);
                width = sourceWidth;
                height = sourceHeight;
                return;
            }

            int targetWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
            int targetHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));

            using var scaled = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(scaled))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.DrawImage(bitmap, 0, 0, targetWidth, targetHeight);
            }

            scaled.Save(savePath, ImageFormat.Png);
            width = targetWidth;
            height = targetHeight;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        #endregion

        /// <summary>
        /// Stop the capture thread. The thread is a background thread, so this is
        /// best effort and process shutdown does not depend on it.
        /// </summary>
        public void Dispose()
        {
            Form? host = _host;
            _host = null;
            _worker = null;

            if (host == null)
                return;

            try
            {
                host.BeginInvoke((Action)(() =>
                {
                    try { host.Close(); }
                    catch { }
                    Application.ExitThread();
                }));
            }
            catch (Exception ex)
            {
                Logger.Warn($"[webcapture] Shutdown failed: {ex.Message}");
            }
        }
    }
}
