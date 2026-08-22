using Microsoft.VisualStudio.Utilities.UnifiedSettings;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>
    /// P1 原型：把非敏感设置子集以「外部区域」形态桥接进 VS2026 Unified Settings。
    /// 单一事实源仍是 DeepSeekOptionsPage.Instance：
    /// GetValue 读属性；SetValue 写属性 -> SaveSettingsToStorage() -> ApplyRuntimeHotUpdates()。
    /// MEF 导出使 Dev18 的目录可发现本 Provider（契约程序集经绑定重定向统一）。
    /// </summary>
    [Export(typeof(IExternalSettingsProvider))]
    internal sealed class DeepSeekExternalSettingsProvider : IExternalSettingsProvider
    {
        private static readonly Dictionary<string, string> StringMap = new(StringComparer.Ordinal)
        {
            ["deepseek.selectedModel"] = "SelectedModel",
            ["deepseek.reasoningEffort"] = "ReasoningEffort",
            ["deepseek.searchProvider"] = "SearchProvider",
            ["deepseek.approvalMode"] = "ApprovalMode",
        };
        private static readonly Dictionary<string, string> BoolMap = new(StringComparer.Ordinal)
        {
            ["deepseek.thinkingEnabled"] = "IsThinkingEnabled",
            ["deepseek.enableWebSearch"] = "EnableWebSearch",
            ["deepseek.showContextStats"] = "ShowContextStats",
            ["deepseek.enableTelemetryExport"] = "EnableTelemetryExport",
            ["deepseek.enableIdeContextInjection"] = "EnableIdeContextInjection",
            ["deepseek.enableAutoCompression"] = "EnableAutoCompression",
        };
        private static readonly Dictionary<string, string> IntMap = new(StringComparer.Ordinal)
        {
            ["deepseek.tokenBudget"] = "TokenBudget",
            ["deepseek.compressionThreshold"] = "CompressionThreshold",
        };

        public event EventHandler<ExternalSettingsChangedEventArgs>? SettingValuesChanged;
        public event EventHandler? ErrorConditionResolved;
        public event EventHandler<DynamicMessageTextChangedEventArgs>? DynamicMessageTextChanged;
        public event EventHandler<EnumSettingChoicesChangedEventArgs>? EnumSettingChoicesChanged;

        internal void NotifyExternalChange()
        {
            try { SettingValuesChanged?.Invoke(this, ExternalSettingsChangedEventArgs.SomeOrAll); }
            catch { }
        }

        private static string FindProp(string settingId)
        {
            if (StringMap.TryGetValue(settingId, out var s)) return s;
            if (BoolMap.TryGetValue(settingId, out var b)) return b;
            if (IntMap.TryGetValue(settingId, out var i)) return i;
            return null;
        }

        public Task<ExternalSettingOperationResult<T>> GetValueAsync<T>(string settingId, CancellationToken cancellationToken)
        {
            try
            {
                var t = DeepSeekOptionsPage.Instance;
                if (t == null) return Task.FromResult(Fail<T>("options not loaded yet"));
                var prop = FindProp(settingId);
                if (prop == null) return Task.FromResult(Fail<T>("unknown setting id: " + settingId));
                var raw = t.GetType().GetProperty(prop)?.GetValue(t);
                if (raw is T typed) return Task.FromResult(Ok(typed));
                var converted = Convert.ChangeType(raw, typeof(T));
                return Task.FromResult(Ok((T)converted));
            }
            catch (Exception ex)
            {
                return Task.FromResult(Fail<T>(ex.Message));
            }
        }

        public Task<ExternalSettingOperationResult> SetValueAsync<T>(string settingId, T value, CancellationToken cancellationToken)
        {
            try
            {
                var t = DeepSeekOptionsPage.Instance;
                if (t == null) return Task.FromResult(Fail("options not loaded yet"));
                var prop = FindProp(settingId);
                if (prop == null) return Task.FromResult(Fail("unknown setting id: " + settingId));

                var pi = t.GetType().GetProperty(prop);
                if (pi == null || !pi.CanWrite)
                    return Task.FromResult(Fail("property not writable: " + prop));

                var converted = Convert.ChangeType(value, pi.PropertyType);
                pi.SetValue(t, converted);
                t.SaveSettingsToStorage();
                t.ApplyRuntimeHotUpdates();
                NotifyExternalChange();
                return Task.FromResult(default(ExternalSettingOperationResult));
            }
            catch (Exception ex)
            {
                return Task.FromResult(Fail(ex.Message));
            }
        }

        // ── 结果构造辅助（集中一处，适配真实嵌套类型形态）──
        private static ExternalSettingOperationResult Fail(string message)
            => new ExternalSettingOperationResult.Failure(message, ExternalSettingsErrorScope.SingleSettingOnly, false);
        private static ExternalSettingOperationResult<T> Ok<T>(T value)
            => new ExternalSettingOperationResult<T>.Success(value);
        private static ExternalSettingOperationResult<T> Fail<T>(string message)
            => new ExternalSettingOperationResult<T>.Failure(message, ExternalSettingsErrorScope.SingleSettingOnly, false);

        public Task<ExternalSettingOperationResult<IReadOnlyList<EnumChoice>>> GetEnumChoicesAsync(
            string settingId, CancellationToken cancellationToken)
        {
            IReadOnlyList<EnumChoice> choices =
                settingId == "deepseek.reasoningEffort" ? new[]
                {
                    new EnumChoice("low", "Low"), new EnumChoice("medium", "Medium"), new EnumChoice("high", "High"),
                } :
                settingId == "deepseek.approvalMode" ? new[]
                {
                    new EnumChoice("SmartBlock", "Smart block"),
                    new EnumChoice("BlockAll", "Block all"),
                    new EnumChoice("AllowAll", "Allow all"),
                } :
                settingId == "deepseek.searchProvider" ? new[]
                {
                    new EnumChoice("DuckDuckGo", "DuckDuckGo"), new EnumChoice("Baidu", "Baidu"),
                }
                : Array.Empty<EnumChoice>();
            return Task.FromResult(Ok(choices));
        }

        public Task<string> GetMessageTextAsync(string messageId, CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);

        public Task OpenBackingStoreAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

    }

    /// <summary>
    /// Dev18 Unified Settings 注册探针：自动发现 ExternalSettingsRegionDefinition
    /// 及其注册入口，将蓝图写入诊断日志。只读、无副作用、绝不抛出。
    /// </summary>
    internal static class UnifiedSettingsRegistrationProbe
    {
        public static void Run()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type? def = null;
                    try { def = asm.GetType("Microsoft.VisualStudio.Services.UnifiedSettings.DataModel.ExternalSettingsRegionDefinition", false); }
                    catch { }
                    if (def == null) continue;

                    DiagnosticLog.Write($"[UnifiedSettings] 定义类型位于: {asm.GetName().Name}");
                    foreach (var ctor in def.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
                        DiagnosticLog.Write($"[UnifiedSettings]   ctor: {ctor}");
                    foreach (var p in def.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        DiagnosticLog.Write($"[UnifiedSettings]   prop: {p.PropertyType.Name} {p.Name}");

                    foreach (var t in SafeGetTypes(asm))
                    {
                        if (!t.IsClass) continue;
                        var tn = t.FullName ?? t.Name;
                        if (tn.IndexOf("ExternalSettings", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        {
                            if (m.Name.IndexOf("Register", StringComparison.OrdinalIgnoreCase) >= 0)
                                DiagnosticLog.Write($"[UnifiedSettings]   candidate: {tn}.{m.Name}");
                        }
                        foreach (var pp in t.GetProperties(BindingFlags.Public | BindingFlags.Static))
                        {
                            if (pp.Name.IndexOf("Region", StringComparison.OrdinalIgnoreCase) >= 0)
                                DiagnosticLog.Write($"[UnifiedSettings]   candidate prop: {tn}.{pp.Name} : {pp.PropertyType.Name}");
                        }
                    }
                    return;
                }
                DiagnosticLog.Write("[UnifiedSettings] 未发现定义类型（非 Dev18 或新设置栈未加载）");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[UnifiedSettings] 探针异常: {ex.Message}");
            }
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
            catch { return Array.Empty<Type>(); }
        }
    }
}
