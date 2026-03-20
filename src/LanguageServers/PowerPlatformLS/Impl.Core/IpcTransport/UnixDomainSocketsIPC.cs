namespace Microsoft.PowerPlatformLS.Impl.Core.IpcTransport
{
    using Microsoft.Extensions.Logging;
    using System.Net.Sockets;

    // Unix Domain Sockets are backed by a *file* (not a IP address / port like normal sockets). 
    internal sealed class UnixDomainSocketsIPC : BaseIpcTransport
    {
        // Can only create the stream after we've connected. 
        private NetworkStream? _stream;

        private readonly Socket _socket;

        // Filename backing the socket. 
        private readonly string _unixEndpoint;

        public override bool IsActive => _socket.Connected;

        public UnixDomainSocketsIPC(string unixEndpoint, ILogger logger)
            : base(logger)
        {
            _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

            _unixEndpoint = unixEndpoint;
        }

        protected override Stream Reader => _stream ?? throw new InvalidOperationException($"Call StartAsync()");

        protected override Stream Writer => _stream ?? throw new InvalidOperationException($"Call StartAsync()");

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            await _socket.ConnectAsync(new UnixDomainSocketEndPoint(_unixEndpoint), cancellationToken).ConfigureAwait(false);

            _stream = new NetworkStream(_socket, true);
        }

        protected override void ChildTransportDispose()
        {
            _stream?.Dispose();
            _socket.Dispose();
        }
    }
}
