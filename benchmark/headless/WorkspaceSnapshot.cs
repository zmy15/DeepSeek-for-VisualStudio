using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace BenchmarkRunner
{
    internal sealed class WorkspaceSnapshot
    {
        private static readonly HashSet<string> IgnoredDirectories =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".git",
                ".vs",
                "bin",
                "obj",
                "build",
                "TestResults",
                "node_modules",
            };

        [JsonPropertyName("file_count")]
        public int FileCount => Files.Count;

        [JsonPropertyName("files")]
        public Dictionary<string, string> Files { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public WorkspaceSnapshotDiff Diff(WorkspaceSnapshot final)
        {
            var added = final.Files.Keys
                .Where(path => !Files.ContainsKey(path))
                .OrderBy(path => path)
                .ToArray();
            var deleted = Files.Keys
                .Where(path => !final.Files.ContainsKey(path))
                .OrderBy(path => path)
                .ToArray();
            var modified = Files.Keys
                .Where(path => final.Files.TryGetValue(path, out string? hash)
                    && !string.Equals(hash, Files[path], StringComparison.Ordinal))
                .OrderBy(path => path)
                .ToArray();

            return new WorkspaceSnapshotDiff
            {
                Added = added,
                Deleted = deleted,
                Modified = modified,
            };
        }

        public static WorkspaceSnapshot Capture(string root)
        {
            string fullRoot = Path.GetFullPath(root);
            var snapshot = new WorkspaceSnapshot();
            foreach (string file in EnumerateFiles(fullRoot))
            {
                string relative = file.Substring(fullRoot.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');
                snapshot.Files[relative] = HashFile(file);
            }
            return snapshot;
        }

        public void WriteToArchive(ZipArchive archive, string root)
        {
            string fullRoot = Path.GetFullPath(root);
            foreach (string relative in Files.Keys.OrderBy(path => path))
            {
                string source = Path.Combine(
                    fullRoot,
                    relative.Replace('/', Path.DirectorySeparatorChar));
                ZipArchiveEntry entry = archive.CreateEntry(
                    "workspace/" + relative,
                    CompressionLevel.Optimal);
                using Stream input = File.OpenRead(source);
                using Stream output = entry.Open();
                input.CopyTo(output);
            }
        }

        private static IEnumerable<string> EnumerateFiles(string root)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                foreach (string child in Directory.GetDirectories(directory))
                {
                    string name = Path.GetFileName(child);
                    FileAttributes attributes = File.GetAttributes(child);
                    if (!IgnoredDirectories.Contains(name)
                        && (attributes & FileAttributes.ReparsePoint) == 0)
                        pending.Push(child);
                }

                foreach (string file in Directory.GetFiles(directory))
                    yield return file;
            }
        }

        private static string HashFile(string path)
        {
            using SHA256 sha = SHA256.Create();
            using FileStream stream = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(stream))
                .Replace("-", string.Empty)
                .ToLowerInvariant();
        }
    }

    internal sealed class WorkspaceSnapshotDiff
    {
        [JsonPropertyName("added")]
        public string[] Added { get; set; } = Array.Empty<string>();

        [JsonPropertyName("deleted")]
        public string[] Deleted { get; set; } = Array.Empty<string>();

        [JsonPropertyName("modified")]
        public string[] Modified { get; set; } = Array.Empty<string>();

        [JsonIgnore]
        public bool Changed => Added.Length + Deleted.Length + Modified.Length > 0;

        [JsonIgnore]
        public int ChangeCount => Added.Length + Deleted.Length + Modified.Length;

        [JsonIgnore]
        public IEnumerable<string> AllPaths =>
            Added.Concat(Deleted).Concat(Modified).Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
