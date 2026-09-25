using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class ModelTokenLimitServiceTests
{
    [Fact]
    public void DefaultsToOneMillionTokens()
    {
        ModelTokenLimitService.GetMaxTokens("deepseek-v4-pro", null)
            .Should().Be(1_000_000);
        ModelTokenLimitService.GetMaxTokens("custom-model", string.Empty)
            .Should().Be(1_000_000);
    }

    [Fact]
    public void ParseLimits_IsCaseInsensitiveAndSupportsKAndMSuffixes()
    {
        var limits = ModelTokenLimitService.ParseLimits(
            "DeepSeek-V4-Pro=1M\ncustom-model=128K\ninvalid\n# comment\nother=32000");

        limits["deepseek-v4-pro"].Should().Be(1_000_000);
        limits["CUSTOM-MODEL"].Should().Be(128_000);
        limits["other"].Should().Be(32_000);
        limits.Should().NotContainKey("invalid");
        limits.Should().NotContainKey("# comment");
    }

    [Theory]
    [InlineData("deepseek-v4-pro", 90, 900_000)]
    [InlineData("deepseek-v4-pro", 80, 800_000)]
    [InlineData("custom-model", 50, 500_000)]
    public void CalculateBudget_UsesModelLimitAndSharedPercent(
        string model,
        int percent,
        int expected)
    {
        ModelTokenLimitService.CalculateBudget(
                model,
                percent,
                "deepseek-v4-pro=1M\ncustom-model=1M")
            .Should().Be(expected);
    }

    [Fact]
    public void CalculateBudget_UsesPerModelOverride()
    {
        string configuration = "deepseek-v4-pro=1M\ncustom-model=128K";

        ModelTokenLimitService.CalculateBudget("deepseek-v4-pro", 90, configuration)
            .Should().Be(900_000);
        ModelTokenLimitService.CalculateBudget("custom-model", 90, configuration)
            .Should().Be(115_200);
    }

    [Theory]
    [InlineData(1_000_000, "1M")]
    [InlineData(128_000, "128K")]
    [InlineData(32_000, "32K")]
    [InlineData(12_345, "12345")]
    public void FormatTokenCount_ReturnsCompactEditorValue(int value, string expected)
    {
        ModelTokenLimitService.FormatTokenCount(value).Should().Be(expected);
    }

    [Fact]
    public void NormalizeBudgetPercent_MigratesLegacyAbsoluteTokenValue()
    {
        ModelTokenLimitService.NormalizeBudgetPercent(900_000).Should().Be(90);
        ModelTokenLimitService.NormalizeBudgetPercent(500_000).Should().Be(50);
        ModelTokenLimitService.NormalizeBudgetPercent(0).Should().Be(90);
        ModelTokenLimitService.NormalizeBudgetPercent(120).Should().Be(100);
    }
}