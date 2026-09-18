using System.Globalization;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Settings;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Settings;

public class ApprovalModeConverterTests
{
    [Fact]
    public void StandardValues_UseLocalizedLabels()
    {
        var converter = new ApprovalModeConverter();

        var values = converter.GetStandardValues(null)!.Cast<string>().ToArray();

        values.Should().Equal(
            LocalizationService.Instance["approval.blockAll"],
            LocalizationService.Instance["approval.allowAll"],
            LocalizationService.Instance["approval.smartBlock"]);
        values.Should().OnlyContain(value => !value.Contains("chat.approval", StringComparison.Ordinal));
    }

    [Fact]
    public void Convert_RoundTripsStoredAndLocalizedValues()
    {
        var converter = new ApprovalModeConverter();
        string localized = converter.ConvertTo(
            null,
            CultureInfo.CurrentCulture,
            "AllowAll",
            typeof(string))!.ToString()!;

        localized.Should().Be(LocalizationService.Instance["approval.allowAll"]);
        localized.Should().NotContain("[");
        converter.ConvertFrom(null, CultureInfo.CurrentCulture, localized)
            .Should().Be("AllowAll");
        converter.ConvertFrom(null, CultureInfo.CurrentCulture, "SmartBlock")
            .Should().Be("SmartBlock");
    }
}
