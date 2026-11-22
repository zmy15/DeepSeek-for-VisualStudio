using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BenchmarkRunner
{
    internal sealed class ToolAuditWorkspace
    {
        private static readonly HashSet<string> ExcludedDirectories = new(
            new[] { ".git", ".vs", "bin", "obj", "build", "node_modules", ".venv", "TestResults" },
            StringComparer.OrdinalIgnoreCase);

        private ToolAuditWorkspace(string root)
        {
            Root = root;
        }

        public string Root { get; }
        public string SampleSourcePath => Path.Combine(Root, "audit-fixtures", "AuditSample.cs");
        public string EditFilePath => Path.Combine(Root, "audit-fixtures", "AuditEdit.cs");
        public string EditDirectoryPath => Path.Combine(Root, "audit-fixtures", "created-directory");
        public string BreakableSourcePath { get; private set; } = string.Empty;

        public static async Task<ToolAuditWorkspace> CreateAsync(
            string sourceWorkspace,
            CancellationToken cancellationToken)
        {
            string source = Path.GetFullPath(sourceWorkspace);
            if (!Directory.Exists(source))
                throw new DirectoryNotFoundException(source);

            string root = Path.Combine(
                Path.GetTempPath(),
                "DeepSeekVS-tool-audit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            CopyDirectory(source, root);

            var workspace = new ToolAuditWorkspace(root);
            workspace.WriteFixtures();
            workspace.BreakableSourcePath = workspace.FindBreakableSource();
            await workspace.InitializeGitAsync(cancellationToken);
            return workspace;
        }

        public void BreakPrimarySource()
        {
            if (string.IsNullOrWhiteSpace(BreakableSourcePath)
                || !File.Exists(BreakableSourcePath))
            {
                throw new InvalidOperationException(
                    "No source file was found for the compile-error audit.");
            }

            bool isCpp = new[]
            {
                ".c", ".cc", ".cpp", ".cxx",
            }.Contains(Path.GetExtension(BreakableSourcePath), StringComparer.OrdinalIgnoreCase);
            File.AppendAllText(
                BreakableSourcePath,
                Environment.NewLine
                + (isCpp
                    ? "this line is deliberately not valid C++;\n"
                    : "this line is deliberately not valid C#;\n"),
                new UTF8Encoding(false));
        }

        private void WriteFixtures()
        {
            string fixtureDirectory = Path.Combine(Root, "audit-fixtures");
            Directory.CreateDirectory(fixtureDirectory);
            File.WriteAllText(
                SampleSourcePath,
                """
                namespace AuditFixtures;

                /// <summary>Audit sample widget.</summary>
                public sealed class AuditSample
                {
                    /// <summary>Runs the audit sample.</summary>
                    public void Run()
                    {
                    }

                    public int Value { get; set; }
                }
                """,
                new UTF8Encoding(false));
            File.WriteAllText(
                EditFilePath,
                """
                public static class AuditEdit
                {
                    public const string Value = "replace-me";
                }
                """,
                new UTF8Encoding(false));

            string skillDirectory = Path.Combine(
                Root,
                ".github",
                "skills",
                "audit-skill");
            Directory.CreateDirectory(skillDirectory);
            File.WriteAllText(
                Path.Combine(skillDirectory, "SKILL.md"),
                """
                ---
                name: audit-skill
                description: Fixture used by the headless tool audit.
                ---
                # Audit Skill

                This skill exists only to verify skill discovery and loading.
                """,
                new UTF8Encoding(false));
            string referencesDirectory = Path.Combine(skillDirectory, "references");
            Directory.CreateDirectory(referencesDirectory);
            File.WriteAllText(
                Path.Combine(referencesDirectory, "reference.txt"),
                "audit-skill-resource",
                new UTF8Encoding(false));
        }

        private string FindBreakableSource()
        {
            bool isCmake = File.Exists(Path.Combine(Root, "CMakeLists.txt"));
            string[] extensions = isCmake
                ? new[] { ".cpp", ".cc", ".cxx", ".c" }
                : new[] { ".cs" };
            return Directory
                .EnumerateFiles(Root, "*", SearchOption.AllDirectories)
                .Where(path => extensions.Contains(
                    Path.GetExtension(path),
                    StringComparer.OrdinalIgnoreCase))
                .Where(path => !IsExcludedPath(path))
                .Where(path => !path.Contains(
                    $"{Path.DirectorySeparatorChar}audit-fixtures{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path.Contains(
                    $"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault() ?? string.Empty;
        }

        private async Task InitializeGitAsync(CancellationToken cancellationToken)
        {
            await RunGitAsync("init", cancellationToken);
            await RunGitAsync("config user.email audit@example.invalid", cancellationToken);
            await RunGitAsync("config user.name \"Tool Audit\"", cancellationToken);
            await RunGitAsync("add .", cancellationToken);
            await RunGitAsync("commit -m \"audit baseline\"", cancellationToken);
        }

        private async Task RunGitAsync(string arguments, CancellationToken cancellationToken)
        {
            ToolchainResult result = await ToolchainProcess.RunAsync(
                "git",
                arguments,
                Root,
                cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git {arguments} failed: {result.Output}");
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            foreach (string directory in Directory.EnumerateDirectories(
                source,
                "*",
                SearchOption.AllDirectories))
            {
                if (ExcludedDirectories.Contains(Path.GetFileName(directory)))
                    continue;
                string relative = GetRelativePath(source, directory);
                if (relative.Split(Path.DirectorySeparatorChar)
                    .Any(ExcludedDirectories.Contains))
                {
                    continue;
                }
                Directory.CreateDirectory(Path.Combine(destination, relative));
            }

            foreach (string file in Directory.EnumerateFiles(
                source,
                "*",
                SearchOption.AllDirectories))
            {
                string relative = GetRelativePath(source, file);
                if (relative.Split(Path.DirectorySeparatorChar)
                    .Any(ExcludedDirectories.Contains))
                {
                    continue;
                }
                string target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }

        private static bool IsExcludedPath(string path)
        {
            return path.Split(Path.DirectorySeparatorChar)
                .Any(ExcludedDirectories.Contains);
        }

        private static string GetRelativePath(string root, string path)
        {
            string normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string normalizedPath = Path.GetFullPath(path);
            if (!normalizedPath.StartsWith(
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Path '{path}' is not inside '{root}'.");
            }
            return normalizedPath.Substring(normalizedRoot.Length);
        }
    }
}
