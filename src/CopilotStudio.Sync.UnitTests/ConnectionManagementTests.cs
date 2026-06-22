// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class ConnectionManagementTests
{
    private static readonly DirectoryPath Workspace = new("c:/test/workspace/");

    private static void Write(InMemoryFileAccessor accessor, string relativePath, string content)
    {
        using var stream = accessor.OpenWrite(new AgentFilePath(relativePath));
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    private static void WriteClassicConnectionReferences(InMemoryFileAccessor accessor, params (string logicalName, string connectorId)[] refs)
    {
        var list = refs.Select(r => new ConnectionReference.Builder
        {
            ConnectionReferenceLogicalName = r.logicalName,
            ConnectorId = r.connectorId,
        }.Build()).ToList();

        using var stream = accessor.OpenWrite(new AgentFilePath("connectionreferences.mcs.yml"));
        using var writer = new StreamWriter(stream);
        using var ctx = YamlSerializationContext.UseStandardSerializationContextIfNotDefined(throwOnInvalidYaml: false);
        CodeSerializer.SerializeConnectionReferences(writer, list);
    }

    [Fact]
    public void GetWorkflowStatusViews_AllReferencesBound_CanEnable()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var accessor = (InMemoryFileAccessor)factory.Create(Workspace);
        Write(
            accessor,
            "workflows/notify/metadata.yml",
            "name: Notify Flow\nworkflowId: 11111111-1111-1111-1111-111111111111\nstateCode: 0\nstatusCode: 1\nconnectionReferences:\n  - cr_office365\n");

        var views = new[]
        {
            new AgentConnectionView { ConnectionReferenceLogicalName = "cr_office365", BoundConnectionExists = true },
        };

        var workflows = synchronizer.GetWorkflowStatusViews(Workspace, views);

        var workflow = Assert.Single(workflows);
        Assert.Equal(WorkflowState.Draft, workflow.State);
        Assert.True(workflow.CanEnable);
    }

    [Fact]
    public void GetWorkflowStatusViews_UnboundReference_CannotEnable()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var accessor = (InMemoryFileAccessor)factory.Create(Workspace);
        Write(
            accessor,
            "workflows/notify/metadata.yml",
            "name: Notify Flow\nworkflowId: 22222222-2222-2222-2222-222222222222\nstateCode: 0\nstatusCode: 1\nconnectionReferences:\n  - cr_office365\n");

        var views = new[]
        {
            new AgentConnectionView { ConnectionReferenceLogicalName = "cr_office365", BoundConnectionExists = false },
        };

        var workflows = synchronizer.GetWorkflowStatusViews(Workspace, views);

        var workflow = Assert.Single(workflows);
        Assert.False(workflow.CanEnable);
    }

    [Fact]
    public void TryWriteConnectionsCache_StaleGenerationAfterMutatingWrite_DoesNotOverwrite()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        factory.Create(Workspace);

        var staleGeneration = synchronizer.GetConnectionsCacheGeneration(Workspace);

        var freshViews = new[]
        {
            new AgentConnectionView { ConnectionReferenceLogicalName = "cr_fresh", BoundConnectionExists = true },
        };
        synchronizer.WriteConnectionsCache(Workspace, freshViews);

        var staleViews = new[]
        {
            new AgentConnectionView { ConnectionReferenceLogicalName = "cr_stale", BoundConnectionExists = false },
        };
        var wrote = synchronizer.TryWriteConnectionsCache(Workspace, staleViews, staleGeneration);

        Assert.False(wrote);
        var cache = synchronizer.ReadConnectionsCache(Workspace);
        Assert.NotNull(cache);
        var view = Assert.Single(cache!.Connections);
        Assert.Equal("cr_fresh", view.ConnectionReferenceLogicalName);
    }

    [Fact]
    public void TryWriteConnectionsCache_CurrentGeneration_WritesAndAdvancesGeneration()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        factory.Create(Workspace);

        var generation = synchronizer.GetConnectionsCacheGeneration(Workspace);
        var views = new[]
        {
            new AgentConnectionView { ConnectionReferenceLogicalName = "cr_office365", BoundConnectionExists = true },
        };

        var wrote = synchronizer.TryWriteConnectionsCache(Workspace, views, generation);

        Assert.True(wrote);
        Assert.NotEqual(generation, synchronizer.GetConnectionsCacheGeneration(Workspace));
        var cache = synchronizer.ReadConnectionsCache(Workspace);
        Assert.NotNull(cache);
        Assert.Equal("cr_office365", Assert.Single(cache!.Connections).ConnectionReferenceLogicalName);
    }

    [Fact]
    public async Task SetWorkflowActivationsAsync_SingleUnknownWorkflow_FailsWithoutCallingDataverse()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        factory.Create(Workspace);
        var dataverse = new Mock<ISyncDataverseClient>();

        var result = await synchronizer.SetWorkflowActivationsAsync(
            Workspace,
            new[] { new WorkflowActivationRequest { WorkflowId = Guid.NewGuid(), Activate = true } },
            dataverse.Object,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("not found", result.Message);
        dataverse.Verify(
            c => c.SetWorkflowStateAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SetWorkflowActivationsAsync_EmptyRequests_NoDataverseCallAndSucceeds()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        factory.Create(Workspace);
        var dataverse = new Mock<ISyncDataverseClient>();

        var result = await synchronizer.SetWorkflowActivationsAsync(
            Workspace,
            System.Array.Empty<WorkflowActivationRequest>(),
            dataverse.Object,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        dataverse.Verify(
            c => c.SetWorkflowStateAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SetWorkflowActivationsAsync_UnknownWorkflows_SkipsAllAndReportsFailureWithoutDataverse()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        factory.Create(Workspace);
        var dataverse = new Mock<ISyncDataverseClient>();

        var requests = new[]
        {
            new WorkflowActivationRequest { WorkflowId = Guid.NewGuid(), Activate = true },
            new WorkflowActivationRequest { WorkflowId = Guid.NewGuid(), Activate = true },
        };

        var result = await synchronizer.SetWorkflowActivationsAsync(
            Workspace,
            requests,
            dataverse.Object,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("not found", result.Message);
        dataverse.Verify(
            c => c.SetWorkflowStateAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SetWorkflowActivationsAsync_EnableReturnsConnectionAuthorizationError_KeepsDraftAndReportsFailure()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var accessor = (InMemoryFileAccessor)factory.Create(Workspace);
        var workflowId = Guid.NewGuid();
        Write(
            accessor,
            "workflows/notify/metadata.yml",
            $"name: Notify Flow\nworkflowId: {workflowId}\nstateCode: 0\nstatusCode: 1\n");

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(c => c.SetWorkflowStateAsync(workflowId, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Dataverse request failed (400): {\"error\":{\"code\":\"0x80060467\",\"message\":\"ConnectionAuthorizationFailed: connection cannot be used to activate this flow\"}}"));

        var result = await synchronizer.SetWorkflowActivationsAsync(
            Workspace,
            new[] { new WorkflowActivationRequest { WorkflowId = workflowId, Activate = true } },
            dataverse.Object,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrEmpty(result.Message));
        Assert.Contains("draft", result.Message!, StringComparison.OrdinalIgnoreCase);
        var workflow = Assert.Single(result.Workflows);
        Assert.Equal(workflowId.ToString(), workflow.WorkflowId);
        Assert.Equal(WorkflowState.Draft, workflow.State);
    }

    [Fact]
    public async Task RemoveConnectionReferenceAsync_WithUsages_Unconfirmed_ReturnsBlockingUsages()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var accessor = (InMemoryFileAccessor)factory.Create(Workspace);
        Write(accessor, "actions/sendmail.mcs.yml", "kind: TaskDialog\nconnectionReference: cr_office365\n");

        var result = await synchronizer.RemoveConnectionReferenceAsync(
            Workspace, new BotDefinition(), "cr_office365", confirmed: false, CancellationToken.None);

        Assert.False(result.Removed);
        var usage = Assert.Single(result.Usages);
        Assert.Equal(UsageKind.Action, usage.Kind);
        Assert.Equal("actions/sendmail.mcs.yml", usage.FilePath);
    }

    [Fact]
    public async Task RemoveConnectionReferenceAsync_Declared_RemovesFromLocalFile()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var accessor = (InMemoryFileAccessor)factory.Create(Workspace);
        WriteClassicConnectionReferences(
            accessor,
            ("cree9_agent.shared_office365.keep", "/providers/Microsoft.PowerApps/apis/shared_office365"),
            ("cree9_agent.shared_teams.drop", "/providers/Microsoft.PowerApps/apis/shared_teams"));

        var result = await synchronizer.RemoveConnectionReferenceAsync(
            Workspace, new BotDefinition(), "cree9_agent.shared_teams.drop", confirmed: true, CancellationToken.None);

        Assert.True(result.Removed);
        Assert.False(accessor.Exists(new AgentFilePath(".mcs/botdefinition.json")));
        using var stream = accessor.OpenRead(new AgentFilePath("connectionreferences.mcs.yml"));
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        Assert.Contains("cree9_agent.shared_office365.keep", content);
        Assert.DoesNotContain("cree9_agent.shared_teams.drop", content);
    }

    [Fact]
    public async Task RemoveConnectionReferenceAsync_NoDeclaredReferences_DoesNotRemove()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        factory.Create(Workspace);

        var result = await synchronizer.RemoveConnectionReferenceAsync(
            Workspace, new BotDefinition(), "cr_unused", confirmed: true, CancellationToken.None);

        Assert.False(result.Removed);
    }

    [Fact]
    public async Task DeclareConnectionReferencesAsync_NameWithoutConnectorSegment_ReturnsInvalidAndDoesNotProvision()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        factory.Create(Workspace);
        var dataverse = new Mock<ISyncDataverseClient>();

        var result = await synchronizer.DeclareConnectionReferencesAsync(
            Workspace,
            new BotDefinition(),
            new[] { "cre98_AgentB4CC.mail.abc123" },
            dataverse.Object,
            CancellationToken.None);

        Assert.Empty(result.Declared);
        var invalid = Assert.Single(result.Invalid);
        Assert.Equal("cre98_AgentB4CC.mail.abc123", invalid);
        dataverse.Verify(
            c => c.EnsureConnectionReferenceExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<Guid?>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateConnectionReferenceForConnectorAsync_MintsLogicalNameAndProvisions()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        factory.Create(Workspace);
        var dataverse = new Mock<ISyncDataverseClient>();

        var logicalName = await synchronizer.CreateConnectionReferenceForConnectorAsync(
            Workspace,
            new BotDefinition(),
            "shared_office365",
            dataverse.Object,
            CancellationToken.None);

        Assert.Contains(".shared_office365.", logicalName);
        dataverse.Verify(
            c => c.EnsureConnectionReferenceExistsAsync(
                logicalName,
                "/providers/Microsoft.PowerApps/apis/shared_office365",
                It.IsAny<CancellationToken>(),
                It.IsAny<Guid?>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateConnectionReferenceForConnectorAsync_ReusesPrefixFromExistingReference()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var accessor = (InMemoryFileAccessor)factory.Create(Workspace);
        WriteClassicConnectionReferences(
            accessor,
            ("cree9_agent.shared_msnweather.abc", "/providers/Microsoft.PowerApps/apis/shared_msnweather"));
        var dataverse = new Mock<ISyncDataverseClient>();

        var logicalName = await synchronizer.CreateConnectionReferenceForConnectorAsync(
            Workspace,
            new BotDefinition(),
            "shared_sharepointonline",
            dataverse.Object,
            CancellationToken.None);

        Assert.StartsWith("cree9_agent.shared_sharepointonline.", logicalName);
    }

    [Fact]
    public async Task GetAgentConnectionViewsAsync_SourcesDeclaredReferencesFromDisk_NotStaleDefinition()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var accessor = (InMemoryFileAccessor)factory.Create(Workspace);

        WriteClassicConnectionReferences(
            accessor,
            ("cree9_agent.shared_office365.kept", "/providers/Microsoft.PowerApps/apis/shared_office365"));

        var staleDefinition = new BotDefinition().WithConnectionReferences(new List<ConnectionReference>
        {
            new ConnectionReference.Builder
            {
                ConnectionReferenceLogicalName = "cree9_agent.shared_office365.kept",
                ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_office365",
            }.Build(),
            new ConnectionReference.Builder
            {
                ConnectionReferenceLogicalName = "cree9_agent.shared_office365.removed",
                ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_office365",
            }.Build(),
        });

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(c => c.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SyncDataverseClient.ConnectionReferenceInfo>());

        var catalog = new Mock<IConnectionCatalogClient>();
        var context = new PowerAppsContext { AccessToken = string.Empty, EnvironmentId = "env" };

        var views = await synchronizer.GetAgentConnectionViewsAsync(
            Workspace, staleDefinition, dataverse.Object, catalog.Object, context, CancellationToken.None);

        var view = Assert.Single(views);
        Assert.Equal("cree9_agent.shared_office365.kept", view.ConnectionReferenceLogicalName);
    }

    [Fact]
    public async Task GetAgentConnectionViewsAsync_WorkflowOnlyReference_ProducesUndeclaredRow()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var accessor = (InMemoryFileAccessor)factory.Create(Workspace);

        Write(
            accessor,
            "workflows/notify/metadata.yml",
            "name: Notify Flow\nworkflowId: 33333333-3333-3333-3333-333333333333\nstateCode: 0\nstatusCode: 1\nconnectionReferences:\n  - cree9_agent.shared_office365.wkonly\n");

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(c => c.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SyncDataverseClient.ConnectionReferenceInfo>());

        var catalog = new Mock<IConnectionCatalogClient>();
        var context = new PowerAppsContext { AccessToken = string.Empty, EnvironmentId = "env" };

        var views = await synchronizer.GetAgentConnectionViewsAsync(
            Workspace, new BotDefinition(), dataverse.Object, catalog.Object, context, CancellationToken.None);

        var view = Assert.Single(views);
        Assert.Equal("cree9_agent.shared_office365.wkonly", view.ConnectionReferenceLogicalName);
        Assert.Equal("shared_office365", view.ConnectorName);
        Assert.False(view.IsDeclared);
    }

    [Fact]
    public async Task ApplyConnectionBindingsAsync_EnvironmentSpecificCustomConnector_ResolvesConnectorBeforeBind()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var accessor = (InMemoryFileAccessor)factory.Create(Workspace);

        const string logicalName = "cree9_agent.shared_weather-20agent-5f1234567890abcdef.someid";
        const string staleConnectorId = "/providers/Microsoft.PowerApps/apis/shared_weather-20agent-5f1234567890abcdef";
        const string resolvedConnectorId = "/providers/Microsoft.PowerApps/apis/shared_weather-20agent-5fabcdef0123456789";

        WriteClassicConnectionReferences(accessor, (logicalName, staleConnectorId));

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(c => c.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SyncDataverseClient.ConnectionReferenceInfo>());
        dataverse
            .Setup(c => c.GetConnectorsByInternalIdPrefixAsync("shared_weather-20agent-5f", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new CustomConnectorMetadata { ConnectorInternalId = "shared_weather-20agent-5fabcdef0123456789", ModifiedOn = new DateTime(2024, 6, 1) },
            });

        var catalog = new Mock<IConnectionCatalogClient>();
        var context = new PowerAppsContext { AccessToken = string.Empty, EnvironmentId = "env" };

        var bindings = new[]
        {
            new ConnectionBindingRequest { ConnectionReferenceLogicalName = logicalName, ConnectionId = "conn-7eee" },
        };

        await synchronizer.ApplyConnectionBindingsAsync(
            Workspace, new BotDefinition(), dataverse.Object, catalog.Object, context, bindings, CancellationToken.None);

        dataverse.Verify(
            c => c.EnsureConnectionReferenceExistsAsync(logicalName, resolvedConnectorId, It.IsAny<CancellationToken>(), It.IsAny<Guid?>()),
            Times.Once);
        dataverse.Verify(
            c => c.BindConnectionReferenceAsync(logicalName, "conn-7eee", It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task ApplyConnectionBindingsAsync_StandardConnectorDeclaredLocallyButMissingInDataverse_EnsuresExistsBeforeBind()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var accessor = (InMemoryFileAccessor)factory.Create(Workspace);

        const string logicalName = "cre98_agentc10conn.shared_office365users.06aae3a7bb9d4d1c82ddd1f7220f754b";
        const string connectorId = "/providers/Microsoft.PowerApps/apis/shared_office365users";

        // The reference is declared on disk but does not yet exist in Dataverse (e.g. after a reattach where it was
        // filtered out of provisioning). Binding must create it rather than fail with 'not found in Dataverse'.
        WriteClassicConnectionReferences(accessor, (logicalName, connectorId));

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(c => c.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SyncDataverseClient.ConnectionReferenceInfo>());

        var catalog = new Mock<IConnectionCatalogClient>();
        var context = new PowerAppsContext { AccessToken = string.Empty, EnvironmentId = "env" };

        var bindings = new[]
        {
            new ConnectionBindingRequest { ConnectionReferenceLogicalName = logicalName, ConnectionId = "user@contoso.com" },
        };

        await synchronizer.ApplyConnectionBindingsAsync(
            Workspace, new BotDefinition(), dataverse.Object, catalog.Object, context, bindings, CancellationToken.None);

        dataverse.Verify(
            c => c.EnsureConnectionReferenceExistsAsync(logicalName, connectorId, It.IsAny<CancellationToken>(), It.IsAny<Guid?>()),
            Times.Once);
        dataverse.Verify(
            c => c.BindConnectionReferenceAsync(logicalName, "user@contoso.com", It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task GetAgentConnectionViewsAsync_OneConnectorListFails_OtherConnectorsStillListed()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var accessor = (InMemoryFileAccessor)factory.Create(Workspace);

        WriteClassicConnectionReferences(
            accessor,
            ("cree9_agent.shared_bogus.x", "/providers/Microsoft.PowerApps/apis/shared_bogus"),
            ("cree9_agent.shared_teams.y", "/providers/Microsoft.PowerApps/apis/shared_teams"));

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(c => c.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SyncDataverseClient.ConnectionReferenceInfo>());

        var catalog = new Mock<IConnectionCatalogClient>();
        catalog
            .Setup(c => c.ListConnectionsAsync(It.IsAny<PowerAppsContext>(), "shared_bogus", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connections request failed (404): ApiResourceNotFound"));
        catalog
            .Setup(c => c.ListConnectionsAsync(It.IsAny<PowerAppsContext>(), "shared_teams", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ConnectionInstance { Name = "conn-1", DisplayName = "Teams", Status = "Connected" } });

        var context = new PowerAppsContext { AccessToken = "token", EnvironmentId = "env" };

        var views = await synchronizer.GetAgentConnectionViewsAsync(
            Workspace, new BotDefinition(), dataverse.Object, catalog.Object, context, CancellationToken.None);

        Assert.Equal(2, views.Count);
        var bogus = Assert.Single(views, v => v.ConnectorName == "shared_bogus");
        Assert.Empty(bogus.Candidates);
        var teams = Assert.Single(views, v => v.ConnectorName == "shared_teams");
        Assert.Single(teams.Candidates);
    }

    private static Mock<ISyncDataverseClient> DataverseWithBoundReference(string logicalName, string connectionId)
    {
        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(c => c.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new SyncDataverseClient.ConnectionReferenceInfo
                {
                    ConnectionReferenceLogicalName = logicalName,
                    ConnectionId = connectionId,
                },
            });
        return dataverse;
    }

    [Fact]
    public async Task GetAgentConnectionViewsAsync_NoCatalogToken_PreservesBoundState()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var accessor = (InMemoryFileAccessor)factory.Create(Workspace);
        WriteClassicConnectionReferences(
            accessor,
            ("cree9_agent.shared_office365.x", "/providers/Microsoft.PowerApps/apis/shared_office365"));

        var dataverse = DataverseWithBoundReference("cree9_agent.shared_office365.x", "conn-abc");
        var catalog = new Mock<IConnectionCatalogClient>();
        var context = new PowerAppsContext { AccessToken = string.Empty, EnvironmentId = "env" };

        var views = await synchronizer.GetAgentConnectionViewsAsync(
            Workspace, new BotDefinition(), dataverse.Object, catalog.Object, context, CancellationToken.None);

        var view = Assert.Single(views);
        Assert.Equal("conn-abc", view.BoundConnectionId);
        Assert.True(view.BoundConnectionExists);
        catalog.Verify(
            c => c.ListConnectionsAsync(It.IsAny<PowerAppsContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetAgentConnectionViewsAsync_ConnectorListFails_PreservesBoundState()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var accessor = (InMemoryFileAccessor)factory.Create(Workspace);
        WriteClassicConnectionReferences(
            accessor,
            ("cree9_agent.shared_office365.x", "/providers/Microsoft.PowerApps/apis/shared_office365"));

        var dataverse = DataverseWithBoundReference("cree9_agent.shared_office365.x", "conn-abc");
        var catalog = new Mock<IConnectionCatalogClient>();
        catalog
            .Setup(c => c.ListConnectionsAsync(It.IsAny<PowerAppsContext>(), "shared_office365", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connections request failed (503)"));
        var context = new PowerAppsContext { AccessToken = "token", EnvironmentId = "env" };

        var views = await synchronizer.GetAgentConnectionViewsAsync(
            Workspace, new BotDefinition(), dataverse.Object, catalog.Object, context, CancellationToken.None);

        var view = Assert.Single(views);
        Assert.True(view.BoundConnectionExists);
        Assert.Empty(view.Candidates);
    }

    [Fact]
    public async Task GetAgentConnectionViewsAsync_CatalogMissingBoundConnection_MarksUnbound()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var accessor = (InMemoryFileAccessor)factory.Create(Workspace);
        WriteClassicConnectionReferences(
            accessor,
            ("cree9_agent.shared_office365.x", "/providers/Microsoft.PowerApps/apis/shared_office365"));

        var dataverse = DataverseWithBoundReference("cree9_agent.shared_office365.x", "conn-abc");
        var catalog = new Mock<IConnectionCatalogClient>();
        catalog
            .Setup(c => c.ListConnectionsAsync(It.IsAny<PowerAppsContext>(), "shared_office365", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ConnectionInstance { Name = "some-other-conn", Status = "Connected" } });
        var context = new PowerAppsContext { AccessToken = "token", EnvironmentId = "env" };

        var views = await synchronizer.GetAgentConnectionViewsAsync(
            Workspace, new BotDefinition(), dataverse.Object, catalog.Object, context, CancellationToken.None);

        var view = Assert.Single(views);
        Assert.False(view.BoundConnectionExists);
    }

    [Fact]
    public async Task GetAgentConnectionViewsAsync_CatalogContainsBoundConnection_MarksBound()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var accessor = (InMemoryFileAccessor)factory.Create(Workspace);
        WriteClassicConnectionReferences(
            accessor,
            ("cree9_agent.shared_office365.x", "/providers/Microsoft.PowerApps/apis/shared_office365"));

        var dataverse = DataverseWithBoundReference("cree9_agent.shared_office365.x", "conn-abc");
        var catalog = new Mock<IConnectionCatalogClient>();
        catalog
            .Setup(c => c.ListConnectionsAsync(It.IsAny<PowerAppsContext>(), "shared_office365", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ConnectionInstance { Name = "conn-abc", Status = "Connected" } });
        var context = new PowerAppsContext { AccessToken = "token", EnvironmentId = "env" };

        var views = await synchronizer.GetAgentConnectionViewsAsync(
            Workspace, new BotDefinition(), dataverse.Object, catalog.Object, context, CancellationToken.None);

        var view = Assert.Single(views);
        Assert.True(view.BoundConnectionExists);
    }

    [Fact]
    public async Task GetAgentConnectionViewsAsync_ConnectorListFails_MarksCatalogUnavailable()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var accessor = (InMemoryFileAccessor)factory.Create(Workspace);
        WriteClassicConnectionReferences(
            accessor,
            ("cree9_agent.shared_office365.x", "/providers/Microsoft.PowerApps/apis/shared_office365"));

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(c => c.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SyncDataverseClient.ConnectionReferenceInfo>());
        var catalog = new Mock<IConnectionCatalogClient>();
        catalog
            .Setup(c => c.ListConnectionsAsync(It.IsAny<PowerAppsContext>(), "shared_office365", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Connections request timed out after 30s."));
        var context = new PowerAppsContext { AccessToken = "token", EnvironmentId = "env" };

        var views = await synchronizer.GetAgentConnectionViewsAsync(
            Workspace, new BotDefinition(), dataverse.Object, catalog.Object, context, CancellationToken.None);

        var view = Assert.Single(views);
        Assert.True(view.CatalogUnavailable);
        Assert.Empty(view.Candidates);
    }

    [Fact]
    public async Task GetAgentConnectionViewsAsync_ConnectorListSucceedsEmpty_DoesNotMarkCatalogUnavailable()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var accessor = (InMemoryFileAccessor)factory.Create(Workspace);
        WriteClassicConnectionReferences(
            accessor,
            ("cree9_agent.shared_office365.x", "/providers/Microsoft.PowerApps/apis/shared_office365"));

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(c => c.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SyncDataverseClient.ConnectionReferenceInfo>());
        var catalog = new Mock<IConnectionCatalogClient>();
        catalog
            .Setup(c => c.ListConnectionsAsync(It.IsAny<PowerAppsContext>(), "shared_office365", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ConnectionInstance>());
        var context = new PowerAppsContext { AccessToken = "token", EnvironmentId = "env" };

        var views = await synchronizer.GetAgentConnectionViewsAsync(
            Workspace, new BotDefinition(), dataverse.Object, catalog.Object, context, CancellationToken.None);

        var view = Assert.Single(views);
        Assert.False(view.CatalogUnavailable);
        Assert.Empty(view.Candidates);
    }

    [Fact]
    public async Task GetAgentConnectionViewsAsync_NoCatalogToken_DoesNotMarkCatalogUnavailable()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var accessor = (InMemoryFileAccessor)factory.Create(Workspace);
        WriteClassicConnectionReferences(
            accessor,
            ("cree9_agent.shared_office365.x", "/providers/Microsoft.PowerApps/apis/shared_office365"));

        var dataverse = DataverseWithBoundReference("cree9_agent.shared_office365.x", "conn-abc");
        var catalog = new Mock<IConnectionCatalogClient>();
        var context = new PowerAppsContext { AccessToken = string.Empty, EnvironmentId = "env" };

        var views = await synchronizer.GetAgentConnectionViewsAsync(
            Workspace, new BotDefinition(), dataverse.Object, catalog.Object, context, CancellationToken.None);

        var view = Assert.Single(views);
        Assert.False(view.CatalogUnavailable);
    }
}

