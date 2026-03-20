namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public sealed class ServerWorkspaceCapabilities
    {
        public WorkspaceFoldersCapabilities? WorkspaceFolders { get; set; }

        public FileOperationCapabilities? FileOperations { get; set; }
    }
}