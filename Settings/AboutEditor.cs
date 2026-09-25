using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Design;
using DeepSeek_v4_for_VisualStudio.Services;
using System.Windows.Forms;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    public class AboutEditor : UITypeEditor
    {
        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext? context)
            => UITypeEditorEditStyle.Modal;

        public override object? EditValue(
            ITypeDescriptorContext? context,
            IServiceProvider provider,
            object? value)
        {
            using var dialog = new AboutDialog();
            dialog.ShowDialog();
            return value;
        }
    }

    internal sealed class AboutDialog : Form
    {
        public AboutDialog()
        {
            var l = LocalizationService.Instance;
            Text = l["settings.about.title"];
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 285);
            Font = new Font("Segoe UI", 9f);

            var titleLabel = new Label
            {
                Text = "DeepSeek for Visual Studio",
                Font = new Font(Font.FontFamily, 14f, FontStyle.Bold),
                Location = new Point(18, 16),
                AutoSize = true,
            };
            var versionLabel = new Label
            {
                Text = string.Format(l["settings.about.version"], AboutInfo.Version),
                ForeColor = SystemColors.GrayText,
                Location = new Point(20, 50),
                AutoSize = true,
            };
            var repositoryLabel = new Label
            {
                Text = l["settings.about.repositoryLabel"],
                Location = new Point(20, 86),
                AutoSize = true,
            };
            var repositoryLink = CreateLink(AboutInfo.RepositoryUrl, new Point(112, 84));
            var issuesLabel = new Label
            {
                Text = l["settings.about.issuesLabel"],
                Location = new Point(20, 116),
                AutoSize = true,
            };
            var issuesLink = CreateLink(AboutInfo.IssuesUrl, new Point(112, 114));
            var starLabel = new Label
            {
                Text = l["settings.about.starMessage"],
                Location = new Point(20, 156),
                Size = new Size(520, 42),
            };

            var starButton = new Button
            {
                Text = l["settings.about.star"],
                Location = new Point(324, 224),
                Size = new Size(100, 30),
            };
            starButton.Click += (_, _) => OpenLink(AboutInfo.StarUrl);
            var closeButton = new Button
            {
                Text = l["settings.about.close"],
                DialogResult = DialogResult.OK,
                Location = new Point(434, 224),
                Size = new Size(100, 30),
            };
            AcceptButton = closeButton;
            CancelButton = closeButton;

            Controls.AddRange(new Control[]
            {
                titleLabel,
                versionLabel,
                repositoryLabel,
                repositoryLink,
                issuesLabel,
                issuesLink,
                starLabel,
                starButton,
                closeButton,
            });
        }

        private LinkLabel CreateLink(string url, Point location)
        {
            var link = new LinkLabel
            {
                Text = url,
                Location = location,
                AutoSize = true,
            };
            link.LinkClicked += (_, _) => OpenLink(url);
            return link;
        }

        private static void OpenLink(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format(LocalizationService.Instance["settings.about.linkError"], ex.Message),
                    LocalizationService.Instance["settings.about.title"],
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
    }
}