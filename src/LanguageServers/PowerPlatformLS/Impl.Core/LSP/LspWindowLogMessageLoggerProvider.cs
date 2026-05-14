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
    /// <see cref="ILoggerProvider"/> that forwards every <see cref="ILogger"/> entry
    /// to the LSP client as a <c>window/logMessage</c> notification. The VS Code
    /// language client renders these via its <c>LogOutputChannel</c>, producing the
    /// timestamped, color-coded <c>[info]/[warning]/[error]</c> formatting users see
    /// in the GitHub Copilot Chat output panel.
    /// </summary>
    /// <remarks>
    /// <para>Log entries are pushed onto an unbounded <see cref="Channel{T}"/> by the
    /// logger callback (a single, allocation-light enqueue) and drained by a single
    /// background pump that owns the only call site of <c>transport.SendAsync</c>.
    /// This keeps the logger call path completely free of I/O and locks, and makes
    /// re-entrancy impossible — any log that the transport itself produces while
    /// sending is just another enqueue, never a nested SendAsync.</para>
    /// <para>The transport is resolved lazily: entries logged before the transport
    /// is available or connected are simply dropped (the client cannot receive them
    /// anyway). AppInsights logging continues to capture them in parallel.</para>
    /// </remarks>
    [ProviderAlias("LspWindowLogMessage")]
    public sealed class LspWindowLogMessageLoggerProvider : ILoggerProvider
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ConcurrentDictionary<string, ForwardingLogger> _loggers = new();

        // Unbounded MPSC: many loggers write, one pump reads. Bounded would risk
        // dropping messages or blocking the caller; the pump drains aggressively
        // so unbounded growth is not a concern in practice.
        private readonly Channel<QueuedEntry> _queue = Channel.CreateUnbounded<QueuedEntry>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

        private readonly CancellationTokenSource _shutdownCts = new();

        private int _pumpStarted;
        private ILspTransport? _cachedTransport;

        public LspWindowLogMessageLoggerProvider(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return _loggers.GetOrAdd(categoryName, name => new ForwardingLogger(name, this));
        }

        public void Dispose()
        {
            _shutdownCts.Cancel();
            _queue.Writer.TryComplete();
            _loggers.Clear();
            _shutdownCts.Dispose();
        }

        private void Enqueue(QueuedEntry entry)
        {
            // TryWrite never blocks on an unbounded channel.
            if (!_queue.Writer.TryWrite(entry))
            {
                return;
            }

            EnsurePumpStarted();
        }

        private void EnsurePumpStarted()
        {
            if (Interlocked.CompareExchange(ref _pumpStarted, 1, 0) != 0)
            {
                return;
            }

            // Long-running so the pump does not occupy a regular threadpool slot.
            _ = Task.Factory.StartNew(
                PumpAsync,
                _shutdownCts.Token,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default).Unwrap();
        }

        private async Task PumpAsync()
        {
            var token = _shutdownCts.Token;
            try
            {
                while (await _queue.Reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    while (_queue.Reader.TryRead(out var entry))
                    {
                        await SendAsync(entry, token).ConfigureAwait(false);
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

        private async Task SendAsync(QueuedEntry entry, CancellationToken cancellationToken)
        {
            var transport = ResolveTransport();
            if (transport == null || !transport.IsActive)
            {
                return;
            }

            LspJsonRpcMessage rpcMessage;
            try
            {
                var payload = new LogMessageParams
                {
                    Type = entry.MessageType,
                    Message = entry.Message,
                };

                rpcMessage = new LspJsonRpcMessage
                {
                    Method = LspMethods.PowerPlatformLogMessage,
                    Params = JsonSerializer.SerializeToElement(payload, Constants.DefaultSerializationOptions),
                };
            }
            catch
            {
                return;
            }

            try
            {
                await transport.SendAsync(rpcMessage, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Transport may have closed underneath us. Drop and keep pumping.
            }
        }

        private ILspTransport? ResolveTransport()
        {
            if (_cachedTransport != null)
            {
                return _cachedTransport;
            }

            try
            {
                _cachedTransport = (ILspTransport?)_serviceProvider.GetService(typeof(ILspTransport));
            }
            catch
            {
                // Not yet available; try again on the next entry.
            }

            return _cachedTransport;
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

            // We accept everything and let MEL's filter layer decide what to forward.
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

                // LSP MessageType: Error = 1, Warning = 2, Info = 3, Log = 4, Debug = 5.
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
