
namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    using System.Text.Json;

    public sealed class LspJsonRpcMessage : BaseJsonRpcMessage
    {
        public string Method { get; init; } = string.Empty;

        public JsonElement? Params { get; init; } = default;
    }
}
