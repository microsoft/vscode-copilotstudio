namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public class WorkspaceEdit
    {
        public IFileOperation[]? DocumentChanges { get; set; } = null;
    }
}