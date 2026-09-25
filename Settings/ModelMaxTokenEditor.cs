using DeepSeek_v4_for_VisualStudio.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;
using System.Linq;
using System.Windows.Forms;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>
    /// “模型最大 Token”选项编辑器：列出官方与自定义端点模型，逐行输入上下文窗口。
    /// </summary>
    public class ModelMaxTokenEditor : UITypeEditor
    {
        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext? context)
            => UITypeEditorEditStyle.Modal;

        public override object? EditValue(
            ITypeDescriptorContext? context,
            IServiceProvider provider,
            object? value)
        {
            if (context?.Instance is not DeepSeekOptionsPage page)
                return value;

            var configured = ModelTokenLimitService.ParseLimits(page.ModelMaxTokenLimits);
            var models = OfficialModelCatalogService.GetModels()
                .Concat(page.GetCustomModels())
                .Concat(new[] { page.SelectedModel, page.ActiveCustomModel })
                .Concat(configured.Keys)
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => model.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                .ToList();

            using var dialog = new ModelMaxTokenDialog(models, configured);
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                page.ModelMaxTokenLimits = dialog.ConfigurationText;
                return page.ModelMaxTokenLimits;
            }

            return value;
        }
    }

    /// <summary>模型最大 Token 编辑对话框。</summary>
    internal sealed class ModelMaxTokenDialog : Form
    {
        private readonly DataGridView _grid;

        public string ConfigurationText { get; private set; } = string.Empty;

        public ModelMaxTokenDialog(
            IReadOnlyList<string> models,
            IReadOnlyDictionary<string, int> configured)
        {
            var l = LocalizationService.Instance;
            Text = l["settings.modelMaxTokenLimits.title"];
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = false;
            MinimizeBox = false;
            ClientSize = new Size(620, 440);
            MinimumSize = new Size(480, 320);
            Font = new Font("Segoe UI", 9f);

            var descriptionLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 44,
                Padding = new Padding(12, 10, 12, 6),
                Text = l["settings.modelMaxTokenLimits.dialogDescription"],
            };

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                EditMode = DataGridViewEditMode.EditOnEnter,
                MultiSelect = false,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "model",
                HeaderText = l["settings.modelMaxTokenLimits.modelColumn"],
                ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 62,
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "tokens",
                HeaderText = l["settings.modelMaxTokenLimits.contextColumn"],
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 38,
            });
            _grid.RowTemplate.Height = 34;
            _grid.ColumnHeadersHeight = 32;

            foreach (string model in models)
            {
                string text = configured.TryGetValue(model, out int maxTokens)
                    ? ModelTokenLimitService.FormatTokenCount(maxTokens)
                    : string.Empty;
                _grid.Rows.Add(model, text);
            }

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(12, 8, 12, 8),
            };
            var cancelButton = new Button
            {
                Text = l["settings.modelPicker.cancel"],
                DialogResult = DialogResult.Cancel,
                Size = new Size(84, 28),
            };
            var okButton = new Button
            {
                Text = l["settings.modelPicker.ok"],
                Size = new Size(84, 28),
            };
            okButton.Click += (_, _) =>
            {
                if (TryBuildConfiguration(out string configuration, out string error))
                {
                    ConfigurationText = configuration;
                    DialogResult = DialogResult.OK;
                    return;
                }

                MessageBox.Show(
                    this,
                    error,
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            };
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(okButton);
            AcceptButton = okButton;
            CancelButton = cancelButton;

            Controls.Add(_grid);
            Controls.Add(buttons);
            Controls.Add(descriptionLabel);
        }

        private bool TryBuildConfiguration(out string configuration, out string error)
        {
            var lines = new List<string>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                string model = row.Cells["model"].Value?.ToString()?.Trim() ?? string.Empty;
                string text = row.Cells["tokens"].Value?.ToString()?.Trim() ?? string.Empty;
                if (model.Length == 0 || text.Length == 0)
                    continue;

                if (!ModelTokenLimitService.TryParseTokenCount(text, out int maxTokens))
                {
                    configuration = string.Empty;
                    error = string.Format(
                        LocalizationService.Instance["settings.modelMaxTokenLimits.invalidValue"],
                        model);
                    return false;
                }

                lines.Add(model + "=" + maxTokens);
            }

            configuration = string.Join(Environment.NewLine, lines);
            error = string.Empty;
            return true;
        }
    }
}