namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public sealed class FileOperationRegistrationOptions
    {
        public required FileOperationFilter[] Filters { get; set; }
    }
}