using System;
using System.Collections.Generic;
using System.IO;
using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// Agent 路径解析与项目文件判定工具（从 <see cref="BaseAgent"/> 下沉）。
    /// 全部为无状态静态方法；<see cref="BaseAgent"/> 上保留同名转发以维持子类与既有测试的编译契约。
    /// </summary>
    internal static class AgentPathResolver
    {
        /// <summary>
        /// 需要用户确认才能修改的项目文件扩展名 / 文件名集合（内部实现用可写集合）。
        /// </summary>
        private static readonly HashSet<string> ProjectFileExtensionSet = new(StringComparer.OrdinalIgnoreCase)
        {
            ".vcxproj", ".csproj", ".fsproj", ".vbproj",
            ".sln", ".slnx",
            ".vcxproj.filters", ".vcxproj.user",
            ".csproj.user", ".vbproj.user",
            "CMakeLists.txt", "CMakeSettings.json",
            "packages.config", "Directory.Build.props", "Directory.Build.targets",
            ".props", ".targets",
            "Makefile", "makefile", "GNUmakefile",
            ".slnf",
        };

        /// <summary>
        /// 需要用户确认才能修改的项目文件扩展名 / 文件名集合。
        /// 以只读属性暴露，避免外部改写内部集合。
        /// </summary>
        internal static IReadOnlyCollection<string> ProjectFileExtensions => ProjectFileExtensionSet;

        /// <summary>
        /// 检查文件是否为项目配置文件（需要用户确认才能修改）。
        /// EditAgent / BuildAgent 共享使用。
        /// </summary>
        /// <param name="filePath">待判定的文件路径；允许为 <c>null</c>。</param>
        /// <returns>文件名为项目配置文件或扩展名在集合内时返回 <c>true</c>。</returns>
        internal static bool IsProjectFile(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            string fileName = System.IO.Path.GetFileName(filePath);
            string ext = System.IO.Path.GetExtension(filePath);
            return ProjectFileExtensionSet.Contains(fileName) || ProjectFileExtensionSet.Contains(ext);
        }

        /// <summary>
        /// 从 AgentContext 解析工作区根目录。
        /// BuildAgent / EditAgent / ExploreAgent 共享使用。
        /// </summary>
        /// <param name="context">Agent 执行上下文，提供 <c>SolutionPath</c>。</param>
        /// <returns>Workspace 根目录；当 SolutionPath 指向存在的文件时返回其所在目录。</returns>
        internal static string GetWorkspaceRoot(AgentContext context)
        {
            string root = context.SolutionPath ?? string.Empty;
            if (!string.IsNullOrEmpty(root) && File.Exists(root))
                root = Path.GetDirectoryName(root) ?? root;
            return root;
        }

        /// <summary>
        /// 将路径解析为绝对路径。
        /// </summary>
        /// <param name="filePath">待解析路径。</param>
        /// <param name="workspaceRoot">工作区根目录；为空时原样返回相对路径。</param>
        /// <returns>解析出的绝对路径。</returns>
        internal static string ResolveToAbsolutePath(string filePath, string? workspaceRoot)
        {
            if (Path.IsPathRooted(filePath))
                return Path.GetFullPath(filePath);

            if (!string.IsNullOrEmpty(workspaceRoot))
            {
                string candidate = Path.Combine(workspaceRoot, filePath.Replace('/', '\\'));
                return Path.GetFullPath(candidate);
            }

            return filePath;
        }

        /// <summary>
        /// 解析 AI 返回的文件路径（支持相对路径和 Unix 风格路径）。
        /// </summary>
        /// <param name="filePath">AI 返回的路径。</param>
        /// <param name="solutionPath">解决方案目录，用于拼接相对路径与同名文件搜索。</param>
        /// <returns>解析后的路径；无法解析时返回原始输入。</returns>
        internal static string ResolveFilePath(string filePath, string? solutionPath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return filePath;
            if (System.IO.Path.IsPathRooted(filePath) && System.IO.File.Exists(System.IO.Path.GetDirectoryName(filePath)))
                return filePath;

            if (System.IO.Path.IsPathRooted(filePath) && !string.IsNullOrEmpty(solutionPath))
            {
                string relativePart = filePath.TrimStart('/').Replace('/', '\\');
                string candidate = System.IO.Path.Combine(solutionPath, relativePart);
                if (System.IO.File.Exists(candidate))
                {
                    Logger.Info($"[PathResolve] AI路径 {filePath} → {candidate}");
                    return candidate;
                }

                string fileName = System.IO.Path.GetFileName(filePath);
                if (!string.IsNullOrEmpty(fileName))
                {
                    try
                    {
                        var matches = System.IO.Directory.GetFiles(solutionPath, fileName,
                            System.IO.SearchOption.AllDirectories);
                        if (matches.Length > 0)
                        {
                            Logger.Info($"[PathResolve] AI路径 {filePath} → {matches[0]} (方案目录搜索)");
                            return matches[0];
                        }
                    }
                    catch { }
                }

                Logger.Warn($"[PathResolve] AI路径无法匹配已有文件，将创建: {candidate}");
                return candidate;
            }
            else if (!string.IsNullOrEmpty(solutionPath))
            {
                string candidate = System.IO.Path.Combine(solutionPath, filePath.Replace('/', '\\'));
                Logger.Info($"[PathResolve] 相对路径 {filePath} → {candidate}");
                return candidate;
            }

            return filePath;
        }

        /// <summary>
        /// 规范化文件路径（统一分隔符、去除尾部空格），用于 GroupBy 合并。
        /// </summary>
        /// <param name="filePath">待规范化的路径。</param>
        /// <returns>统一使用反斜杠且去除尾部分隔符的路径。</returns>
        internal static string NormalizePath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return filePath;
            return filePath.Replace('/', '\\').Trim().TrimEnd('\\');
        }
    }
}
