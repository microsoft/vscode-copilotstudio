namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public sealed class RenameFilesParams : IDefaultContextRequest
    {
        public required FileRename[] Files { get; set; }
    }
}
