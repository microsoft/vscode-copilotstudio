namespace Microsoft.PowerPlatformLS.Impl.Core.IpcTransport
{
    using Microsoft.Extensions.Logging;
    using System.IO.Pipes;

    internal sealed class NamedPipeIpc : BaseIpcTransport
    {
        private readonly NamedPipeClientStream _pipeConnection;

        public NamedPipeIpc(string pipeName, ILogger logger) : base(logger)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Named pipes are only supported on Windows.");
            }

            if (pipeName.StartsWith("\\\\.\\pipe\\", StringComparison.InvariantCultureIgnoreCase))
            {
                pipeName = pipeName["\\\\.\\pipe\\".Length..];
            }

            // Although language server is a server, we let the client act as the server part of the pipe
            // This helps avoid the timing issue where client spawns the server but cannot exactly predict when server would create pipe if client was then connecting to it
            // This strategy allows client to create the pipe fully, spawn the server which would then safely connect to the pipe
            _pipeConnection = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            return _pipeConnection.ConnectAsync(cancellationToken);
        }

        public override bool IsActive => _pipeConnection.IsConnected;

        protected override Stream Reader => _pipeConnection;

        protected override Stream Writer => _pipeConnection;

        protected override void ChildTransportDispose()
        {
            _pipeConnection.Dispose();
        }
    }
}