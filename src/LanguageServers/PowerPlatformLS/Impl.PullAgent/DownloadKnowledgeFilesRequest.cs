namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;

    internal class DownloadKnowledgeFilesRequest : DataverseRequest, IHasWorkspace
    {
        public required Uri WorkspaceUri { get; set; }

        public string[]? SchemaNames { get; set; }
    }
}
