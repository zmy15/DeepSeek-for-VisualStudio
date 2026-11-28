using System;
using System.Collections.Generic;
using DeepSeek_v4_for_VisualStudio.Services.WebAssets;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// <see cref="WebAssetLoader"/> 的加载、占位符替换与取值校验测试。
/// 这些用例不依赖具体资源内容，因此在资源尚未全部外移时也能稳定覆盖规则本身。
/// </summary>
public class WebAssetLoaderTests
{
    private static Dictionary<string, string> Map(params (string Key, string Value)[] pairs)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
            map[pair.Key] = pair.Value;
        return map;
    }

    [Fact]
    public void ApplyPlaceholders_ReplacesAllKnownKeys()
    {
        string raw = "var a={{alpha}};var b={{beta}};";

        string result = WebAssetLoader.ApplyPlaceholders(
            raw, "probe.js", Map(("alpha", "\"A\""), ("beta", "\"B\"")));

        result.Should().Be("var a=\"A\";var b=\"B\";");
    }

    [Fact]
    public void ApplyPlaceholders_WithoutPlaceholders_ReturnsRawUnchanged()
    {
        const string raw = "window.x = 1;";

        string result = WebAssetLoader.ApplyPlaceholders(raw, "probe.js", Map());

        result.Should().Be(raw);
    }

    [Fact]
    public void ApplyPlaceholders_MissingKey_ThrowsWithKeyName()
    {
        // 该用例必须显式失败而不是产出含占位符的脚本：
        // 未替换的占位符进入运行时会导致前端语法错误。
        Action act = () => WebAssetLoader.ApplyPlaceholders(
            "var a={{alpha}};", "probe.js", Map(("other", "x")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*alpha*");
    }

    [Fact]
    public void ApplyPlaceholders_MissingKeyWithEmptyValue_StillThrows()
    {
        // 回归：替换值为空串时，若仅靠「替换后是否残留 {{」判断会漏报缺键
        Action act = () => WebAssetLoader.ApplyPlaceholders(
            "var a={{alpha}};", "probe.js", Map());

        act.Should().Throw<InvalidOperationException>().WithMessage("*alpha*");
    }

    [Theory]
    [InlineData("var a={{bad-key}};")]
    [InlineData("var a={{unclosed;")]
    public void ApplyPlaceholders_MalformedPlaceholder_Throws(string raw)
    {
        Action act = () => WebAssetLoader.ApplyPlaceholders(
            raw, "probe.js", Map(("bad", "x"), ("unclosed", "y")));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ApplyPlaceholders_ValueContainingDollarSign_IsInsertedLiterally()
    {
        // 使用 MatchEvaluator 替换，值中的 $ 不得被当作正则替换模式解析
        string result = WebAssetLoader.ApplyPlaceholders(
            "var price={{price}};", "probe.js", Map(("price", "\"$1$2\"")));

        result.Should().Be("var price=\"$1$2\";");
    }

    [Fact]
    public void GetJs_UnknownResource_ThrowsAndListsAvailableAssets()
    {
        Action act = () => WebAssetLoader.GetJs("this-asset-does-not-exist");

        act.Should().Throw<InvalidOperationException>().WithMessage("*未找到 WebAsset 资源*");
    }

    [Fact]
    public void GetCss_UnknownResource_Throws()
    {
        Action act = () => WebAssetLoader.GetCss("this-style-does-not-exist");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void GetJs_EmptyName_ThrowsArgumentException()
    {
        Action act = () => WebAssetLoader.GetJs("   ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EnumerateResourceNames_ReturnsSortedList()
    {
        IReadOnlyList<string> js = WebAssetLoader.EnumerateJsResourceNames();
        IReadOnlyList<string> css = WebAssetLoader.EnumerateCssResourceNames();

        js.Should().NotBeNull();
        css.Should().NotBeNull();
        js.Should().BeInAscendingOrder(StringComparer.Ordinal);
        css.Should().BeInAscendingOrder(StringComparer.Ordinal);
        // 短名不得带扩展名，否则调用方按短名读取会失败
        js.Should().OnlyContain(name => !name.EndsWith(".js", StringComparison.Ordinal));
        css.Should().OnlyContain(name => !name.EndsWith(".css", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("#1e1e1e")]
    [InlineData("rgba(0, 0, 0, 0.5)")]
    [InlineData("'Segoe UI'")]
    public void ValidateCssValue_AllowsSafeValues(string value)
    {
        WebAssetLoader.ValidateCssValue(value).Should().Be(value);
    }

    [Theory]
    [InlineData("#fff;color:red")]
    [InlineData("red}body{color:blue")]
    [InlineData("<script>")]
    public void ValidateCssValue_RejectsDeclarationBreakingValues(string value)
    {
        Action act = () => WebAssetLoader.ValidateCssValue(value);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ValidateCssValue_Null_Throws()
    {
        Action act = () => WebAssetLoader.ValidateCssValue(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
