using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>
    /// "自定义模型名称"的取模型列表编辑器（参考 CC Switch 的供应商模型抓取）。
    /// 属性网格中显示 "..." 按钮，弹出对话框：端点 + 密钥 → GET /models → 勾选一个或多个模型。
    /// </summary>
    public class ModelPickerEditor : UITypeEditor
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

            var targetEndpoint = page.FindCustomEndpointForModel(
                page.ActiveCustomEndpointId,
                page.ActiveCustomModel);
            using var dialog = new ModelPickerDialog(
                targetEndpoint?.BaseUrl ?? page.ApiBaseUrl,
                targetEndpoint?.ApiKey ?? ApiKeyProtection.Unprotect(page.CustomApiKey),
                readOnlyEndpoint: true);
            if (dialog.ShowDialog() == DialogResult.OK &&
                dialog.SelectedModels.Count > 0)
            {
                ApplyModelsToEndpoint(page, targetEndpoint?.Id, dialog.SelectedModels);
                return page.CustomModelName;
            }

            return value;
        }

        internal static void ApplyModelsToEndpoint(
            DeepSeekOptionsPage page,
            string? targetEndpointId,
            IReadOnlyList<string> selectedModels)
        {
            var endpoints = page.GetCustomEndpoints()
                .Select(endpoint => endpoint.Clone())
                .ToList();
            var endpoint = endpoints.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, targetEndpointId, StringComparison.Ordinal))
                ?? (endpoints.Count == 1 ? endpoints[0] : null);
            if (endpoint == null &&
                endpoints.Count == 0 &&
                !string.IsNullOrWhiteSpace(page.ApiBaseUrl))
            {
                endpoint = new CustomEndpointConfig(
                    "legacy-custom-endpoint",
                    DeepSeekOptionsPage.InferCustomEndpointName(page.ApiBaseUrl),
                    page.ApiBaseUrl,
                    ApiKeyProtection.Unprotect(page.CustomApiKey));
                endpoints.Add(endpoint);
            }

            if (endpoint != null)
            {
                endpoint.Models = endpoint.Models
                    .Union(selectedModels, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                page.SetCustomEndpoints(endpoints);
                page.ActiveCustomEndpointId = endpoint.Id;
                page.ActiveCustomModel = selectedModels.Last();
                page.ActiveModelSource = "custom";
            }
            else
            {
                var models = page.GetCustomModels()
                    .Union(selectedModels, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                page.SetCustomModels(models);
                page.ActiveCustomModel = selectedModels.Last();
                page.ActiveModelSource = "custom";
            }
        }
    }

    /// <summary>
    /// 手动勾选视觉模型：同时列出官方接口模型和自定义端点模型。
    /// </summary>
    public class VisionModelPickerEditor : UITypeEditor
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

            using var dialog = new VisionModelPickerDialog(
                page.GetVisionModels(),
                page.GetCustomModels());
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                page.SetVisionModels(dialog.SelectedVisionModels);
                return page.CustomVisionModels;
            }

            return value;
        }
    }

    /// <summary>视觉模型勾选对话框：官方与自定义模型统一展示。</summary>
    internal sealed class VisionModelPickerDialog : Form
    {
        private sealed record VisionModelItem(string Model, string Display, bool IsOfficial, bool FromMetadata);

        private readonly CheckedListBox _modelList;

        /// <summary>用户勾选的自定义端点模型；官方模型由接口元数据判定，不参与落盘。</summary>
        public IReadOnlyList<string> SelectedVisionModels
            => _modelList.CheckedItems.Cast<VisionModelItem>()
                .Where(item => !item.FromMetadata)
                .Select(item => item.Model)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        public VisionModelPickerDialog(IReadOnlyList<string> visionModels, IReadOnlyList<string> customModels)
        {
            var l = LocalizationService.Instance;
            Text = l["settings.visionModels.title"];
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(460, 392);
            Font = new Font("Segoe UI", 9f);

            var descriptionLabel = new Label
            {
                Text = l["settings.visionModels.description"],
                Location = new Point(12, 12),
                AutoSize = true,
            };
            _modelList = new CheckedListBox
            {
                Location = new Point(12, 34),
                Size = new Size(436, 296),
                CheckOnClick = true,
                DisplayMember = nameof(VisionModelItem.Display),
            };

            var officialMetadata = OfficialModelCatalogService.GetMetadata();
            var officialModels = officialMetadata.Select(model => model.Id).ToList();
            var sources = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in officialModels)
                sources.Add(model, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "official" });
            foreach (var model in customModels)
            {
                if (!sources.TryGetValue(model, out var modelSources))
                {
                    modelSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "custom" };
                    sources[model] = modelSources;
                }
                modelSources.Add("custom");
            }

            var metadataById = officialMetadata.ToDictionary(
                model => model.Id, StringComparer.OrdinalIgnoreCase);

            foreach (var pair in sources)
            {
                var sourceText = pair.Value.Contains("official") && pair.Value.Contains("custom")
                    ? l["settings.visionModels.source.both"]
                    : pair.Value.Contains("official")
                        ? l["settings.visionModels.source.official"]
                        : l["settings.visionModels.source.custom"];

                bool fromMetadata = metadataById.TryGetValue(pair.Key, out var metadata) &&
                    metadata.HasModalityInfo;
                string display = string.IsNullOrWhiteSpace(sourceText)
                    ? pair.Key
                    : $"{pair.Key} ({sourceText})";
                // 接口已声明模态的官方模型：标注能力并锁定勾选状态，用户无需也不能改动。
                if (fromMetadata)
                {
                    display += metadata.SupportsImageInput
                        ? " · " + l["settings.visionModels.autoImage"]
                        : " · " + l["settings.visionModels.autoTextOnly"];
                }

                _modelList.Items.Add(
                    new VisionModelItem(
                        pair.Key, display, pair.Value.Contains("official"), fromMetadata),
                    false);
            }

            var selected = new HashSet<string>(visionModels, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _modelList.Items.Count; i++)
            {
                if (_modelList.Items[i] is not VisionModelItem item)
                    continue;

                // 官方模型由 input_modalities 权威判定；自定义模型沿用用户勾选。
                bool isChecked = item.FromMetadata
                    ? metadataById[item.Model].SupportsImageInput
                    : selected.Contains(item.Model);
                if (isChecked)
                    _modelList.SetItemChecked(i, true);
            }

            // 元数据行不可改动：勾选动作直接被还原。
            _modelList.ItemCheck += (_, e) =>
            {
                if (_modelList.Items[e.Index] is VisionModelItem item &&
                    item.FromMetadata &&
                    e.NewValue != (metadataById[item.Model].SupportsImageInput
                        ? CheckState.Checked
                        : CheckState.Unchecked))
                {
                    e.NewValue = metadataById[item.Model].SupportsImageInput
                        ? CheckState.Checked
                        : CheckState.Unchecked;
                }
            };

            var okButton = new Button
            {
                Text = l["settings.modelPicker.ok"],
                DialogResult = DialogResult.OK,
                Location = new Point(273, 344),
                Size = new Size(84, 26),
            };
            var cancelButton = new Button
            {
                Text = l["settings.modelPicker.cancel"],
                DialogResult = DialogResult.Cancel,
                Location = new Point(364, 344),
                Size = new Size(84, 26),
            };
            AcceptButton = okButton;
            CancelButton = cancelButton;

            Controls.AddRange(new Control[]
            {
                descriptionLabel, _modelList, okButton, cancelButton,
            });
        }
    }

    /// <summary>模型抓取对话框：GET /models + 可勾选多个模型的列表。</summary>
    internal sealed class ModelPickerDialog : Form
    {
        private readonly TextBox _baseUrlBox;
        private readonly TextBox _apiKeyBox;
        private readonly CheckedListBox _modelList;
        private readonly Button _fetchButton;
        private readonly Label _statusLabel;

        public string? SelectedModel => _modelList.CheckedItems.Count > 0
            ? _modelList.CheckedItems[0] as string
            : null;
        public IReadOnlyList<string> SelectedModels
            => _modelList.CheckedItems.Cast<string>()
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => model.Trim())
                .Where(model => model.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        public ModelPickerDialog(
            string baseUrl,
            string apiKey,
            bool readOnlyEndpoint = false)
        {
            var l = LocalizationService.Instance;
            Text = l["settings.modelPicker.title"];
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(460, 368);
            Font = new Font("Segoe UI", 9f);

            var urlLabel = new Label
            {
                Text = l["settings.modelPicker.baseUrl"],
                Location = new Point(12, 12),
                AutoSize = true,
            };
            _baseUrlBox = new TextBox { Location = new Point(12, 32), Width = 436 };
            _baseUrlBox.Text = string.IsNullOrWhiteSpace(baseUrl)
                ? DeepSeekApiService.DefaultBaseUrl
                : baseUrl;

            var keyLabel = new Label
            {
                Text = l["settings.modelPicker.apiKey"],
                Location = new Point(12, 62),
                AutoSize = true,
            };
            _apiKeyBox = new TextBox
            {
                Location = new Point(12, 82),
                Width = 436,
                UseSystemPasswordChar = true,
            };
            _apiKeyBox.Text = apiKey ?? string.Empty;
            if (readOnlyEndpoint)
            {
                _baseUrlBox.ReadOnly = true;
                _apiKeyBox.ReadOnly = true;
            }

            _fetchButton = new Button
            {
                Text = l["settings.modelPicker.fetch"],
                Location = new Point(12, 112),
                Size = new Size(160, 26),
            };
            _fetchButton.Click += async (_, _) => await FetchAsync();

            _statusLabel = new Label
            {
                Location = new Point(182, 118),
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
            };

            var listLabel = new Label
            {
                Text = l["settings.modelPicker.select"],
                Location = new Point(12, 148),
                AutoSize = true,
            };
            _modelList = new CheckedListBox
            {
                Location = new Point(12, 168),
                Size = new Size(436, 130),
                CheckOnClick = true,
            };

            var okButton = new Button
            {
                Text = l["settings.modelPicker.ok"],
                Location = new Point(273, 326),
                Size = new Size(84, 26),
            };
            okButton.Click += (_, _) =>
            {
                if (_modelList.CheckedItems.Count == 0)
                {
                    MessageBox.Show(
                        this,
                        l["settings.modelPicker.selectRequired"],
                        Text,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                DialogResult = DialogResult.OK;
            };
            var cancelButton = new Button
            {
                Text = l["settings.modelPicker.cancel"],
                DialogResult = DialogResult.Cancel,
                Location = new Point(364, 326),
                Size = new Size(84, 26),
            };
            AcceptButton = okButton;
            CancelButton = cancelButton;

            Controls.AddRange(new Control[]
            {
                urlLabel, _baseUrlBox, keyLabel, _apiKeyBox,
                _fetchButton, _statusLabel, listLabel, _modelList,
                okButton, cancelButton,
            });
        }

        private async Task FetchAsync()
        {
            var l = LocalizationService.Instance;
            _fetchButton.Enabled = false;
            _modelList.Items.Clear();
            _statusLabel.Text = l["settings.modelPicker.fetching"];
            _statusLabel.ForeColor = SystemColors.GrayText;
            try
            {
                var models = await ModelFetchService.FetchModelsAsync(
                    _baseUrlBox.Text.Trim(), _apiKeyBox.Text.Trim());
                foreach (var model in models)
                    _modelList.Items.Add(model);
                _statusLabel.Text = string.Format(l["settings.modelPicker.count"], models.Count);
            }
            catch (Exception ex)
            {
                _statusLabel.Text = ex.Message;
                _statusLabel.ForeColor = Color.Firebrick;
            }
            finally
            {
                _fetchButton.Enabled = true;
            }
        }
    }
}
