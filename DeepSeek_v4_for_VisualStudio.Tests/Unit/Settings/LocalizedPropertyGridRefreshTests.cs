using System.ComponentModel;
using System.Runtime.CompilerServices;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Settings;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Settings;

/// <summary>
/// 语言切换串行执行：本测试会改写全局 LocalizationService，禁止与其他测试并行。
/// </summary>
[CollectionDefinition("Localization", DisableParallelization = true)]
public class LocalizationCollectionDefinition
{
}

/// <summary>
/// 回归测试：切换语言后，VS 选项页（属性网格）里已经缓存的属性描述必须跟着切换。
/// .NET Framework 的 MemberDescriptor 会把 Description 缓存到私有字段，语言变更时
/// 必须清掉该缓存，否则会出现“属性名已切换、描述仍是旧语言”的现象。
/// </summary>
[Collection("Localization")]
public class LocalizedPropertyGridRefreshTests
{
    private static readonly System.Reflection.BindingFlags CacheFieldFlags =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    [Fact]
    public void ApprovalModeDescription_SwitchesWithLanguage_EvenForCachedDescriptor()
    {
        var localization = LocalizationService.Instance;
        string originalLanguage = localization.CurrentLanguage;

        try
        {
            // 触发 DeepSeekOptionsPage 的静态构造，注册语言变更后的属性网格刷新
            RuntimeHelpers.RunClassConstructor(typeof(DeepSeekOptionsPage).TypeHandle);

            var descriptor = TypeDescriptor
                .GetProperties(typeof(DeepSeekOptionsPage))[nameof(DeepSeekOptionsPage.ApprovalMode)];
            descriptor.Should().NotBeNull();

            // 先以中文读取一次，制造 MemberDescriptor 内部的描述缓存
            localization.SetLanguage("zh-CN");
            string chinese = descriptor!.Description;
            chinese.Should().Be(localization["settings.approvalMode.description"]);

            // 切换到英文后，同一个描述符必须返回英文描述（修复前会保留中文缓存）
            localization.SetLanguage("en");
            string english = descriptor.Description;
            english.Should().Be(localization["settings.approvalMode.description"]);
            english.Should().NotBe(chinese);

            // 再切回中文，确认双向切换都生效
            localization.SetLanguage("zh-CN");
            descriptor.Description.Should().Be(chinese);
        }
        finally
        {
            localization.SetLanguage(originalLanguage);
        }
    }

    [Fact]
    public void Refresh_ClearsMemberDescriptorDescriptionCache()
    {
        var descriptor = TypeDescriptor
            .GetProperties(typeof(DeepSeekOptionsPage))[nameof(DeepSeekOptionsPage.ApprovalMode)]!;
        descriptor.Description.Should().NotBeNullOrEmpty();

        var cacheFields = typeof(MemberDescriptor)
            .GetFields(CacheFieldFlags)
            .Where(field =>
                field.FieldType == typeof(string)
                && field.Name.IndexOf("description", StringComparison.OrdinalIgnoreCase) >= 0)
            .ToArray();
        cacheFields.Should().NotBeEmpty();
        cacheFields.Should().OnlyContain(field => field.GetValue(descriptor) != null);

        LocalizedPropertyGridRefresh.Refresh(typeof(DeepSeekOptionsPage));

        cacheFields.Should().OnlyContain(field => field.GetValue(descriptor) == null);
    }
}