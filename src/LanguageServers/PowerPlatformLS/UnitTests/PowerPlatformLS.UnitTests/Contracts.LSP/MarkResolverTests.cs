namespace Microsoft.PowerPlatformLS.Contracts.Lsp.UnitTests
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Utilities;
    using System;
    using System.Linq;
    using Xunit;

    public class MarkResolverTests
    {
        private const string TestString1 = "In circuits deep, thoughts come alive, \nBoundless knowledge, ever they strive, \nArtificial minds, with wisdom, thrive.\n";

        private static readonly MarkResolver SharedResolver = new MarkResolver(TestString1);

        private static readonly Position Zero = new Position { Line = 0, Character = 0 };
        private static readonly Position EndOfDocument = SharedResolver.GetPosition(TestString1.Length);


        [Fact]
        public void EmptyDoc()
        {
            var resolver = new MarkResolver(string.Empty);
            var range = resolver.GetRange(0, 0);
            Assert.Equal(default, range);
            var position = resolver.GetPosition(0);
            Assert.Equal(default, position);
        }


        [Fact]
        public void MultiLineRanges()
        {
            const string Data = "abc\r\nb\r\nc\nd\ne";
            var resolver = new MarkResolver(Data);
            var ranges = resolver.GetRangesInLines(0, 3).ToArray();
            var range = Assert.Single(ranges);
            Assert.Equal(3, range.length);
            Assert.Equal(new Position { Line = 0, Character = 0 }, range.start);

            ranges = resolver.GetRangesInLines(0, 2).ToArray();
            range = Assert.Single(ranges);
            Assert.Equal(2, range.length);
            Assert.Equal(new Position { Line = 0, Character = 0 }, range.start);

            ranges = resolver.GetRangesInLines(0, 4).ToArray();
            range = Assert.Single(ranges);
            Assert.Equal(4, range.length);
            Assert.Equal(new Position { Line = 0, Character = 0 }, range.start);

            ranges = resolver.GetRangesInLines(0, 7).ToArray();
            Assert.Equal(2, ranges.Length);
            Assert.Equal(4, ranges[0].length);
            Assert.Equal(new Position { Line = 0, Character = 0 }, ranges[0].start);
            Assert.Equal(2, ranges[1].length);
            Assert.Equal(new Position { Line = 1, Character = 0 }, ranges[1].start);

            ranges = resolver.GetRangesInLines(0, Data.Length).ToArray();
            Assert.Equal(5, ranges.Length);
            Assert.Equal(4, ranges[0].length);
            Assert.Equal(2, ranges[1].length);
            Assert.Equal(1, ranges[2].length);
            Assert.Equal(1, ranges[3].length);
            Assert.Equal(1, ranges[4].length);
        }

        [Fact]
        public void Success_RoundTrip_OnGetIndex_GetPosition()
        {
            static void AssertRoundTripPosition(Position inputPosition, int expectedIndex)
            {
                var index = SharedResolver.GetIndex(inputPosition);
                Assert.Equal(expectedIndex, index);
                var outputPosition = SharedResolver.GetPosition(index);
                Assert.Equal(inputPosition, outputPosition);
            }

            static void AssertRoundTripIndex(int inputIndex, Position expectedPosition)
            {
                var position = SharedResolver.GetPosition(inputIndex);
                Assert.Equal(expectedPosition, position);
                var outputIndex = SharedResolver.GetIndex(position);
                Assert.Equal(inputIndex, outputIndex);
            }

            static void AssertRoundTrips(Position position, int index)
            {
                AssertRoundTripPosition(position, index);
                AssertRoundTripIndex(index, position);
            }

            // Edge case : line beginning
            AssertRoundTrips(Zero, 0);

            // middle of line
            AssertRoundTrips(new Position { Line = 0, Character = 1 }, 1);

            // edge case : line end
            AssertRoundTrips(new Position { Line = 0, Character = 39 }, 39);

            // edge case : eod
            AssertRoundTrips(EndOfDocument, TestString1.Length);
        }

        [Fact]
        public void Failure_OnGetIndex()
        {
            int index;

            // line index out of bound
            index = SharedResolver.GetIndex(new Position { Line = 4, Character = 0 });
            Assert.Equal(-1, index);

            // character index out of bound: line end +1
            index = SharedResolver.GetIndex(new Position { Line = 0, Character = 40 });
            Assert.Equal(-1, index);

            // negative character
            index = SharedResolver.GetIndex(new Position { Line = 1, Character = -1 });
            Assert.Equal(-1, index);

            // negative line
            index = SharedResolver.GetIndex(new Position { Line = -1, Character = 0 });
            Assert.Equal(-1, index);
        }

        [Fact]
        public void Success_OutOfBound_OnGetPosition()
        {
            // negative index
            var position = SharedResolver.GetPosition(-1);
            Assert.Equal(Zero, position);

            // index out of bound
            position = SharedResolver.GetPosition(TestString1.Length + 1);
            Assert.Equal(EndOfDocument, position);
        }

        [Fact]
        public void Success_OnGetValidPosition()
        {
            Position position;

            // plain case
            position = SharedResolver.GetValidPosition(0, 0);
            Assert.Equal(Zero, position);

            // negative character index : on previous line
            position = SharedResolver.GetValidPosition(1, -1);
            Assert.Equal(new Position { Line = 0, Character = 39 }, position);

            // character index out of bound : on next line
            position = SharedResolver.GetValidPosition(0, 40);
            Assert.Equal(new Position { Line = 1, Character = 0 }, position);

            // line out of bound positive, character index negative: walk all characters backward
            position = SharedResolver.GetValidPosition(3, -TestString1.Length);
            Assert.Equal(Zero, position);

            // edge case: walk all characters backward but one
            position = SharedResolver.GetValidPosition(3, 1 - TestString1.Length);
            Assert.Equal(new Position { Line = 0, Character = 1 }, position);

            // line out of bound positive: end of last line
            position = SharedResolver.GetValidPosition(3, 0);
            Assert.Equal(EndOfDocument, position);

            // walk all characters forward
            position = SharedResolver.GetValidPosition(-1, TestString1.Length);
            Assert.Equal(EndOfDocument, position);

            // character index out of bound: end of last line
            position = SharedResolver.GetValidPosition(1, TestString1.Length);
            Assert.Equal(EndOfDocument, position);

            // character index out of bound negative: first character of first line
            position = SharedResolver.GetValidPosition(0, -1);
            Assert.Equal(Zero, position);
        }
    }
}