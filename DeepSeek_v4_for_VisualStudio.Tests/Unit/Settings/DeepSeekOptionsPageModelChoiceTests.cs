using System.ComponentModel;
using System.Reflection;
using DeepSeek_v4_for_VisualStudio.Settings;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Settings;

public class DeepSeekOptionsPageModelChoiceTests
{
    private static readonly string[] ModelSettingsProperties =
    {
        nameof(DeepSeekOptionsPage.ApiKey),
        nameof(DeepSeekOptionsPage.CustomApiKey),
        nameof(DeepSeekOptionsPage.ApiBaseUrl),
        nameof(DeepSeekOptionsPage.CustomModelName),
        nameof(DeepSeekOptionsPage.ModelMaxTokenLimits),
        nameof(DeepSeekOptionsPage.CustomVisionModels),
        nameof(DeepSeekOptionsPage.CustomModelPicker),
        nameof(DeepSeekOptionsPage.TestConnection),
        nameof(DeepSeekOptionsPage.SelectedModelChoice),
        nameof(DeepSeekOptionsPage.IsThinkingEnabled),
        nameof(DeepSeekOptionsPage.ReasoningEffort),
    };

    [Fact]
    public void ModelSettings_AreMergedIntoOneVisibleCategory()
    {
        string expectedCategory = LocalizationService.Instance["settings.category.model"];

        foreach (var propertyName in ModelSettingsProperties)
        {
            var property = typeof(DeepSeekOptionsPage).GetProperty(propertyName)!;
            property.GetCustomAttribute<BrowsableAttribute>()?.Browsable.Should().NotBe(false);
            property.GetCustomAttribute<CategoryAttribute>()?.Category.Should().Be(expectedCategory);
        }

        typeof(DeepSeekOptionsPage)
            .GetProperty(nameof(DeepSeekOptionsPage.ActiveModelSource))!
            .GetCustomAttribute<BrowsableAttribute>()!
            .Browsable
            .Should()
            .BeFalse();
    }

    [Fact]
    public void ModelSettings_UseSpecifiedDisplayOrder()
    {
        string[] expectedOrder =
        {
            nameof(DeepSeekOptionsPage.ApiKey),
            nameof(DeepSeekOptionsPage.ApiBaseUrl),
            nameof(DeepSeekOptionsPage.CustomApiKey),
            nameof(DeepSeekOptionsPage.TestConnection),
            nameof(DeepSeekOptionsPage.CustomModelPicker),
            nameof(DeepSeekOptionsPage.CustomModelName),
            nameof(DeepSeekOptionsPage.ModelMaxTokenLimits),
            nameof(DeepSeekOptionsPage.CustomVisionModels),
            nameof(DeepSeekOptionsPage.SelectedModelChoice),
            nameof(DeepSeekOptionsPage.IsThinkingEnabled),
            nameof(DeepSeekOptionsPage.ReasoningEffort),
        };

        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
            typeof(DeepSeekOptionsPage).TypeHandle);
        var properties = TypeDescriptor.GetProperties(typeof(DeepSeekOptionsPage));
        string availableProperties = string.Join(", ", properties.Cast<PropertyDescriptor>().Select(p => p.Name));
        int previousIndex = -1;
        foreach (string propertyName in expectedOrder)
        {
            var property = properties[propertyName];
            property.Should().NotBeNull($"available properties: {availableProperties}");
            int index = properties.IndexOf(property!);
            index.Should().BeGreaterThan(previousIndex, propertyName);
            previousIndex = index;
        }
    }

    [Fact]
    public void ModelMaxTokenLimits_UsesOptionEditor()
    {
        var property = typeof(DeepSeekOptionsPage)
            .GetProperty(nameof(DeepSeekOptionsPage.ModelMaxTokenLimits))!;

        property.CanWrite.Should().BeTrue();
        property.GetCustomAttribute<EditorAttribute>()?.EditorTypeName
            .Should().Contain(nameof(ModelMaxTokenEditor));
    }

    [Fact]
    public void CustomVisionModels_RemainsWritableAndDiscoverableByPicker()
    {
        var property = typeof(DeepSeekOptionsPage)
            .GetProperty(nameof(DeepSeekOptionsPage.CustomVisionModels))!;

        property.CanWrite.Should().BeTrue();
        (property.GetCustomAttribute<ReadOnlyAttribute>()?.IsReadOnly ?? false)
            .Should().BeFalse();
        property.GetCustomAttribute<DesignerSerializationVisibilityAttribute>()?.Visibility
            .Should().Be(DesignerSerializationVisibility.Visible);
        property.GetCustomAttribute<EditorAttribute>()?.EditorTypeName
            .Should().Contain(nameof(VisionModelPickerEditor));
    }
}
