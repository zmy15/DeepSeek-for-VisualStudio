using System.Net;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class ApiRetryPolicyTests
{
    [Fact]
    public void Evaluate_503ModelNotFound_IsNotRetryable()
    {
        const string body =
            """{"error":{"code":"model_not_found","message":"No available channel for model deepseek-v4-flash","type":"new_api_error"}}""";

        ApiRetryDecision decision =
            ApiRetryPolicy.Evaluate((int)HttpStatusCode.ServiceUnavailable, body);

        decision.IsRetryable.Should().BeFalse();
        decision.ErrorCode.Should().Be("model_not_found");
    }

    [Fact]
    public void Evaluate_503WithKnownModelMessage_IsNotRetryable()
    {
        const string body =
            """{"error":{"message":"No available channel for model deepseek-v4-flash under group default"}}""";

        ApiRetryDecision decision =
            ApiRetryPolicy.Evaluate((int)HttpStatusCode.ServiceUnavailable, body);

        decision.IsRetryable.Should().BeFalse();
    }

    [Theory]
    [InlineData(503, """{"error":{"code":"server_error","message":"upstream failed"}}""")]
    [InlineData(500, """{"error":{"code":"internal_error","message":"temporary failure"}}""")]
    [InlineData(502, "")]
    [InlineData(429, """{"error":{"code":"rate_limit_exceeded","message":"slow down"}}""")]
    [InlineData(408, "")]
    public void Evaluate_TransientFailures_AreRetryable(int statusCode, string body)
    {
        ApiRetryPolicy.Evaluate(statusCode, body).IsRetryable.Should().BeTrue();
    }

    [Theory]
    [InlineData(400, """{"error":{"code":"invalid_request_error","message":"bad request"}}""")]
    [InlineData(401, """{"error":{"code":"invalid_api_key","message":"bad key"}}""")]
    [InlineData(503, """{"error":{"code":"insufficient_quota","message":"quota exhausted"}}""")]
    [InlineData(503, """{"error":{"code":"context_length_exceeded","message":"too long"}}""")]
    [InlineData(501, "")]
    [InlineData(505, "")]
    public void Evaluate_PermanentFailures_AreNotRetryable(int statusCode, string body)
    {
        ApiRetryPolicy.Evaluate(statusCode, body).IsRetryable.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_CaseInsensitiveCodeAndPropertyNames_AreRecognized()
    {
        const string body =
            """{"Error":{"Code":"MODEL_NOT_FOUND","Message":"target does not exist"}}""";

        ApiRetryDecision decision =
            ApiRetryPolicy.Evaluate((int)HttpStatusCode.ServiceUnavailable, body);

        decision.IsRetryable.Should().BeFalse();
        decision.ErrorCode.Should().Be("MODEL_NOT_FOUND");
    }

    [Fact]
    public void Evaluate_NonRetryableTypeWithUnknownCode_IsNotRetryable()
    {
        const string body =
            """{"error":{"code":"gateway_error","type":"invalid_request_error","message":"bad request"}}""";

        ApiRetryDecision decision =
            ApiRetryPolicy.Evaluate((int)HttpStatusCode.ServiceUnavailable, body);

        decision.IsRetryable.Should().BeFalse();
        decision.ErrorCode.Should().Be("gateway_error");
    }

    [Fact]
    public void NormalizeRetryCount_ClampsToSupportedRange()
    {
        ApiRetryPolicy.NormalizeRetryCount(-1).Should().Be(0);
        ApiRetryPolicy.NormalizeRetryCount(3).Should().Be(3);
        ApiRetryPolicy.NormalizeRetryCount(99).Should().Be(10);
    }

    [Fact]
    public void GetBackoffDelay_UsesExponentialBackoffAndCapsAtThirtySeconds()
    {
        ApiRetryPolicy.GetBackoffDelay(0).Should().Be(TimeSpan.Zero);
        ApiRetryPolicy.GetBackoffDelay(1).Should().Be(TimeSpan.FromSeconds(1));
        ApiRetryPolicy.GetBackoffDelay(2).Should().Be(TimeSpan.FromSeconds(2));
        ApiRetryPolicy.GetBackoffDelay(3).Should().Be(TimeSpan.FromSeconds(4));
        ApiRetryPolicy.GetBackoffDelay(4).Should().Be(TimeSpan.FromSeconds(8));
        ApiRetryPolicy.GetBackoffDelay(5).Should().Be(TimeSpan.FromSeconds(16));
        ApiRetryPolicy.GetBackoffDelay(6).Should().Be(TimeSpan.FromSeconds(30));
        ApiRetryPolicy.GetBackoffDelay(10).Should().Be(TimeSpan.FromSeconds(30));
    }
}
