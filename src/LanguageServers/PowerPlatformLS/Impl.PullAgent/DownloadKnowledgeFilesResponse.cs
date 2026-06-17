namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;
    using System.Collections.Generic;

    internal class DownloadKnowledgeFilesResponse : ResponseBase
    {
        public IReadOnlyList<KnowledgeFileInfo> Downloaded { get; set; } = Array.Empty<KnowledgeFileInfo>();
    }
}
