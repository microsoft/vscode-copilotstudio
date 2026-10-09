// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync.Dataverse;
using System.Text.Json;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class StandaloneWorkflowFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcs-standalone-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string WriteWorkflow(string name, Guid workflowId, string definition)
    {
        var folder = Path.Combine(WorkflowWorkspace.WorkflowsDirectory(_root), WorkflowWorkspace.FolderNameFor(name, workflowId));

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, WorkflowWorkspace.DefinitionFileName), definition);
        File.WriteAllText(Path.Combine(folder, WorkflowWorkspace.MetadataFileName), WorkflowMetadataFile.Write(WorkflowScaffolder.NewMetadata(name, workflowId, WorkflowWorkspace.RelativeFolderFor(name, workflowId))));

        return folder;
    }

    private const string DefinitionWithReferences = """
    {
      "properties": {
        "connectionReferences": {
          "shared_teams": {
            "api": { "name": "shared_teams" },
            "connection": { "connectionReferenceLogicalName": "new_sharedteams_aa643948" }
          },
          "shared_msnweather": {
            "api": { "name": "shared_msnweather" },
            "connection": { "connectionReferenceLogicalName": "new_sharedmsnweather_2a0bcba7" }
          }
        }
      }
    }
    """;

    private const string DefinitionWithSqlReference = """
    {"properties":{"connectionReferences":{"shared_sql":{"api":{"name":"shared_sql"},"connection":{"connectionReferenceLogicalName":"new_sql_1b2c3d"}}}}}
    """;

    [Theory]
    [InlineData("WF Test 1", "WFTest1")]
    [InlineData("a/b:c", "abc")]
    [InlineData("trailing.  ", "trailing")]
    [InlineData(null, "")]
    public void FolderNameFor_StripsWhatAFolderNameCannotHold(string? name, string expectedPrefix)
    {
        var workflowId = Guid.NewGuid();

        Assert.Equal($"{expectedPrefix}-{workflowId}", WorkflowWorkspace.FolderNameFor(name, workflowId));
    }

    [Fact]
    public void FolderNameFor_StripsTheSameCharactersOnEveryPlatform()
    {
        var workflowId = Guid.NewGuid();

        Assert.Equal($"report-{workflowId}", WorkflowWorkspace.FolderNameFor("re\\p*o?r<t>|\":/", workflowId));
        Assert.Equal($"AB-{workflowId}", WorkflowWorkspace.FolderNameFor("A\u0001B\u007f", workflowId));
    }

    [Fact]
    public void PortableDefinition_LeavesAnActionPayloadThatOnlyLooksLikeADeclaration()
    {
        const string definition = """
        {"properties":{"definition":{"actions":{"Compose":{"inputs":{
          "connection":{"connectionReferenceLogicalName":"business-data"},
          "connectionName":"must-preserve"}}}}}}
        """;

        var portable = WorkflowDefinitionFile.PortableDefinition(definition);

        Assert.Contains("must-preserve", portable, StringComparison.Ordinal);
    }

    [Fact]
    public void PortableDefinition_StillDropsTheBindingOfANestedDeclaration()
    {
        const string definition = """
        {"properties":{"definition":{"triggers":{"manual":{"metadata":{"associatedData":{"graph":{"connectionReferences":{
          "shared_teams":{"connection":{"connectionReferenceLogicalName":"new_teams"},"connectionName":"shared-teams-13d033ce"}}}}}}}}}}
        """;

        var portable = WorkflowDefinitionFile.PortableDefinition(definition);

        Assert.DoesNotContain("shared-teams-13d033ce", portable, StringComparison.Ordinal);
        Assert.Contains("new_teams", portable, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteConnectionReferenceDeclaration_WhenADefinitionIsEmpty_LeavesTheDeclarationAlone()
    {
        WriteWorkflow("Flow", Guid.NewGuid(), DefinitionWithReferences);
        var path = Path.Combine(_root, WorkflowDefinitionFile.ConnectionReferencesFileName);

        WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(_root);
        var declared = File.ReadAllText(path);

        WriteWorkflow("Blank", Guid.NewGuid(), string.Empty);

        Assert.False(WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(_root));
        Assert.Equal(declared, File.ReadAllText(path), StringComparer.Ordinal);
    }

    [Fact]
    public void WriteConnectionReferenceDeclaration_WhenAWorkflowIsMissingItsMetadata_LeavesTheDeclarationAlone()
    {
        WriteWorkflow("Flow", Guid.NewGuid(), DefinitionWithReferences);
        var path = Path.Combine(_root, WorkflowDefinitionFile.ConnectionReferencesFileName);

        WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(_root);
        var declared = File.ReadAllText(path);

        foreach (var folder in WorkflowWorkspace.FindAll(_root))
        {
            File.Delete(folder.MetadataPath);
        }

        Assert.False(WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(_root));
        Assert.Equal(declared, File.ReadAllText(path), StringComparer.Ordinal);
    }

    [Fact]
    public void HasIncompleteWorkflow_IsFalseWhenEveryWorkflowCarriesBothFiles()
    {
        WriteWorkflow("Flow", Guid.NewGuid(), DefinitionWithReferences);

        Assert.False(WorkflowWorkspace.HasIncompleteWorkflow(_root));
    }

    [Theory]
    [InlineData("{\"properties\":{\"connectionReferences\":[]}}")]
    [InlineData("{\"properties\":{\"connectionReferences\":\"none\"}}")]
    [InlineData("{\"properties\":[]}")]
    [InlineData("[]")]
    [InlineData("\"a definition\"")]
    [InlineData("{\"properties\":{\"connectionReferences\":{\"shared_teams\":\"not an object\"}}}")]
    [InlineData("{\"properties\":{\"connectionReferences\":{\"shared_teams\":{\"connection\":\"not an object\"}}}}")]
    public void TryGetConnectionReferences_WhenTheShapeIsNotOneThisReads_ReportsFailure(string clientData)
    {
        Assert.False(WorkflowDefinitionFile.TryGetConnectionReferences(clientData, out var declared));
        Assert.Empty(declared);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"properties\":{}}")]
    [InlineData("{\"properties\":{\"connectionReferences\":{}}}")]
    [InlineData("{\"properties\":{\"connectionReferences\":{\"shared_teams\":{\"api\":{\"name\":\"shared_teams\"}}}}}")]
    public void TryGetConnectionReferences_WhenTheDefinitionSimplyDeclaresNothing_ReportsSuccess(string clientData)
    {
        Assert.True(WorkflowDefinitionFile.TryGetConnectionReferences(clientData, out var declared));
        Assert.Empty(declared);
    }

    [Fact]
    public void WriteConnectionReferenceDeclaration_WhenADefinitionDeclaresReferencesInAShapeThisCannotRead_KeepsTheDeclaration()
    {
        WriteWorkflow("Teams", Guid.NewGuid(), DefinitionWithReferences);
        WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(_root);

        var path = Path.Combine(_root, WorkflowDefinitionFile.ConnectionReferencesFileName);
        var before = File.ReadAllText(path);

        WriteWorkflow("Malformed", Guid.NewGuid(), "{\"properties\":{\"connectionReferences\":[]}}");

        Assert.False(WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(_root));
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void TryGetConnectionReferences_SeparatesAnEmptyDefinitionFromAnUnreadableOne()
    {
        Assert.True(WorkflowDefinitionFile.TryGetConnectionReferences("{}", out var none));
        Assert.Empty(none);

        Assert.False(WorkflowDefinitionFile.TryGetConnectionReferences("{ not json", out var unreadable));
        Assert.Empty(unreadable);

        Assert.False(WorkflowDefinitionFile.TryGetConnectionReferences("   ", out var blank));
        Assert.Empty(blank);
    }

    [Fact]
    public void WriteConnectionReferenceDeclaration_WhenTheOnlyDefinitionCannotBeRead_DoesNotDeleteTheDeclaration()
    {
        WriteWorkflow("Flow", Guid.NewGuid(), DefinitionWithReferences);
        var path = Path.Combine(_root, WorkflowDefinitionFile.ConnectionReferencesFileName);

        WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(_root);

        foreach (var folder in WorkflowWorkspace.FindAll(_root))
        {
            File.WriteAllText(folder.DefinitionPath, "{ not json");
        }

        Assert.False(WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(_root));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void WorkflowIdOf_ReadsTheIdentifierTheFolderNameEndsWith()
    {
        var workflowId = Guid.NewGuid();

        Assert.Equal(workflowId, WorkflowWorkspace.WorkflowIdOf(WorkflowWorkspace.FolderNameFor("Flow", workflowId)));
    }

    [Theory]
    [InlineData("no-identifier")]
    [InlineData("")]
    [InlineData(null)]
    public void WorkflowIdOf_WithoutATrailingIdentifier_IsNull(string? folderName) => Assert.Null(WorkflowWorkspace.WorkflowIdOf(folderName));

    [Fact]
    public void FindAll_ReturnsOnlyFoldersCarryingBothFiles()
    {
        var complete = Guid.NewGuid();
        WriteWorkflow("Complete", complete, "{}");

        var partial = Path.Combine(WorkflowWorkspace.WorkflowsDirectory(_root), WorkflowWorkspace.FolderNameFor("Partial", Guid.NewGuid()));
        Directory.CreateDirectory(partial);
        File.WriteAllText(Path.Combine(partial, WorkflowWorkspace.DefinitionFileName), "{}");

        Directory.CreateDirectory(Path.Combine(WorkflowWorkspace.WorkflowsDirectory(_root), "not-a-workflow"));

        var found = Assert.Single(WorkflowWorkspace.FindAll(_root));

        Assert.Equal(complete, found.WorkflowId);
        Assert.True(File.Exists(found.DefinitionPath));
        Assert.True(File.Exists(found.MetadataPath));
    }

    [Fact]
    public void FindAll_WhenThereIsNoWorkflowsFolder_IsEmpty() => Assert.Empty(WorkflowWorkspace.FindAll(_root));

    [Fact]
    public void IsStandaloneWorkspace_WithWorkflowsAndNoAgentProject_IsTrue()
    {
        WriteWorkflow("Flow", Guid.NewGuid(), "{}");

        Assert.True(WorkflowWorkspace.IsStandaloneWorkspace(_root));
    }

    [Theory]
    [InlineData("settings.mcs.yml")]
    [InlineData("agent.mcs.yml")]
    [InlineData("collection.mcs.yml")]
    public void IsStandaloneWorkspace_WhenAnAgentProjectIsPresent_IsFalse(string projectFileName)
    {
        WriteWorkflow("Flow", Guid.NewGuid(), "{}");
        File.WriteAllText(Path.Combine(_root, projectFileName), "displayName: Agent");

        Assert.False(WorkflowWorkspace.IsStandaloneWorkspace(_root));
    }

    [Fact]
    public void IsStandaloneWorkspace_WithoutWorkflows_IsFalse()
    {
        Directory.CreateDirectory(_root);

        Assert.False(WorkflowWorkspace.IsStandaloneWorkspace(_root));
    }

    [Fact]
    public void Format_IndentsTheDefinition() => Assert.Contains("\n", WorkflowDefinitionFile.Format("{\"a\":{\"b\":1}}"), StringComparison.Ordinal);

    [Fact]
    public void Format_LeavesUnparseableContentAlone() => Assert.Equal("not json", WorkflowDefinitionFile.Format("not json"));

    [Fact]
    public void Format_OfNothing_IsEmpty() => Assert.Equal(string.Empty, WorkflowDefinitionFile.Format(null));

    [Fact]
    public void ConnectionReferences_ReadsTheLogicalNameAndConnector()
    {
        var declared = WorkflowDefinitionFile.ConnectionReferences(DefinitionWithReferences);

        Assert.Equal(["new_sharedteams_aa643948", "new_sharedmsnweather_2a0bcba7"], declared.Select(reference => reference.LogicalName));
        Assert.Equal(["shared_teams", "shared_msnweather"], declared.Select(reference => reference.ConnectorInternalId));
    }

    [Fact]
    public void ConnectionReferences_FallsBackToTheKeyWhenNoApiIsNamed()
    {
        var declared = Assert.Single(WorkflowDefinitionFile.ConnectionReferences("""
        {"properties":{"connectionReferences":{"shared_sql":{"connection":{"connectionReferenceLogicalName":"new_sql"}}}}}
        """));

        Assert.Equal("shared_sql", declared.ConnectorInternalId);
    }

    [Fact]
    public void PortableDefinition_DropsTheConnectionNameWhereverItIsNested()
    {
        var portable = WorkflowDefinitionFile.PortableDefinition("""
        {"properties":{
          "connectionReferences":{"shared_teams":{"connection":{"connectionReferenceLogicalName":"new_teams"},"connectionName":"shared-teams-13d033ce"}},
          "definition":{"triggers":{"manual":{"metadata":{"associatedData":{"graph":{
            "connectionReferences":{"shared_teams":{"connection":{"connectionReferenceLogicalName":"new_teams"},"connectionName":"shared-teams-13d033ce"}}}}}}}}}}
        """);

        Assert.DoesNotContain("shared-teams-13d033ce", portable, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(portable, "new_teams").Count);
    }

    [Fact]
    public void PortableDefinition_KeepsTheConnectorKeysActionsAndNodesUse()
    {
        var portable = WorkflowDefinitionFile.PortableDefinition("""
        {"properties":{
          "connectionReferences":{"shared_teams":{"connection":{"connectionReferenceLogicalName":"new_teams"},"connectionName":"shared-teams-13d033ce"}},
          "definition":{
            "triggers":{"manual":{"metadata":{"associatedData":{"graph":{"nodes":[{"data":{"config":{"connectionName":"shared_teams"}}}]}}}}},
            "actions":{"Create_a_team":{"inputs":{"host":{"connectionName":"shared_teams"}}}}}}}
        """);

        using var document = JsonDocument.Parse(portable);
        var definition = document.RootElement.GetProperty("properties").GetProperty("definition");

        Assert.Equal("shared_teams", definition.GetProperty("actions").GetProperty("Create_a_team").GetProperty("inputs").GetProperty("host").GetProperty("connectionName").GetString());
        Assert.Equal("shared_teams", definition.GetProperty("triggers").GetProperty("manual").GetProperty("metadata").GetProperty("associatedData").GetProperty("graph").GetProperty("nodes")[0].GetProperty("data").GetProperty("config").GetProperty("connectionName").GetString());
        Assert.DoesNotContain("shared-teams-13d033ce", portable, StringComparison.Ordinal);
    }

    [Fact]
    public void PortableDefinition_DropsTheConnectionNameInsideAnArray()
    {
        var portable = WorkflowDefinitionFile.PortableDefinition("""
        {"properties":{"connectionReferences":[{"connection":{"connectionReferenceLogicalName":"new_teams"},"connectionName":"shared-teams-13d033ce"}]}}
        """);

        Assert.DoesNotContain("shared-teams-13d033ce", portable, StringComparison.Ordinal);
    }

    [Fact]
    public void PortableDefinition_DropsTheConnectionNameTheSourceEnvironmentBound()
    {
        var portable = WorkflowDefinitionFile.PortableDefinition("""
        {"properties":{"connectionReferences":{
          "shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"},"runtimeSource":"embedded","connectionName":"shared-teams-13d033ce"}}}}
        """);

        Assert.DoesNotContain("connectionName", portable, StringComparison.Ordinal);
        Assert.Contains("new_teams", portable, StringComparison.Ordinal);
        Assert.Contains("embedded", portable, StringComparison.Ordinal);
    }

    [Fact]
    public void PortableDefinition_KeepsAConnectionNameThatHasNoConnectionReference()
    {
        const string definition = """{"properties":{"connectionReferences":{"shared_teams":{"api":{"name":"shared_teams"},"connectionName":"shared-teams-13d033ce"}}}}""";

        Assert.Equal(definition, WorkflowDefinitionFile.PortableDefinition(definition));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void PortableDefinition_WithNothingToRead_IsEmpty(string? clientData) => Assert.Empty(WorkflowDefinitionFile.PortableDefinition(clientData));

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"properties\":{}}")]
    [InlineData("{\"properties\":{\"connectionReferences\":[]}}")]
    public void PortableDefinition_WithNothingToDrop_IsUnchanged(string clientData) => Assert.Equal(clientData, WorkflowDefinitionFile.PortableDefinition(clientData));

    [Theory]
    [InlineData("{\"properties\":{\"definition\":{\"actions\":{\"Compose\":{\"inputs\":{\"connection\":\"manual\"}}}}}}")]
    [InlineData("{\"properties\":{\"definition\":{\"actions\":{\"Compose\":{\"inputs\":{\"connection\":123}}}}}}")]
    [InlineData("{\"properties\":{\"definition\":{\"actions\":{\"Compose\":{\"inputs\":{\"connection\":[\"a\"]}}}}}}")]
    [InlineData("{\"properties\":{\"definition\":{\"actions\":{\"Compose\":{\"inputs\":{\"connection\":null}}}}}}")]
    public void PortableDefinition_WhenConnectionIsNotAnObject_IsUnchanged(string clientData) =>
        Assert.Equal(clientData, WorkflowDefinitionFile.PortableDefinition(clientData));

    [Fact]
    public void PortableDefinition_WhenAScalarConnectionSitsBesideARealOne_StillDropsTheBoundName()
    {
        var portable = WorkflowDefinitionFile.PortableDefinition("""
        {"properties":{
          "definition":{"actions":{"Compose":{"inputs":{"connection":"manual"}}}},
          "connectionReferences":{"shared_teams":{"connection":{"connectionReferenceLogicalName":"new_teams"},"connectionName":"shared-teams-13d033ce"}}}}
        """);

        Assert.DoesNotContain("shared-teams-13d033ce", portable, StringComparison.Ordinal);
        Assert.Contains("manual", portable, StringComparison.Ordinal);
    }

    [Fact]
    public void PortableDefinition_LeavesTheRestOfTheDefinitionAlone()
    {
        var portable = WorkflowDefinitionFile.PortableDefinition("""
        {"properties":{"definition":{"triggers":{"manual":{"type":"Request"}}},"connectionReferences":{
          "shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"},"connectionName":"x"}}}}
        """);

        using var document = JsonDocument.Parse(portable);

        Assert.Equal("Request", document.RootElement.GetProperty("properties").GetProperty("definition").GetProperty("triggers").GetProperty("manual").GetProperty("type").GetString());
        Assert.Equal(["new_teams"], WorkflowDefinitionFile.ConnectionReferenceNames(portable));
    }

    [Fact]
    public void ConnectionReferences_KeepsEntriesThatDifferOnlyByCase()
    {
        var declared = WorkflowDefinitionFile.ConnectionReferences("""
        {"properties":{"connectionReferences":{
          "a":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}},
          "b":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"NEW_TEAMS"}}}}}
        """);

        Assert.Equal(["new_teams", "NEW_TEAMS"], declared.Select(reference => reference.LogicalName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"properties\":{}}")]
    [InlineData("{\"properties\":{\"connectionReferences\":[]}}")]
    public void ConnectionReferences_WithNothingToRead_IsEmpty(string? clientData) => Assert.Empty(WorkflowDefinitionFile.ConnectionReferences(clientData));

    [Fact]
    public void ConnectionReferences_KeepsEveryEntryEvenWhenTwoShareALogicalName()
    {
        var declared = WorkflowDefinitionFile.ConnectionReferences("""
        {"properties":{"connectionReferences":{
          "shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}},
          "shared_teams_1":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}}}}
        """);

        Assert.Equal(["new_teams", "new_teams"], declared.Select(reference => reference.LogicalName));
    }

    [Fact]
    public void WriteConnectionReferenceDeclaration_DeclaresASharedLogicalNameOnlyOnce()
    {
        WriteWorkflow("Flow", Guid.NewGuid(), """
        {"properties":{"connectionReferences":{
          "shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}},
          "shared_teams_1":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}}}}
        """);

        WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(_root);

        var written = File.ReadAllText(Path.Combine(_root, WorkflowDefinitionFile.ConnectionReferencesFileName));

        Assert.Equal(1, written.Split("new_teams").Length - 1);
    }

    [Fact]
    public void WriteConnectionReferenceDeclaration_UnionsWhatEveryWorkflowDeclares()
    {
        WriteWorkflow("Teams", Guid.NewGuid(), DefinitionWithReferences);
        WriteWorkflow("Sql", Guid.NewGuid(), DefinitionWithSqlReference);

        Assert.True(WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(_root));

        var written = File.ReadAllText(Path.Combine(_root, WorkflowDefinitionFile.ConnectionReferencesFileName));

        Assert.Contains("new_sharedteams_aa643948", written, StringComparison.Ordinal);
        Assert.Contains("new_sharedmsnweather_2a0bcba7", written, StringComparison.Ordinal);
        Assert.Contains("new_sql_1b2c3d", written, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteConnectionReferenceDeclaration_WhenOneWorkflowDeclaresNothing_KeepsWhatTheOthersDeclare()
    {
        WriteWorkflow("Teams", Guid.NewGuid(), DefinitionWithReferences);
        WriteWorkflow("Bare", Guid.NewGuid(), "{}");

        WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(_root);

        var path = Path.Combine(_root, WorkflowDefinitionFile.ConnectionReferencesFileName);

        Assert.True(File.Exists(path));
        Assert.Contains("new_sharedteams_aa643948", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void WriteConnectionReferenceDeclaration_OnAnAgentWorkspace_IsRefused()
    {
        WriteWorkflow("Flow", Guid.NewGuid(), DefinitionWithReferences);
        File.WriteAllText(Path.Combine(_root, "settings.mcs.yml"), "displayName: Agent");
        File.WriteAllText(Path.Combine(_root, WorkflowDefinitionFile.ConnectionReferencesFileName), "owned by the agent");

        Assert.Throws<ArgumentException>(() => WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(_root));
        Assert.Equal("owned by the agent", File.ReadAllText(Path.Combine(_root, WorkflowDefinitionFile.ConnectionReferencesFileName)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void WriteConnectionReferenceDeclaration_RequiresAWorkspaceRoot(string? workspaceRoot) =>
        Assert.Throws<ArgumentException>(() => WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(workspaceRoot!));

    [Fact]
    public void ConnectionReferences_FromAParsedDocument_MatchesTheStringOverload()
    {
        using var document = JsonDocument.Parse(DefinitionWithReferences);

        Assert.Equal(
            WorkflowDefinitionFile.ConnectionReferences(DefinitionWithReferences),
            WorkflowDefinitionFile.ConnectionReferences(document.RootElement));
    }

    [Fact]
    public void ConnectionReferenceNames_ProjectsTheLogicalNames() =>
        Assert.Equal(
            WorkflowDefinitionFile.ConnectionReferences(DefinitionWithReferences).Select(reference => reference.LogicalName),
            WorkflowDefinitionFile.ConnectionReferenceNames(DefinitionWithReferences));

    [Fact]
    public void WriteConnectionReferenceDeclaration_WritesEveryConnectorWithItsPrefix()
    {
        WriteWorkflow("Flow", Guid.NewGuid(), DefinitionWithReferences);

        Assert.True(WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(_root));

        var written = File.ReadAllText(Path.Combine(_root, WorkflowDefinitionFile.ConnectionReferencesFileName));

        Assert.Contains("new_sharedteams_aa643948", written, StringComparison.Ordinal);
        Assert.Contains(WorkflowDefinitionFile.ConnectorIdPrefix + "shared_teams", written, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteConnectionReferenceDeclaration_WhenNothingChanged_WritesNothing()
    {
        WriteWorkflow("Flow", Guid.NewGuid(), DefinitionWithReferences);
        WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(_root);

        Assert.False(WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(_root));
    }

    [Fact]
    public void WriteConnectionReferenceDeclaration_NeverWritesTheConnectionTheEnvironmentOwns()
    {
        WriteWorkflow("Flow", Guid.NewGuid(), DefinitionWithReferences);

        WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(_root);

        Assert.DoesNotContain("connectionId", File.ReadAllText(Path.Combine(_root, WorkflowDefinitionFile.ConnectionReferencesFileName)), StringComparison.Ordinal);
    }

    [Fact]
    public void WriteConnectionReferenceDeclaration_WhenADefinitionCannotBeRead_LeavesTheDeclarationAlone()
    {
        WriteWorkflow("Flow", Guid.NewGuid(), DefinitionWithReferences);
        WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(_root);

        var path = Path.Combine(_root, WorkflowDefinitionFile.ConnectionReferencesFileName);
        var before = File.ReadAllText(path);

        var broken = WriteWorkflow("Broken", Guid.NewGuid(), "{ not json");

        Assert.False(WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(_root));
        Assert.Equal(before, File.ReadAllText(path));
        Assert.True(Directory.Exists(broken));
    }

    [Fact]
    public void WriteConnectionReferenceDeclaration_WhenNoReferenceRemains_DeletesTheFile()
    {
        var folder = WriteWorkflow("Flow", Guid.NewGuid(), DefinitionWithReferences);
        WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(_root);

        File.WriteAllText(Path.Combine(folder, WorkflowWorkspace.DefinitionFileName), "{}");

        Assert.True(WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(_root));
        Assert.False(File.Exists(Path.Combine(_root, WorkflowDefinitionFile.ConnectionReferencesFileName)));
    }

    [Fact]
    public void WriteConnectionReferenceDeclaration_WithNoFileAndNoReference_ReportsNoChange()
    {
        Directory.CreateDirectory(_root);

        Assert.False(WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(_root));
    }

    [Fact]
    public void WriteIfChanged_WritesOnlyWhenTheContentDiffers()
    {
        var path = Path.Combine(_root, "nested", "file.txt");

        Assert.True(WorkflowDefinitionFile.WriteIfChanged(path, "one"));
        Assert.False(WorkflowDefinitionFile.WriteIfChanged(path, "one"));
        Assert.True(WorkflowDefinitionFile.WriteIfChanged(path, "two"));
        Assert.Equal("two", File.ReadAllText(path));
    }

    [Fact]
    public void WriteIfChanged_WritesWithoutAByteOrderMark()
    {
        var path = Path.Combine(_root, "file.txt");
        WorkflowDefinitionFile.WriteIfChanged(path, "content");

        Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(path).Take(3).ToArray());
    }

    [Fact]
    public void MetadataFile_RoundTripsEveryProperty()
    {
        var original = WorkflowScaffolder.NewMetadata("Round Trip", Guid.NewGuid(), "workflows/RoundTrip");
        original.Description = "described";

        var restored = WorkflowMetadataFile.ReadStrict(WorkflowMetadataFile.Write(original));

        Assert.NotNull(restored);
        Assert.Equal(original.WorkflowId, restored!.WorkflowId);
        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.Description, restored.Description);
        Assert.Equal(original.StateCode, restored.StateCode);
        Assert.Equal(original.StatusCode, restored.StatusCode);
        Assert.Equal(original.JsonFileName, restored.JsonFileName);
        Assert.Equal(original.PrimaryEntity, restored.PrimaryEntity);
        Assert.Equal(original.IsCustomizable!.ManagedPropertyLogicalName, restored.IsCustomizable!.ManagedPropertyLogicalName);
    }

    [Fact]
    public void MetadataFile_ReadStrict_RejectsAPropertyTheTypeDoesNotDeclare() =>
        Assert.ThrowsAny<Exception>(() => WorkflowMetadataFile.ReadStrict($"workflowId: {Guid.NewGuid()}\nname: Flow\nsomethingNew: 42\n"));

    [Fact]
    public void MetadataFile_ReadStrict_OfAnEmptyDocument_IsNull() => Assert.Null(WorkflowMetadataFile.ReadStrict(string.Empty));

    [Fact]
    public void MetadataFile_Write_RejectsNothingToWrite() => Assert.Throws<ArgumentNullException>(() => WorkflowMetadataFile.Write(null!));

    [Fact]
    public void Scaffold_WritesADefinitionAndMetadataPairThatReadsBack()
    {
        var workflowId = Guid.NewGuid();

        var written = WorkflowScaffolder.Scaffold(_root, "Scaffolded Flow", workflowId);

        Assert.Equal(
            [$"workflows/ScaffoldedFlow-{workflowId}/workflow.json", $"workflows/ScaffoldedFlow-{workflowId}/metadata.yml"],
            written);

        var found = Assert.Single(WorkflowWorkspace.FindAll(_root));

        Assert.Equal(workflowId, found.WorkflowId);
        Assert.Equal(workflowId, WorkflowMetadataFile.ReadStrict(File.ReadAllText(found.MetadataPath))!.WorkflowId);
    }

    [Fact]
    public void Scaffold_NamesTheWorkflowInTheDefinition()
    {
        WorkflowScaffolder.Scaffold(_root, "Scaffolded Flow", Guid.NewGuid());

        var definition = File.ReadAllText(WorkflowWorkspace.FindAll(_root).Single().DefinitionPath);

        Assert.Contains("Scaffolded Flow", definition, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", definition, StringComparison.Ordinal);
    }

    [Fact]
    public void Scaffold_CreatesAGenericAgentCallWithInputAndOutput()
    {
        WorkflowScaffolder.Scaffold(_root, "Scaffolded Flow", Guid.NewGuid());

        var definition = File.ReadAllText(WorkflowWorkspace.FindAll(_root).Single().DefinitionPath);
        using var document = JsonDocument.Parse(definition);
        var workflow = document.RootElement.GetProperty("properties").GetProperty("definition");
        var triggerProperties = workflow.GetProperty("triggers").GetProperty("manual").GetProperty("inputs").GetProperty("schema").GetProperty("properties");
        var responseProperties = workflow.GetProperty("actions").GetProperty("Respond_to_the_agent").GetProperty("inputs").GetProperty("schema").GetProperty("properties");

        Assert.Equal(JsonValueKind.Object, triggerProperties.GetProperty("input").ValueKind);
        Assert.Equal(JsonValueKind.Object, responseProperties.GetProperty("output").ValueKind);
        Assert.False(triggerProperties.TryGetProperty("event", out _));
        Assert.False(triggerProperties.TryGetProperty("timestamp", out _));
        Assert.False(triggerProperties.TryGetProperty("metadata", out _));
        Assert.DoesNotContain("SessionStart", definition, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hook", definition, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scaffold_GivesEachWorkflowItsOwnNodeIdentifiers()
    {
        WorkflowScaffolder.Scaffold(_root, "First", Guid.NewGuid());
        var first = File.ReadAllText(WorkflowWorkspace.FindAll(_root).Single().DefinitionPath);

        var second = Path.Combine(Path.GetTempPath(), "mcs-standalone-" + Guid.NewGuid().ToString("N"));

        try
        {
            WorkflowScaffolder.Scaffold(second, "First", Guid.NewGuid());

            Assert.NotEqual(first, File.ReadAllText(WorkflowWorkspace.FindAll(second).Single().DefinitionPath));
        }
        finally
        {
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public void Scaffold_WhenAWorkflowAlreadyOccupiesTheFolder_KeepsWhatIsThere()
    {
        var workflowId = Guid.NewGuid();
        WorkflowScaffolder.Scaffold(_root, "Scaffolded Flow", workflowId);

        var found = WorkflowWorkspace.FindAll(_root).Single();
        File.WriteAllText(found.DefinitionPath, DefinitionWithReferences);

        Assert.Throws<InvalidOperationException>(() => WorkflowScaffolder.Scaffold(_root, "Scaffolded Flow", workflowId));
        Assert.Equal(DefinitionWithReferences, File.ReadAllText(found.DefinitionPath));
    }

    [Fact]
    public void Scaffold_WhenOnlyTheMetadataSurvives_KeepsItRatherThanStartingOver()
    {
        var workflowId = Guid.NewGuid();
        WorkflowScaffolder.Scaffold(_root, "Scaffolded Flow", workflowId);

        var found = WorkflowWorkspace.FindAll(_root).Single();
        var metadata = File.ReadAllText(found.MetadataPath);
        File.Delete(found.DefinitionPath);

        Assert.Throws<InvalidOperationException>(() => WorkflowScaffolder.Scaffold(_root, "Scaffolded Flow", workflowId));
        Assert.Equal(metadata, File.ReadAllText(found.MetadataPath));
        Assert.False(File.Exists(found.DefinitionPath));
    }

    [Fact]
    public void Scaffold_RequiresAName() => Assert.Throws<ArgumentException>(() => WorkflowScaffolder.Scaffold(_root, "  ", Guid.NewGuid()));

    [Fact]
    public void Scaffold_RequiresAWorkspaceRoot() => Assert.Throws<ArgumentException>(() => WorkflowScaffolder.Scaffold("  ", "Flow", Guid.NewGuid()));
}
