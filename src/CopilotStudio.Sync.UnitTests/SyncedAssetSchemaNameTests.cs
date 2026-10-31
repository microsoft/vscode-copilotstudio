// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class SyncedAssetSchemaNameTests
{
    [Theory]
    [InlineData("Mcs.Connector.2fcc84f9-b852-496a-8b53-f571ccbf74c0.openapidefinition", "Mcs.Connector.2fcc84f9-b852-496a-8b53-f571ccbf74c0")]
    [InlineData("Mcs.Connector.2fcc84f9-b852-496a-8b53-f571ccbf74c0.iconblob", "Mcs.Connector.2fcc84f9-b852-496a-8b53-f571ccbf74c0")]
    [InlineData("Mcs.AIPrompt.2fcc84f9-b852-496a-8b53-f571ccbf74c0.metadata", "Mcs.AIPrompt.2fcc84f9-b852-496a-8b53-f571ccbf74c0")]
    [InlineData("Mcs.Workflow.2fcc84f9-b852-496a-8b53-f571ccbf74c0.metadata", "Mcs.Workflow.2fcc84f9-b852-496a-8b53-f571ccbf74c0")]
    public void GetAssetSchemaName_FileSchemaName_ReturnsTheOwningAssetSchemaName(string schemaName, string expectedAssetSchemaName)
    {
        Assert.Equal(expectedAssetSchemaName, SyncedAssetSchemaName.GetAssetSchemaName(schemaName));
    }

    [Theory]
    [InlineData("Mcs.Connector.2fcc84f9-b852-496a-8b53-f571ccbf74c0")]
    [InlineData("Mcs.Workflow.2fcc84f9-b852-496a-8b53-f571ccbf74c0")]
    [InlineData("cr1_agent.topic.Greeting")]
    [InlineData("Mcs.Connector.zzzzzzzz-zzzz-zzzz-zzzz-zzzzzzzzzzzz.metadata")]
    [InlineData("Mcs.Connector.2fcc84f9")]
    public void GetAssetSchemaName_NotAFileOfAnAsset_ReturnsTheSchemaNameUnchanged(string schemaName)
    {
        Assert.Equal(schemaName, SyncedAssetSchemaName.GetAssetSchemaName(schemaName));
    }
}
