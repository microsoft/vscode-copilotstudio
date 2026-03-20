namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{

    using System.Text.Json.Serialization;

    public sealed class SignatureInformation
    {
        public string Label { get; set; } = string.Empty;

        public string? Documentation { get; set; } = null;

        public ParameterInformation[]? Parameters { get; set; } = null;

        public uint? ActiveParameter { get; set; } = null;
    }
}