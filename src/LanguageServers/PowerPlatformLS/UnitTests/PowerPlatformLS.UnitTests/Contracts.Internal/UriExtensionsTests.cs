namespace Microsoft.PowerPlatformLS.UnitTests.Contracts.Internal
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using System;
    using Xunit;

    public class UriExtensionsTests
    {
        [Theory]
        // Windows
        [InlineData("file:///C:/Users/John/Documents/report.pdf", "C:/Users/John/Documents/report.pdf", false)]
        [InlineData("file:///D:/data/test.txt", "D:/data/test.txt", false)]
        [InlineData("file:///c%3A/pvaAgent/Agent1/topics/Escalate.mcs.yml", "c:/pvaAgent/Agent1/topics/Escalate.mcs.yml", false)]

        // Linux/OSx
        [InlineData("file:///home/jane/docs/report.pdf", "/home/jane/docs/report.pdf", true)]
        [InlineData("file:///etc/hosts", "/etc/hosts", true)]
        public void FilePathNormalizedTest(string filePath, string expected, bool isUnix)
        {
            var originalIsLinuxPlatform = PlatformService.IsUnixPlatform;
            try
            {
                PlatformService.IsUnixPlatform = () => isUnix;

                Uri fileUri = new Uri(filePath);
                var normalized = fileUri.ToFilePath();

                Assert.Equal(expected, normalized.ToString());
            }
            finally
            {
                PlatformService.IsUnixPlatform = originalIsLinuxPlatform;

            }
        }

        [Theory]
        // Windows
        [InlineData("file:///C:/Users/John/Documents/", "C:/Users/John/Documents/", false)]
        [InlineData("file:///D:/data/", "D:/data/", false)]
        [InlineData("file:///c%3A/pvaAgent/Agent1/topics/", "c:/pvaAgent/Agent1/topics/", false)]

        // Linux/OSx
        [InlineData("file:///home/jane/docs/", "/home/jane/docs/", true)]
        [InlineData("file:///etc/hosts/", "/etc/hosts/", true)]
        [InlineData("file:///", "", true)]
        public void DirectoryPathNormalizedTest(string directoryPath, string expected, bool isUnix)
        {
            var originalIsUnixPlatform = PlatformService.IsUnixPlatform;
            try
            {
                PlatformService.IsUnixPlatform = () => isUnix;

                Uri fileUri = new Uri(directoryPath);
                var normalized = fileUri.ToDirectoryPath();

                Assert.Equal(expected, normalized.ToString());
            }
            finally
            {
                PlatformService.IsUnixPlatform = originalIsUnixPlatform;
            }
        }
    }
}
