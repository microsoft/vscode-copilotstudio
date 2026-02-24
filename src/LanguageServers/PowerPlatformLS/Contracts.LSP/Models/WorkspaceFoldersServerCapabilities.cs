namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public sealed class WorkspaceFoldersCapabilities
    {
        public bool? Supported { get; set; }
        public bool? ChangeNotifications { get; set; }
    }
}