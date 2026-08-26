// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;

namespace Microsoft.CopilotStudio.Sync.Dataverse;

public interface IStreamingKnowledgeFileClient
{
    /// <summary>
    /// Downloads knowledge file content into the stream. 
    /// </summary>
    Task DownloadKnowledgeFileAsync(Stream destination, BotComponentId botComponentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads knowledge file content from an open stream. 
    /// </summary>
    Task UploadKnowledgeFileAsync(Stream content, Guid botComponentId, string fileName, CancellationToken cancellationToken = default);
}
