namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
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
