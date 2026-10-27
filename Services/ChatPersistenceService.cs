using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 对话持久化服务 — 按项目保存/加载多轮对话会话。
    /// 文件存储在 %LocalAppData%\DeepSeekVS\conversations\ 下，
    /// 以解决方案路径的哈希值作为文件名，每个文件包含该项目的所有会话。
    /// </summary>
    internal static class ChatPersistenceService
    {
        private static readonly string BaseDir = StoragePaths.ConversationsDirectory;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        /// <summary>
        /// 根据解决方案路径计算持久化文件路径。
        /// 使用 SHA256 哈希（取前16位）避免路径中的非法字符。
        /// </summary>
        public static string GetStoragePath(string? solutionPath)
        {
            Directory.CreateDirectory(BaseDir);
            return StoragePaths.ProjectFilePath(BaseDir, solutionPath);
        }

        #region Public Methods - Session Level

        /// <summary>
        /// 加载指定解决方案的所有会话。如果文件不存在或为空，返回空容器。
        /// 自动兼容旧版单对话格式（自动迁移到多会话格式）。
        /// </summary>
        public static SessionsContainer LoadSessions(string? solutionPath)
        {
            var filePath = GetStoragePath(solutionPath);
            bool isUnsaved = string.IsNullOrWhiteSpace(solutionPath);

            try
            {
                if (!File.Exists(filePath))
                {
                    Logger.Info($"会话文件不存在，创建空容器: {Path.GetFileName(filePath)}");
                    return new SessionsContainer { SolutionPath = solutionPath ?? "(unsaved)" };
                }

                // RAG-SOURCE: file-read 读取会话持久化文件（JSON 反序列化）
                var json = File.ReadAllText(filePath, Encoding.UTF8);

                var container = JsonSerializer.Deserialize<SessionsContainer>(json, JsonOptions);
                if (container != null)
                {
                    container.SolutionPath = solutionPath ?? "(unsaved)";

                    // ── 清理 _unsaved.json 中从未使用过的冗余空会话 ──
                    int originalCount = container.Sessions.Count;
                    if (isUnsaved && originalCount > 1)
                    {
                        container.Sessions = container.Sessions
                            .Where(s => !IsUnusedEmptySession(s))
                            .ToList();

                        if (container.Sessions.Count == 0)
                        {
                            container.Sessions.Add(new ChatSession
                            {
                                Id = Guid.NewGuid().ToString("N"),
                                // 标题留空，展示层回退为本地化文案；避免把语言写进持久化数据
                                Title = string.Empty,
                                CreatedAt = DateTime.Now,
                                LastActiveAt = DateTime.Now,
                            });
                        }

                        if (container.Sessions.All(s => s.Id != container.ActiveSessionId))
                        {
                            container.ActiveSessionId = container.Sessions[0].Id;
                        }

                        int removed = originalCount - container.Sessions.Count;
                        if (removed > 0)
                            Logger.Info($"[清理] 已移除 {removed} 个冗余空会话 ← {Path.GetFileName(filePath)}");
                    }

                    MigrateLegacyDefaultTitles(container);
                    Logger.Info($"已加载 {container.Sessions.Count} 个会话 ← {Path.GetFileName(filePath)}");
                    return container;
                }

                return new SessionsContainer { SolutionPath = solutionPath ?? "(unsaved)" };
            }
            catch (Exception ex)
            {
                Logger.Error($"加载会话失败: {Path.GetFileName(filePath)}", ex);
                return new SessionsContainer { SolutionPath = solutionPath ?? "(unsaved)" };
            }
        }

        /// <summary>
        /// 保存所有会话到文件。
        /// </summary>
        public static void SaveSessions(string? solutionPath, SessionsContainer container)
        {
            if (container == null) return;

            var filePath = GetStoragePath(solutionPath);
            container.LastSaved = DateTime.Now;
            container.SolutionPath = solutionPath ?? "(unsaved)";

            try
            {
                var json = JsonSerializer.Serialize(container, JsonOptions);
                File.WriteAllText(filePath, json, Encoding.UTF8);
                Logger.Info($"会话已保存 ({container.Sessions.Count} 个会话) → {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                Logger.Error(LocalizationService.Instance["session.saveFailed"], ex);
            }
        }

        /// <summary>
        /// 删除指定会话文件（整个项目的所有会话）。
        /// </summary>
        public static void DeleteAllSessions(string? solutionPath)
        {
            var filePath = GetStoragePath(solutionPath);
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    Logger.Info($"会话文件已删除: {Path.GetFileName(filePath)}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error(LocalizationService.Instance["session.deleteFailed"], ex);
            }
        }

        /// <summary>
        /// 迁移历史数据：旧版本把本地化的「新对话」文案直接写进 <see cref="ChatSession.Title"/>，
        /// 导致标题与语言强绑定。此处把任何语言的该文案还原为空标题。
        /// <para>
        /// 为什么必须迁移：旧数据在中文下存为「新对话」、英文下存为「New Conversation」，
        /// 而判定逻辑比较的是当前语言 —— 若不还原，切成另一语言后
        /// AI 标题生成会被误判为「标题已被用户改写」而跳过，空会话也不再被清理。
        /// </para>
        /// <para>可见性放宽到 internal 以便单元测试直接覆盖迁移规则（避免写真实用户数据目录）。</para>
        /// </summary>
        internal static void MigrateLegacyDefaultTitles(SessionsContainer container)
        {
            if (container?.Sessions == null) return;

            var legacyDefaults = GetLegacyDefaultTitleValues();
            if (legacyDefaults.Count == 0) return;

            int migrated = 0;
            foreach (var session in container.Sessions)
            {
                if (session == null || string.IsNullOrWhiteSpace(session.Title)) continue;

                // 仅当标题恰为某语言的「新对话」时才还原，用户自定义标题不受影响
                if (!legacyDefaults.Contains(session.Title.Trim())) continue;

                session.Title = string.Empty;
                migrated++;
            }

            if (migrated > 0)
                Logger.Info($"[清理] 已将 {migrated} 个旧版默认标题迁移为空标题（与语言解耦）");
        }

        /// <summary>缓存的所有语言「新对话」文案，用于识别旧版写入的默认标题。</summary>
        private static HashSet<string>? _legacyDefaultTitles;

        /// <summary>
        /// 读取指定语言的 locale JSON 原始文本。
        /// <para>
        /// 依次尝试多个根目录，不能只依赖 <c>Assembly.Location</c>：
        /// 测试宿主与部分插件加载场景会把程序集影子复制到临时目录，
        /// 此时程序集同级并不存在 Resources\Locales，会导致读取静默失败。
        /// </para>
        /// </summary>
        private static string? LoadLocaleJson(string languageCode)
        {
            foreach (var root in EnumerateCandidateRoots())
            {
                try
                {
                    var candidate = Path.Combine(root, "Resources", "Locales", languageCode + ".json");
                    if (File.Exists(candidate))
                        return File.ReadAllText(candidate, Encoding.UTF8);
                }
                catch (Exception)
                {
                    // 路径过长/无权限等，继续尝试下一个根目录
                }
            }

            return null;
        }

        /// <summary>按优先级枚举可能包含 Resources\Locales 的根目录。</summary>
        private static IEnumerable<string> EnumerateCandidateRoots()
        {
            string? asmDir = null;
            try { asmDir = Path.GetDirectoryName(typeof(ChatPersistenceService).Assembly.Location); }
            catch (Exception) { }

            if (!string.IsNullOrEmpty(asmDir))
            {
                yield return asmDir;

                // 从程序集目录逐级向上找（覆盖 bin\Debug\net472 → 扩展根目录等布局）
                var dir = new DirectoryInfo(asmDir);
                for (int i = 0; i < 6 && dir?.Parent != null; i++)
                {
                    dir = dir.Parent;
                    yield return dir.FullName;
                }
            }

            // 当前工作目录（VS 宿主下通常就是扩展部署目录）
            string? cwd = null;
            try { cwd = Directory.GetCurrentDirectory(); }
            catch (Exception) { }
            if (!string.IsNullOrEmpty(cwd)) yield return cwd;

            // 扩展安装目录：VS 会把扩展部署到固定位置，作为最后兜底
            // 注：结果先收集到局部变量再 yield，因为 yield return 不能出现在带 catch 的 try 内
            foreach (var dir in FindInstalledExtensionDirs())
            {
                yield return dir;
            }
        }

        /// <summary>
        /// 在 VS 扩展安装目录下查找本扩展的部署位置。
        /// 枚举失败时返回空集合（不影响其他候选根目录）。
        /// </summary>
        private static IReadOnlyList<string> FindInstalledExtensionDirs()
        {
            var results = new List<string>();

            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(localAppData)) return results;

                var extensionsRoot = Path.Combine(localAppData, "Microsoft", "VisualStudio");
                if (!Directory.Exists(extensionsRoot)) return results;

                foreach (var dll in Directory.EnumerateFiles(
                    extensionsRoot, "DeepSeek_v4_for_VisualStudio.dll", SearchOption.AllDirectories))
                {
                    var d = Path.GetDirectoryName(dll);
                    if (!string.IsNullOrEmpty(d)) results.Add(d);
                }
            }
            catch (Exception)
            {
                // 枚举失败不影响其他候选路径
            }

            return results;
        }

        /// <summary>
        /// 读取全部受支持语言中 <c>session.new</c> 的取值。
        /// 直接解析语言 JSON（而非走 LocalizationService），因为后者只持有当前语言的文案，
        /// 无法识别「在另一种语言下创建」的历史标题。
        /// </summary>
        private static HashSet<string> GetLegacyDefaultTitleValues()
        {
            if (_legacyDefaultTitles != null) return _legacyDefaultTitles;

            var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var lang in LocalizationService.SupportedLanguages)
            {
                try
                {
                    string? json = LoadLocaleJson(lang.Code);
                    if (string.IsNullOrWhiteSpace(json)) continue;

                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("session.new", out var el)
                        && el.ValueKind == JsonValueKind.String)
                    {
                        string? v = el.GetString();
                        if (!string.IsNullOrWhiteSpace(v)) values.Add(v.Trim());
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[清理] 读取 {lang.Code} 的 session.new 失败: {ex.Message}");
                }
            }

            _legacyDefaultTitles = values;
            return values;
        }

        /// <summary>
        /// 判断会话是否为「从未使用过的空会话」，用于清理冗余条目。
        /// 判据只看「标题是否为空」这一与语言无关的事实（可见性放宽到 internal
        /// 以便单元测试覆盖该契约）。
        /// </summary>
        internal static bool IsUnusedEmptySession(ChatSession session)
        {
            if (session == null) return true;

            // 有 TreeData → 使用过
            if (!string.IsNullOrWhiteSpace(session.TreeDataJson)) return false;

            // 有 API 历史记录 → 使用过
            if (session.ApiHistory.Count > 0) return false;

            // 已有标题（AI 生成或回退标题）→ 使用过。
            // 注意：这里**不能**拿标题与当前语言的「新对话」文案比较 ——
            // 标题不存本地化文案，转换后比较会因语言切换而失效
            // （旧缺陷：英文下建的空会话在中文界面不再被清理）。
            if (!session.HasEmptyTitle) return false;

            // 无 TreeData、无 ApiHistory、标题为空 → 未使用
            return true;
        }

        /// <summary>
        /// （已移除）历史版本曾在此按 <c>ReasoningTextPolicy</c> 的旧上限裁剪已落盘的
        /// <c>reasoning_content</c>。该行为会破坏官方要求的「完整回传」：
        /// 携带 tools 的请求在后续所有请求中必须完整回传 reasoning_content，否则 API 返回 400。
        /// 因此加载时不再对已持久化的思考内容做任何裁剪。
        /// </summary>
        #endregion
    }
}
