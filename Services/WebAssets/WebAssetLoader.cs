using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace DeepSeek_v4_for_VisualStudio.Services.WebAssets
{
    /// <summary>
    /// 前端静态资源（JS / CSS）加载器 — 从程序集嵌入资源读取文本、按资源名缓存原文，
    /// 并支持 <c>{{key}}</c> 占位符一次性替换，使大段脚本与样式不再以 C# 字符串字面量形式维护。
    /// </summary>
    /// <remarks>
    /// 占位符替换值必须由调用方先行转义（JS 用 <c>ChatHtmlService.EscapeJsString</c> 产出的
    /// 可直接嵌入表达式的片段，CSS 需先经 <see cref="ValidateCssValue"/> 校验），本类**不做二次转义**，
    /// 以免双重转义破坏字面量。
    /// </remarks>
    public static class WebAssetLoader
    {
        /// <summary>当前程序集，嵌入资源由它承载。</summary>
        private static readonly Assembly AssetAssembly = typeof(WebAssetLoader).Assembly;

        /// <summary>
        /// 资源名根前缀。取程序集名而非硬编码 RootNamespace，
        /// 避免程序集名与根命名空间不一致时资源名推导失效。
        /// </summary>
        private static readonly string RootPrefix = AssetAssembly.GetName().Name ?? "DeepSeek_v4_for_VisualStudio";

        /// <summary>JS 资源完整前缀（不含短名与扩展名）。</summary>
        private static readonly string JsFullPrefix = RootPrefix + ".Resources.Chat.Js.";

        /// <summary>CSS 资源完整前缀（不含短名与扩展名）。</summary>
        private static readonly string CssFullPrefix = RootPrefix + ".Resources.Chat.Css.";

        /// <summary>JS 资源扩展名。</summary>
        private const string JsExtension = ".js";

        /// <summary>CSS 资源扩展名。</summary>
        private const string CssExtension = ".css";

        /// <summary>占位符正则：<c>{{key}}</c>，key 仅允许字母、数字与下划线。</summary>
        private static readonly Regex PlaceholderRegex = new Regex(
            @"\{\{(?<key>[A-Za-z0-9_]+)\}\}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// 带引号占位符正则：<c>"{{key}}"</c>（整段含两侧双引号）。
        /// 取值位置必须用它：裸 <c>{{key}}</c> 落在对象字面量或变量赋值处是非法 JS，
        /// 写成带引号形式后文件本身即为合法脚本，替换时再把整段（含引号）换成已转义的字面量。
        /// </summary>
        private static readonly Regex QuotedPlaceholderRegex = new Regex(
            @"""\{\{(?<key>[A-Za-z0-9_]+)\}\}""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// 资源原文缓存，键为完整资源名。
        /// 只缓存原文不缓存替换结果：占位符组合几乎不可枚举，缓存结果会造成内存无界增长。
        /// </summary>
        private static readonly ConcurrentDictionary<string, string> RawCache =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        /// <summary>无占位符场景的共享空字典，避免每次分配。</summary>
        private static readonly IReadOnlyDictionary<string, string> EmptyPlaceholders =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// 读取不含占位符的 JS 资源原文。
        /// </summary>
        /// <param name="name">资源短名，如 <c>detox-emojis</c>，对应 <c>Resources\Chat\Js\detox-emojis.js</c>。</param>
        /// <returns>UTF-8 解码后的脚本文本。</returns>
        /// <exception cref="InvalidOperationException">资源不存在，或原文中残留无法解析的占位符。</exception>
        public static string GetJs(string name)
        {
            string fullName = BuildFullName(JsFullPrefix, name, JsExtension);
            return ApplyPlaceholders(LoadRaw(fullName), fullName, EmptyPlaceholders);
        }

        /// <summary>
        /// 读取 JS 资源并替换全部占位符。
        /// </summary>
        /// <param name="name">资源短名。</param>
        /// <param name="placeholders">占位符键到「已转义的 JS 表达式片段」的映射。</param>
        /// <returns>替换完成的脚本文本。</returns>
        /// <exception cref="InvalidOperationException">
        /// 资源不存在；引用了字典中不存在的 key；替换后仍残留无法解析的占位符。
        /// </exception>
        public static string GetJs(string name, IReadOnlyDictionary<string, string> placeholders)
        {
            string fullName = BuildFullName(JsFullPrefix, name, JsExtension);
            return ApplyPlaceholders(LoadRaw(fullName), fullName, placeholders ?? EmptyPlaceholders);
        }

        /// <summary>
        /// 读取 CSS 资源并（可选）替换占位符。
        /// </summary>
        /// <param name="name">资源短名，如 <c>dark</c>，对应 <c>Resources\Chat\Css\dark.css</c>。</param>
        /// <param name="placeholders">占位符映射；取值需事先通过 <see cref="ValidateCssValue"/> 校验。</param>
        /// <returns>替换完成的样式文本。</returns>
        /// <exception cref="InvalidOperationException">资源不存在，或占位符未全部替换。</exception>
        public static string GetCss(string name, IReadOnlyDictionary<string, string>? placeholders = null)
        {
            string fullName = BuildFullName(CssFullPrefix, name, CssExtension);
            return ApplyPlaceholders(LoadRaw(fullName), fullName, placeholders ?? EmptyPlaceholders);
        }

        /// <summary>
        /// 校验 CSS 替换值安全性：禁止出现可跳出声明块的字符，
        /// 防止运行时主题色等取值意外截断或注入额外规则。
        /// </summary>
        /// <param name="value">待校验的 CSS 取值。</param>
        /// <returns>校验通过时原样返回，便于链式使用。</returns>
        /// <exception cref="ArgumentNullException">取值为 null。</exception>
        /// <exception cref="ArgumentException">包含分号、花括号或尖括号等被禁止的字符。</exception>
        public static string ValidateCssValue(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));

            foreach (char c in value)
            {
                if (c == ';' || c == '{' || c == '}' || c == '<' || c == '>')
                {
                    throw new ArgumentException(
                        $"CSS 取值包含非法字符 '{c}'，可能跳出声明块: {value}", nameof(value));
                }
            }

            return value;
        }

        /// <summary>枚举全部已嵌入的 JS 资源短名，供完整性测试与校验脚本使用。</summary>
        /// <returns>按名称排序的短名集合；未嵌入任何 JS 资源时返回空集合。</returns>
        public static IReadOnlyList<string> EnumerateJsResourceNames()
            => Enumerate(JsFullPrefix, JsExtension);

        /// <summary>枚举全部已嵌入的 CSS 资源短名。</summary>
        /// <returns>按名称排序的短名集合；未嵌入任何 CSS 资源时返回空集合。</returns>
        public static IReadOnlyList<string> EnumerateCssResourceNames()
            => Enumerate(CssFullPrefix, CssExtension);

        /// <summary>
        /// 拼出完整资源名。
        /// </summary>
        /// <param name="fullPrefix">资源文件夹对应的完整前缀。</param>
        /// <param name="name">资源短名。</param>
        /// <param name="extension">扩展名（含点）。</param>
        /// <returns>完整资源名。</returns>
        /// <exception cref="ArgumentException">短名为空。</exception>
        private static string BuildFullName(string fullPrefix, string name, string extension)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("WebAsset 短名不能为空", nameof(name));

            return fullPrefix + name + extension;
        }

        /// <summary>
        /// 读取并缓存资源原文。
        /// </summary>
        /// <param name="fullName">完整资源名。</param>
        /// <returns>资源文本。</returns>
        /// <exception cref="InvalidOperationException">资源不存在；消息中附带可用资源名以便定位拼写错误。</exception>
        private static string LoadRaw(string fullName)
        {
            if (RawCache.TryGetValue(fullName, out var cached))
                return cached;

            using (Stream? stream = AssetAssembly.GetManifestResourceStream(fullName))
            {
                if (stream == null)
                {
                    string available = string.Join(", ", AssetAssembly.GetManifestResourceNames()
                        .Where(n => n.IndexOf(".Resources.Chat.", StringComparison.Ordinal) >= 0)
                        .OrderBy(n => n, StringComparer.Ordinal));

                    throw new InvalidOperationException(
                        $"未找到 WebAsset 资源 '{fullName}'。当前可用资源: "
                        + (string.IsNullOrEmpty(available) ? "(无)" : available));
                }

                // 统一按 UTF-8 读取；保留 BOM 检测以便人工编辑时误存 BOM 也能正确解码。
                using (var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true))
                {
                    string text = reader.ReadToEnd();
                    RawCache[fullName] = text;
                    return text;
                }
            }
        }

        /// <summary>
        /// 执行占位符替换并做残留校验。
        /// </summary>
        /// <param name="raw">资源原文。</param>
        /// <param name="resourceName">完整资源名，用于异常消息定位。</param>
        /// <param name="placeholders">占位符映射。</param>
        /// <returns>替换后的文本。</returns>
        /// <exception cref="InvalidOperationException">缺少必填 key，或存在无法解析的占位符写法。</exception>
        internal static string ApplyPlaceholders(
            string raw, string resourceName, IReadOnlyDictionary<string, string> placeholders)
        {
            if (raw == null) throw new ArgumentNullException(nameof(raw));

            if (raw.IndexOf("{{", StringComparison.Ordinal) < 0)
                return raw;

            // 先收集全部被引用的 key 并与字典做差集，而不是替换后检查残留：
            // 替换值为空字符串时，残留检查会漏掉「key 缺失」这一真实错误。
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in QuotedPlaceholderRegex.Matches(raw))
                referenced.Add(match.Groups["key"].Value);
            foreach (Match match in PlaceholderRegex.Matches(raw))
                referenced.Add(match.Groups["key"].Value);

            List<string> missing = referenced
                .Where(key => !placeholders.ContainsKey(key))
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"WebAsset '{resourceName}' 缺少占位符取值: {string.Join(", ", missing)}");
            }

            // 顺序不可颠倒：先替换带引号的整段，否则裸形式会先把 {{key}} 吃掉，
            // 留下 "值" 再被处理一次，最终得到 ""值"" 这类错误字面量。
            // 使用 MatchEvaluator 而非替换字符串：替换值中的 "$" 不会被当作正则替换模式解析。
            string result = QuotedPlaceholderRegex.Replace(raw, match => placeholders[match.Groups["key"].Value]);
            result = PlaceholderRegex.Replace(result, match => placeholders[match.Groups["key"].Value]);

            int leftover = result.IndexOf("{{", StringComparison.Ordinal);
            if (leftover >= 0)
            {
                throw new InvalidOperationException(
                    $"WebAsset '{resourceName}' 存在无法解析的占位符（key 只允许字母、数字与下划线），"
                    + $"位置 {leftover}: {result.Substring(leftover, Math.Min(40, result.Length - leftover))}");
            }

            return result;
        }

        /// <summary>
        /// 枚举指定类别下全部资源短名。
        /// </summary>
        /// <param name="fullPrefix">类别前缀。</param>
        /// <param name="extension">扩展名（含点）。</param>
        /// <returns>按名称排序的短名集合。</returns>
        private static IReadOnlyList<string> Enumerate(string fullPrefix, string extension)
        {
            var names = new List<string>();

            foreach (string fullName in AssetAssembly.GetManifestResourceNames())
            {
                if (!fullName.StartsWith(fullPrefix, StringComparison.Ordinal))
                    continue;
                if (!fullName.EndsWith(extension, StringComparison.Ordinal))
                    continue;

                names.Add(fullName.Substring(
                    fullPrefix.Length, fullName.Length - fullPrefix.Length - extension.Length));
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }
    }
}
