
namespace Microsoft.PowerPlatformLS.UnitTests
{
    using System.Collections.Concurrent;
    using System.IO;

    internal class TestDataReader
    {
        private const string TestDataRoot = "TestData";
        private static readonly ConcurrentDictionary<string, string> FileNameToContent = new ConcurrentDictionary<string, string>();

        public static string GetTestData(string filename)
        {
            if (!FileNameToContent.TryGetValue(filename, out var data))
            {
                data = File.ReadAllText(Path.Combine(TestDataRoot, filename)).ReplaceLineEndings("\r\n");
                FileNameToContent[filename] = data;
            }

            return data;
        }
    }
}
