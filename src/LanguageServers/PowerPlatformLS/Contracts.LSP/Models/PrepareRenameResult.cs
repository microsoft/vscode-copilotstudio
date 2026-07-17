namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public sealed class PrepareRenameResult
    {
        public required Range Range { get; set; }

        public required string Placeholder { get; set; }
    }
}
