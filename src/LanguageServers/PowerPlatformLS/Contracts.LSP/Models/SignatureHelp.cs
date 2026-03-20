namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{

    using System.Text.Json.Serialization;

    public sealed class SignatureHelp
    {
        public SignatureInformation[] Signatures { get; set; } = [];

        public uint? ActiveSignature { get; set; } = null;

        public uint? ActiveParameter { get; set; } = null;
    }
}