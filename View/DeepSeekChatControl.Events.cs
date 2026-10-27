using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.Agents;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.ToolWindows;
using DeepSeek_v4_for_VisualStudio.Utils;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// UI 事件处理器：输入框键盘、按钮点击、下拉框选择、WebView2 事件等。
    /// </summary>
    public partial class DeepSeekChatControl
    {
        #region Event Handlers - Input

        /// <summary>
        /// 输入框键盘事件：Enter 直接发送消息，Ctrl+Enter 插入换行，Ctrl+V 粘贴图片。
        /// Ctrl+V 在隧道阶段拦截，优先于 TextBox 内部命令绑定，确保图片粘贴可靠触发。
        /// </summary>
        private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // ── Ctrl+V: 优先检查剪贴板图片，在隧道阶段拦截 ──
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Logger.Info("PreviewKeyDown 检测到 Ctrl+V，检查剪贴板...");
                bool hasImage = TryPasteClipboardImage();
                if (hasImage)
                {
                    Logger.Info("Ctrl+V 已作为图片粘贴处理，拦截事件。");
                    e.Handled = true; // 拦截，阻止 TextBox 的默认文本粘贴
                    return;
                }
                Logger.Info("Ctrl+V 剪贴板无图片，交由 TextBox 默认文本粘贴处理。");
                // 无图片时放行，让 TextBox 默认行为处理文本粘贴
                return;
            }

            // ── Agent 弹出框键盘导航 ──
            if (AgentSuggestionPopup.IsOpen)
            {
                if (e.Key == Key.Down)
                {
                    e.Handled = true;
                    NavigateAgentSuggestion(1);
                    return;
                }
                if (e.Key == Key.Up)
                {
                    e.Handled = true;
                    NavigateAgentSuggestion(-1);
                    return;
                }
                if (e.Key == Key.Enter || e.Key == Key.Tab)
                {
                    e.Handled = true;
                    AcceptAgentSuggestion();
                    return;
                }
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    AgentSuggestionPopup.IsOpen = false;
                    return;
                }
            }

            // ── Skill 弹出框键盘导航 ──
            if (SkillSuggestionPopup.IsOpen)
            {
                if (e.Key == Key.Down)
                {
                    e.Handled = true;
                    NavigateSkillSuggestion(1);
                    return;
                }
                if (e.Key == Key.Up)
                {
                    e.Handled = true;
                    NavigateSkillSuggestion(-1);
                    return;
                }
                if (e.Key == Key.Enter || e.Key == Key.Tab)
                {
                    e.Handled = true;
                    AcceptSkillSuggestion();
                    return;
                }
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    SkillSuggestionPopup.IsOpen = false;
                    return;
                }
            }

            // ── 输入历史导航（无弹出框时，上下键浏览历史输入）──
            if (!AgentSuggestionPopup.IsOpen && !SkillSuggestionPopup.IsOpen)
            {
                if (e.Key == Key.Up)
                {
                    e.Handled = true;
                    NavigateInputHistory(-1);
                    return;
                }
                if (e.Key == Key.Down)
                {
                    e.Handled = true;
                    NavigateInputHistory(1);
                    return;
                }
            }

            // ── 编辑模式下 ESC 取消编辑 ──
            if (e.Key == Key.Escape && _pendingEditMsgIndex >= 0)
            {
                e.Handled = true;
                _ = HandleEditCancelAsync(_pendingEditMsgIndex);
                return;
            }

            if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    // Ctrl+Enter: 插入换行
                    e.Handled = false;
                    return;
                }

                if (Keyboard.Modifiers == ModifierKeys.Shift)
                {
                    // Shift+Enter: 插入换行
                    e.Handled = false;
                    return;
                }

                // 如果弹出框打开，Enter 优先选择技能/Agent
                if (SkillSuggestionPopup.IsOpen)
                {
                    e.Handled = true;
                    AcceptSkillSuggestion();
                    return;
                }
                if (AgentSuggestionPopup.IsOpen)
                {
                    e.Handled = true;
                    AcceptAgentSuggestion();
                    return;
                }

                // 普通 Enter: 发送消息
                e.Handled = true;
                SendMessage();
            }
        }

        /// <summary>
        /// 输入框粘贴事件（DataObject.Pasting 附加事件）：覆盖 Ctrl+V、右键菜单、Shift+Insert 等全部粘贴来源。
        /// 粘贴文本长度达到阈值时转存为临时文件并添加为附件，并取消默认的文本插入。
        /// </summary>
        private void InputTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            try
            {
                // 仅处理纯文本粘贴；图片粘贴由 PreviewKeyDown / CommandBinding 路径处理
                string? text = null;
                if (e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText, true))
                {
                    text = e.SourceDataObject.GetData(DataFormats.UnicodeText) as string;
                }
                else if (e.SourceDataObject.GetDataPresent(DataFormats.Text, true))
                {
                    text = e.SourceDataObject.GetData(DataFormats.Text) as string;
                }

                if (TryPasteLargeTextAsAttachment(text))
                {
                    e.CancelCommand(); // 取消默认粘贴，避免超长文本插入输入框
                    Logger.Info("InputTextBox_Pasting: 超长文本已转存为附件，取消默认粘贴。");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"InputTextBox_Pasting 失败: {ex.Message}", ex);
            }
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            // 如果弹出框打开，先关闭
            if (SkillSuggestionPopup.IsOpen)
            {
                SkillSuggestionPopup.IsOpen = false;
            }
            if (AgentSuggestionPopup.IsOpen)
            {
                AgentSuggestionPopup.IsOpen = false;
            }
            SendMessage();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopGeneration();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            // 二次确认：清空不可恢复，防止误触丢失会话
            var msg = LocalizationService.Instance["chat.clearConfirm"];
            var title = LocalizationService.Instance["chat.clearConfirmTitle"];
            if (!LocalizedMessageBox.Confirm(Window.GetWindow(this), title, msg))
            {
                return;
            }
            ClearConversation();
        }

        /// <summary>
        /// 拖放文件到聊天显示区 —— 拖入阶段：仅在确有文件拖入时声明接受并显示提示层。
        /// <para>
        /// 背景：聊天区使用视觉托管的 WebView2CompositionControl，
        /// Chromium 的 HTML5 拖放事件（dragenter/dragover/drop）在其下不会触发
        /// （上游 WebView2Feedback #5676），故拖放必须在 WPF 层接管。
        /// 参照 <c>SVsMainWindowDropTarget</c> 文档，自行注册拖放的窗口
        /// 不会继承 VS 主窗口「拖入即打开文件」的默认行为。
        /// </para>
        /// </summary>
        private void ChatWebViewHost_PreviewDragEnter(object sender, DragEventArgs e)
        {
            bool hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
            SetDropOverlayVisible(hasFiles);
            e.Handled = true;
        }

        /// <summary>
        /// 拖放文件到聊天显示区 —— 悬停阶段：必须声明接受，否则 Drop 不会触发。
        /// </summary>
        /// <remarks>
        /// 此处理器在单次拖拽中会触发数十次（随鼠标移动连续触发），
        /// 因此禁止记录日志或重建 UI —— 只做廉价的赋值与 Visibility 判断。
        /// </remarks>
        private void ChatWebViewHost_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        /// <summary>
        /// 拖放文件到聊天显示区 —— 离开阶段：隐藏提示层。
        /// </summary>
        private void ChatWebViewHost_PreviewDragLeave(object sender, DragEventArgs e)
        {
            SetDropOverlayVisible(false);
            e.Handled = true;
        }

        /// <summary>
        /// 拖放文件到聊天显示区 —— 落下阶段：把文件加入附件列表并统一提示结果。
        /// </summary>
        private void ChatWebViewHost_PreviewDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            SetDropOverlayVisible(false);
            try
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)
                    || e.Data.GetData(DataFormats.FileDrop) is not string[] paths
                    || paths.Length == 0)
                {
                    return;
                }

                var result = AttachmentCollector.Collect(paths, _attachedFilePaths);
                if (result.AddedCount > 0)
                {
                    _attachedFilePaths.AddRange(result.Added);
                    RefreshAttachedFilesUI();
                }

                StatusLabel.Text = BuildAttachmentStatusText(result);
                Logger.Info($"[DropFiles] 拖入 {paths.Length} 项: 新增={result.AddedCount}, "
                    + $"重复={result.AlreadyAttached.Count}, 不支持={result.Unsupported.Count}, "
                    + $"目录={result.Directories.Count}, 不存在={result.NotFound.Count}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[DropFiles] 处理拖入文件失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 显示/隐藏拖入提示层。
        /// 文案每次显示时重新取当前语言，保证语言切换后立即生效
        /// （不可缓存到首次显示，否则切换语言后提示会停留在旧语言）。
        /// </summary>
        private void SetDropOverlayVisible(bool visible)
        {
            if (visible)
            {
                var L = LocalizationService.Instance;
                ChatDropOverlayTitle.Text = L["status.dropOverlayTitle"];
                ChatDropOverlayHint.Text = L["status.dropOverlayHint"];
            }

            ChatDropOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 根据附件分类结果生成状态栏提示。优先报告实际新增，
        /// 无新增时按「目录 → 格式 → 不存在 → 重复」的优先级说明原因。
        /// 供拖放与文件对话框等各添加入口共用，保证提示口径一致。
        /// </summary>
        private static string BuildAttachmentStatusText(AttachmentCollectResult result)
        {
            var L = LocalizationService.Instance;

            if (result.AddedCount > 0)
            {
                string text = L.Format("status.filesAddedToContext", result.AddedCount);
                int skipped = result.Directories.Count + result.Unsupported.Count;
                return skipped > 0
                    ? text + L.Format("status.dropFilesSkippedSuffix", skipped)
                    : text;
            }

            if (result.Directories.Count > 0)
            {
                return L.Format("status.dropDirectoryUnsupported",
                    System.IO.Path.GetFileName(result.Directories[0]));
            }

            if (result.Unsupported.Count > 0)
            {
                return L.Format("status.fileFormatUnsupported",
                    System.IO.Path.GetExtension(result.Unsupported[0]));
            }

            if (result.NotFound.Count > 0)
            {
                return L.Format("status.dropFileNotFound",
                    System.IO.Path.GetFileName(result.NotFound[0]));
            }

            return L["status.noFilesAdded"];
        }

        /// <summary>
        /// 文件上传按钮点击：打开文件选择对话框，将选中文件添加到附件列表。
        /// </summary>
        private void UploadButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要上传的文件",
                Filter = FileParserService.GetFileFilter(),
                Multiselect = true,
            };

            if (dlg.ShowDialog() == true)
            {
                var result = AttachmentCollector.Collect(dlg.FileNames, _attachedFilePaths);
                if (result.AddedCount > 0)
                {
                    _attachedFilePaths.AddRange(result.Added);
                    RefreshAttachedFilesUI();
                }

                StatusLabel.Text = BuildAttachmentStatusText(result);
            }
        }

        /// <summary>
        /// 移除单个已附加的文件。
        /// <para>
        /// 按**完整路径**而非文件名移除：不同目录下可能存在同名文件
        /// （如两个项目的 `Program.cs`），按文件名查找会删错条目。
        /// 绑定见 XAML 中移除按钮的 <c>Tag="{Binding FilePath}"</c>。
        /// </para>
        /// </summary>
        private void RemoveAttachedFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string filePath || string.IsNullOrEmpty(filePath))
                return;

            // 路径比较与 AttachmentCollector 保持一致：归一化 + 忽略大小写
            int index = _attachedFilePaths.FindIndex(
                p => PathsEqual(p, filePath));

            if (index >= 0)
            {
                _attachedFilePaths.RemoveAt(index);
                RefreshAttachedFilesUI();
                Logger.Info($"[Attachment] 已移除附件: {filePath}");
            }
            else
            {
                // 列表已被其他操作更新（如发送后清空）时属正常路径，记录调试信息即可
                Logger.Debug($"[Attachment] 未找到待移除的附件（列表可能已刷新）: {filePath}");
            }
        }

        /// <summary>
        /// 判断两个附件路径是否指向同一文件。
        /// 归一化为全路径后按 OrdinalIgnoreCase 比较，与
        /// <see cref="AttachmentCollector"/> 的判重口径一致；
        /// 归一化失败时退回原始字符串比较，避免整次操作失败。
        /// <para>可见性放宽到 internal 以便单元测试覆盖（见 AttachedFilePathComparisonTests）。</para>
        /// </summary>
        internal static bool PathsEqual(string? a, string? b)
        {
            if (a == null || b == null) return false;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

            try
            {
                return string.Equals(
                    Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 点击附件缩略图或文件名时，用系统默认打开方式打开原图/原文件。
        /// </summary>
        private void OpenAttachedFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is string filePath)
            {
                TryOpenAttachment(filePath);
            }
        }

        /// <summary>
        /// 刷新附件文件标签 UI。
        /// </summary>
        private void RefreshAttachedFilesUI()
        {
            AttachedFilesControl.ItemsSource = null;
            AttachedFilesControl.ItemsSource = _attachedFilePaths
                .Select(path =>
                {
                    // 只判定一次图片类型，供图标/缩略图互斥显示与缩略图生成共用
                    bool isImage = OcrService.IsImageFile(path);
                    return new AttachedFileItem
                    {
                        FilePath = path,
                        FileName = System.IO.Path.GetFileName(path),
                        IsImage = isImage,
                        ThumbnailSource = isImage ? CreateThumbnail(path) : null,
                    };
                })
                .ToList();
        }

        private static BitmapImage? CreateThumbnail(string imagePath)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.DecodePixelWidth = 48;
                bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Attachment] 生成缩略图失败 {System.IO.Path.GetFileName(imagePath)}: {ex.Message}");
                return null;
            }
        }

        private static void TryOpenAttachment(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    Logger.Warn($"[Attachment] 无法打开附件，文件不存在: {filePath}");
                    return;
                }

                System.Diagnostics.Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"[Attachment] 打开附件失败 {System.IO.Path.GetFileName(filePath)}: {ex.Message}", ex);
            }
        }

        /// <summary>图表导出目录：%LocalAppData%\DeepSeekVS\diagrams\</summary>
        internal static string DiagramExportDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekVS", "diagrams");

        /// <summary>SVG 文档声明：独立 .svg 文件按 XML 解析，声明缺失时部分查看器会误判编码。</summary>
        private const string DiagramXmlDeclaration = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";

        /// <summary>
        /// 归一化后的根 &lt;svg&gt; id。Mermaid 每次渲染都用时间戳生成新 id，
        /// 且该 id 同时出现在 style 选择器、marker id 与 url(#...) 引用里，
        /// 不统一替换就做不到"同一张图内容相同"。
        /// </summary>
        private const string StableDiagramSvgId = "mermaid-diagram";

        /// <summary>图表文件保留天数。图表是一次性查看的辅助产物，过期即清理。</summary>
        internal const int DiagramRetentionDays = 7;

        /// <summary>图表文件数量上限。即便都在保留期内，也只保留最新的这么多张。</summary>
        internal const int DiagramMaxFileCount = 200;

        /// <summary>最近一次图表清理日期，保证每天只真正清扫一次（读写由该锁保护）。</summary>
        private static readonly object _diagramCleanupLock = new object();
        private static DateTime _lastDiagramCleanupDate = DateTime.MinValue;

        /// <summary>
        /// 匹配 HTML 空元素（void element）。用于兜底修复页面序列化产物：
        /// Mermaid 把节点文字放在 foreignObject 的 XHTML 里，标签内可能出现
        /// &lt;br&gt;，在 XML 里必须自闭合，否则独立 .svg 打开时报
        /// "Opening and ending tag mismatch"。
        /// 属性部分只接受 name="value" 形式，避免误吞属性值里的 &gt;。
        /// </summary>
        private static readonly Regex HtmlVoidTagRegex = new(
            @"<(?<tag>area|base|br|col|embed|hr|img|input|link|meta|param|source|track|wbr)"
            + @"(?<attrs>(?:\s+[:\w-]+\s*=\s*""[^""<>]*"")*)\s*(?<selfclose>/?)>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// 保存图表 SVG 到本地固定目录并用系统默认程序打开。
        /// </summary>
        /// <remarks>
        /// 只接受以 &lt;svg 开头的单张图（Mermaid 已渲染产物），并限制长度，
        /// 避免把任意 HTML 写盘后交给默认浏览器打开。
        /// </remarks>
        private void SaveDiagramToFile(string svg)
        {
            try
            {
                if (!IsExportableDiagramSvg(svg))
                {
                    Logger.Warn("[Diagram] 忽略不可导出的图表内容");
                    StatusLabel.Text = LocalizationService.Instance["status.diagramSaveFailed"];
                    return;
                }

                Directory.CreateDirectory(DiagramExportDirectory);

                string document = NormalizeDiagramSvg(svg);
                if (!IsWellFormedXml(document))
                    Logger.Warn("[Diagram] 导出内容不是合法 XML，已按原文写出（独立打开可能报错）");

                // 同一张图只落盘一次：重复点击直接复用已有文件，避免堆积同名副本
                string hash = ComputeDiagramHash(document);
                string? existing = FindExistingDiagramFile(DiagramExportDirectory, hash);

                string filePath;
                if (existing != null)
                {
                    filePath = existing;
                    Logger.Info($"[Diagram] 复用已保存的图表: {filePath}");
                }
                else
                {
                    filePath = Path.Combine(DiagramExportDirectory,
                        $"diagram_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{hash}.svg");

                    // 已带 XML 声明，按 UTF-8 无 BOM 写出，避免部分查看器把 BOM 当正文
                    File.WriteAllText(filePath, document, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    Logger.Info($"[Diagram] 图表已保存: {filePath}");
                }

                StatusLabel.Text = LocalizationService.Instance.Format("status.diagramSaved", filePath);

                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true,
                });

                // 保存成功后顺带清扫（每天只真正执行一次），失败不影响本次保存
                CleanupExpiredDiagramsIfNeeded();
            }
            catch (Exception ex)
            {
                Logger.Error($"[Diagram] 保存图表失败: {ex.Message}", ex);
                StatusLabel.Text = LocalizationService.Instance["status.diagramSaveFailed"];
            }
        }

        /// <summary>
        /// 每天最多执行一次的图表清扫（懒执行）。
        /// 采用与 Logger/DiagnosticLog 相同的双检锁，避免并发保存时重复清扫。
        /// </summary>
        internal static void CleanupExpiredDiagramsIfNeeded()
        {
            DateTime today = DateTime.Today;
            if (_lastDiagramCleanupDate >= today) return;

            lock (_diagramCleanupLock)
            {
                if (_lastDiagramCleanupDate >= today) return;

                CleanupExpiredDiagrams();
                _lastDiagramCleanupDate = today;
            }
        }

        /// <summary>
        /// 清理导出目录中的过期图表：超过保留期，或数量超过上限时最旧的若干张。
        /// 只处理 <c>diagram_*.svg</c>，绝不触碰同目录下的日志、会话等其他文件；
        /// 单个文件删除失败（例如正被查看器占用）只跳过该文件，不中断整轮清扫。
        /// </summary>
        /// <param name="retentionDays">保留天数。</param>
        /// <param name="maxFiles">保留的文件数量上限。</param>
        /// <param name="directoryOverride">测试用的目录覆盖；为空时使用默认导出目录。</param>
        /// <returns>实际删除的文件数。</returns>
        internal static int CleanupExpiredDiagrams(
            int retentionDays = DiagramRetentionDays,
            int maxFiles = DiagramMaxFileCount,
            string? directoryOverride = null)
        {
            try
            {
                string directory = directoryOverride ?? DiagramExportDirectory;
                if (!Directory.Exists(directory)) return 0;

                var candidates = new List<FileInfo>();
                foreach (string path in Directory.EnumerateFiles(directory, "diagram_*.svg"))
                {
                    try
                    {
                        candidates.Add(new FileInfo(path));
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[Diagram] 读取图表文件信息失败（跳过）: {path} — {ex.Message}");
                    }
                }

                DateTime cutoff = DateTime.Now.AddDays(-retentionDays);
                int removed = 0;

                // 先按保留期清理
                var survivors = new List<FileInfo>(candidates.Count);
                foreach (FileInfo file in candidates)
                {
                    if (file.LastWriteTime < cutoff)
                    {
                        if (TryDeleteDiagramFile(file)) removed++;
                    }
                    else
                    {
                        survivors.Add(file);
                    }
                }

                // 再按数量上限清理最旧的（即便未过期）
                if (maxFiles >= 0 && survivors.Count > maxFiles)
                {
                    survivors.Sort((a, b) => a.LastWriteTime.CompareTo(b.LastWriteTime));
                    int overflow = survivors.Count - maxFiles;
                    for (int i = 0; i < overflow; i++)
                    {
                        if (TryDeleteDiagramFile(survivors[i])) removed++;
                    }
                }

                if (removed > 0)
                    Logger.Info($"[Diagram] 图表清扫完成：移除 {removed} 个文件");

                return removed;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Diagram] 图表清扫失败: {ex.Message}");
                return 0;
            }
        }

        /// <summary>删除单个图表文件；失败只记录警告并返回 false，不向上抛出。</summary>
        private static bool TryDeleteDiagramFile(FileInfo file)
        {
            try
            {
                file.Delete();
                Logger.Info($"[Diagram] 已清理过期图表: {file.Name}");
                return true;
            }
            catch (Exception ex)
            {
                // 典型情况：文件正被浏览器/查看器占用，下次清扫再试即可
                Logger.Warn($"[Diagram] 清理图表失败（跳过）: {file.Name} — {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 校验待导出的 SVG 文本：必须是单张 &lt;svg&gt; 图且长度在合理范围内。
        /// </summary>
        internal static bool IsExportableDiagramSvg(string? svg)
        {
            if (string.IsNullOrWhiteSpace(svg)) return false;
            if (svg.Length > 4 * 1024 * 1024) return false;

            string trimmed = svg.TrimStart();
            // 页面正常发的是 HTML 序列化的 <svg> 片段，这里也允许已带 XML 声明的输入
            if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
            {
                int declEnd = trimmed.IndexOf("?>", StringComparison.Ordinal);
                trimmed = declEnd >= 0 ? trimmed.Substring(declEnd + 2).TrimStart() : trimmed;
            }
            if (!trimmed.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)) return false;

            // 页面 JS 用 outerHTML 序列化，正常不会带 <script>；此处做纵深防御
            if (trimmed.IndexOf("<script", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            return true;
        }

        /// <summary>
        /// 把 Mermaid 渲染出的 SVG 片段整理成合法、可独立打开的 .svg 文档：
        /// 补 XML 声明与 xmlns；当内容不是合法 XML 时，兜底把 HTML 空元素自闭合。
        /// </summary>
        internal static string NormalizeDiagramSvg(string? svg)
        {
            string text = (svg ?? string.Empty).Trim();
            if (text.Length == 0) return string.Empty;

            if (!text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
                text = DiagramXmlDeclaration + "\n" + text;

            text = EnsureSvgNamespace(text);
            text = NormalizeDiagramIdentity(text);

            // 正常路径由页面 XMLSerializer 产出合法 XML，直接返回；
            // 仅在不合法时才动手修复，避免误改本来就正确的内容。
            if (IsWellFormedXml(text)) return text;

            string repaired = SelfCloseVoidElements(text);
            return IsWellFormedXml(repaired) ? repaired : text;
        }

        /// <summary>给根 &lt;svg&gt; 补 xmlns（缺省命名空间会导致独立打开时按 HTML 解析）。</summary>
        private static string EnsureSvgNamespace(string text)
        {
            Match match = Regex.Match(text, @"<svg(\s|>)", RegexOptions.IgnoreCase);
            if (!match.Success) return text;

            int tagEnd = text.IndexOf('>', match.Index);
            if (tagEnd < 0) return text;

            string tag = text.Substring(match.Index, tagEnd - match.Index + 1);
            if (tag.IndexOf("xmlns", StringComparison.OrdinalIgnoreCase) >= 0) return text;

            return text.Substring(0, match.Index + 4)
                + " xmlns=\"http://www.w3.org/2000/svg\""
                + text.Substring(match.Index + 4);
        }

        /// <summary>
        /// 计算图表内容指纹，用于"同一张图只存一次"。
        /// 先消除 Mermaid 每次渲染不同的 id，再按 UTF-8 内容取 SHA-256 前 16 位十六进制。
        /// </summary>
        internal static string ComputeDiagramHash(string document)
        {
            string canonical = NormalizeDiagramIdentity(document ?? string.Empty);

            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                byte[] digest = sha256.ComputeHash(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                    .GetBytes(canonical));

                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                    sb.Append(digest[i].ToString("X2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// 抹掉 Mermaid 渲染随机 id，使同一张图得到同一份文本。
        /// Mermaid 的 id 形如 mermaid-svg-1792252615344-3，会同时出现在
        /// 根标签 id、style 选择器、marker/clipPath id 与 url(#...) 引用中，
        /// 这里做整体字面量替换（含其中的时间戳），保证替换彻底且可复现。
        /// </summary>
        internal static string NormalizeDiagramIdentity(string document)
        {
            if (string.IsNullOrEmpty(document)) return string.Empty;

            return Regex.Replace(document,
                @"mermaid-svg-\d+-\d+",
                StableDiagramSvgId,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>识别文件名里已内嵌的内容指纹（diagram_时间_指纹.svg）。</summary>
        private static readonly Regex DiagramFileNameHashRegex = new(
            @"_([0-9A-F]{16})\.svg$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// 在导出目录中查找已存在且内容相同的图表文件。
        /// 文件名内嵌内容指纹，直接命中即可；兼容早期不带指纹的旧文件时退化为内容比较。
        /// </summary>
        private static string? FindExistingDiagramFile(string directory, string hash)
        {
            try
            {
                foreach (string candidate in Directory.EnumerateFiles(directory, "*.svg"))
                {
                    string fileName = Path.GetFileName(candidate);
                    if (fileName.IndexOf(hash, StringComparison.OrdinalIgnoreCase) >= 0)
                        return candidate;

                    // 文件名已带别的指纹 → 内容必然不同，跳过读取
                    if (DiagramFileNameHashRegex.IsMatch(fileName)) continue;
                    if (!fileName.StartsWith("diagram_", StringComparison.OrdinalIgnoreCase)) continue;

                    // 旧文件名不含指纹：读一次内容比对，避免重复堆积
                    try
                    {
                        string existingDocument = File.ReadAllText(candidate);
                        if (string.Equals(ComputeDiagramHash(existingDocument), hash,
                                StringComparison.OrdinalIgnoreCase))
                            return candidate;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (DirectoryNotFoundException) { }
            catch (UnauthorizedAccessException ex)
            {
                Logger.Warn($"[Diagram] 扫描导出目录失败: {ex.Message}");
            }

            return null;
        }

        /// <summary>把未闭合的 HTML 空元素改为自闭合形式（&lt;br&gt; → &lt;br /&gt;）。</summary>
        private static string SelfCloseVoidElements(string text)
        {
            return HtmlVoidTagRegex.Replace(text, match =>
            {
                if (match.Groups["selfclose"].Value.Length > 0) return match.Value;
                return $"<{match.Groups["tag"].Value}{match.Groups["attrs"].Value} />";
            });
        }

        /// <summary>
        /// 判断文本是否为合法 XML。禁用 DTD 与外部实体解析，避免校验动作本身引入 XXE。
        /// </summary>
        internal static bool IsWellFormedXml(string? xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return false;

            try
            {
                var settings = new System.Xml.XmlReaderSettings
                {
                    DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                    XmlResolver = null,
                };

                using (var reader = System.Xml.XmlReader.Create(new StringReader(xml), settings))
                {
                    while (reader.Read()) { }
                }

                return true;
            }
            catch (System.Xml.XmlException)
            {
                return false;
            }
        }

        /// <summary>
        /// 清空已上传的文件列表。
        /// </summary>
        private void ClearAttachedFiles()
        {
            _attachedFilePaths.Clear();
            RefreshAttachedFilesUI();
        }

        /// <summary>
        /// ＋ 按钮点击：弹出上下文菜单 Popup。
        /// </summary>
        private void AddContextButton_Click(object sender, RoutedEventArgs e)
        {
            AddContextPopup.IsOpen = !AddContextPopup.IsOpen;
        }

        /// <summary>
        /// 添加上下文菜单 - 活动文档：将当前编辑器中的活动文档内容添加到对话上下文。
        /// </summary>
        private async void AddActiveDocument_Click(object sender, RoutedEventArgs e)
        {
            AddContextPopup.IsOpen = false;
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var dte = (EnvDTE.DTE)Package.GetGlobalService(typeof(EnvDTE.DTE));
                var doc = dte?.ActiveDocument;
                if (doc == null)
                {
                    StatusLabel.Text = LocalizationService.Instance["status.noActiveDocument"];
                    return;
                }

                string filePath = doc.FullName;
                string language = doc.Language;

                // 检查是否已添加
                if (_attachedFilePaths.Contains(filePath, StringComparer.OrdinalIgnoreCase))
                {
                    StatusLabel.Text = string.Format(LocalizationService.Instance["status.context.fileAlreadyAttached"], System.IO.Path.GetFileName(filePath));
                    return;
                }

                // 活动文档直接添加文件路径（FileParserService 会读取内容）
                if (FileParserService.IsSupportedFormat(filePath))
                {
                    _attachedFilePaths.Add(filePath);
                    RefreshAttachedFilesUI();
                    StatusLabel.Text = string.Format(LocalizationService.Instance["status.context.fileAttached"], System.IO.Path.GetFileName(filePath), language);
                    Logger.Info($"[AddContext] 活动文档已添加: {filePath}, 语言={language}");
                }
                else
                {
                    // 对于不支持的格式，将文件内容保存为临时 .txt 文件后添加
                    var textDoc = (EnvDTE.TextDocument)doc.Object("TextDocument");
                    string content = textDoc.StartPoint.CreateEditPoint().GetText(textDoc.EndPoint);

                    string tempDir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "DeepSeekVS", "temp", "context");
                    System.IO.Directory.CreateDirectory(tempDir);
                    string tempPath = System.IO.Path.Combine(tempDir,
                        $"doc_{System.IO.Path.GetFileNameWithoutExtension(filePath)}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                    System.IO.File.WriteAllText(tempPath, content);

                    _attachedFilePaths.Add(tempPath);
                    RefreshAttachedFilesUI();
                    StatusLabel.Text = string.Format(LocalizationService.Instance["status.context.fileAttached"], System.IO.Path.GetFileName(filePath), language);
                    Logger.Info($"[AddContext] 活动文档已保存为临时文件并添加: {tempPath}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"添加活动文档失败: {ex.Message}", ex);
                StatusLabel.Text = string.Format(LocalizationService.Instance["status.addDocFailed"], ex.Message);
            }
        }

        /// <summary>
        /// 添加上下文菜单 - 项目文件：打开文件对话框，选择项目中的文件。
        /// </summary>
        private void AddProjectFiles_Click(object sender, RoutedEventArgs e)
        {
            AddContextPopup.IsOpen = false;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要添加到上下文的项目文件",
                Filter = FileParserService.GetFileFilter(),
                Multiselect = true,
            };

            if (dlg.ShowDialog() == true)
            {
                var result = AttachmentCollector.Collect(dlg.FileNames, _attachedFilePaths);
                if (result.AddedCount > 0)
                {
                    _attachedFilePaths.AddRange(result.Added);
                    RefreshAttachedFilesUI();
                }

                StatusLabel.Text = BuildAttachmentStatusText(result);
                Logger.Info($"[AddContext] 项目文件已添加: {result.AddedCount} 个, "
                    + $"重复 {result.AlreadyAttached.Count} 个, 不支持 {result.Unsupported.Count} 个");
            }
        }

        /// <summary>
        /// 添加上下文菜单 - 项目全部文件：扫描解决方案中所有项目的源代码文件并添加到上下文。
        /// </summary>
        private async void AddAllProjectFiles_Click(object sender, RoutedEventArgs e)
        {
            AddContextPopup.IsOpen = false;
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var dte = (EnvDTE.DTE)Package.GetGlobalService(typeof(EnvDTE.DTE));
                if (dte?.Solution == null || !dte.Solution.IsOpen)
                {
                    StatusLabel.Text = LocalizationService.Instance["status.search.noSolution"];
                    return;
                }

                StatusLabel.Text = LocalizationService.Instance["status.search.scanningFiles"];
                int addedCount = 0;
                int skippedCount = 0;
                var sourceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ".cs", ".vb", ".cpp", ".c", ".h", ".hpp", ".fs", ".fsx",
                    ".xaml", ".xml", ".json", ".config", ".csproj", ".vbproj",
                    ".py", ".js", ".ts", ".jsx", ".tsx", ".css", ".scss", ".less",
                    ".html", ".htm", ".razor", ".cshtml", ".vbhtml",
                    ".sql", ".md", ".txt", ".yml", ".yaml", ".ps1", ".psm1",
                    ".go", ".rs", ".java", ".kt", ".swift", ".proto",
                };

                // 遍历解决方案中的所有项目
                foreach (EnvDTE.Project project in dte.Solution.Projects)
                {
                    try
                    {
                        var (projAdded, projSkipped) = await AddProjectItemsRecursiveAsync(project.ProjectItems, sourceExtensions);
                        addedCount += projAdded;
                        skippedCount += projSkipped;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[AddContext] 扫描项目 {project.Name} 时出错: {ex.Message}");
                    }
                }

                RefreshAttachedFilesUI();
                StatusLabel.Text = addedCount > 0
                    ? LocalizationService.Instance.Format("status.projectFilesAdded", addedCount)
                        + (skippedCount > 0 ? LocalizationService.Instance.Format("status.projectFilesSkippedSuffix", skippedCount) : "")
                    : LocalizationService.Instance["status.noSourceFilesFound"];
                Logger.Info($"[AddContext] 项目全部文件已添加: {addedCount} 个, 跳过: {skippedCount} 个");
            }
            catch (Exception ex)
            {
                Logger.Error($"添加项目全部文件失败: {ex.Message}", ex);
                StatusLabel.Text = string.Format(LocalizationService.Instance["status.addAllFilesFailed"], ex.Message);
            }
        }

        /// <summary>
        /// 递归遍历项目项，收集源代码文件。
        /// 返回 (addedCount, skippedCount)。
        /// </summary>
        private async Task<(int added, int skipped)> AddProjectItemsRecursiveAsync(
            EnvDTE.ProjectItems projectItems,
            HashSet<string> sourceExtensions)
        {
            int addedCount = 0;
            int skippedCount = 0;
            if (projectItems == null) return (addedCount, skippedCount);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            foreach (EnvDTE.ProjectItem item in projectItems)
            {
                try
                {
                    // 递归处理子项（文件夹）
                    if (item.ProjectItems != null && item.ProjectItems.Count > 0)
                    {
                        var (childAdded, childSkipped) = await AddProjectItemsRecursiveAsync(item.ProjectItems, sourceExtensions);
                        addedCount += childAdded;
                        skippedCount += childSkipped;
                    }

                    // 检查文件
                    string? filePath = null;
                    try { filePath = item.FileNames[0]; } catch { /* 某些项没有文件名 */ }

                    if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                    {
                        string ext = System.IO.Path.GetExtension(filePath);
                        if (sourceExtensions.Contains(ext) || FileParserService.IsSupportedFormat(filePath!))
                        {
                            if (!_attachedFilePaths.Contains(filePath!, StringComparer.OrdinalIgnoreCase))
                            {
                                _attachedFilePaths.Add(filePath!);
                                addedCount++;
                            }
                            else
                            {
                                skippedCount++;
                            }
                        }
                    }
                }
                catch
                {
                    // 跳过无法访问的项
                }
            }

            return (addedCount, skippedCount);
        }

        /// <summary>
        /// 添加上下文菜单 - 选中代码块：将编辑器选中的代码保存为临时文件并添加到上下文。
        /// </summary>
        private async void AddSelectedCode_Click(object sender, RoutedEventArgs e)
        {
            AddContextPopup.IsOpen = false;
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var dte = (EnvDTE.DTE)Package.GetGlobalService(typeof(EnvDTE.DTE));
                var doc = dte?.ActiveDocument;
                if (doc == null)
                {
                    StatusLabel.Text = LocalizationService.Instance["status.context.noActiveDoc"];
                    return;
                }

                var textDoc = (EnvDTE.TextDocument)doc.Object("TextDocument");
                var selection = textDoc.Selection as EnvDTE.TextSelection;
                if (selection == null || selection.IsEmpty)
                {
                    StatusLabel.Text = LocalizationService.Instance["status.context.noSelection"];
                    return;
                }

                string selectedCode = selection.Text;
                if (string.IsNullOrWhiteSpace(selectedCode))
                {
                    StatusLabel.Text = LocalizationService.Instance["status.context.selectionEmpty"];
                    return;
                }

                string language = doc.Language ?? "text";
                string fileExt = GetFileExtensionForLanguage(language);

                // 保存选中代码到临时文件
                string tempDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekVS", "temp", "context");
                System.IO.Directory.CreateDirectory(tempDir);
                string tempPath = System.IO.Path.Combine(tempDir,
                    $"selection_{DateTime.Now:yyyyMMdd_HHmmss}.{fileExt}");
                System.IO.File.WriteAllText(tempPath, selectedCode);

                _attachedFilePaths.Add(tempPath);
                RefreshAttachedFilesUI();
                StatusLabel.Text = string.Format(LocalizationService.Instance["status.context.selectionAttached"], selectedCode.Length, language);
                Logger.Info($"[AddContext] 选中代码块已添加: {tempPath}, 长度={selectedCode.Length}, 语言={language}");
            }
            catch (Exception ex)
            {
                Logger.Error($"添加选中代码块失败: {ex.Message}", ex);
                StatusLabel.Text = string.Format(LocalizationService.Instance["status.addSelectionFailed"], ex.Message);
            }
        }

        /// <summary>
        /// 添加上下文菜单 - 调试输出：捕获输出窗口中的调试信息，
        /// 并自动提取报错中引用的文件一并加入上下文。
        /// </summary>
        private async void AddDebugOutput_Click(object sender, RoutedEventArgs e)
        {
            AddContextPopup.IsOpen = false;
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var dte = (EnvDTE80.DTE2)Package.GetGlobalService(typeof(EnvDTE.DTE));
                if (dte == null)
                {
                    StatusLabel.Text = LocalizationService.Instance["status.context.noDebugEnv"];
                    return;
                }

                // 尝试从输出窗口获取调试输出
                string debugOutput = await CaptureDebugOutputAsync(dte);

                if (string.IsNullOrWhiteSpace(debugOutput))
                {
                    StatusLabel.Text = LocalizationService.Instance["status.context.noDebugOutput"];
                    return;
                }

                // 保存调试输出到临时文件
                string tempDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekVS", "temp", "context");
                System.IO.Directory.CreateDirectory(tempDir);
                string tempPath = System.IO.Path.Combine(tempDir,
                    $"debug_output_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                System.IO.File.WriteAllText(tempPath, debugOutput);

                _attachedFilePaths.Add(tempPath);
                RefreshAttachedFilesUI();

                // ── 从调试输出中提取报错引用的文件路径并添加上下文 ──
                int referencedFileCount = await ExtractAndAddReferencedFilesAsync(debugOutput);

                StatusLabel.Text = referencedFileCount > 0
                    ? $"已添加调试输出 ({debugOutput.Length} 字符) + {referencedFileCount} 个关联文件"
                    : $"已添加调试输出 ({debugOutput.Length} 字符)";
                Logger.Info($"[AddContext] 调试输出已添加: {tempPath}, 长度={debugOutput.Length}, 关联文件={referencedFileCount} 个");
            }
            catch (Exception ex)
            {
                Logger.Error($"添加调试输出失败: {ex.Message}", ex);
                StatusLabel.Text = string.Format(LocalizationService.Instance["status.addDebugFailed"], ex.Message);
            }
        }

        /// <summary>
        /// 从调试/构建输出中提取报错引用的文件路径，并添加到附件列表。
        /// 支持 MSBuild 格式 (file.cs(line,col): error) 和通用路径格式。
        /// </summary>
        private async Task<int> ExtractAndAddReferencedFilesAsync(string output)
        {
            int addedCount = 0;
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await Task.Run(() =>
            {
                try
                {
                    // 模式 1: MSBuild/编译器格式  path\to\file.ext(12,34): error CS0001: ...
                    var msbuildPattern = new System.Text.RegularExpressions.Regex(
                        @"([A-Za-z]:[^(\r\n]+?\.\w+)\s*\(\d+",
                        System.Text.RegularExpressions.RegexOptions.Multiline);

                    foreach (System.Text.RegularExpressions.Match match in msbuildPattern.Matches(output))
                    {
                        string candidate = match.Groups[1].Value.Trim();
                        if (System.IO.File.Exists(candidate) && seenPaths.Add(candidate))
                        {
                            Logger.Info($"[AddContext] 从调试输出提取文件: {candidate}");
                        }
                    }

                    // 模式 2: 绝对路径格式 (带盘符的常见扩展名)
                    var absPathPattern = new System.Text.RegularExpressions.Regex(
                        @"([A-Za-z]:[^\s""']+?\.(cs|vb|cpp|c|h|hpp|fs|py|js|ts|jsx|tsx|xaml|xml|json|config|csproj|vbproj|sql|md|txt|yml|yaml))\b",
                        System.Text.RegularExpressions.RegexOptions.Multiline);

                    foreach (System.Text.RegularExpressions.Match match in absPathPattern.Matches(output))
                    {
                        string candidate = match.Groups[1].Value.Trim();
                        if (System.IO.File.Exists(candidate) && seenPaths.Add(candidate))
                        {
                            Logger.Info($"[AddContext] 从调试输出提取文件: {candidate}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[AddContext] 提取关联文件失败: {ex.Message}");
                }
            });

            // 添加提取到的文件
            foreach (string filePath in seenPaths)
            {
                if (!_attachedFilePaths.Contains(filePath, StringComparer.OrdinalIgnoreCase))
                {
                    if (FileParserService.IsSupportedFormat(filePath))
                    {
                        _attachedFilePaths.Add(filePath);
                        addedCount++;
                    }
                    else
                    {
                        Logger.Info($"[AddContext] 跳过不支持的关联文件格式: {filePath}");
                    }
                }
            }

            if (addedCount > 0)
            {
                RefreshAttachedFilesUI();
            }

            return addedCount;
        }

        /// <summary>
        /// 从 Visual Studio 输出窗口捕获调试输出。
        /// 尝试读取"调试"窗格的内容，如果不可用则读取"生成"窗格。
        /// </summary>
        private async Task<string> CaptureDebugOutputAsync(EnvDTE80.DTE2 dte)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                // 通过 DTE2 的 ToolWindows.OutputWindow 获取输出窗格列表
                var panes = dte.ToolWindows.OutputWindow.OutputWindowPanes;
                var debugPaneNames = new[] { "调试", "Debug", "生成", "Build" };

                foreach (string paneName in debugPaneNames)
                {
                    try
                    {
                        var pane = panes.Item(paneName);
                        if (pane != null)
                        {
                            var textDoc = pane.TextDocument;
                            if (textDoc != null)
                            {
                                var content = textDoc.StartPoint.CreateEditPoint()
                                    .GetText(textDoc.EndPoint);
                                if (!string.IsNullOrWhiteSpace(content))
                                {
                                    Logger.Info($"[AddContext] 从输出窗格 \"{paneName}\" 获取内容，长度={content.Length}");
                                    return content;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // 窗格不存在则跳过
                    }
                }

                // 遍历所有窗格查找非空内容
                foreach (EnvDTE.OutputWindowPane pane in panes)
                {
                    try
                    {
                        var textDoc = pane.TextDocument;
                        if (textDoc != null)
                        {
                            var content = textDoc.StartPoint.CreateEditPoint()
                                .GetText(textDoc.EndPoint);
                            if (!string.IsNullOrWhiteSpace(content))
                            {
                                Logger.Info($"[AddContext] 从输出窗格 \"{pane.Name}\" 获取内容，长度={content.Length}");
                                return content;
                            }
                        }
                    }
                    catch
                    {
                        // 跳过无法访问的窗格
                    }
                }

                Logger.Info("[AddContext] 所有输出窗格均为空");
                return string.Empty;
            }
            catch (Exception ex)
            {
                Logger.Error($"捕获调试输出失败: {ex.Message}", ex);
                return string.Empty;
            }
        }

        /// <summary>
        /// 根据语言名称获取对应的文件扩展名。
        /// </summary>
        private static string GetFileExtensionForLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
                return "txt";

            return language.ToLowerInvariant() switch
            {
                "csharp" or "c#" => "cs",
                "vb" or "vb.net" or "basic" => "vb",
                "c/c++" or "c++" or "cpp" => "cpp",
                "c" => "c",
                "f#" or "fsharp" => "fs",
                "python" => "py",
                "javascript" or "js" => "js",
                "typescript" or "ts" => "ts",
                "html" or "htmlx" => "html",
                "css" => "css",
                "xml" or "xaml" => "xml",
                "json" => "json",
                "sql" => "sql",
                "java" => "java",
                "go" => "go",
                "rust" => "rs",
                "powershell" or "ps1" => "ps1",
                "shell" or "bash" => "sh",
                "markdown" => "md",
                "yaml" or "yml" => "yml",
                _ => "txt"
            };
        }

        #endregion

        #region Event Handlers - Skill Suggestions

        /// <summary>
        /// 输入框文本变更：检测 / 触发 Skill 自动补全、检测 @ 触发 Agent 路由自动补全。
        /// </summary>
        private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                // ── 占位提示文字显隐 ──
                UpdatePlaceholderVisibility();

                // ── @ / token 蓝色高亮同步（内容变化即重算，内部按内容去重） ──
                UpdateInputHighlight();

                // 判定依据是「光标所在的当前 token」而非整段文本。
                // 发送侧支持 "@agent /skill 参数" 组合输入，若沿用整段行首锚定，
                // 则 "@ask " 的尾随空格会让 / 菜单永久无法打开。
                var currentToken = GetCurrentToken(out _, out _);

                // ── @ 路由 token 检测 ──
                if (currentToken.StartsWith("@", StringComparison.Ordinal))
                {
                    // 关闭 Skill 弹出框（同一时刻只服务光标所在的一个 token）
                    if (SkillSuggestionPopup.IsOpen)
                        SkillSuggestionPopup.IsOpen = false;

                    // 提取 @ 后的文本用于过滤
                    var agentFilterText = currentToken.Length > 1 ? currentToken.Substring(1).ToLowerInvariant() : string.Empty;
                    UpdateAgentSuggestions(agentFilterText);
                    return;
                }

                // 关闭 Agent 弹出框（当前 token 不是 @ 路由）
                if (AgentSuggestionPopup.IsOpen)
                    AgentSuggestionPopup.IsOpen = false;

                // ── / 斜杠命令 token 检测 ──
                if (!currentToken.StartsWith("/", StringComparison.Ordinal))
                {
                    if (SkillSuggestionPopup.IsOpen)
                        SkillSuggestionPopup.IsOpen = false;
                    return;
                }

                // 提取 / 后的文本用于过滤
                var filterText = currentToken.Length > 1 ? currentToken.Substring(1).ToLowerInvariant() : string.Empty;
                UpdateSkillSuggestions(filterText);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Skill] 文本变更处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取输入框中光标所在的当前 token（以空白字符为分隔）。
        /// 用光标回溯而非整段锚定，是因为发送侧支持 "@agent /skill 参数" 组合输入，
        /// 需要让 @ 与 / 各自针对光标所在片段独立触发补全。
        /// </summary>
        /// <param name="tokenStart">输出参数：当前 token 在文本中的起始索引（含）。</param>
        /// <param name="tokenEnd">输出参数：当前 token 的结束索引（不含），即光标位置。</param>
        /// <returns>光标所在的当前 token 文本；文本为空或光标越界时返回空字符串。</returns>
        private string GetCurrentToken(out int tokenStart, out int tokenEnd)
        {
            var text = InputTextBox.Text ?? string.Empty;

            // 光标越界时收敛到文本末尾，避免 Substring 抛出越界异常
            var caret = InputTextBox.CaretIndex;
            if (caret < 0)
                caret = 0;
            if (caret > text.Length)
                caret = text.Length;

            // 从光标向前回溯到最近的空白字符，确定当前 token 的起点
            tokenStart = caret;
            while (tokenStart > 0 && !char.IsWhiteSpace(text[tokenStart - 1]))
                tokenStart--;

            tokenEnd = caret;
            return text.Substring(tokenStart, caret - tokenStart);
        }

        /// <summary>
        /// 替换输入框中光标所在的当前 token，并保留 token 之后的原有文本。
        /// 供 @Agent 与 /Skill 两条补全路径复用，确保插入行为（尾随空格、光标位置、
        /// 后续文本保留）完全一致，避免两条路径各自实现导致语义分叉。
        /// </summary>
        /// <param name="replacement">替换文本；调用方需自行包含便于继续输入的尾随空格。</param>
        private void ReplaceCurrentToken(string replacement)
        {
            var text = InputTextBox.Text ?? string.Empty;

            // 先取当前 token 范围再改写文本：赋值 Text 会触发 TextChanged，之后光标位置不再可靠
            GetCurrentToken(out var tokenStart, out var tokenEnd);

            // 保留光标之后的原有文本，避免覆盖 "@ask /review" 这类组合输入的后半段
            var tail = tokenEnd < text.Length ? text.Substring(tokenEnd) : string.Empty;

            InputTextBox.Text = text.Substring(0, tokenStart) + replacement + tail;

            // 光标落在替换文本之后、原有尾段之前，便于用户继续输入
            InputTextBox.CaretIndex = tokenStart + replacement.Length;
            InputTextBox.Focus();
        }

        /// <summary>
        /// 更新 Skill 建议列表。
        /// </summary>
        private void UpdateSkillSuggestions(string filterText)
        {
            try
            {
                if (_skillDiscoveryResult == null)
                {
                    // 后台异步加载技能缓存，不阻塞 UI 线程。
                    // InitializeSkillsAsync() 已在启动时加载当前解决方案技能，此处为兜底路径。
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await DiscoverSkillsForCurrentSolutionAsync();
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"[Skill] 后台技能发现失败: {ex.Message}");
                        }
                    });
                    // 首次无缓存时关闭弹出框，下次输入时将命中缓存
                    if (SkillSuggestionPopup.IsOpen)
                        SkillSuggestionPopup.IsOpen = false;
                    return;
                }

                var allSkills = _skillDiscoveryResult?.UserInvocableSkills ?? new List<SkillDefinition>();

                // 添加内置元命令
                var L = LocalizationService.Instance;
                var metaCommands = new List<SkillSuggestionItem>
                {
                    new SkillSuggestionItem
                    {
                        Name = "help",
                        Description = L["popup.skillDesc.help"],
                        Source = L["popup.skillSource.builtin"],
                        IsMeta = true,
                    },
                    new SkillSuggestionItem
                    {
                        Name = "create-skill",
                        Description = L["popup.skillDesc.createSkill"],
                        Source = L["popup.skillSource.builtin"],
                        IsMeta = true,
                    },
                    new SkillSuggestionItem
                    {
                        Name = "refresh-skills",
                        Description = L["popup.skillDesc.refreshSkills"],
                        Source = L["popup.skillSource.builtin"],
                        IsMeta = true,
                    },
                };

                // 添加用户/项目技能
                var skillItems = allSkills
                    .Select(s => new SkillSuggestionItem
                    {
                        Name = s.Name,
                        Description = s.AlwaysInject
                            ? $"{L["popup.skillDesc.alwaysInject"]} - {s.Description}"
                            : s.Description,
                        Source = s.Source switch
                        {
                            SkillSource.Project => L["popup.skillSource.project"],
                            SkillSource.User => L["popup.skillSource.user"],
                            SkillSource.BuiltIn => L["popup.skillSource.package"],
                            _ => ""
                        },
                        IsMeta = false,
                        SkillDefinition = s,
                    })
                    .ToList();

                var allItems = metaCommands.Concat(skillItems).ToList();

                // 按过滤文本筛选。filterText 是光标前的单个 token，不再可能含空格，
                // 因此无需再拆分"命令 + 参数"。
                if (!string.IsNullOrEmpty(filterText))
                {
                    allItems = allItems
                        .Where(item => item.Name.StartsWith(filterText, StringComparison.OrdinalIgnoreCase)
                                       || item.Description.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();
                }

                // 更新 ListBox
                SkillSuggestionListBox.ItemsSource = allItems;

                if (allItems.Count > 0)
                {
                    SkillSuggestionListBox.SelectedIndex = 0;
                    SkillSuggestionPopup.IsOpen = true;

                    // 动态调整弹出框宽度
                    var maxNameLen = allItems.Max(item => item.Name.Length);
                    // 不显式设置宽度，让 WPF 自动布局
                }
                else
                {
                    SkillSuggestionPopup.IsOpen = false;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Skill] 更新建议列表失败: {ex.Message}");
                SkillSuggestionPopup.IsOpen = false;
            }
        }

        /// <summary>
        /// 导航 Skill 建议列表（上下键）。
        /// </summary>
        private void NavigateSkillSuggestion(int direction)
        {
            if (!SkillSuggestionPopup.IsOpen || SkillSuggestionListBox.Items.Count == 0)
                return;

            var newIndex = SkillSuggestionListBox.SelectedIndex + direction;
            if (newIndex < 0)
                newIndex = SkillSuggestionListBox.Items.Count - 1;
            else if (newIndex >= SkillSuggestionListBox.Items.Count)
                newIndex = 0;

            SkillSuggestionListBox.SelectedIndex = newIndex;
            SkillSuggestionListBox.ScrollIntoView(SkillSuggestionListBox.SelectedItem);
        }

        /// <summary>
        /// 接受当前选中的 Skill 建议（Enter/Tab 键）。
        /// </summary>
        private void AcceptSkillSuggestion()
        {
            if (!SkillSuggestionPopup.IsOpen || SkillSuggestionListBox.SelectedItem == null)
                return;

            if (SkillSuggestionListBox.SelectedItem is SkillSuggestionItem item)
            {
                // 替换输入框文本为 /skill-name
                var skillName = item.Name;

                // ── 日志：记录用户从弹出框选择了技能 ──
                if (item.IsMeta)
                    Logger.Info($"[Skill] 用户从弹出框选择元命令: /{skillName}");
                else
                    Logger.Info($"[Skill] 用户从弹出框选择技能: /{skillName} | 描述: {item.Description} | 来源: {item.Source}");

                // 统一插入 "/skill-name "（含尾随空格）：与 @Agent 补全行为对齐，
                // 用户选完即可直接输入参数；ArgumentHint 仅作界面展示，不写入输入框以免污染命令。
                ReplaceCurrentToken($"/{skillName} ");
            }

            SkillSuggestionPopup.IsOpen = false;
        }

        /// <summary>
        /// ListBox 选中项变更。
        /// </summary>
        private void SkillSuggestionListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 预留：选中项变更时的处理
        }

        /// <summary>
        /// ListBox 键盘事件：Enter/Tab 接受选择，Escape 关闭弹出框。
        /// 解决鼠标点击选中后按 Enter 无反应的问题。
        /// </summary>
        private void SkillSuggestionListBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                e.Handled = true;
                AcceptSkillSuggestion();
                return;
            }
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                SkillSuggestionPopup.IsOpen = false;
                InputTextBox.Focus();
                return;
            }
            if (e.Key == Key.Up || e.Key == Key.Down)
            {
                // 让 ListBox 默认处理导航
                e.Handled = false;
            }
        }

        /// <summary>
        /// ListBox 双击选中技能。
        /// </summary>
        private void SkillSuggestionListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            AcceptSkillSuggestion();
        }

        #endregion

        #region Event Handlers - Agent Suggestions

        /// <summary>
        /// 更新 Agent 路由建议列表。
        /// 根据 @ 后的输入文本过滤可用的 Agent。
        /// </summary>
        private void UpdateAgentSuggestions(string filterText)
        {
            try
            {
                var L = LocalizationService.Instance;
                var agents = new List<AgentSuggestionItem>
                {
                    new AgentSuggestionItem
                    {
                        Name = "ask",
                        Icon = "",
                        Description = L["popup.agentDesc.ask"],
                        ArgumentHint = L["popup.agentHint.ask"],
                        AgentType = AgentType.Ask,
                    },
                    new AgentSuggestionItem
                    {
                        Name = "explore",
                        Icon = "",
                        Description = L["popup.agentDesc.explore"],
                        ArgumentHint = L["popup.agentHint.explore"],
                        AgentType = AgentType.Explore,
                    },
                    new AgentSuggestionItem
                    {
                        Name = "plan",
                        Icon = "",
                        Description = L["popup.agentDesc.plan"],
                        ArgumentHint = L["popup.agentHint.plan"],
                        AgentType = AgentType.Plan,
                    },
                    new AgentSuggestionItem
                    {
                        Name = "edit",
                        Icon = "",
                        Description = L["popup.agentDesc.edit"],
                        ArgumentHint = L["popup.agentHint.edit"],
                        AgentType = AgentType.Edit,
                    },
                    new AgentSuggestionItem
                    {
                        Name = "build",
                        Icon = "",
                        Description = L["popup.agentDesc.build"],
                        ArgumentHint = L["popup.agentHint.build"],
                        AgentType = AgentType.Build,
                    },
                };

                // 按过滤文本筛选
                if (!string.IsNullOrEmpty(filterText))
                {
                    agents = agents
                        .Where(a => a.Name.StartsWith(filterText, StringComparison.OrdinalIgnoreCase)
                                    || a.Description.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();
                }

                // 更新 ListBox
                AgentSuggestionListBox.ItemsSource = agents;

                if (agents.Count > 0)
                {
                    AgentSuggestionListBox.SelectedIndex = 0;
                    AgentSuggestionPopup.IsOpen = true;
                }
                else
                {
                    AgentSuggestionPopup.IsOpen = false;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Agent] 更新建议列表失败: {ex.Message}");
                AgentSuggestionPopup.IsOpen = false;
            }
        }

        /// <summary>
        /// 导航 Agent 建议列表（上下键）。
        /// </summary>
        private void NavigateAgentSuggestion(int direction)
        {
            if (!AgentSuggestionPopup.IsOpen || AgentSuggestionListBox.Items.Count == 0)
                return;

            var newIndex = AgentSuggestionListBox.SelectedIndex + direction;
            if (newIndex < 0)
                newIndex = AgentSuggestionListBox.Items.Count - 1;
            else if (newIndex >= AgentSuggestionListBox.Items.Count)
                newIndex = 0;

            AgentSuggestionListBox.SelectedIndex = newIndex;
            AgentSuggestionListBox.ScrollIntoView(AgentSuggestionListBox.SelectedItem);
        }

        /// <summary>
        /// 接受当前选中的 Agent 建议（Enter/Tab 键）。
        /// 将输入框文本替换为 @agent-name 格式。
        /// </summary>
        private void AcceptAgentSuggestion()
        {
            if (!AgentSuggestionPopup.IsOpen || AgentSuggestionListBox.SelectedItem == null)
                return;

            if (AgentSuggestionListBox.SelectedItem is AgentSuggestionItem item)
            {
                var agentName = item.Name;

                Logger.Info($"[Agent] 用户从弹出框选择 Agent: @{agentName} ({item.Description})");

                // 只替换光标所在的 token，不再整段覆盖：发送侧支持 "@agent /skill 参数" 组合输入，
                // 整段覆盖会丢弃用户已输入的 /skill 等内容。尾随空格保留，便于继续输入。
                ReplaceCurrentToken($"@{agentName} ");
            }

            AgentSuggestionPopup.IsOpen = false;
        }

        /// <summary>
        /// Agent ListBox 键盘事件：Enter/Tab 接受选择，Escape 关闭弹出框。
        /// </summary>
        private void AgentSuggestionListBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                e.Handled = true;
                AcceptAgentSuggestion();
                return;
            }
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                AgentSuggestionPopup.IsOpen = false;
                InputTextBox.Focus();
                return;
            }
            if (e.Key == Key.Up || e.Key == Key.Down)
            {
                // 让 ListBox 默认处理导航
                e.Handled = false;
            }
        }

        /// <summary>
        /// Agent ListBox 双击选中 Agent。
        /// </summary>
        private void AgentSuggestionListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            AcceptAgentSuggestion();
        }

        /// <summary>
        /// Agent ListBox 选中项变更。
        /// </summary>
        private void AgentSuggestionListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 预留：选中项变更时的处理
        }

        #endregion

        #region Event Handlers - LostFocus & Placeholder

        /// <summary>
        /// 输入框获得焦点时隐藏占位提示文字。
        /// </summary>
        private void InputTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            UpdatePlaceholderVisibility();
        }

        /// <summary>
        /// 输入框失去焦点时关闭弹出框（延迟关闭，允许点击 ListBox 项）。
        /// </summary>
        private async void InputTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            // 延迟关闭，确保 ListBox 的 MouseDoubleClick 能先触发
            await System.Threading.Tasks.Task.Delay(200);
            if (SkillSuggestionPopup.IsOpen && !SkillSuggestionListBox.IsKeyboardFocusWithin)
            {
                SkillSuggestionPopup.IsOpen = false;
            }
            if (AgentSuggestionPopup.IsOpen && !AgentSuggestionListBox.IsKeyboardFocusWithin)
            {
                AgentSuggestionPopup.IsOpen = false;
            }
            UpdatePlaceholderVisibility();
        }

        /// <summary>
        /// 更新输入框占位提示文字的可见性。
        /// 规则：输入框为空 且 无焦点 → 显示；否则隐藏。
        /// </summary>
        private void UpdatePlaceholderVisibility()
        {
            if (InputPlaceholder == null) return;
            bool isEmpty = string.IsNullOrEmpty(InputTextBox.Text);
            bool isFocused = InputTextBox.IsFocused;
            InputPlaceholder.Visibility = (isEmpty && !isFocused) ? Visibility.Visible : Visibility.Collapsed;
        }

        #endregion

        #region Event Handlers - Session & Model

        private void SessionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSessionSelection) return; // 程序化填充期间忽略
            if (SessionComboBox.SelectedItem is ChatSession session && session != _activeSession)
            {
                SwitchToSession(session);
            }
        }

        private void DeleteSessionButton_Click(object sender, RoutedEventArgs e)
        {
            // ── 删除前确认 ──
            string confirmMsg = LocalizationService.Instance["chat.confirmDeleteConversation"];
            if (string.IsNullOrEmpty(confirmMsg))
                confirmMsg = LocalizationService.Instance["status.deleteConfirm"];

            var result = System.Windows.MessageBox.Show(
                confirmMsg,
                LocalizationService.Instance["chat.deleteConversation"] ?? "删除对话",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);

            if (result == System.Windows.MessageBoxResult.Yes)
            {
                DeleteCurrentSession();
            }
        }

        private void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            CreateNewChat();
        }

        private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRefreshingCoreControls) return;

            if (_apiService != null && ModelComboBox.SelectedItem is ModelListItem item)
            {
                // ── 回写到选项页并持久化，确保 Tools→Options 和重启后生效 ──
                if (_options != null)
                {
                    // 按条目来源路由：官方条目 → SelectedModel + official 来源；
                    // 自定义条目 → ActiveCustomModel + custom 来源，不改动模型列表。
                    bool isCustomEntry = item.Source == ModelListItem.EntrySource.Custom;
                    var targetSource = isCustomEntry ? "custom" : "official";
                    var currentModel = isCustomEntry ? _options.ActiveCustomModel : _options.SelectedModel;

                    bool sourceChanged = !string.Equals(_options.ActiveModelSource, targetSource, StringComparison.Ordinal);
                    bool modelChanged = !string.Equals(currentModel, item.Model, StringComparison.Ordinal);
                    if (sourceChanged || modelChanged)
                    {
                        if (isCustomEntry)
                            _options.ActiveCustomModel = item.Model;
                        else
                            _options.SelectedModel = item.Model;
                        _options.ActiveModelSource = targetSource;
                        try { _options.SaveSettingsToStorage(); } catch { /* 非关键路径 */ }
                        UnifiedSettingsSync.PushFromPage(_options);
                    }

                    // 下拉框切换不只是换模型名：来源切换时必须同步 Base URL、API Key 与视觉标记。
                    var config = DeepSeekEndpointResolver.Resolve(_options);
                    _apiService.UpdateEndpoint(config);
                    UpdateEndpointCapabilityControls();
                    ApplyContextBudgetFromSettings(config.Model);

                    // 模型/来源切换影响 capture_window 等工具可见性 → 使 Agent 完整工具集缓存失效
                    _agentFactory?.InvalidateFullToolSetCache();
                    Logger.Info($"模型端点切换为: source={targetSource}, baseUrl={_apiService.BaseUrl}, model={config.Model}");

                    // 官方 ↔ 自定义来源切换会改变联网搜索的下拉项（DeepSeek 官方仅在官方来源提供）
                    // 与官方 Search / Fetch 的可用性，必须重新按新来源刷新搜索配置。
                    if (sourceChanged)
                    {
                        RefreshWebSearchFromSettings();

                        // 用户此前在自定义端点下偏好过 DeepSeek 引擎时，列表里没有对应项，
                        // 这里把偏好纠正为官方默认引擎，避免保存值与实际显示长期不一致。
                        if (_options != null && !string.Equals(_options.SearchProvider, _webSearchEngine, StringComparison.Ordinal))
                        {
                            _options.SearchProvider = _webSearchEngine;
                            try { _options.SaveSettingsToStorage(); } catch { /* 非关键路径 */ }
                            UnifiedSettingsSync.PushFromPage(_options);
                        }
                    }
                }
                Logger.Info($"模型切换为: {item.Display}");
                RecordRuntimeSettingsApplied();
            }
        }

        private void ApprovalModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRefreshingCoreControls) return;

            if (ApprovalModeComboBox.SelectedValue is Models.ApprovalMode mode)
            {
                // 同步缓存，供后台线程快速判断审批模式
                _cachedApprovalMode = mode;

                // 持久化到设置
                string modeValue = mode switch
                {
                    Models.ApprovalMode.BlockAll => "BlockAll",
                    Models.ApprovalMode.AllowAll => "AllowAll",
                    _ => "SmartBlock",
                };
                if (_options != null && _options.ApprovalMode != modeValue)
                {
                    _options.ApprovalMode = modeValue;
                    // 立即写入存储，确保重启 VS 后仍然生效
                    try { _options.SaveSettingsToStorage(); } catch { /* 非关键路径 */ }
                    UnifiedSettingsSync.PushFromPage(_options);
                }
                Logger.Info($"审批模式切换为: {mode}");
                RecordRuntimeSettingsApplied();
            }
        }

        private void ThinkingCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isRefreshingCoreControls) return;

            if (_apiService != null)
            {
                bool enabled = ThinkingCheckBox.IsChecked == true;
                string effort = EffortComboBox.SelectedItem as string ?? "high";
                _apiService.ConfigureThinking(enabled, effort);
                if (_options != null && _options.IsThinkingEnabled != enabled)
                {
                    _options.IsThinkingEnabled = enabled;
                    try { _options.SaveSettingsToStorage(); } catch { /* 非关键路径 */ }
                    UnifiedSettingsSync.PushFromPage(_options);
                }
                Logger.Info($"思考模式: {(enabled ? "启用" : "禁用")}, 强度: {effort}");
                RecordRuntimeSettingsApplied();
            }
        }

        // TODO: RagCheckBox 暂时注释掉，功能未完善
        /*
        private void RagCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            bool enabled = RagCheckBox.IsChecked == true;
            if (_ragService != null)
                _ragService.IsEnabled = enabled;
            Logger.Info($"代码索引: {(enabled ? "启用" : "禁用")}");
        }
        */

        private void EffortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRefreshingCoreControls) return;

            if (_apiService != null && EffortComboBox.SelectedItem is string effort)
            {
                bool enabled = ThinkingCheckBox.IsChecked == true;
                _apiService.ConfigureThinking(enabled, effort);
                // 持久化到设置
                if (_options != null && _options.ReasoningEffort != effort)
                {
                    _options.ReasoningEffort = effort;
                    // 立即写入存储，确保重启 VS 后仍然生效
                    try { _options.SaveSettingsToStorage(); } catch { /* 非关键路径 */ }
                    UnifiedSettingsSync.PushFromPage(_options);
                }
                Logger.Info($"推理强度切换为: {effort}");
                RecordRuntimeSettingsApplied();
            }
        }

        #endregion

        #region Event Handlers - Web Search

        /// <summary>
        /// 鼠标悬停到会话消耗标签时刷新 ToolTip，确保显示的是最新调用次数与费用。
        /// 计费说明涉及官方 Search / Fetch 的按次计费口径，悬停时才计算可避免无谓刷新。
        /// </summary>
        private void BalanceLabel_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
            => UpdateSessionConsumptionToolTip();

        /// <summary>
        /// 联网搜索开关按钮点击：切换开启/关闭。
        /// 开关只决定 web_search 工具是否加入工具集，开启后由模型按需调用，不再固定每轮搜索。
        /// </summary>
        private void WebSearchToggleButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool enable = _webSearchEngine == "Off";
                if (_webSearchEngine == "Off")
                {
                    // 使用 ComboBox 当前选择的引擎 Id，而非硬编码 DuckDuckGo。
                    // 下拉项在端点来源切换后会重建，只有 Id 与显示文本解耦才不会错位。
                    string newEngine = GetSelectedSearchEngineId() ?? "DuckDuckGo";
                    _webSearchEngine = newEngine;
                    // 同步 ComboBox 选中项到当前引擎（确保 UI 一致）
                    int idx = IndexOfSearchEngine(newEngine);
                    if (idx >= 0)
                        WebSearchEngineComboBox.SelectedIndex = idx;
                }
                else
                {
                    _webSearchEngine = "Off";
                }

                if (_options != null && _options.EnableWebSearch != enable)
                {
                    _options.EnableWebSearch = enable;
                    try { _options.SaveSettingsToStorage(); } catch { /* 非关键路径 */ }
                    UnifiedSettingsSync.PushFromPage(_options);
                }

                ApplyWebSearchConfig();
                RefreshWebSearchToolRegistration();
                Logger.Info($"联网搜索状态切换为: {_webSearchEngine}");
                UpdateWebSearchToggleAppearance();

                // 提示百度/Bing 未配置 Key 的情况
                if (_webSearchEngine == "Baidu" && (_options == null || string.IsNullOrWhiteSpace(_options.BaiduApiKey)))
                {
                    StatusLabel.Text = LocalizationService.Instance["status.search.baiduKeyRequired"];
                }
                else if (_webSearchEngine == "Bing" && (_options == null || string.IsNullOrWhiteSpace(_options.BingApiKey)))
                {
                    StatusLabel.Text = LocalizationService.Instance["status.search.bingKeyRequired"];
                }
                else
                {
                    StatusLabel.Text = string.Empty;
                }

                RecordRuntimeSettingsApplied();
            }
            catch (Exception ex)
            {
                Logger.Error($"WebSearchToggleButton_Click 异常: {ex.Message}", ex);
            }
        }
        /// <summary>
        /// MCP 配置按钮点击：打开 MCP 服务器配置对话框。
        /// </summary>
        private void McpConfigButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 从文件加载当前配置
                var currentConfigs = McpConfigStore.Load();

                var dialog = new McpConfigDialog(currentConfigs, savedServers =>
                {
                    // 保存到文件
                    McpConfigStore.Save(savedServers);
                    Logger.Info($"[MCP Config] 已保存 {savedServers.Count} 个 MCP 服务器配置");
                });

                dialog.Owner = Window.GetWindow(this);
                dialog.ShowDialog();

                // 重新初始化 MCP 连接
                InitializeMcp();

                // 刷新 MCP 按钮状态
                UpdateMcpButtonAppearance();
            }
            catch (Exception ex)
            {
                Logger.Error($"McpConfigButton_Click 异常: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 更新 MCP 配置按钮的外观，反映连接状态。
        /// </summary>
        private void UpdateMcpButtonAppearance()
        {
            try
            {
                var L = LocalizationService.Instance;
                if (_mcpManager == null || _mcpManager.AllTools.Count == 0)
                {
                    McpConfigButton.ToolTip = L["input.mcpNotConnected"];
                    McpConfigButton.Foreground = new SolidColorBrush(
                        Color.FromRgb(0x88, 0x88, 0x88));
                }
                else
                {
                    int toolCount = _mcpManager.AllTools.Count;
                    McpConfigButton.ToolTip = L.Format("input.mcpConnected", toolCount);
                    McpConfigButton.Foreground = new SolidColorBrush(
                        Color.FromRgb(0x4E, 0xC9, 0xB0));
                }
            }
            catch (Exception ex)
            {
                Logger.Info($"[MCP] 更新按钮外观失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 根据当前搜索引擎更新切换按钮的外观和 ToolTip。
        /// </summary>
        private void UpdateWebSearchToggleAppearance()
        {
            // 主题应用阶段也会调用此方法，需容忍控件尚未就绪。
            if (WebSearchToggleButton == null || WebSearchEngineComboBox == null) return;

            bool isOn = _webSearchEngine != "Off";
            var L = LocalizationService.Instance;
            if (isOn)
            {
                WebSearchToggleButton.Foreground = new SolidColorBrush(
                    Color.FromRgb(0x6C, 0xAF, 0xD9));
                WebSearchToggleButton.ToolTip = L["input.webSearchOn"];
            }
            else
            {
                WebSearchToggleButton.Foreground = new SolidColorBrush(
                    Color.FromRgb(0x88, 0x88, 0x88));
                WebSearchToggleButton.ToolTip = L["input.webSearchOff"];
            }

            // 下拉框可见性：开启时显示，关闭时隐藏
            WebSearchEngineComboBox.Visibility = isOn ? Visibility.Visible : Visibility.Collapsed;
        }


        /// <summary>
        /// 联网搜索引擎选择变更事件。仅在搜索已开启时生效，搜索关闭时仅记录偏好。
        /// </summary>
        private void WebSearchEngineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (_isRefreshingCoreControls) return;
                if (WebSearchEngineComboBox.SelectedIndex < 0) return;

                // 搜索关闭时，ComboBox 仅作为偏好存储，不切换引擎
                if (_webSearchEngine == "Off") return;

                string? newEngine = GetSelectedSearchEngineId();
                if (newEngine == null) return;

                if (_webSearchEngine == newEngine) return; // 避免循环触发

                _webSearchEngine = newEngine;
                if (_options != null && _options.SearchProvider != newEngine)
                {
                    _options.SearchProvider = newEngine;
                    try { _options.SaveSettingsToStorage(); } catch { /* 非关键路径 */ }
                    UnifiedSettingsSync.PushFromPage(_options);
                }

                Logger.Info($"联网搜索引擎切换为: {_webSearchEngine}");
                UpdateWebSearchToggleAppearance();
                ApplyWebSearchConfig();

                if (_webSearchEngine == "Baidu" && (_options == null || string.IsNullOrWhiteSpace(_options.BaiduApiKey)))
                {
                    StatusLabel.Text = LocalizationService.Instance["status.search.baiduKeyRequired"];
                }
                else if (_webSearchEngine == "Bing" && (_options == null || string.IsNullOrWhiteSpace(_options.BingApiKey)))
                {
                    StatusLabel.Text = LocalizationService.Instance["status.search.bingKeyRequired"];
                }

                RecordRuntimeSettingsApplied();
            }
            catch (Exception ex)
            {
                Logger.Error($"WebSearchEngineComboBox_SelectionChanged 异常: {ex.Message}", ex);
            }
        }

        #endregion

        #region Event Handlers - WebView2

        /// <summary>
        /// WebView2 新窗口请求事件：拦截 target='_blank' / window.open 链接，
        /// 一律在系统默认浏览器中打开，不在 WebView2 内新建窗口。
        /// </summary>
        private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true; // 阻止 WebView2 内部打开
            OpenUriInSystemBrowser(e.Uri);
        }

        /// <summary>
        /// WebView2 导航请求拦截：
        /// - vs-navigate:// 自定义协议：文件名/符号名跳转，交宿主处理；
        /// - 用户点击的网页链接：取消 WebView2 内导航，改用系统默认浏览器打开，
        ///   避免点击回答里的引用链接后整个聊天页面被导航走；
        /// - 其它真实导航（页面重建等）：放行，并屏蔽缩放持久化以便完成后恢复缩放。
        /// </summary>
        private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Uri)) return;

            // ── 只拦截 vs-navigate:// 自定义协议 ──
            if (e.Uri.StartsWith("vs-navigate://", StringComparison.OrdinalIgnoreCase))
            {
                _suppressWebViewZoomPersistence = false;
                e.Cancel = true; // 阻止 WebView2 导航

                try
                {
                    Uri uri = new Uri(e.Uri);
                    var queryParams = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    string? name = System.Net.WebUtility.UrlDecode(queryParams["name"]);
                    string? line = System.Net.WebUtility.UrlDecode(queryParams["line"]);

                    if (string.IsNullOrEmpty(name)) return;

                    if (uri.Host == "file")
                    {
                        _ = NavigateToFileAsync(name, line);
                    }
                    else if (uri.Host == "symbol")
                    {
                        _ = NavigateToSymbolAsync(name);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[Nav] vs-navigate 拦截失败 ({e.Uri}): {ex.Message}", ex);
                }

                return;
            }

            // ── 用户点击的网页链接：不在 WebView2 内导航，交给系统默认浏览器 ──
            if (e.IsUserInitiated && IsExternalLinkUri(e.Uri))
            {
                e.Cancel = true;
                OpenUriInSystemBrowser(e.Uri);
                return;
            }

            // ── 真实导航会重置用户缩放，先屏蔽持久化事件，导航完成后恢复保存的比例 ──
            _suppressWebViewZoomPersistence = true;
        }

        /// <summary>
        /// 判断是否为应交给系统默认程序打开的网页链接（http/https/mailto）。
        /// 内部协议（vs-navigate://、about:、data:、file:）与页内锚点都不算外部链接。
        /// </summary>
        internal static bool IsExternalLinkUri(string? uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return false;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return false;

            return parsed.Scheme switch
            {
                "http" or "https" or "mailto" => true,
                _ => false,
            };
        }

        /// <summary>用系统默认浏览器/程序打开指定 URI。</summary>
        private static void OpenUriInSystemBrowser(string uri)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = uri,
                    UseShellExecute = true,
                });
                Logger.Info($"在外部浏览器打开: {uri}");
            }
            catch (Exception ex)
            {
                Logger.Error($"打开外部浏览器失败 ({uri}): {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 导航到工作区中的指定文件（通过文件名搜索并打开），支持行号和路径前缀。
        /// </summary>
        private async Task NavigateToFileAsync(string fileName, string? lineInfo = null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                // ── 解析行号信息 ──
                int targetLine = 0;
                int targetEndLine = 0;
                if (!string.IsNullOrEmpty(lineInfo))
                {
                    var dashIdx = lineInfo.IndexOf('-');
                    if (dashIdx > 0)
                    {
                        int.TryParse(lineInfo.Substring(0, dashIdx), out targetLine);
                        int.TryParse(lineInfo.Substring(dashIdx + 1), out targetEndLine);
                    }
                    else
                    {
                        int.TryParse(lineInfo, out targetLine);
                    }
                }

                // ── 获取工作区根目录 ──
                string? slnPath = _solutionPath;
                if (string.IsNullOrEmpty(slnPath) || (!File.Exists(slnPath) && !Directory.Exists(slnPath)))
                {
                    Logger.Warn($"[Nav] 无法导航到文件 {fileName}: 解决方案路径无效");
                    return;
                }

                string workspaceRoot = File.Exists(slnPath)
                    ? Path.GetDirectoryName(slnPath) ?? slnPath
                    : slnPath;

                // ── 文件搜索策略：逐级路径后缀匹配，优先精确路径 ──
                string? filePath = null;

                // 策略0：绝对路径直接使用（支持工作区外的文件，如用户级 skill）
                if (Path.IsPathRooted(fileName) && File.Exists(fileName))
                {
                    filePath = fileName;
                }

                // 策略1：直接拼接工作区根目录（处理如 src/db_engine.cpp 的路径）
                if (filePath == null)
                {
                    string directPath = Path.Combine(workspaceRoot, fileName.Replace('/', '\\'));
                    if (File.Exists(directPath))
                    {
                        filePath = directPath;
                    }
                }

                // 策略2：递归搜索文件名（忽略路径前缀）
                if (filePath == null)
                {
                    string searchPattern = Path.GetFileName(fileName);
                    var matches = await Task.Run(() =>
                        Directory.GetFiles(workspaceRoot, searchPattern, SearchOption.AllDirectories)
                            .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\")
                                && !f.Contains("\\.git\\") && !f.Contains("\\node_modules\\"))
                            .ToList());

                    if (matches.Count > 0)
                    {
                        // 优先选择路径后缀最匹配的文件
                        string normalizedName = fileName.Replace('/', '\\');
                        filePath = matches
                            .OrderBy(f => GetPathSuffixMatchLength(f, normalizedName))
                            .ThenBy(f => f.Length) // 短路径优先
                            .Last(); // 最长后缀匹配优先
                    }
                }

                if (filePath == null)
                {
                    Logger.Warn($"[Nav] 未找到文件: {fileName} in {workspaceRoot}");
                    return;
                }

                // 通过 VS SDK 打开文件
                var openDoc = await ServiceProvider.GetGlobalServiceAsync(
                    typeof(Microsoft.VisualStudio.Shell.Interop.SVsUIShellOpenDocument))
                    as Microsoft.VisualStudio.Shell.Interop.IVsUIShellOpenDocument;

                if (openDoc != null)
                {
                    var logicalView = Microsoft.VisualStudio.VSConstants.LOGVIEWID_Primary;
                    openDoc.OpenDocumentViaProject(
                        filePath, ref logicalView,
                        out _, out _, out _,
                        out Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame frame);
                    frame?.Show();
                    Logger.Info($"[Nav] 打开文件: {filePath}" +
                        (targetLine > 0 ? $" (行 {targetLine}{(targetEndLine > 0 ? $"-{targetEndLine}" : "")})" : ""));
                }

                // ── 跳转到指定行 ──
                if (targetLine > 0)
                {
                    // 短暂延迟等待文档加载
                    await Task.Delay(200);

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    try
                    {
                        var textManager = Package.GetGlobalService(
                            typeof(Microsoft.VisualStudio.TextManager.Interop.SVsTextManager))
                            as Microsoft.VisualStudio.TextManager.Interop.IVsTextManager;

                        if (textManager != null)
                        {
                            Microsoft.VisualStudio.TextManager.Interop.IVsTextView? textView;
                            textManager.GetActiveView(1, null, out textView);
                            if (textView != null)
                            {
                                int line = Math.Max(0, targetLine - 1);
                                int col = 0;
                                textView.SetCaretPos(line, col);
                                textView.CenterLines(line, Math.Max(1, (targetEndLine > 0 ? targetEndLine - targetLine + 1 : 5)));
                                textView.SendExplicitFocus();
                                Logger.Info($"[Nav] 跳转到行 {targetLine}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[Nav] 行跳转失败: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Nav] 导航到文件失败 ({fileName}): {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 计算文件路径与目标路径后缀的匹配长度（越长表示路径越精确匹配）。
        /// </summary>
        private static int GetPathSuffixMatchLength(string filePath, string targetPath)
        {
            string fp = filePath.Replace('/', '\\');
            string tp = targetPath.Replace('/', '\\');
            int matchLen = 0;
            int fi = fp.Length - 1;
            int ti = tp.Length - 1;
            while (fi >= 0 && ti >= 0 && char.ToLowerInvariant(fp[fi]) == char.ToLowerInvariant(tp[ti]))
            {
                matchLen++;
                fi--;
                ti--;
            }
            // 要求以目录分隔符或开头结束（避免部分匹配，如 a/b.cpp 匹配到 x/b.cpp 而不是 xx/b.cpp）
            if (fi >= 0 && fp[fi] != '\\' && fp[fi] != '/') return 0;
            return matchLen;
        }

        /// <summary>
        /// 导航到工作区中的指定符号定义（通过文件内容搜索定位并跳转到对应行）。
        /// </summary>
        private async Task NavigateToSymbolAsync(string symbolName)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                // ── 获取工作区根目录 ──
                string? slnPath = _solutionPath;
                if (string.IsNullOrEmpty(slnPath) || (!File.Exists(slnPath) && !Directory.Exists(slnPath)))
                {
                    Logger.Warn($"[Nav] 无法导航到符号 {symbolName}: 解决方案路径无效");
                    return;
                }

                string workspaceRoot = File.Exists(slnPath)
                    ? Path.GetDirectoryName(slnPath) ?? slnPath
                    : slnPath;

                // 在源码文件中搜索符号定义
                var sourceExts = SharedConstants.SourceFileExtensions
                    .Where(e => e.StartsWith("."))
                    .ToArray();
                // ── 多语言定义/声明模式（优先级从高到低）──
                // 模式1：类型声明 — class/struct/enum/interface/record/trait + 名称
                // 模式2：函数/方法定义 — 返回类型 + 名称 + (...（C/C++/C#/Java/TS）
                // 模式3：修饰符引导 — public/private/protected/static/export + 名称
                // 模式4：Python 风格 — def name( 或 class name
                // 模式5：C/C++ 成员 — ClassName::name / ClassName::~name
                string escaped = Regex.Escape(symbolName);
                var definitionPatterns = new (string pattern, string label)[]
                {
                    // ── 类型声明（跨语言）──
                    ($@"\b(class|struct|enum|interface|record|trait|union|namespace|module)\s+{escaped}\b", "type"),
                    // ── C/C++ 函数定义：返回类型 + 名称 + ( ──
                    ($@"\b(void|int|bool|char|short|long|float|double|auto|size_t|wchar_t|HRESULT|NTSTATUS)\s*\*?\s*{escaped}\s*\(", "c_func"),
                    ($@"\b(unsigned|signed|const|static|inline|virtual|explicit|extern|friend)\s+.*\b{escaped}\s*\(", "c_mod_func"),
                    // ── C#/Java 方法定义 ──
                    ($@"\b(void|int|string|bool|var|async|Task|object|double|float|long|byte|char)\s+{escaped}\s*[\(\<]", "cs_func"),
                    ($@"\b(public|private|protected|internal|static|override|virtual|abstract|sealed|partial|readonly|volatile)\s+.*\b{escaped}\b", "cs_mod"),
                    // ── C/C++ 构造函数/析构函数  ClassName::ClassName / ClassName::~ClassName ──
                    ($@"\b\w+::{escaped}\s*\(", "cpp_member"),
                    // ── Python 风格 ──
                    ($@"\bdef\s+{escaped}\s*\(", "py_func"),
                    ($@"\bclass\s+{escaped}\b", "py_class"),
                    // ── JS/TS 风格 ──
                    ($@"\b(function|async\s+function)\s+{escaped}\s*\(", "js_func"),
                    ($@"\b(let|const|var)\s+{escaped}\b", "js_var"),
                    // ── 泛型模式：Name<T> 类型参数 ──
                    ($@"\b{escaped}\s*<.*>\s*[\(\:]", "generic"),
                };

                string? foundFile = null;
                int foundLine = 0;
                string? matchLabel = null;

                // ── 第1轮：严格定义匹配（所有模式，跨所有文件）──
                await Task.Run(() =>
                {
                    foreach (var ext in sourceExts)
                    {
                        if (foundFile != null) break;
                        try
                        {
                            var files = Directory.GetFiles(workspaceRoot, "*" + ext, SearchOption.AllDirectories)
                                .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\")
                                    && !f.Contains("\\.git\\") && !f.Contains("\\node_modules\\")
                                    && !f.Contains("\\packages\\") && !f.Contains("\\TestResults\\")
                                    && !f.Contains("\\.vs\\"));
                            foreach (var file in files.Take(500))
                            {
                                try
                                {
                                    var lines = File.ReadAllLines(file);
                                    for (int i = 0; i < lines.Length; i++)
                                    {
                                        string line = lines[i].Trim();
                                        if (!line.Contains(symbolName)) continue;

                                        foreach (var (pattern, label) in definitionPatterns)
                                        {
                                            if (Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase))
                                            {
                                                foundFile = file;
                                                foundLine = i + 1;
                                                matchLabel = label;
                                                break;
                                            }
                                        }
                                        if (foundFile != null) break;
                                    }
                                    if (foundFile != null) break;
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                });

                // ── 第2轮：宽松回退（仅在无定义匹配时，匹配优先级高于随机命中）──
                if (foundFile == null)
                {
                    await Task.Run(() =>
                    {
                        foreach (var ext in sourceExts)
                        {
                            if (foundFile != null) break;
                            try
                            {
                                var files = Directory.GetFiles(workspaceRoot, "*" + ext, SearchOption.AllDirectories)
                                    .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\")
                                        && !f.Contains("\\.git\\") && !f.Contains("\\node_modules\\")
                                        && !f.Contains("\\packages\\") && !f.Contains("\\TestResults\\"));
                                foreach (var file in files.Take(300))
                                {
                                    try
                                    {
                                        var lines = FileEncodingHelper.ReadAllLines(file);
                                        for (int i = 0; i < lines.Length; i++)
                                        {
                                            string line = lines[i].Trim();
                                            // 优先：行以符号名开头（可能是定义）或包含 { / = / ( 等定义特征
                                            if (line.StartsWith(symbolName, StringComparison.Ordinal)
                                                || (line.Contains(symbolName)
                                                    && (line.Contains('{') || line.Contains('=') || line.Contains('('))))
                                            {
                                                foundFile = file;
                                                foundLine = i + 1;
                                                matchLabel = "loose_def";
                                                break;
                                            }
                                        }
                                        if (foundFile != null) break;
                                    }
                                    catch { }
                                }
                            }
                            catch { }
                        }
                    });
                }

                // ── 第3轮：纯文本匹配（最后手段）──
                if (foundFile == null)
                {
                    await Task.Run(() =>
                    {
                        foreach (var ext in sourceExts)
                        {
                            if (foundFile != null) break;
                            try
                            {
                                var files = Directory.GetFiles(workspaceRoot, "*" + ext, SearchOption.AllDirectories)
                                    .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\")
                                        && !f.Contains("\\.git\\") && !f.Contains("\\node_modules\\")
                                        && !f.Contains("\\packages\\"));
                                foreach (var file in files.Take(200))
                                {
                                    try
                                    {
                                        var lines = FileEncodingHelper.ReadAllLines(file);
                                        for (int i = 0; i < lines.Length; i++)
                                        {
                                            if (lines[i].Contains(symbolName))
                                            {
                                                foundFile = file;
                                                foundLine = i + 1;
                                                matchLabel = "text";
                                                break;
                                            }
                                        }
                                        if (foundFile != null) break;
                                    }
                                    catch { }
                                }
                            }
                            catch { }
                        }
                    });
                }

                if (foundFile != null)
                {
                    // 通过 VS SDK 打开文件
                    var openDoc = await ServiceProvider.GetGlobalServiceAsync(
                        typeof(Microsoft.VisualStudio.Shell.Interop.SVsUIShellOpenDocument))
                        as Microsoft.VisualStudio.Shell.Interop.IVsUIShellOpenDocument;

                    if (openDoc != null)
                    {
                        var logicalView = Microsoft.VisualStudio.VSConstants.LOGVIEWID_Primary;
                        openDoc.OpenDocumentViaProject(
                            foundFile, ref logicalView,
                            out _, out _, out _,
                            out Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame frame);
                        frame?.Show();

                        // 跳转到指定行
                        // 跳转到指定行
                        if (foundLine > 0)
                        {
                            try
                            {
                                var textManager = Package.GetGlobalService(
                                    typeof(Microsoft.VisualStudio.TextManager.Interop.SVsTextManager))
                                    as Microsoft.VisualStudio.TextManager.Interop.IVsTextManager;

                                if (textManager != null)
                                {
                                    Microsoft.VisualStudio.TextManager.Interop.IVsTextView? textView;
                                    textManager.GetActiveView(1, null, out textView);
                                    if (textView != null)
                                    {
                                        textView.SetCaretPos(foundLine - 1, 0);
                                        textView.CenterLines(foundLine - 1, 1);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn($"[Nav] 跳转到行 {foundLine} 失败: {ex.Message}");
                            }
                        }

                        Logger.Info($"[Nav] 打开符号 {symbolName}: {foundFile}:{foundLine} (match={matchLabel})");
                    }
                }
                else
                {
                    Logger.Warn($"[Nav] 未找到符号定义: {symbolName} in {workspaceRoot}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Nav] 导航到符号失败 ({symbolName}): {ex.Message}", ex);
            }
        }

        private async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string message = e.TryGetWebMessageAsString();
                if (string.IsNullOrWhiteSpace(message)) return;

                // ── 页面就绪信号：WebView2 DOM + JS 全部加载完成 ──
                if (message == "__pageReady__")
                {
                    _pageReady = true;
                    Logger.Info("[Render] WebView2 页面就绪信号收到");
                    // 重建窗口内被丢弃的终态渲染必须补发，否则气泡会停在占位/纯文本状态。
                    TryReplayPendingStreamEnd();
                    return;
                }

                var obj = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(message);
                if (obj.TryGetProperty("type", out var typeProp))
                {
                    string type = typeProp.GetString() ?? string.Empty;

                    if (type == "debugShortcut")
                    {
                        TriggerDebugShortcut();
                    }
                    else if (type == "viewCodeShortcut")
                    {
                        bool showDesigner = obj.TryGetProperty("designer", out var designerProp)
                            && designerProp.GetBoolean();
                        TriggerViewCodeShortcut(showDesigner);
                    }
                    else if (type == "applyCode")
                    {
                        string code = obj.TryGetProperty("code", out var codeProp)
                            ? codeProp.GetString() ?? string.Empty : string.Empty;
                        ApplyCodeToActiveDocument(code);
                    }
                    else if (type == "openAttachment")
                    {
                        string filePath = obj.TryGetProperty("path", out var pathProp)
                            ? pathProp.GetString() ?? string.Empty : string.Empty;
                        if (!string.IsNullOrWhiteSpace(filePath))
                            TryOpenAttachment(filePath);
                    }
                    else if (type == "saveDiagramToFile")
                    {
                        string svg = obj.TryGetProperty("svg", out var svgProp)
                            ? svgProp.GetString() ?? string.Empty : string.Empty;
                        SaveDiagramToFile(svg);
                    }
                    else if (type == "retryMessage")
                    {
                        int msgIndex = obj.TryGetProperty("messageIndex", out var retryIdxProp)
                            ? retryIdxProp.GetInt32() : -1;
                        if (msgIndex >= 0)
                            _ = RetryMessageAsync(msgIndex);
                    }
                    else if (type == "editMessage")
                    {
                        int msgIndex = obj.TryGetProperty("messageIndex", out var editIdxProp)
                            ? editIdxProp.GetInt32() : -1;
                        if (msgIndex >= 0)
                            _ = EditMessageAsync(msgIndex);
                    }
                    else if (type == "editMessageConfirm")
                    {
                        int msgIndex = obj.TryGetProperty("messageIndex", out var editConfirmIdxProp)
                            ? editConfirmIdxProp.GetInt32() : -1;
                        string newText = obj.TryGetProperty("text", out var editTextProp)
                            ? editTextProp.GetString() ?? string.Empty : string.Empty;
                        if (msgIndex >= 0 && !string.IsNullOrEmpty(newText))
                            _ = HandleEditConfirmAsync(msgIndex, newText);
                    }
                    else if (type == "loadEarlier")
                    {
                        _ = LoadEarlierMessagesAsync();
                    }
                    else if (type == "editMessageCancel")
                    {
                        int msgIndex = obj.TryGetProperty("messageIndex", out var editCancelIdxProp)
                            ? editCancelIdxProp.GetInt32() : -1;
                        if (msgIndex >= 0)
                            _ = HandleEditCancelAsync(msgIndex);
                    }
                    else if (type == "navigateBranch")
                    {
                        string nodeId = obj.TryGetProperty("nodeId", out var nodeIdProp)
                            ? nodeIdProp.GetString() ?? string.Empty : string.Empty;
                        int direction = obj.TryGetProperty("direction", out var dirProp)
                            ? dirProp.GetInt32() : 0;
                        if (!string.IsNullOrEmpty(nodeId) && direction != 0)
                            _ = NavigateBranchAsync(nodeId, direction);
                    }
                    else if (type == "navigateVersion")
                    {
                        // ── 旧版兼容：转换为 navigateBranch ──
                        int msgIndex = obj.TryGetProperty("messageIndex", out var navIdxProp)
                            ? navIdxProp.GetInt32() : -1;
                        int direction = obj.TryGetProperty("direction", out var dirPropOld)
                            ? dirPropOld.GetInt32() : 0;
                        if (msgIndex >= 0 && direction != 0)
                        {
                            // 旧版按消息索引导航 → 转为按 nodeId 导航
                            var node = GetConvNodeByMessageIndex(msgIndex);
                            if (node != null)
                                _ = NavigateBranchAsync(node.Id, direction);
                        }
                    }
                    else if (type == "agentApprove")
                    {
                        string requestId = obj.TryGetProperty("requestId", out var reqIdProp)
                            ? reqIdProp.GetString() ?? string.Empty : string.Empty;
                        bool approved = obj.TryGetProperty("approved", out var apprProp)
                            && apprProp.GetBoolean();
                        var permAgent = _agentFactory?.FindAgentWithPendingPermission(requestId) ?? _activeAgent;
                        permAgent?.RespondToPermission(requestId, approved);

                        // 移除权限 UI
                        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                        {
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            if (ChatWebView.CoreWebView2 != null)
                            {
                                try
                                {
                                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(
                                        ChatHtmlService.BuildRemoveElementJs(
                                            ChatHtmlService.PermissionCardIdPrefix + requestId));
                                }
                                catch { }
                            }

                            // 审批卡片串行显示：当前项处理完 → 显示下一个
                            AdvanceApprovalQueue(requestId);
                        });
                    }
                    // ── 终端命令审批 ──
                    else if (type == "terminalApprove")
                    {
                        string requestId = obj.TryGetProperty("requestId", out var termReqIdProp)
                            ? termReqIdProp.GetString() ?? string.Empty : string.Empty;
                        var termPermAgent = _agentFactory?.FindAgentWithPendingPermission(requestId) ?? _activeAgent;
                        termPermAgent?.RespondToPermission(requestId, true);

                        // 移除审批 UI
                        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                        {
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            if (ChatWebView.CoreWebView2 != null)
                            {
                                try
                                {
                                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(
                                        ChatHtmlService.BuildRemoveElementJs(
                                            ChatHtmlService.TerminalApprovalCardIdPrefix + requestId));
                                    StatusLabel.Text = LocalizationService.Instance["status.terminalApproved"];
                                }
                                catch { }
                            }

                            // 审批卡片串行显示：当前项处理完 → 显示下一个
                            AdvanceApprovalQueue(requestId);
                        });
                    }
                    else if (type == "terminalSkip")
                    {
                        string requestId = obj.TryGetProperty("requestId", out var termSkipReqIdProp)
                            ? termSkipReqIdProp.GetString() ?? string.Empty : string.Empty;
                        var skipPermAgent = _agentFactory?.FindAgentWithPendingPermission(requestId) ?? _activeAgent;
                        skipPermAgent?.RespondToPermission(requestId, false);

                        // 移除审批 UI
                        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                        {
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            if (ChatWebView.CoreWebView2 != null)
                            {
                                try
                                {
                                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(
                                        ChatHtmlService.BuildRemoveElementJs(
                                            ChatHtmlService.TerminalApprovalCardIdPrefix + requestId));
                                    StatusLabel.Text = LocalizationService.Instance["status.terminalSkipped"];
                                }
                                catch { }
                            }

                            // 审批卡片串行显示：当前项处理完 → 显示下一个
                            AdvanceApprovalQueue(requestId);
                        });
                    }
                    // ── VisualStudio_askQuestions 回答 ──
                    else if (type == "answerQuestions")
                    {
                        string requestId = obj.TryGetProperty("requestId", out var qReqIdProp)
                            ? qReqIdProp.GetString() ?? string.Empty : string.Empty;
                        string answers = obj.TryGetProperty("answers", out var ansProp)
                            ? ansProp.GetString() ?? "{}" : "{}";
                        var questionAgent = _agentFactory?.FindAgentWithPendingQuestion(requestId) ?? _activeAgent;
                        questionAgent?.RespondToQuestions(requestId, answers);
                        lock (_lock) { _presentedQuestionRequests.Remove(requestId); }

                        // 移除问题 UI
                        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                        {
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            if (ChatWebView.CoreWebView2 != null)
                            {
                                try
                                {
                                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(
                                        "var p=document.getElementById('agent-questions');if(p)p.remove();");
                                    StatusLabel.Text = LocalizationService.Instance["status.ready"];
                                }
                                catch { }
                            }
                        });
                    }
                    // ── 诊断消息（来自 JS try-catch / DOM 检查）──
                    else if (type == "diagnostic")
                    {
                        string msg = obj.TryGetProperty("msg", out var diagProp)
                            ? diagProp.GetString() ?? string.Empty : string.Empty;
                        Logger.Info($"[WebMessage] JS diagnostic: {msg}");
                    }
                    else if (type == "fileDeleteConfirm")
                    {
                        string requestId = obj.TryGetProperty("requestId", out var delReqIdProp)
                            ? delReqIdProp.GetString() ?? string.Empty : string.Empty;
                        bool confirmed = obj.TryGetProperty("confirmed", out var confProp)
                            && confProp.GetBoolean();

                        // 按 RequestId 精确查找待处理的权限请求（支持并发权限场景）
                        var filePermAgent = _agentFactory?.FindAgentWithPendingPermission(requestId);
                        var pendingPermission = filePermAgent?.TryGetPendingPermission(requestId);
                        List<string> filePaths = pendingPermission?.FilePaths ?? new List<string>();

                        // 审批层只负责放行，不能提前删除文件；
                        // 实际删除统一由 delete_file 执行一次，避免工具侧看到“文件不存在”。

                        // 完成权限响应（解除 Agent 等待）
                        (filePermAgent ?? _activeAgent)?.RespondToPermission(requestId, confirmed);

                        // 移除文件删除确认 UI
                        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                        {
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            if (ChatWebView.CoreWebView2 != null)
                            {
                                try
                                {
                                    await ChatWebView.CoreWebView2.ExecuteScriptAsync(
                                        ChatHtmlService.BuildRemoveElementJs(
                                            ChatHtmlService.FileDeleteCardIdPrefix + requestId));
                                    StatusLabel.Text = confirmed
                                        ? LocalizationService.Instance.Format("status.filesDeleted", filePaths.Count)
                                        : LocalizationService.Instance["status.deleteCancelled"];
                                }
                                catch { }

                                // 审批卡片串行显示：当前项处理完 → 显示下一个
                                AdvanceApprovalQueue(requestId);
                            }
                        });
                    }
                    else if (type == "executeHandoff")
                    {
                        string targetAgent = obj.TryGetProperty("targetAgent", out var targetProp)
                            ? targetProp.GetString() ?? "Edit" : "Edit";
                        string label = obj.TryGetProperty("label", out var labelProp)
                            ? labelProp.GetString() ?? LocalizationService.Instance["plan.handoff.label"] : LocalizationService.Instance["plan.handoff.label"];
                        _ = ExecuteAgentHandoffAsync(targetAgent, label);
                    }
                    // ── 过程折叠块开合：持久化用户意图，切走/重开面板后仍保持 ──
                    else if (type == "turnProcessToggled")
                    {
                        string? toggleTurnId = obj.TryGetProperty("turnId", out var turnIdProp)
                            ? turnIdProp.GetString() : null;
                        if (!string.IsNullOrEmpty(toggleTurnId))
                        {
                            // 仅当明确回传 true 才视为收起，避免字段缺失时被误判
                            bool collapsed = obj.TryGetProperty("collapsed", out var collapsedProp)
                                && collapsedProp.ValueKind == System.Text.Json.JsonValueKind.True;
                            HandleTurnProcessToggled(toggleTurnId!, collapsed);
                        }
                    }
                    // ── 关闭任务面板：清除持久化的 PlanJson，防止重启后重新显示 ──
                    else if (type == "dismissTaskPanel")
                    {
                        string planId = obj.TryGetProperty("planId", out var planIdProp)
                            ? planIdProp.GetString() ?? string.Empty : string.Empty;
                        if (!string.IsNullOrEmpty(planId))
                        {
                            DismissTaskPanel(planId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"WebMessage 处理异常: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Applies the AI-generated code to the active document.
        /// Uses TerminalWindowHelper for proper TextBuffer manipulation.
        /// </summary>
        private void ApplyCodeToActiveDocument(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;

            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                string? error = await TerminalWindowHelper.ApplyCodeToActiveDocumentAsync(code);
                if (error != null)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = $" {error}";
                    Logger.Warn($"[ApplyCode] 写入失败: {error}");
                }
            });
        }

        #endregion
    }
}
