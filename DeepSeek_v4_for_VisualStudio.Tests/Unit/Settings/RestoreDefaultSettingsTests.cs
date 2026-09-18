using System.ComponentModel;
using System.Drawing.Design;
using DeepSeek_v4_for_VisualStudio.Settings;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Settings;

public class RestoreDefaultSettingsTests
{
    [Fact]
    public void RestoreDefaultSettings_UsesButtonEditorWithoutPersistingValue()
    {
        var property = typeof(DeepSeekOptionsPage)
            .GetProperty(nameof(DeepSeekOptionsPage.RestoreDefaultSettings))!;

        property.GetCustomAttributes(typeof(EditorAttribute), inherit: false)
            .Cast<EditorAttribute>()
            .Should().Contain(attribute =>
                attribute.EditorTypeName == typeof(RestoreDefaultSettingsEditor).AssemblyQualifiedName);

        property.GetCustomAttributes(typeof(DesignerSerializationVisibilityAttribute), inherit: false)
            .Cast<DesignerSerializationVisibilityAttribute>()
            .Should().ContainSingle()
            .Which.Visibility.Should().Be(DesignerSerializationVisibility.Hidden);
    }
}
