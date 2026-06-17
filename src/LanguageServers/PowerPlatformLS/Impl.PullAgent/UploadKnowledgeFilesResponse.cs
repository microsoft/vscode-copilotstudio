namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;
    using System.Collections.Generic;

    internal class UploadKnowledgeFilesResponse : ResponseBase
    {
        public IReadOnlyList<string> Uploaded { get; set; } = Array.Empty<string>();
    }
}
