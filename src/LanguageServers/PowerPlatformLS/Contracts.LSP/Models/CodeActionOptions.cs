namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public sealed class CodeActionOptions
    {
        public string[]? CodeActionKinds { get; set; } = null;

        public bool? ResolveProvider { get; set; } = null;
    }
}