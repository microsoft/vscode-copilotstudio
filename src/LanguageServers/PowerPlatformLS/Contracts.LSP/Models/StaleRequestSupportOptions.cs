namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    using System.Text.Json.Serialization;

    public sealed class StaleRequestSupportOptions
    {
        public bool Cancel { get; set; } = false;

        public string[] RetryOnContentModified = [];
    }
}