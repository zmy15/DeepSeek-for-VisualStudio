using System;
using System.IO;
using System.Text;
using System.Text.Json;
using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 一轮正在执行的 Agent 任务的内存快照（仅用于崩溃/强杀后的恢复）。
    /// <para>
    /// 为什么需要它：会话持久化只在「任务开始时」和「任务收尾时」各落盘一次，
    /// 执行中的流式正文、过程时间线、推理内容都只存在于内存。VS 被强杀、或工具窗口
    /// 在任务执行中被重建时，这些内容会整体丢失——磁盘上只剩一条用户消息和
    /// <see cref="SessionActiveRun"/> 标记，用户看到的是「任务没执行」。
    /// </para>
    /// </summary>
    public sealed class InflightTurnSnapshot
    {
        /// <summary>所属会话 Id。</summary>
        public string SessionId { get; set; } = string.Empty;

        /// <summary>轮次标识（与 <see cref="Models.ChatMessage.TurnId"/> 对应）。</summary>
        public string TurnId { get; set; } = string.Empty;

        /// <summary>执行该轮的 Agent 类型。</summary>
        public string AgentType { get; set; } = string.Empty;

        /// <summary>已接收到的最终正文。</summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>已接收到的过程时间线（工具调用、步骤日志）。</summary>
        public string TimelineContent { get; set; } = string.Empty;

        /// <summary>已接收到的推理内容。</summary>
        public string ReasoningContent { get; set; } = string.Empty;

        /// <summary>快照时间。</summary>
        public DateTime CapturedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// 执行中快照的读写：<c>conversations\inflight\{projectKey}\{sessionId}.json</c>。
    /// <para>
    /// 独立于会话文件，原因有三：不污染权威会话数据、可由后台线程安全写入、
    /// 崩溃后残留也不会让会话本身变得不可加载。快照在任务正常收尾时删除。
    /// </para>
    /// </summary>
    internal static class InflightTurnSnapshotService
    {
        /// <summary>单字段上限，避免异常超长输出把快照写成巨文件。</summary>
        private const int MaxFieldChars = 512 * 1024;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        /// <summary>快照根目录。</summary>
        private static string RootDirectory => Path.Combine(
            StoragePaths.ConversationsDirectory, "inflight");

        /// <summary>指定解决方案 + 会话的快照文件路径。</summary>
        public static string GetSnapshotPath(string? solutionPath, string sessionId)
        {
            string dir = Path.Combine(RootDirectory, StoragePaths.ProjectKey(solutionPath));
            // sessionId 由 Guid 生成（十六进制），但仍做一次净化，避免异常输入穿越目录
            string safeId = SanitizeFileName(sessionId);
            return Path.Combine(dir, safeId + ".json");
        }

        /// <summary>
        /// 写入快照。先写临时文件再原子替换，避免崩溃瞬间留下半截 JSON。
        /// 供后台线程调用；任何失败都只记日志，不影响任务本身。
        /// </summary>
        public static void Save(string? solutionPath, InflightTurnSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.SessionId))
                return;

            try
            {
                var path = GetSnapshotPath(solutionPath, snapshot.SessionId);
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var capped = new InflightTurnSnapshot
                {
                    SessionId = snapshot.SessionId,
                    TurnId = snapshot.TurnId,
                    AgentType = snapshot.AgentType,
                    Content = Cap(snapshot.Content),
                    TimelineContent = Cap(snapshot.TimelineContent),
                    ReasoningContent = Cap(snapshot.ReasoningContent),
                    CapturedAt = snapshot.CapturedAt,
                };

                string json = JsonSerializer.Serialize(capped, JsonOptions);
                string temp = path + ".tmp";
                File.WriteAllText(temp, json, Encoding.UTF8);

                if (File.Exists(path))
                    File.Replace(temp, path, null);
                else
                    File.Move(temp, path);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Inflight] 写入执行中快照失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>读取快照；不存在或损坏时返回 null（损坏的文件顺手删除）。</summary>
        public static InflightTurnSnapshot? Load(string? solutionPath, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return null;

            var path = GetSnapshotPath(solutionPath, sessionId);
            if (!File.Exists(path))
                return null;

            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                return JsonSerializer.Deserialize<InflightTurnSnapshot>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Inflight] 读取执行中快照失败，按无快照处理: {ex.GetType().Name}: {ex.Message}");
                TryDelete(path);
                return null;
            }
        }

        /// <summary>删除快照（任务正常收尾或已被恢复后调用）。</summary>
        public static void Delete(string? solutionPath, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return;

            TryDelete(GetSnapshotPath(solutionPath, sessionId));
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Inflight] 删除快照失败（忽略）: {ex.GetType().Name}");
            }
        }

        private static string Cap(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.Length <= MaxFieldChars) return value;
            return value.Substring(0, MaxFieldChars);
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "_unknown";

            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                bool invalid = c == Path.DirectorySeparatorChar
                    || c == Path.AltDirectorySeparatorChar
                    || Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0;
                sb.Append(invalid ? '_' : c);
            }

            return sb.ToString();
        }
    }
}
