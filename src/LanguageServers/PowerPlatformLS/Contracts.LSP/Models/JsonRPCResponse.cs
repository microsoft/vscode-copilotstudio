namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public sealed class JsonRpcResponse : BaseJsonRpcMessage
    {
        public JsonElement? Result { get; init; }

        public JsonRpcError? Error { get; init; }
    }
}