namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Core
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CommonLanguageServerProtocol.Framework.JsonRpc;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Impl.Core;
    using Moq;
    using System.IO;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    public class LanguageServerListenerTests
    {
        // Belt-and-suspenders regression: even if a future transport exception slips past
        // the JsonRpcStream guard, the BackgroundService must not terminate the host.
        [Fact]
        public async Task ExecuteAsync_Swallows_IOException_Surfaced_By_Stream()
        {
            var streamMock = new Mock<IJsonRpcStream>();
            streamMock
                .Setup(s => s.RunAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.FromException(new IOException(
                    "Unable to write data to the transport connection: Broken pipe.",
                    new SocketException())));

            var serverMock = new Mock<ILanguageServer>();
            serverMock.Setup(s => s.WaitForExitAsync()).Returns(Task.CompletedTask);

            var loggerMock = new Mock<ILspLogger>();

            var listener = new LanguageServerListener(streamMock.Object, loggerMock.Object, serverMock.Object);

            var startEx = await Record.ExceptionAsync(() => listener.StartAsync(CancellationToken.None));
            Assert.Null(startEx);

            var executeTask = listener.ExecuteTask;
            Assert.NotNull(executeTask);
            var execEx = await Record.ExceptionAsync(() => executeTask!);
            Assert.Null(execEx);

            serverMock.Verify(s => s.WaitForExitAsync(), Times.Once);
        }
    }
}
