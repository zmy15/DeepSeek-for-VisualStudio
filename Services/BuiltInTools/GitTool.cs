using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// git 工具 — 支持常用 Git 操作，并显式提供安全的只读查询能力。
    /// 启动时自动检测 git 是否安装；危险操作通过 BaseAgent 审批流程控制，
    /// 只读 Agent 的写操作在工具层直接拒绝。
    /// </summary>
    public class GitTool : BuiltInToolBase
    {
        // ═══════════════════════════════════════════════════════════════
        // Git 安装检测（进程级缓存，首次调用时检测一次）
        // ═══════════════════════════════════════════════════════════════

        private static readonly Lazy<(bool Available, string Version)> _gitDetection =
            new(() => DetectGitInstallation());

        /// <summary>git 是否已安装</summary>
        public static bool IsGitAvailable => _gitDetection.Value.Available;

        /// <summary>git 版本字符串</summary>
        public static string GitVersion => _gitDetection.Value.Version;

        private static (bool, string) DetectGitInstallation()
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);

                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    Logger.Info($"[GitTool] git 已安装: {output}");
                    return (true, output);
                }

                Logger.Warn("[GitTool] git 未安装或不可用");
                return (false, string.Empty);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[GitTool] git 检测失败: {ex.Message}");
                return (false, string.Empty);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // 操作分类常量
        // ═══════════════════════════════════════════════════════════════

        /// <summary>只读操作 — 自动放行，无需审批</summary>
        private static readonly HashSet<string> ReadOnlyOps = new(StringComparer.OrdinalIgnoreCase)
        {
            "status", "diff", "log", "show",
            "describe", "tag", "rev-parse", "reflog", "ls-files",
        };

        /// <summary>写操作 — 在只读 Agent 中拒绝</summary>
        private static readonly HashSet<string> WriteOps = new(StringComparer.OrdinalIgnoreCase)
        {
            "add", "commit", "branch", "checkout", "merge", "pull", "stash", "reset",
        };

        /// <summary>危险操作 — 无论是否为写操作，都需要审批</summary>
        private static readonly HashSet<string> DangerousOps = new(StringComparer.OrdinalIgnoreCase)
        {
            "push",
        };

        /// <summary>每种只读 operation 允许的布尔/固定值 flags。</summary>
        private static readonly Dictionary<string, HashSet<string>> AllowedGitFlags = new(StringComparer.OrdinalIgnoreCase)
        {
            ["status"] = new(StringComparer.Ordinal) { "--short", "--branch", "--porcelain", "--porcelain=v1", "--porcelain=v2", "--untracked-files=all", "--untracked-files=normal", "--untracked-files=no" },
            ["diff"] = new(StringComparer.Ordinal) { "--stat", "--shortstat", "--numstat", "--name-only", "--name-status", "--cached", "--staged", "--check", "--word-diff", "--ignore-space-at-eol", "--ignore-all-space", "--ignore-blank-lines", "--binary", "--full-index", "--no-ext-diff", "--no-textconv" },
            ["log"] = new(StringComparer.Ordinal) { "-s", "--no-patch", "--oneline", "--graph", "--decorate", "--all", "--first-parent", "--stat", "--shortstat", "--numstat", "--name-only", "--name-status", "--follow", "--reverse", "--merges", "--no-merges", "--pickaxe-regex", "--full-history", "--simplify-merges", "--topo-order", "--date-order", "--no-ext-diff", "--no-textconv" },
            ["show"] = new(StringComparer.Ordinal) { "-s", "--no-patch", "--stat", "--shortstat", "--numstat", "--name-only", "--name-status", "--oneline", "--decorate", "--no-ext-diff", "--no-textconv" },
            ["describe"] = new(StringComparer.Ordinal) { "--tags", "--long", "--always", "--dirty" },
            ["tag"] = new(StringComparer.Ordinal) { "--list" },
            ["rev-parse"] = new(StringComparer.Ordinal) { "--short", "--verify", "--symbolic", "--symbolic-full-name", "--abbrev-ref", "--is-inside-work-tree", "--show-toplevel", "--show-prefix", "--is-bare-repository" },
            ["reflog"] = new(StringComparer.Ordinal) { "--date=iso", "--date=relative" },
            ["ls-files"] = new(StringComparer.Ordinal) { "--cached", "--deleted", "--modified", "--others", "--exclude-standard", "--stage", "--unmerged", "--directory", "--error-unmatch" },
            ["add"] = new(StringComparer.Ordinal) { "--all", "-A", "--update", "-u", "--intent-to-add", "-N", "--dry-run", "-n" },
            ["commit"] = new(StringComparer.Ordinal) { "--amend", "--no-edit", "--allow-empty", "--allow-empty-message", "--signoff", "--no-verify" },
            ["branch"] = new(StringComparer.Ordinal) { "--list", "-l", "--all", "-a", "--remotes", "-r", "--verbose", "-v", "--show-current" },
            ["checkout"] = new(StringComparer.Ordinal) { "--track", "-t", "--detach", "--quiet", "-q" },
            ["merge"] = new(StringComparer.Ordinal) { "--ff-only", "--no-ff", "--squash", "--no-commit", "--abort", "--continue", "--quit" },
            ["pull"] = new(StringComparer.Ordinal) { "--rebase", "--ff-only", "--no-ff", "--autostash", "--no-rebase" },
            ["push"] = new(StringComparer.Ordinal) { "--dry-run", "--set-upstream", "-u" },
            ["stash"] = new(StringComparer.Ordinal) { "--include-untracked", "-u", "--keep-index", "--staged", "--quiet" },
            ["reset"] = new(StringComparer.Ordinal) { "--soft", "--mixed", "--quiet" },
        };

        /// <summary>每种只读 operation 允许的、需要后续值的 flags。</summary>
        private static readonly Dictionary<string, HashSet<string>> AllowedGitValueFlags = new(StringComparer.OrdinalIgnoreCase)
        {
            ["log"] = new(StringComparer.Ordinal) { "-S", "-G", "--grep", "--author", "--committer", "--since", "--until", "--format", "--date", "--max-count", "--skip", "--ancestry-path", "--diff-filter" },
            ["show"] = new(StringComparer.Ordinal) { "--format", "--date", "--diff-filter" },
            ["diff"] = new(StringComparer.Ordinal) { "--diff-filter", "--ignore-matching-lines" },
            ["describe"] = new(StringComparer.Ordinal) { "--match", "--exclude" },
            ["tag"] = new(StringComparer.Ordinal) { "--sort", "--merged", "--no-merged", "--contains", "--points-at", "--format" },
        };

        /// <summary>所有有效操作</summary>
        private static readonly HashSet<string> AllOps = new(StringComparer.OrdinalIgnoreCase)
        {
            "status", "diff", "log", "show", "describe", "tag",
            "rev-parse", "reflog", "ls-files",
            "add", "commit", "branch", "checkout", "merge", "pull", "push", "stash", "reset",
        };

        /// <summary>同步模式超时</summary>
        private static readonly TimeSpan SyncTimeout = TimeSpan.FromMinutes(2);

        /// <summary>
        /// 当前调用 Agent 类型（由 BaseAgent 在执行前设置，用于运行时权限校验）。
        /// AskAgent / ExploreAgent 只能执行只读操作。
        /// P1-7：用 AsyncLocal 隔离并发 Agent，避免"类级静态可写"被同时运行的
        /// 其他 Agent 覆盖，导致只读判定被静默绕过或反向误拦。
        /// </summary>
        private static readonly System.Threading.AsyncLocal<AgentType?> CurrentAgentTypeAsyncLocal = new();
        public static AgentType? CurrentAgentType
        {
            get => CurrentAgentTypeAsyncLocal.Value;
            set => CurrentAgentTypeAsyncLocal.Value = value;
        }

        /// <summary>
        /// 判断 Git 操作是否可安全地用于只读 Agent。
        /// 统一供 GitTool 和 BaseAgent 审批逻辑使用，避免两处白名单漂移。
        /// </summary>
        internal static bool IsReadOnlyOperation(
            string operation,
            string branch,
            string mode,
            string path,
            bool delete)
        {
            return ReadOnlyOps.Contains(operation)
                || (operation == "branch" && string.IsNullOrEmpty(branch) && !delete)
                || (operation == "stash"
                    && (string.Equals(mode, "list", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(mode, "show", StringComparison.OrdinalIgnoreCase)))
                || (operation == "reset" && !string.IsNullOrEmpty(path));
        }

        /// <summary>
        /// 判断 Agent 是否属于只读模式。
        /// </summary>
        /// <param name="agentType">当前 Agent 类型；为空表示未限定 Agent。</param>
        /// <returns>Ask/Explore Agent 返回 true，其余返回 false。</returns>
        internal static bool IsReadOnlyAgent(AgentType? agentType)
        {
            return agentType is AgentType.Ask or AgentType.Explore;
        }

        /// <summary>
        /// 统一判断 Git 调用是否需要进入审批流程。
        /// 只读 Agent 的写操作直接交给 GitTool 拦截，不先用审批覆盖权限边界。
        /// </summary>
        /// <param name="agentType">当前 Agent 类型。</param>
        /// <param name="operation">Git 操作名。</param>
        /// <param name="isReadOnly">该操作在当前参数下是否属于只读操作。</param>
        /// <param name="mode">操作的细化模式，例如 reset hard、stash drop。</param>
        /// <param name="delete">是否请求删除分支。</param>
        /// <param name="force">是否请求强制操作。</param>
        /// <param name="flags">额外 Git 参数。</param>
        /// <param name="reason">需要审批的具体参数或操作。</param>
        /// <returns>需要审批时返回 true。</returns>
        internal static bool RequiresApproval(
            AgentType? agentType,
            string operation,
            bool isReadOnly,
            string mode,
            bool delete,
            bool force,
            IReadOnlyList<string>? flags,
            out string reason)
        {
            reason = string.Empty;

            // 只读 Agent 的写操作必须在执行层阻断；审批只能放宽危险参数，
            // 不能放宽 Agent 的职责边界。
            if (IsReadOnlyAgent(agentType) && !isReadOnly)
                return false;

            if (FlagsRequireApproval(operation, flags, out string flagReason))
            {
                reason = flagReason;
                return true;
            }

            string dangerousReason = GetDangerousOperationReason(operation, mode, delete, force);
            if (string.IsNullOrEmpty(dangerousReason))
                return false;

            reason = dangerousReason;
            return true;
        }

        /// <summary>
        /// 获取危险操作或危险参数的审批原因。
        /// </summary>
        /// <param name="operation">Git 操作名。</param>
        /// <param name="mode">操作的细化模式。</param>
        /// <param name="delete">是否请求删除分支。</param>
        /// <param name="force">是否请求强制操作。</param>
        /// <returns>需要审批时返回可读原因，否则返回空字符串。</returns>
        internal static string GetDangerousOperationReason(
            string operation,
            string mode,
            bool delete,
            bool force)
        {
            if (DangerousOps.Contains(operation))
                return operation;

            if (string.Equals(operation, "reset", StringComparison.OrdinalIgnoreCase)
                && string.Equals(mode, "hard", StringComparison.OrdinalIgnoreCase))
                return "--hard";

            if (string.Equals(operation, "branch", StringComparison.OrdinalIgnoreCase)
                && delete
                && force)
                return "--force --delete";

            if (string.Equals(operation, "stash", StringComparison.OrdinalIgnoreCase)
                && string.Equals(mode, "drop", StringComparison.OrdinalIgnoreCase))
                return "drop";

            return string.Empty;
        }

        public override string Name => "git";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "git",
                    Description = L["tool.git.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            operation = new
                            {
                                type = "string",
                                description = L["tool.git.param.operation"],
                                @enum = new[]
                                {
                                    "status", "diff", "log", "show", "describe", "tag",
                                    "rev-parse", "reflog", "ls-files",
                                    "add", "commit", "branch", "checkout", "merge", "pull", "push", "stash", "reset"
                                }
                            },
                            path = new { type = "string", description = L["tool.git.param.path"] },
                            message = new { type = "string", description = L["tool.git.param.message"] },
                            branch = new { type = "string", description = L["tool.git.param.branch"] },
                            reference = new { type = "string", description = L["tool.git.param.reference"] },
                            range = new { type = "string", description = L["tool.git.param.range"] },
                            files = new
                            {
                                type = "array",
                                items = new { type = "string" },
                                description = L["tool.git.param.files"]
                            },
                            staged = new { type = "boolean", description = L["tool.git.param.staged"] },
                            count = new { type = "integer", description = L["tool.git.param.count"] },
                            oneline = new { type = "boolean", description = L["tool.git.param.oneline"] },
                            search = new { type = "string", description = L["tool.git.param.search"] },
                            searchRegex = new { type = "boolean", description = L["tool.git.param.searchRegex"] },
                            noDiff = new { type = "boolean", description = L["tool.git.param.noDiff"] },
                            flags = new
                            {
                                type = "array",
                                items = new { type = "string" },
                                description = L["tool.git.param.flags"]
                            },
                            delete = new { type = "boolean", description = L["tool.git.param.delete"] },
                            force = new { type = "boolean", description = L["tool.git.param.force"] },
                            remote = new { type = "string", description = L["tool.git.param.remote"] },
                            mode = new { type = "string", description = L["tool.git.param.mode"] },
                            purpose = new { type = "string", description = L["tool.git.param.purpose"] },
                        },
                        required = new[] { "operation" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string operation = GetStringArg(args, "operation");
            string desc = operation.ToLowerInvariant() switch
            {
                "status" => L["tool.git.displayStatus"],
                "diff" => L["tool.git.displayDiff"],
                "log" => L["tool.git.displayLog"],
                "show" => L["tool.git.displayShow"],
                "describe" => L["tool.git.displayDescribe"],
                "tag" => L["tool.git.displayTag"],
                "rev-parse" => L["tool.git.displayRevParse"],
                "reflog" => L["tool.git.displayReflog"],
                "ls-files" => L["tool.git.displayLsFiles"],
                "add" => L["tool.git.displayAdd"],
                "commit" => L["tool.git.displayCommit"],
                "branch" => L["tool.git.displayBranch"],
                "checkout" => L["tool.git.displayCheckout"],
                "merge" => L["tool.git.displayMerge"],
                "pull" => L["tool.git.displayPull"],
                "push" => L["tool.git.displayPush"],
                "stash" => L["tool.git.displayStash"],
                "reset" => L["tool.git.displayReset"],
                _ => $"git {operation}",
            };
            return desc;
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return L["tool.common.noResult"];
            if (toolResult.StartsWith("Error: ") || toolResult.StartsWith("[BLOCKED] ")) return toolResult;
            if (toolResult.Contains("exit code: 0"))
                return L["tool.git.success"];
            return L["tool.git.executed"];
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string operation = GetStringArg(args, "operation").ToLowerInvariant().Trim();

            // ── 参数验证 ──
            if (string.IsNullOrEmpty(operation))
                return L["tool.git.missingOperation"];

            if (!AllOps.Contains(operation))
                return string.Format(L["tool.git.unknownOperation"], operation);

            // ── git 安装检测 ──
            if (!IsGitAvailable)
                return L["tool.git.notInstalled"];

            // ── 工作目录检测（查找 .git 目录）──
            string? workingDir = NormalizeWorkspaceRoot(workspaceRoot);
            string? gitDir = FindGitDir(workingDir);
            if (gitDir == null)
                return L["tool.git.noRepo"];

            // ── 运行时 Agent 权限校验 ──
            // AskAgent / ExploreAgent 只能执行只读操作；EditAgent/BuildAgent 无限制
            if (CurrentAgentType is AgentType.Ask or AgentType.Explore)
            {
                bool isReadOnly = IsReadOnlyOperation(
                    operation,
                    GetStringArg(args, "branch"),
                    GetStringArg(args, "mode"),
                    GetStringArg(args, "path"),
                    GetBoolArg(args, "delete"));

                if (!isReadOnly)
                {
                    Logger.Warn($"[GitTool] 只读 Agent 尝试执行写操作被拒绝 ({CurrentAgentType}): git {operation}");
                    return string.Format(
                        L["tool.git.agentBlocked"],
                        CurrentAgentType.ToString(),
                        operation);
                }
            }

            // ── 构建 git 命令行 ──
            string gitCommand = BuildGitCommand(operation, args, gitDir);
            if (gitCommand.StartsWith("[BLOCKED] "))
                return gitCommand; // 被硬拒绝的操作

            // ── 执行 git 命令 ──
            try
            {
                return await RunGitCommandAsync(gitCommand, gitDir);
            }
            catch (Exception ex)
            {
                Logger.Error($"[GitTool] git {operation} 执行异常: {ex.Message}", ex);
                return string.Format(L["tool.git.failed"], ex.Message);
            }
        }

        #region Git Command Builder

        /// <summary>
        /// 根据操作类型和参数构建 git 命令。
        /// 非白名单或危险 flags 由 BaseAgent 进入审批流程；格式错误返回 Error。
        /// </summary>
        internal string BuildGitCommand(string operation, Dictionary<string, JsonElement> args, string repoDir)
        {
            if (!TryValidateRevisionRange(operation, args, out string? rangeError))
                return rangeError!;

            if (!TryValidateGitFlags(operation, args, out string? blockedMessage))
                return blockedMessage!;

            string command = BuildBaseGitCommand(operation, args, repoDir);
            if (command.StartsWith("[BLOCKED] ", StringComparison.Ordinal))
                return command;

            string flags = FormatValidatedGitFlags(operation, args);
            return string.IsNullOrEmpty(flags)
                ? command
                : InsertFlagsAfterCommand(command, flags);
        }

        /// <summary>
        /// 读取 log/diff 的提交范围；range 优先，未提供时兼容旧的 reference 参数。
        /// </summary>
        private static string GetRevisionRange(Dictionary<string, JsonElement> args)
        {
            string range = GetStringArg(args, "range");
            return string.IsNullOrEmpty(range) ? GetStringArg(args, "reference") : range;
        }

        /// <summary>
        /// 校验 log/diff 的提交范围，避免位置参数被解释为危险选项。
        /// </summary>
        private static bool TryValidateRevisionRange(
            string operation,
            Dictionary<string, JsonElement> args,
            out string? blockedMessage)
        {
            blockedMessage = null;
            if (!string.Equals(operation, "log", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(operation, "diff", StringComparison.OrdinalIgnoreCase))
                return true;

            string range = GetRevisionRange(args);
            if (string.IsNullOrWhiteSpace(range))
                return true;

            bool hasControlChars = range.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0;
            bool validShape = System.Text.RegularExpressions.Regex.IsMatch(
                range,
                @"^[0-9A-Za-z_./@{}^~:-]+$");
            if (range.StartsWith("-", StringComparison.Ordinal) || hasControlChars || !validShape)
            {
                blockedMessage = L.Format("tool.git.rangeInvalid", range);
                return false;
            }

            return true;
        }

        private static bool TryValidateGitFlags(
            string operation,
            Dictionary<string, JsonElement> args,
            out string? blockedMessage)
        {
            blockedMessage = null;
            string[] flags = GetStringArrayArg(args, "flags") ?? Array.Empty<string>();
            if (flags.Length == 0)
                return true;

            AllowedGitFlags.TryGetValue(operation, out var exactFlags);
            AllowedGitValueFlags.TryGetValue(operation, out var valueFlags);

            for (int i = 0; i < flags.Length; i++)
            {
                string flag = (flags[i] ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(flag)
                    || !flag.StartsWith("-", StringComparison.Ordinal)
                    || flag.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                {
                    blockedMessage = L.Format(
                        "tool.git.flagInvalid",
                        operation,
                        flag);
                    return false;
                }

                if (exactFlags?.Contains(flag) == true)
                    continue;

                if (valueFlags?.Contains(flag) == true)
                {
                    if (i + 1 >= flags.Length)
                    {
                        blockedMessage = L.Format(
                            "tool.git.flagInvalid",
                            operation,
                            flag);
                        return false;
                    }
                    i++;
                    continue;
                }

                // Non-whitelisted but syntactically valid flags are allowed here;
                // BaseAgent requires explicit approval before execution.
                if (i + 1 < flags.Length
                    && !string.IsNullOrWhiteSpace(flags[i + 1])
                    && !flags[i + 1].TrimStart().StartsWith("-", StringComparison.Ordinal))
                {
                    i++;
                }
            }

            return true;
        }

        private static string FormatValidatedGitFlags(string operation, Dictionary<string, JsonElement> args)
        {
            string[] flags = GetStringArrayArg(args, "flags") ?? Array.Empty<string>();
            if (flags.Length == 0)
                return string.Empty;

            AllowedGitFlags.TryGetValue(operation, out var exactFlags);
            AllowedGitValueFlags.TryGetValue(operation, out var valueFlags);
            var sb = new StringBuilder();

            for (int i = 0; i < flags.Length; i++)
            {
                string flag = (flags[i] ?? string.Empty).Trim();
                if (exactFlags?.Contains(flag) == true)
                {
                    sb.Append(' ').Append(flag);
                }
                else if (valueFlags?.Contains(flag) == true)
                {
                    string value = flags[++i] ?? string.Empty;
                    sb.Append(' ').Append(flag).Append(' ').Append(QuoteFlagValue(value));
                }
                else if (TryGetAttachedValuePrefix(flag, valueFlags, out string? prefix))
                {
                    string value = flag.Substring(prefix!.Length);
                    sb.Append(' ').Append(prefix);
                    if (!prefix.EndsWith("=", StringComparison.Ordinal))
                        sb.Append(' ');
                    sb.Append(QuoteFlagValue(value));
                }
                else
                {
                    int equalsIndex = flag.IndexOf('=');
                    if (equalsIndex > 0 && equalsIndex < flag.Length - 1)
                    {
                        sb.Append(' ')
                          .Append(flag.Substring(0, equalsIndex + 1))
                          .Append(QuoteFlagValue(flag.Substring(equalsIndex + 1)));
                    }
                    else if (i + 1 < flags.Length
                        && !string.IsNullOrWhiteSpace(flags[i + 1])
                        && !flags[i + 1].TrimStart().StartsWith("-", StringComparison.Ordinal))
                    {
                        string value = flags[++i] ?? string.Empty;
                        sb.Append(' ').Append(flag).Append(' ').Append(QuoteFlagValue(value));
                    }
                    else
                    {
                        sb.Append(' ').Append(flag);
                    }
                }
            }

            return sb.ToString().Trim();
        }

        private static bool TryGetAttachedValuePrefix(
            string flag,
            HashSet<string>? valueFlags,
            out string? prefix)
        {
            prefix = null;
            if (valueFlags == null)
                return false;

            foreach (string valueFlag in valueFlags)
            {
                if (valueFlag.StartsWith("--", StringComparison.Ordinal))
                {
                    string candidate = valueFlag + "=";
                    if (flag.StartsWith(candidate, StringComparison.Ordinal)
                        && flag.Length > candidate.Length)
                    {
                        prefix = candidate;
                        return true;
                    }
                }
                else if (valueFlag.Length == 2
                    && flag.StartsWith(valueFlag, StringComparison.Ordinal)
                    && flag.Length > valueFlag.Length)
                {
                    prefix = valueFlag;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 判断 flags 中是否存在需要用户审批的非白名单参数。
        /// </summary>
        internal static bool FlagsRequireApproval(
            string operation,
            IReadOnlyList<string>? flags,
            out string reason)
        {
            reason = string.Empty;
            if (flags == null || flags.Count == 0)
                return false;

            AllowedGitFlags.TryGetValue(operation, out var exactFlags);
            AllowedGitValueFlags.TryGetValue(operation, out var valueFlags);
            var dangerous = new List<string>();

            for (int i = 0; i < flags.Count; i++)
            {
                string flag = (flags[i] ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(flag))
                    continue;

                if (exactFlags?.Contains(flag) == true)
                    continue;

                if (valueFlags?.Contains(flag) == true)
                {
                    if (i + 1 >= flags.Count)
                        return false; // malformed flag: let BuildGitCommand return a format error
                    i++;
                    continue;
                }

                if (TryGetAttachedValuePrefix(flag, valueFlags, out _))
                    continue;

                dangerous.Add(flag);
                if (i + 1 < flags.Count
                    && !string.IsNullOrWhiteSpace(flags[i + 1])
                    && !flags[i + 1].TrimStart().StartsWith("-", StringComparison.Ordinal))
                {
                    dangerous.Add(flags[i + 1]);
                    i++;
                }
            }

            if (dangerous.Count == 0)
                return false;

            reason = string.Join(" ", dangerous);
            return true;
        }

        private static string QuoteFlagValue(string value)
        {
            return "\"" + EscapeArg(value) + "\"";
        }

        private static string InsertFlagsAfterCommand(string command, string flags)
        {
            int firstSpace = command.IndexOf(' ');
            if (firstSpace < 0)
                return command + " " + flags;

            string commandName = command.Substring(0, firstSpace);
            if (string.Equals(commandName, "stash", StringComparison.OrdinalIgnoreCase))
            {
                int secondSpace = command.IndexOf(' ', firstSpace + 1);
                return secondSpace < 0
                    ? command + " " + flags
                    : command.Substring(0, secondSpace) + " " + flags + command.Substring(secondSpace);
            }

            return command.Substring(0, firstSpace) + " " + flags + command.Substring(firstSpace);
        }

        private string BuildBaseGitCommand(string operation, Dictionary<string, JsonElement> args, string repoDir)
        {
            switch (operation)
            {
                case "status":
                    {
                        string path = GetStringArg(args, "path");
                        return string.IsNullOrEmpty(path) ? "status --porcelain" : $"status --porcelain -- \"{EscapeArg(path)}\"";
                    }

                case "diff":
                    {
                        bool staged = GetBoolArg(args, "staged");
                        string path = GetStringArg(args, "path");
                        string range = GetRevisionRange(args);
                        var sb = new StringBuilder("diff");
                        if (staged) sb.Append(" --staged");
                        if (!string.IsNullOrEmpty(range)) sb.Append($" {EscapeArg(range)}");
                        if (!string.IsNullOrEmpty(path)) sb.Append($" -- \"{EscapeArg(path)}\"");
                        return sb.ToString();
                    }

                case "log":
                    {
                        int count = GetIntArg(args, "count", 10);
                        bool oneline = GetBoolArg(args, "oneline");
                        bool noDiff = GetBoolArg(args, "noDiff");
                        string search = GetStringArg(args, "search");
                        bool searchRegex = GetBoolArg(args, "searchRegex");
                        string path = GetStringArg(args, "path");
                        string range = GetRevisionRange(args);
                        var sb = new StringBuilder("log");
                        if (oneline) sb.Append(" --oneline");
                        if (noDiff) sb.Append(" -s");
                        int clamped = count < 1 ? 1 : (count > 50 ? 50 : count);
                        sb.Append($" -{clamped}");
                        if (!string.IsNullOrEmpty(search))
                        {
                            sb.Append($" -S \"{EscapeArg(search)}\"");
                            if (searchRegex) sb.Append(" --pickaxe-regex");
                        }
                        if (!string.IsNullOrEmpty(range)) sb.Append($" {EscapeArg(range)}");
                        if (!string.IsNullOrEmpty(path)) sb.Append($" -- \"{EscapeArg(path)}\"");
                        return sb.ToString();
                    }

                case "show":
                    {
                        string commit = GetStringArg(args, "reference");
                        if (string.IsNullOrEmpty(commit))
                            commit = GetStringArg(args, "branch"); // backward compatibility
                        bool noDiff = GetBoolArg(args, "noDiff");
                        string path = GetStringArg(args, "path");
                        var sb = new StringBuilder("show");
                        if (noDiff) sb.Append(" -s");
                        if (!string.IsNullOrEmpty(commit))
                            sb.Append($" {EscapeArg(commit)}");
                        else
                            sb.Append(" HEAD");
                        if (!string.IsNullOrEmpty(path))
                            sb.Append($" -- \"{EscapeArg(path)}\"");
                        return sb.ToString();
                    }

                case "describe":
                    {
                        string reference = GetStringArg(args, "reference");
                        if (string.IsNullOrEmpty(reference))
                            reference = GetStringArg(args, "branch");
                        if (string.IsNullOrEmpty(reference))
                            reference = "HEAD";
                        return $"describe --tags --abbrev=0 {EscapeArg(reference)}";
                    }

                case "tag":
                    {
                        string reference = GetStringArg(args, "reference");
                        if (string.IsNullOrEmpty(reference))
                            reference = GetStringArg(args, "branch");

                        var sb = new StringBuilder("tag --list --sort=-creatordate");
                        if (!string.IsNullOrEmpty(reference))
                            sb.Append($" --merged {EscapeArg(reference)}");
                        return sb.ToString();
                    }

                case "rev-parse":
                    {
                        string reference = GetStringArg(args, "reference");
                        if (string.IsNullOrEmpty(reference))
                            reference = GetStringArg(args, "branch");
                        if (string.IsNullOrEmpty(reference))
                            reference = "HEAD";

                        string mode = GetStringArg(args, "mode").ToLowerInvariant().Trim();
                        return mode == "short"
                            ? $"rev-parse --short {EscapeArg(reference)}"
                            : $"rev-parse {EscapeArg(reference)}";
                    }

                case "reflog":
                    {
                        int count = GetIntArg(args, "count", 20);
                        int clamped = count < 1 ? 1 : (count > 50 ? 50 : count);
                        string reference = GetStringArg(args, "reference");
                        if (string.IsNullOrEmpty(reference))
                            reference = GetStringArg(args, "branch");

                        var sb = new StringBuilder($"reflog show --date=iso -{clamped}");
                        if (!string.IsNullOrEmpty(reference))
                            sb.Append($" {EscapeArg(reference)}");
                        return sb.ToString();
                    }

                case "ls-files":
                    {
                        string path = GetStringArg(args, "path");
                        return string.IsNullOrEmpty(path)
                            ? "ls-files"
                            : $"ls-files -- \"{EscapeArg(path)}\"";
                    }

                case "add":
                    {
                        var files = GetStringArrayArg(args, "files");
                        if (files == null || files.Length == 0)
                            return "add .";
                        return $"add -- {string.Join(" ", Array.ConvertAll(files, EscapeArg))}";
                    }

                case "commit":
                    {
                        string message = GetStringArg(args, "message");
                        if (string.IsNullOrWhiteSpace(message))
                            return "[BLOCKED] " + L["tool.git.commitNoMessage"];
                        var files = GetStringArrayArg(args, "files");
                        string escapedMsg = EscapeArg(message);
                        if (files == null || files.Length == 0)
                            return $"commit -m \"{escapedMsg}\"";
                        return $"commit -m \"{escapedMsg}\" -- {string.Join(" ", Array.ConvertAll(files, EscapeArg))}";
                    }

                case "branch":
                    {
                        string branch = GetStringArg(args, "branch");
                        bool delete = GetBoolArg(args, "delete");
                        bool force = GetBoolArg(args, "force");

                        if (delete)
                        {
                            if (string.IsNullOrEmpty(branch))
                                return "branch --list";
                            return force
                                ? $"branch -D \"{EscapeArg(branch)}\""
                                : $"branch -d \"{EscapeArg(branch)}\"";
                        }

                        if (!string.IsNullOrEmpty(branch))
                            return $"branch \"{EscapeArg(branch)}\"";

                        string listMode = GetStringArg(args, "mode").ToLowerInvariant().Trim();
                        return listMode switch
                        {
                            "all" => "branch --all",
                            "remote" => "branch --remotes",
                            "verbose" => "branch -vv",
                            _ => "branch --list",
                        };
                    }

                case "checkout":
                    {
                        string branch = GetStringArg(args, "branch");
                        if (string.IsNullOrEmpty(branch))
                            return "[BLOCKED] " + L["tool.git.checkoutNoBranch"];
                        return $"checkout \"{EscapeArg(branch)}\"";
                    }

                case "merge":
                    {
                        string branch = GetStringArg(args, "branch");
                        if (string.IsNullOrEmpty(branch))
                            return "[BLOCKED] " + L["tool.git.mergeNoBranch"];

                        string mode = GetStringArg(args, "mode").ToLowerInvariant().Trim();
                        return mode switch
                        {
                            "ff-only" => $"merge --ff-only \"{EscapeArg(branch)}\"",
                            "no-ff" => $"merge --no-ff \"{EscapeArg(branch)}\"",
                            "squash" => $"merge --squash \"{EscapeArg(branch)}\"",
                            _ => $"merge \"{EscapeArg(branch)}\"",
                        };
                    }

                case "pull":
                    {
                        string remote = GetStringArg(args, "remote");
                        string branch = GetStringArg(args, "branch");
                        if (string.IsNullOrEmpty(remote)) remote = "origin";
                        return string.IsNullOrEmpty(branch)
                            ? $"pull {EscapeArg(remote)}"
                            : $"pull {EscapeArg(remote)} \"{EscapeArg(branch)}\"";
                    }

                case "push":
                    {
                        string remote = GetStringArg(args, "remote");
                        string branch = GetStringArg(args, "branch");
                        bool force = GetBoolArg(args, "force");
                        if (string.IsNullOrEmpty(remote)) remote = "origin";

                        string forceFlag = force ? " --force" : string.Empty;
                        return string.IsNullOrEmpty(branch)
                            ? $"push{forceFlag} {EscapeArg(remote)}"
                            : $"push{forceFlag} {EscapeArg(remote)} \"{EscapeArg(branch)}\"";
                    }

                case "stash":
                    {
                        string mode = GetStringArg(args, "mode").ToLowerInvariant().Trim();
                        string message = GetStringArg(args, "message");
                        return mode switch
                        {
                            "pop" => "stash pop",
                            "list" => "stash list",
                            "show" => "stash show",
                            "apply" => "stash apply",
                            "drop" => "stash drop",
                            _ => string.IsNullOrEmpty(message)
                                ? "stash push"
                                : $"stash push -m \"{EscapeArg(message)}\"",
                        };
                    }

                case "reset":
                    {
                        string path = GetStringArg(args, "path");
                        string mode = GetStringArg(args, "mode").ToLowerInvariant().Trim();

                        // 如果指定了 path，为 unstage 操作（reset HEAD <path>）
                        if (!string.IsNullOrEmpty(path))
                            return $"reset HEAD -- \"{EscapeArg(path)}\"";

                        // 否则为模式 reset
                        bool hasHardFlag = (GetStringArrayArg(args, "flags") ?? Array.Empty<string>())
                            .Any(f => string.Equals(f, "--hard", StringComparison.Ordinal));
                        string resetMode = mode switch
                        {
                            "hard" => "--hard",
                            "soft" => "--soft",
                            "mixed" => "--mixed",
                            _ => hasHardFlag ? string.Empty : "--mixed",
                        };
                        return string.IsNullOrEmpty(resetMode)
                            ? "reset HEAD~1"
                            : $"reset {resetMode} HEAD~1";
                    }

                default:
                    return $"[BLOCKED] Unknown operation: {operation}";
            }
        }

        #endregion

        #region Git Execution

        /// <summary>
        /// 执行 git 命令并返回格式化输出。
        /// </summary>
        private async Task<string> RunGitCommandAsync(string gitArgs, string workingDir)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = gitArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir,
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            // 超时保护
            var timeoutTask = Task.Delay(SyncTimeout);
            var readTask = Task.WhenAll(stdoutTask, stderrTask);
            var completed = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);

            if (completed == timeoutTask)
            {
                try { process.Kill(); } catch { }
                process.Dispose();
                return $"Timeout: " + string.Format(L["tool.git.timeout"], SyncTimeout.TotalSeconds);
            }

            await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
            int exitCode = process.ExitCode;
            string stdout = stdoutTask.Result;
            string stderr = stderrTask.Result;

            var sb = new StringBuilder();
            sb.AppendLine($"git 输出 (退出码: {exitCode}):");
            if (!string.IsNullOrWhiteSpace(stdout))
                sb.AppendLine(stdout.TrimEnd());
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                // git 常将信息性消息（如 "Switched to branch"）输出到 stderr
                if (exitCode == 0)
                    sb.AppendLine(stderr.TrimEnd());
                else
                {
                    sb.AppendLine("--- STDERR ---");
                    sb.AppendLine(stderr.TrimEnd());
                }
            }

            string result = sb.ToString().TrimEnd();

            // 截断过长输出
            const int maxOutput = 60000;
            if (result.Length > maxOutput)
                result = result.Substring(0, maxOutput) + $"\n\n...(截断，总输出 {result.Length} 字符)";

            return string.IsNullOrWhiteSpace(result)
                ? L["tool.git.noOutput"]
                : result;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// 查找工作目录或其父目录中的 .git 目录。
        /// </summary>
        private static string? FindGitDir(string? startDir)
        {
            if (string.IsNullOrEmpty(startDir))
                return null;

            try
            {
                string? current = Path.GetFullPath(startDir);
                while (current != null)
                {
                    string gitPath = Path.Combine(current, ".git");
                    if (Directory.Exists(gitPath) || File.Exists(gitPath))
                        return current;

                    string? parent = Path.GetDirectoryName(current);
                    if (parent == current) break; // 到达文件系统根
                    current = parent;
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// 转义命令行参数中的特殊字符。
        /// </summary>
        private static string EscapeArg(string arg)
        {
            if (string.IsNullOrEmpty(arg))
                return "\"\"";
            // 如果参数不含空格或特殊字符，直接返回
            if (!arg.Contains(" ") && !arg.Contains("\"") && !arg.Contains("\\"))
                return arg;
            // 转义双引号和反斜杠
            return arg.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        #endregion
    }
}
