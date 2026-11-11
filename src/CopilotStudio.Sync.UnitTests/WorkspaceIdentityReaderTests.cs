using System.Text;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.UnitTests.Copy;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public sealed class WorkspaceIdentityReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "identitytests-" + Guid.NewGuid().ToString("N"));

    public WorkspaceIdentityReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Read_AQuotedSchemaAndDisplayName_IsRead()
    {
        Write("settings.mcs.yml", "displayName: \"Contoso Helpdesk\"\nschemaName: \"con_helpdesk\"\n");

        Assert.Equal(new WorkspaceIdentity("con_helpdesk", "Contoso Helpdesk", null, AuthoringShape.Classic), WorkspaceIdentityReader.Read(_root));
    }

    [Fact]
    public void Read_AMinimalUnquotedFile_IsRead()
    {
        Write("settings.mcs.yml", "displayName: X\nschemaName: y_z\n");

        var identity = WorkspaceIdentityReader.Read(_root);

        Assert.Equal("y_z", identity?.SchemaName);
        Assert.Equal("X", identity?.DisplayName);
    }

    [Fact]
    public void Read_UnescapesAQuotedDisplayName()
    {
        Write("settings.mcs.yml", "displayName: \"He said \\\"hi\\\"\"\nschemaName: \"con_agent\"\n");

        Assert.Equal("He said \"hi\"", WorkspaceIdentityReader.Read(_root)?.DisplayName);
    }

    [Fact]
    public void Read_ATopLevelDescription_IsRead()
    {
        Write("settings.mcs.yml", "displayName: Helpdesk\nschemaName: con_agent\ndescription: Answers questions.\n");

        Assert.Equal("Answers questions.", WorkspaceIdentityReader.Read(_root)?.Description);
    }

    [Theory]
    [InlineData("schemaName: \"con_agent\"\n")]
    [InlineData("displayName: \"  \"\nschemaName: \"con_agent\"\n")]
    public void Read_WithoutADisplayName_FallsBackToTheSchemaName(string settings)
    {
        Write("settings.mcs.yml", settings);

        Assert.Equal("con_agent", WorkspaceIdentityReader.Read(_root)?.DisplayName);
    }

    [Fact]
    public void Read_IgnoresANestedKeyOfTheSameName()
    {
        Write("settings.mcs.yml", "schemaName: \"con_agent\"\nconfiguration:\n  gPTSettings:\n    defaultSchemaName: con_agent.gpt.default\n");

        Assert.Equal("con_agent", WorkspaceIdentityReader.Read(_root)?.SchemaName);
    }

    [Fact]
    public void Read_TheClassicAgentFileWithoutSettings_IsRead()
    {
        Write("agent.mcs.yml", "displayName: \"Classic Agent\"\nschemaName: \"con_classic\"\n");

        Assert.Equal(new WorkspaceIdentity("con_classic", "Classic Agent", null, AuthoringShape.Classic), WorkspaceIdentityReader.Read(_root));
    }

    [Fact]
    public void Read_SettingsWithAnIdentity_WinOverTheClassicAgentFile()
    {
        Write("settings.mcs.yml", "displayName: Settings Agent\nschemaName: con_settings\n");
        Write("agent.mcs.yml", "displayName: Classic Agent\nschemaName: con_classic\n");

        Assert.Equal("con_settings", WorkspaceIdentityReader.Read(_root)?.SchemaName);
    }

    [Fact]
    public void Read_SettingsWithoutAnIdentity_FallBackToTheClassicAgentFile()
    {
        Write("settings.mcs.yml", "displayName: Settings Agent\n");
        Write("agent.mcs.yml", "displayName: Classic Agent\nschemaName: con_classic\n");

        Assert.Equal("con_classic", WorkspaceIdentityReader.Read(_root)?.SchemaName);
    }

    [Theory]
    [InlineData("displayName: \"Contoso Helpdesk\"\n", null)]
    [InlineData("displayName: \"unterminated\nschemaName: con_agent\n", null)]
    [InlineData("displayName: Settings Agent\n", "mcs.metadata:\n  componentName: Classic Source\nkind: GptComponentMetadata\ninstructions: Help.\n")]
    public void Read_WithoutAReadableIdentity_ReturnsNull(string settings, string? agent)
    {
        Write("settings.mcs.yml", settings);

        if (agent is not null)
        {
            Write("agent.mcs.yml", agent);
        }

        Assert.Null(WorkspaceIdentityReader.Read(_root));
    }

    [Fact]
    public void Read_WithNoProjectFile_ReturnsNull()
    {
        Assert.Null(WorkspaceIdentityReader.Read(_root));
    }

    [Fact]
    public void Read_ACliWorkspace_IsCliCopilot()
    {
        using var workspace = CopyTestWorkspace.Cli();

        Assert.Equal(new WorkspaceIdentity(CopyTestWorkspace.CliSchema, "Cli Source", null, AuthoringShape.CliCopilot), WorkspaceIdentityReader.Read(workspace.Root));
    }

    [Fact]
    public void Read_AClassicWorkspaceWithASettingsFile_IsClassic()
    {
        using var workspace = CopyTestWorkspace.Classic();

        Assert.Equal(new WorkspaceIdentity(CopyTestWorkspace.ClassicSchema, "Classic Source", null, AuthoringShape.Classic), WorkspaceIdentityReader.Read(workspace.Root));
    }

    [Fact]
    public void Read_ALayoutMarkerOverClassicContent_IsUnknown()
    {
        using var workspace = CopyTestWorkspace.Classic();
        workspace.Write(AgentClassifier.WorkspaceLayoutMarkerFileName, "layoutVersion: 1\n");

        Assert.Equal(AuthoringShape.Unknown, WorkspaceIdentityReader.Read(workspace.Root)?.AuthoringShape);
    }

    private void Write(string relative, string content) => File.WriteAllText(Path.Combine(_root, relative), content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}
