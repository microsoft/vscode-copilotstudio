// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Xunit;
using static Microsoft.CopilotStudio.Sync.UnitTests.EnvironmentVariableTestFixtures;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class EnvironmentVariableDependenciesTests
{
    [Fact]
    public void GetReferencedSchemaNames_FindsMessageConditionAndConnectionReferences()
    {
        var conditionDialog = CodeSerializer.Deserialize<BotElement>(
            "kind: AdaptiveDialog\n" +
            "beginDialog:\n" +
            "  kind: OnRecognizedIntent\n" +
            "  id: main\n" +
            "  intent: {}\n" +
            "  actions:\n" +
            "    - kind: ConditionGroup\n" +
            "      id: conditionGroup\n" +
            "      conditions:\n" +
            "        - id: conditionItem\n" +
            "          condition: =Env.cr9f2_condition = \"on\"\n") as DialogBase;
        var conditionTopic = new DialogComponent(schemaName: $"{AgentSchemaName}.topic.Condition", displayName: "Condition", description: string.Empty, id: Guid.NewGuid(), parentBotComponentId: default, dialog: conditionDialog ?? throw new Xunit.Sdk.XunitException("Expected a dialog."));
        var knowledge = new KnowledgeSourceComponent(schemaName: $"{AgentSchemaName}.knowledge.Graph", id: Guid.NewGuid(), configuration: new KnowledgeSourceConfiguration(new GraphConnectorSearchSource(connectionId: new EnvironmentVariableReference("cr9f2_connection"))));

        var schemaNames = EnvironmentVariableDependencies.GetReferencedSchemaNames(new BotComponentBase[] { CreateTopic("Message", "{Env.cr9f2_message}"), conditionTopic, knowledge }, EnvironmentVariableDependencies.DefaultAnalyzer);

        Assert.Equal(new[] { "cr9f2_condition", "cr9f2_connection", "cr9f2_message" }, schemaNames.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void GetReferencedSchemaNames_IgnoresCaseAndComponentsWithoutReferences()
    {
        var schemaNames = EnvironmentVariableDependencies.GetReferencedSchemaNames(new BotComponentBase[] { CreateTopic("Upper", "{Env.CR9F2_SHARED}"), CreateTopic("Lower", SharedActivity), CreateTopic("None", UnreferencedActivity) }, EnvironmentVariableDependencies.DefaultAnalyzer);

        Assert.Single(schemaNames);
        Assert.Contains(SharedSchemaName, schemaNames);
    }

    [Fact]
    public void GetReferencingComponents_ListsEachComponentOncePerReferencedName()
    {
        var first = CreateTopic("First", SharedActivity + " " + SharedActivity);
        var second = CreateTopic("Second", "{Env.CR9F2_SHARED} {Env.cr9f2_other}");

        var referencingComponents = EnvironmentVariableDependencies.GetReferencingComponents(new BotComponentBase[] { first, second, CreateTopic("None", UnreferencedActivity) }, EnvironmentVariableDependencies.DefaultAnalyzer);

        Assert.Equal(2, referencingComponents.Count);
        Assert.Equal(new[] { first.SchemaNameString, second.SchemaNameString }, referencingComponents[SharedSchemaName].Select(component => component.SchemaNameString));
        Assert.Equal(new[] { second.SchemaNameString }, referencingComponents["cr9f2_other"].Select(component => component.SchemaNameString));
    }

    [Theory]
    [InlineData("cr123.owned", "cr123", true)]
    [InlineData("cr123.owned.nested", "cr123", true)]
    [InlineData("cr123", "cr123", true)]
    [InlineData("cr123_shared", "cr123", false)]
    [InlineData("cr1234.shared", "cr123", false)]
    [InlineData("cr12.shared", "cr123", false)]
    [InlineData("CR123.shared", "cr123", false)]
    [InlineData(".shared", "", false)]
    public void IsOwnedBy_ComparesTheTextBeforeTheFirstDotOrdinally(string schemaName, string rootSchemaName, bool expected)
    {
        Assert.Equal(expected, EnvironmentVariableOwnership.IsOwnedBy(schemaName, rootSchemaName));
    }

    [Fact]
    public void IsOwnedBy_UsesTheAgentOrCollectionSchemaName_AndOtherDefinitionsOwnNothing()
    {
        Assert.True(EnvironmentVariableOwnership.IsOwnedBy(OwnedSchemaName, new BotDefinition().WithEntity(CreateBotEntity())));
        Assert.False(EnvironmentVariableOwnership.IsOwnedBy(SharedSchemaName, new BotDefinition().WithEntity(CreateBotEntity())));
        Assert.True(EnvironmentVariableOwnership.IsOwnedBy("cr123_collection.owned", new BotComponentCollectionDefinition(componentCollection: new BotComponentCollection(schemaName: "cr123_collection"))));
        Assert.False(EnvironmentVariableOwnership.IsOwnedBy(OwnedSchemaName, new UnknownDefinitionBase(RecordDataValue.Empty)));
    }
}
