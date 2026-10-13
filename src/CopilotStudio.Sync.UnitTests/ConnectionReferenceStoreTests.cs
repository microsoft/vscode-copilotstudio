// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class ConnectionReferenceStoreTests
{
    private static readonly byte[] ByteOrderMark = { 0xEF, 0xBB, 0xBF };

    private static InMemoryFileAccessor NewWorkspace() => new InMemoryFileAccessor(new DirectoryPath("workspace"));

    private static ConnectionReference Reference(string logicalName, string connector) =>
        new ConnectionReference(
            connectionReferenceLogicalName: logicalName,
            connectionId: string.Empty,
            connectorId: WorkflowDefinitionFile.ConnectorIdPrefix + connector);

    private static string ReadText(IFileAccessor fileAccessor, string path)
    {
        using var stream = fileAccessor.OpenRead(new AgentFilePath(path));
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return reader.ReadToEnd();
    }

    private static byte[] ReadBytes(IFileAccessor fileAccessor, string path)
    {
        using var stream = fileAccessor.OpenRead(new AgentFilePath(path));
        using var buffer = new MemoryStream();

        stream.CopyTo(buffer);

        return buffer.ToArray();
    }

    private static string LayeredPath(string logicalName) =>
        $"{ConnectionReferenceStore.LayeredFolderName}/{logicalName}{ConnectionReferenceStore.LayeredFileExtension}";

    private static string[] LayeredFiles(IFileAccessor fileAccessor) =>
        fileAccessor.ListFiles(ConnectionReferenceStore.LayeredFolderName, "*" + ConnectionReferenceStore.LayeredFileExtension)
            .Select(path => path.ToString())
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static bool WriteLayered(IFileAccessor fileAccessor, params ConnectionReference[] references) =>
        ConnectionReferenceStore.Write(fileAccessor, references, ConnectionReferenceLayout.Layered, null, CancellationToken.None);

    private static bool WriteFlat(IFileAccessor fileAccessor, params ConnectionReference[] references) =>
        ConnectionReferenceStore.Write(fileAccessor, references, ConnectionReferenceLayout.Flat, null, CancellationToken.None);

    [Fact]
    public void Layered_WritesOneFilePerReference()
    {
        var workspace = NewWorkspace();

        Assert.True(WriteLayered(
            workspace,
            Reference("new_sharedmsnweather_08889", "shared_msnweather"),
            Reference("new_sharedteams_777c895a", "shared_teams")));

        Assert.Equal(
            [LayeredPath("new_sharedmsnweather_08889"), LayeredPath("new_sharedteams_777c895a")],
            LayeredFiles(workspace));
    }

    [Fact]
    public void Layered_MatchesTheBytesTheCliWrote()
    {
        var workspace = NewWorkspace();

        WriteLayered(workspace, Reference("new_sharedmsnweather_08889", "shared_msnweather"));

        var expected = string.Join(
            Environment.NewLine,
            "connectionReferences:",
            "  - connectionReferenceLogicalName: new_sharedmsnweather_08889",
            "    connectorId: /providers/Microsoft.PowerApps/apis/shared_msnweather",
            string.Empty);

        Assert.Equal(
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(expected),
            ReadBytes(workspace, LayeredPath("new_sharedmsnweather_08889")));
    }

    [Fact]
    public void Layered_WritesWithoutAByteOrderMark()
    {
        var workspace = NewWorkspace();

        WriteLayered(workspace, Reference("new_sharedteams_777c895a", "shared_teams"));

        Assert.NotEqual(ByteOrderMark, ReadBytes(workspace, LayeredPath("new_sharedteams_777c895a")).Take(3).ToArray());
    }

    [Fact]
    public void Flat_KeepsTheByteOrderMarkClassicWorkspacesCarry()
    {
        var workspace = NewWorkspace();

        WriteFlat(workspace, Reference("new_sharedteams_777c895a", "shared_teams"));

        Assert.Equal(ByteOrderMark, ReadBytes(workspace, ConnectionReferenceStore.FlatFileName).Take(3).ToArray());
    }

    [Fact]
    public void Layered_WhenNothingChanged_ReportsNoChange()
    {
        var workspace = NewWorkspace();
        var reference = Reference("new_sharedteams_777c895a", "shared_teams");

        Assert.True(WriteLayered(workspace, reference));
        Assert.False(WriteLayered(workspace, reference));
    }

    [Fact]
    public void Layered_PrunesAReferenceThatIsNoLongerDeclared()
    {
        var workspace = NewWorkspace();

        WriteLayered(workspace, Reference("new_teams", "shared_teams"), Reference("new_weather", "shared_msnweather"));

        Assert.True(WriteLayered(workspace, Reference("new_teams", "shared_teams")));
        Assert.Equal([LayeredPath("new_teams")], LayeredFiles(workspace));
    }

    [Fact]
    public void Layered_WithNoReference_RemovesEveryFile()
    {
        var workspace = NewWorkspace();

        WriteLayered(workspace, Reference("new_teams", "shared_teams"));

        Assert.True(WriteLayered(workspace));
        Assert.Empty(LayeredFiles(workspace));
    }

    [Fact]
    public void Layered_LeavesNestedFilesAlone()
    {
        var workspace = NewWorkspace();
        var nested = new AgentFilePath($"{ConnectionReferenceStore.LayeredFolderName}/nested/keep{ConnectionReferenceStore.LayeredFileExtension}");

        using (var stream = workspace.OpenWrite(nested))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write("keep me");
        }

        WriteLayered(workspace, Reference("new_teams", "shared_teams"));

        Assert.True(workspace.Exists(nested));
    }

    [Fact]
    public void Layered_KeepsTheLastOfADuplicatedLogicalName()
    {
        var workspace = NewWorkspace();

        WriteLayered(workspace, Reference("New_Teams", "shared_teams"), Reference("new_teams", "shared_msnweather"));

        var written = Assert.Single(LayeredFiles(workspace));

        Assert.Equal(LayeredPath("new_teams"), written);
        Assert.Contains("shared_msnweather", ReadText(workspace, written), StringComparison.Ordinal);
    }

    [Fact]
    public void Layered_SkipsAnInvalidLogicalNameAndStillWritesTheRest()
    {
        var workspace = NewWorkspace();
        var warnings = new List<string>();

        ConnectionReferenceStore.Write(
            workspace,
            [Reference("nested/name", "shared_teams"), Reference("new_teams", "shared_teams")],
            ConnectionReferenceLayout.Layered,
            warnings.Add,
            CancellationToken.None);

        Assert.Equal([LayeredPath("new_teams")], LayeredFiles(workspace));
        Assert.Single(warnings);
    }

    [Fact]
    public void Layered_SuppressesTheFlatFile()
    {
        var workspace = NewWorkspace();

        WriteFlat(workspace, Reference("new_teams", "shared_teams"));

        Assert.True(WriteLayered(workspace, Reference("new_teams", "shared_teams")));
        Assert.False(workspace.Exists(new AgentFilePath(ConnectionReferenceStore.FlatFileName)));
    }

    [Fact]
    public void Flat_LeavesTheLayeredFolderAlone()
    {
        var workspace = NewWorkspace();

        WriteLayered(workspace, Reference("new_teams", "shared_teams"));
        WriteFlat(workspace, Reference("new_teams", "shared_teams"));

        Assert.Equal([LayeredPath("new_teams")], LayeredFiles(workspace));
    }

    [Fact]
    public void Flat_WithNoReference_RemovesTheFile()
    {
        var workspace = NewWorkspace();

        WriteFlat(workspace, Reference("new_teams", "shared_teams"));

        Assert.True(WriteFlat(workspace));
        Assert.False(workspace.Exists(new AgentFilePath(ConnectionReferenceStore.FlatFileName)));
    }

    [Fact]
    public void Read_FindsWhatEitherLayoutDeclares()
    {
        var workspace = NewWorkspace();

        WriteFlat(workspace, Reference("new_flat", "shared_teams"));
        CliAgentConnectionsWriter.WriteAll(workspace, [Reference("new_layered", "shared_msnweather")], null, CancellationToken.None);

        Assert.Equal(
            ["new_flat", "new_layered"],
            ConnectionReferenceStore.Read(workspace, null, CancellationToken.None)
                .Select(reference => reference.ConnectionReferenceLogicalName.Value)
                .OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void Read_PrefersTheLayeredShapeWhenBothDeclareAName()
    {
        var workspace = NewWorkspace();

        WriteFlat(workspace, Reference("new_teams", "shared_teams"));
        CliAgentConnectionsWriter.WriteAll(workspace, [Reference("new_teams", "shared_msnweather")], null, CancellationToken.None);

        var only = Assert.Single(ConnectionReferenceStore.Read(workspace, null, CancellationToken.None));

        Assert.Equal(WorkflowDefinitionFile.ConnectorIdPrefix + "shared_msnweather", only.ConnectorId.Value);
    }

    [Theory]
    [InlineData(ConnectionReferenceLayout.Flat)]
    [InlineData(ConnectionReferenceLayout.Layered)]
    public void Detect_WithoutAnyFile_IsTheFallback(ConnectionReferenceLayout fallback) =>
        Assert.Equal(fallback, ConnectionReferenceStore.Detect(NewWorkspace(), fallback));

    [Fact]
    public void Detect_PrefersWhatIsAlreadyOnDisk()
    {
        var layered = NewWorkspace();
        WriteLayered(layered, Reference("new_teams", "shared_teams"));

        var flat = NewWorkspace();
        WriteFlat(flat, Reference("new_teams", "shared_teams"));

        Assert.Equal(ConnectionReferenceLayout.Layered, ConnectionReferenceStore.Detect(layered, ConnectionReferenceLayout.Flat));
        Assert.Equal(ConnectionReferenceLayout.Flat, ConnectionReferenceStore.Detect(flat, ConnectionReferenceLayout.Layered));
    }

    [Fact]
    public void Migrate_ToLayered_ExplodesTheFlatFileAndRemovesIt()
    {
        var workspace = NewWorkspace();

        WriteFlat(workspace, Reference("new_teams", "shared_teams"), Reference("new_weather", "shared_msnweather"));

        Assert.True(ConnectionReferenceStore.Migrate(workspace, ConnectionReferenceLayout.Layered, null, CancellationToken.None));
        Assert.Equal([LayeredPath("new_teams"), LayeredPath("new_weather")], LayeredFiles(workspace));
        Assert.False(workspace.Exists(new AgentFilePath(ConnectionReferenceStore.FlatFileName)));
    }

    [Fact]
    public void Migrate_ToFlat_FoldsTheLayeredFilesAndRemovesThem()
    {
        var workspace = NewWorkspace();

        WriteLayered(workspace, Reference("new_teams", "shared_teams"), Reference("new_weather", "shared_msnweather"));

        Assert.True(ConnectionReferenceStore.Migrate(workspace, ConnectionReferenceLayout.Flat, null, CancellationToken.None));
        Assert.Empty(LayeredFiles(workspace));

        var written = ReadText(workspace, ConnectionReferenceStore.FlatFileName);

        Assert.Contains("new_teams", written, StringComparison.Ordinal);
        Assert.Contains("new_weather", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Migrate_RoundTripsWithoutLosingAnything()
    {
        var workspace = NewWorkspace();

        WriteFlat(workspace, Reference("new_teams", "shared_teams"), Reference("new_weather", "shared_msnweather"));

        var before = ReadText(workspace, ConnectionReferenceStore.FlatFileName);

        ConnectionReferenceStore.Migrate(workspace, ConnectionReferenceLayout.Layered, null, CancellationToken.None);
        ConnectionReferenceStore.Migrate(workspace, ConnectionReferenceLayout.Flat, null, CancellationToken.None);

        Assert.Equal(before, ReadText(workspace, ConnectionReferenceStore.FlatFileName));
    }

    [Fact]
    public void Migrate_WhenTheLayoutAlreadyMatches_ReportsNoChange()
    {
        var workspace = NewWorkspace();

        WriteLayered(workspace, Reference("new_teams", "shared_teams"));

        Assert.False(ConnectionReferenceStore.Migrate(workspace, ConnectionReferenceLayout.Layered, null, CancellationToken.None));
    }

    [Fact]
    public void Deduplicate_DropsReferencesWithoutALogicalName() =>
        Assert.Empty(ConnectionReferenceStore.Deduplicate([Reference(string.Empty, "shared_teams")]));

    [Fact]
    public void Deduplicate_OfNothing_IsEmpty() => Assert.Empty(ConnectionReferenceStore.Deduplicate(null));

    [Fact]
    public void Write_RequiresAWorkspace() =>
        Assert.Throws<ArgumentNullException>(() => ConnectionReferenceStore.Write(null!, [], ConnectionReferenceLayout.Layered, null, CancellationToken.None));

    [Fact]
    public void Read_RequiresAWorkspace() =>
        Assert.Throws<ArgumentNullException>(() => ConnectionReferenceStore.Read(null!, null, CancellationToken.None));

    [Fact]
    public void Detect_RequiresAWorkspace() =>
        Assert.Throws<ArgumentNullException>(() => ConnectionReferenceStore.Detect(null!, ConnectionReferenceLayout.Layered));
}
