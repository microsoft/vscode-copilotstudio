namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public sealed class CreateFile : IFileOperation
    {
        public const string KindName = "create";
        public string Kind { get; set; } = KindName;
        public required Uri Uri { get; set; }
    }
}