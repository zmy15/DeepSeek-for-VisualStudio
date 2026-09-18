using System;
using System.Collections.Generic;

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

        private const ulong HashBase = 1099511628211UL;

        private readonly int _maxCharacters;
        private readonly int _blockCharacters;
        private readonly int _repeatThreshold;
        private readonly int _searchWindowCharacters;
        private readonly char[] _tailBuffer;
        private readonly ulong[] _blockHashes;
        private readonly Dictionary<ulong, int> _hashCounts = new();
        private readonly ulong _hashBasePower;
        private int _tailStart;
        private int _tailLength;
        private int _hashStart;
        private int _hashCount;
        private ulong _currentBlockHash;
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
            _tailBuffer = new char[searchWindowCharacters];
            _blockHashes = new ulong[searchWindowCharacters];
            _hashBasePower = ComputeHashPower(blockCharacters);
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

                AppendNormalizedChar(char.ToLowerInvariant(c));
            }

            if (_tailLength < _blockCharacters * _repeatThreshold || _hashCount == 0)
                return new ReasoningGuardResult(ReasoningGuardAction.Continue, _characterCount);

            ulong latestBlockHash = _blockHashes[(_hashStart + _hashCount - 1) % _blockHashes.Length];
            if (!_hashCounts.TryGetValue(latestBlockHash, out int hashOccurrences)
                || hashOccurrences < _repeatThreshold)
            {
                return new ReasoningGuardResult(ReasoningGuardAction.Continue, _characterCount);
            }

            string window = BuildTailString();
            string block = window.Substring(window.Length - _blockCharacters);
            int repetitionCount = CountOccurrences(window, block);
            return repetitionCount >= _repeatThreshold
                ? new ReasoningGuardResult(
                    ReasoningGuardAction.LoopDetected,
                    _characterCount,
                    repetitionCount)
                : new ReasoningGuardResult(ReasoningGuardAction.Continue, _characterCount);
        }

        private void AppendNormalizedChar(char c)
        {
            bool removeFromBlock = _tailLength >= _blockCharacters;
            char charToRemove = removeFromBlock
                ? GetTailChar(_tailLength - _blockCharacters)
                : '\0';
            bool bufferFull = _tailLength >= _tailBuffer.Length;

            if (bufferFull)
            {
                _tailBuffer[_tailStart] = c;
                _tailStart = (_tailStart + 1) % _tailBuffer.Length;
            }
            else
            {
                _tailBuffer[(_tailStart + _tailLength) % _tailBuffer.Length] = c;
                _tailLength++;
            }

            unchecked
            {
                _currentBlockHash = removeFromBlock
                    ? (_currentBlockHash - CharValue(charToRemove) * _hashBasePower) * HashBase + CharValue(c)
                    : _currentBlockHash * HashBase + CharValue(c);
            }

            if (_tailLength < _blockCharacters)
                return;

            if (bufferFull)
                RemoveOldestHash();
            AddHash(_currentBlockHash);
        }

        private void AddHash(ulong hash)
        {
            _blockHashes[(_hashStart + _hashCount) % _blockHashes.Length] = hash;
            _hashCount++;
            _hashCounts.TryGetValue(hash, out int count);
            _hashCounts[hash] = count + 1;
        }

        private void RemoveOldestHash()
        {
            if (_hashCount == 0)
                return;

            ulong oldest = _blockHashes[_hashStart];
            if (_hashCounts.TryGetValue(oldest, out int count))
            {
                if (count <= 1)
                    _hashCounts.Remove(oldest);
                else
                    _hashCounts[oldest] = count - 1;
            }

            _hashStart = (_hashStart + 1) % _blockHashes.Length;
            _hashCount--;
        }

        private char GetTailChar(int offsetFromStart)
        {
            int index = _tailStart + offsetFromStart;
            if (index >= _tailBuffer.Length)
                index %= _tailBuffer.Length;
            return _tailBuffer[index];
        }

        private string BuildTailString()
        {
            var chars = new char[_tailLength];
            for (int i = 0; i < _tailLength; i++)
                chars[i] = GetTailChar(i);
            return new string(chars);
        }

        private static ulong ComputeHashPower(int length)
        {
            ulong power = 1;
            for (int i = 1; i < length; i++)
            {
                unchecked
                {
                    power *= HashBase;
                }
            }
            return power;
        }

        private static ulong CharValue(char c)
        {
            return (ulong)c + 1;
        }

        public void Reset()
        {
            _characterCount = 0;
            _tailStart = 0;
            _tailLength = 0;
            _hashStart = 0;
            _hashCount = 0;
            _currentBlockHash = 0;
            _hashCounts.Clear();
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
