namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public sealed class FileOperationFilter
    {
        public required FileOperationPattern Pattern { get; set; }
    }
}