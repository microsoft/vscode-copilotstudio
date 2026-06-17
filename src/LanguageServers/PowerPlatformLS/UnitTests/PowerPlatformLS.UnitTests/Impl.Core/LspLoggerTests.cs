namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Core
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using System;
    using System.Linq;
    using System.Reflection;
    using Xunit;

    [Collection("LoggingTestsCollection")]
    public class LspLoggerTests : IDisposable
    {
        private readonly TestLogger _testLogger = new();
        private readonly LspLogger _logger;

        public LspLoggerTests()
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
        public void LogDebug_Writes_At_Debug_Level()
        {
            _logger.LogDebug("debug value {Value}", 123);

            var debugLog = Assert.Single(_testLogger.Debug);
            Assert.Contains("debug value 123", debugLog);
        }

        [Fact]
        public void LogStartContext_Skips_BuiltIn_Lsp_Methods()
        {
            foreach (var method in new[]
            {
                "textDocument/completion",
                "$/progress",
                "initialize",
                "shutdown",
                "exit",
                "workspace/didChangeConfiguration",
                "workspace/didRenameFiles",
            })
            {
                _logger.LogStartContext(method);
            }

            Assert.Empty(_testLogger.Info);
            Assert.Equal(0, LspRequestContext.CurrentRequestId);
        }

        [Fact]
        public void LogStartContext_Logs_Custom_Methods_At_Information()
        {
            var requestId = LspLogger.AllocateRequestId();
            _logger.SetCurrentRequestId(requestId);

            _logger.LogStartContext("powerplatformls/syncPull");

            var infoLog = Assert.Single(_testLogger.Info);
            Assert.Contains($"[Req: {requestId}] Started handler for: powerplatformls/syncPull", infoLog);
        }

        [Fact]
        public void LogEndContext_Skips_BuiltIn_Lsp_Methods()
        {
            LspRequestContext.CurrentRequestId = 19;

            foreach (var method in new[]
            {
                "textDocument/completion",
                "$/progress",
                "initialize",
            })
            {
                _logger.LogEndContext(method, 5);
            }

            Assert.Empty(_testLogger.Info);
        }

        [Fact]
        public void LogEndContext_Logs_Custom_Methods_With_Duration()
        {
            LspRequestContext.CurrentRequestId = 19;

            _logger.LogEndContext("powerplatformls/syncPull", 17);

            var infoLog = Assert.Single(_testLogger.Info);
            Assert.Contains("[Req: 19] Completed handler for: powerplatformls/syncPull, duration=17ms", infoLog);
        }

        [Theory]
        [InlineData("textDocument/completion")]
        [InlineData("$/progress")]
        [InlineData("initialize")]
        [InlineData("shutdown")]
        [InlineData("exit")]
        [InlineData("workspace/didChangeConfiguration")]
        [InlineData("workspace/didRenameFiles")]
        public void IsBuiltInLspMethod_Returns_True_For_Known_Prefixes(string method)
        {
            Assert.True(LspLogger.IsBuiltInLspMethod(method));
        }

        [Theory]
        [InlineData("powerplatformls/syncPull")]
        [InlineData("workspace/listWorkspaces")]
        [InlineData("copilotstudio/customMethod")]
        public void IsBuiltInLspMethod_Returns_False_For_Custom_Methods(string method)
        {
            Assert.False(LspLogger.IsBuiltInLspMethod(method));
        }

        [Fact]
        public void AllocateRequestId_Increments_Sequentially()
        {
            var first = LspLogger.AllocateRequestId();
            var second = LspLogger.AllocateRequestId();

            Assert.Equal(first + 1, second);
        }

        [Fact]
        public void LogStartContext_Reads_RequestId_From_AsyncLocal()
        {
            var requestId = LspLogger.AllocateRequestId();
            // Simulate the queue calling SetCurrentRequestId (which sets the AsyncLocal)
            // as QueueItem.StartRequestAsync does before LogStartContext.
            _logger.SetCurrentRequestId(requestId);

            _logger.LogStartContext("powerplatformls/syncPush");

            var infoLog = Assert.Single(_testLogger.Info);
            Assert.Contains($"[Req: {requestId}]", infoLog);
        }

        [Fact]
        public void SetCurrentRequestId_Sets_AsyncLocal()
        {
            _logger.SetCurrentRequestId(42);

            Assert.Equal(42, LspRequestContext.CurrentRequestId);
        }

        private static void ResetLspLoggerState()
        {
            var counterField = typeof(LspLogger).GetField("_lspRequestCounter", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(counterField);
            counterField!.SetValue(null, 0);
        }
    }
}
