namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    using System.Text.Json.Serialization;

    public sealed class ClientGeneralCapabilities
    {
        public StaleRequestSupportOptions? StaleRequestSupport { get; set; }

    }
}