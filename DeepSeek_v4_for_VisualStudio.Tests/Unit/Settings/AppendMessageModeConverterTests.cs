using System.ComponentModel;
using System.Globalization;
using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Settings;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Settings;

public class AppendMessageModeConverterTests
{
    [Fact]
    public void StandardValues_ContainEnumValues()
    {
        var converter = new AppendMessageModeConverter();

        converter.GetStandardValues(null)!.Cast<AppendMessageMode>()
            .Should().Equal(AppendMessageMode.Queue, AppendMessageMode.Guidance);
    }

    [Fact]
    public void Convert_RoundTripsLocalizedDisplayText()
    {
        var converter = new AppendMessageModeConverter();
        string queueText = converter.ConvertTo(
            null,
            CultureInfo.CurrentCulture,
            AppendMessageMode.Queue,
            typeof(string))!.ToString()!;

        queueText.Should().Be(LocalizationService.Instance["settings.appendMessageMode.queue"]);
        converter.ConvertFrom(null, CultureInfo.CurrentCulture, queueText)
            .Should().Be(AppendMessageMode.Queue);
        converter.ConvertFrom(null, CultureInfo.CurrentCulture, "Guidance")
            .Should().Be(AppendMessageMode.Guidance);
    }

    [Theory]
    [InlineData("zh-CN", "settings.appendMessageMode.queue", AppendMessageMode.Queue)]
    [InlineData("zh-CN", "settings.appendMessageMode.guidance", AppendMessageMode.Guidance)]
    [InlineData("en", "settings.appendMessageMode.queue", AppendMessageMode.Queue)]
    [InlineData("en", "settings.appendMessageMode.guidance", AppendMessageMode.Guidance)]
    public void ConvertFrom_AcceptsLocalizedTextFromAnySupportedLanguage(
        string locale,
        string resourceKey,
        AppendMessageMode expected)
    {
        var converter = new AppendMessageModeConverter();
        string localized = LocalizationService.Instance.GetValueForLocale(resourceKey, locale);

        object? result = converter.ConvertFrom(null, CultureInfo.CurrentCulture, localized);

        result.Should().Be(expected);
    }

    [Fact]
    public void ConvertFrom_UnknownPersistedText_FallsBackToDefault()
    {
        var converter = new AppendMessageModeConverter();

        object? result = converter.ConvertFrom(
            null,
            CultureInfo.CurrentCulture,
            "legacy-localized-value");

        result.Should().Be(DeepSeekOptionsPage.DefaultAppendMessageMode);
    }
}
