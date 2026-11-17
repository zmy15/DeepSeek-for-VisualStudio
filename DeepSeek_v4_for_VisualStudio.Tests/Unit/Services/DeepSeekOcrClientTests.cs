using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Settings;
using System.Net;
using System.Net.Http;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class DeepSeekOcrClientTests
{
    [Fact]
    public async Task ExtractTextAsync_UsesOfficialMultipartApiAndFormatsPages()
    {
        const string responseJson =
            """
            {
              "id": "ocr-test",
              "object": "ocr.result",
              "purpose": "text_recognition",
              "page_count": 2,
              "pages": [
                {"index":0,"text":"<｜ref｜>第一页<｜/ref｜><｜box｜>[[1,2,3,4]]<｜/box｜>","finish_reason":"stop"},
                {"index":1,"text":"second page","finish_reason":"stop"}
              ]
            }
            """;
        var handler = new CapturingHandler(responseJson);
        using var httpClient = new HttpClient(handler);
        using var client = new DeepSeekOcrClient(httpClient);
        client.Configure("test-key", isOfficialEndpoint: true);
        string filePath = CreateTempFile(".png");

        try
        {
            DeepSeekOcrResult? result = await client.ExtractResultAsync(filePath);

            result.Should().NotBeNull();
            result!.PageCount.Should().Be(2);
            result.Text.Should().Contain("第一页");
            result.Text.Should().NotContain("<｜ref｜>");
            result.Text.Should().Contain("Page 1/2");
            result.Text.Should().Contain("Page 2/2");
            result.Text.Should().Contain("second page");
            handler.RequestUri.Should().Be(DeepSeekOcrClient.OcrEndpoint);
            handler.Authorization.Should().Be("Bearer test-key");
            handler.RequestBody.Should().Contain("name=file");
            handler.RequestBody.Should().Contain("name=purpose");
            handler.RequestBody.Should().Contain("text_recognition");
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ExtractTextAsync_RequiresOfficialEndpoint()
    {
        var handler = new CapturingHandler("{}");
        using var httpClient = new HttpClient(handler);
        using var client = new DeepSeekOcrClient(httpClient);
        client.Configure("test-key", isOfficialEndpoint: false);
        string filePath = CreateTempFile(".png");

        try
        {
            string? result = await client.ExtractTextAsync(filePath);

            result.Should().BeNull();
            handler.RequestCount.Should().Be(0);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void ParseResponseText_ReturnsNullForApiError()
    {
        const string responseJson =
            """{"error":{"message":"OCR did not finish in time","type":"gateway_timeout_error"}}""";

        DeepSeekOcrClient.ParseResponseText(responseJson).Should().BeNull();
    }

    [Fact]
    public void OfficialOcrBilling_UsesSuccessfulPageCountAndDualCurrencyRates()
    {
        try
        {
            OcrService.ResetOfficialOcrStats();

            OcrService.RecordOfficialOcrSuccess(1000);
            OcrService.OfficialOcrSuccessPageCount.Should().Be(1000);
            OcrService.OfficialOcrCostYuan.Should().BeApproximately(1.5, 0.000001);
            OcrService.OfficialOcrCostUsd.Should().BeApproximately(0.225, 0.000001);

            OcrService.RecordOfficialOcrSuccess(0);
            OcrService.RecordOfficialOcrSuccess(-1);
            OcrService.OfficialOcrSuccessPageCount.Should().Be(1000);

            OcrService.RestoreOfficialOcrStats(250);
            OcrService.OfficialOcrSuccessPageCount.Should().Be(250);
            OcrService.OfficialOcrCostYuan.Should().BeApproximately(0.375, 0.000001);
            OcrService.OfficialOcrCostUsd.Should().BeApproximately(0.05625, 0.000001);
        }
        finally
        {
            OcrService.ResetOfficialOcrStats();
        }
    }

    [Fact]
    public async Task ExtractTextFromImageAsync_IsDisabledForVisionModel()
    {
        string filePath = CreateTempFile(".png");
        try
        {
            OcrService.VisionModelActive = true;
            string? result = await OcrService.ExtractTextFromImageAsync(filePath);
            result.Should().BeNull();
        }
        finally
        {
            OcrService.VisionModelActive = false;
            File.Delete(filePath);
        }
    }

    [Theory]
    [InlineData(true, "key", false, false)]
    [InlineData(false, "", false, false)]
    [InlineData(false, "key", true, false)]
    [InlineData(false, "key", false, true)]
    public void IsDeepSeekOfficialOcrAvailable_MatchesOfficialEndpointAndKey(
        bool isCustom,
        string apiKey,
        bool isVision,
        bool expected)
    {
        var config = new DeepSeekEndpointConfig(
            apiKey,
            "deepseek-flash",
            null,
            isCustom,
            IsVision: isVision);

        DeepSeekOptionsPage.IsDeepSeekOfficialOcrAvailable(config).Should().Be(expected);
    }

    [Theory]
    [InlineData("file.pdf", true)]
    [InlineData("file.docx", true)]
    [InlineData("file.ppt", true)]
    [InlineData("file.png", false)]
    [InlineData("file.txt", false)]
    public void IsOfficialOcrDocumentFile_RecognizesDocumentFormats(
        string fileName,
        bool expected)
    {
        OcrService.IsOfficialOcrDocumentFile(fileName).Should().Be(expected);
    }

    private static string CreateTempFile(string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"deepseek-ocr-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        return path;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _responseJson;

        public CapturingHandler(string responseJson)
        {
            _responseJson = responseJson;
        }

        public int RequestCount { get; private set; }
        public string? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestUri = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            RequestBody = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
