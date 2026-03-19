namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.Transport
{
    using System.Buffers;
    using System.Collections.Concurrent;
    using System.Text;
    using System.Text.Json;

    /// <summary>
    /// Client-side LSP transport over streams. Implements the LSP base protocol
    /// (Content-Length framing) and provides request/response correlation and notification capture.
    /// </summary>
    public sealed class LspClientTransport
    {
        private readonly Stream _inputStream;  // write to server stdin
        private readonly Stream _outputStream; // read from server stdout
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement?>> _pendingRequests = new();
        private readonly ConcurrentBag<(string Method, JsonElement? Params)> _notifications = [];
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement?>> _notificationWaiters = new();
        private readonly ConcurrentDictionary<string, Func<string, JsonElement?, JsonElement?>> _serverRequestHandlers = new();
        private readonly TaskCompletionSource _transportDead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _nextId = 1;
        private Task? _readLoop;
        private TextWriter? _trace;

        public LspClientTransport(Stream inputStream, Stream outputStream)
        {
            _inputStream = inputStream;
            _outputStream = outputStream;
        }

        /// <summary>
        /// Register a custom handler for a specific server-initiated request method.
        /// The handler receives the method name and params, and returns the result to send back.
        /// If no handler is registered, the default response policy applies.
        /// </summary>
        public void SetServerRequestHandler(string method, Func<string, JsonElement?, JsonElement?> handler)
        {
            _serverRequestHandlers[method] = handler;
        }

        /// <summary>
        /// Enable wire-level tracing. Every sent/received message and raw header line
        /// will be written here. Pass <c>Console.Error</c> for stderr or a <c>StreamWriter</c>
        /// for file output.
        /// </summary>
        public void EnableTrace(TextWriter traceWriter)
        {
            _trace = traceWriter;
        }


        private void Trace(string message)
        {
            _trace?.WriteLine($"[TRACE {DateTime.UtcNow:HH:mm:ss.fff}] {message}");
            _trace?.Flush();
        }

        /// <summary>
        /// Start the background read loop that processes incoming messages from the server.
        /// </summary>
        public void StartListening(CancellationToken cancellationToken = default)
        {
            _readLoop = Task.Run(() => ReadLoopAsync(cancellationToken), cancellationToken);
        }

        /// <summary>
        /// Send a JSON-RPC request and await the response.
        /// </summary>
        public async Task<JsonElement?> SendRequestAsync(string method, object? @params, CancellationToken cancellationToken = default)
        {
            var id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[id] = tcs;

            using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

            var message = new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params,
            };

            Trace($">>> REQUEST id={id} method={method}");
            await WriteMessageAsync(message, cancellationToken);
            var result = await tcs.Task;
            Trace($"<<< RESPONSE id={id} method={method}");
            return result;
        }

        /// <summary>
        /// Send a JSON-RPC notification (no response expected).
        /// </summary>
        public async Task SendNotificationAsync(string method, object? @params, CancellationToken cancellationToken = default)
        {
            var message = new
            {
                jsonrpc = "2.0",
                method,
                @params,
            };

            Trace($">>> NOTIFICATION method={method}");
            await WriteMessageAsync(message, cancellationToken);
        }

        /// <summary>
        /// Drain and return all captured notifications since the last drain.
        /// </summary>
        public List<(string Method, JsonElement? Params)> DrainNotifications()
        {
            var result = new List<(string, JsonElement?)>();
            while (_notifications.TryTake(out var n))
            {
                result.Add(n);
            }

            return result;
        }

        /// <summary>
        /// Wait for a specific notification method. Event-driven — completes immediately
        /// when the notification arrives. Fails fast if the transport dies.
        /// </summary>
        public async Task<JsonElement?> WaitForNotificationAsync(string method, int timeoutMs, CancellationToken cancellationToken = default)
        {
            // Register the waiter BEFORE draining the buffer. This closes the race
            // window where a notification arrives between the buffer check and
            // waiter registration — the read loop would add it to _notifications
            // (no waiter registered yet) and the waiter would time out despite
            // the notification being available.
            var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var key = $"{method}_{Guid.NewGuid():N}";
            _notificationWaiters[key] = tcs;

            // Now check already-buffered notifications
            var drained = DrainNotifications();
            var matchIndex = -1;
            for (var i = 0; i < drained.Count; i++)
            {
                if (drained[i].Method == method)
                {
                    matchIndex = i;
                    break;
                }
            }

            if (matchIndex >= 0)
            {
                // Found in buffer — remove our waiter since we don't need it.
                _notificationWaiters.TryRemove(key, out _);

                // Edge case: the read loop may have already delivered a second matching
                // notification to our TCS between registration and this point. If so,
                // put that notification back into the buffer so it isn't lost.
                if (tcs.Task.IsCompletedSuccessfully)
                {
                    _notifications.Add((method, tcs.Task.Result));
                }

                for (var i = 0; i < drained.Count; i++)
                {
                    if (i != matchIndex)
                    {
                        _notifications.Add(drained[i]);
                    }
                }

                return drained[matchIndex].Params;
            }

            // No match in buffer — put everything back
            for (var i = 0; i < drained.Count; i++)
            {
                _notifications.Add(drained[i]);
            }

            // Waiter is already registered — set up timeout

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);
            using var reg = cts.Token.Register(() => tcs.TrySetException(
                new TimeoutException($"Timed out waiting for notification '{method}' after {timeoutMs}ms.")));

            try
            {
                // Race: notification arrival vs transport death vs timeout
                var completed = await Task.WhenAny(tcs.Task, _transportDead.Task);
                if (completed == _transportDead.Task)
                {
                    throw new IOException("LSP transport closed while waiting for notification.");
                }

                return await tcs.Task;
            }
            finally
            {
                _notificationWaiters.TryRemove(key, out _);
            }
        }

        private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
        {
            if (_transportDead.Task.IsCompleted)
            {
                throw new IOException("LSP transport is closed.");
            }

            var json = JsonSerializer.Serialize(message, SerializationOptions.Default);
            var bodyBytes = Encoding.UTF8.GetBytes(json);
            var header = $"Content-Length: {bodyBytes.Length}\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);

            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await _inputStream.WriteAsync(headerBytes, cancellationToken);
                await _inputStream.WriteAsync(bodyBytes, cancellationToken);
                await _inputStream.FlushAsync(cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task ReadLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var message = await ReadMessageAsync(cancellationToken);
                    if (message is null)
                    {
                        Trace("ReadLoop: stream ended (null message)");
                        break; // stream closed
                    }

                    Trace($"ReadLoop: dispatching message: {message.Value.GetRawText()[..Math.Min(200, message.Value.GetRawText().Length)]}");
                    ProcessIncomingMessage(message.Value);
                }
            }
            catch (OperationCanceledException)
            {
                Trace("ReadLoop: cancelled (expected during shutdown)");
            }
            catch (Exception ex)
            {
                Trace($"ReadLoop: EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                // Signal all waiters that the transport is dead
                _transportDead.TrySetResult();

                var ex = new IOException("LSP transport closed.");
                foreach (var (_, tcs) in _pendingRequests)
                {
                    tcs.TrySetException(ex);
                }

                foreach (var (_, tcs) in _notificationWaiters)
                {
                    tcs.TrySetException(ex);
                }
            }
        }

        private async Task<JsonElement?> ReadMessageAsync(CancellationToken cancellationToken)
        {
            // Parse headers (Content-Length).
            // The server may write non-LSP log lines to stdout (info:, warn:, etc.)
            // interleaved with LSP messages. We must tolerate:
            //   - Non-header lines before Content-Length → skip them
            //   - Empty lines before Content-Length → skip them (could be log output spacing)
            //   - Only treat an empty line as the header/body separator AFTER we've seen Content-Length
            int contentLength = -1;

            while (true)
            {
                var line = await ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    return null; // stream ended
                }

                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line["Content-Length:".Length..].Trim();
                    contentLength = int.Parse(value);
                    Trace($"ReadMsg: Content-Length={contentLength}");
                    continue;
                }

                // Log non-empty non-header lines (these are server log output on stdout)
                if (line.Length > 0)
                {
                    Trace($"ReadMsg: skip non-header line: {line[..Math.Min(120, line.Length)]}");
                }

                // Empty line is the header/body separator — but only if we've seen Content-Length.
                // Otherwise it's just log output spacing and we skip it.
                if (line.Length == 0)
                {
                    if (contentLength >= 0)
                    {
                        break; // valid separator after Content-Length
                    }

                    // Skip empty lines that appear before any Content-Length header
                    continue;
                }

                // Any other line (Content-Type, log output, etc.) — skip it
            }

            // Read body.
            // The server writes log output to stdout on background threads, which can
            // land between the header separator and the actual JSON body. We handle this
            // by scanning forward byte-by-byte until we find '{' (JSON-RPC messages
            // always start with '{'), then reading contentLength - 1 remaining bytes.
            var bodyBuffer = new byte[contentLength];

            // Scan past any interleaved log output to find the JSON body start
            while (true)
            {
                var oneByte = new byte[1];
                var read = await _outputStream.ReadAsync(oneByte, cancellationToken);
                if (read == 0)
                {
                    return null; // stream ended
                }

                if (oneByte[0] == (byte)'{')
                {
                    bodyBuffer[0] = oneByte[0];
                    break;
                }

                // Not '{' — this is log output noise, skip it
                Trace($"ReadMsg: skip pre-body byte: 0x{oneByte[0]:X2} '{(char)oneByte[0]}'");
            }

            // Read the remaining contentLength - 1 bytes of the JSON body
            var totalRead = 1;
            while (totalRead < contentLength)
            {
                var read = await _outputStream.ReadAsync(bodyBuffer.AsMemory(totalRead, contentLength - totalRead), cancellationToken);
                if (read == 0)
                {
                    return null; // stream ended
                }

                totalRead += read;
            }

            var doc = JsonDocument.Parse(bodyBuffer);
            return doc.RootElement.Clone();
        }

        private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var sb = new StringBuilder();
            var buffer = new byte[1];

            while (true)
            {
                var read = await _outputStream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return sb.Length > 0 ? sb.ToString() : null;
                }

                var c = (char)buffer[0];
                if (c == '\n')
                {
                    // Trim trailing \r if present
                    if (sb.Length > 0 && sb[^1] == '\r')
                    {
                        sb.Length--;
                    }

                    return sb.ToString();
                }

                sb.Append(c);
            }
        }

        private void ProcessIncomingMessage(JsonElement message)
        {
            // Check if it's a response (has "id" and "result" or "error")
            if (message.TryGetProperty("id", out var idElement) &&
                (message.TryGetProperty("result", out _) || message.TryGetProperty("error", out _)))
            {
                var id = idElement.GetInt32();
                Trace($"ProcessMsg: response id={id}");
                if (_pendingRequests.TryRemove(id, out var tcs))
                {
                    if (message.TryGetProperty("error", out var error))
                    {
                        Trace($"ProcessMsg: id={id} ERROR: {error.GetRawText()[..Math.Min(200, error.GetRawText().Length)]}");
                        tcs.TrySetException(new LspErrorException(error));
                    }
                    else if (message.TryGetProperty("result", out var result))
                    {
                        Trace($"ProcessMsg: id={id} result: {result.GetRawText()[..Math.Min(200, result.GetRawText().Length)]}");
                        tcs.TrySetResult(result.Clone());
                    }
                    else
                    {
                        Trace($"ProcessMsg: id={id} null result");
                        tcs.TrySetResult(null);
                    }
                }
                else
                {
                    Trace($"ProcessMsg: id={id} NO PENDING REQUEST FOUND (orphaned response)");
                }
            }
            else if (message.TryGetProperty("method", out var methodElement))
            {
                var method = methodElement.GetString() ?? string.Empty;
                message.TryGetProperty("params", out var @params);
                var cloned = @params.ValueKind != JsonValueKind.Undefined ? @params.Clone() : (JsonElement?)null;

                // Distinguish server requests (method + id) from notifications (method only).
                // Server requests require a JSON-RPC response; notifications do not.
                if (message.TryGetProperty("id", out var serverReqIdElement))
                {
                    _ = HandleServerRequestAsync(serverReqIdElement, method, cloned);
                    return;
                }

                // It's a notification — check if anyone is waiting for this method
                var delivered = false;
                foreach (var (key, tcs) in _notificationWaiters)
                {
                    if (key.StartsWith(method + "_", StringComparison.Ordinal))
                    {
                        if (tcs.TrySetResult(cloned))
                        {
                            _notificationWaiters.TryRemove(key, out _);
                            delivered = true;
                            break;
                        }
                    }
                }

                if (!delivered)
                {
                    _notifications.Add((method, cloned));
                }
            }
        }

        /// <summary>
        /// Handle a server-initiated JSON-RPC request by invoking the registered handler
        /// (or the default response policy) and sending the result back. Exceptions in the
        /// handler are surfaced as JSON-RPC error responses (-32603 InternalError).
        /// </summary>
        private async Task HandleServerRequestAsync(JsonElement idElement, string method, JsonElement? @params)
        {
            // Determine the id — could be int or string per JSON-RPC spec
            object id = idElement.ValueKind == JsonValueKind.Number
                ? idElement.GetInt32()
                : (object)(idElement.GetString() ?? idElement.GetRawText());

            Trace($"ProcessMsg: server request id={id} method={method}");

            try
            {
                JsonElement? result;

                if (_serverRequestHandlers.TryGetValue(method, out var handler))
                {
                    result = handler(method, @params);
                }
                else
                {
                    result = GetDefaultServerRequestResponse(method, @params);
                }

                Trace($"ProcessMsg: responding to server request id={id} method={method}");
                await SendServerRequestResponseAsync(id, result);
            }
            catch (Exception ex)
            {
                Trace($"ProcessMsg: server request handler FAILED id={id} method={method}: {ex.Message}");
                await SendServerRequestErrorAsync(id, -32603, ex.Message);
            }
        }

        /// <summary>
        /// Default response policy for server-initiated requests. Method-aware to avoid
        /// breaking common server requests that expect specific result shapes.
        /// </summary>
        private static JsonElement? GetDefaultServerRequestResponse(string method, JsonElement? @params)
        {
            return method switch
            {
                // workspace/configuration expects an array of config values, one per requested item.
                // Returning [] means "no configuration overrides" — safe default.
                "workspace/configuration" => JsonSerializer.SerializeToElement(Array.Empty<object>()),

                // All other methods: return null (void result).
                // This covers client/registerCapability, client/unregisterCapability,
                // window/workDoneProgress/create, and custom server requests.
                _ => null,
            };
        }

        /// <summary>
        /// Send a JSON-RPC success response to a server-initiated request.
        /// </summary>
        private async Task SendServerRequestResponseAsync(object id, JsonElement? result)
        {
            var resultPayload = result.HasValue
                ? result.Value
                : JsonSerializer.SerializeToElement<object?>(null);
            var message = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = resultPayload,
            };

            await WriteMessageAsync(message, CancellationToken.None);
        }

        /// <summary>
        /// Send a JSON-RPC error response to a server-initiated request.
        /// </summary>
        private async Task SendServerRequestErrorAsync(object id, int code, string message)
        {
            var errorResponse = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new Dictionary<string, object>
                {
                    ["code"] = code,
                    ["message"] = message,
                },
            };

            await WriteMessageAsync(errorResponse, CancellationToken.None);
        }
    }

    /// <summary>
    /// Exception representing an LSP JSON-RPC error response.
    /// </summary>
    public sealed class LspErrorException : Exception
    {
        public JsonElement ErrorData { get; }

        public LspErrorException(JsonElement error)
            : base(error.TryGetProperty("message", out var msg) ? msg.GetString() : error.ToString())
        {
            ErrorData = error.Clone();
        }
    }
}