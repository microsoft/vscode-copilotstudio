namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;

    internal class ListKnowledgeFilesRequest : DataverseRequest, IHasWorkspace
    {
        public required Uri WorkspaceUri { get; set; }
    }
}
