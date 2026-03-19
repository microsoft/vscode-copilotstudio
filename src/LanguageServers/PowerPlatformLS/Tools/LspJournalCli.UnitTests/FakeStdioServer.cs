namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.UnitTests
{
    using System.Collections.Concurrent;
    using System.Text;
    using System.Text.Json;

    /// <summary>
/// A fake LSP server that communicates over in-memory streams. Used to test
/// the client transport's handling of server-initiated requests, notifications,
/// and normal request/response flows.
///
/// Wiring: the "server" reads from <see cref="ServerInputStream"/> and writes
/// to <see cref="ServerOutputStream"/>. The client transport is given the
/// opposite ends: it writes to <see cref="ClientToServerStream"/> (server's stdin)
/// and reads from <see cref="ServerToClientStream"/> (server's stdout).
/// </summary>
internal sealed class FakeStdioServer : IAsyncDisposable
{
    private readonly PairedStream _clientToServer = new();
    private readonly PairedStream _serverToClient = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<JsonElement> _receivedMessages = new();
    private readonly SemaphoreSlim _messageReceived = new(0);
    private Task? _readLoop;

    /// <summary>Client writes here (server's stdin).</summary>
    public Stream ClientToServerStream => _clientToServer.WriteStream;

    /// <summary>Client reads here (server's stdout).</summary>
    public Stream ServerToClientStream => _serverToClient.ReadStream;

    /// <summary>Server reads here (own stdin).</summary>
    private Stream ServerInputStream => _clientToServer.ReadStream;

    /// <summary>Server writes here (own stdout).</summary>
    private Stream ServerOutputStream => _serverToClient.WriteStream;

    /// <summary>
    /// Start the fake server's read loop (consumes client messages).
    /// </summary>
    public void Start()
    {
        _readLoop = Task.Run(async () =>
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    var msg = await ReadLspMessageAsync(ServerInputStream, _cts.Token);
                    if (msg is null) break;
                    _receivedMessages.Enqueue(msg.Value);
                    _messageReceived.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        });
    }

    /// <summary>
    /// Wait for the fake server to receive a message from the client.
    /// </summary>
    public async Task<JsonElement> WaitForClientMessageAsync(int timeoutMs = 5000)
    {
        if (!await _messageReceived.WaitAsync(timeoutMs, _cts.Token))
        {
            throw new TimeoutException($"Fake server did not receive a client message within {timeoutMs}ms.");
        }

        _receivedMessages.TryDequeue(out var msg);
        return msg;
    }

    /// <summary>
    /// Send a JSON-RPC response from the server to the client (reply to a client request).
    /// </summary>
    public async Task SendResponseAsync(int id, object? result)
    {
        var message = new { jsonrpc = "2.0", id, result };
        await WriteLspMessageAsync(ServerOutputStream, message);
    }

    /// <summary>
    /// Send a JSON-RPC error response from the server to the client.
    /// </summary>
    public async Task SendErrorResponseAsync(int id, int code, string message)
    {
        var msg = new { jsonrpc = "2.0", id, error = new { code, message } };
        await WriteLspMessageAsync(ServerOutputStream, msg);
    }

    /// <summary>
    /// Send a JSON-RPC notification from the server to the client.
    /// </summary>
    public async Task SendNotificationAsync(string method, object? @params = null)
    {
        var message = new { jsonrpc = "2.0", method, @params };
        await WriteLspMessageAsync(ServerOutputStream, message);
    }

    /// <summary>
    /// Send a JSON-RPC request from the server to the client (server→client request).
    /// This is the key scenario: the server asks the client something and expects a response.
    /// </summary>
    public async Task SendServerRequestAsync(int id, string method, object? @params = null)
    {
        var message = new { jsonrpc = "2.0", id, method, @params };
        await WriteLspMessageAsync(ServerOutputStream, message);
    }

    /// <summary>
    /// Close the server's output stream, signaling EOF to the client.
    /// </summary>
    public void CloseOutputStream()
    {
        _serverToClient.CompleteWriting();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _serverToClient.CompleteWriting();
        _clientToServer.CompleteWriting();

        if (_readLoop is not null)
        {
            try { await _readLoop; }
            catch { /* best-effort */ }
        }

        _cts.Dispose();
        _messageReceived.Dispose();
    }

    #region LSP base protocol helpers

    private static async Task WriteLspMessageAsync(Stream stream, object message)
    {
        var json = JsonSerializer.Serialize(message, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        var bodyBytes = Encoding.UTF8.GetBytes(json);
        var header = $"Content-Length: {bodyBytes.Length}\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);

        await stream.WriteAsync(headerBytes);
        await stream.WriteAsync(bodyBytes);
        await stream.FlushAsync();
    }

    private static async Task<JsonElement?> ReadLspMessageAsync(Stream stream, CancellationToken ct)
    {
        // Read headers
        int contentLength = -1;
        while (true)
        {
            var line = await ReadLineAsync(stream, ct);
            if (line is null) return null;

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                contentLength = int.Parse(line["Content-Length:".Length..].Trim());
                continue;
            }

            if (line.Length == 0 && contentLength >= 0) break;
        }

        // Read body
        var body = new byte[contentLength];
        int totalRead = 0;
        while (totalRead < contentLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(totalRead, contentLength - totalRead), ct);
            if (read == 0) return null;
            totalRead += read;
        }

        var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buf, ct);
            if (read == 0) return sb.Length > 0 ? sb.ToString() : null;
            var c = (char)buf[0];
            if (c == '\n')
            {
                if (sb.Length > 0 && sb[^1] == '\r') sb.Length--;
                return sb.ToString();
            }
            sb.Append(c);
        }
    }

    #endregion
}

/// <summary>
/// In-memory paired stream: one side writes, the other reads. Backed by a pipe-like
/// mechanism using <see cref="System.IO.Pipelines"/> semantics but via simple
/// MemoryStream + signaling for test simplicity.
/// </summary>
internal sealed class PairedStream
{
    private readonly Pipe _pipe = new();

    public Stream WriteStream => _pipe.WriteStream;
    public Stream ReadStream => _pipe.ReadStream;

    public void CompleteWriting() => _pipe.CompleteWriting();

    /// <summary>
    /// Simple in-memory pipe with blocking read semantics.
    /// </summary>
    private sealed class Pipe
    {
        private readonly SemaphoreSlim _dataAvailable = new(0);
        private readonly object _lock = new();
        private readonly Queue<byte[]> _chunks = new();
        private bool _completed;
        private byte[]? _currentChunk;
        private int _currentOffset;

        public Stream WriteStream { get; }
        public Stream ReadStream { get; }

        public Pipe()
        {
            WriteStream = new PipeWriteStream(this);
            ReadStream = new PipeReadStream(this);
        }

        public void Write(byte[] data)
        {
            lock (_lock)
            {
                if (_completed) throw new ObjectDisposedException("Pipe is closed for writing.");
                _chunks.Enqueue(data);
            }
            _dataAvailable.Release();
        }

        public void CompleteWriting()
        {
            lock (_lock)
            {
                _completed = true;
            }
            _dataAvailable.Release(); // wake any blocked readers
        }

        public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            while (true)
            {
                // Try to read from current chunk
                if (_currentChunk is not null)
                {
                    var available = _currentChunk.Length - _currentOffset;
                    var toCopy = Math.Min(available, count);
                    Buffer.BlockCopy(_currentChunk, _currentOffset, buffer, offset, toCopy);
                    _currentOffset += toCopy;
                    if (_currentOffset >= _currentChunk.Length)
                    {
                        _currentChunk = null;
                        _currentOffset = 0;
                    }
                    return toCopy;
                }

                // Try to get next chunk
                lock (_lock)
                {
                    if (_chunks.Count > 0)
                    {
                        _currentChunk = _chunks.Dequeue();
                        _currentOffset = 0;
                        continue;
                    }

                    if (_completed) return 0; // EOF
                }

                // Wait for data
                await _dataAvailable.WaitAsync(ct);
            }
        }

        private sealed class PipeWriteStream(Pipe pipe) : Stream
        {
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count)
            {
                var copy = new byte[count];
                Buffer.BlockCopy(buffer, offset, copy, 0, count);
                pipe.Write(copy);
            }
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                Write(buffer, offset, count);
                return Task.CompletedTask;
            }
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            {
                var copy = buffer.ToArray();
                pipe.Write(copy);
                return ValueTask.CompletedTask;
            }
            public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        }

        private sealed class PipeReadStream(Pipe pipe) : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) =>
                pipe.ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
                pipe.ReadAsync(buffer, offset, count, ct);
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                // Copy to temp array for simplicity
                var temp = new byte[buffer.Length];
                var read = await pipe.ReadAsync(temp, 0, temp.Length, ct);
                temp.AsMemory(0, read).CopyTo(buffer);
                return read;
            }
        }
    }
}
}
