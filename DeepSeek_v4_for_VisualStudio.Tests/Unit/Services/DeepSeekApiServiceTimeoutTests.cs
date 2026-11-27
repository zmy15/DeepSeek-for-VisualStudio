using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class DeepSeekApiServiceTimeoutTests
{
    [Fact]
    public void Constructor_AppliesConfiguredTimeout()
    {
        using var service = new DeepSeekApiService(
            "test-key",
            requestTimeoutSeconds: 120);

        service.RequestTimeout.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void UpdateRequestTimeout_ClampsAndAppliesNewValue()
    {
        using var service = new DeepSeekApiService("test-key");

        service.UpdateRequestTimeout(5);
        service.RequestTimeout.Should().Be(TimeSpan.FromSeconds(30));

        service.UpdateRequestTimeout(180);
        service.RequestTimeout.Should().Be(TimeSpan.FromSeconds(180));

        service.UpdateRequestTimeout(5000);
        service.RequestTimeout.Should().Be(TimeSpan.FromSeconds(3600));
    }
}
