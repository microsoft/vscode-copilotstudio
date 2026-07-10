namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Core
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
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

        [Fact]
        public void LogEndContext_Logs_Failed_When_Succeeded_Is_False()
        {
            LspRequestContext.CurrentRequestId = 19;

            _logger.LogEndContext("powerplatformls/syncPull", 42, HandlerOutcome.Failure);

            var infoLog = Assert.Single(_testLogger.Info);
            Assert.Contains("[Req: 19] Failed handler for: powerplatformls/syncPull, duration=42ms", infoLog);
        }

        [Fact]
        public void LogEndContext_Logs_Canceled_When_Canceled_Is_True()
        {
            LspRequestContext.CurrentRequestId = 21;

            _logger.LogEndContext("powerplatformls/syncPull", 15, HandlerOutcome.Canceled);

            var infoLog = Assert.Single(_testLogger.Info);
            Assert.Contains("[Req: 21] Canceled handler for: powerplatformls/syncPull, duration=15ms", infoLog);
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

        [Fact]
        public void LogStartContext_Includes_Agent_Name_When_Provided()
        {
            var requestId = LspLogger.AllocateRequestId();
            _logger.SetCurrentRequestId(requestId);

            _logger.LogStartContext("powerplatformls/syncPull", "MCS Helper");

            var infoLog = Assert.Single(_testLogger.Info);
            Assert.Contains($"[Req: {requestId}] Started handler for: powerplatformls/syncPull, agent='MCS Helper'", infoLog);
        }

        [Fact]
        public void LogEndContext_Includes_Agent_Name_And_Duration()
        {
            LspRequestContext.CurrentRequestId = 5;

            _logger.LogEndContext("powerplatformls/getLocalChanges", 12, HandlerOutcome.Success, "MCS Helper");

            var infoLog = Assert.Single(_testLogger.Info);
            Assert.Contains("[Req: 5] Completed handler for: powerplatformls/getLocalChanges, agent='MCS Helper', duration=12ms", infoLog);
        }

        [Fact]
        public void LogEndContext_Agent_Name_Without_Duration()
        {
            LspRequestContext.CurrentRequestId = 7;

            _logger.LogEndContext("powerplatformls/cloneAgent", agentName: "Test Bot");

            var infoLog = Assert.Single(_testLogger.Info);
            Assert.Contains("[Req: 7] Completed handler for: powerplatformls/cloneAgent, agent='Test Bot'", infoLog);
            Assert.DoesNotContain("duration=", infoLog);
        }

        [Fact]
        public void LogEndContext_Failure_With_Agent_Name_Logs_Failed()
        {
            LspRequestContext.CurrentRequestId = 9;

            _logger.LogEndContext("powerplatformls/syncPush", 100, HandlerOutcome.Failure, "My Agent");

            var infoLog = Assert.Single(_testLogger.Info);
            Assert.Contains("Failed handler for: powerplatformls/syncPush, agent='My Agent', duration=100ms", infoLog);
        }

        [Fact]
        public void LogEndContext_Canceled_With_Agent_Name_Logs_Canceled()
        {
            LspRequestContext.CurrentRequestId = 10;

            _logger.LogEndContext("powerplatformls/getRemoteChanges", 50, HandlerOutcome.Canceled, "Bot X");

            var infoLog = Assert.Single(_testLogger.Info);
            Assert.Contains("Canceled handler for: powerplatformls/getRemoteChanges, agent='Bot X', duration=50ms", infoLog);
        }

        [Fact]
        public void LogSensitiveInformation_Logs_Full_Message_For_TestLogger()
        {
            _logger.LogSensitiveInformation("Valid agent: 'c:/Users/john/agents/MyBot/'", "Valid agent: 'MyBot'");

            var infoLog = Assert.Single(_testLogger.Info);
            Assert.Contains("Valid agent: 'c:/Users/john/agents/MyBot/'", infoLog);
        }

        [Fact]
        public void LogSensitiveWarning_Logs_Full_Message_For_TestLogger()
        {
            LspRequestContext.CurrentRequestId = 3;

            _logger.LogSensitiveWarning("Dataverse error: user@email.com has no access", "Dataverse error: access denied");

            var warningLog = Assert.Single(_testLogger.Warning);
            Assert.Contains("user@email.com has no access", warningLog);
        }

        [Fact]
        public void LogSensitiveError_Logs_Full_Message_For_TestLogger()
        {
            LspRequestContext.CurrentRequestId = 4;

            _logger.LogSensitiveError("Failed for c:/Users/john/file.yml", "Failed for file");

            var errorLog = Assert.Single(_testLogger.Error);
            Assert.Contains("c:/Users/john/file.yml", errorLog);
        }

        [Fact]
        public void LogError_Includes_RequestId_When_Set()
        {
            LspRequestContext.CurrentRequestId = 11;

            _logger.LogError("something went wrong");

            var errorLog = Assert.Single(_testLogger.Error);
            Assert.Contains("[Req: 11]", errorLog);
            Assert.Contains("something went wrong", errorLog);
        }

        [Fact]
        public void LogError_Omits_RequestId_When_Zero()
        {
            LspRequestContext.CurrentRequestId = 0;

            _logger.LogError("no request context");

            var errorLog = Assert.Single(_testLogger.Error);
            Assert.DoesNotContain("[Req:", errorLog);
            Assert.Contains("no request context", errorLog);
        }
    }
}
