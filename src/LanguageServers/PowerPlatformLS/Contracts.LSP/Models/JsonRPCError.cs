namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public sealed class JsonRpcError
    {
        public ErrorCodes Code { get; init; } = ErrorCodes.InternalError;

        public string Message { get; init; } = string.Empty;

        public object? Data { get; init; }
    }
}