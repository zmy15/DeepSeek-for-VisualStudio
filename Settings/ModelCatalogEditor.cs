using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
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
    /// 统一「模型设置」表格中的一行数据。
    /// </summary>
    /// <param name="Model">模型名称（官方或自定义端点模型）。</param>
    /// <param name="MaxTokens">该模型的最大上下文 Token；0 表示未配置（运行时回退默认值）。</param>
    /// <param name="IsVision">是否支持图片/PDF 直传（多模态）。</param>
    /// <param name="IsOfficial">是否来自官方远端目录。</param>
    /// <param name="FromMetadata">
    /// Token/视觉是否来自官方接口元数据。为 true 时表格中该行只读，用户不可手改。
    /// </param>
    /// <param name="DisplayName">展示名称：官方取接口 name，自定义模型可自行填写。</param>
    /// <param name="MaxOutputTokens">单次输出上限（token）；官方取接口值，自定义模型可自行填写，0 表示未设置。</param>
    /// <param name="EffortLevels">推理档位列表；官方取接口 supported_levels，自定义模型可自行填写。</param>
    internal sealed record ModelCatalogRow(
        string Model,
        int MaxTokens,
        bool IsVision,
        bool IsOfficial = false,
        bool FromMetadata = false,
        string DisplayName = "",
        int MaxOutputTokens = 0,
        IReadOnlyList<string>? EffortLevels = null,
        string EndpointId = "",
        string EndpointName = "");

    /// <summary>
    /// 端点集合、模型列表和模型元数据与统一表格行集合之间的双向映射。
    /// 表格确认后同时回写多端点 JSON 与兼容用的旧版模型字符串。
    /// </summary>
    internal static class ModelCatalogMapping
    {
        /// <summary>
        /// 从选项页读取端点与模型配置，组装为表格行集合。
        /// 行顺序：官方模型 → 自定义模型 → 已配置 Token 的模型 → 已勾选的视觉模型 → 当前选中项，去重保序。
        /// </summary>
        /// <param name="page">当前的 DeepSeek 选项页实例。</param>
        /// <returns>初始行集合（官方模型在前，便于用户识别）。</returns>
        public static List<ModelCatalogRow> BuildRows(DeepSeekOptionsPage page)
        {
            if (page == null)
                throw new ArgumentNullException(nameof(page));

            var officialModels = OfficialModelCatalogService.GetModels();
            var endpoints = page.GetCustomEndpoints();
            var customEntries = page.GetCustomModelEntries();
            var limits = ModelTokenLimitService.ParseLimits(page.ModelMaxTokenLimits);
            var vision = new HashSet<string>(page.GetVisionModels(), StringComparer.OrdinalIgnoreCase);
            var extra = DeepSeekOptionsPage.ParseCustomModelMetadata(page.CustomModelMetadata);

            var rows = new List<ModelCatalogRow>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var defaultEndpoint = endpoints.FirstOrDefault();

            // 局部函数：统一做非空校验、去重并填充该行的 Token/视觉初始值。
            // 官方模型以接口元数据为权威值；接口未给出时回退用户配置。
            void Add(
                string? model,
                string? endpointId = null,
                string? endpointName = null,
                bool treatAsCustom = false)
            {
                if (string.IsNullOrWhiteSpace(model))
                    return;
                string name = model.Trim();
                string id = (endpointId ?? string.Empty).Trim();
                string sourceName = (endpointName ?? string.Empty).Trim();
                string key = treatAsCustom || id.Length > 0
                    ? id + "\0" + name
                    : "official\0" + name;
                if (name.Length == 0 || !seen.Add(key))
                    return;

                var metadata = treatAsCustom || id.Length > 0
                    ? null
                    : OfficialModelCatalogService.TryGetMetadata(name);
                if (metadata != null)
                {
                    int officialTokens = metadata.ContextWindow ?? 0;
                    bool officialVision = metadata is { HasModalityInfo: true, SupportsImageInput: true };
                    rows.Add(new ModelCatalogRow(
                        name, officialTokens, officialVision, IsOfficial: true, FromMetadata: true,
                        metadata.DisplayName ?? string.Empty, metadata.MaxOutputTokens ?? 0,
                        metadata.EffortLevelsOrEmpty));
                    return;
                }

                limits.TryGetValue(name, out int maxTokens);
                extra.TryGetValue(name, out var customExtra);
                rows.Add(new ModelCatalogRow(
                    name, maxTokens, vision.Contains(name),
                    DisplayName: customExtra?.DisplayName ?? string.Empty,
                    MaxOutputTokens: customExtra?.MaxOutputTokens ?? 0,
                    EffortLevels: customExtra?.EffortLevels ?? Array.Empty<string>(),
                    EndpointId: id,
                    EndpointName: sourceName));
            }

            foreach (string model in officialModels)
                Add(model);
            foreach (var entry in customEntries)
                Add(entry.Model, entry.EndpointId, entry.EndpointName, treatAsCustom: true);
            foreach (string model in limits.Keys)
            {
                Add(
                    model,
                    defaultEndpoint?.Id,
                    defaultEndpoint?.Name);
            }
            foreach (string model in vision)
            {
                Add(
                    model,
                    defaultEndpoint?.Id,
                    defaultEndpoint?.Name);
            }
            Add(page.SelectedModel);
            var activeEndpoint = endpoints.FirstOrDefault(endpoint =>
                string.Equals(
                    endpoint.Id,
                    page.ActiveCustomEndpointId,
                    StringComparison.Ordinal));
            Add(
                page.ActiveCustomModel,
                page.ActiveCustomEndpointId,
                activeEndpoint?.Name,
                treatAsCustom: true);

            return rows;
        }

        /// <summary>
        /// 把表格行集合回写为端点集合与兼容用的模型字符串。
        /// </summary>
        /// <param name="page">当前的 DeepSeek 选项页实例。</param>
        /// <param name="rows">用户在表格中编辑并确认的行集合。</param>
        public static void ApplyRows(
            DeepSeekOptionsPage page,
            IReadOnlyList<ModelCatalogRow> rows,
            IReadOnlyList<CustomEndpointConfig>? endpoints = null)
        {
            if (page == null)
                throw new ArgumentNullException(nameof(page));
            if (rows == null)
                throw new ArgumentNullException(nameof(rows));

            var configuredEndpoints = DeepSeekOptionsPage.NormalizeCustomEndpoints(
                endpoints ?? page.GetCustomEndpoints()).ToList();
            var endpointMap = configuredEndpoints.ToDictionary(
                endpoint => endpoint.Id,
                StringComparer.Ordinal);
            var modelsByEndpoint = configuredEndpoints.ToDictionary(
                endpoint => endpoint.Id,
                _ => new List<string>(),
                StringComparer.Ordinal);

            // ① 自定义模型列表：官方模型由 OfficialModelCatalogService 动态提供。
            //    用户若显式保留同名自定义模型，则按端点继续保存。
            foreach (var row in rows.Where(row => !row.IsOfficial))
            {
                var endpoint = row.EndpointId.Length > 0 &&
                    endpointMap.TryGetValue(row.EndpointId, out var matched)
                        ? matched
                        : configuredEndpoints.FirstOrDefault();
                if (endpoint != null && modelsByEndpoint.TryGetValue(endpoint.Id, out var models))
                    models.Add(row.Model);
            }

            if (configuredEndpoints.Count > 0)
            {
                var updatedEndpoints = configuredEndpoints
                    .Select(endpoint => new CustomEndpointConfig(
                        endpoint.Id,
                        endpoint.Name,
                        endpoint.BaseUrl,
                        endpoint.ApiKey,
                        modelsByEndpoint[endpoint.Id]))
                    .ToList();
                page.SetCustomEndpoints(updatedEndpoints);
            }
            else
            {
                if (endpoints != null)
                    page.SetCustomEndpoints(Array.Empty<CustomEndpointConfig>());
                var customModels = rows
                    .Where(row => !row.IsOfficial)
                    .Select(row => row.Model)
                    .ToList();
                page.SetCustomModels(customModels);
            }

            // ② 模型最大 Token：只写入用户可编辑（非官方元数据）的行，格式保持 "模型名=token"。
            //    官方模型的上下文窗口由接口提供，落盘会造成过期副本。
            var limitLines = rows
                .Where(row => row.MaxTokens > 0 && !row.FromMetadata)
                .Select(row => row.Model + "=" + row.MaxTokens);
            page.ModelMaxTokenLimits = string.Join(Environment.NewLine, limitLines);

            // ③ 视觉模型：官方模型由接口的 input_modalities 权威判定，只写入自定义模型的手动勾选。
            page.SetVisionModels(
                rows.Where(row => row.IsVision && !row.IsOfficial).Select(row => row.Model));

            // ④ 自定义模型的扩展元数据（展示名称 / 输出上限 / 推理档位）：
            //    官方模型由 /models 提供，不落盘；仅记录自定义模型的用户输入。
            page.CustomModelMetadata = DeepSeekOptionsPage.FormatCustomModelMetadata(
                rows.Where(row => !row.IsOfficial)
                    .Select(row => (
                        row.Model,
                        new DeepSeekOptionsPage.ModelExtraMetadata(
                            row.DisplayName ?? string.Empty,
                            row.MaxOutputTokens,
                            row.EffortLevels ?? Array.Empty<string>()))));
        }
    }

    /// <summary>
    /// 「模型设置」统一表格属性的类型编辑器。点击属性网格的 … 时弹出 <see cref="ModelCatalogDialog"/>，
    /// 在一个表格中同时编辑自定义模型列表、模型最大 Token 与视觉模型三项配置。
    /// </summary>
    public class ModelCatalogEditor : UITypeEditor
    {
        /// <inheritdoc />
        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext? context)
            => UITypeEditorEditStyle.Modal;

        /// <inheritdoc />
        public override object? EditValue(
            ITypeDescriptorContext? context,
            IServiceProvider provider,
            object? value)
        {
            if (context?.Instance is not DeepSeekOptionsPage page)
                return value;

            var rows = ModelCatalogMapping.BuildRows(page);
            var officialModels = OfficialModelCatalogService.GetModels();

            using var dialog = new ModelCatalogDialog(
                rows,
                officialModels,
                page.GetCustomEndpoints());
            if (dialog.ShowDialog() == DialogResult.OK && dialog.ResultRows is { } resultRows)
            {
                ModelCatalogMapping.ApplyRows(page, resultRows, dialog.ResultEndpoints);
                return page.CustomModelName;
            }

            return value;
        }
    }

    /// <summary>
    /// 统一「模型设置」表格对话框：每行代表一个模型，可编辑模型名、上下文 Token、视觉勾选并删除；
    /// 顶部提供「+ 添加模型」按钮，支持从端点获取或手动填写。
    /// </summary>
    internal sealed class ModelCatalogDialog : Form
    {
        private readonly DataGridView _grid;

        /// <summary>确定后输出的编辑结果行集合；取消时为 null。</summary>
        public IReadOnlyList<ModelCatalogRow>? ResultRows { get; private set; }

        /// <summary>确定后输出的端点集合。</summary>
        public IReadOnlyList<CustomEndpointConfig> ResultEndpoints { get; private set; }

        /// <summary>
        /// 构造对话框。
        /// </summary>
        /// <param name="rows">从三个底层字符串解析出的初始行集合。</param>
        /// <param name="officialModels">官方模型名列表，用于识别官方条目。</param>
        public ModelCatalogDialog(
            IReadOnlyList<ModelCatalogRow> rows,
            IReadOnlyList<string> officialModels,
            IReadOnlyList<CustomEndpointConfig> endpoints)
        {
            _endpoints = DeepSeekOptionsPage.NormalizeCustomEndpoints(endpoints).ToList();
            ResultEndpoints = _endpoints;
            // 官方模型由远端目录动态提供、删除无处落盘，下次打开必然重现，
            // 故预先构建集合，用于标注来源并拦截对官方行的删除操作。
            _officialSet = new HashSet<string>(officialModels, StringComparer.OrdinalIgnoreCase);
            _metadataSet = new HashSet<string>(
                OfficialModelCatalogService.GetMetadata().Select(model => model.Id),
                StringComparer.OrdinalIgnoreCase);

            var l = LocalizationService.Instance;
            Text = l["settings.modelCatalog.title"];
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = false;
            MinimizeBox = false;
            ClientSize = new Size(940, 500);
            MinimumSize = new Size(720, 360);
            Font = new Font("Segoe UI", 9f);

            var descriptionLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 46,
                Padding = new Padding(12, 10, 12, 6),
                Text = l["settings.modelCatalog.dialogDescription"],
            };

            // ── 顶部工具栏：仅放「+ 添加模型」按钮，保持增删行与确定/取消分离 ──
            var toolbar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 44,
                Padding = new Padding(12, 6, 12, 6),
            };
            var addButton = new Button
            {
                Text = l["settings.modelCatalog.addButton"],
                Dock = DockStyle.Left,
                Width = 130,
            };
            addButton.Click += OnAddModels;
            toolbar.Controls.Add(addButton);
            var manageEndpointsButton = new Button
            {
                Text = l["settings.customEndpoints.manageButton"],
                Dock = DockStyle.Left,
                Width = 150,
            };
            manageEndpointsButton.Click += OnManageEndpoints;
            toolbar.Controls.Add(manageEndpointsButton);

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
                HeaderText = l["settings.modelCatalog.modelColumn"],
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 26,
            });
            // 「展示名称」列：官方模型由接口 name 自动填入（该行只读）；自定义模型可自行填写。
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "display",
                HeaderText = l["settings.modelCatalog.displayNameColumn"],
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 20,
            });
            // 只读「来源」列：标注该行来自官方远端目录还是用户自定义/手动添加。
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "source",
                HeaderText = l["settings.modelCatalog.sourceColumn"],
                ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 12,
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "tokens",
                HeaderText = l["settings.modelCatalog.contextColumn"],
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 16,
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "maxout",
                HeaderText = l["settings.modelCatalog.maxOutputColumn"],
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 14,
            });
            _grid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = "vision",
                HeaderText = l["settings.modelCatalog.visionColumn"],
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 7,
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "effort",
                HeaderText = l["settings.modelCatalog.effortColumn"],
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 13,
            });
            _grid.Columns.Add(new DataGridViewButtonColumn
            {
                Name = "delete",
                HeaderText = l["settings.modelCatalog.deleteColumn"],
                Text = l["settings.modelCatalog.deleteColumn"],
                UseColumnTextForButtonValue = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                Width = 64,
                FlatStyle = FlatStyle.Flat,
            });
            _grid.RowTemplate.Height = 34;
            _grid.ColumnHeadersHeight = 32;
            _grid.CellContentClick += OnGridCellContentClick;

            ReplaceRows(rows);

            // ── 底部按钮：右对齐的「确定 / 取消」 ──
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
            okButton.Click += OnOkClick;
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(okButton);
            AcceptButton = okButton;
            CancelButton = cancelButton;

            Controls.Add(_grid);
            Controls.Add(toolbar);
            Controls.Add(buttons);
            Controls.Add(descriptionLabel);
        }

        /// <summary>用给定的行集合重建表格内容（先清空再逐行填充）。</summary>
        private void ReplaceRows(IReadOnlyList<ModelCatalogRow> rows)
        {
            var l = LocalizationService.Instance;
            string officialText = l["settings.modelCatalog.sourceOfficial"];
            string customText = l["settings.modelCatalog.sourceCustom"];

            _grid.Rows.Clear();
            foreach (var row in rows)
            {
                string tokenText = row.MaxTokens > 0
                    ? ModelTokenLimitService.FormatTokenCount(row.MaxTokens)
                    : string.Empty;

                // 来源由官方模型集合判定：命中即官方（元数据自动填充），否则视为自定义/手动添加。
                string endpointName = row.EndpointName;
                if (endpointName.Length == 0 && row.EndpointId.Length > 0)
                {
                    endpointName = _endpoints.FirstOrDefault(endpoint =>
                        string.Equals(endpoint.Id, row.EndpointId, StringComparison.Ordinal))?.Name
                        ?? string.Empty;
                }
                string sourceText = row.IsOfficial
                    ? officialText
                    : endpointName.Length > 0
                        ? string.Format(l["settings.modelCatalog.sourceEndpoint"], endpointName)
                        : customText;
                string displayText = row.DisplayName ?? string.Empty;
                string maxOutputText = row.MaxOutputTokens > 0
                    ? ModelTokenLimitService.FormatTokenCount(row.MaxOutputTokens)
                    : string.Empty;
                string effortText = row.EffortLevels is { Count: > 0 } levels
                    ? string.Join(" / ", levels)
                    : string.Empty;

                int index = _grid.Rows.Add(
                    row.Model, displayText, sourceText, tokenText, maxOutputText,
                    row.IsVision, effortText, string.Empty);
                _grid.Rows[index].Tag = row.EndpointId;

                // 官方模型的全部元数据由接口声明，整行只读，避免用户改动后与接口不一致。
                // 自定义模型行保持可编辑，用户可自行填写展示名称/上下文/输出上限/视觉/推理档位。
                if (row.FromMetadata)
                {
                    var gridRow = _grid.Rows[index];
                    foreach (DataGridViewCell cell in gridRow.Cells)
                    {
                        // 删除按钮列保持可点击（点击后仍会拦截官方行）。
                        if (cell.OwningColumn?.Name == "delete")
                            continue;
                        cell.ReadOnly = true;
                        cell.Style.BackColor = SystemColors.Control;
                    }
                }
            }
        }

        /// <summary>「+ 添加模型」：弹出二选一对话框，把返回的模型名追加为新行（已存在则跳过）。</summary>
        private void OnAddModels(object? sender, EventArgs e)
        {
            var l = LocalizationService.Instance;
            using var dialog = new ModelAddDialog(_endpoints);
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            _endpoints = DeepSeekOptionsPage.NormalizeCustomEndpoints(dialog.ResultEndpoints).ToList();
            ResultEndpoints = _endpoints;
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataGridViewRow gridRow in _grid.Rows)
            {
                string name = gridRow.Cells["model"].Value?.ToString()?.Trim() ?? string.Empty;
                if (name.Length > 0)
                    existing.Add((gridRow.Tag?.ToString() ?? string.Empty) + "\0" + name);
            }

            foreach (var entry in dialog.AddedModels)
            {
                string key = entry.EndpointId + "\0" + entry.Model;
                if (existing.Add(key))
                {
                    string endpointName = _endpoints.FirstOrDefault(endpoint =>
                        string.Equals(endpoint.Id, entry.EndpointId, StringComparison.Ordinal))?.Name
                        ?? entry.EndpointName;
                    int index = _grid.Rows.Add(
                        entry.Model,
                        string.Empty,
                        endpointName.Length > 0
                            ? string.Format(l["settings.modelCatalog.sourceEndpoint"], endpointName)
                            : l["settings.modelCatalog.sourceCustom"],
                        string.Empty, string.Empty, false, string.Empty, string.Empty);
                    _grid.Rows[index].Tag = entry.EndpointId;
                }
            }
        }

        private void OnManageEndpoints(object? sender, EventArgs e)
        {
            using var dialog = new CustomEndpointManagerDialog(_endpoints);
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            _endpoints = DeepSeekOptionsPage.NormalizeCustomEndpoints(dialog.ResultEndpoints).ToList();
            ResultEndpoints = _endpoints;
            var activeIds = new HashSet<string>(
                _endpoints.Select(endpoint => endpoint.Id), StringComparer.Ordinal);
            for (int i = _grid.Rows.Count - 1; i >= 0; i--)
            {
                string endpointId = _grid.Rows[i].Tag?.ToString() ?? string.Empty;
                if (endpointId.Length > 0 && !activeIds.Contains(endpointId))
                    _grid.Rows.RemoveAt(i);
            }
            RefreshEndpointSourceCells();
        }

        private void RefreshEndpointSourceCells()
        {
            var l = LocalizationService.Instance;
            var endpointNames = _endpoints.ToDictionary(
                endpoint => endpoint.Id,
                endpoint => endpoint.Name,
                StringComparer.Ordinal);
            foreach (DataGridViewRow row in _grid.Rows)
            {
                string endpointId = row.Tag?.ToString() ?? string.Empty;
                if (endpointId.Length == 0)
                    continue;
                if (endpointNames.TryGetValue(endpointId, out string? endpointName))
                {
                    row.Cells["source"].Value = string.Format(
                        l["settings.modelCatalog.sourceEndpoint"],
                        endpointName);
                }
            }
        }

        /// <summary>处理「删除」按钮列点击：移除该行。</summary>
        private void OnGridCellContentClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0)
                return;
            if (_grid.Columns[e.ColumnIndex].Name != "delete")
                return;

            // 官方模型由远端目录动态提供、删除无处落盘，若放行会在下次打开时重现，
            // 故直接拦截并提示，避免用户误以为已删除。
            string model = _grid.Rows[e.RowIndex].Cells["model"].Value?.ToString()?.Trim() ?? string.Empty;
            string endpointId = _grid.Rows[e.RowIndex].Tag?.ToString() ?? string.Empty;
            if (endpointId.Length == 0 && model.Length > 0 && _officialSet.Contains(model))
            {
                MessageBox.Show(
                    this,
                    LocalizationService.Instance["settings.modelCatalog.officialNotDeletable"],
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            _grid.Rows.RemoveAt(e.RowIndex);
        }

        /// <summary>确定按钮：校验并回读表格为行集合。</summary>
        private void OnOkClick(object? sender, EventArgs e)
        {
            if (TryBuildRows(out var rows, out string? error))
            {
                ResultRows = rows;
                ResultEndpoints = _endpoints;
                DialogResult = DialogResult.OK;
                return;
            }

            MessageBox.Show(
                this,
                error,
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        /// <summary>
        /// 逐行读取表格内容并校验合法性。
        /// 模型名为空的行会被跳过；Token 列非空时必须能解析；重复模型名会被拒绝。
        /// </summary>
        /// <param name="result">成功时输出的行集合。</param>
        /// <param name="errorMessage">失败时输出的错误提示。</param>
        /// <returns>全部行合法返回 true。</returns>
        private bool TryBuildRows(out List<ModelCatalogRow> result, out string? errorMessage)
        {
            // 提交编辑中的单元格，避免读到旧值。
            _grid.EndEdit();

            var l = LocalizationService.Instance;
            var rows = new List<ModelCatalogRow>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (DataGridViewRow gridRow in _grid.Rows)
            {
                string model = gridRow.Cells["model"].Value?.ToString()?.Trim() ?? string.Empty;
                if (model.Length == 0)
                    continue;

                string endpointId = gridRow.Tag?.ToString()?.Trim() ?? string.Empty;
                string endpointName = _endpoints.FirstOrDefault(endpoint =>
                    string.Equals(endpoint.Id, endpointId, StringComparison.Ordinal))?.Name
                    ?? string.Empty;
                string duplicateKey = endpointId + "\0" + model;
                if (!seen.Add(duplicateKey))
                {
                    result = rows;
                    errorMessage = string.Format(l["settings.modelCatalog.duplicateModel"], model);
                    return false;
                }

                string tokenText = gridRow.Cells["tokens"].Value?.ToString()?.Trim() ?? string.Empty;
                int maxTokens = 0;
                if (tokenText.Length > 0 &&
                    !ModelTokenLimitService.TryParseTokenCount(tokenText, out maxTokens))
                {
                    result = rows;
                    errorMessage = string.Format(l["settings.modelCatalog.invalidValue"], model);
                    return false;
                }

                bool isOfficial = endpointId.Length == 0 && _officialSet.Contains(model);
                bool fromMetadata = isOfficial && _metadataSet.Contains(model);
                string displayName =
                    gridRow.Cells["display"].Value?.ToString()?.Trim() ?? string.Empty;
                string maxOutputText =
                    gridRow.Cells["maxout"].Value?.ToString()?.Trim() ?? string.Empty;
                string effortText =
                    gridRow.Cells["effort"].Value?.ToString()?.Trim() ?? string.Empty;

                int maxOutput = 0;
                if (maxOutputText.Length > 0 &&
                    !ModelTokenLimitService.TryParseTokenCount(maxOutputText, out maxOutput))
                {
                    result = rows;
                    errorMessage = string.Format(l["settings.modelCatalog.invalidValue"], model);
                    return false;
                }

                bool isVision = ModelCatalogDialog.ReadCheckBox(gridRow.Cells["vision"].Value);
                IReadOnlyList<string> effortLevels = ParseEffortLevels(effortText);

                // 官方行的元数据以接口为准（整行只读，此处再取一次防止 UI 状态异常）。
                if (fromMetadata)
                {
                    var metadata = OfficialModelCatalogService.TryGetMetadata(model);
                    maxTokens = metadata?.ContextWindow ?? maxTokens;
                    displayName = metadata?.DisplayName ?? string.Empty;
                    maxOutput = metadata?.MaxOutputTokens ?? 0;
                    effortLevels = metadata?.EffortLevelsOrEmpty ?? Array.Empty<string>();
                    isVision = metadata is { HasModalityInfo: true, SupportsImageInput: true };
                }

                rows.Add(new ModelCatalogRow(
                    model, maxTokens, isVision, isOfficial, fromMetadata,
                    displayName, maxOutput, effortLevels, endpointId, endpointName));
            }

            result = rows;
            errorMessage = null;
            return true;
        }

        /// <summary>把 DataGridView 复选单元格的值安全地转换为布尔值。</summary>
        private static bool ReadCheckBox(object? value)
            => value is bool flag && flag;

        /// <summary>解析推理档位文本：支持 / | ; , 、分隔，去重保序。</summary>
        private static IReadOnlyList<string> ParseEffortLevels(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<string>();

            return text
                .Split(new[] { '/', '|', ';', ',', '；', '，' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(level => level.Trim())
                .Where(level => level.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>官方模型名集合（忽略大小写），用于标注来源并拦截官方行的删除。</summary>
        private readonly HashSet<string> _officialSet;

        /// <summary>带接口元数据的官方模型集合；这些行的 Token/视觉单元格只读。</summary>
        private readonly HashSet<string> _metadataSet;

        private List<CustomEndpointConfig> _endpoints;
    }

    /// <summary>
    /// 「+ 添加模型」子对话框：先选择自定义端点，再从端点获取或手动填写模型。
    /// 端点管理支持同时维护多个自定义端点。
    /// </summary>
    internal sealed class ModelAddDialog : Form
    {
        private readonly ListBox _pendingList;
        private readonly ComboBox _endpointCombo;
        private readonly TextBox _manualBox;
        private readonly List<CustomEndpointModelEntry> _added = new();
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
        private List<CustomEndpointConfig> _endpoints;

        /// <summary>确定后输出待添加的模型及所属端点。</summary>
        public IReadOnlyList<CustomEndpointModelEntry> AddedModels => _added;

        /// <summary>确定后的端点集合，包括本次新增或修改的端点。</summary>
        public IReadOnlyList<CustomEndpointConfig> ResultEndpoints => _endpoints;

        public ModelAddDialog(IReadOnlyList<CustomEndpointConfig> endpoints)
        {
            _endpoints = endpoints.Select(endpoint => endpoint.Clone()).ToList();

            var l = LocalizationService.Instance;
            Text = l["settings.modelCatalog.addTitle"];
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 470);
            Font = new Font("Segoe UI", 9f);

            var descriptionLabel = new Label
            {
                Text = l["settings.modelCatalog.addDescription"],
                Location = new Point(12, 12),
                Size = new Size(536, 36),
            };

            var endpointLabel = new Label
            {
                Text = l["settings.customEndpoints.selectLabel"],
                Location = new Point(12, 56),
                AutoSize = true,
            };
            _endpointCombo = new ComboBox
            {
                Location = new Point(12, 76),
                Width = 400,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            var manageButton = new Button
            {
                Text = l["settings.customEndpoints.manageButton"],
                Location = new Point(420, 74),
                Size = new Size(128, 26),
            };
            manageButton.Click += OnManageEndpoints;

            var fetchButton = new Button
            {
                Text = l["settings.modelCatalog.addFromEndpoint"],
                Location = new Point(12, 112),
                Size = new Size(180, 28),
            };
            fetchButton.Click += OnFetchFromEndpoint;

            var manualLabel = new Label
            {
                Text = l["settings.modelCatalog.addManualLabel"],
                Location = new Point(12, 152),
                AutoSize = true,
            };
            _manualBox = new TextBox
            {
                Location = new Point(12, 172),
                Width = 400,
            };
            var manualAddButton = new Button
            {
                Text = l["settings.modelCatalog.addManualButton"],
                Location = new Point(420, 170),
                Size = new Size(128, 26),
            };
            manualAddButton.Click += (_, _) => AddManualModels();

            var listLabel = new Label
            {
                Text = l["settings.modelCatalog.addListLabel"],
                Location = new Point(12, 210),
                AutoSize = true,
            };
            _pendingList = new ListBox
            {
                Location = new Point(12, 232),
                Size = new Size(536, 164),
                SelectionMode = SelectionMode.None,
            };

            var okButton = new Button
            {
                Text = l["settings.modelPicker.ok"],
                DialogResult = DialogResult.OK,
                Location = new Point(373, 418),
                Size = new Size(84, 26),
            };
            var cancelButton = new Button
            {
                Text = l["settings.modelPicker.cancel"],
                DialogResult = DialogResult.Cancel,
                Location = new Point(464, 418),
                Size = new Size(84, 26),
            };
            AcceptButton = okButton;
            CancelButton = cancelButton;

            Controls.AddRange(new Control[]
            {
                descriptionLabel, endpointLabel, _endpointCombo, manageButton,
                fetchButton, manualLabel, _manualBox, manualAddButton,
                listLabel, _pendingList, okButton, cancelButton,
            });

            RefreshEndpointCombo();
        }

        private void OnManageEndpoints(object? sender, EventArgs e)
        {
            using var dialog = new CustomEndpointManagerDialog(_endpoints);
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            _endpoints = DeepSeekOptionsPage.NormalizeCustomEndpoints(dialog.ResultEndpoints).ToList();
            RebuildAddedModels();
            RefreshEndpointCombo();
        }

        private void OnFetchFromEndpoint(object? sender, EventArgs e)
        {
            var endpoint = GetSelectedEndpoint();
            if (endpoint == null)
            {
                ShowEndpointRequired();
                return;
            }

            using var picker = new ModelPickerDialog(
                endpoint.BaseUrl,
                endpoint.ApiKey,
                readOnlyEndpoint: true);
            if (picker.ShowDialog(this) != DialogResult.OK)
                return;

            AddModels(endpoint, picker.SelectedModels);
        }

        private void AddManualModels()
        {
            var endpoint = GetSelectedEndpoint();
            if (endpoint == null)
            {
                ShowEndpointRequired();
                return;
            }

            var models = DeepSeekOptionsPage.ParseCustomModels(_manualBox.Text);
            if (models.Count == 0)
                return;

            AddModels(endpoint, models);
            _manualBox.Clear();
        }

        private void AddModels(
            CustomEndpointConfig endpoint,
            IEnumerable<string> models)
        {
            foreach (string raw in models)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                string model = raw.Trim();
                string key = endpoint.Id + "\0" + model;
                if (model.Length == 0 || !_seen.Add(key))
                    continue;

                var entry = new CustomEndpointModelEntry(endpoint.Id, endpoint.Name, model);
                _added.Add(entry);
                _pendingList.Items.Add($"{entry.Model} · {entry.EndpointName}");
            }
        }

        private void RebuildAddedModels()
        {
            var endpointNames = _endpoints.ToDictionary(
                endpoint => endpoint.Id,
                endpoint => endpoint.Name,
                StringComparer.Ordinal);
            var refreshed = _added
                .Where(entry => endpointNames.ContainsKey(entry.EndpointId))
                .Select(entry => new CustomEndpointModelEntry(
                    entry.EndpointId,
                    endpointNames[entry.EndpointId],
                    entry.Model))
                .ToList();

            _added.Clear();
            _seen.Clear();
            _pendingList.Items.Clear();
            foreach (var entry in refreshed)
            {
                string key = entry.EndpointId + "\0" + entry.Model;
                if (!_seen.Add(key))
                    continue;
                _added.Add(entry);
                _pendingList.Items.Add($"{entry.Model} · {entry.EndpointName}");
            }
        }

        private void RefreshEndpointCombo()
        {
            string previousId = GetSelectedEndpoint()?.Id ?? string.Empty;
            _endpointCombo.Items.Clear();
            foreach (var endpoint in _endpoints)
                _endpointCombo.Items.Add(new EndpointComboItem(endpoint));

            int selectedIndex = 0;
            for (int i = 0; i < _endpointCombo.Items.Count; i++)
            {
                if (_endpointCombo.Items[i] is EndpointComboItem item &&
                    string.Equals(item.Endpoint.Id, previousId, StringComparison.Ordinal))
                {
                    selectedIndex = i;
                    break;
                }
            }

            if (_endpointCombo.Items.Count > 0)
                _endpointCombo.SelectedIndex = selectedIndex;
        }

        private CustomEndpointConfig? GetSelectedEndpoint()
            => (_endpointCombo.SelectedItem as EndpointComboItem)?.Endpoint;

        private void ShowEndpointRequired()
        {
            MessageBox.Show(
                this,
                LocalizationService.Instance["settings.customEndpoints.required"],
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private sealed class EndpointComboItem
        {
            public CustomEndpointConfig Endpoint { get; }

            public EndpointComboItem(CustomEndpointConfig endpoint) => Endpoint = endpoint;

            public override string ToString()
                => $"{Endpoint.Name} · {Endpoint.BaseUrl}";
        }
    }

    /// <summary>
    /// Configures multiple custom endpoints in one dialog. The dialog keeps API keys masked
    /// and leaves model selection to <see cref="ModelAddDialog"/>.
    /// </summary>
    internal sealed class CustomEndpointManagerDialog : Form
    {
        private readonly ListBox _endpointList;
        private readonly TextBox _nameBox;
        private readonly TextBox _baseUrlBox;
        private readonly TextBox _apiKeyBox;
        private readonly List<CustomEndpointConfig> _endpoints;
        private bool _updatingFields;

        public IReadOnlyList<CustomEndpointConfig> ResultEndpoints { get; private set; }

        public CustomEndpointManagerDialog(IReadOnlyList<CustomEndpointConfig> endpoints)
        {
            _endpoints = endpoints.Select(endpoint => endpoint.Clone()).ToList();
            ResultEndpoints = _endpoints;

            var l = LocalizationService.Instance;
            Text = l["settings.customEndpoints.title"];
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(760, 480);
            Font = new Font("Segoe UI", 9f);

            var descriptionLabel = new Label
            {
                Text = l["settings.customEndpoints.description"],
                Location = new Point(12, 12),
                Size = new Size(736, 42),
            };

            _endpointList = new ListBox
            {
                Location = new Point(12, 64),
                Size = new Size(250, 330),
            };
            _endpointList.SelectedIndexChanged += (_, _) => LoadSelectedEndpoint();

            var addButton = new Button
            {
                Text = l["settings.customEndpoints.add"],
                Location = new Point(12, 404),
                Size = new Size(96, 28),
            };
            addButton.Click += (_, _) => AddEndpoint();
            var removeButton = new Button
            {
                Text = l["settings.customEndpoints.remove"],
                Location = new Point(116, 404),
                Size = new Size(96, 28),
            };
            removeButton.Click += (_, _) => RemoveEndpoint();

            var nameLabel = new Label
            {
                Text = l["settings.customEndpoints.name"],
                Location = new Point(284, 64),
                AutoSize = true,
            };
            _nameBox = new TextBox
            {
                Location = new Point(284, 84),
                Width = 464,
            };
            _nameBox.TextChanged += (_, _) => UpdateSelectedEndpoint();

            var baseUrlLabel = new Label
            {
                Text = l["settings.modelPicker.baseUrl"],
                Location = new Point(284, 122),
                AutoSize = true,
            };
            _baseUrlBox = new TextBox
            {
                Location = new Point(284, 142),
                Width = 464,
            };
            _baseUrlBox.TextChanged += (_, _) => UpdateSelectedEndpoint();

            var apiKeyLabel = new Label
            {
                Text = l["settings.modelPicker.apiKey"],
                Location = new Point(284, 180),
                AutoSize = true,
            };
            _apiKeyBox = new TextBox
            {
                Location = new Point(284, 200),
                Width = 464,
                UseSystemPasswordChar = true,
            };
            _apiKeyBox.TextChanged += (_, _) => UpdateSelectedEndpoint();

            var hintLabel = new Label
            {
                Text = l["settings.customEndpoints.modelsHint"],
                Location = new Point(284, 242),
                Size = new Size(464, 70),
                ForeColor = SystemColors.GrayText,
            };

            var okButton = new Button
            {
                Text = l["settings.modelPicker.ok"],
                Location = new Point(568, 436),
                Size = new Size(84, 28),
            };
            okButton.Click += OnOkClick;
            var cancelButton = new Button
            {
                Text = l["settings.modelPicker.cancel"],
                DialogResult = DialogResult.Cancel,
                Location = new Point(664, 436),
                Size = new Size(84, 28),
            };
            AcceptButton = okButton;
            CancelButton = cancelButton;

            Controls.AddRange(new Control[]
            {
                descriptionLabel, _endpointList, addButton, removeButton,
                nameLabel, _nameBox, baseUrlLabel, _baseUrlBox,
                apiKeyLabel, _apiKeyBox, hintLabel, okButton, cancelButton,
            });

            RefreshEndpointList();
            if (_endpointList.Items.Count > 0)
                _endpointList.SelectedIndex = 0;
        }

        private void AddEndpoint()
        {
            var endpoint = new CustomEndpointConfig();
            _endpoints.Add(endpoint);
            RefreshEndpointList();
            _endpointList.SelectedIndex = _endpoints.Count - 1;
            _baseUrlBox.Focus();
        }

        private void RemoveEndpoint()
        {
            int index = _endpointList.SelectedIndex;
            if (index < 0 || index >= _endpoints.Count)
                return;

            _endpoints.RemoveAt(index);
            RefreshEndpointList();
            if (_endpoints.Count > 0)
                _endpointList.SelectedIndex = Math.Min(index, _endpoints.Count - 1);
            else
                LoadSelectedEndpoint();
        }

        private void RefreshEndpointList()
        {
            int selected = _endpointList.SelectedIndex;
            _endpointList.Items.Clear();
            foreach (var endpoint in _endpoints)
                _endpointList.Items.Add(FormatEndpoint(endpoint));
            if (selected >= 0 && selected < _endpointList.Items.Count)
                _endpointList.SelectedIndex = selected;
        }

        private static string FormatEndpoint(CustomEndpointConfig endpoint)
        {
            string name = string.IsNullOrWhiteSpace(endpoint.Name)
                ? LocalizationService.Instance["settings.customEndpoints.unnamed"]
                : endpoint.Name;
            return $"{name} · {endpoint.BaseUrl}";
        }

        private void UpdateEndpointListItem(int index)
        {
            if (index >= 0 && index < _endpointList.Items.Count && index < _endpoints.Count)
                _endpointList.Items[index] = FormatEndpoint(_endpoints[index]);
        }

        private void LoadSelectedEndpoint()
        {
            if (_updatingFields)
                return;

            int index = _endpointList.SelectedIndex;
            _updatingFields = true;
            try
            {
                bool enabled = index >= 0 && index < _endpoints.Count;
                _nameBox.Enabled = enabled;
                _baseUrlBox.Enabled = enabled;
                _apiKeyBox.Enabled = enabled;
                if (!enabled)
                {
                    _nameBox.Clear();
                    _baseUrlBox.Clear();
                    _apiKeyBox.Clear();
                    return;
                }

                var endpoint = _endpoints[index];
                _nameBox.Text = endpoint.Name;
                _baseUrlBox.Text = endpoint.BaseUrl;
                _apiKeyBox.Text = endpoint.ApiKey;
            }
            finally
            {
                _updatingFields = false;
            }
        }

        private void UpdateSelectedEndpoint()
        {
            if (_updatingFields)
                return;

            int index = _endpointList.SelectedIndex;
            if (index < 0 || index >= _endpoints.Count)
                return;

            var endpoint = _endpoints[index];
            endpoint.Name = _nameBox.Text.Trim();
            endpoint.BaseUrl = _baseUrlBox.Text.Trim();
            endpoint.ApiKey = _apiKeyBox.Text.Trim();
            UpdateEndpointListItem(index);
        }

        private void OnOkClick(object? sender, EventArgs e)
        {
            var l = LocalizationService.Instance;
            foreach (var endpoint in _endpoints)
            {
                if (!Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    MessageBox.Show(
                        this,
                        string.Format(l["settings.customEndpoints.invalidUrl"], endpoint.BaseUrl),
                        Text,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }
            }

            ResultEndpoints = DeepSeekOptionsPage.NormalizeCustomEndpoints(_endpoints);
            if (_endpoints.Count > 0 && ResultEndpoints.Count == 0)
            {
                MessageBox.Show(
                    this,
                    l["settings.customEndpoints.required"],
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            DialogResult = DialogResult.OK;
        }
    }
}
