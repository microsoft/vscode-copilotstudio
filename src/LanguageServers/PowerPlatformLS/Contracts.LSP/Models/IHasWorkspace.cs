namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    using System;

    public interface IHasWorkspace
    {
        Uri WorkspaceUri { get; }
    }
}
