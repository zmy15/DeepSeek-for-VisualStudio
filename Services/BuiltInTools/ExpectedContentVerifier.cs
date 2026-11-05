using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// expected 文本解析与结果校验器。
    /// apply_patch 使用 @唯一位置锚点；兼容场景也支持
    /// +InitializeComponent、-OldMethod 以及带行号格式。
    /// </summary>
    internal static class ExpectedContentVerifier
    {
        internal const string DeletedMarker = "(deleted)";
        private const char ContainsMarker = '*';

        internal static bool IsDeletedExpectation(string expectedText)
            => string.Equals(expectedText?.Trim(), DeletedMarker,
                StringComparison.OrdinalIgnoreCase);

        internal static string? ValidateExpected(string expectedText)
        {
            if (IsDeletedExpectation(expectedText))
                return null;

            if (IsContentAnchorFormat(expectedText))
            {
                return TryParseContentAnchors(
                    expectedText, out _, out string error)
                    ? null
                    : error;
            }

            var parsed = ParseLineNumberedFragment(expectedText);
            return parsed.Success ? null : parsed.Error;
        }

        /// <summary>
        /// 解析连续行号片段。apply_patch 只需要校验受影响区域，
        /// 因此允许行号从任意正整数开始，例如 28|...、29|...。
        /// </summary>
        internal static (bool Success, int StartLine, List<string> Lines, string Error)
            ParseLineNumberedFragment(string expectedText, bool requireStartAtOne = false)
        {
            var lines = new List<string>();
            int startLine = 0;
            string normalized = (expectedText ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            string[] rawLines = normalized.Split('\n');

            for (int i = 0; i < rawLines.Length; i++)
            {
                string rawLine = rawLines[i];
                if (i == rawLines.Length - 1 && rawLine.Length == 0)
                    continue;

                Match match = Regex.Match(
                    rawLine,
                    @"^\s*(\d+)\s*(?:\||\u2192)(.*)$");
                if (!match.Success)
                {
                    match = Regex.Match(
                        rawLine,
                        @"^\s*(\d+)\s*(?::|\.)\s?(.*)$");
                }
                if (!match.Success
                    || !int.TryParse(match.Groups[1].Value, out int lineNumber)
                    || lineNumber < 1)
                {
                    return (false, 0, lines,
                        LocalizationService.Instance.Format(
                            "tool.editVerify.expectedFormat", i + 1, rawLine));
                }

                if (lines.Count == 0)
                {
                    if (requireStartAtOne && lineNumber != 1)
                    {
                        return (false, 0, lines,
                            LocalizationService.Instance.Format(
                                "tool.editVerify.expectedStartAtOne", lineNumber));
                    }

                    startLine = lineNumber;
                }
                else if (lineNumber != startLine + lines.Count)
                {
                    return (false, 0, lines,
                        LocalizationService.Instance.Format(
                            "tool.editVerify.expectedSequential",
                            i + 1,
                            lineNumber,
                            startLine + lines.Count));
                }

                string expectedLine = match.Groups[2].Value;
                if (expectedLine.Length == 1 && expectedLine[0] == ContainsMarker)
                {
                    return (false, 0, lines,
                        LocalizationService.Instance.Format(
                            "tool.editVerify.expectedFormat", i + 1, rawLine));
                }

                lines.Add(expectedLine);
            }

            if (lines.Count == 0)
            {
                return (false, 0, lines,
                    LocalizationService.Instance["tool.editVerify.missingExpected"]);
            }

            return (true, startLine, lines, string.Empty);
        }

        internal static string FormatLineNumberedContent(IReadOnlyList<string> lines)
        {
            if (lines.Count == 0) return "(empty)";
            return string.Join("\n", lines.Select(
                (line, index) => $"{index + 1}|{line}"));
        }

        /// <summary>
        /// 校验 expected 中声明的连续行片段，不要求 expected 覆盖整个文件。
        /// </summary>
        internal static string VerifyExpectedFragment(
            string expectedText,
            string? actualContent,
            string filePath,
            bool requireFullContent = false)
        {
            if (IsContentAnchorFormat(expectedText))
            {
                if (!TryParseContentAnchors(
                    expectedText, out var anchors, out string anchorError))
                    return anchorError;

                return VerifyContentAnchors(anchors, actualContent, filePath);
            }

            var parsed = ParseLineNumberedFragment(
                expectedText, requireStartAtOne: requireFullContent);
            if (!parsed.Success) return parsed.Error;

            return VerifyParsedLines(
                parsed.Lines, parsed.StartLine, actualContent, filePath,
                requireFullContent);
        }

        internal static bool IsLocationAnchorFormat(string expectedText)
        {
            string normalized = (expectedText ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');

            foreach (string rawLine in normalized.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                    continue;

                return rawLine[0] == '@';
            }

            return false;
        }

        internal static (bool Success, List<string> Anchors, string Error)
            ParseLocationAnchors(string expectedText)
        {
            return TryParseLocationAnchors(
                expectedText, out List<string> anchors, out string error)
                ? (true, anchors, string.Empty)
                : (false, anchors, error);
        }

        private static bool TryParseLocationAnchors(
            string expectedText,
            out List<string> anchors,
            out string error)
        {
            anchors = new List<string>();
            error = string.Empty;
            string normalized = (expectedText ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            string[] rawLines = normalized.Split('\n');

            for (int i = 0; i < rawLines.Length; i++)
            {
                string rawLine = rawLines[i];
                if (string.IsNullOrWhiteSpace(rawLine))
                    continue;

                if (rawLine[0] != '@' || rawLine.Length == 1
                    || string.IsNullOrWhiteSpace(rawLine.Substring(1)))
                {
                    error = LocalizationService.Instance.Format(
                        "tool.editVerify.locationAnchorFormat", i + 1, rawLine);
                    return false;
                }

                anchors.Add(rawLine.Substring(1).Trim());
            }

            if (anchors.Count == 0)
            {
                error = LocalizationService.Instance["tool.editVerify.missingExpected"];
                return false;
            }

            return true;
        }

        private static bool IsContentAnchorFormat(string expectedText)
        {
            string normalized = (expectedText ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');

            foreach (string rawLine in normalized.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                    continue;

                return rawLine[0] == '+' || rawLine[0] == '-';
            }

            return false;
        }

        private static bool TryParseContentAnchors(
            string expectedText,
            out List<(char Kind, string Text)> anchors,
            out string error)
        {
            anchors = new List<(char Kind, string Text)>();
            error = string.Empty;
            string normalized = (expectedText ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            string[] rawLines = normalized.Split('\n');

            for (int i = 0; i < rawLines.Length; i++)
            {
                string rawLine = rawLines[i];
                if (string.IsNullOrWhiteSpace(rawLine))
                    continue;

                char kind = rawLine[0];
                if ((kind != '+' && kind != '-') || rawLine.Length == 1)
                {
                    error = LocalizationService.Instance.Format(
                        "tool.editVerify.expectedFormat", i + 1, rawLine);
                    return false;
                }

                anchors.Add((kind, rawLine.Substring(1)));
            }

            if (anchors.Count == 0)
            {
                error = LocalizationService.Instance["tool.editVerify.missingExpected"];
                return false;
            }

            return true;
        }

        private static string VerifyContentAnchors(
            List<(char Kind, string Text)> anchors,
            string? actualContent,
            string filePath)
        {
            if (actualContent == null)
            {
                return LocalizationService.Instance.Format(
                    "tool.editVerify.anchorFileMissing", filePath);
            }

            foreach (var (kind, text) in anchors)
            {
                bool present = actualContent.IndexOf(text, StringComparison.Ordinal) >= 0;
                if (kind == '+' && !present)
                {
                    return LocalizationService.Instance.Format(
                        "tool.editVerify.anchorMissing", text, filePath);
                }
                if (kind == '-' && present)
                {
                    return LocalizationService.Instance.Format(
                        "tool.editVerify.anchorPresent", text, filePath);
                }
            }

            return LocalizationService.Instance["tool.editVerify.verified"];
        }

        private static string VerifyParsedLines(
            List<string> expectedLines,
            int startLine,
            string? actualContent,
            string filePath,
            bool requireFullContent)
        {
            string[] actualLines = SplitContentIntoLines(actualContent);

            int startIndex = startLine - 1;
            int maxLines = requireFullContent
                ? Math.Max(expectedLines.Count, actualLines.Length)
                : expectedLines.Count;

            for (int i = 0; i < maxLines; i++)
            {
                string expectedLine = i < expectedLines.Count
                    ? expectedLines[i]
                    : "<missing>";

                int actualIndex = startIndex + i;
                string actualLine = actualIndex >= 0 && actualIndex < actualLines.Length
                    ? actualLines[actualIndex]
                    : "<missing>";

                if (!ExpectedLineMatches(expectedLine, actualLine))
                {
                    return BuildVerificationFailure(
                        filePath,
                        actualIndex + 1,
                        expectedLine,
                        actualLine,
                        actualLines,
                        actualContent);
                }
            }

            return LocalizationService.Instance["tool.editVerify.verified"];
        }

        private static bool ExpectedLineMatches(string expectedLine, string actualLine)
        {
            if (expectedLine.Length > 1 && expectedLine[0] == ContainsMarker)
            {
                return actualLine.IndexOf(
                    expectedLine.Substring(1), StringComparison.Ordinal) >= 0;
            }

            return string.Equals(expectedLine, actualLine, StringComparison.Ordinal);
        }

        internal static string BuildCurrentStateMessage(string? actualContent)
        {
            string currentState = actualContent == null
                ? "(file not found)"
                : FormatLineNumberedContent(SplitContentIntoLines(actualContent));
            return LocalizationService.Instance.Format(
                "tool.editVerify.currentState", currentState);
        }

        internal static string[] SplitContentIntoLines(string? content)
        {
            if (content == null) return Array.Empty<string>();

            string normalized = content
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            if (normalized.StartsWith("\uFEFF", StringComparison.Ordinal))
                normalized = normalized.Substring(1);
            if (normalized.EndsWith("\n", StringComparison.Ordinal))
                normalized = normalized.Substring(0, normalized.Length - 1);

            return normalized.Length == 0
                ? Array.Empty<string>()
                : normalized.Split('\n');
        }

        private static string BuildVerificationFailure(
            string filePath,
            int lineNumber,
            string expectedLine,
            string actualLine,
            string[] actualLines,
            string? actualContent)
        {
            string currentState = actualContent == null
                ? "(file not found)"
                : FormatLineNumberedContent(actualLines);

            return LocalizationService.Instance.Format(
                "tool.editVerify.failed",
                lineNumber,
                filePath,
                $"{lineNumber}|{expectedLine}",
                $"{lineNumber}|{actualLine}",
                currentState);
        }
    }
}
