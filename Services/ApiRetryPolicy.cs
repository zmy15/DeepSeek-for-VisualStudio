using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using DeepSeek_v4_for_VisualStudio.Settings;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// Provider 对一次 HTTP 失败给出的重试结论。
    /// </summary>
    internal readonly struct ApiRetryDecision
    {
        public ApiRetryDecision(bool isRetryable, string? errorCode)
        {
            IsRetryable = isRetryable;
            ErrorCode = errorCode;
        }

        public bool IsRetryable { get; }

        public string? ErrorCode { get; }
    }

    /// <summary>
    /// 判断 API 错误是否值得重试。部分 OpenAI 兼容网关会把请求配置错误
    /// （例如模型不存在）包装成 HTTP 503，因此不能只依赖状态码。
    /// </summary>
    internal static class ApiRetryPolicy
    {
        private const int MaxBackoffSeconds = 30;

        internal static int? RetryCountOverrideForTests { get; set; }

        private static readonly HashSet<string> NonRetryableErrorCodes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "account_deactivated",
                "authentication_error",
                "billing_hard_limit_reached",
                "content_filter",
                "content_filtered",
                "content_policy_violation",
                "context_length_exceeded",
                "image_not_supported",
                "incorrect_api_key",
                "insufficient_balance",
                "insufficient_permissions",
                "insufficient_quota",
                "invalid_api_key",
                "invalid_authentication",
                "invalid_image",
                "invalid_model",
                "invalid_parameter",
                "invalid_request",
                "invalid_request_error",
                "maximum_context_length_exceeded",
                "model_not_found",
                "model_not_supported",
                "not_found_error",
                "organization_deactivated",
                "permission_denied",
                "permission_error",
                "quota_exceeded",
                "token_limit_exceeded",
                "unsupported_capability",
                "unsupported_model",
                "unsupported_parameter",
            };

        private static readonly string[] NonRetryableMessageFragments =
        {
            "authentication failed",
            "billing hard limit",
            "content policy",
            "content_filter",
            "context length",
            "incorrect api key",
            "insufficient balance",
            "insufficient quota",
            "invalid api key",
            "maximum context",
            "model does not exist",
            "model is not supported",
            "model not found",
            "no available channel for model",
            "permission denied",
            "too many tokens",
            "unsupported model",
        };

        public static ApiRetryDecision Evaluate(int statusCode, string? responseBody)
        {
            ApiErrorInfo error = ParseError(responseBody);
            if (IsNonRetryableCode(error.Code) || IsNonRetryableCode(error.Type))
            {
                return new ApiRetryDecision(
                    isRetryable: false,
                    errorCode: error.Code ?? error.Type);
            }

            if (ContainsNonRetryableMessage(error.Message))
            {
                return new ApiRetryDecision(
                    isRetryable: false,
                    errorCode: error.Code ?? error.Type);
            }

            return new ApiRetryDecision(
                isRetryable: IsRetryableStatusCode(statusCode),
                errorCode: error.Code ?? error.Type);
        }

        public static bool IsRetryableStatusCode(int statusCode)
        {
            if (statusCode == (int)HttpStatusCode.RequestTimeout ||
                statusCode == 425 || // Too Early
                statusCode == 429) // Too Many Requests (enum unavailable on net472)
            {
                return true;
            }

            return statusCode >= 500 &&
                   statusCode <= 599 &&
                   statusCode != (int)HttpStatusCode.NotImplemented &&
                   statusCode != (int)HttpStatusCode.HttpVersionNotSupported;
        }

        /// <summary>
        /// 当前配置允许的额外重试次数（不含首次请求）。
        /// </summary>
        public static int GetConfiguredRetryCount()
        {
            int configured = RetryCountOverrideForTests
                             ?? DeepSeekOptionsPage.Instance?.ApiMaxRetryCount
                             ?? DeepSeekOptionsPage.DefaultApiMaxRetryCount;
            return NormalizeRetryCount(configured);
        }

        public static int GetMaxAttempts()
            => GetConfiguredRetryCount() + 1;

        public static int NormalizeRetryCount(int retryCount)
        {
            if (retryCount < DeepSeekOptionsPage.MinApiMaxRetryCount)
                return DeepSeekOptionsPage.MinApiMaxRetryCount;
            if (retryCount > DeepSeekOptionsPage.MaxApiMaxRetryCount)
                return DeepSeekOptionsPage.MaxApiMaxRetryCount;
            return retryCount;
        }

        /// <summary>
        /// 第 N 次重试前的指数退避：1s、2s、4s、8s…，上限 30s。
        /// </summary>
        public static TimeSpan GetBackoffDelay(int retryNumber)
        {
            if (retryNumber <= 0)
                return TimeSpan.Zero;

            int exponent = Math.Min(retryNumber - 1, 5);
            int seconds = Math.Min(1 << exponent, MaxBackoffSeconds);
            return TimeSpan.FromSeconds(seconds);
        }

        private static bool IsNonRetryableCode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            string code = value!.Trim();
            if (NonRetryableErrorCodes.Contains(code)) return true;

            return code.EndsWith("_not_found", StringComparison.OrdinalIgnoreCase) ||
                   code.EndsWith("_not_supported", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsNonRetryableMessage(string? message)
        {
            if (string.IsNullOrWhiteSpace(message)) return false;

            foreach (string fragment in NonRetryableMessageFragments)
            {
                if (message!.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static ApiErrorInfo ParseError(string? responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
                return default;

            try
            {
                using var document = JsonDocument.Parse(responseBody!);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return new ApiErrorInfo(null, null, responseBody);

                string? code = null;
                string? type = null;
                string? message = null;

                if (TryGetProperty(root, "error", out JsonElement error))
                {
                    if (error.ValueKind == JsonValueKind.String)
                    {
                        message = error.GetString();
                    }
                    else if (error.ValueKind == JsonValueKind.Object)
                    {
                        code = GetStringProperty(error, "code");
                        type = GetStringProperty(error, "type");
                        message = GetStringProperty(error, "message") ??
                                  GetStringProperty(error, "detail") ??
                                  GetStringProperty(error, "msg");
                    }
                }

                code ??= GetStringProperty(root, "code");
                type ??= GetStringProperty(root, "type");
                message ??= GetStringProperty(root, "message") ??
                           GetStringProperty(root, "detail");

                return new ApiErrorInfo(code, type, message ?? responseBody);
            }
            catch (JsonException)
            {
                return new ApiErrorInfo(null, null, responseBody);
            }
        }

        private static string? GetStringProperty(JsonElement element, string propertyName)
        {
            return TryGetProperty(element, propertyName, out JsonElement value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static bool TryGetProperty(
            JsonElement element,
            string propertyName,
            out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        private readonly struct ApiErrorInfo
        {
            public ApiErrorInfo(string? code, string? type, string? message)
            {
                Code = code;
                Type = type;
                Message = message;
            }

            public string? Code { get; }

            public string? Type { get; }

            public string? Message { get; }
        }
    }

    /// <summary>
    /// 附加在 <see cref="HttpRequestException.Data"/> 上的 Provider 重试结论。
    /// </summary>
    internal readonly struct ApiRequestFailureInfo
    {
        public ApiRequestFailureInfo(
            int statusCode,
            string? errorCode,
            bool isRetryable,
            string? errorDetails)
        {
            StatusCode = statusCode;
            ErrorCode = errorCode;
            IsRetryable = isRetryable;
            ErrorDetails = errorDetails;
        }

        public int StatusCode { get; }

        public string? ErrorCode { get; }

        public bool IsRetryable { get; }

        public string? ErrorDetails { get; }
    }

    /// <summary>
    /// 在保持 <see cref="HttpRequestException"/> 公开契约不变的前提下携带重试元数据。
    /// </summary>
    internal static class ApiRequestFailure
    {
        private const string StatusCodeDataKey = "DeepSeek.ApiFailure.StatusCode";
        private const string ErrorCodeDataKey = "DeepSeek.ApiFailure.ErrorCode";
        private const string IsRetryableDataKey = "DeepSeek.ApiFailure.IsRetryable";
        private const string ErrorDetailsDataKey = "DeepSeek.ApiFailure.ErrorDetails";

        public static HttpRequestException Create(
            string message,
            int statusCode,
            string? errorCode,
            bool isRetryable,
            string? errorDetails,
            Exception? innerException = null)
        {
            var exception = new HttpRequestException(message, innerException);
            exception.Data[StatusCodeDataKey] = statusCode;
            exception.Data[IsRetryableDataKey] = isRetryable;
            if (!string.IsNullOrWhiteSpace(errorCode))
                exception.Data[ErrorCodeDataKey] = errorCode;
            if (!string.IsNullOrWhiteSpace(errorDetails))
                exception.Data[ErrorDetailsDataKey] = errorDetails;
            return exception;
        }

        public static bool IsProviderHandled(HttpRequestException exception)
        {
            return exception.Data.Contains(IsRetryableDataKey);
        }

        public static bool TryGet(
            HttpRequestException exception,
            out ApiRequestFailureInfo info)
        {
            if (!IsProviderHandled(exception))
            {
                info = default;
                return false;
            }

            int statusCode = exception.Data[StatusCodeDataKey] is int status ? status : 0;
            bool isRetryable = exception.Data[IsRetryableDataKey] is true;
            info = new ApiRequestFailureInfo(
                statusCode,
                exception.Data[ErrorCodeDataKey] as string,
                isRetryable,
                exception.Data[ErrorDetailsDataKey] as string);
            return true;
        }
    }
}
