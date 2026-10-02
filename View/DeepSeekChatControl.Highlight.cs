using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// 输入框 @ / token 蓝色高亮：内容重建、触发时机、内部滚动同步与滚动条宽度补偿。
    /// 采用「TextBox 之上叠加只读高亮层」的方式实现，不替换 TextBox 类型，
    /// 因此光标、IME、粘贴、补全弹窗等既有行为完全不受影响。
    /// </summary>
    public partial class DeepSeekChatControl
    {
        #region Highlight Fields

        /// <summary>输入框内部的滚动宿主，用于订阅滚动事件做高亮层偏移同步。</summary>
        private ScrollViewer _inputScrollViewer;

        /// <summary>上一次参与高亮计算的文本内容，用于避免 SelectionChanged/TextChanged 重复重算。</summary>
        private string _lastHighlightedText;

        /// <summary>上一次参与高亮计算的文本长度，作为轻量短路判据（配合内容比较）。</summary>
        private int _lastHighlightedTextLength = -1;

        /// <summary>高亮层当前是否已按「滚动条可见」补过右侧内边距，避免重复累加。</summary>
        private bool _highlightPaddingCompensated;

        /// <summary>是否已完成一次性初始化（订阅 SelectionChanged、挂接内部 ScrollViewer）。</summary>
        private bool _inputHighlightInitialized;

        #endregion

        #region Highlight Core

        /// <summary>
        /// 重算输入框高亮层内容：按 <see cref="MentionTokenizer.FindTokens"/> 切分文本，
        /// 为每个命中 token 生成带覆盖背景刷的蓝色 Run，非命中部分不渲染（透出底层字形）。
        /// 文本为空或没有命中 token 时清空 Inlines 并折叠高亮层。
        /// </summary>
        private void UpdateInputHighlight()
        {
            if (InputHighlightLayer == null || InputTextBox == null)
                return;

            EnsureInputHighlightInitialized();

            var text = InputTextBox.Text ?? string.Empty;

            // 内容未变则直接返回：SelectionChanged 触发频繁，靠长度 + 内容双重比较短路
            if (_lastHighlightedText != null
                && _lastHighlightedTextLength == text.Length
                && string.Equals(_lastHighlightedText, text, StringComparison.Ordinal))
            {
                return;
            }

            _lastHighlightedText = text;
            _lastHighlightedTextLength = text.Length;

            InputHighlightLayer.Inlines.Clear();

            var tokens = string.IsNullOrEmpty(text)
                ? null
                : MentionTokenizer.FindTokens(text);

            if (tokens == null || tokens.Count == 0)
            {
                // 没有可着色的 token 时折叠，避免无意义的叠加绘制开销
                InputHighlightLayer.Visibility = Visibility.Collapsed;
                return;
            }

            var coverBrush = GetHighlightCoverBrush();

            foreach (var token in tokens)
            {
                // 覆盖刷铺在 Run 背景上，遮住底层 TextBox 的灰色字形，避免蓝色与灰色重影
                var run = new Run(text.Substring(token.Start, token.Length))
                {
                    Foreground = GetMentionForeground(token.Kind),
                    Background = coverBrush,
                };
                InputHighlightLayer.Inlines.Add(run);
            }

            InputHighlightLayer.Visibility = Visibility.Visible;

            // 滚动条显隐会改变内容宽度，需在显示后重新对齐，再同步偏移
            EnsureHighlightPaddingCompensation();
            SyncHighlightScrollOffset();
        }

        /// <summary>
        /// 根据 token 类型返回高亮前景色（深/浅主题自适应）。
        /// </summary>
        /// <param name="kind">token 类型（Agent 或 Skill）。</param>
        /// <returns>用于 Run.Foreground 的画刷，两种类型均为蓝色系。</returns>
        private Brush GetMentionForeground(MentionTokenKind kind)
        {
            var isLight = IsLightTheme();

            // Agent 与 Skill 都用蓝色渲染，仅在亮度上略作区分：
            // Agent 稍亮、Skill 稍沉，便于一眼分辨路由前缀与技能命令
            if (kind == MentionTokenKind.Agent)
            {
                return new SolidColorBrush(isLight
                    ? Color.FromRgb(0x00, 0x5A, 0x9E)
                    : Color.FromRgb(0x82, 0xC7, 0xF0));
            }

            return new SolidColorBrush(isLight
                ? Color.FromRgb(0x00, 0x78, 0xD4)
                : Color.FromRgb(0x6C, 0xAF, 0xD9));
        }

        /// <summary>
        /// 返回用于覆盖 TextBox 灰色字形的高亮背景刷（与输入区实际底色一致）。
        /// </summary>
        /// <returns>用于 Run.Background 的不透明画刷。</returns>
        private Brush GetHighlightCoverBrush()
        {
            // 必须与输入区真实底色一致：否则蓝色字形背后会出现突兀的色块
            var background = InputAreaBorder?.Background;
            if (background != null)
                return background;

            // 主题尚未应用时的兜底底色，与 XAML 中 InputAreaBorder 的默认值保持一致
            return new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
        }

        /// <summary>
        /// 判断当前是否为浅色主题，用于高亮配色自适应。
        /// </summary>
        /// <returns>输入框前景色偏暗（浅色主题）时返回 true。</returns>
        private bool IsLightTheme()
        {
            return InputTextBox?.Foreground is SolidColorBrush brush && brush.Color.R < 0x80;
        }

        #endregion

        #region Highlight Triggers

        /// <summary>
        /// 文本选区/光标变化：转发到高亮重算，内容未变时由内部短路，成本可忽略。
        /// </summary>
        /// <param name="sender">事件源（输入框）。</param>
        /// <param name="e">路由事件参数。</param>
        private void InputTextBox_SelectionChanged(object sender, RoutedEventArgs e)
        {
            UpdateInputHighlight();
        }

        /// <summary>
        /// 输入框内部滚动：把 ScrollViewer 的垂直/水平偏移取反同步到高亮层，
        /// 保证多行内容滚动时蓝色文字跟随。
        /// </summary>
        /// <param name="sender">事件源（输入框模板内的 ScrollViewer）。</param>
        /// <param name="e">滚动变更事件参数。</param>
        private void InputScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            SyncHighlightScrollOffset();
        }

        /// <summary>
        /// 主题切换后刷新高亮层配色：清空去重缓存强制重算，让蓝色与覆盖底色随主题更新。
        /// </summary>
        private void RefreshInputHighlightTheme()
        {
            _lastHighlightedText = null;
            _lastHighlightedTextLength = -1;
            UpdateInputHighlight();
        }

        #endregion

        #region Highlight Initialization & Sync

        /// <summary>
        /// 一次性初始化：订阅输入框 SelectionChanged 并挂接模板内的 ScrollViewer。
        /// 反复调用安全（幂等）。
        /// </summary>
        private void EnsureInputHighlightInitialized()
        {
            if (_inputHighlightInitialized || InputTextBox == null)
                return;

            _inputHighlightInitialized = true;

            InputTextBox.SelectionChanged += InputTextBox_SelectionChanged;
            AttachInputScrollViewer();
        }

        /// <summary>
        /// 定位 InputTextBox 模板内承载文本的 ScrollViewer（PART_ContentHost），
        /// 订阅其 ScrollChanged 并做首次偏移同步。
        /// </summary>
        private void AttachInputScrollViewer()
        {
            if (_inputScrollViewer != null || InputTextBox == null)
                return;

            // 文本宿主位于控件模板内部，需先展开模板才能按名取到
            InputTextBox.ApplyTemplate();
            _inputScrollViewer = InputTextBox.Template?.FindName("PART_ContentHost", InputTextBox) as ScrollViewer;

            // 模板尚未生成时不缓存失败结果，留待下次调用重试
            if (_inputScrollViewer == null)
                return;

            _inputScrollViewer.ScrollChanged += InputScrollViewer_ScrollChanged;
            SyncHighlightScrollOffset();
        }

        /// <summary>
        /// 把内部 ScrollViewer 的偏移量取反写回高亮层的 TranslateTransform。
        /// 高亮层与 TextBox 文本不在同一滚动宿主内，只能靠外部偏移实现视觉跟随。
        /// </summary>
        private void SyncHighlightScrollOffset()
        {
            if (InputHighlightOffset == null)
                return;

            InputHighlightOffset.X = -(_inputScrollViewer?.HorizontalOffset ?? 0);
            InputHighlightOffset.Y = -(_inputScrollViewer?.VerticalOffset ?? 0);
        }

        /// <summary>
        /// 依据 ScrollViewer.ComputedVerticalScrollBarVisibility 动态给高亮层补右侧内边距，
        /// 抵消滚动条出现后 TextBox 内容区变窄导致的换行点不一致。
        /// </summary>
        private void EnsureHighlightPaddingCompensation()
        {
            if (InputHighlightLayer == null || InputTextBox == null || _inputScrollViewer == null)
                return;

            var needCompensation = _inputScrollViewer.ComputedVerticalScrollBarVisibility == Visibility.Visible;
            if (needCompensation == _highlightPaddingCompensated)
                return;

            // 滚动条只压缩内容宽度，因此仅在右侧追加滚动条宽度，其余边距与 TextBox 保持逐项一致
            var basePadding = InputTextBox.Padding;
            InputHighlightLayer.Padding = needCompensation
                ? new Thickness(
                    basePadding.Left,
                    basePadding.Top,
                    basePadding.Right + SystemParameters.VerticalScrollBarWidth,
                    basePadding.Bottom)
                : basePadding;

            _highlightPaddingCompensated = needCompensation;
        }

        #endregion
    }
}
