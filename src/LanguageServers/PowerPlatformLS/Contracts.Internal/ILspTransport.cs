namespace Microsoft.PowerPlatformLS.Contracts.Internal
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    public interface ILspTransport : IDisposable
    {
        Task StartAsync(CancellationToken cancellationToken);

        Task<BaseJsonRpcMessage> GetNextMessageAsync(CancellationToken cancellationToken);

        Task SendAsync<T>(T response, CancellationToken cancellationToken) where T : BaseJsonRpcMessage;

        bool IsActive { get; }
    }
}