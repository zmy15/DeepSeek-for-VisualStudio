using Microsoft.VisualStudio.Text.Editor;
using System;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 编辑器内联 Diff 宿主抽象 — 限定服务层对宿主的最小依赖面。
    /// 具体实现位于 View 层（WPF Adorner + VS 差异查看器），由 <see cref="IEditorDiffHostFactory"/> 创建，
    /// 使 <see cref="EditorDiffMarkerService"/> 无需直接引用 View 命名空间即可驱动预览宿主。
    /// </summary>
    public interface IEditorDiffHost
    {
        /// <summary>
        /// 宿主关闭后触发 — 供服务清理按文本缓冲区索引的宿主登记，避免字典残留已释放的宿主。
        /// </summary>
        event EventHandler? Closed;

        /// <summary>
        /// 在宿主上显示指定会话的 Diff 视图。
        /// </summary>
        /// <param name="session">已创建的内联 Diff 会话。</param>
        void Show(InlineDiffSession session);

        /// <summary>
        /// 沿用既有宿主重建 Diff 视图与分块按钮，用于同一文件的连续刷新（无需销毁再创建宿主）。
        /// </summary>
        /// <param name="session">刷新后的内联 Diff 会话。</param>
        void RefreshSession(InlineDiffSession session);

        /// <summary>
        /// 确保覆盖层仍挂在编辑器可视树上（切换文档标签会把可视树卸载重载，Adorner 可能已被丢弃）。
        /// 再次点击改动条目时调用，用于恢复显示。
        /// </summary>
        void EnsureOverlayVisible();

        /// <summary>
        /// 关闭宿主并释放其占用的覆盖层资源。
        /// </summary>
        void Close();
    }

    /// <summary>
    /// 内联 Diff 宿主工厂 — 由 View 层实现并注入到服务，服务借此创建宿主而无需知道其具体类型。
    /// </summary>
    public interface IEditorDiffHostFactory
    {
        /// <summary>
        /// 为指定编辑器视图创建内联 Diff 宿主。
        /// </summary>
        /// <param name="textView">目标 WPF 文本视图，宿主将把覆盖层挂载到其可视元素上。</param>
        /// <returns>新建的宿主实例；实现方必须保证返回非 null。</returns>
        IEditorDiffHost Create(IWpfTextView textView);
    }
}
