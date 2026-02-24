namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    using System.Text.Json.Serialization;

    public abstract class BaseJsonRpcMessage
    {
        // we shoud default to 2.0
        public string JsonRpc { get; init; } = "2.0";

        public int? Id { get; init; }
    }
}