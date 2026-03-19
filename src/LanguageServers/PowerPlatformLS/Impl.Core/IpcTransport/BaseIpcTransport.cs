namespace Microsoft.PowerPlatformLS.Impl.Core.IpcTransport
{
    using Microsoft.Extensions.Logging;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Exceptions;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Core.Serialization;
    using System.Text;

    // This base message protocol is described here:
    // https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#headerPart
    // Key points:
    // - the content headers are encoded using ascii.
    // - There is a content-length header which specifies length of body *in bytes* (not characeters), so we need the underlying Stream. 
    // - the content body is encoded using UTF8.
    // - always use \r\n, regardless of platform.
    internal abstract class BaseIpcTransport : ILspTransport
    {
        private bool _disposed = false;
        private readonly ILogger _logger;

        public BaseIpcTransport(ILogger logger)
        {
            _logger = logger;
        }

        public virtual bool IsActive => Reader.CanRead && Writer.CanWrite;

        protected abstract void ChildTransportDispose();

        // These are Stream, not StreamReader/StreamWriter becasue the protocol specifies bytes lengths and encodings,
        // so we need full control over the underlying bytes. 
        protected abstract Stream Reader { get; }

        protected abstract Stream Writer { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            GC.SuppressFinalize(this);

            try
            {
                ChildTransportDispose();
                Reader.Dispose();
                Writer.Dispose();
            }
            catch
            {
                // do nothing
            }
        }

        private enum ParseMode
        {
            // Parsing a header name, before the ':'
            HeaderName,

            // Parsing a header value, after the ':'
            HeaderValue,

            // Parsing the body. 
            Body
        }

        // Possible headers 
        const string ContentLengthHeader = "Content-Length";
        const string ContentTypeHeader = "Content-Type"; // application/vscode-jsonrpc; charset=utf-8

        private static readonly int MaxHeaderLength = Math.Max(ContentLengthHeader.Length, ContentTypeHeader.Length) + 1;
        private static readonly int MaxHeaderValueLength = 50; // arbitrary, safety check against runaway data.

        // Parse a JsonRpc message from the stream. 
        internal static async Task<BaseJsonRpcMessage> ReadMessageAsync(Stream reader, ILogger logger, CancellationToken cancellationToken)
        {
            try
            {
                // Message format is: 
                // Content-Length: ###\r\n    <-- in ascii encoding 
                // \r\n
                // xxxx    <-- string in UTF8 encoding 

                // Where xxxx is ### bytes long, and decoded to a json string via UTF8
                var headers = new Dictionary<string, string>(StringComparer.Ordinal);

                var headerName = new StringBuilder();
                var headerValue = new StringBuilder();

                var mode = ParseMode.HeaderName;
                while (true)
                {
                    bool endOfLine;

                    int b = reader.ReadByte();
                    if (b  == -1)
                    {
                        throw new InvalidOperationException($"Unexpected end of stream");
                    }

                    if (b == '\r') // any \r must be followed by \n. According to spec, regardless of platformn. 
                    {
                        int b2 = reader.ReadByte();
                        if (b2 != '\n')
                        {
                            // Bad protocol ....
                            throw new InvalidOperationException($"Bad protocol: \\r can't be followed by: {b2}");
                        }
                        else
                        {
                            endOfLine = true;
                        }
                    } else
                    {
                        endOfLine = false;
                    }

                    if (mode == ParseMode.HeaderName)
                    {
                        if (b == ':')
                        {
                            mode = ParseMode.HeaderValue;
                            continue;
                        }
                        else if (endOfLine)
                        {
                            if (headerName.Length == 0)
                            {
                                // this is \r\n at the start of line -signifies end of headers.
                                break;
                            }
                            else
                            {
                                throw new InvalidOperationException($"Bad protocol: Unexpected \\r: {headerName}");
                            }
                        }
                        else
                        {
                            if (headerName.Length > MaxHeaderLength)
                            {
                                throw new InvalidOperationException($"Bad protocol: unexpected header: {headerName}");
                            }
                            headerName.Append((char)b);
                            continue;
                        }
                    }
                    else if (mode == ParseMode.HeaderValue)
                    {
                        if (endOfLine)
                        {
                            // end of header value
                            mode = ParseMode.HeaderName;

                            var header = headerName.ToString();
                            if (header != ContentLengthHeader && header != ContentTypeHeader)
                            {
                                logger.LogWarning($"Unrecognized header: {header}");
                            }

                            headers.Add(header, headerValue.ToString().Trim());
                            headerName.Clear();
                            headerValue.Clear();
                            continue;
                        }
                        else
                        {
                            if (headerValue.Length > MaxHeaderValueLength)
                            {
                                throw new InvalidOperationException($"Bad protocol: unexpected value for {headerName}: {headerValue}");
                            }
                            headerValue.Append((char)b);
                        }
                    }
                }

                // Let any format issues here throw an exception. 
                uint contentLengthBytes = uint.Parse(headers[ContentLengthHeader]);

                var buffer = new byte[contentLengthBytes];

                await reader.ReadExactlyAsync(buffer, 0, buffer.Length, cancellationToken);
                
                var serializedMessage = Encoding.UTF8.GetString(buffer);

                logger.LogTrace($"Transport received: {serializedMessage}");
                return LspJsonSerializationHelper.Deserialize<LspJsonRpcMessage>(serializedMessage) ?? throw new ParseException("rpc message is null");
            }
            catch (Exception ex)
            {
                return ex.CreateErrorResponseFromException();
            }
        }

        public Task<BaseJsonRpcMessage> GetNextMessageAsync(CancellationToken cancellationToken)
        {
            return ReadMessageAsync(Reader, _logger, cancellationToken);            
        }

        // Ensure that we only send through the pipe one at a time. 
        private static readonly SemaphoreSlim SendLock = new SemaphoreSlim(1, 1);

        public async Task SendAsync<T>(T message, CancellationToken cancellationToken)
            where T : BaseJsonRpcMessage
        {            
            string messageString = SerializeMessage(message);

            var bodyBytes = Encoding.UTF8.GetBytes(messageString);

            var messageBuilder = new StringBuilder();
            messageBuilder.Append(ContentLengthHeader);
            messageBuilder.Append($": {bodyBytes.Length}\r\n");
            messageBuilder.Append("\r\n");

            // Spec says headers are in Ascii. 
            string messageStr = messageBuilder.ToString();
            var headerBytes = Encoding.ASCII.GetBytes(messageStr);

            await SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await Writer.WriteAsync(headerBytes, 0, headerBytes.Length).ConfigureAwait(false);
                await Writer.WriteAsync(bodyBytes, 0, bodyBytes.Length).ConfigureAwait(false);
                _logger.LogTrace($"Transport sent: {messageString}");
                await Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                SendLock.Release();
            }
        }

        private static string SerializeMessage<T>(T message) where T : BaseJsonRpcMessage => message switch
        {
            LspJsonRpcMessage rpcMessage => LspJsonSerializationHelper.Serialize(rpcMessage),
            JsonRpcResponse rpcResponse => LspJsonSerializationHelper.Serialize(rpcResponse),
            _ => throw new InvalidOperationException("RPC Message Type not recognized.")
        };

        public abstract Task StartAsync(CancellationToken cancellationToken);
    }
}