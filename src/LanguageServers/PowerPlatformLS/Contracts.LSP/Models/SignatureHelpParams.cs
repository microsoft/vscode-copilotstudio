namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{

    using System.Text.Json.Serialization;

    public sealed class SignatureHelpParams : TextDocumentPositionParams
    {
        public SignatureHelpContext? Context { get; set; } = null;
    }
}