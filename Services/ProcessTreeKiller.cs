using System;
using System.Diagnostics;
using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 结束进程及其子进程树。
    ///
    /// 兼容 .NET Framework 4.7.2（不支持 <c>Process.Kill(entireProcessTree)</c>）：
    /// 先 Kill 主进程，再用 <c>taskkill /T /F</c> 清理残留子进程（cmd → cmake → cl 等）。
    /// 此前 <c>BuildService</c> 与 <c>TestService</c> 各有一份逐字相同的实现，
    /// 只有日志前缀不同；统一收敛到这里。
    /// </summary>
    internal static class ProcessTreeKiller
    {
        /// <param name="process">要终止的进程（通常为 cmd / dotnet / cmake 外层进程）。</param>
        /// <param name="logTag">日志前缀，例如 <c>[BuildService]</c>。</param>
        public static void Kill(Process process, string logTag)
        {
            int pid;
            try
            {
                pid = process.Id;
            }
            catch (Exception ex)
            {
                Logger.Warn($"{logTag} 读取 PID 失败，跳过进程树清理: {ex.Message}");
                return;
            }

            try
            {
                // 先尝试直接 Kill 主进程
                if (!process.HasExited)
                    process.Kill();
            }
            catch (Exception ex)
            {
                Logger.Warn($"{logTag} Kill 主进程 (PID={pid}) 失败: {ex.Message}");
            }

            // 通过 taskkill /T 清理残留的子进程树
            try
            {
                var killPsi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/T /F /PID {pid}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var killer = Process.Start(killPsi);
                killer?.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Logger.Warn($"{logTag} taskkill /T (PID={pid}) 失败: {ex.Message}");
            }
        }
    }
}
