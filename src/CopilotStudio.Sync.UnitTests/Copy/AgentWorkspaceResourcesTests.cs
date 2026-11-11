using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.CopilotStudio.Sync.Copy;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests.Copy;

public class AgentWorkspaceResourcesTests
{
    [Fact]
    public void List_ClassicWorkspace_ListsItsFlowPromptConnectorAndConnectionReferencesInKindOrder()
    {
        using var workspace = CopyTestWorkspace.Classic();
        workspace.WriteConnector();

        var resources = AgentWorkspaceResources.List(workspace.Root);

        Assert.Equal(
            new[]
            {
                new AgentWorkspaceResource(CopyIdentityKinds.Workflow, CopyTestWorkspace.ClassicWorkflowId.ToString("D")),
                new AgentWorkspaceResource(CopyIdentityKinds.AIModel, CopyTestWorkspace.PromptModelId.ToString("D")),
                new AgentWorkspaceResource(CopyIdentityKinds.Connector, CopyTestWorkspace.ConnectorId.ToString("D")),
                new AgentWorkspaceResource(CopyIdentityKinds.ConnectionReference, CopyTestWorkspace.ClassicTeamsReference),
                new AgentWorkspaceResource(CopyIdentityKinds.ConnectionReference, CopyTestWorkspace.ClassicWeatherReference),
                new AgentWorkspaceResource(CopyIdentityKinds.ConnectionReference, CopyTestWorkspace.ConnectorReference),
            },
            resources);
    }

    [Fact]
    public void List_CliWorkspace_ListsTheDeclarationsOfEveryConnectionsFileInNameOrder()
    {
        using var workspace = CopyTestWorkspace.Cli();
        workspace.Write("infrastructure/connections/notes.txt", "not a declaration");

        var resources = AgentWorkspaceResources.List(workspace.Root);

        Assert.Equal(
            new[]
            {
                new AgentWorkspaceResource(CopyIdentityKinds.Workflow, CopyTestWorkspace.CliWorkflowId.ToString("D")),
                new AgentWorkspaceResource(CopyIdentityKinds.ConnectionReference, CopyTestWorkspace.CliTeamsReference),
                new AgentWorkspaceResource(CopyIdentityKinds.ConnectionReference, CopyTestWorkspace.CliWeatherReference),
            },
            resources);
    }

    [Fact]
    public void List_FolderThatNamesNoResource_IsSkipped()
    {
        using var workspace = CopyTestWorkspace.Cli();
        var promptWithoutMetadata = Guid.NewGuid();
        workspace.Write("prompts/unnamed/metadata.yml", "name: unnamed\n");
        workspace.Write($"prompts/draft-{promptWithoutMetadata:D}/prompt.json", "{}");
        workspace.Write("connectors/empty/openapidefinition.json", "{}");
        workspace.Write("connectors/unidentified/metadata.yml", "{\n  \"name\": \"unidentified\"\n}");
        workspace.Write("connectors/listed/metadata.yml", "[]");

        var resources = AgentWorkspaceResources.List(workspace.Root);

        Assert.Equal(new[] { CopyTestWorkspace.CliWorkflowId.ToString("D"), promptWithoutMetadata.ToString("D"), CopyTestWorkspace.CliTeamsReference, CopyTestWorkspace.CliWeatherReference }, resources.Select(resource => resource.Identity));
    }

    [Fact]
    public void List_ReferenceDeclaredInTwoFiles_IsListedOnce()
    {
        using var workspace = CopyTestWorkspace.Cli();
        workspace.Write("infrastructure/connections/zz-duplicate.sync.yaml", $"connectionReferences:\n  - connectionReferenceLogicalName: {CopyTestWorkspace.CliTeamsReference}\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_teams\n");

        var resources = AgentWorkspaceResources.List(workspace.Root);

        Assert.Single(resources, resource => resource.Identity == CopyTestWorkspace.CliTeamsReference);
    }

    [Fact]
    public void List_EmptyWorkspace_ListsNothing()
    {
        using var workspace = CopyTestWorkspace.Cli();
        workspace.Delete("workflows");
        workspace.Delete("infrastructure");

        Assert.Empty(AgentWorkspaceResources.List(workspace.Root));
    }

    [Fact]
    public void List_UnparseableConnectorMetadata_NamesTheFile()
    {
        using var workspace = CopyTestWorkspace.Cli();
        workspace.Write("connectors/broken/metadata.yml", "{ not json");

        var failure = Assert.Throws<InvalidDataException>(() => AgentWorkspaceResources.List(workspace.Root));

        Assert.StartsWith("connectors/broken/metadata.yml could not be read: ", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindExistingAsync_ReturnsWhatTheEnvironmentHolds_InTheGivenOrder()
    {
        var missingWorkflow = Guid.NewGuid();
        var workflow = Guid.NewGuid();
        var model = Guid.NewGuid();
        var connector = Guid.NewGuid();
        var client = new Mock<ICopyDataverseClient>(MockBehavior.Strict);
        client.Setup(environment => environment.FindExistingWorkflowIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { workflow });
        client.Setup(environment => environment.FindExistingAIModelIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { model });
        client.Setup(environment => environment.FindExistingConnectorIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<Guid>());
        client.Setup(environment => environment.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { new SyncDataverseClient.ConnectionReferenceInfo { ConnectionReferenceLogicalName = "CR_AGENT.SHARED_TEAMS" } });
        var resources = new[]
        {
            new AgentWorkspaceResource(CopyIdentityKinds.ConnectionReference, "cr_agent.shared_teams"),
            new AgentWorkspaceResource(CopyIdentityKinds.Workflow, missingWorkflow.ToString("D")),
            new AgentWorkspaceResource(CopyIdentityKinds.Connector, connector.ToString("D")),
            new AgentWorkspaceResource(CopyIdentityKinds.Workflow, workflow.ToString("D").ToUpperInvariant()),
            new AgentWorkspaceResource(CopyIdentityKinds.ConnectionReference, "cr_agent.shared_weather"),
            new AgentWorkspaceResource(CopyIdentityKinds.AIModel, model.ToString("D")),
        };

        var existing = await AgentWorkspaceResources.FindExistingAsync(client.Object, resources, CancellationToken.None);

        Assert.Equal(new[] { resources[0], resources[3], resources[5] }, existing);
        client.Verify(environment => environment.FindExistingWorkflowIdsAsync(It.Is<IEnumerable<Guid>>(identifiers => identifiers.SequenceEqual(new[] { missingWorkflow, workflow })), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(environment => environment.GetConnectionReferencesByLogicalNamesAsync(It.Is<IEnumerable<string>>(names => names.SequenceEqual(new[] { "cr_agent.shared_teams", "cr_agent.shared_weather" })), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FindExistingAsync_LooksUpOnlyTheKindsItIsGiven()
    {
        var workflow = Guid.NewGuid();
        var client = new Mock<ICopyDataverseClient>(MockBehavior.Strict);
        client.Setup(environment => environment.FindExistingWorkflowIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { workflow });

        var existing = await AgentWorkspaceResources.FindExistingAsync(client.Object, new[] { new AgentWorkspaceResource(CopyIdentityKinds.Workflow, workflow.ToString("D")) }, CancellationToken.None);

        Assert.Single(existing);
    }

    [Fact]
    public async Task FindExistingAsync_IdentityThatIsNotAnIdentifier_IsNeverFound()
    {
        var client = new Mock<ICopyDataverseClient>(MockBehavior.Strict);
        client.Setup(environment => environment.FindExistingAIModelIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync((IEnumerable<Guid> identifiers, CancellationToken _) => identifiers.ToArray());

        var existing = await AgentWorkspaceResources.FindExistingAsync(client.Object, new[] { new AgentWorkspaceResource(CopyIdentityKinds.AIModel, "draft-prompt") }, CancellationToken.None);

        Assert.Empty(existing);
        client.Verify(environment => environment.FindExistingAIModelIdsAsync(It.Is<IEnumerable<Guid>>(identifiers => !identifiers.Any()), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(CopyIdentityKinds.Schema)]
    [InlineData("agent")]
    public async Task FindExistingAsync_KindAnAgentWorkspaceDoesNotHold_IsRefused(string kind)
    {
        var client = new Mock<ICopyDataverseClient>(MockBehavior.Strict);

        await Assert.ThrowsAsync<ArgumentException>(() => AgentWorkspaceResources.FindExistingAsync(client.Object, new[] { new AgentWorkspaceResource(kind, Guid.NewGuid().ToString("D")) }, CancellationToken.None));
    }
}
