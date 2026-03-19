namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{

    using System.Text.Json.Serialization;

    public sealed class SignatureHelpOptions
    {
        public string[]? TriggerCharacters { get; set; } = null;

        public string[]? RetriggerCharacters { get; set; } = null;
    }
}