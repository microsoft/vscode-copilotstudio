namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent
{
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Reflection;
    using System.Threading;
    using Xunit;

    [Collection("LoggingTestsCollection")]
    public class LspExceptionHandlerTests : IDisposable
    {
        private readonly TestLogger _testLogger = new();
        private readonly LspLogger _logger;

        public LspExceptionHandlerTests()
        {
            ResetLspLoggerState();
            _logger = new LspLogger(new TestLogger<LspLogger>(_testLogger));
        }

        public void Dispose()
        {
            LspRequestContext.CurrentRequestId = 0;
            ResetLspLoggerState();
        }

        [Fact]
        public void Handle_HttpRequestException_Returns_502_Without_Logging()
        {
            var ex = new HttpRequestException("Connection refused");

            var (code, message) = LspExceptionHandler.Handle(ex, _logger);

            Assert.Equal(502, code);
            Assert.Equal("Connection refused", message);
            Assert.Empty(_testLogger.Error);
            Assert.Empty(_testLogger.Warning);
        }

        [Fact]
        public void Handle_HttpRequestException_401_Maps_To_401()
        {
            var ex = new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);

            var (code, _) = LspExceptionHandler.Handle(ex, _logger);

            Assert.Equal(401, code);
        }

        [Fact]
        public void Handle_HttpRequestException_403_Passes_Through()
        {
            var ex = new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden);

            var (code, _) = LspExceptionHandler.Handle(ex, _logger);

            Assert.Equal(403, code);
        }

        [Fact]
        public void Handle_HttpRequestException_429_Maps_To_429()
        {
            var ex = new HttpRequestException("Too Many Requests", null, HttpStatusCode.TooManyRequests);

            var (code, _) = LspExceptionHandler.Handle(ex, _logger);

            Assert.Equal(429, code);
        }

        [Fact]
        public void Handle_HttpRequestException_500_Passes_Through()
        {
            var ex = new HttpRequestException("Server Error", null, HttpStatusCode.InternalServerError);

            var (code, _) = LspExceptionHandler.Handle(ex, _logger);

            Assert.Equal(500, code);
        }

        [Fact]
        public void Handle_FileNotFoundException_Returns_400_Without_Logging()
        {
            var ex = new FileNotFoundException("agent.mcs.yml not found");

            var (code, message) = LspExceptionHandler.Handle(ex, _logger);

            Assert.Equal(400, code);
            Assert.Equal("agent.mcs.yml not found", message);
            Assert.Empty(_testLogger.Error);
        }

        [Fact]
        public void Handle_DirectoryNotFoundException_Returns_400_Without_Logging()
        {
            var ex = new DirectoryNotFoundException("workspace dir missing");

            var (code, message) = LspExceptionHandler.Handle(ex, _logger);

            Assert.Equal(400, code);
            Assert.Empty(_testLogger.Error);
        }

        [Fact]
        public void Handle_InvalidOperationException_Returns_400_Without_Logging()
        {
            var ex = new InvalidOperationException("Agent is not connected");

            var (code, message) = LspExceptionHandler.Handle(ex, _logger);

            Assert.Equal(400, code);
            Assert.Equal("Agent is not connected", message);
            Assert.Empty(_testLogger.Error);
        }

        [Fact]
        public void Handle_WorkspaceValidationException_Returns_400_Without_Logging()
        {
            var ex = new WorkspaceValidationException(
                [new WorkspaceDiagnostic("workflows/Notify Jane Doe-abc/metadata.yml", "Unexpected character.", 12, 5)]);

            var (code, message) = LspExceptionHandler.Handle(ex, _logger);

            Assert.Equal(400, code);
            Assert.Equal(
                "1 workspace file could not be read:" + Environment.NewLine + "  workflows/Notify Jane Doe-abc/metadata.yml(12,5): Unexpected character.",
                StripPiiTags(message));
            Assert.Empty(_testLogger.Error);
            Assert.Empty(_testLogger.Warning);
            Assert.DoesNotContain(_testLogger.Info, entry => entry.Contains("Notify Jane Doe", StringComparison.Ordinal));
        }

        [Fact]
        public void Handle_WorkspaceValidationException_TagsFileAndMessageSoTelemetryRedactsThem()
        {
            var ex = new WorkspaceValidationException(
                [new WorkspaceDiagnostic("topics/Jane Doe Onboarding.mcs.yml", "Duplicate key 'customer-private-key'.", 12, 5)]);

            var (_, message) = LspExceptionHandler.Handle(ex, _logger);
            var redacted = RedactPiiTags(message);

            Assert.DoesNotContain("Jane Doe Onboarding", redacted, StringComparison.Ordinal);
            Assert.DoesNotContain("customer-private-key", redacted, StringComparison.Ordinal);
            Assert.Contains("(12,5)", redacted, StringComparison.Ordinal);
            Assert.Contains("1 workspace file could not be read", redacted, StringComparison.Ordinal);
        }

        [Fact]
        public void Handle_WorkspaceValidationException_EncodesContentThatWouldCloseTheMarker()
        {
            var ex = new WorkspaceValidationException(
                [new WorkspaceDiagnostic("topics/One.mcs.yml", "Duplicate key '</pii>secret'.", 1, 1)]);

            var (_, message) = LspExceptionHandler.Handle(ex, _logger);

            Assert.DoesNotContain("secret", RedactPiiTags(message), StringComparison.Ordinal);
            Assert.Contains("Duplicate key '</pii>secret'.", StripPiiTags(message), StringComparison.Ordinal);
        }

        [Fact]
        public void Handle_WorkspaceValidationException_KeepsUnpositionedDiagnosticsReadable()
        {
            var ex = new WorkspaceValidationException(
                [new WorkspaceDiagnostic("settings.mcs.yml", "Unreadable.", 0, 0)]);

            var (_, message) = LspExceptionHandler.Handle(ex, _logger);

            Assert.EndsWith("  settings.mcs.yml: Unreadable.", StripPiiTags(message), StringComparison.Ordinal);
        }

        private static readonly System.Text.RegularExpressions.Regex PiiTagPattern =
            new("<pii(?: type=\"(?<type>[^\"]+)\")?(?: encoded=\"true\")?>(?<value>.*?)</pii>", System.Text.RegularExpressions.RegexOptions.Singleline);

        [Fact]
        public void DescribeConflicts_NoConflicts_ReturnsAnEmptyMessage()
        {
            Assert.Equal(string.Empty, LspExceptionHandler.DescribeConflicts(Array.Empty<WorkspaceDiagnostic>()));
        }

        [Fact]
        public void DescribeConflicts_NamesEveryConflictedFileForTheUser()
        {
            var message = LspExceptionHandler.DescribeConflicts(
            [
                new WorkspaceDiagnostic("topics/Jane Doe Onboarding.mcs.yml", "Unresolved merge conflict.", 3, 1, WorkspaceDiagnosticKind.MergeConflict),
                new WorkspaceDiagnostic("settings.mcs.yml", "Unresolved merge conflict.", 7, 1, WorkspaceDiagnosticKind.MergeConflict),
            ]);

            Assert.Contains("2 workspace files have unresolved merge conflicts", StripPiiTags(message), StringComparison.Ordinal);
            Assert.Contains("topics/Jane Doe Onboarding.mcs.yml(3,1)", StripPiiTags(message), StringComparison.Ordinal);
            Assert.Contains("settings.mcs.yml(7,1)", StripPiiTags(message), StringComparison.Ordinal);
        }

        [Fact]
        public void DescribeConflicts_TagsFileNamesSoTelemetryRedactsThem()
        {
            var message = LspExceptionHandler.DescribeConflicts(
                [new WorkspaceDiagnostic("topics/Jane Doe Onboarding.mcs.yml", "Unresolved merge conflict.", 3, 1, WorkspaceDiagnosticKind.MergeConflict)]);

            Assert.DoesNotContain("Jane Doe Onboarding", RedactPiiTags(message), StringComparison.Ordinal);
            Assert.Contains("(3,1)", RedactPiiTags(message), StringComparison.Ordinal);
        }

        [Fact]
        public void DescribeConflicts_RepeatedManifestDiagnostic_UsesOneFileAndOneEntry()
        {
            const string path = "behaviors/skill-1/SKILL.md";
            var message = LspExceptionHandler.DescribeConflicts(
            [
                new WorkspaceDiagnostic(path, "Unresolved merge conflict.", 6, 1, WorkspaceDiagnosticKind.MergeConflict),
                new WorkspaceDiagnostic(path, "Unresolved merge conflict.", 6, 1, WorkspaceDiagnosticKind.MergeConflict),
            ]);

            Assert.Equal(
                "1 workspace file has unresolved merge conflicts. Resolve them before pushing:" + Environment.NewLine
                    + "  behaviors/skill-1/SKILL.md(6,1): Unresolved merge conflict.",
                StripPiiTags(message));
            Assert.DoesNotContain(path, RedactPiiTags(message), StringComparison.Ordinal);
        }

        [Fact]
        public void DescribeConflicts_DifferentConflictsInOneFile_KeepBothEntriesButCountOneFile()
        {
            var message = LspExceptionHandler.DescribeConflicts(
            [
                new WorkspaceDiagnostic("behaviors/skill-1/SKILL.md", "Unresolved merge conflict.", 6, 1, WorkspaceDiagnosticKind.MergeConflict),
                new WorkspaceDiagnostic("behaviors\\skill-1\\SKILL.md", "Unresolved merge conflict.", 16, 1, WorkspaceDiagnosticKind.MergeConflict),
            ]);

            Assert.StartsWith("1 workspace file has unresolved merge conflicts.", StripPiiTags(message), StringComparison.Ordinal);
            Assert.Contains("(6,1)", message, StringComparison.Ordinal);
            Assert.Contains("(16,1)", message, StringComparison.Ordinal);
        }

        [Fact]
        public void DescribeConflicts_CountsFilesBeforeRedactingTheirPaths()
        {
            var message = LspExceptionHandler.DescribeConflicts(
            [
                new WorkspaceDiagnostic("behaviors/first/SKILL.md", "Unresolved merge conflict.", 6, 1, WorkspaceDiagnosticKind.MergeConflict),
                new WorkspaceDiagnostic("behaviors/second/SKILL.md", "Unresolved merge conflict.", 6, 1, WorkspaceDiagnosticKind.MergeConflict),
                new WorkspaceDiagnostic("behaviors/first/SKILL.md", "Unresolved merge conflict.", 6, 1, WorkspaceDiagnosticKind.MergeConflict),
            ]);

            Assert.StartsWith("2 workspace files have unresolved merge conflicts.", StripPiiTags(message), StringComparison.Ordinal);
            Assert.Equal(3, StripPiiTags(message).Split(Environment.NewLine).Length);
        }

        private static string StripPiiTags(string value)
            => PiiTagPattern.Replace(value, match => match.Groups["value"].Value.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&"));

        private static string RedactPiiTags(string value)
            => PiiTagPattern.Replace(value, match => $"[REDACTED {match.Groups["type"].Value}]");

        [Fact]
        public void Handle_OperationCancelled_By_User_Returns_499()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var ex = new OperationCanceledException(cts.Token);

            var (code, message) = LspExceptionHandler.Handle(ex, _logger, cts.Token);

            Assert.Equal(499, code);
            Assert.Equal("Operation was cancelled.", message);
            Assert.Empty(_testLogger.Error);
        }

        [Fact]
        public void Handle_OperationCancelled_Without_Token_Returns_504_Timeout()
        {
            var ex = new OperationCanceledException("timed out");

            var (code, message) = LspExceptionHandler.Handle(ex, _logger);

            Assert.Equal(504, code);
            Assert.Equal("Operation timed out.", message);
            Assert.Empty(_testLogger.Error);
        }

        [Fact]
        public void Handle_UnexpectedException_Returns_500_And_Logs_With_StackTrace()
        {
            Exception captured;
            try { throw new NullReferenceException("oops"); }
            catch (Exception ex) { captured = ex; }

            var (code, message) = LspExceptionHandler.Handle(captured, _logger);

            Assert.Equal(500, code);
            Assert.Equal("oops", message);
            var errorLog = Assert.Single(_testLogger.Error);
            Assert.Contains("NullReferenceException", errorLog);
            Assert.Contains("oops", errorLog);
        }

        private static void ResetLspLoggerState()
        {
            var counterField = typeof(LspLogger).GetField("_lspRequestCounter", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(counterField);
            counterField!.SetValue(null, 0);
        }
    }
}
