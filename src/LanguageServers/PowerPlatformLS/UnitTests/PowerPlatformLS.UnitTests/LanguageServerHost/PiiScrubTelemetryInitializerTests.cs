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

        [Theory]
        [InlineData(
            "Request to https://org.crm.dynamics.com/api/data/v9.2/bots?$filter=name%20eq%20'test'&token=secret123",
            "Request to https://org.crm.dynamics.com/api/data/v9.2/bots?<query>")]
        [InlineData(
            "GET https://api.example.com/path?key=value",
            "GET https://api.example.com/path?<query>")]
        [InlineData(
            "No query string https://api.example.com/path",
            "No query string https://api.example.com/path")]
        public void ScrubMessage_Removes_URL_Query_Strings(string input, string expected)
        {
            var result = PiiScrubTelemetryInitializer.ScrubMessage(input);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void Initialize_Scrubs_ErrorMessage_And_Source_Properties()
        {
            var initializer = new PiiScrubTelemetryInitializer();
            var trace = new TraceTelemetry("operation failed");
            trace.Properties["ErrorMessage"] = @"File not found: C:\Users\john\agent.mcs.yml";
            trace.Properties["Error"] = "user@contoso.com had an issue";
            trace.Properties["Source"] = @"at C:\Users\dev\src\Handler.cs:42";

            initializer.Initialize(trace);

            Assert.Equal("File not found: <path>", trace.Properties["ErrorMessage"]);
            Assert.Equal("<email> had an issue", trace.Properties["Error"]);
            // Path is scrubbed but :42 line number is preserved (not part of path pattern)
            Assert.Equal("at <path>:42", trace.Properties["Source"]);
        }

        [Theory]
        [InlineData(
            "Agent 191e2a0a-5390-f111-8077-000d3a199a69 not found",
            "Agent <id> not found")]
        [InlineData(
            "No GUIDs here",
            "No GUIDs here")]
        [InlineData(
            "Bot 191E2A0A-5390-F111-8077-000D3A199A69 in env 00000000-0000-0000-0000-000000000001",
            "Bot <id> in env <id>")]
        public void ScrubMessage_Removes_GUIDs(string input, string expected)
        {
            var result = PiiScrubTelemetryInitializer.ScrubMessage(input);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData(
            "<pii>hello@a.com</pii> an error",
            "<email> an error")]
        [InlineData(
            "<pii>hello@a.com</pii> an error <pii>something unknown</pii>",
            "<email> an error <redacted>")]
        [InlineData(
            @"<pii>C:\Users\john\file.txt</pii> failed",
            "<path> failed")]
        [InlineData(
            "<pii>191e2a0a-5390-f111-8077-000d3a199a69</pii> not found",
            "<id> not found")]
        [InlineData(
            "<pii>John Smith</pii> triggered an error",
            "<redacted> triggered an error")]
        [InlineData(
            "<pii> hello@a.com </pii> trailing spaces trimmed",
            "<email> trailing spaces trimmed")]
        public void ScrubMessage_Handles_Pii_Tags(string input, string expected)
        {
            var result = PiiScrubTelemetryInitializer.ScrubMessage(input);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void ScrubMessage_Handles_Mixed_Tagged_And_Untagged_PII()
        {
            var result = PiiScrubTelemetryInitializer.ScrubMessage(
                "<pii>user@test.com</pii> also untagged@email.com in message");
            Assert.Equal("<email> also <email> in message", result);
        }

        [Fact]
        public void ScrubMessage_Preserves_SessionId_GUID()
        {
            var result = PiiScrubTelemetryInitializer.ScrubMessage(
                "MCS-LSP Startup: pid=12816, sessionId=191e2a0a-5390-f111-8077-000d3a199a69, telemetry=enabled");
            Assert.Equal("MCS-LSP Startup: pid=12816, sessionId=191e2a0a-5390-f111-8077-000d3a199a69, telemetry=enabled", result);
        }

        [Fact]
        public void Initialize_Scrubs_ContentRoot_Property()
        {
            var initializer = new PiiScrubTelemetryInitializer();
            var trace = new TraceTelemetry("Content root path: <path>");
            trace.Properties["ContentRoot"] = @"c:\Users\gngangomtiem\Projects\vscode-copilotstudio\src\lspOut";

            initializer.Initialize(trace);

            Assert.Equal("<path>", trace.Properties["ContentRoot"]);
        }

        [Theory]
        [InlineData(
            @"<pii type=""EMAIL ADDRESS"" encoded=""true"">user@contoso.com</pii> loaded",
            "<email> loaded")]
        [InlineData(
            @"<pii type=""EMAIL ADDRESS"" encoded=""true"">gngangomtiem@asdkt4.onmicrosoft.com</pii> > Developer: Loaded 0 environment(s)",
            "<email> > Developer: Loaded 0 environment(s)")]
        [InlineData(
            @"<pii type=""PATH"" encoded=""true"">C:\Users\dev\file.txt</pii> failed",
            "<path> failed")]
        [InlineData(
            @"<pii type=""PERSON NAME"" encoded=""true"">John Smith</pii> triggered an error",
            "<redacted> triggered an error")]
        public void ScrubMessage_Handles_Pii_Tags_With_Attributes(string input, string expected)
        {
            var result = PiiScrubTelemetryInitializer.ScrubMessage(input);
            Assert.Equal(expected, result);
        }
    }
}
