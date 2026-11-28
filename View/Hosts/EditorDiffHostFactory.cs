using DeepSeek_v4_for_VisualStudio.Services;
using Microsoft.VisualStudio.Text.Editor;

namespace DeepSeek_v4_for_VisualStudio.View.Hosts
{
    /// <summary>
    /// 内联 Diff 宿主工厂的 View 层实现 — 把具体的 <see cref="EditorDiffHost"/> 创建过程留在 UI 层。
    /// 由扩展初始化时注入到 <see cref="EditorDiffMarkerService.HostFactory"/>，
    /// 使服务层依赖 <see cref="IEditorDiffHost"/> 抽象而不必引用 View 命名空间。
    /// </summary>
    internal sealed class EditorDiffHostFactory : IEditorDiffHostFactory
    {
        /// <summary>
        /// 为指定编辑器视图创建基于 WPF Adorner 的内联 Diff 宿主。
        /// </summary>
        /// <param name="textView">目标 WPF 文本视图。</param>
        /// <returns>新建的内联 Diff 宿主实例，包装为服务层契约类型。</returns>
        public IEditorDiffHost Create(IWpfTextView textView) => new EditorDiffHost(textView);
    }
}
