using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.View.InlineEdit
{
    /// <summary>
    /// Inline Edit 指令条（P1-B）。
    ///
    /// 轻量无边框浮窗，锚定在选区起始行附近：
    /// - Ready 态：输入指令，Enter 提交（不关窗，由命令方决定后续），Esc 关闭
    /// - Busy  态：显示生成中状态，Esc 触发 CancelRequested（取消 LLM 调用）
    /// - Error 态：红色错误提示并回到 Ready，支持原地重试
    ///
    /// 窗口失焦时自动关闭（Ready 态），与 Copilot 行为一致。
    /// </summary>
    internal sealed class InlineEditBarWindow : Window
    {
        private const double BarWidth = 600;
        private const double EstimatedHeight = 64;

        private static readonly Brush BgBrush = Hex("#252526");
        private static readonly Brush InputBgBrush = Hex("#1E1E1E");
        private static readonly Brush InputFgBrush = Hex("#D4D4D4");
        private static readonly Brush BorderAccentBrush = Hex("#007ACC");
        private static readonly Brush HintBrush = Hex("#9A9A9A");
        private static readonly Brush ErrorBrush = Hex("#F48771");

        private readonly Point _anchorScreen;          // 物理像素坐标
        private readonly TextBox _input;
        private readonly TextBlock _hintText;
        private readonly TextBlock _statusText;
        private readonly Border _statusBorder;

        private bool _busy;
        private TaskCompletionSource<string?> _submittedTcs = NewTcs();
        private TaskCompletionSource<bool> _closedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Enter 提交（窗口保持打开；命令方通过 ResetSubmit 复位以支持重试）。</summary>
        public Task<string?> WaitForSubmitAsync() => _submittedTcs.Task;

        public Task WaitForCloseAsync() => _closedTcs.Task;

        /// <summary>Esc 取消生成（仅 Busy 态触发）。</summary>
        public event Action? CancelRequested;

        public InlineEditBarWindow(Point anchorScreenPhysical)
        {
            _anchorScreen = anchorScreenPhysical;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = true;
            Topmost = true;
            Width = BarWidth;
            SizeToContent = SizeToContent.Height;

            var hint = LocalizationService.Instance;
            _input = new TextBox
            {
                FontSize = 13,
                Padding = new Thickness(6, 5, 6, 5),
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = InputBgBrush,
                Foreground = InputFgBrush,
                BorderBrush = Hex("#3F3F46"),
                CaretBrush = InputFgBrush,
                Margin = new Thickness(0),
            };

            _hintText = new TextBlock
            {
                Text = hint["inlineEdit.hint"],
                Foreground = HintBrush,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };

            var inputRow = new Grid();
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_input, 0);
            Grid.SetColumn(_hintText, 1);
            inputRow.Children.Add(_input);
            inputRow.Children.Add(_hintText);

            // 占位符需在 TextBox 挂入布局后附加（宿主 Grid 叠加在其上层）
            AttachPlaceholder(_input, hint["inlineEdit.placeholder"]);

            _statusText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            _statusBorder = new Border
            {
                Child = _statusText,
                Padding = new Thickness(2, 6, 2, 0),
                Visibility = Visibility.Collapsed,
            };

            var stack = new StackPanel();
            stack.Children.Add(inputRow);
            stack.Children.Add(_statusBorder);

            var root = new Border
            {
                Child = stack,
                CornerRadius = new CornerRadius(8),
                Background = BgBrush,
                BorderBrush = BorderAccentBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 2,
                    Opacity = 0.45,
                },
            };

            Content = root;

            PreviewKeyDown += OnBarPreviewKeyDown;
            Deactivated += (_, _) => { if (!_busy) Close(); };
            Closed += (_, _) => _closedTcs.TrySetResult(true);
            ContentRendered += (_, _) => Reposition();
        }

        // ────────────────────────── 公开 API ──────────────────────────

        public void ShowBar()
        {
            Reposition();
            Show();
            Activate();
            FocusInput();
        }

        public void SetBusy(string message)
        {
            _busy = true;
            _input.IsEnabled = false;
            _hintText.Text = LocalizationService.Instance["inlineEdit.escToCancel"];
            ShowStatus(message, "#9CDCFE");
        }

        public void SetError(string message)
        {
            _busy = false;
            _input.IsEnabled = true;
            _hintText.Text = LocalizationService.Instance["inlineEdit.hint"];
            ShowStatus(message, null);   // null → ErrorBrush
            FocusInput(selectAll: true);
        }

        public void CloseGracefully() => Close();

        /// <summary>提交后复位 TCS，允许同一窗口再次提交（原地重试）。</summary>
        public void ResetSubmit()
        {
            if (_submittedTcs.Task.IsCompleted)
                _submittedTcs = NewTcs();
        }

        // ────────────────────────── 内部实现 ──────────────────────────

        private void OnBarPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                if (_busy) return;
                string text = _input.Text?.Trim() ?? string.Empty;
                if (text.Length == 0) return;
                _submittedTcs.TrySetResult(text);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (_busy) CancelRequested?.Invoke();
                else Close();
            }
        }

        private void Reposition()
        {
            var workArea = SystemParameters.WorkArea;

            // 物理像素 → WPF 逻辑单位（DPI 缩放换算）
            double dpiX = 1, dpiY = 1;
            try
            {
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget != null)
                {
                    dpiX = source.CompositionTarget.TransformToDevice.M11;
                    dpiY = source.CompositionTarget.TransformToDevice.M22;
                }
            }
            catch { /* 默认 100% 缩放 */ }

            double logicalX = dpiX > 0 ? _anchorScreen.X / dpiX : _anchorScreen.X;
            double logicalY = dpiY > 0 ? _anchorScreen.Y / dpiY : _anchorScreen.Y;

            double height = ActualHeight > 0 ? ActualHeight : EstimatedHeight;

            Left = Math.Min(Math.Max(logicalX - 40, workArea.Left), workArea.Right - Width - 4);
            Top = logicalY + 22;
            if (Top + height > workArea.Bottom)
                Top = Math.Max(workArea.Top, logicalY - height - 28);
        }

        private void FocusInput(bool selectAll = false)
        {
            Activate();
            _input.Focus();
            Keyboard.Focus(_input);
            if (selectAll && !string.IsNullOrEmpty(_input.Text))
                _input.SelectAll();
            else
                _input.CaretIndex = _input.Text?.Length ?? 0;
        }

        private void ShowStatus(string message, string? hexColor)
        {
            _statusText.Text = message;
            _statusText.Foreground = hexColor != null ? Hex(hexColor) : ErrorBrush;
            _statusBorder.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// net472/WPF 无原生 placeholder：在 TextBox 所在 Grid 单元格内叠加提示文本层。
        /// 必须在 TextBox 已挂入父 Panel 后调用。
        /// </summary>
        private static void AttachPlaceholder(TextBox box, string placeholder)
        {
            var holder = new TextBlock
            {
                Text = placeholder,
                Foreground = Hex("#6B6B6B"),
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };

            var host = new Grid { Background = Brushes.Transparent };
            host.Children.Add(box);
            host.Children.Add(holder);

            void UpdateVisibility()
            {
                holder.Visibility = string.IsNullOrEmpty(box.Text) && !box.IsKeyboardFocusWithin
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            box.TextChanged += (_, _) => UpdateVisibility();
            box.GotKeyboardFocus += (_, _) => UpdateVisibility();
            box.LostKeyboardFocus += (_, _) => UpdateVisibility();

            if (box.Parent is Panel parent)
            {
                int idx = parent.Children.IndexOf(box);
                parent.Children.RemoveAt(idx);
                if (parent is Grid g)
                    Grid.SetColumn(host, Grid.GetColumn(box));
                parent.Children.Insert(idx, host);
            }
        }

        private static SolidColorBrush Hex(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private static TaskCompletionSource<string?> NewTcs()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
