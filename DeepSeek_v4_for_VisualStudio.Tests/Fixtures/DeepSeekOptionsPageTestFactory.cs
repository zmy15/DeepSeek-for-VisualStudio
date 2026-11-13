using System.Runtime.Serialization;
using DeepSeek_v4_for_VisualStudio.Settings;

namespace DeepSeek_v4_for_VisualStudio.Tests.Fixtures;

/// <summary>
/// Creates DeepSeekOptionsPage without invoking DialogPage's VS-hosted constructor.
/// Only tests for pure settings logic should use this factory.
/// </summary>
internal static class DeepSeekOptionsPageTestFactory
{
    public static DeepSeekOptionsPage Create()
        => (DeepSeekOptionsPage)FormatterServices.GetUninitializedObject(
            typeof(DeepSeekOptionsPage));
}
