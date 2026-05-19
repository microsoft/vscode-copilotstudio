namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Core
{
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Core;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp;
    using System.IO;
    using System.Net.Sockets;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    // Same collection as the provider tests: both touch the static ClientReadyTcs.
    [Collection(nameof(LspWindowLogMessageCollection))]
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

        // The LSP window/logMessage forwarding pump (LspWindowLogMessageLoggerProvider)
        // is gated on the client's "initialized" notification. JsonRpcStream is the
        // single place that observes that notification and releases the gate.
        [Fact]
        public async Task RunAsync_Signals_ClientReady_On_Initialized_Notification()
        {
            ResetClientReadyTcs();
            Assert.False(LspWindowLogMessageLoggerProvider.ClientReadyTcs.Task.IsCompleted);

            var transport = new InitializedThenStopTransport();
            var stream = new JsonRpcStream(transport, NullLogger<JsonRpcStream>.Instance);

            await stream.RunAsync(CancellationToken.None);

            Assert.True(LspWindowLogMessageLoggerProvider.ClientReadyTcs.Task.IsCompletedSuccessfully);

            ResetClientReadyTcs();
        }

        private static void ResetClientReadyTcs()
        {
            var field = typeof(LspWindowLogMessageLoggerProvider).GetField(
                nameof(LspWindowLogMessageLoggerProvider.ClientReadyTcs),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(field);
            field!.SetValue(null, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        private sealed class InitializedThenStopTransport : ILspTransport
        {
            private int _readCount;
            private bool _active = true;

            public bool IsActive => _active;

            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task<BaseJsonRpcMessage> GetNextMessageAsync(CancellationToken cancellationToken)
            {
                if (_readCount++ == 0)
                {
                    BaseJsonRpcMessage init = new LspJsonRpcMessage { Method = LspMethods.Initialized };
                    return Task.FromResult(init);
                }

                _active = false;
                BaseJsonRpcMessage sentinel = new LspJsonRpcMessage { Method = "_stop" };
                return Task.FromResult(sentinel);
            }

            public Task SendAsync<T>(T response, CancellationToken cancellationToken) where T : BaseJsonRpcMessage
                => Task.CompletedTask;

            public void Dispose() { }
        }
    }
}
