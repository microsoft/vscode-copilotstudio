// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Node S4 (TDD D34) - the knowledge-file discovery scan in ReadWorkspaceDefinitionAsync is
// shape-keyed: CLI agents scan capabilities/knowledge/files/, classic agents scan
// knowledge/files/. Location is shape-keyed, NOT a migration: classic stays byte-identical.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class CliAgentNodeS4KnowledgeFilePathTests
{
    private static readonly byte[] CsvBytes = Encoding.UTF8.GetBytes("col1,col2\n1,2\n");

    [Fact]
    public async Task CliAgent_NewKnowledgeFile_UnderCapabilitiesFolder_IsDiscovered()
    {
        var (_, _, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        await accessor.WriteAsync(
            new AgentFilePath("capabilities/knowledge/files/NewCliDoc.csv"), CsvBytes, CancellationToken.None);

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        Assert.Contains(read.Components.OfType<FileAttachmentComponent>(),
            c => string.Equals(c.DisplayName, "NewCliDoc.csv", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CliAgent_NewKnowledgeFile_UnderClassicFolder_IsIgnored()
    {
        // A file placed at the classic knowledge/files/ path must NOT be discovered for a CLI
        // agent (the CLI scan only looks at capabilities/knowledge/files/).
        var (_, _, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        await accessor.WriteAsync(
            new AgentFilePath("knowledge/files/StrayClassicDoc.csv"), CsvBytes, CancellationToken.None);

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        Assert.DoesNotContain(read.Components.OfType<FileAttachmentComponent>(),
            c => string.Equals(c.DisplayName, "StrayClassicDoc.csv", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ClassicAgent_NewKnowledgeFile_UnderClassicFolder_IsDiscovered()
    {
        // Classic regression: classic agents still scan knowledge/files/ (unchanged).
        var (_, _, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("HRAgent");

        await accessor.WriteAsync(
            new AgentFilePath("knowledge/files/NewClassicDoc.csv"), CsvBytes, CancellationToken.None);

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        Assert.Contains(read.Components.OfType<FileAttachmentComponent>(),
            c => string.Equals(c.DisplayName, "NewClassicDoc.csv", StringComparison.OrdinalIgnoreCase));
    }
}
