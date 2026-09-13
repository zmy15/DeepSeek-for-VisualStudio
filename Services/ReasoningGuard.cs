using System;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    public enum ReasoningGuardAction
    {
        Continue,
        LoopDetected,
        LengthLimitExceeded
    }

    public readonly struct ReasoningGuardResult
    {
        public ReasoningGuardResult(
            ReasoningGuardAction action,
            int characterCount,
            int repetitionCount = 0)
        {
            Action = action;
            CharacterCount = characterCount;
            RepetitionCount = repetitionCount;
        }

        public ReasoningGuardAction Action { get; }

        public int CharacterCount { get; }

        public int RepetitionCount { get; }

        public bool ShouldBreak => Action != ReasoningGuardAction.Continue;
    }

    /// <summary>
    /// Detects repeated reasoning blocks and enforces a hard in-flight reasoning limit.
    /// The guard keeps only a small normalized tail, so it does not duplicate the full
    /// reasoning payload in memory.
    /// </summary>
    public sealed class ReasoningLoopGuard
    {
        public const int DefaultMaxCharacters = 128_000;
        public const int DefaultBlockCharacters = 512;
        public const int DefaultRepeatThreshold = 3;
        public const int DefaultSearchWindowCharacters = 8_192;

        private readonly int _maxCharacters;
        private readonly int _blockCharacters;
        private readonly int _repeatThreshold;
        private readonly int _searchWindowCharacters;
        private readonly StringBuilder _normalizedTail;
        private int _characterCount;

        public ReasoningLoopGuard(
            int maxCharacters = DefaultMaxCharacters,
            int blockCharacters = DefaultBlockCharacters,
            int repeatThreshold = DefaultRepeatThreshold,
            int searchWindowCharacters = DefaultSearchWindowCharacters)
        {
            if (maxCharacters < 1)
                throw new ArgumentOutOfRangeException(nameof(maxCharacters));
            if (blockCharacters < 1)
                throw new ArgumentOutOfRangeException(nameof(blockCharacters));
            if (repeatThreshold < 2)
                throw new ArgumentOutOfRangeException(nameof(repeatThreshold));
            if (searchWindowCharacters < blockCharacters * repeatThreshold)
                throw new ArgumentOutOfRangeException(nameof(searchWindowCharacters));

            _maxCharacters = maxCharacters;
            _blockCharacters = blockCharacters;
            _repeatThreshold = repeatThreshold;
            _searchWindowCharacters = searchWindowCharacters;
            _normalizedTail = new StringBuilder(searchWindowCharacters + blockCharacters);
        }

        public ReasoningGuardResult Inspect(string? delta)
        {
            if (string.IsNullOrEmpty(delta))
                return new ReasoningGuardResult(ReasoningGuardAction.Continue, _characterCount);

            _characterCount += delta.Length;
            if (_characterCount >= _maxCharacters)
            {
                return new ReasoningGuardResult(
                    ReasoningGuardAction.LengthLimitExceeded,
                    _characterCount);
            }

            foreach (char c in delta)
            {
                if (char.IsWhiteSpace(c))
                    continue;

                _normalizedTail.Append(char.ToLowerInvariant(c));
            }

            if (_normalizedTail.Length > _searchWindowCharacters)
            {
                int removeCount = _normalizedTail.Length - _searchWindowCharacters;
                _normalizedTail.Remove(0, removeCount);
            }

            if (_normalizedTail.Length < _blockCharacters * _repeatThreshold)
                return new ReasoningGuardResult(ReasoningGuardAction.Continue, _characterCount);

            string window = _normalizedTail.ToString();
            string block = window.Substring(window.Length - _blockCharacters);
            int repetitionCount = CountOccurrences(window, block);
            if (repetitionCount >= _repeatThreshold)
            {
                return new ReasoningGuardResult(
                    ReasoningGuardAction.LoopDetected,
                    _characterCount,
                    repetitionCount);
            }

            return new ReasoningGuardResult(ReasoningGuardAction.Continue, _characterCount);
        }

        public void Reset()
        {
            _characterCount = 0;
            _normalizedTail.Clear();
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            int index = 0;
            while (index <= text.Length - value.Length)
            {
                int found = text.IndexOf(value, index, StringComparison.Ordinal);
                if (found < 0)
                    break;

                count++;
                index = found + value.Length;
            }

            return count;
        }
    }

    /// <summary>
    /// Centralized bounds for reasoning text that is retained by the context, UI,
    /// persistence, compression and retry paths.
    /// </summary>
    public static class ReasoningTextPolicy
    {
        public const int StoredReasoningMaxChars = 32_000;
        public const int HistoricalToolCallMaxChars = 4_000;
        public const int CompressionReasoningMaxChars = 2_000;
        public const int RetryReasoningMaxChars = 2_000;

        public static string? ClampStored(string? text)
            => Clamp(text, StoredReasoningMaxChars);

        public static string? ClampHistoricalToolCall(string? text)
            => Clamp(text, HistoricalToolCallMaxChars);

        public static string? ClampForCompression(string? text)
            => Clamp(text, CompressionReasoningMaxChars);

        public static string? ClampForRetry(string? text)
            => Clamp(text, RetryReasoningMaxChars);

        public static string? Clamp(string? text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || maxChars <= 0 || text.Length <= maxChars)
                return text;

            string marker = $"\n...[reasoning truncated: {text.Length - maxChars} chars omitted]...\n";
            if (marker.Length >= maxChars)
                return text.Substring(text.Length - maxChars);

            int available = maxChars - marker.Length;
            int headLength = available / 3;
            int tailLength = available - headLength;
            return text.Substring(0, headLength)
                + marker
                + text.Substring(text.Length - tailLength);
        }
    }

    internal sealed class ReasoningLoopDetectedException : Exception
    {
        public ReasoningLoopDetectedException(ReasoningGuardResult result)
            : base($"Reasoning loop detected after {result.CharacterCount} characters.")
        {
            Result = result;
        }

        public ReasoningGuardResult Result { get; }
    }
}
