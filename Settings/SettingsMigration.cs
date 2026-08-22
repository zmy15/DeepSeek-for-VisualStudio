using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>
    /// 跨实例设置迁移（问题 2 修复）。
    ///
    /// 背景：VS 每个实例的 DialogPage 设置存放在各自的 privateregistry.bin 中，
    /// VS2022 的配置不会自动出现在 VS2026（新 hive 完全独立）。
    ///
    /// 策略：当当前实例 ApiKey 为空时，枚举同机其他实例的 bin，
    /// 用 RegLoadAppKey 只读挂载，找到 DeepSeekOptionsPage 集合，
    /// 将非空值按属性名回填到目标 OptionsPage 并走正常 SaveSettingsToStorage 持久化。
    /// </summary>
    internal static class SettingsMigration
    {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegLoadAppKey(string fileName, out IntPtr hKey, uint samDesired, uint options, uint reserved);

        [DllImport("advapi32.dll")]
        private static extern int RegCloseKey(IntPtr hKey);

        private const uint KEY_READ = 0x20019;

        /// <summary>尝试从其他实例迁移设置。返回是否发生迁移。</summary>
        public static bool TryMigrateInto(DeepSeekOptionsPage target)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(target.ApiKey)) return false; // 已有配置

                var baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "VisualStudio");

                var candidates = Directory.GetDirectories(baseDir)
                    .Where(d => !d.EndsWith("Exp", StringComparison.OrdinalIgnoreCase)) // 正式实例优先
                    .Select(d => Path.Combine(d, "privateregistry.bin"))
                    .Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTime);

                foreach (var bin in candidates)
                {
                    Logger.Info($"[Settings] 迁移探测: {bin}");
                    var values = TryReadValues(bin);
                    if (values == null || values.Count == 0)
                    {
                        Logger.Info("[Settings] 迁移探测: 未找到有效 DeepSeekOptionsPage 集合");
                        continue;
                    }

                    int applied = Apply(target, values);
                    if (applied > 0)
                    {
                        target.SaveSettingsToStorage();
                        Logger.Info($"[Settings] 已从 {Path.GetFileName(Path.GetDirectoryName(bin))} 迁移 {applied} 项设置");
                        return true;
                    }
                }
                Logger.Info("[Settings] 迁移结束：无可迁移来源");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Settings] 迁移失败: {ex.Message}");
            }
            return false;
        }

        private static Dictionary<string, string>? TryReadValues(string binPath)
        {
            IntPtr hKey = IntPtr.Zero;
            try
            {
                int err = RegLoadAppKey(binPath, out hKey, KEY_READ, 0, 0);
                if (err != 0 || hKey == IntPtr.Zero)
                {
                    Logger.Info($"[Settings] RegLoadAppKey 失败: win32err={err}");
                    return null;
                }

                // SafeRegistryHandle 拥有句柄，负责最终释放（不再单独 RegCloseKey）
                using var root = RegistryKey.FromHandle(new Microsoft.Win32.SafeHandles.SafeRegistryHandle(hKey, true));
                var page = FindKeyRecursive(root, "DeepSeekOptionsPage", maxDepth: 6);
                if (page == null) return null;

                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (page.GetValueNames().Length == 0) return null;
                foreach (var name in page.GetValueNames())
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    if (page.GetValue(name) is string s && !string.IsNullOrEmpty(s))
                        dict[name] = s;   // DPAPI 密文原样复制（同一用户可解）
                }
                return dict.ContainsKey("ApiKey") ? dict : null; // 必须含 Key 才视为有效来源
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Settings] 读取 {binPath} 失败: {ex.Message}");
                return null;
            }
        }

        private static RegistryKey? FindKeyRecursive(RegistryKey root, string nameHint, int maxDepth)
        {
            if (maxDepth < 0) return null;
            foreach (var sub in root.GetSubKeyNames())
            {
                if (sub.IndexOf(nameHint, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var k = root.OpenSubKey(sub);
                    if (k != null && k.GetValueNames().Length > 0) return k;
                    k?.Dispose();
                }
            }
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k == null) continue;
                var found = FindKeyRecursive(k, nameHint, maxDepth - 1);
                if (found != null) return found;
            }
            return null;
        }

        private static int Apply(DeepSeekOptionsPage target, Dictionary<string, string> values)
        {
            int applied = 0;
            var props = typeof(DeepSeekOptionsPage).GetProperties()
                .Where(p => p.CanRead && p.CanWrite);

            foreach (var p in props)
            {
                if (!values.TryGetValue(p.Name, out var raw)) continue;
                try
                {
                    if (p.PropertyType == typeof(string))
                    {
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        p.SetValue(target, raw);
                        applied++;
                    }
                    else if (p.PropertyType == typeof(bool))
                    {
                        p.SetValue(target, bool.Parse(raw));
                        applied++;
                    }
                    else if (p.PropertyType == typeof(int))
                    {
                        p.SetValue(target, int.Parse(raw));
                        applied++;
                    }
                    else if (p.PropertyType == typeof(double))
                    {
                        p.SetValue(target, double.Parse(raw));
                        applied++;
                    }
                }
                catch { /* 单项失败跳过 */ }
            }
            return applied;
        }
    }
}
