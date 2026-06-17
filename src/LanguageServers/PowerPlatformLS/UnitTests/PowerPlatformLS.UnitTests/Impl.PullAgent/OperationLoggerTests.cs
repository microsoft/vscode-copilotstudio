namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using System;
    using System.Linq;
    using System.Threading.Tasks;
    using Xunit;

    [Collection("LoggingTestsCollection")]
    public class OperationLoggerTests : IDisposable
    {
        private readonly TestLogger _testLogger = new();
        private readonly LspOperationLogger _logger;

        public OperationLoggerTests()
        {
            _logger = new LspOperationLogger(new TestLogger<LspOperationLogger>(_testLogger));
        }

        public void Dispose()
        {
            LspRequestContext.CurrentRequestId = 0;
        }

        [Fact]
        public void Execute_Logs_Start_And_Completion_At_Information()
        {
            var result = _logger.Execute("syncPull", () => 7);

            Assert.Equal(7, result);
            var infoLogs = _testLogger.Info.ToList();
            Assert.Equal(2, infoLogs.Count);
            Assert.Contains("Sync operation started: syncPull", infoLogs[0]);
            Assert.Contains("Sync operation completed: syncPull", infoLogs[1]);
            Assert.Contains("duration=", infoLogs[1]);
        }

        [Fact]
        public void Execute_Logs_Error_On_Exception()
        {
            var exception = Assert.Throws<InvalidOperationException>(() => _logger.Execute<int>("syncPush", () => throw new InvalidOperationException("boom")));

            Assert.Equal("boom", exception.Message);
            var errorLog = Assert.Single(_testLogger.Error);
            Assert.Contains("InvalidOperationException", errorLog);
            Assert.Contains("boom", errorLog);
            Assert.Contains("Sync operation failed: syncPush", errorLog);
            Assert.Contains("duration=", errorLog);
        }

        [Fact]
        public async Task ExecuteAsync_Logs_Start_And_Completion_At_Information()
        {
            var result = await _logger.ExecuteAsync("syncPull", async () =>
            {
                await Task.Yield();
                return 11;
            });

            Assert.Equal(11, result);
            var infoLogs = _testLogger.Info.ToList();
            Assert.Equal(2, infoLogs.Count);
            Assert.Contains("Sync operation started: syncPull", infoLogs[0]);
            Assert.Contains("Sync operation completed: syncPull", infoLogs[1]);
            Assert.Contains("duration=", infoLogs[1]);
        }

        [Fact]
        public async Task ExecuteAsync_Logs_Error_On_Exception()
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _logger.ExecuteAsync<int>("syncPush", async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("boom");
            }));

            Assert.Equal("boom", exception.Message);
            var errorLog = Assert.Single(_testLogger.Error);
            Assert.Contains("InvalidOperationException", errorLog);
            Assert.Contains("boom", errorLog);
            Assert.Contains("Sync operation failed: syncPush", errorLog);
            Assert.Contains("duration=", errorLog);
        }

        [Fact]
        public void Execute_Includes_RequestId_From_LspRequestContext()
        {
            LspRequestContext.CurrentRequestId = 73;

            _logger.Execute("syncPull", () => 5);

            Assert.All(_testLogger.Info, log => Assert.Contains("[Req: 73]", log));
        }
    }
}
