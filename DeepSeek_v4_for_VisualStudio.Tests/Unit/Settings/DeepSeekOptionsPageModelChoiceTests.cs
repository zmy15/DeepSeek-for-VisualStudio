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
        nameof(DeepSeekOptionsPage.ModelCatalog),
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
            nameof(DeepSeekOptionsPage.ModelCatalog),
            nameof(DeepSeekOptionsPage.SelectedModelChoice),
            nameof(DeepSeekOptionsPage.IsThinkingEnabled),
            nameof(DeepSeekOptionsPage.ReasoningEffort),
        };

        var properties = TypeDescriptor.GetProperties(typeof(DeepSeekOptionsPage));
        var displayNames = expectedOrder
            .Select(propertyName => properties[propertyName]?.DisplayName ?? string.Empty)
            .ToList();

        displayNames.Select(name => name.Length >= 3 ? name.Substring(0, 3) : string.Empty)
            .Should().Equal("01.", "02.", "03.", "04.", "05.", "06.", "07.", "08.");
        displayNames
            .SequenceEqual(displayNames.OrderBy(name => name, StringComparer.CurrentCulture))
            .Should().BeTrue();
    }

    [Fact]
    public void About_UsesAboutEditorAndPublishesProjectLinks()
    {
        var property = typeof(DeepSeekOptionsPage).GetProperty(nameof(DeepSeekOptionsPage.About))!;

        property.GetCustomAttribute<EditorAttribute>()?.EditorTypeName
            .Should().Contain(nameof(AboutEditor));
        AboutInfo.RepositoryUrl.Should().Be("https://github.com/zmy15/DeepSeek-for-VisualStudio");
        AboutInfo.IssuesUrl.Should().Be(AboutInfo.RepositoryUrl + "/issues");
        AboutInfo.Version.Should().Be(Vsix.Version);
    }

    [Fact]
    public void ModelCatalog_UsesUnifiedEditorAndHidesLegacyProperties()
    {
        // 统一入口属性使用 ModelCatalogEditor，且不持久化自身值。
        var catalog = typeof(DeepSeekOptionsPage)
            .GetProperty(nameof(DeepSeekOptionsPage.ModelCatalog))!;
        catalog.GetCustomAttribute<EditorAttribute>()?.EditorTypeName
            .Should().Contain(nameof(ModelCatalogEditor));
        catalog.GetCustomAttribute<DesignerSerializationVisibilityAttribute>()?.Visibility
            .Should().Be(DesignerSerializationVisibility.Hidden);

        // 旧的三个底层字符串属性仍需保留且可写（供运行时消费链与热更新使用），
        // 但已从属性网格隐藏，避免与统一表格重复。
        foreach (var legacyName in new[]
        {
            nameof(DeepSeekOptionsPage.CustomModelName),
            nameof(DeepSeekOptionsPage.ModelMaxTokenLimits),
            nameof(DeepSeekOptionsPage.CustomVisionModels),
            nameof(DeepSeekOptionsPage.CustomModelPicker),
        })
        {
            var property = typeof(DeepSeekOptionsPage).GetProperty(legacyName)!;
            property.GetCustomAttribute<BrowsableAttribute>()!.Browsable.Should().BeFalse();
        }

        // 底层字符串属性仍可被写入，保证 ModelCatalogMapping 回写路径可用。
        typeof(DeepSeekOptionsPage)
            .GetProperty(nameof(DeepSeekOptionsPage.ModelMaxTokenLimits))!
            .CanWrite.Should().BeTrue();
        typeof(DeepSeekOptionsPage)
            .GetProperty(nameof(DeepSeekOptionsPage.CustomModelName))!
            .CanWrite.Should().BeTrue();
    }
}
