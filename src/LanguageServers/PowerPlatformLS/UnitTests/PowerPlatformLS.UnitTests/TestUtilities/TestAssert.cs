#nullable enable

namespace Microsoft.PowerPlatformLS.UnitTests.TestUtilities
{
    using System.Linq;
    using Xunit;

    internal static class TestAssert
    {
        /// <summary>
        /// Help when <see cref="Assert.Equal{T}(System.Collections.Generic.IEnumerable{T}, System.Collections.Generic.IEnumerable{T})"/> error message isn't clear enough (e.g. strings are too long).
        /// </summary>
        public static void StringArrayEqual(string[] expected, string?[] actual)
        {
            Assert.Equal(expected, actual);
        }

        public static void StringArrayContains(string[] expected, string?[] actual)
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int idx = 0; idx < expected.Count(); ++idx)
            {
                Assert.Contains(expected[idx], actual[idx]);
            }
        }
    }
}
