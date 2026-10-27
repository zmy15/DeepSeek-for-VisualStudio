using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;

namespace DeepSeek_v4_for_VisualStudio.Utils
{
    /// <summary>
    /// 编辑器视图访问辅助。
    ///
    /// 此前 <c>InlineAiEditCommand</c> 与 <c>IdeContextTracker</c> 各自复制了一份
    /// 「通过 SVsTextManager + IVsEditorAdaptersFactoryService 取活动 IWpfTextView」的实现，
    /// 两处必须同步维护（任一VS API 变化都会只改一处）。统一收敛到这里。
    /// </summary>
    internal static class EditorViewHelper
    {
        /// <summary>
        /// 获取当前活动的 WPF 文本视图；无活动视图或服务缺失时返回 null。
        /// 必须在 UI 线程调用。
        /// </summary>
        public static IWpfTextView? TryGetActiveWpfTextView()
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
    }
}
