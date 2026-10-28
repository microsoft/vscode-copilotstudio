// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class StandaloneWorkflowCacheTests
{
    private const string Definition = """
    {"properties":{"connectionReferences":{"shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}}}}
    """;

    private const string OtherDefinition = """
    {"properties":{"definition":{"actions":{}}}}
    """;

    private static readonly AgentFilePath CachePath = new AgentFilePath(".mcs/botdefinition.json");

    private static readonly Encoding NoByteOrderMark = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static InMemoryFileAccessor NewWorkspace() => new InMemoryFileAccessor(new DirectoryPath("workspace"));

    private static SyncDataverseClient.WorkflowMetadata Row(Guid workflowId, string name, string definition) => new()
    {
        WorkflowId = workflowId,
        Name = name,
        ClientData = definition,
        StateCode = 0,
        StatusCode = 1,
    };

    private static void Write(IFileAccessor workspace, string path, string content)
    {
        using var stream = workspace.OpenWrite(new AgentFilePath(path));
        using var writer = new StreamWriter(stream, NoByteOrderMark);

        writer.Write(content);
    }

    [Fact]
    public void Read_WithNoCache_IsNull() => Assert.Null(StandaloneWorkflowCache.Read(NewWorkspace(), Guid.NewGuid()));

    [Fact]
    public void Flows_WithNoCache_IsEmpty() => Assert.Empty(StandaloneWorkflowCache.Flows(NewWorkspace()));

    [Fact]
    public void Write_StoresTheWorkflowInTheAgentCacheFile()
    {
        var workflowId = Guid.NewGuid();
        var workspace = NewWorkspace();

        StandaloneWorkflowCache.Write(workspace, Row(workflowId, "Flow", Definition));

        Assert.True(workspace.Exists(CachePath));
        Assert.False(workspace.Exists(new AgentFilePath($".mcs/workflows/{workflowId}/metadata.yml")));
        Assert.False(workspace.Exists(new AgentFilePath($".mcs/workflows/{workflowId}/workflow.json")));
    }

    [Fact]
    public void Read_AfterWrite_ReturnsWhatTheServiceReturned()
    {
        var workflowId = Guid.NewGuid();
        var workspace = NewWorkspace();

        StandaloneWorkflowCache.Write(workspace, Row(workflowId, "Flow", Definition));

        var cached = StandaloneWorkflowCache.Read(workspace, workflowId);

        Assert.NotNull(cached);
        Assert.Equal("Flow", cached.Name);
        Assert.Equal(Definition, cached.ClientData);
        Assert.Equal(workflowId, cached.WorkflowId);
    }

    [Fact]
    public void Read_ReturnsTheRawDefinitionNotAReformattedOne()
    {
        var workflowId = Guid.NewGuid();
        var workspace = NewWorkspace();

        StandaloneWorkflowCache.Write(workspace, Row(workflowId, "Flow", Definition));

        Assert.Equal(Definition, (StandaloneWorkflowCache.Read(workspace, workflowId) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).ClientData);
    }

    [Fact]
    public void Write_OfTheSameWorkflowTwice_ReportsNoSecondChange()
    {
        var workflowId = Guid.NewGuid();
        var workspace = NewWorkspace();

        Assert.True(StandaloneWorkflowCache.Write(workspace, Row(workflowId, "Flow", Definition)));
        Assert.False(StandaloneWorkflowCache.Write(workspace, Row(workflowId, "Flow", Definition)));
    }

    [Fact]
    public void Write_OfAnEditedWorkflow_ReportsAChange()
    {
        var workflowId = Guid.NewGuid();
        var workspace = NewWorkspace();

        StandaloneWorkflowCache.Write(workspace, Row(workflowId, "Flow", Definition));

        Assert.True(StandaloneWorkflowCache.Write(workspace, Row(workflowId, "Flow", OtherDefinition)));
        Assert.Equal(OtherDefinition, (StandaloneWorkflowCache.Read(workspace, workflowId) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).ClientData);
    }

    [Fact]
    public void Write_OfASecondWorkflow_KeepsTheFirst()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var workspace = NewWorkspace();

        StandaloneWorkflowCache.Write(workspace, Row(first, "First", Definition));
        StandaloneWorkflowCache.Write(workspace, Row(second, "Second", OtherDefinition));

        Assert.Equal(2, StandaloneWorkflowCache.Flows(workspace).Length);
        Assert.Equal("First", (StandaloneWorkflowCache.Read(workspace, first) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).Name);
        Assert.Equal("Second", (StandaloneWorkflowCache.Read(workspace, second) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).Name);
    }

    [Fact]
    public void Write_OfAnExistingWorkflow_KeepsItsPosition()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var workspace = NewWorkspace();

        StandaloneWorkflowCache.Write(workspace, Row(first, "First", Definition));
        StandaloneWorkflowCache.Write(workspace, Row(second, "Second", OtherDefinition));
        StandaloneWorkflowCache.Write(workspace, Row(first, "First Renamed", Definition));

        Assert.Equal(first, StandaloneWorkflowCache.Flows(workspace)[0].WorkflowId.Value);
        Assert.Equal(second, StandaloneWorkflowCache.Flows(workspace)[1].WorkflowId.Value);
    }

    [Fact]
    public void Remove_DropsOnlyTheNamedWorkflow()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var workspace = NewWorkspace();

        StandaloneWorkflowCache.Write(workspace, Row(first, "First", Definition));
        StandaloneWorkflowCache.Write(workspace, Row(second, "Second", OtherDefinition));

        Assert.True(StandaloneWorkflowCache.Remove(workspace, first));
        Assert.Null(StandaloneWorkflowCache.Read(workspace, first));
        Assert.NotNull(StandaloneWorkflowCache.Read(workspace, second));
    }

    [Fact]
    public void Remove_OfAnUnknownWorkflow_ReportsNoChange() => Assert.False(StandaloneWorkflowCache.Remove(NewWorkspace(), Guid.NewGuid()));

    [Fact]
    public void CachedFlows_CarryTheExtensionDataTheAgentCacheUses()
    {
        var workflowId = Guid.NewGuid();
        var workspace = NewWorkspace();

        StandaloneWorkflowCache.Write(workspace, Row(workflowId, "Flow", Definition));

        var flow = Assert.Single(StandaloneWorkflowCache.Flows(workspace));
        var metadata = WorkflowMetadataFile.ReadStrict(WorkspaceSynchronizer.GetWorkflowMetadata(flow));

        Assert.Equal(Definition, WorkspaceSynchronizer.GetRawClientData(flow));
        Assert.NotNull(metadata);
        Assert.Equal(workflowId, metadata.WorkflowId);
        Assert.Equal("Flow", metadata.Name);
    }

    [Fact]
    public void Write_PreservesAnAgentEntityAlreadyInTheCache()
    {
        var workflowId = Guid.NewGuid();
        var workspace = NewWorkspace();
        var entity = (CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cr1\ndisplayName: Agent") ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value."));

        WorkspaceSynchronizer.WriteCloudCache(workspace, new BotDefinition().WithEntity(entity));
        StandaloneWorkflowCache.Write(workspace, Row(workflowId, "Flow", Definition));

        var snapshot = Assert.IsType<BotDefinition>(WorkspaceSynchronizer.ReadCloudCacheSnapshot(workspace));

        Assert.NotNull(snapshot.Entity);
        Assert.Equal("Agent", snapshot.Entity.DisplayName);
        Assert.Single(snapshot.Flows);
    }

    [Fact]
    public void Write_IntoAnAgentCache_IsReadBackByTheSameAgentReader()
    {
        var workflowId = Guid.NewGuid();
        var workspace = NewWorkspace();

        WorkspaceSynchronizer.WriteCloudCache(workspace, new BotDefinition());
        StandaloneWorkflowCache.Write(workspace, Row(workflowId, "Flow", Definition));

        var snapshot = (WorkspaceSynchronizer.ReadCloudCacheSnapshot(workspace) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value."));
        var flow = Assert.Single(snapshot.Flows);

        Assert.Equal(workflowId, flow.WorkflowId.Value);
        Assert.Equal("Flow", flow.DisplayName);
    }

    [Fact]
    public void HasLocalChange_WithNoCachedCopy_IsTrue()
    {
        var workflowId = Guid.NewGuid();
        var workspace = NewWorkspace();
        var folder = WorkflowWorkspace.RelativeFolderFor("Flow", workflowId);

        Write(workspace, $"{folder}/workflow.json", Definition);
        Write(workspace, $"{folder}/metadata.yml", WorkflowMetadataFile.Write(Row(workflowId, "Flow", Definition)));

        Assert.True(StandaloneWorkflowCache.HasLocalChange(workspace, WorkflowWorkspace.FindAll(workspace).Single()));
    }

    [Fact]
    public void HasLocalChange_WithAMatchingCachedCopy_IsFalse()
    {
        var workflowId = Guid.NewGuid();
        var workspace = NewWorkspace();
        var folder = WorkflowWorkspace.RelativeFolderFor("Flow", workflowId);

        Write(workspace, $"{folder}/workflow.json", Definition);
        Write(workspace, $"{folder}/metadata.yml", WorkflowMetadataFile.Write(Row(workflowId, "Flow", Definition)));
        StandaloneWorkflowCache.Write(workspace, Row(workflowId, "Flow", Definition));

        Assert.False(StandaloneWorkflowCache.HasLocalChange(workspace, WorkflowWorkspace.FindAll(workspace).Single()));
    }

    [Fact]
    public void HasLocalChange_AfterAMetadataFieldOutsideNameStateAndDescriptionIsEdited_IsTrue()
    {
        var workflowId = Guid.NewGuid();
        var workspace = NewWorkspace();
        var folder = WorkflowWorkspace.RelativeFolderFor("Flow", workflowId);
        var cached = Row(workflowId, "Flow", Definition);
        var edited = Row(workflowId, "Flow", Definition);

        edited.Scope = 4;

        Write(workspace, $"{folder}/workflow.json", Definition);
        Write(workspace, $"{folder}/metadata.yml", WorkflowMetadataFile.Write(edited));
        StandaloneWorkflowCache.Write(workspace, cached);

        Assert.True(StandaloneWorkflowCache.HasLocalChange(workspace, WorkflowWorkspace.FindAll(workspace).Single()));
    }

    [Fact]
    public void HasLocalChange_AfterTheIntroducedVersionIsEdited_IsTrue()
    {
        var workflowId = Guid.NewGuid();
        var workspace = NewWorkspace();
        var folder = WorkflowWorkspace.RelativeFolderFor("Flow", workflowId);
        var edited = Row(workflowId, "Flow", Definition);

        edited.IntroducedVersion = "2.0.0.0";

        Write(workspace, $"{folder}/workflow.json", Definition);
        Write(workspace, $"{folder}/metadata.yml", WorkflowMetadataFile.Write(edited));
        StandaloneWorkflowCache.Write(workspace, Row(workflowId, "Flow", Definition));

        Assert.True(StandaloneWorkflowCache.HasLocalChange(workspace, WorkflowWorkspace.FindAll(workspace).Single()));
    }

    [Fact]
    public void HasLocalChange_WhenOnlyTheDefinitionFormattingDiffers_IsFalse()
    {
        var workflowId = Guid.NewGuid();
        var workspace = NewWorkspace();
        var folder = WorkflowWorkspace.RelativeFolderFor("Flow", workflowId);

        Write(workspace, $"{folder}/workflow.json", WorkflowDefinitionFile.Format(Definition));
        Write(workspace, $"{folder}/metadata.yml", WorkflowMetadataFile.Write(Row(workflowId, "Flow", Definition)));
        StandaloneWorkflowCache.Write(workspace, Row(workflowId, "Flow", Definition));

        Assert.False(StandaloneWorkflowCache.HasLocalChange(workspace, WorkflowWorkspace.FindAll(workspace).Single()));
    }

    [Fact]
    public void HasLocalChange_AfterTheDefinitionIsEdited_IsTrue()    {
        var workflowId = Guid.NewGuid();
        var workspace = NewWorkspace();
        var folder = WorkflowWorkspace.RelativeFolderFor("Flow", workflowId);

        Write(workspace, $"{folder}/workflow.json", Definition);
        Write(workspace, $"{folder}/metadata.yml", WorkflowMetadataFile.Write(Row(workflowId, "Flow", Definition)));
        StandaloneWorkflowCache.Write(workspace, Row(workflowId, "Flow", Definition));
        Write(workspace, $"{folder}/workflow.json", OtherDefinition);

        Assert.True(StandaloneWorkflowCache.HasLocalChange(workspace, WorkflowWorkspace.FindAll(workspace).Single()));
    }

    [Fact]
    // ! Null is intentional to verify argument validation.
    public void Write_RejectsNothingToWrite() => Assert.Throws<ArgumentNullException>(() => StandaloneWorkflowCache.Write(NewWorkspace(), null!));

    [Fact]
    // ! Null is intentional to verify argument validation.
    public void Flows_RejectsNoWorkspace() => Assert.Throws<ArgumentNullException>(() => StandaloneWorkflowCache.Flows(null!));
}
