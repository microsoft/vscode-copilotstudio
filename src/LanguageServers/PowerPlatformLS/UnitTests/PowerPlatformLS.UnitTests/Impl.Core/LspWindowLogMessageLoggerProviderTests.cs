namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Core
{
    using global::Microsoft.Extensions.Logging;
    using global::Microsoft.Extensions.Logging.Abstractions;
    using global::Microsoft.PowerPlatformLS.Contracts.Internal;
    using global::Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using global::Microsoft.PowerPlatformLS.Impl.Core;
    using global::Microsoft.PowerPlatformLS.Impl.Core.Lsp;
    using System;
    using System.Collections.Concurrent;
    using System.Reflection;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    // Serialized: ClientReadyTcs is a process-global static; running these in parallel
    // with each other (or with JsonRpcStream tests that trigger it) would race.
    [Collection(nameof(LspWindowLogMessageCollection))]
    public class LspWindowLogMessageLoggerProviderTests : IDisposable
    {
        private readonly StubTransport _transport = new();
        private readonly StubServiceProvider _services;
        private readonly LspWindowLogMessageLoggerProvider _provider;

        public LspWindowLogMessageLoggerProviderTests()
        {
            ResetClientReadyTcs();
            _services = new StubServiceProvider(_transport);
            _provider = new LspWindowLogMessageLoggerProvider(_services);
        }

        public void Dispose()
        {
            _provider.Dispose();
            ResetClientReadyTcs();
        }

        [Fact]
        public async Task Log_Forwards_WindowLogMessage_After_ClientReady()
        {
            _transport.Activate();
            LspWindowLogMessageLoggerProvider.SignalClientReady();

            var logger = _provider.CreateLogger("My.Namespace.Foo");
            logger.LogInformation("hello world");

            var sent = await _transport.WaitForOneAsync();

            Assert.Equal(LspMethods.WindowLogMessage, sent.Method);
            var payload = sent.Params!.Value.Deserialize<LogMessageParams>(Constants.DefaultSerializationOptions)!;
            Assert.Equal(3, payload.Type); // Information => 3
            Assert.Equal("hello world", payload.Message);
        }

        [Fact]
        public async Task Pump_Buffers_Until_Transport_Becomes_Active()
        {
            // Transport not active yet, but client is "ready".
            LspWindowLogMessageLoggerProvider.SignalClientReady();

            var logger = _provider.CreateLogger("Cat");
            logger.LogInformation("before transport");

            // Nothing should be sent yet.
            await Task.Delay(200);
            Assert.Empty(_transport.Sent);

            // Activating the transport unblocks the pump.
            _transport.Activate();

            var sent = await _transport.WaitForOneAsync();
            Assert.Equal("before transport", DeserializeMessage(sent));
        }

        [Fact]
        public async Task Pump_Buffers_Until_ClientReady_Is_Signaled()
        {
            _transport.Activate();

            var logger = _provider.CreateLogger("Cat");
            logger.LogInformation("before initialized");

            // Even with an active transport, nothing flushes before the gate opens.
            await Task.Delay(200);
            Assert.Empty(_transport.Sent);

            LspWindowLogMessageLoggerProvider.SignalClientReady();

            var sent = await _transport.WaitForOneAsync();
            Assert.Equal("before initialized", DeserializeMessage(sent));
        }

        [Theory]
        [InlineData(LogLevel.Critical, 1)]
        [InlineData(LogLevel.Error, 1)]
        [InlineData(LogLevel.Warning, 2)]
        [InlineData(LogLevel.Information, 3)]
        [InlineData(LogLevel.Trace, 4)]
        [InlineData(LogLevel.Debug, 5)]
        public async Task Log_Maps_LogLevel_To_Lsp_MessageType(LogLevel level, int expectedType)
        {
            _transport.Activate();
            LspWindowLogMessageLoggerProvider.SignalClientReady();

            var logger = _provider.CreateLogger("Cat");
            logger.Log(level, "msg");

            var sent = await _transport.WaitForOneAsync();
            var payload = sent.Params!.Value.Deserialize<LogMessageParams>(Constants.DefaultSerializationOptions)!;
            Assert.Equal(expectedType, payload.Type);
        }

        [Fact]
        public async Task Log_With_Empty_Message_And_No_Exception_Is_Dropped()
        {
            _transport.Activate();
            LspWindowLogMessageLoggerProvider.SignalClientReady();

            var logger = _provider.CreateLogger("Cat");
            logger.LogInformation(string.Empty);
            // Follow up with a real message so we can prove the empty one wasn't sent.
            logger.LogInformation("real");

            var sent = await _transport.WaitForOneAsync();
            Assert.Equal("real", DeserializeMessage(sent));
            await Task.Delay(100);
            Assert.Empty(_transport.Sent);
        }

        [Fact]
        public async Task Log_Appends_Exception_When_Provided()
        {
            _transport.Activate();
            LspWindowLogMessageLoggerProvider.SignalClientReady();

            var logger = _provider.CreateLogger("Cat");
            var ex = new InvalidOperationException("boom");
            logger.LogError(ex, "failed");

            var sent = await _transport.WaitForOneAsync();
            var message = DeserializeMessage(sent);
            Assert.StartsWith("failed", message);
            Assert.Contains("InvalidOperationException", message);
            Assert.Contains("boom", message);
        }

        [Fact]
        public async Task Log_With_Only_Exception_Uses_Exception_String()
        {
            _transport.Activate();
            LspWindowLogMessageLoggerProvider.SignalClientReady();

            var logger = _provider.CreateLogger("Cat");
            var ex = new InvalidOperationException("only");
            logger.LogError(ex, string.Empty);

            var sent = await _transport.WaitForOneAsync();
            var message = DeserializeMessage(sent);
            Assert.Contains("InvalidOperationException", message);
            Assert.Contains("only", message);
        }

        [Theory]
        [InlineData("A.B.C", "msg")]
        [InlineData("OnlyOne", "msg")]
        [InlineData("Generic`1[[X]]", "msg")]
        [InlineData("Ns.Generic`2[[X],[Y]]", "msg")]
        public async Task CreateLogger_Forwards_Message_Without_Category_Prefix(string fullName, string expectedMessage)
        {
            _transport.Activate();
            LspWindowLogMessageLoggerProvider.SignalClientReady();

            var logger = _provider.CreateLogger(fullName);
            logger.LogInformation("msg");

            var sent = await _transport.WaitForOneAsync();
            Assert.Equal(expectedMessage, DeserializeMessage(sent));
        }

        [Fact]
        public void CreateLogger_Returns_Same_Instance_For_Same_Category()
        {
            var a = _provider.CreateLogger("X.Y");
            var b = _provider.CreateLogger("X.Y");
            Assert.Same(a, b);
        }

        [Fact]
        public async Task Pump_Continues_After_Transport_SendAsync_Throws()
        {
            _transport.ThrowOnNextSend = true;
            _transport.Activate();
            LspWindowLogMessageLoggerProvider.SignalClientReady();

            var logger = _provider.CreateLogger("Cat");
            logger.LogInformation("first");  // will throw inside SendAsync
            logger.LogInformation("second"); // must still go through

            var sent = await _transport.WaitForOneAsync();
            Assert.Equal("second", DeserializeMessage(sent));
        }

        [Fact]
        public void SignalClientReady_Is_Idempotent()
        {
            LspWindowLogMessageLoggerProvider.SignalClientReady();
            // Second call must not throw (TrySetResult).
            LspWindowLogMessageLoggerProvider.SignalClientReady();
            Assert.True(LspWindowLogMessageLoggerProvider.ClientReadyTcs.Task.IsCompletedSuccessfully);
        }

        [Fact]
        public void IsEnabled_Returns_True_For_All_Levels_Except_None()
        {
            var logger = _provider.CreateLogger("Cat");
            foreach (LogLevel level in Enum.GetValues<LogLevel>())
            {
                Assert.Equal(level != LogLevel.None, logger.IsEnabled(level));
            }
        }

        private static string DeserializeMessage(LspJsonRpcMessage rpc)
        {
            var payload = rpc.Params!.Value.Deserialize<LogMessageParams>(Constants.DefaultSerializationOptions)!;
            return payload.Message;
        }

        private static void ResetClientReadyTcs()
        {
            var field = typeof(LspWindowLogMessageLoggerProvider).GetField(
                nameof(LspWindowLogMessageLoggerProvider.ClientReadyTcs),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(field);
            field!.SetValue(null, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        private sealed class StubTransport : ILspTransport
        {
            private readonly object _gate = new();
            private TaskCompletionSource<LspJsonRpcMessage> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private bool _active;

            public ConcurrentQueue<LspJsonRpcMessage> Sent { get; } = new();

            public bool ThrowOnNextSend { get; set; }

            public bool IsActive => _active;

            public void Activate() => _active = true;

            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task<BaseJsonRpcMessage> GetNextMessageAsync(CancellationToken cancellationToken)
            {
                // Park forever; the provider doesn't read from the transport.
                var tcs = new TaskCompletionSource<BaseJsonRpcMessage>();
                cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
                return tcs.Task;
            }

            public Task SendAsync<T>(T response, CancellationToken cancellationToken) where T : BaseJsonRpcMessage
            {
                if (ThrowOnNextSend)
                {
                    ThrowOnNextSend = false;
                    return Task.FromException(new InvalidOperationException("simulated transport failure"));
                }

                if (response is LspJsonRpcMessage rpc)
                {
                    Sent.Enqueue(rpc);
                    TaskCompletionSource<LspJsonRpcMessage> toComplete;
                    lock (_gate)
                    {
                        toComplete = _next;
                        _next = new TaskCompletionSource<LspJsonRpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    toComplete.TrySetResult(rpc);
                }

                return Task.CompletedTask;
            }

            public async Task<LspJsonRpcMessage> WaitForOneAsync(int timeoutMs = 5000)
            {
                // If something was sent before the wait was set up, consume and return it.
                if (Sent.TryDequeue(out var existing))
                {
                    return existing;
                }

                Task<LspJsonRpcMessage> wait;
                lock (_gate)
                {
                    wait = _next.Task;
                }

                using var cts = new CancellationTokenSource(timeoutMs);
                var completed = await Task.WhenAny(wait, Task.Delay(Timeout.Infinite, cts.Token));
                if (completed != wait)
                {
                    throw new TimeoutException($"No message sent within {timeoutMs}ms.");
                }

                // Dequeue to stay consistent with the peek-first path above.
                Sent.TryDequeue(out _);
                return await wait;
            }

            public void Dispose() { }
        }

        private sealed class StubServiceProvider : IServiceProvider
        {
            private readonly ILspTransport _transport;
            public StubServiceProvider(ILspTransport transport) => _transport = transport;
            public object? GetService(Type serviceType) =>
                serviceType == typeof(ILspTransport) ? _transport : null;
        }
    }

    [CollectionDefinition(nameof(LspWindowLogMessageCollection), DisableParallelization = true)]
    public sealed class LspWindowLogMessageCollection { }
}
