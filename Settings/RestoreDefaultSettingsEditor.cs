using DeepSeek_v4_for_VisualStudio.Services;
using System;
using System.ComponentModel;
using System.Drawing.Design;
using System.Windows.Forms;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>
    /// 旧版设置页中的“恢复默认设置”按钮。
    /// API 密钥等凭据保持不变，其余设置恢复为新安装时的默认值。
    /// </summary>
    public sealed class RestoreDefaultSettingsEditor : UITypeEditor
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

            var localization = LocalizationService.Instance;
            var confirm = MessageBox.Show(
                localization["settings.restoreDefaults.confirmMessage"],
                localization["settings.restoreDefaults.confirmTitle"],
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.OK)
                return value;

            try
            {
                page.RestoreDefaultsPreservingCredentials();
                page.SaveSettingsToStorage();
                page.ApplyRuntimeHotUpdates();
                UnifiedSettingsSync.PushFromPage(page);
                MessageBox.Show(
                    localization["settings.restoreDefaults.success"],
                    localization["settings.restoreDefaults.confirmTitle"],
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    localization.Format("settings.restoreDefaults.failure", ex.Message),
                    localization["settings.restoreDefaults.confirmTitle"],
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return value;
        }
    }
}
