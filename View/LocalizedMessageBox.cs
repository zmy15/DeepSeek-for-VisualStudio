using DeepSeek_v4_for_VisualStudio.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// 使用扩展本地化资源显示确认框，避免系统 MessageBox 按钮语言与应用语言不一致。
    /// </summary>
    internal static class LocalizedMessageBox
    {
        public static bool Confirm(Window? owner, string title, string message)
        {
            var dialog = new Window
            {
                Title = title,
                Owner = owner,
                Width = 480,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = owner == null
                    ? WindowStartupLocation.CenterScreen
                    : WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = SystemColors.WindowBrush,
                FontFamily = SystemFonts.MessageFontFamily,
                FontSize = SystemFonts.MessageFontSize,
            };

            var root = new Grid
            {
                Margin = new Thickness(24, 20, 24, 20),
            };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var icon = new TextBlock
            {
                Text = "\uE7BA",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 32,
                Foreground = new SolidColorBrush(Color.FromRgb(212, 160, 0)),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 18, 0),
            };
            Grid.SetColumn(icon, 0);
            Grid.SetRow(icon, 0);
            root.Children.Add(icon);

            var messageText = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = SystemColors.WindowTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(messageText, 1);
            Grid.SetRow(messageText, 0);
            root.Children.Add(messageText);

            var okButton = new Button
            {
                Content = LocalizationService.Instance["general.ok"],
                MinWidth = 84,
                Height = 28,
                Margin = new Thickness(0, 0, 10, 0),
                IsDefault = false,
            };
            var cancelButton = new Button
            {
                Content = LocalizationService.Instance["general.cancel"],
                MinWidth = 84,
                Height = 28,
                IsCancel = true,
                IsDefault = true,
            };

            okButton.Click += (_, _) => dialog.DialogResult = true;
            cancelButton.Click += (_, _) => dialog.DialogResult = false;

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0),
            };
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);

            Grid.SetColumn(buttonPanel, 0);
            Grid.SetColumnSpan(buttonPanel, 2);
            Grid.SetRow(buttonPanel, 1);
            root.Children.Add(buttonPanel);

            dialog.Content = root;
            dialog.Loaded += (_, _) => cancelButton.Focus();

            return dialog.ShowDialog() == true;
        }
    }
}
