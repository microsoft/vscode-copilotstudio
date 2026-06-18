namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;
    using System.Collections.Generic;

    internal class ListKnowledgeFilesResponse : ResponseBase
    {
        public IReadOnlyList<KnowledgeFileInfo> Files { get; set; } = Array.Empty<KnowledgeFileInfo>();
    }
}
