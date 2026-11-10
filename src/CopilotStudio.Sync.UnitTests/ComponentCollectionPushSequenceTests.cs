// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using System.Text;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;
using ProductionFileAccessorFactory = Microsoft.CopilotStudio.McsCore.InMemoryFileAccessorFactory;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

internal sealed record CollectionThenAgentOutcome(ComponentCollectionPushResult<PushChangesetResult> Result, IReadOnlyList<AuthoringOperationContextBase> Saves, AuthoringOperationContextBase CollectionContext, AuthoringOperationContextBase AgentContext, string AgentCache, string FreshAgentCache, string CollectionCache, string FreshCollectionCache);

public class ComponentCollectionPushSequenceTests
{
    private static readonly DirectoryPath Agent = new("project/Contoso Agent/");

    private static readonly DirectoryPath Alpha = new("project/Alpha Tools/");

    private static readonly DirectoryPath Zeta = new("project/Zeta Tools/");

    private static readonly AgentFilePath CloudCachePath = new(".mcs/botdefinition.json");

    private static readonly AgentFilePath ChangeTokenPath = new(".mcs/changetoken.txt");

    private readonly List<DirectoryPath> _pushed = new();

    [Fact]
    public async Task PushAsync_PushesEveryCollectionBeforeTheAgent()
    {
        var result = await PushAsync(Resolution(Zeta, Alpha));

        Assert.Equal(new[] { Zeta, Alpha, Agent }, _pushed);
        Assert.Equal(new[] { Zeta, Alpha }, result.Collections.Select(collection => collection.Folder));
        Assert.Equal(new[] { "pushed project/Zeta Tools/", "pushed project/Alpha Tools/" }, result.Collections.Select(collection => collection.Pushed));
        Assert.Equal("pushed project/Contoso Agent/", result.Agent);
    }

    [Fact]
    public async Task PushAsync_SkipsACollectionTheCallerDoesNotLetThrough()
    {
        var asked = new List<DirectoryPath>();

        var result = await PushAsync(Resolution(Zeta, Alpha), (folder, _) =>
        {
            asked.Add(folder);
            return Task.FromResult(folder != Zeta);
        });

        Assert.Equal(new[] { Zeta, Alpha }, asked);
        Assert.Equal(new[] { Alpha, Agent }, _pushed);
        Assert.Equal(Alpha, Assert.Single(result.Collections).Folder);
    }

    [Fact]
    public async Task PushAsync_WithoutCollections_PushesOnlyTheAgent()
    {
        var result = await PushAsync(new ComponentCollectionFolderResolution());

        Assert.Equal(new[] { Agent }, _pushed);
        Assert.Empty(result.Collections);
    }

    [Fact]
    public async Task PushAsync_ReportsTheMissingDeclarations()
    {
        Assert.Equal(new[] { "../Absent Tools/" }, (await PushAsync(new ComponentCollectionFolderResolution { Missing = ImmutableArray.Create("../Absent Tools/") })).Missing);
        Assert.Equal(new[] { Agent }, _pushed);
    }

    [Fact]
    public async Task PushAsync_WhenACollectionFails_NeverPushesTheAgent()
    {
        var failure = new InvalidOperationException("Save rejected");

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => ComponentCollectionPushSequence.PushAsync<string>(Resolution(Zeta, Alpha), Agent, (folder, _) => folder == Zeta ? Task.FromException<string>(failure) : Task.FromResult(Record(folder)), null, CancellationToken.None)));
        Assert.Empty(_pushed);
    }

    [Fact]
    public async Task PushAsync_WhenCancelled_PushesNothing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => ComponentCollectionPushSequence.PushAsync(Resolution(Zeta), Agent, (folder, _) => Task.FromResult(Record(folder)), null, cancellation.Token));

        Assert.Empty(_pushed);
    }

    [Fact]
    public async Task PushAsync_WithoutARequiredArgument_Throws()
    {
        Assert.Equal("collections", (await Assert.ThrowsAsync<ArgumentNullException>(() => ComponentCollectionPushSequence.PushAsync(null!, Agent, (folder, _) => Task.FromResult(Record(folder)), null, CancellationToken.None))).ParamName);
        Assert.Equal("pushWorkspaceAsync", (await Assert.ThrowsAsync<ArgumentNullException>(() => ComponentCollectionPushSequence.PushAsync<string>(Resolution(), Agent, null!, null, CancellationToken.None))).ParamName);
    }

    [Fact]
    public async Task PushAsync_ThroughTheSynchronizer_LeavesEveryCacheEqualToAFreshClone()
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseWorkspace(new DirectoryPath("operation/"));

        var outcome = await PushCollectionThenAgentInMemoryAsync(factory, session.Root);

        Assert.Equal(new[] { outcome.CollectionContext, outcome.AgentContext }, outcome.Saves);
        Assert.Single(outcome.Result.Collections);
        Assert.Equal(outcome.FreshCollectionCache, outcome.CollectionCache);
        Assert.Equal(outcome.FreshAgentCache, outcome.AgentCache);
        Assert.Contains("cree9_tools.topic.Lookup", outcome.CollectionCache, StringComparison.Ordinal);
        Assert.Contains("cree9_agent.topic.Greeting", outcome.AgentCache, StringComparison.Ordinal);
    }

    internal static async Task<CollectionThenAgentOutcome> PushCollectionThenAgentInMemoryAsync(IFileAccessorFactory factory, DirectoryPath root)
    {
        var agent = root.GetChildDirectoryPath("Contoso Agent/");
        var collection = root.GetChildDirectoryPath("Shared Tools/");
        var island = new FakeIsland();
        var cloud = new FakeDataverseCloud();
        var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(LspProjectorService.Instance), factory, island.Service.Object, new TestSyncProgress(new List<string>()), new LspComponentPathResolver());
        var agentContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var collectionContext = new BotComponentCollectionAuthoringOperationContext(impersonatedUser: null, organizationInfo: new CdsOrganizationInfo(), reference: new BotComponentCollectionReference(environmentId: "env1", cdsId: Guid.NewGuid()), solutionUniqueName: "TestSolution");
        var agentSyncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        var collectionSyncInfo = new AgentSyncInfo { ComponentCollectionId = Guid.NewGuid() };
        var pushes = new Dictionary<DirectoryPath, (AuthoringOperationContextBase Context, Guid? AgentId, BotComponentBase Component)>
        {
            [collection] = (collectionContext, null, PushComponentCollectionExclusionTests.Topic("cree9_tools.topic.Lookup", "Lookup")),
            [agent] = (agentContext, agentSyncInfo.AgentId, PushComponentCollectionExclusionTests.Topic("cree9_agent.topic.Greeting", "Greeting")),
        };

        await synchronizer.CloneChangesAsync(collection, new ReferenceTracker(), collectionContext, cloud.Client.Object, collectionSyncInfo, CancellationToken.None);
        await synchronizer.CloneChangesAsync(agent, new ReferenceTracker(), agentContext, cloud.Client.Object, agentSyncInfo, CancellationToken.None);
        await WriteAsync(factory.Create(collection), new AgentFilePath("collection.mcs.yml"), "schemaName: cree9_tools\n");
        await WriteAsync(factory.Create(agent), new AgentFilePath("references.mcs.yml"), "componentCollections:\n  - schemaName:\n    directory: ../Shared Tools/\n");

        var result = await ComponentCollectionPushSequence.PushAsync(ComponentCollectionFolders.Resolve(factory, root, agent), agent, (folder, cancellationToken) => synchronizer.PushChangesetAsync(folder, pushes[folder].Context, new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(pushes[folder].Component) }, null, "token-1"), cloud.Client.Object, pushes[folder].AgentId, null, ImmutableArray<AIPromptMetadata>.Empty, cancellationToken), null, CancellationToken.None);

        var freshAgent = root.GetChildDirectoryPath("Fresh Agent/");
        var freshCollection = root.GetChildDirectoryPath("Fresh Tools/");
        await synchronizer.CloneChangesAsync(freshCollection, new ReferenceTracker(), collectionContext, cloud.Client.Object, collectionSyncInfo, CancellationToken.None);
        await synchronizer.CloneChangesAsync(freshAgent, new ReferenceTracker(), agentContext, cloud.Client.Object, agentSyncInfo, CancellationToken.None);

        return new CollectionThenAgentOutcome(
            result,
            island.Saves,
            collectionContext,
            agentContext,
            await CacheOfAsync(factory.Create(agent)),
            await CacheOfAsync(factory.Create(freshAgent)),
            await CacheOfAsync(factory.Create(collection)),
            await CacheOfAsync(factory.Create(freshCollection)));
    }

    private static async Task<string> CacheOfAsync(IFileAccessor workspace) => await workspace.ReadStringAsync(CloudCachePath, CancellationToken.None) + "\n" + await workspace.ReadStringAsync(ChangeTokenPath, CancellationToken.None);

    private static async Task WriteAsync(IFileAccessor workspace, AgentFilePath path, string content) => await workspace.WriteAsync(path, Encoding.UTF8.GetBytes(content), CancellationToken.None);

    private static ComponentCollectionFolderResolution Resolution(params DirectoryPath[] found) => new() { Found = found.ToImmutableArray() };

    private string Record(DirectoryPath folder)
    {
        _pushed.Add(folder);
        return $"pushed {folder}";
    }

    private Task<ComponentCollectionPushResult<string>> PushAsync(ComponentCollectionFolderResolution collections, Func<DirectoryPath, CancellationToken, Task<bool>>? shouldPushCollectionAsync = null) => ComponentCollectionPushSequence.PushAsync(collections, Agent, (folder, _) => Task.FromResult(Record(folder)), shouldPushCollectionAsync, CancellationToken.None);
}
