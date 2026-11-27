using DeepSeek_v4_for_VisualStudio.CodeCompletion;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Tests.Fixtures;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.CodeCompletion;

public class CompletionSettingsMonitorTests
{
    [Fact]
    public void SettingsChanged_WhenCompletionIsEnabled_NotifiesLiveViews()
    {
        var page = DeepSeekOptionsPageTestFactory.Create();
        page.AutoCompleteEnabled = false;
        page.AutoCompleteDelay = 800;
        page.AutoCompleteContinueAfterAccept = true;

        using var monitor = new CompletionSettingsMonitor(() => page);
        CompletionRuntimeSettings? observed = null;
        monitor.Changed += settings => observed = settings;

        page.AutoCompleteEnabled = true;
        page.AutoCompleteDelay = 350;
        page.AutoCompleteContinueAfterAccept = false;
        DeepSeekOptionsPage.NotifySettingsChanged();

        monitor.Current.Should().Be(new CompletionRuntimeSettings(
            Enabled: true,
            DelayMilliseconds: 350,
            ContinueAfterAccept: false));
        observed.Should().Be(monitor.Current);
    }

    [Fact]
    public void SettingsChanged_WhenCompletionIsDisabled_NotifiesLiveViews()
    {
        var page = DeepSeekOptionsPageTestFactory.Create();
        page.AutoCompleteEnabled = true;
        page.AutoCompleteDelay = 500;
        page.AutoCompleteContinueAfterAccept = true;

        using var monitor = new CompletionSettingsMonitor(() => page);
        CompletionRuntimeSettings? observed = null;
        monitor.Changed += settings => observed = settings;

        page.AutoCompleteEnabled = false;
        DeepSeekOptionsPage.NotifySettingsChanged();

        monitor.Current.Enabled.Should().BeFalse();
        observed.Should().Be(monitor.Current);
    }

    [Fact]
    public void DisposedMonitor_IgnoresLaterSettingsChanges()
    {
        var page = DeepSeekOptionsPageTestFactory.Create();
        page.AutoCompleteEnabled = false;
        var monitor = new CompletionSettingsMonitor(() => page);
        CompletionRuntimeSettings? observed = null;
        monitor.Changed += settings => observed = settings;

        monitor.Dispose();
        page.AutoCompleteEnabled = true;
        DeepSeekOptionsPage.NotifySettingsChanged();

        observed.Should().BeNull();
    }

    [Fact]
    public void OptionsInstanceReplacement_RefreshesRestoredViews()
    {
        DeepSeekOptionsPage? originalInstance = DeepSeekOptionsPage.Instance;
        try
        {
            var startupDefaults = DeepSeekOptionsPageTestFactory.Create();
            startupDefaults.AutoCompleteEnabled = false;
            startupDefaults.AutoCompleteDelay = 800;
            startupDefaults.AutoCompleteContinueAfterAccept = true;

            var persistedOptions = DeepSeekOptionsPageTestFactory.Create();
            persistedOptions.AutoCompleteEnabled = true;
            persistedOptions.AutoCompleteDelay = 450;
            persistedOptions.AutoCompleteContinueAfterAccept = false;

            DeepSeekOptionsPage.Instance = startupDefaults;
            using var monitor = new CompletionSettingsMonitor(
                () => DeepSeekOptionsPage.Instance);
            CompletionRuntimeSettings? observed = null;
            monitor.Changed += settings => observed = settings;

            DeepSeekOptionsPage.Instance = persistedOptions;

            monitor.Current.Should().Be(new CompletionRuntimeSettings(
                Enabled: true,
                DelayMilliseconds: 450,
                ContinueAfterAccept: false));
            observed.Should().Be(monitor.Current);
        }
        finally
        {
            DeepSeekOptionsPage.Instance = originalInstance;
        }
    }
}
