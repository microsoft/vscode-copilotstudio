namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public sealed class RenameFile : IFileOperation
    {
        public const string KindName = "rename";
        public string Kind { get; set; } = KindName;
        public required Uri OldUri { get; set; }
        public required Uri NewUri { get; set; }
    }
}