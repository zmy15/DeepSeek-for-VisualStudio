using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;

namespace DeepSeek_v4_for_VisualStudio.CodeCompletion
{
    /// <summary>
    /// Snapshot of the code-completion settings that affect an existing editor view.
    /// </summary>
    internal readonly record struct CompletionRuntimeSettings(
        bool Enabled,
        int DelayMilliseconds,
        bool ContinueAfterAccept);

    /// <summary>
    /// Watches <see cref="DeepSeekOptionsPage.SettingsChanged"/> and raises a change
    /// only when a completion-related setting has changed. This keeps existing text
    /// views in sync with both the classic options page and Unified Settings.
    /// </summary>
    internal sealed class CompletionSettingsMonitor : IDisposable
    {
        private readonly Func<DeepSeekOptionsPage?> optionsProvider;
        private bool disposed;

        public CompletionSettingsMonitor(Func<DeepSeekOptionsPage?> optionsProvider)
        {
            this.optionsProvider = optionsProvider
                ?? throw new ArgumentNullException(nameof(optionsProvider));

            Current = ReadSettings(optionsProvider());
            DeepSeekOptionsPage.SettingsChanged += OnSettingsChanged;
        }

        public CompletionRuntimeSettings Current { get; private set; }

        public event Action<CompletionRuntimeSettings>? Changed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            DeepSeekOptionsPage.SettingsChanged -= OnSettingsChanged;
        }

        private void OnSettingsChanged()
        {
            if (disposed)
            {
                return;
            }

            try
            {
                CompletionRuntimeSettings next = ReadSettings(optionsProvider());
                if (next == Current)
                {
                    return;
                }

                Current = next;
                Changed?.Invoke(next);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Completion] Failed to apply runtime completion settings: {ex.Message}");
            }
        }

        internal static CompletionRuntimeSettings ReadSettings(DeepSeekOptionsPage? options)
        {
            if (options == null)
            {
                return new CompletionRuntimeSettings(
                    Enabled: false,
                    DelayMilliseconds: 800,
                    ContinueAfterAccept: true);
            }

            return new CompletionRuntimeSettings(
                options.AutoCompleteEnabled,
                Math.Max(1, options.AutoCompleteDelay),
                options.AutoCompleteContinueAfterAccept);
        }
    }
}
