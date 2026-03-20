namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Utilities
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Collections.Immutable;

    public class MarkResolver
    {
        private readonly ImmutableArray<(int start, int length)> _linesStartAndLength;
        private const char NewLine = '\n';
        private const int NewLineLength = 1;

        /// <summary>
        /// Build a new instance of MarkResolver, utility to optimize conversion from "Line+Column" to "Index".
        /// </summary>
        /// <param name="text">Text string.</param>
        public MarkResolver(string text)
        {
            var linesStartAndLength = ImmutableArray.CreateBuilder<(int start, int length)>();
            var currentIndex = 0;
            var remaining = text.AsSpan();
            while (remaining.Length > 0)
            {
                int idx = remaining.IndexOf(NewLine);
                if ((uint)idx < (uint)remaining.Length)
                {
                    linesStartAndLength.Add((currentIndex, idx));
                    int sliceLength = idx + NewLineLength;
                    currentIndex += sliceLength ;
                    remaining = remaining.Slice(idx + NewLineLength);
                    if (remaining.Length == 0) // newline at EOF
                    {
                        linesStartAndLength.Add((currentIndex, 0));
                    }
                }
                else
                {
                    linesStartAndLength.Add((currentIndex, remaining.Length));
                    remaining = default;
                }
            }

            if (linesStartAndLength.Count == 0)
            {
                linesStartAndLength.Add((0, 0));
            }

            _linesStartAndLength = linesStartAndLength.ToImmutable();
        }

        public Range GetRange(int startIndex, int endIndex)
        {
            var startPosition = GetPosition(startIndex);
            var endPosition = GetPosition(endIndex);
            return new Range
            {
                Start = startPosition,
                End = endPosition,
            };
        }

        /// <summary>
        /// Converts a start position and length to in-bounds ranges across lines. 
        /// </summary>
        public IEnumerable<(Position start, int length)> GetRangesInLines(int startIndex, int length)
        {
            int endIndex = startIndex + length;
            var startPosition = GetPosition(startIndex);
            int line = startPosition.Line;
            var (lineStart, lineLength) = _linesStartAndLength[line++];
            int remainingLengthInFirstLine = lineLength - startPosition.Character;
            if (remainingLengthInFirstLine >= length || line == _linesStartAndLength.Length)
            {
                yield return (startPosition, length);
            }
            else
            {
                yield return (startPosition, remainingLengthInFirstLine);
                int remainingLength;
                do
                {
                    (lineStart, lineLength) = _linesStartAndLength[line];
                    remainingLength = endIndex - lineStart;
                    if (remainingLength > 0)
                    {
                        yield return (new Position { Line = line, Character = 0 }, Math.Min(lineLength, remainingLength));
                    }
                }
                while (remainingLength > lineLength && ++line < _linesStartAndLength.Length);
            }
        }

        public Position GetPosition(int index)
        {
            index = Math.Max(0, index);
            int left = 0;
            int right = _linesStartAndLength.Length - 1;

            while (left <= right)
            {
                int mid = (left + right) / 2;
                var (start, length) = _linesStartAndLength[mid];

                if (index < start)
                {
                    right = mid - 1;
                }
                else if (index >= start + length + NewLineLength)
                {
                    left = mid + 1;
                }
                else
                {
                    return new Position
                    {
                        Line = mid,
                        Character = index - start
                    };
                }
            }

            // If the index is out of bounds, return the last position
            var (lastStart, lastLength) = _linesStartAndLength[^1];
            return new Position
            {
                Line = _linesStartAndLength.Length - 1,
                Character = lastLength,
            };
        }

        /// <summary>
        /// Given the document layout and (lineIndex, columnIndex) pair (which may be out of bounds),
        /// returns a valid position within the document.
        ///
        /// Utility for navigating documents lines by applying index-diff on the column property only.
        /// Note that the end of a line is a valid position, which matches the index of the newline sequence.
        /// </summary>
        /// <param name="lineIdx">The desired line index (may be negative or too high).</param>
        /// <param name="columnIndex">The desired column index (may be negative or too high).</param>
        /// <returns>A valid position that "rolls" extra columns into adjacent lines.</returns>
        public Position GetValidPosition(int lineIdx, int columnIndex)
        {
            if (_linesStartAndLength.Length == 0)
            {
                return new Position { Line = 0, Character = 0 };
            }

            // Clamp the initial line index.
            if (lineIdx < 0)
            {
                // start computing column from the first position
                lineIdx = 0;
            }
            else if (lineIdx >= _linesStartAndLength.Length)
            {
                // start computing column from the last position
                lineIdx = _linesStartAndLength.Length - 1;
                columnIndex += _linesStartAndLength[lineIdx].length;
            }

            // caret is too far to the left:
            while (columnIndex < 0)
            {
                // already at the first line, clamp to start.
                if (lineIdx == 0)
                {
                    return new() { Line = 0, Character = 0 };
                }

                // Roll the extra offset into the previous line.
                // Loop again – currentColumn might still be negative.
                columnIndex = _linesStartAndLength[lineIdx - 1].length + NewLineLength + columnIndex;
                lineIdx--;
            }

            int lineLength;

            // caret is too far right (past the very end of the line)
            while (columnIndex > (lineLength = _linesStartAndLength[lineIdx].length))
            {
                // already on the last line, clamp to end.
                if (lineIdx == _linesStartAndLength.Length - 1)
                {
                    return new() { Line = lineIdx, Character = lineLength };
                }

                // Determine how far past the end this position is.
                int excess = columnIndex - lineLength;

                // If the excess is within the newline region, we treat that as the beginning of the next line.
                if (excess <= NewLineLength)
                {
                    return new() { Line = lineIdx + 1, Character = 0 };
                }
                else
                {
                    // The caret goes past the newline. Loop again to check excess on next line.
                    columnIndex = excess - NewLineLength;
                    ++lineIdx;
                }
            }

            return new() { Line = lineIdx, Character = columnIndex };
        }

        public int GetIndex(Position position)
        {
            if (position.Line < 0 || position.Line >= _linesStartAndLength.Length)
            {
                return -1;
            }

            // note that end of line is a valid position, which matches the index of the newline sequence.
            if (position.Character < 0 || position.Character > _linesStartAndLength[position.Line].length)
            {
                return -1;
            }

            return _linesStartAndLength[position.Line].start + position.Character;
        }
    }
}