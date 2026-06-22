// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class ConnectionReferenceUsageScannerTests
{
    private static InMemoryFileAccessor CreateAccessor()
        => new InMemoryFileAccessor(new DirectoryPath("c:/test/workspace/"));

    private static void Write(InMemoryFileAccessor accessor, string relativePath, string content)
    {
        using var stream = accessor.OpenWrite(new AgentFilePath(relativePath));
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    [Fact]
    public void Scan_ClassifiesActionAndTopicUsages()
    {
        var accessor = CreateAccessor();
        Write(accessor, "actions/sendmail.mcs.yml", "kind: TaskDialog\nconnectionReference: cr_office365\n");
        Write(accessor, "topics/greeting.mcs.yml", "kind: AdaptiveDialog\nconnectionReference: cr_office365\n");

        var scan = new ConnectionReferenceUsageScanner().Scan(
            accessor,
            connectorInternalIdByLogicalName: System.Collections.Immutable.ImmutableDictionary<string, string>.Empty,
            System.Threading.CancellationToken.None);

        var usages = scan.GetUsages("cr_office365");
        Assert.Equal(2, usages.Length);
        Assert.Contains(usages, u => u.Kind == UsageKind.Action && u.FilePath == "actions/sendmail.mcs.yml");
        Assert.Contains(usages, u => u.Kind == UsageKind.Topic && u.FilePath == "topics/greeting.mcs.yml");
        Assert.Contains("cr_office365", scan.AuthoredLogicalNames);
    }

    [Theory]
    [InlineData("kind: TaskDialog\nconnectionReference: 'cr_office365'\n")]
    [InlineData("kind: TaskDialog\nconnectionReference: \"cr_office365\"\n")]
    public void Scan_DetectsQuotedConnectionReferenceValues(string content)
    {
        var accessor = CreateAccessor();
        Write(accessor, "actions/sendmail.mcs.yml", content);

        var scan = new ConnectionReferenceUsageScanner().Scan(
            accessor,
            connectorInternalIdByLogicalName: System.Collections.Immutable.ImmutableDictionary<string, string>.Empty,
            System.Threading.CancellationToken.None);

        var usage = Assert.Single(scan.GetUsages("cr_office365"));
        Assert.Equal(UsageKind.Action, usage.Kind);
        Assert.Equal("actions/sendmail.mcs.yml", usage.FilePath);
        Assert.Contains("cr_office365", scan.AuthoredLogicalNames);
    }

    [Fact]
    public void Scan_IgnoresConnectionReferencesDeclarationFile()
    {
        var accessor = CreateAccessor();
        Write(accessor, "connectionreferences.mcs.yml", "connectionReference: cr_office365\n");

        var scan = new ConnectionReferenceUsageScanner().Scan(
            accessor,
            connectorInternalIdByLogicalName: System.Collections.Immutable.ImmutableDictionary<string, string>.Empty,
            System.Threading.CancellationToken.None);

        Assert.Empty(scan.GetUsages("cr_office365"));
        Assert.Empty(scan.AuthoredLogicalNames);
    }

    [Fact]
    public void Scan_ReadsWorkflowMetadataStateAndReferences()
    {
        var accessor = CreateAccessor();
        Write(
            accessor,
            "workflows/notify/metadata.yml",
            "name: Notify Flow\nworkflowId: 11111111-1111-1111-1111-111111111111\nstateCode: 1\nstatusCode: 2\nconnectionReferences:\n  - cr_office365\n  - cr_sharepoint\n");

        var scan = new ConnectionReferenceUsageScanner().Scan(
            accessor,
            connectorInternalIdByLogicalName: System.Collections.Immutable.ImmutableDictionary<string, string>.Empty,
            System.Threading.CancellationToken.None);

        var workflow = Assert.Single(scan.Workflows);
        Assert.Equal("Notify Flow", workflow.DisplayName);
        Assert.Equal("11111111-1111-1111-1111-111111111111", workflow.WorkflowId);
        Assert.Equal(WorkflowState.Activated, workflow.State);
        Assert.Equal(new[] { "cr_office365", "cr_sharepoint" }, workflow.ConnectionReferenceLogicalNames);

        var usages = scan.GetUsages("cr_office365");
        var usage = Assert.Single(usages);
        Assert.Equal(UsageKind.Workflow, usage.Kind);
        Assert.Equal("workflows/notify/metadata.yml", usage.FilePath);
        Assert.Equal("Notify Flow", usage.DisplayName);
    }

    [Fact]
    public void Scan_DraftWorkflowMapsToDraftState()
    {
        var accessor = CreateAccessor();
        Write(
            accessor,
            "workflows/draftflow/metadata.yml",
            "name: Draft Flow\nstateCode: 0\nstatusCode: 1\nconnectionReferences: []\n");

        var scan = new ConnectionReferenceUsageScanner().Scan(
            accessor,
            connectorInternalIdByLogicalName: System.Collections.Immutable.ImmutableDictionary<string, string>.Empty,
            System.Threading.CancellationToken.None);

        var workflow = Assert.Single(scan.Workflows);
        Assert.Equal(WorkflowState.Draft, workflow.State);
        Assert.Empty(workflow.ConnectionReferenceLogicalNames);
    }

    [Fact]
    public void Scan_MetadataMissingConnectionReferences_FallsBackToWorkflowJson()
    {
        var accessor = CreateAccessor();
        Write(
            accessor,
            "workflows/notify/metadata.yml",
            "name: Notify Flow\nworkflowId: 44444444-4444-4444-4444-444444444444\nstateCode: 0\nstatusCode: 1\n");
        Write(
            accessor,
            "workflows/notify/workflow.json",
            "{\"properties\":{\"connectionReferences\":{\"shared_office365\":{\"connection\":{\"connectionReferenceLogicalName\":\"cr_office365\"}}}}}");

        var scan = new ConnectionReferenceUsageScanner().Scan(
            accessor,
            connectorInternalIdByLogicalName: System.Collections.Immutable.ImmutableDictionary<string, string>.Empty,
            System.Threading.CancellationToken.None);

        var workflow = Assert.Single(scan.Workflows);
        Assert.Equal(new[] { "cr_office365" }, workflow.ConnectionReferenceLogicalNames);

        var usage = Assert.Single(scan.GetUsages("cr_office365"));
        Assert.Equal(UsageKind.Workflow, usage.Kind);
        Assert.Equal("workflows/notify/metadata.yml", usage.FilePath);
    }

    [Fact]
    public void Scan_MetadataHasConnectionReferences_DoesNotReadWorkflowJson()
    {
        var accessor = CreateAccessor();
        Write(
            accessor,
            "workflows/notify/metadata.yml",
            "name: Notify Flow\nstateCode: 0\nstatusCode: 1\nconnectionReferences:\n  - cr_frommetadata\n");
        Write(
            accessor,
            "workflows/notify/workflow.json",
            "{\"properties\":{\"connectionReferences\":{\"shared_office365\":{\"connection\":{\"connectionReferenceLogicalName\":\"cr_fromjson\"}}}}}");

        var scan = new ConnectionReferenceUsageScanner().Scan(
            accessor,
            connectorInternalIdByLogicalName: System.Collections.Immutable.ImmutableDictionary<string, string>.Empty,
            System.Threading.CancellationToken.None);

        var workflow = Assert.Single(scan.Workflows);
        Assert.Equal(new[] { "cr_frommetadata" }, workflow.ConnectionReferenceLogicalNames);
    }

    [Fact]
    public void Scan_MatchesCustomConnectorByInternalId()
    {
        var accessor = CreateAccessor();
        Write(
            accessor,
            "connectors/weather/metadata.yml",
            "{ \"connectorinternalid\": \"shared_weather-123\", \"displayname\": \"Weather\" }");
        var map = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty
            .Add("cr_weather", "shared_weather-123");

        var scan = new ConnectionReferenceUsageScanner().Scan(
            accessor,
            map,
            System.Threading.CancellationToken.None);

        var usages = scan.GetUsages("cr_weather");
        var usage = Assert.Single(usages);
        Assert.Equal(UsageKind.Connector, usage.Kind);
        Assert.Equal("connectors/weather/metadata.yml", usage.FilePath);
        Assert.Equal("Weather", usage.DisplayName);
    }
}
