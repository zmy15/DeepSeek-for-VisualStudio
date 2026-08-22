using System;
using System.Collections.Generic;
using System.Linq;
using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.TextManager.Interop;

namespace DeepSeek_v4_for_VisualStudio.Services.IdeContext
{
    /// <summary>
    /// IDE 实时态追踪器（P1-A）。
    ///
    /// 设计（报告 §7-§10）：
    /// - 每次用户消息发送前由 View 在 <b>UI 线程</b>调用一次 <see cref="CaptureFromActiveView"/>，
    ///   从当前活动的 IWpfTextView 提取文件/光标/选区/符号/当前文件 squiggle 诊断，
    ///   生成不可变 <see cref="IdeContextSnapshot"/>；Agent 每轮只读取 Current，不重复扫描 VS。
    /// - 捕获失败时置空（fail-closed）：宁可缺上下文，不给过期上下文误导模型。
    /// - 深度查询职责归 get_errors / read_file 工具，本类只做"快速定位"。
    /// </summary>
    public sealed class IdeContextTracker
    {
        private const int MaxDiagnosticsToCollect = 50;

        private volatile IdeContextSnapshot? _current;

        /// <summary>最近一次捕获的快照（null = 尚未捕获或上次捕获失败）。</summary>
        public IdeContextSnapshot? Current => _current;

        /// <summary>清空快照。</summary>
        public void Clear() => _current = null;

        /// <summary>从当前活动编辑器视图捕获快照。必须在 UI 线程调用。</summary>
        public void CaptureFromActiveView()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                _current = CaptureCore();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[IdeContext] 捕获失败: {ex.Message}");
                _current = null;
            }
        }

        // ────────────────────────── 内部实现 ──────────────────────────

        private static IdeContextSnapshot? CaptureCore()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var view = TryGetActiveWpfTextView();
            if (view == null || view.TextSnapshot.Length == 0)
            {
                // 非文本视图（工具窗口/设置页等）→ 无可注入内容
                return null;
            }

            var s = new IdeContextSnapshot();

            // ── 文件路径 ──
            if (view.TextDataModel.DocumentBuffer.Properties.TryGetProperty(
                    typeof(ITextDocument), out ITextDocument doc))
            {
                s.FilePath = doc.FilePath;
            }

            // ── 光标 ──
            var caretPos = view.Caret.Position.BufferPosition;
            var containingLine = caretPos.GetContainingLine();
            s.CursorLine = containingLine.LineNumber + 1;
            s.CursorColumn = caretPos.Position - containingLine.Start.Position + 1;

            // ── 选区 ──
            var selectedSpans = view.Selection.SelectedSpans;
            if (selectedSpans.Count > 0 && !selectedSpans[0].IsEmpty)
            {
                var span = selectedSpans[0];
                s.SelectionText = span.GetText();
                s.SelectionStartLine = span.Start.GetContainingLine().LineNumber + 1;
                s.SelectionEndLine = span.End.GetContainingLine().LineNumber + 1;
            }

            // ── 符号启发式（光标处标识符；非语义解析，够用于"快速定位"）──
            string lineText = containingLine.GetText();
            s.SymbolAtCursor = IdeContextSnapshot.ExtractIdentifierAt(lineText, s.CursorColumn - 1);
            if (!string.IsNullOrWhiteSpace(s.SymbolAtCursor))
                s.SymbolLineText = lineText.Trim();

            // ── 当前文件 squiggle 诊断 ──
            var diags = CollectBufferDiagnostics(view);
            if (diags != null)
            {
                foreach (var d in diags)
                    s.Diagnostics.Add(d);
            }

            s.CapturedAt = DateTime.Now;

            return s.HasContent ? s : null;
        }

        /// <summary>获取当前活动视图（与 CodeActions 的 IVsTextManager 方案一致）。</summary>
        private static IWpfTextView? TryGetActiveWpfTextView()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var textManager = (IVsTextManager?)Package.GetGlobalService(typeof(SVsTextManager));
            if (textManager == null) return null;

            textManager.GetActiveView(1, null, out IVsTextView vsTextView);
            if (vsTextView == null) return null;

            var componentModel = (IComponentModel?)Package.GetGlobalService(typeof(SComponentModel));
            var adapter = componentModel?.DefaultExportProvider
                .GetExport<IVsEditorAdaptersFactoryService>()?.Value;

            return adapter?.GetWpfTextView(vsTextView);
        }

        /// <summary>
        /// 收集当前 buffer 的 squiggle 诊断（错误优先展示由格式化器负责排序）。
        /// 任何失败返回 null —— 诊断缺失不影响其余上下文注入。
        /// </summary>
        private static List<IdeDiagnosticItem>? CollectBufferDiagnostics(IWpfTextView view)
        {
            try
            {
                var componentModel = (IComponentModel?)Package.GetGlobalService(typeof(SComponentModel));
                var factory = componentModel?.DefaultExportProvider
                    .GetExport<IErrorProviderFactory>()?.Value;
                if (factory == null) return null;

                SimpleTagger<ErrorTag> table = factory.GetErrorTable(view.TextBuffer);
                var fullSpan = new SnapshotSpan(view.TextSnapshot, 0, view.TextSnapshot.Length);

                var result = new List<IdeDiagnosticItem>();
                foreach (var mapping in table.GetTagSpans(fullSpan))
                {
                    if (result.Count >= MaxDiagnosticsToCollect) break;

                    string errorType = mapping.Tag.ErrorType ?? string.Empty;
                    string severity =
                        errorType.Contains("error", StringComparison.OrdinalIgnoreCase) ? "error"
                        : errorType.Contains("warn", StringComparison.OrdinalIgnoreCase) ? "warning"
                        : "";

                    if (severity.Length == 0) continue;

                    int lineNo = 0;
                    var snapSpans = mapping.Span.GetSpans(view.TextBuffer);
                    if (snapSpans.Count > 0)
                        lineNo = snapSpans[0].Start.GetContainingLine().LineNumber + 1;

                    string message = (mapping.Tag.ToolTipContent?.ToString() ?? "")
                        .Replace('\r', ' ').Replace('\n', ' ').Trim();

                    result.Add(new IdeDiagnosticItem { Severity = severity, Line = lineNo, Message = message });
                }
                return result;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[IdeContext] 收集诊断失败: {ex.Message}");
                return null;
            }
        }
    }
}
