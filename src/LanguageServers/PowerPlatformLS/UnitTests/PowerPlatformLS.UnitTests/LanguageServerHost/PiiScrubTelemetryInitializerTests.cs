namespace Microsoft.PowerPlatformLS.UnitTests.LanguageServerHost
{
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.PowerPlatformLS.LanguageServerHost;
    using System.Collections.Generic;
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
        [InlineData(
            "Agent at /home/developer/agents/MyBot/agent.mcs.yml",
            "Agent at <path>")]
        [InlineData(
            "Workspace: /Users/john/Projects/copilot-studio/agent/",
            "Workspace: <path>")]
        [InlineData(
            "Temp file /tmp/mcs-sync-12345/output.json failed",
            "Temp file <path> failed")]
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

        [Fact]
        public void Initialize_Scrubs_TraceTelemetry_Message_And_Properties()
        {
            var initializer = new PiiScrubTelemetryInitializer();
            var trace = new TraceTelemetry(@"Error at C:\Users\john\file.cs");
            trace.Properties["FormattedMessage"] = "user@contoso.com failed";
            trace.Properties["Agent"] = @"C:\Users\john\MyBot";
            trace.Properties["SafeKey"] = "no pii here";

            initializer.Initialize(trace);

            Assert.Equal("Error at <path>", trace.Message);
            Assert.Equal("<email> failed", trace.Properties["FormattedMessage"]);
            Assert.Equal("<path>", trace.Properties["Agent"]);
            Assert.Equal("no pii here", trace.Properties["SafeKey"]);
        }

        [Fact]
        public void Initialize_Scrubs_ExceptionTelemetry_Properties()
        {
            var initializer = new PiiScrubTelemetryInitializer();
            var ex = new ExceptionTelemetry(new System.Exception("test"));
            ex.Properties["Message"] = @"Failed for C:\Users\admin\agent";
            ex.Properties["{OriginalFormat}"] = "user@example.com error";

            initializer.Initialize(ex);

            Assert.Equal("Failed for <path>", ex.Properties["Message"]);
            Assert.Equal("<email> error", ex.Properties["{OriginalFormat}"]);
        }

        [Fact]
        public void Initialize_Ignores_Non_Trace_Non_Exception_Telemetry()
        {
            var initializer = new PiiScrubTelemetryInitializer();
            var metric = new MetricTelemetry("test_metric", 42);

            // Should not throw
            initializer.Initialize(metric);

            Assert.Equal("test_metric", metric.Name);
        }

        [Fact]
        public void Initialize_Scrubs_Agent_Property_With_Email()
        {
            var initializer = new PiiScrubTelemetryInitializer();
            var trace = new TraceTelemetry("handler completed");
            trace.Properties["Agent"] = "admin@company.com agent";

            initializer.Initialize(trace);

            Assert.Equal("<email> agent", trace.Properties["Agent"]);
        }

        [Fact]
        public void Initialize_Leaves_Clean_Agent_Property_Unchanged()
        {
            var initializer = new PiiScrubTelemetryInitializer();
            var trace = new TraceTelemetry("handler completed");
            trace.Properties["Agent"] = "MCS Helper";

            initializer.Initialize(trace);

            Assert.Equal("MCS Helper", trace.Properties["Agent"]);
        }
    }
}
