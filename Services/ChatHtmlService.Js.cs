using DeepSeek_v4_for_VisualStudio.Services.WebAssets;
using System;
using System.Collections.Generic;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// JavaScript 生成方法 —— ChatHtmlService 的分部类。
    /// </summary>
    public static partial class ChatHtmlService
    {
        #region JavaScript Builders

        /// <summary>
        /// Exposes localized labels for the diagnostic Context panel without leaking
        /// source-code literals into the WebView DOM.
        /// 脚本骨架已外移至 <c>Resources\Chat\Js\debug-labels.js</c>：标签集合整体序列化为 JSON
        /// 再由页面 JSON.parse，既让资源文件本身是合法 JS，也免去在 C# 里拼接 18 段字符串。
        /// </summary>
        private static string BuildContextDebugLabelsJs()
        {
            var labels = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = L["chat.html.context.title"],
                ["tokens"] = L["chat.html.context.tokens"],
                ["injected"] = L["chat.html.context.injected"],
                ["characters"] = L["chat.html.context.characters"],
                ["file"] = L["chat.html.context.file"],
                ["selection"] = L["chat.html.context.selection"],
                ["cursor"] = L["chat.html.context.cursor"],
                ["symbol"] = L["chat.html.context.symbol"],
                ["diagnostics"] = L["chat.html.context.diagnostics"],
                ["errors"] = L["chat.html.context.errors"],
                ["warnings"] = L["chat.html.context.warnings"],
                ["off"] = L["chat.html.context.off"],
                ["turns"] = L["chat.html.context.turns"],
                ["messages"] = L["chat.html.context.messages"],
                ["toolCalls"] = L["chat.html.context.toolCalls"],
                ["forwarded"] = L["chat.html.context.forwarded"],
                ["requestEstimate"] = L["chat.html.context.requestEstimate"],
                ["tokensPerSecond"] = L["chat.html.context.tokensPerSecond"],
            };

            string json = System.Text.Json.JsonSerializer.Serialize(labels, JsEscapeOptions);

            // 资源里的占位符是带引号形式 "{{labelsJson}}"，loader 会连同两侧引号整段替换，
            // 因此这里必须传入含外层引号的完整 JS 字符串字面量；
            // 若剥掉引号，替换结果会变成对象字面量，JSON.parse 收到对象而非字符串。
            string escaped = EscapeJsString(json);
            var map = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["labelsJson"] = escaped,
            };

            return WebAssetLoader.GetJs("debug-labels", map);
        }

        /// <summary>
        /// 声明 decorateCodeBlocks 函数（语言标签 + highlight.js 语法高亮 + 复制/应用按钮）。
        /// </summary>
        /// <summary>
        /// 渲染层 Emoji→Fluent 字形替换（P1 收口）：数据层保持原样，仅 DOM 文本节点做视觉替换。
        /// 命中映射表的转为 Fluent 图标；未映射的装饰性 emoji 移除。
        /// 用户消息气泡跳过清理，用户输入的 emoji 必须原样显示。
        /// MutationObserver 覆盖一切动态插入（工具行/任务面板/页脚等）。
        /// </summary>
        private static string BuildDetoxEmojisJs()
            => WebAssetLoader.GetJs("detox-emojis");

        private static string BuildDecorateCodeBlocksJsFunction()
        {
            // 复制按钮文案走占位符注入：文件本身是合法 JS，替换时把 "{{key}}" 整段
            // 换成 EscapeJsString 产出的含引号字面量，避免出现 ""文案"" 这类错误。
            var map = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["copyPrefix"] = EscapeJsString(L["chat.html.copyCodeBlock"]),
                ["copiedPrefix"] = EscapeJsString(L["chat.html.copied"]),
            };

            return WebAssetLoader.GetJs("decorate-code-blocks", map);
        }

        /// <summary>
        /// Shift+滚轮横向滚动代码块。正文已外移至 <c>Resources\Chat\Js\shift-scroll.js</c>，
        /// 此处保留原签名以兼容既有调用点。
        /// </summary>
        private static string BuildShiftScrollJs()
            => WebAssetLoader.GetJs("shift-scroll");

        /// <summary>
        /// 阻止 WebView2 页面处理 F5/F7，并把对应快捷键转发给 Visual Studio 宿主。
        /// F5：开始/继续调试；F7：查看代码；Shift+F7：查看设计器。
        /// </summary>
        private static string BuildVsShortcutForwardingJs()
            => WebAssetLoader.GetJs("vs-shortcut-forwarding");

        /// <summary>
        /// KaTeX 数学公式渲染函数。
        /// Markdig 的 UseMathematics() 将 $...$ / $$...$$ 转换为
        /// &lt;span class="math"&gt;\(...\)&lt;/span&gt; 和 &lt;div class="math"&gt;\[...\]&lt;/div&gt;。
        /// 本函数剥离 Markdig 附加的 \(/\) 和 \[/\] 分隔符后，调用 katex.render 渲染。
        /// </summary>
        private static string BuildRenderMathJsFunction()
            => WebAssetLoader.GetJs("render-math");

        /// <summary>
        /// Mermaid 图表渲染函数。
        /// 查找 <code class="language-mermaid"> 代码块，使用 Mermaid 渲染为 SVG 图表。
        /// 渲染后标记 .mermaid-block 防止 highlight.js 二次处理。
        /// </summary>
        private static string BuildRenderMermaidJsFunction()
        {
            // 图表点击提示文案走占位符注入，脚本正文已外移至 render-mermaid.js。
            var map = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["diagramClickTip"] = EscapeJsString(L["chat.html.diagramClickTip"]),
            };

            return WebAssetLoader.GetJs("render-mermaid", map);
        }

        /// <summary>
        /// 智能自动滚动 JS.
        /// 用户滚动离开底部 → 暂停自动滚动；
        /// 用户滚回底部附近 → 恢复自动滚动。
        /// 检测阈值: 距底部 80px 以内视为"在底部"。
        /// </summary>
        private static string BuildAutoScrollJs()
            => WebAssetLoader.GetJs("auto-scroll");

        /// <summary>
        /// 声明 window.__appendMessageHtml 函数，用于增量追加新消息到页面。
        /// </summary>
        private static string BuildAppendMessageJsFunction()
            => WebAssetLoader.GetJs("append-message");

        /// <summary>
        /// 声明重试/编辑/版本导航的 JS 函数。
        /// 通过 window.chrome.webview.postMessage 与 C# 通信。
        /// </summary>
        private static string BuildRetryEditJsFunctions()
        {
            // 脚本正文（含 6 个本地化前缀）已外移至 Resources\Chat\Js\retry-edit.js：
            // 前缀改为 "{{key}}" 占位符，取值仍由 EscapeJsString 产出含引号的字面量后注入，
            // 因此 loader 替换结果与原内联拼接逐字一致。
            var map = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["markdownFailText"] = EscapeJsString(L["chat.html.markdownRenderFailed"]),
                ["retry"] = EscapeJsString(L["chat.html.retryButton"]),
                ["retryTitle"] = EscapeJsString(L["chat.html.retryButtonTitle"]),
                ["copy"] = EscapeJsString(L["chat.html.copyButton"]),
                ["copyTitle"] = EscapeJsString(L["chat.html.copyButtonTitle"]),
                ["copied"] = EscapeJsString(L["chat.html.copySuccessButton"]),
            };

            return WebAssetLoader.GetJs("retry-edit", map);
        }

        private static System.Text.Json.JsonSerializerOptions? _jsEscapeOptions;
        private static readonly object JsEscapeOptionsLock = new();

        /// <summary>
        /// 转义字符串用于嵌入 JS 字符串字面量。
        /// 使用 UnsafeRelaxedJsonEscaping 保留中文等非 ASCII 字符，避免输出 \uXXXX 编码。
        /// </summary>
        private static System.Text.Json.JsonSerializerOptions? JsEscapeOptions
        {
            get
            {
                lock (JsEscapeOptionsLock)
                {
                    if (_jsEscapeOptions != null)
                        return _jsEscapeOptions;

                    try
                    {
                        _jsEscapeOptions = new System.Text.Json.JsonSerializerOptions
                        {
                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                        };
                    }
                    catch (Exception ex)
                    {
                        // System.Text.Json 依赖链异常时，后续 EscapeJsString 会走纯托管降级路径。
                        System.Diagnostics.Debug.WriteLine($"[ChatHtml] JS escape options init failed: {ex.GetType().Name}: {ex.Message}");
                    }

                    return _jsEscapeOptions;
                }
            }
        }

        private static string EscapeJsString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";

            try
            {
                if (TrySerializeJsString(s, out var json))
                    return json;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChatHtml] JSON JS escape failed: {ex.GetType().Name}: {ex.Message}");
            }

            return EscapeJsStringFallback(s);
        }

        private static bool TrySerializeJsString(string s, out string json)
        {
            json = string.Empty;
            var options = JsEscapeOptions;
            if (options == null)
                return false;

            json = System.Text.Json.JsonSerializer.Serialize(s, options);
            return true;
        }

        private static string EscapeJsStringFallback(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length + 2).Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (char.IsControl(c))
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }

            return sb.Append('"').ToString();
        }

        #endregion
    }
}
