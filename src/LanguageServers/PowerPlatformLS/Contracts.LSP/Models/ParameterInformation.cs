namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{

    using System.Text.Json.Serialization;

    public sealed class ParameterInformation
    {
        public object Label { get; set; } = string.Empty;

        public string? Documentation { get; set; } = null;
    }
}