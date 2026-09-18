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

        converter.GetStandardValues(null)!.Cast<string>().Should().Equal(
            LocalizationService.Instance["chat.approval.blockAll"],
            LocalizationService.Instance["chat.approval.allowAll"],
            LocalizationService.Instance["chat.approval.smartBlock"]);
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

        localized.Should().Be(LocalizationService.Instance["chat.approval.allowAll"]);
        converter.ConvertFrom(null, CultureInfo.CurrentCulture, localized)
            .Should().Be("AllowAll");
        converter.ConvertFrom(null, CultureInfo.CurrentCulture, "SmartBlock")
            .Should().Be("SmartBlock");
    }
}
