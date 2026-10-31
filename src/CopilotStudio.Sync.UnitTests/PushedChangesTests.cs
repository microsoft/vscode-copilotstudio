// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using Microsoft.CopilotStudio.McsCore;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class PushedChangesTests
{
    private static readonly Guid ConnectorId = Guid.Parse("2fcc84f9-b852-496a-8b53-f571ccbf74c0");
    private static readonly Guid WorkflowId = Guid.Parse("3fcc84f9-b852-496a-8b53-f571ccbf74c0");
    private static readonly Guid PromptId = Guid.Parse("4fcc84f9-b852-496a-8b53-f571ccbf74c0");

    private static readonly ImmutableArray<Change> Changes = ImmutableArray.Create(
        CreateChange($"{CustomConnectorLayout.SchemaNamePrefix}{ConnectorId}"),
        CreateChange($"{CustomConnectorLayout.SchemaNamePrefix}{ConnectorId}.openapidefinition"),
        CreateChange($"{WorkflowLayout.SchemaNamePrefix}{WorkflowId}"),
        CreateChange($"{WorkflowLayout.SchemaNamePrefix}{WorkflowId}.metadata"),
        CreateChange($"{AiPromptLayout.SchemaNamePrefix}{PromptId}"),
        CreateChange($"{AiPromptLayout.SchemaNamePrefix}{PromptId}.metadata"),
        CreateChange("cr1_agent.topic.Greeting"));

    [Fact]
    public void WithoutFailedAssets_DropsEveryFileOfTheConnectorsWorkflowsAndPromptsThePushDidNotSave()
    {
        var pushed = Changes.WithoutFailedAssets(
            new CustomConnectorPushResult { Failed = new[] { CreateChange($"{CustomConnectorLayout.SchemaNamePrefix}{ConnectorId}") } },
            ImmutableArray.Create(new WorkflowResponse { WorkflowId = WorkflowId, ErrorMessage = "the flow definition is invalid" }),
            ImmutableArray.Create(new AIPromptResponse { AIModelId = PromptId, ErrorMessage = "invalid model" }));

        Assert.Equal("cr1_agent.topic.Greeting", Assert.Single(pushed).SchemaName);
    }

    [Fact]
    public void WithoutFailedAssets_KeepsEveryChangeTheEnvironmentAccepted()
    {
        var pushed = Changes.WithoutFailedAssets(
            new CustomConnectorPushResult(),
            ImmutableArray.Create(new WorkflowResponse { WorkflowId = WorkflowId }),
            ImmutableArray.Create(new AIPromptResponse { AIModelId = PromptId, ErrorMessage = string.Empty }));

        Assert.Equal(Changes, pushed);
    }

    private static Change CreateChange(string schemaName) => new() { Name = schemaName, SchemaName = schemaName, ChangeType = ChangeType.Update };
}
