namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public sealed class ClientCapabilities
    {
        public ClientGeneralCapabilities? General { get; set; }

        public JsonElement? Experimental { get; set; }
    }
}