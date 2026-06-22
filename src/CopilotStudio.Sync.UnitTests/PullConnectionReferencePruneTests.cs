// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Text.Json;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class PullConnectionReferencePruneTests
{
    private const string KeepRef = "cre98_AgentKeep.shared_office365users.aaaa";
    private const string GoneRef = "cre98_AgentGone.shared_sharepointonline.bbbb";

    [Fact]
    public async Task Pull_WhenWorkflowDeletedInCloud_RemovesOrphanedConnectionReference()
    {
        var keepId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var goneId = Guid.Parse("55555555-5555-5555-5555-555555555555");

        await RunPullScenarioAsync(
            cloneWorkflows: new[]
            {
                MakeWorkflow(keepId, "Keep Flow", KeepRef),
                MakeWorkflow(goneId, "Gone Flow", GoneRef),
            },
            pullWorkflows: new[] { MakeWorkflow(keepId, "Keep Flow", KeepRef) },
            injectOrphanIntoCacheAfterClone: false,
            assert: (fileAccessor) =>
            {
                var logicalNames = ReadDefinition(fileAccessor).ConnectionReferences
                    .Select(c => c.ConnectionReferenceLogicalName.Value).ToList();
                Assert.Contains(KeepRef, logicalNames);
                Assert.DoesNotContain(GoneRef, logicalNames);

                var referencesFile = ReadText(fileAccessor, "connectionreferences.mcs.yml");
                Assert.Contains(KeepRef, referencesFile);
                Assert.DoesNotContain(GoneRef, referencesFile);
            });
    }

    [Fact]
    public async Task Pull_WithoutDeletions_KeepsExistingReferences()
    {
        var keepId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        await RunPullScenarioAsync(
            cloneWorkflows: new[] { MakeWorkflow(keepId, "Keep Flow", KeepRef) },
            pullWorkflows: new[] { MakeWorkflow(keepId, "Keep Flow", KeepRef) },
            injectOrphanIntoCacheAfterClone: true,
            assert: (fileAccessor) =>
            {
                var logicalNames = ReadDefinition(fileAccessor).ConnectionReferences
                    .Select(c => c.ConnectionReferenceLogicalName.Value).ToList();
                Assert.Contains(KeepRef, logicalNames);
                Assert.Contains(GoneRef, logicalNames);
            });
    }

    [Fact]
    public async Task Pull_WhenWorkflowDownloadFails_DoesNotPruneOrWipeFlows()
    {
        var keepId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        await RunPullScenarioAsync(
            cloneWorkflows: new[] { MakeWorkflow(keepId, "Keep Flow", KeepRef) },
            pullWorkflows: new[] { MakeWorkflow(keepId, "Keep Flow", KeepRef) },
            injectOrphanIntoCacheAfterClone: false,
            failFirstPullWorkflowDownload: true,
            assert: (fileAccessor) =>
            {
                var definition = ReadDefinition(fileAccessor);
                var logicalNames = definition.ConnectionReferences
                    .Select(c => c.ConnectionReferenceLogicalName.Value).ToList();
                Assert.Contains(KeepRef, logicalNames);
                Assert.Contains(keepId, definition.Flows.Select(f => f.WorkflowId.Value));
            });
    }

    private static async Task RunPullScenarioAsync(
        WorkflowMetadata[] cloneWorkflows,
        WorkflowMetadata[] pullWorkflows,
        bool injectOrphanIntoCacheAfterClone,
        Action<InMemoryFileAccessor> assert,
        WorkflowMetadata[]? secondPullWorkflows = null,
        bool injectOrphanBeforeSecondPull = false,
        bool failFirstPullWorkflowDownload = false)
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "pullprune-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
        var workspace = new DirectoryPath(workspaceRoot.Replace('\\', '/') + "/");
        var agentId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        try
        {
            var currentWorkflows = cloneWorkflows;
            var failWorkflowDownload = false;
            var mockDataverse = new Mock<ISyncDataverseClient>();
            mockDataverse
                .Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    if (failWorkflowDownload)
                    {
                        failWorkflowDownload = false;
                        return Task.FromException<WorkflowMetadata[]>(new IOException("simulated workflow download failure"));
                    }

                    return Task.FromResult(currentWorkflows);
                });
            mockDataverse
                .Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<AIPromptMetadata>());
            mockDataverse
                .Setup(x => x.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IEnumerable<string> names, CancellationToken _) => names
                    .Select(n => new ConnectionReferenceInfo
                    {
                        ConnectionReferenceLogicalName = n,
                        ConnectionId = string.Empty,
                        ConnectorId = "/providers/Microsoft.PowerApps/apis/" + ConnectorOf(n),
                    })
                    .ToArray());

            var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
            var syncInfo = new AgentSyncInfo { AgentId = agentId };

            var bot = new BotEntity.Builder
            {
                SchemaName = new BotEntitySchemaName("cr123"),
                CdsBotId = agentId,
            }.Build();

            mockIsland
                .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PvaComponentChangeSet(null, bot, "token-1"));

            await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

            var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);

            if (injectOrphanIntoCacheAfterClone)
            {
                InjectOrphanIntoCache(fileAccessor);
            }

            var previousDefinition = ReadDefinition(fileAccessor);

            currentWorkflows = pullWorkflows;
            failWorkflowDownload = failFirstPullWorkflowDownload;
            mockIsland
                .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PvaComponentChangeSet(null, bot, "token-2"));

            await synchronizer.PullExistingChangesAsync(workspace, opContext, previousDefinition, mockDataverse.Object, syncInfo, CancellationToken.None);

            if (secondPullWorkflows != null)
            {
                failWorkflowDownload = false;

                if (injectOrphanBeforeSecondPull)
                {
                    InjectOrphanIntoCache(fileAccessor);
                }

                var previousDefinition2 = ReadDefinition(fileAccessor);
                currentWorkflows = secondPullWorkflows;
                mockIsland
                    .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new PvaComponentChangeSet(null, bot, "token-3"));

                await synchronizer.PullExistingChangesAsync(workspace, opContext, previousDefinition2, mockDataverse.Object, syncInfo, CancellationToken.None);
            }

            assert(fileAccessor);
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, true);
            }
        }
    }

    private static void InjectOrphanIntoCache(InMemoryFileAccessor fileAccessor)
    {
        var current = ReadDefinition(fileAccessor);
        if (current.ConnectionReferences.Any(c => string.Equals(c.ConnectionReferenceLogicalName.Value, GoneRef, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var withOrphan = current.WithConnectionReferences(current.ConnectionReferences.Add(
            new ConnectionReference.Builder
            {
                ConnectionReferenceLogicalName = GoneRef,
                ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_sharepointonline",
            }.Build()));
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, withOrphan);
    }

    private static WorkflowMetadata MakeWorkflow(Guid workflowId, string name, string logicalName) => new()
    {
        WorkflowId = workflowId,
        Name = name,
        ClientData = WorkflowJsonReferencing(logicalName),
    };

    private static string ConnectorOf(string logicalName)
    {
        var parts = logicalName.Split('.');
        return parts.FirstOrDefault(p => p.StartsWith("shared_", StringComparison.OrdinalIgnoreCase)) ?? "shared_unknown";
    }

    private static string WorkflowJsonReferencing(string logicalName) =>
        "{\n" +
        "  \"properties\": {\n" +
        "    \"connectionReferences\": {\n" +
        "      \"shared_x\": {\n" +
        $"        \"connection\": {{ \"connectionReferenceLogicalName\": \"{logicalName}\" }}\n" +
        "      }\n" +
        "    }\n" +
        "  }\n" +
        "}";

    private static string ReadText(InMemoryFileAccessor fileAccessor, string path)
    {
        using var stream = fileAccessor.OpenRead(new AgentFilePath(path));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static DefinitionBase ReadDefinition(InMemoryFileAccessor fileAccessor)
    {
        using var stream = fileAccessor.OpenRead(new AgentFilePath(".mcs/botdefinition.json"));
        using (YamlSerializationContext.UseYamlPassThroughSerializationContext())
        {
            return JsonSerializer.Deserialize<DefinitionBase>(stream, ElementSerializer.CreateOptions())!;
        }
    }
}
