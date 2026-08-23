using Microsoft.VisualStudio.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio
{
    /// <summary>
    /// P2 Step2a：VisualStudio.Extensibility in-proc 扩展入口。
    /// 与既有 AsyncPackage 共存于同一 VSIX（ExtensionType="VSSDK+VisualStudio.Extensibility"）。
    /// Step2b 将在此声明 SettingCategory 非敏感子集并注入 Observer。
    /// </summary>
    [VisualStudioContribution]
    internal class DeepSeekExtension : Extension
    {
        public override ExtensionConfiguration? ExtensionConfiguration => new()
        {
            RequiresInProcessHosting = true,
        };

        public DeepSeekExtension()
        {
            DiagnosticLog.Write("[VSEXT] DeepSeekExtension ctor");
        }

        protected override void InitializeServices(IServiceCollection services)
        {
            DiagnosticLog.Write("[VSEXT] InitializeServices enter");
            base.InitializeServices(services);
            services.AddSettingsObservers();
            DiagnosticLog.Write("[VSEXT] InitializeServices exit (AddSettingsObservers done)");
        }
    }
}
