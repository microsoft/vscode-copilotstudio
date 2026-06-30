namespace Microsoft.PowerPlatformLS.UnitTests.LanguageServerHost
{
    using Microsoft.PowerPlatformLS.LanguageServerHost;
    using Xunit;

    public class PiiScrubTelemetryInitializerTests
    {
        [Theory]
        [InlineData(
            @"File not found: C:\Users\john\agents\MyAgent\topics\greeting.mcs.yml",
            "File not found: <path>")]
        [InlineData(
            @"Error in D:\Projects\vscode-copilotstudio\src\test.cs",
            "Error in <path>")]
        [InlineData(
            "Authenticated as user@contoso.com successfully",
            "Authenticated as <email> successfully")]
        [InlineData(
            "No PII here, just a normal message",
            "No PII here, just a normal message")]
        [InlineData(
            @"Multiple paths: C:\Users\admin\file.txt and D:\data\secret.json",
            "Multiple paths: <path> and <path>")]
        public void ScrubMessage_Removes_Known_PII_Patterns(string input, string expected)
        {
            var result = PiiScrubTelemetryInitializer.ScrubMessage(input);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void ScrubMessage_Handles_Null_And_Empty()
        {
            Assert.Equal(string.Empty, PiiScrubTelemetryInitializer.ScrubMessage(null));
            Assert.Equal(string.Empty, PiiScrubTelemetryInitializer.ScrubMessage(string.Empty));
        }
    }
}
