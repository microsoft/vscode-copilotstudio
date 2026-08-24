// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

internal static class KnowledgeFileTestFixtures
{
    internal static Mock<ISyncDataverseClient> CreateStreamingMock(Func<Stream, Task> onDownload)
    {
        var mock = new Mock<ISyncDataverseClient>();
        mock.As<IStreamingKnowledgeFileClient>()
            .Setup(x => x.DownloadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<BotComponentId>(), It.IsAny<CancellationToken>()))
            .Returns<Stream, BotComponentId, CancellationToken>((destination, _, _) => onDownload(destination));
        return mock;
    }

    internal static void WriteBotCloudCache(IFileAccessor fileAccessor, params BotComponentBase[] components)
    {
        var cloudCache = new BotDefinition()
            .WithEntity(CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cr1")!)
            .WithComponents(components);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, cloudCache);
    }

    internal static FileAttachmentComponent CreateFileComponent(string schemaName, string displayName, Guid id)
    {
        var builder = new FileAttachmentComponent()
            .WithSchemaName(schemaName)
            .WithDisplayName(displayName)
            .WithDescription("desc")
            .ToBuilder();
        builder.Id = id;
        return builder.Build();
    }

    internal static Task SeedContent(Stream destination, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        destination.Write(bytes, 0, bytes.Length);
        return Task.CompletedTask;
    }
}
