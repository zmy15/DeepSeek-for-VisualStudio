using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BenchmarkRunner
{
    internal sealed class HeadlessSourceSymbol
    {
        public string Kind { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
        public int Line { get; set; }
    }

    internal static class HeadlessSourceIndex
    {
        private static readonly string[] SourceExtensions =
        {
            ".cs", ".cpp", ".cc", ".cxx", ".c", ".h", ".hpp", ".hh",
        };

        private static readonly Regex CSharpType = new(
            @"^(?:(?:public|private|protected|internal|static|abstract|sealed|partial|unsafe|new)\s+)*(?<kind>namespace|class|interface|struct|enum|record(?:\s+(?:class|struct))?|delegate)\s+(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)",
            RegexOptions.Compiled);

        private static readonly Regex CppType = new(
            @"^(?:(?:template\s*<[^>]+>\s*)|(?:typedef|using)\s+)?(?<kind>namespace|class|struct|union|enum(?:\s+class)?)\s+(?<name>[A-Za-z_]\w*(?:::[A-Za-z_]\w*)*)",
            RegexOptions.Compiled);

        private static readonly Regex CSharpMethod = new(
            @"^(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async|extern|unsafe|partial|new)\s+)*(?<return>[A-Za-z_][\w<>\[\],.?]*(?:\s*\*)?)\s+(?<name>[A-Za-z_]\w*(?:\s*::\s*[A-Za-z_]\w*)?)\s*(?:<[^>]+>)?\s*\(",
            RegexOptions.Compiled);

        private static readonly Regex CppMethod = new(
            @"^(?:(?:inline|static|virtual|explicit|constexpr|consteval|friend|extern|const|unsigned|signed|long|short)\s+)*(?<return>[A-Za-z_][\w:<>,*&\s]*?[*&\s]+)?(?<name>[A-Za-z_]\w*(?:::[A-Za-z_]\w*)*(?:~[A-Za-z_]\w*)?)\s*\(",
            RegexOptions.Compiled);

        private static readonly Regex Property = new(
            @"^(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|new|required)\s+)*(?<type>[A-Za-z_][\w<>\[\],.?]*)\s+(?<name>[A-Za-z_]\w*)\s*\{\s*(?:get|set|init)\b",
            RegexOptions.Compiled);

        private static readonly Regex Field = new(
            @"^(?:(?:public|private|protected|internal|static|readonly|const|volatile|new|unsafe)\s+)+(?<type>[A-Za-z_][\w<>\[\],.?]*)\s+(?<name>[A-Za-z_]\w*)\s*(?:=|;|\{)",
            RegexOptions.Compiled);

        private static readonly HashSet<string> ControlKeywords = new(
            new[]
            {
                "if", "for", "foreach", "while", "switch", "catch", "using",
                "lock", "return", "throw", "new", "sizeof", "typeof", "nameof",
                "decltype", "alignof", "static_assert",
            },
            StringComparer.Ordinal);

        public static List<HeadlessSourceSymbol> ScanFile(
            string filePath,
            int maxResults = 2000)
        {
            if (!File.Exists(filePath))
                return new List<HeadlessSourceSymbol>();
            string[] lines;
            try { lines = File.ReadAllLines(filePath); }
            catch { return new List<HeadlessSourceSymbol>(); }
            return ScanLines(filePath, lines, maxResults);
        }

        public static List<HeadlessSourceSymbol> ScanLines(
            string filePath,
            string[] lines,
            int maxResults = 2000)
        {
            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            bool isCpp = extension is ".cpp" or ".cc" or ".cxx" or ".c" or ".h" or ".hpp" or ".hh";
            var results = new List<HeadlessSourceSymbol>();
            for (int index = 0; index < lines.Length && results.Count < maxResults; index++)
            {
                string raw = lines[index];
                string line = raw.Trim();
                if (!CanContainDeclaration(line))
                    continue;
                HeadlessSourceSymbol? symbol = isCpp
                    ? ParseCpp(line, index + 1, raw)
                    : ParseCSharp(line, index + 1, raw);
                if (symbol != null)
                    results.Add(symbol);
            }
            return results
                .GroupBy(item => $"{item.Line}|{item.Kind}|{item.Name}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => item.Line)
                .ToList();
        }

        public static IReadOnlyList<string> EnumerateSourceFiles(string root)
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => SourceExtensions.Contains(
                    Path.GetExtension(path),
                    StringComparer.OrdinalIgnoreCase))
                .Where(path => !IsExcluded(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string? ExtractAttachedComment(string[] lines, int declarationLine)
        {
            if (lines.Length == 0 || declarationLine <= 1)
                return null;
            int index = Math.Min(lines.Length - 1, declarationLine - 2);
            var collected = new List<string>();
            bool inBlock = false;
            for (; index >= 0; index--)
            {
                string raw = lines[index];
                string trimmed = raw.Trim();
                if (trimmed.Length == 0)
                    break;
                if (inBlock || trimmed.EndsWith("*/", StringComparison.Ordinal))
                {
                    inBlock = !trimmed.StartsWith("/*", StringComparison.Ordinal);
                    collected.Insert(0, CleanCommentLine(trimmed));
                    continue;
                }
                if (trimmed.StartsWith("///", StringComparison.Ordinal)
                    || trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("'", StringComparison.Ordinal))
                {
                    collected.Insert(0, CleanCommentLine(trimmed));
                    continue;
                }
                break;
            }
            while (collected.Count > 0 && string.IsNullOrWhiteSpace(collected[0]))
                collected.RemoveAt(0);
            while (collected.Count > 0
                && string.IsNullOrWhiteSpace(collected[collected.Count - 1]))
                collected.RemoveAt(collected.Count - 1);
            return collected.Count == 0
                ? null
                : string.Join(Environment.NewLine, collected);
        }

        public static bool MatchesSymbol(
            string name,
            string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
                return true;
            string normalized = filter.Replace("::", ".").Trim();
            string candidate = name.Replace("::", ".").Trim();
            return string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase)
                || candidate.EndsWith("." + normalized, StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("." + candidate, StringComparison.OrdinalIgnoreCase)
                || candidate.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static HeadlessSourceSymbol? ParseCSharp(
            string line,
            int lineNumber,
            string signature)
        {
            Match type = CSharpType.Match(line);
            if (type.Success)
                return NewSymbol(type, signature, lineNumber);
            Match method = CSharpMethod.Match(line);
            if (method.Success
                && !ControlKeywords.Contains(method.Groups["name"].Value))
            {
                return new HeadlessSourceSymbol
                {
                    Kind = "method",
                    Name = method.Groups["name"].Value.Replace(" ", string.Empty),
                    Signature = signature.Trim(),
                    Line = lineNumber,
                };
            }
            Match property = Property.Match(line);
            if (property.Success)
                return NewSymbol(property, signature, lineNumber, "property");
            Match field = Field.Match(line);
            if (field.Success)
                return NewSymbol(field, signature, lineNumber, "field");
            return null;
        }

        private static HeadlessSourceSymbol? ParseCpp(
            string line,
            int lineNumber,
            string signature)
        {
            Match type = CppType.Match(line);
            if (type.Success)
                return NewSymbol(type, signature, lineNumber);
            Match method = CppMethod.Match(line);
            if (method.Success)
            {
                string name = method.Groups["name"].Value.Replace(" ", string.Empty);
                if (!ControlKeywords.Contains(name))
                {
                    return new HeadlessSourceSymbol
                    {
                        Kind = "method",
                        Name = name,
                        Signature = signature.Trim(),
                        Line = lineNumber,
                    };
                }
            }
            Match field = Regex.Match(
                line,
                @"^(?<type>[A-Za-z_][\w:<>,*&]*)\s+(?<name>[A-Za-z_]\w*)\s*(?:=[^;]*)?;");
            if (field.Success && !ControlKeywords.Contains(field.Groups["name"].Value))
                return NewSymbol(field, signature, lineNumber, "field");
            return null;
        }

        private static HeadlessSourceSymbol NewSymbol(
            Match match,
            string signature,
            int line,
            string? kindOverride = null)
        {
            string kind = kindOverride
                ?? match.Groups["kind"].Value;
            kind = Regex.Replace(kind, @"\s+", " ").ToLowerInvariant();
            if (kind.StartsWith("enum", StringComparison.Ordinal))
                kind = "enum";
            if (kind is "record class" or "record struct")
                kind = "record";
            return new HeadlessSourceSymbol
            {
                Kind = kind,
                Name = match.Groups["name"].Value,
                Signature = signature.Trim(),
                Line = line,
            };
        }

        private static bool CanContainDeclaration(string line)
        {
            return line.Length > 0
                && !line.StartsWith("//", StringComparison.Ordinal)
                && !line.StartsWith("/*", StringComparison.Ordinal)
                && !line.StartsWith("*", StringComparison.Ordinal)
                && !line.StartsWith("#", StringComparison.Ordinal)
                && !line.StartsWith("[", StringComparison.Ordinal);
        }

        private static string CleanCommentLine(string trimmed)
        {
            if (trimmed.StartsWith("///", StringComparison.Ordinal))
                return trimmed.Substring(3).Trim();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("'", StringComparison.Ordinal))
            {
                return trimmed.Substring(1).Trim();
            }
            if (trimmed.StartsWith("/**", StringComparison.Ordinal))
                return trimmed.Substring(3).Trim();
            if (trimmed.StartsWith("/*", StringComparison.Ordinal))
                return trimmed.Substring(2).Trim();
            string value = trimmed;
            if (value.EndsWith("*/", StringComparison.Ordinal))
                value = value.Substring(0, value.Length - 2);
            return value.TrimStart('*').Trim();
        }

        private static bool IsExcluded(string path)
        {
            string normalized = path.Replace('/', '\\');
            return normalized.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("\\build\\", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("\\.vs\\", StringComparison.OrdinalIgnoreCase);
        }
    }
}
