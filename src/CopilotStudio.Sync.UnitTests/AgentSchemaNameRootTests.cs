// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.CopilotStudio.McsCore;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public sealed class AgentSchemaNameRootTests : IDisposable
{
    private const string Source = "crf9a_AgentD1";

    private const string Target = "cr468_amse2ed10406";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "schema-root-" + Guid.NewGuid().ToString("N"));

    public static TheoryData<bool> Storage => new() { true, false };

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Theory]
    [InlineData(Source, Target)]
    [InlineData(Source + ".agent.Agent", Target + ".agent.Agent")]
    [InlineData("CRF9A_AGENTD1.topic.Escalate", Target + ".topic.Escalate")]
    [InlineData(Source + "_extra.topic.Escalate", Source + "_extra.topic.Escalate")]
    [InlineData("cr999_other.agent.Agent", "cr999_other.agent.Agent")]
    [InlineData("." + Source, "." + Source)]
    [InlineData("", "")]
    public void Rebase_MovesOnlyNamesRootedInTheSourceSchemaName(string schemaName, string expected)
    {
        Assert.Equal(expected, AgentSchemaNameRoot.Rebase(schemaName, Source, Target));
    }

    [Theory]
    [MemberData(nameof(Storage))]
    public void Rebase_RenamesEveryNameTheFolderDeclaresForItsOwnAgentAndNothingElse(bool inMemory)
    {
        var workspace = Workspace(inMemory);
        var untouched = new Dictionary<string, string>
        {
            ["topics/Fallback.mcs.yml"] = $"kind: AdaptiveDialog\nbeginDialog:\n  kind: OnUnknownIntent\n  id: main\n  actions:\n    - kind: BeginDialog\n      id: escalate\n      dialog: {Source}.topic.Escalate\n",
            ["actions/Teams.mcs.yml"] = $"kind: TaskDialog\naction:\n  kind: InvokeConnectorTaskAction\n  connectionReference: {Source}.shared_teams.0add\n",
            ["connectionreferences.mcs.yml"] = $"connectionReferences:\n  - connectionReferenceLogicalName: {Source}.shared_teams.0add\n",
            [$"infrastructure/connections/{Source}.cr.shared_teams.sync.yaml"] = $"connectionReferences:\n  - connectionReferenceLogicalName: {Source}.cr.shared_teams\n",
            ["references.mcs.yml"] = $"componentCollections:\n  - schemaName: {Source}\n    directory: ../Tools/\n",
            ["agents/Other/agent.mcs.yml"] = "mcs.metadata:\n  componentName: Other\n  schemaName: cr999_other.agent.Other\nkind: AgentDialog\n",
            [$"knowledge/files/{Source}.notes.txt"] = $"schemaName: {Source}\n",
            [".mcs/botdefinition.json"] = $"{{\"schemaName\":\"{Source}\"}}",
            [$".mcs/{Source}.baseline.mcs.yml"] = $"schemaName: {Source}\n",
        };
        var rebased = new Dictionary<string, (string Path, string Content)>
        {
            ["settings.mcs.yml"] = ("settings.mcs.yml", "displayName: Agent D1\nschemaName: {0}\nconfiguration:\n  gPTSettings:\n    defaultSchemaName: {0}.gpt.default\n"),
            ["agents/Agent child 1/agent.mcs.yml"] = ("agents/Agent child 1/agent.mcs.yml", "mcs.metadata:\n  componentName: Agent child 1\n  schemaName: {0}.agent.Agent\nkind: AgentDialog\n"),
            ["behaviors/get-us-weather/skill.mcs.yml"] = ("behaviors/get-us-weather/skill.mcs.yml", "mcs.metadata:\n  componentName: get-us-weather\n  schemaName: '{0}.skill.get-us-weather'\n  bundle: \"{0}.file.getusweatherzip\"\n  manifestSchemaName: {0}.file.getusweather.SKILL.md # manifest\nkind: InlineAgentSkill\n"),
            ["behaviors/get-us-weather/scripts/weather.py.mcs.yml"] = ("behaviors/get-us-weather/scripts/weather.py.mcs.yml", "mcs.metadata:\n  componentName: scripts/weather.py\n  schemaName: {0}.file.weatherpy\n"),
            [$"environmentvariables/{Source}.region.mcs.yml"] = ("environmentvariables/{0}.region.mcs.yml", "kind: EnvironmentVariableDefinition\nschemaName: {0}.region\ndisplayName: Region\n"),
            [$"variables/{Source}.GlobalVariableComponent.UserCountry.mcs.yml"] = ("variables/{0}.GlobalVariableComponent.UserCountry.mcs.yml", "mcs.metadata:\n  componentName: UserCountry\nkind: Variable\nname: UserCountry\n"),
        };

        foreach (var file in untouched)
        {
            Write(workspace, file.Key, file.Value);
        }

        foreach (var file in rebased)
        {
            Write(workspace, file.Key, string.Format(file.Value.Content, Source));
        }

        Write(workspace, "agents/Legacy/.agent.json", $"{{\"schemaName\":\"{Source}.agent.Legacy\",\"folderName\":\"Legacy\"}}");

        AgentSchemaNameRoot.Rebase(workspace, Source, Target);

        Assert.Equal(untouched.Keys.Concat(rebased.Values.Select(file => string.Format(file.Path, Target))).Append("agents/Legacy/.agent.json").OrderBy(path => path, StringComparer.Ordinal), workspace.ListFiles().Select(path => path.ToString()).OrderBy(path => path, StringComparer.Ordinal));
        Assert.All(untouched, file => Assert.Equal(file.Value, Read(workspace, file.Key)));
        Assert.All(rebased.Values, file => Assert.Equal(string.Format(file.Content, Target), Read(workspace, string.Format(file.Path, Target))));
        Assert.Equal((Target + ".agent.Legacy", "Legacy"), Link(workspace, "agents/Legacy/.agent.json"));
    }

    [Theory]
    [MemberData(nameof(Storage))]
    public void Rebase_KeepsTheByteOrderMarkAndLineEndingsOfEveryFileItRewrites(bool inMemory)
    {
        var workspace = Workspace(inMemory);
        var marked = Encoding.UTF8.GetPreamble();
        WriteBytes(workspace, "settings.mcs.yml", marked.Concat(Encoding.UTF8.GetBytes($"displayName: Agent D1\r\nschemaName: {Source}\r\n")).ToArray());
        WriteBytes(workspace, $"environmentvariables/{Source}.region.mcs.yml", Encoding.UTF8.GetBytes($"kind: EnvironmentVariableDefinition\r\nschemaName: {Source}.region\r\n"));

        AgentSchemaNameRoot.Rebase(workspace, Source, Target);

        Assert.Equal(marked.Concat(Encoding.UTF8.GetBytes($"displayName: Agent D1\r\nschemaName: {Target}\r\n")), ReadBytes(workspace, "settings.mcs.yml"));
        Assert.Equal(Encoding.UTF8.GetBytes($"kind: EnvironmentVariableDefinition\r\nschemaName: {Target}.region\r\n"), ReadBytes(workspace, $"environmentvariables/{Target}.region.mcs.yml"));
    }

    [Theory]
    [InlineData(null, Target)]
    [InlineData(" ", Target)]
    [InlineData(Source, Source)]
    public void Rebase_WithoutADifferentSourceSchemaName_ChangesNothing(string? sourceRoot, string targetRoot)
    {
        var workspace = Workspace(inMemory: true);
        Write(workspace, "settings.mcs.yml", $"schemaName: {Source}\n");
        Write(workspace, $"environmentvariables/{Source}.region.mcs.yml", $"schemaName: {Source}.region\n");

        AgentSchemaNameRoot.Rebase(workspace, sourceRoot, targetRoot);

        Assert.Equal(new[] { $"environmentvariables/{Source}.region.mcs.yml", "settings.mcs.yml" }, workspace.ListFiles().Select(path => path.ToString()).OrderBy(path => path, StringComparer.Ordinal));
        Assert.Equal($"schemaName: {Source}\n", Read(workspace, "settings.mcs.yml"));
        Assert.Equal($"schemaName: {Source}.region\n", Read(workspace, $"environmentvariables/{Source}.region.mcs.yml"));
    }

    [Theory]
    [MemberData(nameof(Storage))]
    public void Rebase_ToASchemaNameThatDiffersOnlyInCase_RewritesTheNamesAndKeepsTheFileNames(bool inMemory)
    {
        var workspace = Workspace(inMemory);
        var upper = Source.ToUpperInvariant();
        Write(workspace, "settings.mcs.yml", $"schemaName: {Source}\n");
        Write(workspace, $"environmentvariables/{Source}.region.mcs.yml", $"schemaName: {Source}.region\n");

        AgentSchemaNameRoot.Rebase(workspace, Source, upper);

        Assert.Equal(new[] { $"environmentvariables/{Source}.region.mcs.yml", "settings.mcs.yml" }, workspace.ListFiles().Select(path => path.ToString()).OrderBy(path => path, StringComparer.Ordinal));
        Assert.Equal($"schemaName: {upper}\n", Read(workspace, "settings.mcs.yml"));
        Assert.Equal($"schemaName: {upper}.region\n", Read(workspace, $"environmentvariables/{Source}.region.mcs.yml"));
    }

    [Theory]
    [MemberData(nameof(Storage))]
    public void Rebase_WhenARenamedFileWouldReplaceAnother_RefusesBeforeChangingAnything(bool inMemory)
    {
        var workspace = Workspace(inMemory);
        Write(workspace, "settings.mcs.yml", $"schemaName: {Source}\n");
        Write(workspace, $"environmentvariables/{Source}.region.mcs.yml", $"schemaName: {Source}.region\n");
        Write(workspace, $"environmentvariables/{Target}.region.mcs.yml", $"schemaName: {Target}.region\n");

        var diagnostic = Assert.Single(Assert.Throws<WorkspaceValidationException>(() => AgentSchemaNameRoot.Rebase(workspace, Source, Target)).Diagnostics);
        Assert.Equal($"environmentvariables/{Source}.region.mcs.yml", diagnostic.FilePath);
        Assert.Contains($"environmentvariables/{Target}.region.mcs.yml", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal($"schemaName: {Source}\n", Read(workspace, "settings.mcs.yml"));
        Assert.Equal($"schemaName: {Source}.region\n", Read(workspace, $"environmentvariables/{Source}.region.mcs.yml"));
        Assert.Equal($"schemaName: {Target}.region\n", Read(workspace, $"environmentvariables/{Target}.region.mcs.yml"));
    }

    private IFileAccessor Workspace(bool inMemory) => inMemory ? new Microsoft.CopilotStudio.McsCore.InMemoryFileAccessor() : new FileAccessorFactory().Create(new DirectoryPath(_folder.Replace('\\', '/') + "/"));

    private static void Write(IFileAccessor workspace, string path, string content) => WriteBytes(workspace, path, Encoding.UTF8.GetBytes(content));

    private static void WriteBytes(IFileAccessor workspace, string path, byte[] content)
    {
        using var stream = workspace.OpenWrite(new AgentFilePath(path));
        stream.Write(content, 0, content.Length);
    }

    private static byte[] ReadBytes(IFileAccessor workspace, string path)
    {
        using var stream = workspace.OpenRead(new AgentFilePath(path));
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return buffer.ToArray();
    }

    private static string Read(IFileAccessor workspace, string path) => Encoding.UTF8.GetString(ReadBytes(workspace, path));

    private static (string SchemaName, string FolderName) Link(IFileAccessor workspace, string path) => SchemaLink.TryRead(workspace, new AgentFilePath(path)) is { } link ? (link.SchemaName, link.FolderName) : default;
}
