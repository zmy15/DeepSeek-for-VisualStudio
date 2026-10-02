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
    /// <param name="MaxTokens">该模型的最大上下文 Token；0 表示未配置（运行时回退默认 1M）。</param>
    /// <param name="IsVision">是否勾选为支持图片/PDF 直传的多模态模型。</param>
    internal sealed record ModelCatalogRow(string Model, int MaxTokens, bool IsVision);

    /// <summary>
    /// 选项页三个底层字符串与统一表格行集合之间的双向映射。
    /// 拆解旧的「自定义模型列表 / 模型最大 Token / 视觉模型」三项配置为表格行，
    /// 或在确定后按 <b>完全相同的旧格式</b>回写字符串，
    /// 从而让 <see cref="DeepSeekEndpointResolver"/>、<see cref="RuntimeSettingsSnapshot"/>、
    /// <see cref="ModelTokenLimitService"/> 等运行时消费链无需任何改动。
    /// </summary>
    internal static class ModelCatalogMapping
    {
        /// <summary>
        /// 从选项页读取三个底层字符串，组装为表格行集合。
        /// 行顺序：官方模型 → 自定义模型 → 已配置 Token 的模型 → 已勾选的视觉模型 → 当前选中项，去重保序。
        /// </summary>
        /// <param name="page">当前的 DeepSeek 选项页实例。</param>
        /// <returns>初始行集合（官方模型在前，便于用户识别）。</returns>
        public static List<ModelCatalogRow> BuildRows(DeepSeekOptionsPage page)
        {
            if (page == null)
                throw new ArgumentNullException(nameof(page));

            var officialModels = OfficialModelCatalogService.GetModels();
            var customModels = page.GetCustomModels();
            var limits = ModelTokenLimitService.ParseLimits(page.ModelMaxTokenLimits);
            var vision = new HashSet<string>(page.GetVisionModels(), StringComparer.OrdinalIgnoreCase);

            var rows = new List<ModelCatalogRow>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 局部函数：统一做非空校验、去重并填充该行的 Token/视觉初始值。
            void Add(string? model)
            {
                if (string.IsNullOrWhiteSpace(model))
                    return;
                string name = model.Trim();
                if (name.Length == 0 || !seen.Add(name))
                    return;

                limits.TryGetValue(name, out int maxTokens);
                rows.Add(new ModelCatalogRow(name, maxTokens, vision.Contains(name)));
            }

            foreach (string model in officialModels)
                Add(model);
            foreach (string model in customModels)
                Add(model);
            foreach (string model in limits.Keys)
                Add(model);
            foreach (string model in vision)
                Add(model);
            Add(page.SelectedModel);
            Add(page.ActiveCustomModel);

            return rows;
        }

        /// <summary>
        /// 把表格行集合回写为三个底层字符串，格式与改造前保持一致，确保向后兼容。
        /// </summary>
        /// <param name="page">当前的 DeepSeek 选项页实例。</param>
        /// <param name="rows">用户在表格中编辑并确认的行集合。</param>
        public static void ApplyRows(DeepSeekOptionsPage page, IReadOnlyList<ModelCatalogRow> rows)
        {
            if (page == null)
                throw new ArgumentNullException(nameof(page));
            if (rows == null)
                throw new ArgumentNullException(nameof(rows));

            var officialSet = new HashSet<string>(
                OfficialModelCatalogService.GetModels(), StringComparer.OrdinalIgnoreCase);
            // 记录原有自定义条目，避免把用户此前手填、恰与官方同名的模型误删。
            var originalCustom = new HashSet<string>(
                page.GetCustomModels(), StringComparer.OrdinalIgnoreCase);

            // ① 自定义模型列表：官方模型由 OfficialModelCatalogService 动态提供，
            //    若一并写入 CustomModelName，会让聊天窗口下拉出现带自定义后缀的重复项。
            var customModels = rows
                .Where(row => !officialSet.Contains(row.Model) || originalCustom.Contains(row.Model))
                .Select(row => row.Model)
                .ToList();
            page.SetCustomModels(customModels);

            // ② 模型最大 Token：仅写入配置了有效值的行，格式保持 "模型名=token"。
            var limitLines = rows
                .Where(row => row.MaxTokens > 0)
                .Select(row => row.Model + "=" + row.MaxTokens);
            page.ModelMaxTokenLimits = string.Join(Environment.NewLine, limitLines);

            // ③ 视觉模型：写入全部勾选行，换行分隔。
            page.SetVisionModels(rows.Where(row => row.IsVision).Select(row => row.Model));
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
                page.ApiBaseUrl,
                ApiKeyProtection.Unprotect(page.CustomApiKey));
            if (dialog.ShowDialog() == DialogResult.OK && dialog.ResultRows is { } resultRows)
            {
                ModelCatalogMapping.ApplyRows(page, resultRows);
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

        /// <summary>
        /// 构造对话框。
        /// </summary>
        /// <param name="rows">从三个底层字符串解析出的初始行集合。</param>
        /// <param name="officialModels">官方模型名列表，用于识别官方条目。</param>
        /// <param name="baseUrl">自定义端点 Base URL，供「+」按钮的端点获取复用。</param>
        /// <param name="apiKey">自定义端点密钥（已解密），供端点获取复用。</param>
        public ModelCatalogDialog(
            IReadOnlyList<ModelCatalogRow> rows,
            IReadOnlyList<string> officialModels,
            string? baseUrl,
            string? apiKey)
        {
            var l = LocalizationService.Instance;
            Text = l["settings.modelCatalog.title"];
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = false;
            MinimizeBox = false;
            ClientSize = new Size(680, 460);
            MinimumSize = new Size(520, 340);
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
                FillWeight = 46,
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "tokens",
                HeaderText = l["settings.modelCatalog.contextColumn"],
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 30,
            });
            _grid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = "vision",
                HeaderText = l["settings.modelCatalog.visionColumn"],
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 12,
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
            _grid.Rows.Clear();
            foreach (var row in rows)
            {
                string tokenText = row.MaxTokens > 0
                    ? ModelTokenLimitService.FormatTokenCount(row.MaxTokens)
                    : string.Empty;
                _grid.Rows.Add(row.Model, tokenText, row.IsVision, string.Empty);
            }
        }

        /// <summary>「+ 添加模型」：弹出二选一对话框，把返回的模型名追加为新行（已存在则跳过）。</summary>
        private void OnAddModels(object? sender, EventArgs e)
        {
            var l = LocalizationService.Instance;
            using var dialog = new ModelAddDialog(_baseUrl, _apiKey);
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataGridViewRow gridRow in _grid.Rows)
            {
                string name = gridRow.Cells["model"].Value?.ToString()?.Trim() ?? string.Empty;
                if (name.Length > 0)
                    existing.Add(name);
            }

            foreach (string model in dialog.AddedModels)
            {
                if (existing.Add(model))
                    _grid.Rows.Add(model, string.Empty, false, string.Empty);
            }
        }

        /// <summary>处理「删除」按钮列点击：移除该行。</summary>
        private void OnGridCellContentClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0)
                return;
            if (_grid.Columns[e.ColumnIndex].Name != "delete")
                return;

            _grid.Rows.RemoveAt(e.RowIndex);
        }

        /// <summary>确定按钮：校验并回读表格为行集合。</summary>
        private void OnOkClick(object? sender, EventArgs e)
        {
            if (TryBuildRows(out var rows, out string? error))
            {
                ResultRows = rows;
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

                if (!seen.Add(model))
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

                bool isVision = ModelCatalogDialog.ReadCheckBox(gridRow.Cells["vision"].Value);
                rows.Add(new ModelCatalogRow(model, maxTokens, isVision));
            }

            result = rows;
            errorMessage = null;
            return true;
        }

        /// <summary>把 DataGridView 复选单元格的值安全地转换为布尔值。</summary>
        private static bool ReadCheckBox(object? value)
            => value is bool flag && flag;

        private readonly string? _baseUrl;
        private readonly string? _apiKey;
    }

    /// <summary>
    /// 「+ 添加模型」子对话框：支持「从端点获取」与「手动填写」两种方式，返回待追加的模型名集合。
    /// </summary>
    internal sealed class ModelAddDialog : Form
    {
        private readonly ListBox _pendingList;
        private readonly TextBox _manualBox;
        private readonly List<string> _added = new();
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
        private readonly string? _baseUrl;
        private readonly string? _apiKey;

        /// <summary>确定后输出待添加的模型名集合；取消时为空集合。</summary>
        public IReadOnlyList<string> AddedModels => _added;

        /// <summary>
        /// 构造「+ 添加模型」对话框。
        /// </summary>
        /// <param name="baseUrl">自定义端点 Base URL，用于「从端点获取」。</param>
        /// <param name="apiKey">自定义端点密钥（已解密），用于「从端点获取」。</param>
        public ModelAddDialog(string? baseUrl, string? apiKey)
        {
            _baseUrl = baseUrl;
            _apiKey = apiKey;

            var l = LocalizationService.Instance;
            Text = l["settings.modelCatalog.addTitle"];
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(460, 388);
            Font = new Font("Segoe UI", 9f);

            var descriptionLabel = new Label
            {
                Text = l["settings.modelCatalog.addDescription"],
                Location = new Point(12, 12),
                Size = new Size(436, 34),
            };

            // ── 方式一：从端点获取 ──
            var fetchButton = new Button
            {
                Text = l["settings.modelCatalog.addFromEndpoint"],
                Location = new Point(12, 50),
                Size = new Size(180, 28),
            };
            fetchButton.Click += OnFetchFromEndpoint;

            var manualLabel = new Label
            {
                Text = l["settings.modelCatalog.addManualLabel"],
                Location = new Point(12, 90),
                AutoSize = true,
            };
            _manualBox = new TextBox
            {
                Location = new Point(12, 112),
                Width = 436,
            };
            var manualAddButton = new Button
            {
                Text = l["settings.modelCatalog.addManualButton"],
                Location = new Point(360, 140),
                Size = new Size(88, 26),
            };
            manualAddButton.Click += (_, _) => AddManualModels();

            var listLabel = new Label
            {
                Text = l["settings.modelCatalog.addListLabel"],
                Location = new Point(12, 176),
                AutoSize = true,
            };
            _pendingList = new ListBox
            {
                Location = new Point(12, 198),
                Size = new Size(436, 140),
                SelectionMode = SelectionMode.None,
            };

            var okButton = new Button
            {
                Text = l["settings.modelPicker.ok"],
                DialogResult = DialogResult.OK,
                Location = new Point(273, 348),
                Size = new Size(84, 26),
            };
            var cancelButton = new Button
            {
                Text = l["settings.modelPicker.cancel"],
                DialogResult = DialogResult.Cancel,
                Location = new Point(364, 348),
                Size = new Size(84, 26),
            };
            AcceptButton = okButton;
            CancelButton = cancelButton;

            Controls.AddRange(new Control[]
            {
                descriptionLabel, fetchButton, manualLabel, _manualBox,
                manualAddButton, listLabel, _pendingList, okButton, cancelButton,
            });
        }

        /// <summary>「从端点获取」：复用现有模型抓取对话框，把选中的模型追加到待添加队列。</summary>
        private void OnFetchFromEndpoint(object? sender, EventArgs e)
        {
            using var picker = new ModelPickerDialog(_baseUrl ?? string.Empty, _apiKey ?? string.Empty);
            if (picker.ShowDialog(this) == DialogResult.OK)
                AddModels(picker.SelectedModels);
        }

        /// <summary>把手动输入框内容按既有分隔符拆分后追加到待添加队列。</summary>
        private void AddManualModels()
        {
            var models = DeepSeekOptionsPage.ParseCustomModels(_manualBox.Text);
            if (models.Count == 0)
                return;

            AddModels(models);
            _manualBox.Clear();
        }

        /// <summary>去重地追加模型名到待添加队列与列表显示。</summary>
        private void AddModels(IEnumerable<string> models)
        {
            foreach (string raw in models)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                string model = raw.Trim();
                if (model.Length == 0 || !_seen.Add(model))
                    continue;
                _added.Add(model);
                _pendingList.Items.Add(model);
            }
        }
    }
}