namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Core
{
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Core;
    using System.IO;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    public class JsonRpcStreamTests
    {
        // Regression: client-disconnect during SendAsync must not escape RunAsync. Without the
        // fix the IOException propagates through LanguageServerListener.ExecuteAsync and the
        // BackgroundService host terminates the LSP process (BackgroundServiceExceptionBehavior
        // defaults to StopHost), which the extension then surfaces as
        // "Object reference not set to an instance of an object." on the next RPC.
        [Fact]
        public async Task RunAsync_Does_Not_Propagate_IOException_From_SendAsync()
        {
            var transport = new BrokenPipeTransport();
            var stream = new JsonRpcStream(transport, NullLogger<JsonRpcStream>.Instance);

            var ex = await Record.ExceptionAsync(() => stream.RunAsync(CancellationToken.None));

            Assert.Null(ex);
            Assert.True(transport.SendAsyncWasCalled);
        }

        private sealed class BrokenPipeTransport : ILspTransport
        {
            private int _readCount;
            private bool _active = true;

            public bool SendAsyncWasCalled { get; private set; }

            public bool IsActive => _active;

            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task<BaseJsonRpcMessage> GetNextMessageAsync(CancellationToken cancellationToken)
            {
                if (_readCount++ == 0)
                {
                    BaseJsonRpcMessage err = new JsonRpcResponse
                    {
                        Error = new JsonRpcError { Message = "Unexpected end of stream" }
                    };
                    return Task.FromResult(err);
                }

                _active = false;
                BaseJsonRpcMessage sentinel = new JsonRpcResponse { Error = new JsonRpcError() };
                return Task.FromResult(sentinel);
            }

            public Task SendAsync<T>(T response, CancellationToken cancellationToken) where T : BaseJsonRpcMessage
            {
                SendAsyncWasCalled = true;
                _active = false;
                return Task.FromException(new IOException(
                    "Unable to write data to the transport connection: Broken pipe.",
                    new SocketException()));
            }

            public void Dispose() { }
        }
    }
}
