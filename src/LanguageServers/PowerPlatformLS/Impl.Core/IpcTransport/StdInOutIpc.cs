namespace Microsoft.PowerPlatformLS.Impl.Core.IpcTransport
{
    using Microsoft.Extensions.Logging;

    internal sealed class StdInOutIpc : BaseIpcTransport
    {
        private readonly Stream _reader;
        private readonly Stream _writer;

        public StdInOutIpc(ILogger logger)
            : base(logger)
        {
            _reader = Console.OpenStandardInput();
            _writer = Console.OpenStandardOutput();
        }

        protected override Stream Reader => _reader;

        protected override Stream Writer => _writer;

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        protected override void ChildTransportDispose()
        {
        }
    }
}