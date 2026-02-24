namespace Microsoft.PowerPlatformLS.UnitTests.TestUtilities
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Utilities;
    using System;
    using System.Collections.Generic;
    using Range = PowerPlatformLS.Contracts.Lsp.Models.Range;

    internal static class Utils
    {
        public static Range CreateRange(int startLine, int startColumn, int endLine, int endColumn)
        {
            return new Range
            {
                Start = new Position { Line = startLine, Character = startColumn },
                End = new Position { Line = endLine, Character = endColumn }
            };
        }

        public static (string, IEnumerable<Range>) ExtractRanges(string testString)
        {
            var markResolver = new Lazy<MarkResolver>(() => new MarkResolver(testString));
            var ranges = new List<Range>();
            int closeRangeIndex = 0;
            while (true)
            {
                int openRangeIndex = testString.IndexOf("[[", closeRangeIndex);
                if (openRangeIndex < 0)
                {
                    return (testString, ranges);
                }

                testString = testString.Remove(openRangeIndex, 2);
                closeRangeIndex = testString.IndexOf("]]", openRangeIndex);
                if (closeRangeIndex < 0)
                {
                    return (testString, ranges);
                }

                testString = testString.Remove(closeRangeIndex, 2);
                ranges.Add(markResolver.Value.GetRange(openRangeIndex, closeRangeIndex));
            }
        }
    }
}
