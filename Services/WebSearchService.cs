using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>fetch_webpage 的正文获取方式（用户可在设置中选择）。</summary>
    public enum WebFetchMode
    {
        /// <summary>DeepSeek 官方 Fetch 接口；不在官方 API 来源时自动回退本地解析。</summary>
        Official,

        /// <summary>本地抓取 HTML 并自行解析正文。</summary>
        Local,
    }

    /// <summary>
    /// 联网搜索服务。
    /// 搜索优先级：百度千帆 API（需 API Key）→ Bing API（需 Azure Key）→ DuckDuckGo（免费备用）。
    ///
    ///  计费提醒：
    /// - 百度搜索：每月免费 1500 次（约每天 50 次），超出后按量后付费
    ///   详情: https://cloud.baidu.com/doc/qianfan/s/Mmh4sv6ec
    /// - Bing 搜索：每月免费 1000 次（S1 免费层），超出后按量付费
    ///   获取 Key: https://portal.azure.com/ → 创建 Bing Search 资源
    /// - DuckDuckGo：完全免费，但结果质量可能不如百度/Bing
    /// - 当百度/Bing 额度耗尽时会自动切换到 DuckDuckGo
    /// </summary>
    public class WebSearchService : IWebSearchService
    {
        #region Constants

        /// <summary>百度千帆搜索 API 端点</summary>
        private const string BaiduSearchApiUrl = "https://qianfan.baidubce.com/v2/ai_search/web_search";

        /// <summary>Bing Web Search API v7 端点</summary>
        private const string BingSearchApiUrl = "https://api.bing.microsoft.com/v7.0/search";

        /// <summary>DuckDuckGo Lite 搜索端点（免费备用，无 JS，易解析）</summary>
        private const string DuckDuckGoLiteUrl = "https://lite.duckduckgo.com/lite/";

        /// <summary>DeepSeek 官方 Search 接口端点</summary>
        public const string DeepSeekSearchApiUrl = "https://api.deepseek.com/v1/tools/search";

        /// <summary>DeepSeek 官方 Fetch 接口端点</summary>
        public const string DeepSeekFetchApiUrl = "https://api.deepseek.com/v1/tools/fetch";

        /// <summary>搜索结果最大条数</summary>
        private const int MaxSearchResults = 10;

        /// <summary>DeepSeek 官方 Search/Fetch 请求超时（秒）。Fetch 需等待下游抓取，比通用搜索更宽松。</summary>
        private const int DeepSeekRequestTimeoutSeconds = 40;

        /// <summary>DeepSeek Search 接口单价（元 / 每 1000 次成功请求）。</summary>
        public const double SearchPriceCnyPer1K = 5.0;

        /// <summary>DeepSeek Fetch 接口单价（元 / 每 1000 次成功请求）。</summary>
        public const double FetchPriceCnyPer1K = 5.0;

        /// <summary>DeepSeek Search 接口单价（美元 / 每 1000 次成功请求）。</summary>
        public const double SearchPriceUsdPer1K = 0.75;

        /// <summary>DeepSeek Fetch 接口单价（美元 / 每 1000 次成功请求）。</summary>
        public const double FetchPriceUsdPer1K = 0.75;

        /// <summary>请求超时（秒）</summary>
        private const int RequestTimeoutSeconds = 15;

        /// <summary>百度 API 额度耗尽错误码（常见值）</summary>
        private const int BaiduQuotaExhaustedCode = 17;

        /// <summary>fetch_webpage 结果文本中图片 URL 块的起始标记。</summary>
        public const string WebImagesBlockStart = "[WEB_IMAGES]";

        /// <summary>fetch_webpage 结果文本中图片 URL 块的结束标记。</summary>
        public const string WebImagesBlockEnd = "[/WEB_IMAGES]";

        /// <summary>单页网页最多提取的图片 URL 数（防止视觉模型上下文被图片撑爆）。</summary>
        public const int MaxImagesPerPage = 8;

        /// <summary>一次 fetch_webpage 结果最多携带的图片总数（递归抓取多页时兜底）。</summary>
        public const int MaxTotalImages = 10;

        /// <summary>单页网页最多提取的子链接数（供递归抓取，实际只会追 3 个）。</summary>
        public const int MaxLinksPerPage = 24;

        /// <summary>
        /// 视觉模型（deepseek-v4-flash-vision-exp）支持的图片扩展名。
        /// DeepSeek 官方支持 JPEG/PNG/GIF/WebP，且按文件实际内容而非文件名判定；
        /// 客户端无法廉价读取文件头，只能按路径扩展名做预过滤，
        /// 避免把 .svg 等不支持格式作为 image_url 直传导致 HTTP 400。
        /// </summary>
        private static readonly HashSet<string> VisionSupportedImageExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".png", ".jpg", ".jpeg", ".gif", ".webp",
            };

        #endregion

        #region Properties

        private readonly HttpClient _httpClient;
        /// <summary>DeepSeek 官方 Search/Fetch 专用客户端：抓取可能较慢，超时比通用搜索更宽松。</summary>
        private readonly HttpClient _deepSeekHttpClient;
        private string? _baiduApiKey;
        private string? _bingApiKey;
        /// <summary>DeepSeek 官方 API Key；仅官方 API 来源持有，官方 Search 与 Fetch 共用。</summary>
        private string? _officialApiKey;
        private WebFetchMode _webFetchMode = WebFetchMode.Official;
        private SearchProvider _activeProvider;
        private bool _isBaiduQuotaExhausted;

        // ── 官方 Search / Fetch 按次计费统计（每次成功且产生结果的请求计数）──
        private int _officialSearchSuccessCount;
        private int _officialFetchSuccessCount;

        /// <summary>
        /// 获取当前正在使用的搜索提供商。
        /// </summary>
        public SearchProvider ActiveProvider => _activeProvider;

        /// <summary>
        /// 百度额度是否已耗尽（本次会话内）。
        /// </summary>
        public bool IsBaiduQuotaExhausted => _isBaiduQuotaExhausted;

        /// <summary>
        /// 是否持有 DeepSeek 官方 API Key（即当前为官方 API 来源）。
        /// </summary>
        public bool HasOfficialApiKey => !string.IsNullOrWhiteSpace(_officialApiKey);

        /// <summary>
        /// 是否使用 DeepSeek 官方 Search 接口做联网搜索：
        /// 需「官方 API 来源 + 当前搜索引擎选中 DeepSeek」同时成立。
        /// </summary>
        public bool IsDeepSeekSearchEnabled
            => _activeProvider == SearchProvider.DeepSeek && HasOfficialApiKey;

        /// <summary>
        /// 是否使用 DeepSeek 官方 Fetch 接口抓取网页（fetch_webpage）。
        /// 设置选择「官方 API」且为官方 API 来源时为 true；
        /// 自定义端点或未配置官方 Key 时自动回退本地解析，返回 false。
        /// </summary>
        public bool UseOfficialWebFetch
            => _webFetchMode == WebFetchMode.Official && HasOfficialApiKey;

        // ── 官方 Search / Fetch 用量与计费（按次，见官方计费说明）──

        /// <summary>本会话官方 Search 成功（HTTP 200 且 results 非空）的请求次数。</summary>
        public int OfficialSearchSuccessCount => Volatile.Read(ref _officialSearchSuccessCount);

        /// <summary>本会话官方 Fetch 成功（HTTP 200 且返回正文）的请求次数。</summary>
        public int OfficialFetchSuccessCount => Volatile.Read(ref _officialFetchSuccessCount);

        /// <summary>
        /// 本会话官方 Search / Fetch 计费金额（人民币）。
        /// 按成功请求次数计费（每 1000 次 5 元），失败/空结果不计数。
        /// </summary>
        public double OfficialToolCostYuan =>
            OfficialSearchSuccessCount / 1000.0 * SearchPriceCnyPer1K +
            OfficialFetchSuccessCount / 1000.0 * FetchPriceCnyPer1K;

        /// <summary>
        /// 本会话官方 Search / Fetch 计费金额（美元）。
        /// 按成功请求次数计费（每 1000 次 $0.75），失败/空结果不计数。
        /// </summary>
        public double OfficialToolCostUsd =>
            OfficialSearchSuccessCount / 1000.0 * SearchPriceUsdPer1K +
            OfficialFetchSuccessCount / 1000.0 * FetchPriceUsdPer1K;

        /// <summary>重置官方 Search / Fetch 用量与费用统计（新会话开始时调用）。</summary>
        public void ResetOfficialToolStats()
        {
            Interlocked.Exchange(ref _officialSearchSuccessCount, 0);
            Interlocked.Exchange(ref _officialFetchSuccessCount, 0);
        }

        /// <summary>
        /// 从会话恢复官方 Search / Fetch 调用次数（重启 VS / 切换会话时调用）。
        /// 采用绝对值覆盖语义，避免与内存中的当前累计值叠加；负值按 0 处理。
        /// </summary>
        /// <param name="searchCount">官方 Search 成功次数</param>
        /// <param name="fetchCount">官方 Fetch 成功次数</param>
        public void RestoreOfficialToolStats(int searchCount, int fetchCount)
        {
            Interlocked.Exchange(ref _officialSearchSuccessCount, searchCount > 0 ? searchCount : 0);
            Interlocked.Exchange(ref _officialFetchSuccessCount, fetchCount > 0 ? fetchCount : 0);
        }

        /// <summary>
        /// 记录一次成功的官方工具请求。仅"HTTP 200 且产生结果"的请求会计费
        /// （Search 的 results 非空、Fetch 返回正文），失败/空结果/超时不计。
        /// </summary>
        /// <param name="isSearch">true 表示 Search，false 表示 Fetch</param>
        private void RecordOfficialToolSuccess(bool isSearch)
        {
            if (isSearch)
                Interlocked.Increment(ref _officialSearchSuccessCount);
            else
                Interlocked.Increment(ref _officialFetchSuccessCount);
        }

        #endregion

        #region Constructors

        public WebSearchService()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds),
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 DeepSeekVS/1.0");

            _deepSeekHttpClient = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            })
            {
                Timeout = TimeSpan.FromSeconds(DeepSeekRequestTimeoutSeconds),
            };
            _deepSeekHttpClient.DefaultRequestHeaders.Add("User-Agent", "DeepSeekVS/1.0");

            _activeProvider = SearchProvider.DuckDuckGo;
            _isBaiduQuotaExhausted = false;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 配置 DeepSeek 官方 API 来源的可用性（官方 Search 与 Fetch 共用同一官方 Key）。
        /// 仅记录 Key，不改动当前搜索提供商；调用方随后通过
        /// <see cref="ConfigureBaiduSearch"/> / <see cref="ConfigureBingSearch"/> /
        /// <see cref="ConfigureDuckDuckGo"/> / <see cref="ConfigureDeepSeekSearch"/> 选择引擎。
        /// 自定义端点传 null，行为与之前完全一致（第三方引擎 + 本地 HTML 抓取）。
        /// </summary>
        /// <param name="apiKey">DeepSeek 官方 API Key；为空表示官方 API 不可用</param>
        public void ConfigureOfficialApi(string? apiKey)
        {
            bool available = !string.IsNullOrWhiteSpace(apiKey);
            bool changed = available != HasOfficialApiKey;

            _officialApiKey = available ? apiKey : null;

            if (changed)
            {
                Logger.Info(available
                    ? "联网搜索与网页抓取: DeepSeek 官方 API 可用"
                    : "联网搜索与网页抓取: DeepSeek 官方 API 不可用");
            }
        }

        /// <summary>
        /// 选择 DeepSeek 官方自研搜索引擎（需官方 API 来源，否则回退 DuckDuckGo）。
        /// </summary>
        public void ConfigureDeepSeekSearch()
        {
            _activeProvider = HasOfficialApiKey
                ? SearchProvider.DeepSeek
                : SearchProvider.DuckDuckGo;
        }

        /// <summary>
        /// 配置百度千帆搜索 API Key。
        /// 设置后优先使用百度搜索，额度耗尽时自动回退到 DuckDuckGo。
        /// </summary>
        /// <param name="apiKey">百度千帆平台的 API Key（AppBuilder API Key）</param>
        public void ConfigureBaiduSearch(string apiKey)
        {
            _baiduApiKey = apiKey;
            _isBaiduQuotaExhausted = false;

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                _activeProvider = SearchProvider.Baidu;
                Logger.Info(LocalizationService.Instance["websearch.providerSwitched"]);
            }
            else
            {
                _activeProvider = SearchProvider.DuckDuckGo;
                Logger.Info(LocalizationService.Instance["websearch.baiduNotConfigured"]);
            }
        }

        /// <summary>
        /// 配置 Bing Web Search API Key（Azure 订阅密钥）。
        /// 设置后优先使用 Bing 搜索。
        /// </summary>
        /// <param name="apiKey">Azure Portal 中 Bing Search 资源的订阅密钥</param>
        public void ConfigureBingSearch(string apiKey)
        {
            _bingApiKey = apiKey;

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                _activeProvider = SearchProvider.Bing;
                Logger.Info("搜索提供商切换为: Bing (Azure)");
            }
            else
            {
                _activeProvider = SearchProvider.DuckDuckGo;
                Logger.Info("未配置 Bing API Key，使用 DuckDuckGo 搜索");
            }
        }

        /// <summary>
        /// 配置 fetch_webpage 的正文获取方式（官方 API / 本地解析）。
        /// 选择官方时若官方 Key 不可用（自定义端点），仍会自动回退本地解析。
        /// </summary>
        public void ConfigureWebFetchMode(WebFetchMode mode)
        {
            if (_webFetchMode == mode) return;

            _webFetchMode = mode;
            Logger.Info($"网页抓取方式切换为: {mode}");
        }

        /// <summary>
        /// 选择 DuckDuckGo 作为搜索引擎（免费备用，不影响 DeepSeek 官方 Fetch 的可用性）。
        /// </summary>
        public void ConfigureDuckDuckGo()
        {
            _activeProvider = SearchProvider.DuckDuckGo;
        }

        /// <summary>
        /// 执行联网搜索，返回格式化的搜索结果。
        /// 自动处理百度额度耗尽 → DuckDuckGo 的切换。
        /// </summary>
        /// <param name="query">搜索关键词</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <param name="searchRecency">时效过滤（可选）：week/month/semiyear/year</param>
        /// <param name="count">期望返回条数（可选）：1～50；为空使用各引擎默认值</param>
        /// <param name="originalQuestion">触发搜索的用户原始问题（可选，仅官方 Search 使用）</param>
        /// <returns>搜索结果列表（标题、URL、摘要）</returns>
        public async Task<List<WebSearchResult>> SearchAsync(
            string query,
            CancellationToken cancellationToken = default,
            string? searchRecency = null,
            int? count = null,
            string? originalQuestion = null)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<WebSearchResult>();

            Logger.Info($"开始联网搜索: \"{query}\", 提供商: {_activeProvider}");

            try
            {
                var ct = cancellationToken;

                // ── DeepSeek 官方 Search 接口（仅官方 API 来源启用）──
                if (IsDeepSeekSearchEnabled)
                {
                    var results = await SearchDeepSeekAsync(query, ct, searchRecency, count, originalQuestion);
                    if (results.Count > 0)
                        return results;

                    // 官方接口无结果或失败时，回退到用户配置的搜索引擎，避免搜索功能整体不可用。
                    Logger.Info("DeepSeek 官方搜索无结果，回退到 " + _activeProvider);
                }

                // ── 百度搜索（优先） ──
                if (_activeProvider == SearchProvider.Baidu && !_isBaiduQuotaExhausted)
                {
                    try
                    {
                        var results = await SearchBaiduAsync(query, ct, searchRecency);
                        if (results.Count > 0)
                            return results;

                        // 百度返回空结果，可能是额度问题，回退到 DuckDuckGo
                        _activeProvider = SearchProvider.DuckDuckGo;
                        Logger.Info(LocalizationService.Instance["websearch.baiduEmptyResult"]);
                    }
                    catch (BaiduQuotaExhaustedException ex)
                    {
                        Logger.Info($"百度额度已耗尽: {ex.Message}，切换到 DuckDuckGo");
                        _isBaiduQuotaExhausted = true;
                        _activeProvider = SearchProvider.DuckDuckGo;
                    }
                    catch (ApiKeyInvalidException)
                    {
                        // 重新抛出，让调用方（DeepSeekChatControl）处理：显示错误并停止，不静默回退
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"百度搜索失败: {ex.Message}", ex);
                        // 网络错误等不切换 provider，下次重试
                    }
                }

                // ── Bing 搜索 ──
                if (_activeProvider == SearchProvider.Bing)
                {
                    try
                    {
                        var results = await SearchBingAsync(query, ct, searchRecency);
                        if (results.Count > 0)
                            return results;

                        // Bing 返回空结果，回退到 DuckDuckGo
                        _activeProvider = SearchProvider.DuckDuckGo;
                        Logger.Info("Bing 搜索返回空结果，切换到 DuckDuckGo");
                    }
                    catch (ApiKeyInvalidException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Bing 搜索失败: {ex.Message}", ex);
                    }
                }

                // ── DuckDuckGo（备用） ──
                return await SearchDuckDuckGoAsync(query, cancellationToken);
            }
            catch (ApiKeyInvalidException)
            {
                // 重新抛出，让调用方（DeepSeekChatControl）处理：显示错误并停止
                throw;
            }
            catch (OperationCanceledException)
            {
                Logger.Info(LocalizationService.Instance["websearch.cancelled"]);
                return new List<WebSearchResult>();
            }
            catch (Exception ex)
            {
                Logger.Error($"联网搜索失败: {ex.Message}", ex);
                return new List<WebSearchResult>();
            }
        }

        /// <summary>
        /// 重置百度额度耗尽标记（新会话开始时调用）。
        /// </summary>
        public void ResetQuotaState()
        {
            if (_isBaiduQuotaExhausted && !string.IsNullOrWhiteSpace(_baiduApiKey))
            {
                _isBaiduQuotaExhausted = false;
                _activeProvider = SearchProvider.Baidu;
                Logger.Info(LocalizationService.Instance["websearch.baiduQuotaReset"]);
            }
        }

        /// <summary>
        /// 从指定 URL 抓取网页内容，同时提取纯文本、该页内图片与子链接的绝对 URL。
        /// 图片 URL 供视觉模型直读（只传 http(s) 链接，不下载、不转 base64），
        /// 子链接供 fetch_webpage 递归抓取。
        /// 这是"尽力而为"的操作，失败时返回 null 不影响主流程。
        /// </summary>
        /// <param name="url">网页 URL</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="maxContentLength">正文最大字符数；&lt;=0 表示不截断</param>
        /// <returns>抓取结果（纯文本 + 图片 + 子链接），失败返回 null</returns>
        public async Task<WebPageContent?> FetchWebPageContentWithImagesAsync(
            string url, CancellationToken ct = default, int maxContentLength = 0)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;

            // ── Punycode 编码域名，防止同形异义攻击（Homograph Attack）──
            url = EncodeUrlHostname(url);

            // ── DeepSeek 官方 Fetch 接口（仅官方 API 来源启用）：直接返回 Markdown 正文 ──
            if (UseOfficialWebFetch)
            {
                var page = await FetchWebPageViaDeepSeekAsync(url, ct, maxContentLength);
                if (page != null && !string.IsNullOrWhiteSpace(page.Text))
                    return page;

                Logger.Info($"DeepSeek Fetch 未返回可用内容，回退本地 HTML 抓取: {url}");
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Accept", "text/html,application/xhtml+xml");
                request.Headers.Add("Accept-Language", "zh-CN,zh;q=0.9");

                using var response = await _httpClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) return null;

                var html = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(html)) return null;

                // 重定向后以最终 URL 作为相对路径基准，保证相对地址解析正确。
                string baseUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? url;

                // ── 简易 HTML → 纯文本提取 ──
                string text = ExtractTextFromHtml(html);
                if (string.IsNullOrWhiteSpace(text)) return null;

                // maxContentLength > 0 时截断正文（fetch_webpage 的 maxContentLength 参数）
                if (maxContentLength > 0 && text.Length > maxContentLength)
                    text = text.Substring(0, maxContentLength);

                var images = ExtractImageUrls(html, baseUrl);
                var links = ExtractLinkUrls(html, baseUrl);
                return new WebPageContent { Text = text, ImageUrls = images, LinkUrls = links };
            }
            catch (Exception ex)
            {
                Logger.Info($"网页内容抓取失败 ({url}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 对 URL 中的主机名（域名）进行 Punycode 编码，防止同形异义攻击（IDN Homograph Attack）。
        /// 
        /// 同形异义攻击示例：
        /// 攻击者注册使用西里尔字母 'а' (U+0430) 替代拉丁字母 'a' (U+0061) 的域名，
        /// 例如 "аррӏе.com" 看起来像 "apple.com"，实际指向恶意站点。
        /// Punycode 编码将这些 Unicode 域名转为 "xn--" 前缀的 ASCII 形式，
        /// 使浏览器和 HTTP 客户端能够正确区分和处理。
        /// 
        /// 纯 ASCII 域名的 URL 不做任何修改直接返回。
        /// </summary>
        /// <param name="url">原始 URL（可能包含 Unicode 域名）</param>
        /// <returns>域名经过 Punycode 编码后的 URL</returns>
        public static string EncodeUrlHostname(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return url;

            try
            {
                // 尝试解析 URL
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    return url; // 无法解析的 URL 原样返回

                string host = uri.Host;

                // 快速路径：纯 ASCII 域名无需编码
                bool hasNonAscii = false;
                foreach (char c in host)
                {
                    if (c > 127)
                    {
                        hasNonAscii = true;
                        break;
                    }
                }
                if (!hasNonAscii)
                    return url;

                // 使用 .NET IdnMapping 进行 Punycode 编码
                var idn = new System.Globalization.IdnMapping
                {
                    UseStd3AsciiRules = true
                };
                string punycodeHost = idn.GetAscii(host);

                // 重建 URL：替换主机名部分
                string encodedUrl = url.Replace(
                    uri.Scheme + "://" + host,
                    uri.Scheme + "://" + punycodeHost);

                if (encodedUrl != url)
                {
                    Logger.Info($"[Punycode] 域名编码: {host} → {punycodeHost}");
                }

                return encodedUrl;
            }
            catch (Exception ex)
            {
                Logger.Info($"[Punycode] 域名编码失败 ({url}): {ex.Message}");
                return url; // 编码失败时原样返回
            }
        }

        /// <summary>
        /// 验证百度千帆 API Key 是否有效。
        /// 发送一个最小搜索请求，检查响应码。
        /// </summary>
        /// <returns>null 表示有效，否则返回错误描述</returns>
        public async Task<string?> ValidateBaiduApiKeyAsync()
        {
            if (string.IsNullOrWhiteSpace(_baiduApiKey))
                return LocalizationService.Instance["websearch.baiduApiKeyMissing"];

            try
            {
                var requestBody = new Dictionary<string, object>
                {
                    ["messages"] = new[] { new { role = "user", content = "test" } },
                    ["search_source"] = "baidu_search_v2",
                    ["resource_type_filter"] = new[] { new { type = "web", top_k = 1 } }
                };

                var jsonContent = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                });

                using var request = new HttpRequestMessage(HttpMethod.Post, BaiduSearchApiUrl)
                {
                    Content = new StringContent(jsonContent, Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("X-Appbuilder-Authorization", $"Bearer {_baiduApiKey!}");

                using var response = await _httpClient.SendAsync(request);

                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    int statusCode = (int)response.StatusCode;
                    if (statusCode == 401 || statusCode == 403)
                    {
                        string detail = TryExtractBaiduError(responseJson);
                        return $"百度 API Key 无效或已过期 (HTTP {statusCode})。\n" +
                               $"请通过 工具 → 选项 → DeepSeek Chat → Web Search 重新配置。\n" +
                               $"获取 Key: https://console.bce.baidu.com/ai_apaas/accessKey\n" +
                               (string.IsNullOrEmpty(detail) ? "" : $"详情: {detail}");
                    }
                    if (statusCode == 429)
                        return LocalizationService.Instance["service.webSearch.rateLimit"];
                    return $"百度搜索返回 HTTP {statusCode}，请稍后重试。";
                }

                // 检查业务错误码
                using var doc = JsonDocument.Parse(responseJson);
                if (doc.RootElement.TryGetProperty("code", out var codeElement))
                {
                    int code = codeElement.GetInt32();
                    string msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                    if (IsAuthError(code, msg))
                    {
                        return $"百度 API Key 认证失败 (code={code})。\n" +
                               $"请确认 Key 来自千帆 AppBuilder 控制台。\n" +
                               $"获取 Key: https://console.bce.baidu.com/ai_apaas/accessKey\n" +
                               $"详情: {msg}";
                    }
                }

                return null; // 有效
            }
            catch (ApiKeyInvalidException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"百度 API 连接失败: {ex.Message}";
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            _deepSeekHttpClient?.Dispose();
        }

        #endregion

        #region Private Methods - Baidu Qianfan

        /// <summary>
        /// 通过百度千帆 AI 搜索 API 进行网页搜索。
        /// API 文档: https://cloud.baidu.com/doc/qianfan/s/2mh4su4uy
        /// </summary>
        private async Task<List<WebSearchResult>> SearchBaiduAsync(string query, CancellationToken ct, string? searchRecency = null)
        {
            if (string.IsNullOrWhiteSpace(_baiduApiKey))
            {
                Logger.Info("百度 API Key 未配置");
                return new List<WebSearchResult>();
            }

            var requestBody = new Dictionary<string, object>
            {
                ["messages"] = new[]
                {
                    new { role = "user", content = TruncateQuery(query, 72) }
                },
                ["search_source"] = "baidu_search_v2",
                ["resource_type_filter"] = new[]
                {
                    new { type = "web", top_k = MaxSearchResults }
                }
            };

            if (!string.IsNullOrWhiteSpace(searchRecency))
            {
                requestBody["search_recency_filter"] = searchRecency!;
            }

            var jsonContent = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, BaiduSearchApiUrl)
            {
                Content = new StringContent(jsonContent, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Appbuilder-Authorization", $"Bearer {_baiduApiKey!}");

            using var response = await _httpClient.SendAsync(request, ct);

            var responseJson = await response.Content.ReadAsStringAsync();

            // ── 检查错误响应 ──
            if (!response.IsSuccessStatusCode)
            {
                int statusCode = (int)response.StatusCode;
                Logger.Error($"百度搜索返回 HTTP {statusCode}: {responseJson}");

                if (statusCode == 401 || statusCode == 403)
                {
                    string detail = TryExtractBaiduError(responseJson);
                    throw new ApiKeyInvalidException(
                        $"百度 API Key 无效或已过期 (HTTP {statusCode})。\n" +
                        $"请通过 工具 → 选项 → DeepSeek Chat → Web Search 重新配置。\n" +
                        $"获取 Key: https://console.bce.baidu.com/ai_apaas/accessKey\n" +
                        (string.IsNullOrEmpty(detail) ? "" : $"详情: {detail}"));
                }

                CheckBaiduQuotaError(responseJson);
                return new List<WebSearchResult>();
            }

            // ── 解析成功响应 ──
            try
            {
                using var doc = JsonDocument.Parse(responseJson);

                // 检查业务错误码
                if (doc.RootElement.TryGetProperty("code", out var codeElement))
                {
                    int errorCode = codeElement.GetInt32();
                    string errorMsg = doc.RootElement.TryGetProperty("message", out var msgElement)
                        ? msgElement.GetString() ?? "" : "";

                    Logger.Error($"百度搜索业务错误: code={errorCode}, message={errorMsg}");

                    // 检查认证错误
                    if (IsAuthError(errorCode, errorMsg))
                    {
                        throw new ApiKeyInvalidException(
                            $"百度 API Key 认证失败 (code={errorCode})。\n" +
                            $"请确认 Key 来自千帆 AppBuilder 控制台。\n" +
                            $"获取 Key: https://console.bce.baidu.com/ai_apaas/accessKey\n" +
                            $"详情: {errorMsg}");
                    }

                    if (IsQuotaExhaustedError(errorCode, errorMsg))
                    {
                        throw new BaiduQuotaExhaustedException(
                            $"百度搜索额度已耗尽 (code={errorCode})，已自动切换至 DuckDuckGo。\n" +
                            $"请前往 https://console.bce.baidu.com/ai_apaas/resource 开通后付费或等待次日重置。");
                    }

                    return new List<WebSearchResult>();
                }

                // 解析 references 数组
                var results = new List<WebSearchResult>();
                if (doc.RootElement.TryGetProperty("references", out var references))
                {
                    foreach (var refItem in references.EnumerateArray())
                    {
                        string type = refItem.TryGetProperty("type", out var t) ? t.GetString() ?? "web" : "web";
                        if (type != "web") continue; // 只取网页结果

                        results.Add(new WebSearchResult
                        {
                            Title = refItem.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                            Url = refItem.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "",
                            Snippet = refItem.TryGetProperty("content", out var content)
                                ? NormalizeSnippet(content.GetString() ?? "")
                                : "",
                            Date = refItem.TryGetProperty("date", out var date) ? date.GetString() ?? "" : "",
                        });
                    }
                }

                Logger.Info($"百度搜索完成，获取 {results.Count} 条结果");
                return results;
            }
            catch (JsonException ex)
            {
                Logger.Error($"解析百度搜索响应 JSON 失败: {ex.Message}", ex);
                return new List<WebSearchResult>();
            }
        }

        /// <summary>
        /// 检查百度 API 是否返回额度耗尽错误。
        /// </summary>
        private static void CheckBaiduQuotaError(string responseJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                if (doc.RootElement.TryGetProperty("code", out var codeElement))
                {
                    int code = codeElement.GetInt32();
                    string msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                    if (IsQuotaExhaustedError(code, msg))
                    {
                        throw new BaiduQuotaExhaustedException(
                            $"百度搜索额度已耗尽 (code={code})，已自动切换至 DuckDuckGo。\n" +
                            $"请前往 https://console.bce.baidu.com/ai_apaas/resource 开通后付费或等待每日重置。");
                    }
                }
            }
            catch (BaiduQuotaExhaustedException)
            {
                throw;
            }
            catch
            {
                // 非 JSON 响应或解析失败，忽略
            }
        }

        /// <summary>
        /// 判断是否为额度耗尽错误。
        /// </summary>
        private static bool IsQuotaExhaustedError(int code, string message)
        {
            if (code == BaiduQuotaExhaustedCode) return true;

            var lowerMsg = message.ToLowerInvariant();
            return lowerMsg.Contains("quota") ||
                   lowerMsg.Contains("exceeded") ||
                   lowerMsg.Contains("limit") ||
                   lowerMsg.Contains("insufficient") ||
                   lowerMsg.Contains("额度") ||
                   lowerMsg.Contains("超出") ||
                   lowerMsg.Contains("免费") ||
                   lowerMsg.Contains("余额不足");
        }

        /// <summary>
        /// 判断是否为认证相关错误（API Key 无效等）。
        /// </summary>
        private static bool IsAuthError(int code, string message)
        {
            if (code == 1 || code == 2 || code == 111 || code == 112) return true; // 常见认证错误码

            var lowerMsg = message.ToLowerInvariant();
            return lowerMsg.Contains("auth") ||
                   lowerMsg.Contains("invalid") ||
                   lowerMsg.Contains("apikey") ||
                   lowerMsg.Contains("unauthorized") ||
                   lowerMsg.Contains("认证失败") ||
                   lowerMsg.Contains("ak/sk") ||
                   lowerMsg.Contains("access key") ||
                   lowerMsg.Contains("token") ||
                   lowerMsg.Contains("permission");
        }

        /// <summary>
        /// 尝试从百度错误响应中提取可读的错误消息。
        /// </summary>
        private static string TryExtractBaiduError(string responseJson)
        {
            if (string.IsNullOrWhiteSpace(responseJson)) return string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                if (doc.RootElement.TryGetProperty("message", out var msg))
                    return msg.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("error_msg", out var emsg))
                    return emsg.GetString() ?? "";
            }
            catch { }
            return responseJson.Length > 200 ? responseJson.Substring(0, 200) : responseJson;
        }

        #endregion

        #region Private Methods - Bing

        /// <summary>
        /// 通过 Bing Web Search API v7 进行搜索。
        /// API 文档: https://learn.microsoft.com/en-us/bing/search-apis/bing-web-search/reference/endpoints
        /// </summary>
        private async Task<List<WebSearchResult>> SearchBingAsync(string query, CancellationToken ct, string? searchRecency = null)
        {
            if (string.IsNullOrWhiteSpace(_bingApiKey))
            {
                Logger.Info("Bing API Key 未配置");
                return new List<WebSearchResult>();
            }

            // 构建 URL 参数
            var uriBuilder = new StringBuilder(BingSearchApiUrl);
            uriBuilder.Append("?q=");
            uriBuilder.Append(Uri.EscapeDataString(query));
            uriBuilder.Append("&count=");
            uriBuilder.Append(MaxSearchResults);
            uriBuilder.Append("&mkt=zh-CN");
            uriBuilder.Append("&textFormat=Raw");

            if (!string.IsNullOrWhiteSpace(searchRecency))
            {
                // 将 searchRecency 映射为 Bing freshness 参数
                string? freshness = searchRecency switch
                {
                    "week" => "Week",
                    "month" => "Month",
                    "semiyear" => null, // Bing 不支持半年, 忽略
                    "year" => null,      // Bing 不支持年, 忽略
                    _ => null
                };
                if (freshness != null)
                {
                    uriBuilder.Append("&freshness=");
                    uriBuilder.Append(freshness);
                }
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.ToString());
            request.Headers.Add("Ocp-Apim-Subscription-Key", _bingApiKey!);

            using var response = await _httpClient.SendAsync(request, ct);
            var responseJson = await response.Content.ReadAsStringAsync();

            // ── 检查错误响应 ──
            if (!response.IsSuccessStatusCode)
            {
                int statusCode = (int)response.StatusCode;
                Logger.Error($"Bing 搜索返回 HTTP {statusCode}: {responseJson}");

                if (statusCode == 401 || statusCode == 403)
                {
                    string detail = TryExtractBingError(responseJson);
                    throw new ApiKeyInvalidException(
                        $"Bing API Key 无效或已过期 (HTTP {statusCode})。\n" +
                        $"请在 Azure Portal → Bing Search 资源 → Keys and Endpoint 中获取正确密钥。\n" +
                        (string.IsNullOrEmpty(detail) ? "" : $"详情: {detail}"));
                }

                return new List<WebSearchResult>();
            }

            // ── 解析成功响应 ──
            try
            {
                using var doc = JsonDocument.Parse(responseJson);

                var results = new List<WebSearchResult>();

                // 检查是否有错误（Bing 有时返回 200 但包含错误对象）
                if (doc.RootElement.TryGetProperty("_type", out var typeEl) &&
                    typeEl.GetString() == "ErrorResponse")
                {
                    string errorMsg = TryExtractBingError(responseJson);
                    Logger.Error($"Bing 搜索业务错误: {errorMsg}");
                    return new List<WebSearchResult>();
                }

                // 解析 webPages
                if (doc.RootElement.TryGetProperty("webPages", out var webPages) &&
                    webPages.TryGetProperty("value", out var value))
                {
                    foreach (var page in value.EnumerateArray())
                    {
                        results.Add(new WebSearchResult
                        {
                            Title = page.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                            Url = page.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "",
                            Snippet = page.TryGetProperty("snippet", out var snippet)
                                ? NormalizeSnippet(snippet.GetString() ?? "")
                                : "",
                            Date = page.TryGetProperty("datePublished", out var date) ? date.GetString() ?? "" : "",
                        });
                    }
                }

                Logger.Info($"Bing 搜索完成，获取 {results.Count} 条结果");
                return results;
            }
            catch (JsonException ex)
            {
                Logger.Error($"解析 Bing 搜索响应 JSON 失败: {ex.Message}", ex);
                return new List<WebSearchResult>();
            }
        }

        /// <summary>
        /// 尝试从 Bing 错误响应中提取可读的错误消息。
        /// </summary>
        private static string TryExtractBingError(string responseJson)
        {
            if (string.IsNullOrWhiteSpace(responseJson)) return string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                if (doc.RootElement.TryGetProperty("errors", out var errors) &&
                    errors.GetArrayLength() > 0)
                {
                    var first = errors[0];
                    string msg = first.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                    string code = first.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
                    return string.IsNullOrEmpty(code) ? msg : $"[{code}] {msg}";
                }
            }
            catch { }
            return responseJson.Length > 200 ? responseJson.Substring(0, 200) : responseJson;
        }

        #endregion

        #region Private Methods - DeepSeek 官方 Search / Fetch

        /// <summary>
        /// 通过 DeepSeek 官方 Search 接口搜索网页。
        /// API 文档: https://api-docs.deepseek.com/zh-cn/guides/search
        /// 鉴权与 DeepSeek API 相同（Bearer）。失败时返回空列表，由调用方回退到其它搜索引擎。
        /// </summary>
        private async Task<List<WebSearchResult>> SearchDeepSeekAsync(
            string query, CancellationToken ct, string? searchRecency = null,
            int? count = null, string? originalQuestion = null)
        {
            if (!IsDeepSeekSearchEnabled)
                return new List<WebSearchResult>();

            try
            {
                // 官方 Search 接口没有独立的时效过滤字段，改用 query 语法表达。
                string effectiveQuery = AppendRecencySyntax(query, searchRecency);

                // count：官方允许 1～50，超出范围一律夹到边界，避免 400。
                // net472 没有 Math.Clamp，这里手动夹逼。
                int effectiveCount = count ?? MaxSearchResults;
                if (effectiveCount < 1) effectiveCount = 1;
                else if (effectiveCount > 50) effectiveCount = 50;

                var requestBody = new Dictionary<string, object>
                {
                    ["query"] = TruncateQueryChars(effectiveQuery, 128),
                    ["count"] = effectiveCount,
                    ["need_content"] = false,
                };

                // ori_question：帮助官方引擎理解搜索意图，仅在非空且未超长时携带。
                if (!string.IsNullOrWhiteSpace(originalQuestion))
                    requestBody["ori_question"] = TruncateQueryChars(originalQuestion.Trim(), 128);

                var jsonContent = JsonSerializer.Serialize(requestBody);
                using var request = new HttpRequestMessage(HttpMethod.Post, DeepSeekSearchApiUrl)
                {
                    Content = new StringContent(jsonContent, Encoding.UTF8, "application/json"),
                };
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_officialApiKey}");

                using var response = await _deepSeekHttpClient.SendAsync(request, ct);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Error($"DeepSeek 官方搜索返回 HTTP {(int)response.StatusCode}: {TruncateForLog(responseJson)}");
                    return new List<WebSearchResult>();
                }

                var results = ParseDeepSeekSearchResponse(responseJson);
                // 只有 results 非空才算"成功请求"（官方计费口径）；空结果不计费。
                if (results.Count > 0)
                    RecordOfficialToolSuccess(isSearch: true);
                Logger.Info($"DeepSeek 官方搜索完成，获取 {results.Count} 条结果");
                return results;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error($"DeepSeek 官方搜索失败: {ex.Message}", ex);
                return new List<WebSearchResult>();
            }
        }

        /// <summary>
        /// 把时效过滤转换成官方 Search 支持的 query 语法（after:/before:）。
        /// 无法映射的取值原样返回查询词。
        /// </summary>
        private static string AppendRecencySyntax(string query, string? searchRecency)
        {
            string? syntax = searchRecency?.Trim().ToLowerInvariant() switch
            {
                "week" => "after:" + DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd"),
                "month" => "after:" + DateTime.UtcNow.AddMonths(-1).ToString("yyyy-MM-dd"),
                "semiyear" => "after:" + DateTime.UtcNow.AddMonths(-6).ToString("yyyy-MM-dd"),
                "year" => "after:" + DateTime.UtcNow.AddYears(-1).ToString("yyyy-MM-dd"),
                _ => null,
            };

            return syntax == null ? query : query + " " + syntax;
        }

        /// <summary>
        /// 解析 DeepSeek Search 接口响应。优先使用长摘要 summary，缺失时回退 snippet。
        /// </summary>
        internal static List<WebSearchResult> ParseDeepSeekSearchResponse(string responseJson)
        {
            var results = new List<WebSearchResult>();
            if (string.IsNullOrWhiteSpace(responseJson))
                return results;

            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                if (!doc.RootElement.TryGetProperty("results", out var items) ||
                    items.ValueKind != JsonValueKind.Array)
                {
                    return results;
                }

                foreach (var item in items.EnumerateArray())
                {
                    string summary = GetStringProperty(item, "summary");
                    if (string.IsNullOrWhiteSpace(summary))
                        summary = GetStringProperty(item, "snippet");

                    results.Add(new WebSearchResult
                    {
                        Title = GetStringProperty(item, "title"),
                        Url = GetStringProperty(item, "url"),
                        Snippet = NormalizeSnippet(summary),
                        Date = GetStringProperty(item, "date"),
                        SiteName = GetStringProperty(item, "site_name"),
                        Authority = GetIntProperty(item, "authority", WebSearchResult.UnknownAuthority),
                        RerankScore = GetDoubleProperty(item, "rerank_score", WebSearchResult.UnknownScore),
                    });
                }
            }
            catch (JsonException ex)
            {
                Logger.Error($"解析 DeepSeek 官方搜索响应 JSON 失败: {ex.Message}", ex);
            }

            return results;
        }

        /// <summary>
        /// 通过 DeepSeek 官方 Fetch 接口抓取单个网页，返回 Markdown 正文。
        /// 官方接口只返回 Markdown 正文，图片与子链接从 Markdown 语法中提取，
        /// 以保持与本地 HTML 路径一致：图片供视觉模型直读，子链接供递归抓取。
        /// </summary>
        private async Task<WebPageContent?> FetchWebPageViaDeepSeekAsync(
            string url, CancellationToken ct, int maxContentLength)
        {
            try
            {
                var requestBody = new Dictionary<string, object> { ["url"] = url };
                var jsonContent = JsonSerializer.Serialize(requestBody);

                using var request = new HttpRequestMessage(HttpMethod.Post, DeepSeekFetchApiUrl)
                {
                    Content = new StringContent(jsonContent, Encoding.UTF8, "application/json"),
                };
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_officialApiKey}");

                using var response = await _deepSeekHttpClient.SendAsync(request, ct);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Info($"DeepSeek Fetch 返回 HTTP {(int)response.StatusCode}: {TruncateForLog(responseJson)}");
                    return null;
                }

                using var doc = JsonDocument.Parse(responseJson);
                string title = GetStringProperty(doc.RootElement, "title");
                string content = GetStringProperty(doc.RootElement, "content");
                if (string.IsNullOrWhiteSpace(content))
                    return null;

                // 只有返回正文才算"成功请求"（官方计费口径）；空正文不计费。
                RecordOfficialToolSuccess(isSearch: false);

                string text = string.IsNullOrWhiteSpace(title)
                    ? content.Trim()
                    : title.Trim() + "\n\n" + content.Trim();

                if (maxContentLength > 0 && text.Length > maxContentLength)
                    text = text.Substring(0, maxContentLength);

                return new WebPageContent
                {
                    Text = text,
                    ImageUrls = ExtractMarkdownImageUrls(content, url),
                    LinkUrls = ExtractMarkdownLinkUrls(content, url),
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Info($"DeepSeek Fetch 抓取失败 ({url}): {ex.Message}");
                return null;
            }
        }

        /// <summary>读取 JSON 字符串属性，缺失或类型不符时返回空字符串。</summary>
        private static string GetStringProperty(JsonElement element, string propertyName)
            => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        /// <summary>读取 JSON 整数属性，缺失或类型不符时返回 <paramref name="fallback"/>。</summary>
        private static int GetIntProperty(JsonElement element, string propertyName, int fallback)
            => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out int result)
                ? result
                : fallback;

        /// <summary>读取 JSON 数字属性，缺失或类型不符时返回 <paramref name="fallback"/>。</summary>
        private static double GetDoubleProperty(JsonElement element, string propertyName, double fallback)
            => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out double result)
                ? result
                : fallback;

        /// <summary>日志中截断过长的响应体，避免刷屏。</summary>
        private static string TruncateForLog(string text)
            => string.IsNullOrEmpty(text) ? string.Empty
                : text.Length > 300 ? text.Substring(0, 300) : text;

        /// <summary>按 Unicode 字符数截断查询词（官方 Search 接口限制 1～128 字符）。</summary>
        private static string TruncateQueryChars(string query, int maxChars)
            => string.IsNullOrEmpty(query) || query.Length <= maxChars
                ? query
                : query.Substring(0, maxChars);

        #endregion

        #region Private Methods - DuckDuckGo

        /// <summary>
        /// 通过 DuckDuckGo Lite 搜索（免费备用，无需 API Key）。
        /// </summary>
        private async Task<List<WebSearchResult>> SearchDuckDuckGoAsync(string query, CancellationToken ct)
        {
            var results = new List<WebSearchResult>();

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("q", query),
            });

            using var response = await _httpClient.PostAsync(DuckDuckGoLiteUrl, content, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync();
            results = ParseDuckDuckGoLiteHtml(html);

            Logger.Info($"DuckDuckGo 搜索完成，获取 {results.Count} 条结果");
            return results;
        }

        /// <summary>
        /// 解析 DuckDuckGo Lite HTML，提取搜索结果。
        /// </summary>
        private static List<WebSearchResult> ParseDuckDuckGoLiteHtml(string html)
        {
            var results = new List<WebSearchResult>();

            try
            {
                var linkMatches = Regex.Matches(html,
                    @"<a\s+[^>]*href\s*=\s*""(?<url>[^""]+)""[^>]*>(?<title>.*?)</a>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);

                var snippetMatches = Regex.Matches(html,
                    @"<span\s+class\s*=\s*""[^""]*snippet[^""]*""[^>]*>(?<snippet>.*?)</span>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);

                for (int i = 0; i < linkMatches.Count && results.Count < MaxSearchResults; i++)
                {
                    var linkMatch = linkMatches[i];
                    string rawUrl = linkMatch.Groups["url"].Value;
                    string rawTitle = linkMatch.Groups["title"].Value;

                    if (string.IsNullOrWhiteSpace(rawUrl) ||
                        rawUrl.StartsWith("//duckduckgo.com") ||
                        rawUrl.StartsWith("/") ||
                        rawUrl.Contains("duckduckgo.com"))
                        continue;

                    string title = StripHtmlTags(HttpUtility.HtmlDecode(rawTitle)).Trim();
                    if (string.IsNullOrWhiteSpace(title))
                        continue;

                    string url = rawUrl;
                    if (url.StartsWith("//"))
                        url = "https:" + url;

                    string snippet = string.Empty;
                    if (i < snippetMatches.Count)
                    {
                        snippet = StripHtmlTags(HttpUtility.HtmlDecode(
                            snippetMatches[i].Groups["snippet"].Value)).Trim();
                    }

                    if (string.IsNullOrWhiteSpace(snippet))
                    {
                        int linkEnd = linkMatch.Index + linkMatch.Length;
                        int nextTag = html.IndexOf('<', linkEnd);
                        if (nextTag > linkEnd)
                        {
                            string afterLink = html.Substring(linkEnd, nextTag - linkEnd);
                            snippet = StripHtmlTags(HttpUtility.HtmlDecode(afterLink)).Trim();
                        }
                    }

                    results.Add(new WebSearchResult
                    {
                        Title = title,
                        Url = url,
                        Snippet = string.IsNullOrWhiteSpace(snippet) ? LocalizationService.Instance["service.webSearch.noSummary"] : snippet,
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"解析 DuckDuckGo 搜索结果异常: {ex.Message}", ex);
            }

            return results;
        }

        #endregion

        #region Private Methods - Helpers

        /// <summary>
        /// 截断查询词，百度 API 限制 72 字符（一个汉字 = 2 字符）。
        /// </summary>
        private static string TruncateQuery(string query, int maxBytes)
        {
            if (string.IsNullOrEmpty(query)) return query;
            int byteCount = Encoding.UTF8.GetByteCount(query);
            if (byteCount <= maxBytes) return query;

            // 按字符逐步截断直到字节数 <= maxBytes
            var sb = new StringBuilder();
            foreach (char c in query)
            {
                if (Encoding.UTF8.GetByteCount(sb.ToString() + c) > maxBytes)
                    break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 规范化摘要文本。
        /// </summary>
        private static string NormalizeSnippet(string snippet)
        {
            if (string.IsNullOrEmpty(snippet)) return LocalizationService.Instance["service.webSearch.noSummary"];
            return snippet.Trim();
        }

        /// <summary>
        /// 移除 HTML 标签，保留纯文本。
        /// </summary>
        private static string StripHtmlTags(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            string result = Regex.Replace(input, @"<[^>]+>", " ");
            result = Regex.Replace(result, @"\s+", " ").Trim();
            result = HttpUtility.HtmlDecode(result);
            return result;
        }

        /// <summary>
        /// 从 HTML 中提取纯文本内容（简易实现）。
        /// 移除 script/style 标签后，提取 body 内的可见文本。
        /// </summary>
        private static string ExtractTextFromHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return string.Empty;

            try
            {
                // 移除 script 和 style 内容
                html = Regex.Replace(html, @"<script[^>]*>.*?</script>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                html = Regex.Replace(html, @"<style[^>]*>.*?</style>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                html = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline);
                html = Regex.Replace(html, @"<head[^>]*>.*?</head>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

                // 提取 body 内容
                var bodyMatch = Regex.Match(html, @"<body[^>]*>(.*?)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                string bodyContent = bodyMatch.Success ? bodyMatch.Groups[1].Value : html;

                // 移除所有 HTML 标签
                string text = StripHtmlTags(bodyContent);

                return text.Trim();
            }
            catch
            {
                return StripHtmlTags(html).Trim();
            }
        }

        /// <summary>
        /// 从 HTML 中提取 &lt;img&gt; 标签的图片绝对 URL（供视觉模型直读）。
        /// 支持 src 与懒加载的 data-src；跳过 data: 内联、javascript: 等无效值，
        /// 相对路径基于页面最终 URL 转为绝对 http(s) 链接。
        /// </summary>
        /// <param name="html">原始 HTML</param>
        /// <param name="baseUrl">页面绝对 URL（用于解析相对路径）</param>
        /// <param name="maxImages">最多提取的图片数（默认每页上限）</param>
        public static List<string> ExtractImageUrls(string html, string baseUrl, int maxImages = MaxImagesPerPage)
        {
            var urls = new List<string>();
            if (string.IsNullOrWhiteSpace(html) || maxImages <= 0) return urls;

            Uri? baseUri = null;
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBase) &&
                (parsedBase.Scheme == Uri.UriSchemeHttp || parsedBase.Scheme == Uri.UriSchemeHttps))
            {
                baseUri = parsedBase;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 同时匹配 src 与 data-src（懒加载图片）
            foreach (Match m in Regex.Matches(
                html,
                @"<img\b[^>]*?\b(?:src|data-src)\s*=\s*(['""])(.*?)\1",
                RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                string? absolute = NormalizeWebUrl(m.Groups[2].Value, baseUri);
                if (absolute == null) continue;
                if (seen.Add(absolute))
                {
                    urls.Add(absolute);
                    if (urls.Count >= maxImages) break;
                }
            }

            return urls;
        }

        /// <summary>
        /// 从 HTML 中提取 &lt;a&gt; 标签的链接绝对 URL（供递归抓取子页面）。
        /// 相对路径基于页面最终 URL 转为绝对 http(s) 链接，
        /// 过滤 mailto:/tel:/javascript: 等非网页链接。
        /// </summary>
        /// <param name="html">原始 HTML</param>
        /// <param name="baseUrl">页面绝对 URL（用于解析相对路径）</param>
        /// <param name="maxLinks">最多提取的链接数（默认每页上限）</param>
        public static List<string> ExtractLinkUrls(string html, string baseUrl, int maxLinks = MaxLinksPerPage)
        {
            var urls = new List<string>();
            if (string.IsNullOrWhiteSpace(html) || maxLinks <= 0) return urls;

            Uri? baseUri = null;
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBase) &&
                (parsedBase.Scheme == Uri.UriSchemeHttp || parsedBase.Scheme == Uri.UriSchemeHttps))
            {
                baseUri = parsedBase;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in Regex.Matches(
                html,
                @"<a\b[^>]*?\bhref\s*=\s*(['""])(.*?)\1",
                RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                string? absolute = NormalizeWebUrl(m.Groups[2].Value, baseUri);
                if (absolute == null) continue;
                if (seen.Add(absolute))
                {
                    urls.Add(absolute);
                    if (urls.Count >= maxLinks) break;
                }
            }

            return urls;
        }

        /// <summary>
        /// 将 src/href 值规范化为绝对 http(s) URL（图片与链接共用）。
        /// 过滤 data: / javascript: / about: / mailto: / tel: / 空锚点等无法或不应直传的值。
        /// </summary>
        private static string? NormalizeWebUrl(string? raw, Uri? baseUri)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            raw = raw.Trim();

            if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)
                || raw == "#")
            {
                return null;
            }

            try
            {
                raw = HttpUtility.HtmlDecode(raw);

                if (Uri.TryCreate(raw, UriKind.Absolute, out var abs))
                {
                    return (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps)
                        ? abs.AbsoluteUri : null;
                }

                if (baseUri != null && Uri.TryCreate(baseUri, raw, out var rel))
                {
                    return (rel.Scheme == Uri.UriSchemeHttp || rel.Scheme == Uri.UriSchemeHttps)
                        ? rel.AbsoluteUri : null;
                }
            }
            catch
            {
                // 解析失败视为无效 URL
            }

            return null;
        }

        /// <summary>
        /// 在工具结果文本末尾追加图片 URL 块（供视觉模型消费）。
        /// 没有有效图片时原样返回，不追加任何内容。
        /// </summary>
        public static string AppendWebImagesBlock(string content, IEnumerable<string>? imageUrls)
        {
            var urls = imageUrls?
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxTotalImages)
                .ToList();

            if (urls == null || urls.Count == 0)
                return content;

            var sb = new StringBuilder(content);
            sb.AppendLine();
            sb.AppendLine(WebImagesBlockStart);
            foreach (string u in urls)
                sb.AppendLine(u);
            sb.AppendLine(WebImagesBlockEnd);
            return sb.ToString();
        }

        /// <summary>
        /// 解析工具结果文本中的图片 URL 块，返回移除图片块后的干净文本与图片 URL 列表。
        /// 没有图片块时返回原文与空列表。
        /// </summary>
        public static (string CleanText, List<string> ImageUrls) ParseWebImagesBlock(string raw)
        {
            var urls = new List<string>();
            if (string.IsNullOrEmpty(raw))
                return (raw ?? string.Empty, urls);

            int start = raw.IndexOf(WebImagesBlockStart, StringComparison.Ordinal);
            if (start < 0)
                return (raw, urls);

            int contentStart = start + WebImagesBlockStart.Length;
            int end = raw.IndexOf(WebImagesBlockEnd, contentStart, StringComparison.Ordinal);
            if (end < 0)
                return (raw, urls);

            string cleanText = raw.Remove(start, (end + WebImagesBlockEnd.Length) - start).TrimEnd();

            string block = raw.Substring(contentStart, end - contentStart);
            foreach (string line in block.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string u = line.Trim();
                if (Uri.TryCreate(u, UriKind.Absolute, out var parsed)
                    && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
                {
                    urls.Add(u);
                }
            }

            return (cleanText, urls);
        }

        /// <summary>
        /// 过滤出视觉模型支持的图片 URL（按路径扩展名判断，忽略 query/fragment）。
        /// 非支持格式（如 .svg/.bmp/.tiff/.ico）或无扩展名的 URL 会被丢弃，
        /// 避免作为 image_url 直传视觉模型导致 HTTP 400。
        /// 所有 URL 都被过滤掉时返回 null（而非空列表），供调用方直接判断。
        /// </summary>
        public static List<string>? FilterVisionImageUrls(IEnumerable<string>? urls)
        {
            if (urls == null)
                return null;

            var result = new List<string>();
            foreach (string u in urls)
            {
                if (string.IsNullOrWhiteSpace(u))
                    continue;

                string? ext = GetUrlPathExtension(u);
                if (ext != null && VisionSupportedImageExtensions.Contains(ext))
                    result.Add(u);
            }

            return result.Count > 0 ? result : null;
        }

        /// <summary>
        /// 提取 URL 路径部分的扩展名（含点，如 ".svg" / ".png"），忽略 query 与 fragment。
        /// 无法解析为绝对 http(s) URL 或路径无扩展名时返回 null。
        /// </summary>
        private static string? GetUrlPathExtension(string url)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    return null;
                if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                    return null;

                string ext = Path.GetExtension(uri.AbsolutePath);
                return string.IsNullOrEmpty(ext) ? null : ext;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 从 Markdown 文本中提取图片绝对 URL（官方 Fetch 接口返回 Markdown 时使用）。
        /// 支持 ![alt](url) 语法；相对路径基于页面 URL 解析，跳过 data: 等无效值。
        /// </summary>
        public static List<string> ExtractMarkdownImageUrls(string markdown, string baseUrl, int maxImages = MaxImagesPerPage)
        {
            var urls = new List<string>();
            if (string.IsNullOrWhiteSpace(markdown) || maxImages <= 0) return urls;

            Uri? baseUri = TryCreateHttpBaseUri(baseUrl);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in Regex.Matches(
                markdown,
                @"!\[[^\]]*\]\(\s*(?<url><[^>]*>|[^)\s]+)",
                RegexOptions.Singleline))
            {
                string? absolute = NormalizeWebUrl(UnwrapAngleBrackets(m.Groups["url"].Value), baseUri);
                if (absolute == null) continue;
                if (seen.Add(absolute))
                {
                    urls.Add(absolute);
                    if (urls.Count >= maxImages) break;
                }
            }

            return urls;
        }

        /// <summary>
        /// 从 Markdown 文本中提取普通链接 [text](url)（官方 Fetch 接口返回 Markdown 时使用）。
        /// 图片链接被排除，避免同一地址同时进入图片与子链接列表。
        /// </summary>
        public static List<string> ExtractMarkdownLinkUrls(string markdown, string baseUrl, int maxLinks = MaxLinksPerPage)
        {
            var urls = new List<string>();
            if (string.IsNullOrWhiteSpace(markdown) || maxLinks <= 0) return urls;

            Uri? baseUri = TryCreateHttpBaseUri(baseUrl);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 前置负向后行断言排除图片语法 "![alt](...)"，只取普通链接。
            foreach (Match m in Regex.Matches(
                markdown,
                @"(?<!!)\[[^\]]*\]\(\s*(?<url><[^>]*>|[^)\s]+)",
                RegexOptions.Singleline))
            {
                string? absolute = NormalizeWebUrl(UnwrapAngleBrackets(m.Groups["url"].Value), baseUri);
                if (absolute == null) continue;
                if (seen.Add(absolute))
                {
                    urls.Add(absolute);
                    if (urls.Count >= maxLinks) break;
                }
            }

            return urls;
        }

        /// <summary>去掉 Markdown 链接目标两侧的可选尖括号（&lt;url&gt; 写法）。</summary>
        private static string UnwrapAngleBrackets(string raw)
        {
            raw = raw.Trim();
            return raw.Length > 1 && raw[0] == '<' && raw[raw.Length - 1] == '>'
                ? raw.Substring(1, raw.Length - 2).Trim()
                : raw;
        }

        /// <summary>把基准 URL 解析为 http(s) 的 Uri；无法解析时返回 null。</summary>
        private static Uri? TryCreateHttpBaseUri(string baseUrl)
            => Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed) &&
               (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
                ? parsed
                : null;

        #endregion
    }

    #region Supporting Types

    /// <summary>抓取到的网页内容：纯文本 + 该页图片/子链接的绝对 URL。</summary>
    public sealed class WebPageContent
    {
        /// <summary>提取后的网页纯文本。</summary>
        public string? Text { get; set; }

        /// <summary>网页内图片的绝对 http(s) URL 列表（供视觉模型直读）。</summary>
        public List<string> ImageUrls { get; set; } = new();

        /// <summary>网页内 &lt;a&gt; 链接的绝对 http(s) URL 列表（供递归抓取子页面）。</summary>
        public List<string> LinkUrls { get; set; } = new();
    }

    /// <summary>
    /// 搜索提供商枚举。
    /// </summary>
    public enum SearchProvider
    {
        /// <summary>DeepSeek 官方 Search / Fetch 接口（仅官方 API 来源启用，使用自研搜索引擎）</summary>
        DeepSeek,

        /// <summary>DuckDuckGo（完全免费，无需 API Key，备用方案）</summary>
        DuckDuckGo,

        /// <summary>百度千帆 AI 搜索（每月免费 1500 次，需 API Key）</summary>
        Baidu,

        /// <summary>Bing Web Search API v7（需 Azure 订阅 Key，免费层 1000 次/月）</summary>
        Bing,
    }

    /// <summary>
    /// 单条网络搜索结果。
    /// </summary>
    public class WebSearchResult
    {
        /// <summary>结果标题</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>结果 URL</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>结果摘要</summary>
        public string Snippet { get; set; } = string.Empty;

        /// <summary>发布日期（百度 API 返回，DuckDuckGo 可能为空）</summary>
        public string Date { get; set; } = string.Empty;

        /// <summary>
        /// 站点名称（DeepSeek 官方 Search 返回；其它引擎为空）。
        /// </summary>
        public string SiteName { get; set; } = string.Empty;

        /// <summary>
        /// 网页权威性 0～4（DeepSeek 官方 Search），-1 表示未知；其它引擎为 <see cref="UnknownAuthority"/>。
        /// </summary>
        public int Authority { get; set; } = UnknownAuthority;

        /// <summary>
        /// 排序分数 0～3（DeepSeek 官方 Search），综合考虑相关性/权威性/时效性；其它引擎为 <see cref="UnknownScore"/>。
        /// </summary>
        public double RerankScore { get; set; } = UnknownScore;

        /// <summary>权威性未知（非官方引擎或接口未返回）。</summary>
        public const int UnknownAuthority = -1;

        /// <summary>排序分数未知（非官方引擎或接口未返回）。</summary>
        public const double UnknownScore = -1d;
    }

    /// <summary>
    /// 百度搜索额度耗尽时抛出的异常。
    /// </summary>
    internal class BaiduQuotaExhaustedException : Exception
    {
        public BaiduQuotaExhaustedException(string message) : base(message) { }
    }

    /// <summary>
    /// API Key 无效或认证失败时抛出的异常。
    /// 用于 DeepSeek API 和百度 API 的认证错误统一处理。
    /// </summary>
    public class ApiKeyInvalidException : Exception
    {
        public ApiKeyInvalidException(string message) : base(message) { }
    }

    #endregion
}
