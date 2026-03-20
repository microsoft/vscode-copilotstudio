namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{

    using System.Text.Json.Serialization;

    public sealed class SignatureHelpContext
    {
        public SignatureHelpTriggerKind TriggerKind { get; set; }

        public string? TriggerCharacter { get; set; }

        public bool IsRetrigger { get; set; } = false;

        public SignatureHelp? ActiveSignatureHelp { get; set; } = null;

    }
}