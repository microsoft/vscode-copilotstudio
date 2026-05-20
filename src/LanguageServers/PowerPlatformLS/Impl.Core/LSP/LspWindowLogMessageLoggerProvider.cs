namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp
{
    using Microsoft.Extensions.Logging;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;
    using System.Collections.Concurrent;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Channels;
    using System.Threading.Tasks;

    /// <summary>
    /// Forwards <see cref="ILogger"/> entries to the LSP client via
    /// <c>window/logMessage</c>, rendered by the extension's LogOutputChannel
    /// with <c>[error]/[warning]/[info]</c> prefix and timestamp.
    /// </summary>
    /// <remarks>
    /// Entries are queued on an unbounded <see cref="Channel{T}"/> and drained by
    /// a single background pump, keeping the logger call path free of I/O. The pump
    /// waits for both the transport to connect and the client's <c>initialized</c>
    /// notification (<see cref="ClientReadyTcs"/>) before flushing, so the extension's
    /// custom handler is wired and takes precedence over vscode-languageclient's
    /// built-in default (which would otherwise prepend a duplicate
    /// <c>[Error|Warning|Info - h:mm:ss AM/PM]</c>).
    /// </remarks>
    [ProviderAlias("LspWindowLogMessage")]
    internal sealed class LspWindowLogMessageLoggerProvider : ILoggerProvider
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ConcurrentDictionary<string, ForwardingLogger> _loggers = new();

        // Unbounded MPSC: many loggers write, one pump reads.
        private readonly Channel<QueuedEntry> _queue = Channel.CreateUnbounded<QueuedEntry>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

        private readonly CancellationTokenSource _shutdownCts = new();

        // Signaled by JsonRpcStream on the client's "initialized" notification.
        // Not readonly so tests can reset between cases.
        internal static TaskCompletionSource<bool> ClientReadyTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal static void SignalClientReady() => ClientReadyTcs.TrySetResult(true);

        public LspWindowLogMessageLoggerProvider(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;

            // Dedicated long-running thread; the pump otherwise parks in WaitToReadAsync.
            _ = Task.Factory.StartNew(
                PumpAsync,
                _shutdownCts.Token,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default).Unwrap();
        }

        public ILogger CreateLogger(string categoryName)
        {
            return _loggers.GetOrAdd(categoryName, name => new ForwardingLogger(ShortenCategory(name), this));
        }

        public void Dispose()
        {
            _shutdownCts.Cancel();
            _queue.Writer.TryComplete();
            _loggers.Clear();
            _shutdownCts.Dispose();
        }

        // TryWrite never blocks on an unbounded channel.
        private void Enqueue(QueuedEntry entry) => _queue.Writer.TryWrite(entry);

        private async Task PumpAsync()
        {
            var token = _shutdownCts.Token;
            try
            {
                // Poll until the transport is registered and connected. Entries
                // accumulate in the channel meanwhile.
                ILspTransport? transport = null;
                while (!token.IsCancellationRequested)
                {
                    transport = _serviceProvider.GetService(typeof(ILspTransport)) as ILspTransport;
                    if (transport is { IsActive: true })
                    {
                        break;
                    }

                    await Task.Delay(50, token).ConfigureAwait(false);
                }

                if (transport == null)
                {
                    return;
                }

                // Wait for client "initialized" so our user handler wins over the built-in.
                await ClientReadyTcs.Task.WaitAsync(token).ConfigureAwait(false);

                while (await _queue.Reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    while (_queue.Reader.TryRead(out var entry))
                    {
                        if (!transport.IsActive) return;
                        await SendAsync(transport, entry, token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown.
            }
            catch
            {
                // Logging must never crash the host.
            }
        }

        private static async Task SendAsync(ILspTransport transport, QueuedEntry entry, CancellationToken cancellationToken)
        {
            var rpcMessage = new LspJsonRpcMessage
            {
                Method = LspMethods.WindowLogMessage,
                Params = JsonSerializer.SerializeToElement(
                    new LogMessageParams { Type = entry.MessageType, Message = entry.Message },
                    Constants.DefaultSerializationOptions),
            };

            try
            {
                await transport.SendAsync(rpcMessage, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Transport may have closed underneath us. Drop and keep pumping.
            }
        }

        private static string ShortenCategory(string categoryName)
        {
            if (string.IsNullOrEmpty(categoryName))
            {
                return categoryName;
            }

            // Strip generic arity / generic args so e.g. "Foo`1[[Bar]]" → "Foo".
            int tick = categoryName.IndexOf('`');
            if (tick >= 0)
            {
                categoryName = categoryName.Substring(0, tick);
            }

            int lastDot = categoryName.LastIndexOf('.');
            return lastDot >= 0 && lastDot < categoryName.Length - 1
                ? categoryName.Substring(lastDot + 1)
                : categoryName;
        }

        private readonly struct QueuedEntry
        {
            public QueuedEntry(int messageType, string message)
            {
                MessageType = messageType;
                Message = message;
            }

            public int MessageType { get; }

            public string Message { get; }
        }

        private sealed class ForwardingLogger : ILogger
        {
            private readonly string _category;
            private readonly LspWindowLogMessageLoggerProvider _owner;

            public ForwardingLogger(string category, LspWindowLogMessageLoggerProvider owner)
            {
                _category = category;
                _owner = owner;
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            // Accept everything; MEL's filter layer decides what to forward.
            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel) || formatter == null)
                {
                    return;
                }

                string message;
                try
                {
                    message = formatter(state, exception);
                }
                catch
                {
                    return;
                }

                if (exception != null)
                {
                    message = string.IsNullOrEmpty(message)
                        ? exception.ToString()
                        : $"{message}\n{exception}";
                }

                if (string.IsNullOrEmpty(message))
                {
                    return;
                }

                // LSP MessageType: Error = 1, Warning = 2, Info = 3, Trace = 4, Debug = 5.
                int messageType = logLevel switch
                {
                    LogLevel.Critical    => 1,
                    LogLevel.Error       => 1,
                    LogLevel.Warning     => 2,
                    LogLevel.Information => 3,
                    LogLevel.Debug       => 5,
                    LogLevel.Trace       => 4,
                    _                    => 4,
                };

                _owner.Enqueue(new QueuedEntry(messageType, $"[{_category}] {message}"));
            }

            private sealed class NullScope : IDisposable
            {
                public static NullScope Instance { get; } = new NullScope();

                public void Dispose() { }
            }
        }
    }
}
