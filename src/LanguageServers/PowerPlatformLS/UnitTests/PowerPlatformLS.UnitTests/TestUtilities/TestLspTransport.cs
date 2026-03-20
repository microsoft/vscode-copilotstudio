namespace Microsoft.PowerPlatformLS.UnitTests.TestUtilities
{
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Channels;
    using System.Threading.Tasks;

    internal interface ITestStream
    {
        Task<BaseJsonRpcMessage> ReadMessageAsync(CancellationToken cancellationToken);
        bool TryReadMessage([MaybeNullWhen(false)] out BaseJsonRpcMessage message);
        void WriteMessage(BaseJsonRpcMessage message);

        /// <summary>
        /// Wait until all messages are processed.
        /// </summary>
        Task CompleteProcessingAsync();
    }

    internal class TestLspTransport : ILspTransport, ITestStream
    {
        private readonly Channel<BaseJsonRpcMessage> _inputChannel = Channel.CreateUnbounded<BaseJsonRpcMessage>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Channel<BaseJsonRpcMessage> _outputChannel = Channel.CreateUnbounded<BaseJsonRpcMessage>(new UnboundedChannelOptions { SingleReader = true });
        private readonly string _flushMethodName;
        private BaseJsonRpcMessage? _lastMessage = null;

        public TestLspTransport(string flushMethodName)
        {
            _flushMethodName = flushMethodName;
        }

        public bool IsActive { get; private set; } = false;

        public void Dispose()
        {
        }

        public void WriteMessage(BaseJsonRpcMessage message)
        {
            _inputChannel.Writer.TryWrite(message);
        }

        public async Task CompleteProcessingAsync()
        {
            var flushMessage = JsonRpc.CreateRequestMessage(_flushMethodName, (object?)null);
            WriteMessage(flushMessage);
            BaseJsonRpcMessage response;
            while ((response = await ReadMessageAsync(CancellationToken.None)) is not JsonRpcResponse flushResponse ||
                !flushResponse.Result.HasValue ||
                flushResponse.Result.Value.Deserialize<string>() != "done")
            {
                _lastMessage = response;
            }
        }

        public async Task<BaseJsonRpcMessage> ReadMessageAsync(CancellationToken cancellationToken)
        {
            if (TryPopLastMessage(out var message))
            {
                return message;
            }

            return await _outputChannel.Reader.ReadAsync(cancellationToken);
        }

        /// <summary>
        /// Return previous message before queue was flushed, if any.
        /// </summary>
        private bool TryPopLastMessage([MaybeNullWhen(false)] out BaseJsonRpcMessage message)
        {
            message = _lastMessage;
            _lastMessage = null;
            return message != null;
        }

        public bool TryReadMessage([MaybeNullWhen(false)] out BaseJsonRpcMessage message)
        {
            return TryPopLastMessage(out message) || _outputChannel.Reader.TryRead(out message);
        }

        public async Task<BaseJsonRpcMessage> GetNextMessageAsync(CancellationToken cancellationToken)
        {
            try
            {
                var message = await _inputChannel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                return message;
            }
            catch (OperationCanceledException)
            {
                return new JsonRpcResponse { Error = new JsonRpcError { Message = "Operation Cancelled." } };
            }
        }

        public async Task SendAsync<T>(T response, CancellationToken cancellationToken) where T : BaseJsonRpcMessage
        {
            await _outputChannel.Writer.WriteAsync(response, cancellationToken);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            IsActive = true;
            return Task.CompletedTask;
        }
    }
}