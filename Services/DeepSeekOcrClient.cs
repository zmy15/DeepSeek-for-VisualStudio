using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    public sealed class DeepSeekOcrResult
    {
        public string Text { get; init; } = string.Empty;
        public int PageCount { get; init; }
    }

    /// <summary>
    /// DeepSeek 官方 OCR API 客户端。
    /// 参考: https://api-docs.deepseek.com/zh-cn/api/ocr
    /// </summary>
    public class DeepSeekOcrClient : IDisposable
    {
        internal const string OcrEndpoint = "https://api.deepseek.com/v1/tools/ocr";
        public const int MaxPagesPerRequest = 1000;
        public const string TextRecognitionPurpose = "text_recognition";
        public const string RichLayoutRecognitionPurpose = "rich_layout_recognition";
        private static readonly TimeSpan OcrTimeout = TimeSpan.FromSeconds(190);

        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly object _configLock = new();
        private string? _apiKey;
        private bool _officialEndpointEnabled;
        private string _recognitionPurpose = TextRecognitionPurpose;

        public DeepSeekOcrClient()
        {
            _httpClient = new HttpClient
            {
                Timeout = OcrTimeout,
            };
            _ownsHttpClient = true;
        }

        internal DeepSeekOcrClient(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _ownsHttpClient = false;
        }

        public bool IsAvailable
        {
            get
            {
                lock (_configLock)
                    return _officialEndpointEnabled && !string.IsNullOrWhiteSpace(_apiKey);
            }
        }

        public void Configure(
            string? apiKey,
            bool isOfficialEndpoint,
            string? recognitionPurpose = null)
        {
            lock (_configLock)
            {
                _apiKey = apiKey;
                _officialEndpointEnabled = isOfficialEndpoint;
                _recognitionPurpose = NormalizePurpose(recognitionPurpose);
            }
        }

        public string GetStatus()
        {
            string key = IsAvailable ? "status.ocrOfficialReady" : "status.ocrOfficialUnavailable";
            return LocalizationService.Instance[key];
        }

        public async Task<string?> ExtractTextAsync(
            string filePath,
            CancellationToken cancellationToken = default)
            => (await ExtractResultAsync(filePath, cancellationToken))?.Text;

        public async Task<DeepSeekOcrResult?> ExtractResultAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath))
            {
                Logger.Info($"[OCR-DeepSeek] skipped: file not found - {filePath}");
                return null;
            }

            string? apiKey;
            string recognitionPurpose;
            lock (_configLock)
            {
                if (!_officialEndpointEnabled || string.IsNullOrWhiteSpace(_apiKey))
                {
                    Logger.Info("[OCR-DeepSeek] unavailable: current endpoint is not the official DeepSeek API");
                    return null;
                }
                apiKey = _apiKey;
                recognitionPurpose = _recognitionPurpose;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(OcrTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, OcrEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var form = new MultipartFormDataContent();
            Stream fileStream;
            try
            {
                fileStream = File.OpenRead(filePath);
            }
            catch (FileNotFoundException)
            {
                Logger.Info($"[OCR-DeepSeek] skipped: file disappeared before open - {filePath}");
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                Logger.Info($"[OCR-DeepSeek] skipped: directory disappeared before open - {filePath}");
                return null;
            }

            using var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(filePath));
            form.Add(fileContent, "file", Path.GetFileName(filePath));
            form.Add(new StringContent(recognitionPurpose, Encoding.UTF8), "purpose");
            request.Content = form;

            Logger.Info($"[OCR-DeepSeek] start: {Path.GetFileName(filePath)}, purpose={recognitionPurpose}");
            try
            {
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cts.Token);
                Task<string> responseReadTask = response.Content.ReadAsStringAsync();
                Task readDeadlineTask = Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
                Task completedTask = await Task.WhenAny(responseReadTask, readDeadlineTask);
                if (completedTask != responseReadTask)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    throw new TimeoutException("OCR response body read timed out");
                }
                string responseJson = await responseReadTask;

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warn($"[OCR-DeepSeek] HTTP {(int)response.StatusCode}: {TruncateForLog(responseJson)}");
                    return null;
                }

                bool preserveLayoutMarkers = string.Equals(
                    recognitionPurpose,
                    RichLayoutRecognitionPurpose,
                    StringComparison.Ordinal);
                DeepSeekOcrResult? result = ParseResponseResult(
                    responseJson,
                    preserveLayoutMarkers);
                if (result == null)
                {
                    Logger.Warn($"[OCR-DeepSeek] invalid response: {TruncateForLog(responseJson)}");
                    return null;
                }

                Logger.Info($"[OCR-DeepSeek] success: {result.PageCount} page(s), {result.Text.Length} chars");
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Logger.Info("[OCR-DeepSeek] cancelled");
                return null;
            }
            catch (OperationCanceledException)
            {
                Logger.Warn("[OCR-DeepSeek] timed out");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[OCR-DeepSeek] failed: {ex.Message}");
                return null;
            }
        }

        internal static string? ParseResponseText(string? responseJson)
            => ParseResponseResult(responseJson)?.Text;

        internal static DeepSeekOcrResult? ParseResponseResult(
            string? responseJson,
            bool preserveLayoutMarkers = false)
        {
            if (string.IsNullOrWhiteSpace(responseJson)) return null;

            try
            {
                using var document = JsonDocument.Parse(responseJson);
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("error", out var error))
                {
                    string message = error.TryGetProperty("message", out var messageElement)
                        ? messageElement.GetString() ?? "unknown error"
                        : "unknown error";
                    Logger.Warn($"[OCR-DeepSeek] API error: {message}");
                    return null;
                }

                if (!root.TryGetProperty("pages", out var pages) ||
                    pages.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                if (!root.TryGetProperty("page_count", out var pageCountElement) ||
                    !pageCountElement.TryGetInt32(out int pageCount))
                {
                    Logger.Warn("[OCR-DeepSeek] response missing a valid page_count");
                    return null;
                }

                int arrayPageCount = pages.GetArrayLength();
                if (pageCount != arrayPageCount)
                {
                    Logger.Warn(
                        $"[OCR-DeepSeek] page_count mismatch: page_count={pageCount}, pages={arrayPageCount}");
                    return null;
                }
                var builder = new StringBuilder();
                bool hasRecognizedText = false;
                int pageIndex = 0;
                foreach (var page in pages.EnumerateArray())
                {
                    int index = page.TryGetProperty("index", out var indexElement) &&
                                indexElement.TryGetInt32(out int parsedIndex)
                        ? parsedIndex
                        : pageIndex;
                    string rawText = page.TryGetProperty("text", out var textElement)
                        ? textElement.GetString() ?? string.Empty
                        : string.Empty;
                    string text = preserveLayoutMarkers
                        ? rawText
                        : NormalizePageText(rawText);
                    string finishReason = page.TryGetProperty("finish_reason", out var finishElement)
                        ? finishElement.GetString() ?? string.Empty
                        : string.Empty;

                    if (pageCount > 1)
                        builder.AppendLine($"--- Page {index + 1}/{pageCount} ---");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        builder.AppendLine(text);
                        hasRecognizedText = true;
                    }
                    builder.AppendLine();

                    if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Warn($"[OCR-DeepSeek] page {index + 1} hit output limit; text may be incomplete");
                    }
                    pageIndex++;
                }

                return new DeepSeekOcrResult
                {
                    Text = hasRecognizedText ? builder.ToString().Trim() : string.Empty,
                    PageCount = pageCount > 0 ? pageCount : 0,
                };
            }
            catch (JsonException ex)
            {
                Logger.Warn($"[OCR-DeepSeek] response JSON parse failed: {ex.Message}");
                return null;
            }
        }

        internal static string NormalizePageText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            string normalized = Regex.Replace(
                text,
                @"<[｜|]box[｜|]>\s*\[\[[^\]]+\]\]\s*<[｜|]/box[｜|]>",
                string.Empty,
                RegexOptions.IgnoreCase);
            normalized = Regex.Replace(
                normalized,
                @"</?[｜|](?:ref|box)[｜|]>",
                string.Empty,
                RegexOptions.IgnoreCase);
            return normalized.Trim();
        }

        internal static string NormalizePurpose(string? purpose)
            => string.Equals(
                purpose,
                RichLayoutRecognitionPurpose,
                StringComparison.Ordinal)
                    ? RichLayoutRecognitionPurpose
                    : TextRecognitionPurpose;

        private static string GetMimeType(string filePath)
        {
            return Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".tif" or ".tiff" => "image/tiff",
                ".webp" => "image/webp",
                ".png" => "image/png",
                _ => "application/octet-stream",
            };
        }

        private static string TruncateForLog(string? text, int maxLength = 240)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text ?? string.Empty;
            return text.Substring(0, maxLength) + "...";
        }

        public void Dispose()
        {
            if (_ownsHttpClient)
                _httpClient.Dispose();
        }
    }
}
