using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DeepSeek_v4_for_VisualStudio.Services.WebAssets;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 嵌入资源的结构完整性检查。
/// 这些用例不依赖 Node，因此 CI 与本地环境都能覆盖：资源能否被加载、
/// 占位符写法是否成对合法、是否为无 BOM 的 UTF-8 文本。
/// </summary>
public class WebAssetIntegrityTests
{
    /// <summary>合法占位符：{{key}}，key 仅允许字母、数字与下划线。</summary>
    private static readonly Regex WellFormedPlaceholder = new(
        @"\{\{[A-Za-z0-9_]+\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string RepoRoot
    {
        get
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "Resources", "Chat")))
                    return dir.FullName;
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                $"未找到 Resources\\Chat 目录（起点: {AppContext.BaseDirectory}）");
        }
    }

    [Fact]
    public void AllJsAssets_AreLoadable()
    {
        IReadOnlyList<string> names = WebAssetLoader.EnumerateJsResourceNames();
        names.Should().NotBeEmpty("至少应嵌入静态 JS 资源");

        foreach (string name in names)
        {
            // 含占位符的资源必须提供取值才能加载，否则 loader 会按设计抛异常。
            // 这里只关心「资源已嵌入且占位符声明的 key 可被解析」，故给出占位值即可。
            string raw = File.ReadAllText(Path.Combine(RepoRoot, "Resources", "Chat", "Js", name + ".js"));
            Dictionary<string, string> map = BuildPlaceholderMap(raw);

            Action act = () => WebAssetLoader.GetJs(name, map);
            act.Should().NotThrow($"资源 {name} 应可被 WebAssetLoader 读取");
        }
    }

    /// <summary>
    /// 按资源原文里出现的占位符 key 构造占位值映射。
    /// 取值统一给合法 JS 字符串字面量，确保替换后的脚本语法仍然成立。
    /// </summary>
    /// <param name="raw">资源原文。</param>
    /// <returns>占位符键到占位值的映射；无占位符时返回空字典。</returns>
    private static Dictionary<string, string> BuildPlaceholderMap(string raw)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match match in Regex.Matches(raw, @"\{\{([A-Za-z0-9_]+)\}\}"))
            map[match.Groups[1].Value] = "\"placeholder\"";
        return map;
    }

    [Fact]
    public void AllCssAssets_AreLoadable()
    {
        IReadOnlyList<string> names = WebAssetLoader.EnumerateCssResourceNames();
        names.Should().Contain("dark");
        names.Should().Contain("light");

        foreach (string name in names)
        {
            Action act = () => WebAssetLoader.GetCss(name);
            act.Should().NotThrow($"样式 {name} 应可被 WebAssetLoader 读取");
        }
    }

    [Fact]
    public void EveryPlaceholderBlock_IsWellFormed()
    {
        foreach (string name in WebAssetLoader.EnumerateJsResourceNames())
        {
            string raw = File.ReadAllText(Path.Combine(RepoRoot, "Resources", "Chat", "Js", name + ".js"));

            int opened = CountOccurrences(raw, "{{");
            int wellFormed = WellFormedPlaceholder.Matches(raw).Count;

            // 落单的 {{ 或非法的 key 会让占位符在运行期残留，导致前端语法错误
            opened.Should().Be(wellFormed,
                $"资源 {name} 存在无法解析的占位符写法（{{ 出现 {opened} 次，合法占位符 {wellFormed} 个）");
        }
    }

    [Fact]
    public void AllWebAssets_HaveNoUtf8Bom()
    {
        foreach (string folder in new[] { "Js", "Css" })
        {
            string dir = Path.Combine(RepoRoot, "Resources", "Chat", folder);
            if (!Directory.Exists(dir)) continue;

            foreach (string file in Directory.GetFiles(dir, folder == "Js" ? "*.js" : "*.css"))
            {
                byte[] bytes = File.ReadAllBytes(file);

                // BOM 会让部分解析器把首字符判为非法，资源文件统一要求无 BOM
                bool hasBom = bytes.Length >= 3
                    && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

                hasBom.Should().BeFalse($"{Path.GetFileName(file)} 不应包含 UTF-8 BOM");
            }
        }
    }

    private static int CountOccurrences(string text, string token)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }
}
