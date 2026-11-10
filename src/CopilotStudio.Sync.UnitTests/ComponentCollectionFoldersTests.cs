// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;
using ProductionFileAccessorFactory = Microsoft.CopilotStudio.McsCore.InMemoryFileAccessorFactory;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public sealed class ComponentCollectionFoldersTests : IDisposable
{
    private const string Agent = "project/Contoso Helpdesk";

    private static readonly DirectoryPath MemorySandbox = new("sandbox/");

    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "mcs-collections-" + Guid.NewGuid().ToString("N"));

    private readonly ProductionFileAccessorFactory _memory = new();

    public void Dispose()
    {
        _memory.Dispose();

        if (Directory.Exists(_sandbox))
        {
            Directory.Delete(_sandbox, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_ReturnsTheDeclaredCollectionsInDeclarationOrder(bool inMemory)
    {
        WriteAgent(inMemory, "../Zeta Tools/", "../Alpha Tools/");
        WriteCollection(inMemory, "project/Alpha Tools");
        WriteCollection(inMemory, "project/Zeta Tools");

        var resolution = Resolve(inMemory);

        Assert.Equal(new[] { Folder(inMemory, "project/Zeta Tools"), Folder(inMemory, "project/Alpha Tools") }, resolution.Found);
        Assert.Empty(resolution.Missing);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_NeverTakesAnUndeclaredSibling(bool inMemory)
    {
        WriteAgent(inMemory);
        WriteCollection(inMemory, "project/Alpha Tools");

        var resolution = Resolve(inMemory);

        Assert.Empty(resolution.Found);
        Assert.Empty(resolution.Missing);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_ReportsADeclarationWithoutACollectionProjectAsMissing(bool inMemory)
    {
        WriteAgent(inMemory, "../Absent Tools/", "../Plain Folder/");
        Write(inMemory, "project/Plain Folder", "settings.mcs.yml", "schemaName: con_plain\n");

        var resolution = Resolve(inMemory);

        Assert.Empty(resolution.Found);
        Assert.Equal(new[] { "../Absent Tools/", "../Plain Folder/" }, resolution.Missing);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_ReportsACollectionOutsideTheProjectAsMissing(bool inMemory)
    {
        WriteAgent(inMemory, "../../Escaped Tools/");
        WriteCollection(inMemory, "Escaped Tools");

        var resolution = Resolve(inMemory);

        Assert.Empty(resolution.Found);
        Assert.Equal(new[] { "../../Escaped Tools/" }, resolution.Missing);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_ReportsADeclarationClimbingAboveTheRootAsMissing(bool inMemory)
    {
        var climb = string.Concat(Enumerable.Repeat("../", 64)) + "Tools/";
        WriteAgent(inMemory, climb);

        Assert.Equal(new[] { climb }, Resolve(inMemory).Missing);
    }

    [Theory]
    [InlineData("../../Escaped Tools/")]
    [InlineData("../Absent Tools/")]
    [InlineData("./../Absent Tools/./")]
    [InlineData("../Alpha Tools/../Absent Tools")]
    [InlineData("../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../Escaped Tools/")]
    public void Resolve_ChecksEachDeclarationWhereResolveRelativeRefPlacesItAndReportsOneOutsideTheProjectOrWithoutACollectionAsMissing(string declared)
    {
        WriteAgent(false, declared);
        WriteCollection(false, "Escaped Tools");
        var checkedFolders = new List<string>();

        var resolution = ComponentCollectionFolders.Resolve(Path.Combine(_sandbox, "project"), Path.Combine(_sandbox, Agent), path =>
        {
            checkedFolders.Add(path);
            return path;
        });

        Assert.Equal(new[] { declared }, resolution.Missing);
        Assert.Equal(Folder(false, Agent).ResolveRelativeRef(new RelativeDirectoryPath(declared)).ToString(), checkedFolders.Last());
    }

    [Fact]
    public void Resolve_FindsACollectionDeclaredByItsAbsolutePathAndReportsOneOnAnotherRootOrShareAsMissing()
    {
        WriteAgent(true, "/elsewhere/Tools/", "//server/share/Tools/");
        WriteAgent(false, Folder(false, "project/Alpha Tools").ToString());
        WriteCollection(false, "project/Alpha Tools");

        Assert.Equal(new[] { "/elsewhere/Tools/", "//server/share/Tools/" }, Resolve(true).Missing);
        Assert.Equal(Folder(false, "project/Alpha Tools"), Assert.Single(Resolve(false).Found));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_ResolvesEachFolderOnceHoweverItIsWritten(bool inMemory)
    {
        WriteAgent(inMemory, "../Alpha Tools/", "../alpha tools/", "..\\Alpha Tools", "./../Alpha Tools/./", "  ../Alpha Tools/  ");
        WriteCollection(inMemory, "project/Alpha Tools");

        var resolution = Resolve(inMemory);

        Assert.Equal(Folder(inMemory, "project/Alpha Tools"), Assert.Single(resolution.Found));
        Assert.Empty(resolution.Missing);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_SkipsADeclarationWithoutADirectory(bool inMemory)
    {
        Write(inMemory, Agent, "references.mcs.yml", "componentCollections:\n  - schemaName: con_tools\n");

        var resolution = Resolve(inMemory);

        Assert.Empty(resolution.Found);
        Assert.Empty(resolution.Missing);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_WithAnUnreadableReferencesFile_FindsNothing(bool inMemory)
    {
        Write(inMemory, Agent, "references.mcs.yml", "componentCollections: [\n");
        WriteCollection(inMemory, "project/Alpha Tools");

        var resolution = Resolve(inMemory);

        Assert.Empty(resolution.Found);
        Assert.Empty(resolution.Missing);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_InAMissingWorkspace_FindsNothing(bool inMemory)
    {
        var resolution = Resolve(inMemory);

        Assert.Empty(resolution.Found);
        Assert.Empty(resolution.Missing);
    }

    [Fact]
    public void Resolve_WithoutARequiredArgument_Throws()
    {
        Assert.Equal("workspaces", Assert.Throws<ArgumentNullException>(() => ComponentCollectionFolders.Resolve(null!, MemorySandbox, MemorySandbox)).ParamName);
    }

    [Fact]
    public void Resolve_ThroughTheCallersResolver_ResolvesTheProjectAndEachDeclaredFolderWithIt()
    {
        WriteAgent(inMemory: false, "../Zeta Tools/", "../Alpha Tools/");
        WriteCollection(inMemory: false, "project/Alpha Tools");
        WriteCollection(inMemory: false, "project/Zeta Tools");
        var project = Path.Combine(_sandbox, "project");
        var resolved = new List<string>();

        var resolution = ComponentCollectionFolders.Resolve(project, Path.Combine(_sandbox, Agent), path =>
        {
            resolved.Add(path);
            return path;
        });

        Assert.Equal(new[] { project, Folder(inMemory: false, "project/Zeta Tools").ToString(), Folder(inMemory: false, "project/Alpha Tools").ToString() }, resolved);
        Assert.Equal(new[] { Folder(inMemory: false, "project/Zeta Tools"), Folder(inMemory: false, "project/Alpha Tools") }, resolution.Found);
        Assert.Empty(resolution.Missing);
    }

    [Fact]
    public void Resolve_ACollectionTheCallersResolverPlacesOutsideTheProject_IsMissing()
    {
        WriteAgent(inMemory: false, "../Zeta Tools/", "../Alpha Tools/");
        WriteCollection(inMemory: false, "project/Alpha Tools");
        WriteCollection(inMemory: false, "project/Zeta Tools");
        var alpha = Folder(inMemory: false, "project/Alpha Tools").ToString();

        var resolution = ComponentCollectionFolders.Resolve(Path.Combine(_sandbox, "project"), Path.Combine(_sandbox, Agent), path => path == alpha ? Path.Combine(_sandbox, "outside") : path);

        Assert.Equal(new[] { Folder(inMemory: false, "project/Zeta Tools") }, resolution.Found);
        Assert.Equal(new[] { "../Alpha Tools/" }, resolution.Missing);
    }

    [Fact]
    public void Resolve_ThroughTheCallersResolverWithoutARequiredArgument_Throws()
    {
        Assert.Equal("projectDirectory", Assert.Throws<ArgumentNullException>(() => ComponentCollectionFolders.Resolve(null!, _sandbox, path => path)).ParamName);
        Assert.Equal("agentWorkspaceDirectory", Assert.Throws<ArgumentNullException>(() => ComponentCollectionFolders.Resolve(_sandbox, null!, path => path)).ParamName);
        Assert.Equal("resolvePhysicalPath", Assert.Throws<ArgumentNullException>(() => ComponentCollectionFolders.Resolve(_sandbox, _sandbox, null!)).ParamName);
    }

    [Fact]
    public async Task GetOtherLinkedAgentsAsync_ReadsEveryCollectionInOneQueryAndLeavesThePushedAgentOut()
    {
        var agentId = Guid.NewGuid();
        var otherAgent = new AgentInfo { AgentId = Guid.NewGuid(), SchemaName = "con_other" };
        WriteCollection(inMemory: true, "project/Alpha Tools", "con_alpha");
        WriteCollection(inMemory: true, "project/Zeta Tools", "con_zeta");
        WriteCollection(inMemory: true, "project/Unlinked Tools", "con_unlinked");
        var dataverse = new Mock<ISyncDataverseClient>();
        var links = dataverse.As<ISyncComponentCollectionLinkDataverseClient>();
        links.Setup(client => client.GetLinkedAgentsByComponentCollectionSchemaNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IReadOnlyList<AgentInfo>>(StringComparer.OrdinalIgnoreCase)
            {
                ["CON_ALPHA"] = new[] { new AgentInfo { AgentId = agentId, SchemaName = "con_agent" }, otherAgent },
                ["con_zeta"] = new[] { new AgentInfo { AgentId = agentId, SchemaName = "con_agent" } },
            });

        var linked = await ComponentCollectionFolders.GetOtherLinkedAgentsAsync(_memory, new[] { Folder(true, "project/Alpha Tools"), Folder(true, "project/Zeta Tools"), Folder(true, "project/Unlinked Tools"), Folder(true, "project/Absent Tools") }, agentId, dataverse.Object, CancellationToken.None);

        Assert.Equal(new[] { "con_alpha", "con_zeta", "con_unlinked" }, linked.Select(collection => collection.SchemaName));
        Assert.Equal(Folder(true, "project/Alpha Tools"), linked[0].Folder);
        Assert.Same(otherAgent, Assert.Single(linked[0].OtherAgents));
        Assert.Empty(linked[1].OtherAgents);
        Assert.Empty(linked[2].OtherAgents);
        links.Verify(client => client.GetLinkedAgentsByComponentCollectionSchemaNamesAsync(It.Is<IEnumerable<string>>(names => names.SequenceEqual(new[] { "con_alpha", "con_zeta", "con_unlinked" })), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetOtherLinkedAgentsAsync_WithoutACollectionToRead_AsksNothing()
    {
        var dataverse = new Mock<ISyncDataverseClient>();
        var links = dataverse.As<ISyncComponentCollectionLinkDataverseClient>();

        Assert.Empty(await ComponentCollectionFolders.GetOtherLinkedAgentsAsync(_memory, new[] { Folder(true, "project/Absent Tools") }, Guid.NewGuid(), dataverse.Object, CancellationToken.None));
        links.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetOtherLinkedAgentsAsync_ThroughAClientThatCannotQueryLinks_Throws()
    {
        Assert.Contains("component collection link", (await Assert.ThrowsAsync<InvalidOperationException>(() => ComponentCollectionFolders.GetOtherLinkedAgentsAsync(_memory, Array.Empty<DirectoryPath>(), Guid.NewGuid(), new Mock<ISyncDataverseClient>().Object, CancellationToken.None))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOtherLinkedAgentsAsync_WithoutARequiredArgument_Throws()
    {
        var dataverse = new Mock<ISyncDataverseClient>().Object;

        Assert.Equal("workspaces", (await Assert.ThrowsAsync<ArgumentNullException>(() => ComponentCollectionFolders.GetOtherLinkedAgentsAsync(null!, Array.Empty<DirectoryPath>(), Guid.Empty, dataverse, CancellationToken.None))).ParamName);
        Assert.Equal("collectionFolders", (await Assert.ThrowsAsync<ArgumentNullException>(() => ComponentCollectionFolders.GetOtherLinkedAgentsAsync(_memory, null!, Guid.Empty, dataverse, CancellationToken.None))).ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadSchemaNames_MapsEachSchemaNameToItsFolderInReadOrder(bool inMemory)
    {
        WriteCollection(inMemory, "project/Zeta Tools", "cree9_zeta");
        WriteCollection(inMemory, "project/Alpha Tools", "cree9_alpha");

        var names = ComponentCollectionFolders.ReadSchemaNames(Collections(inMemory, "project/Zeta Tools", "project/Alpha Tools"));

        Assert.Equal(new[] { new KeyValuePair<string, string>("cree9_zeta", "project/Zeta Tools"), new KeyValuePair<string, string>("cree9_alpha", "project/Alpha Tools") }, names.Folders);
        Assert.Equal("project/Alpha Tools", names.Folders["CREE9_ALPHA"]);
        Assert.Empty(names.Problems);
    }

    [Theory]
    [InlineData("")]
    [InlineData("displayName: Shared Tools\n")]
    [InlineData("schemaName: \"  \"\n")]
    [InlineData("kind: AdaptiveDialog\nbeginDialog: x\n")]
    public void ReadSchemaNames_RefusesAFolderWhoseProjectNamesNoSchemaName(string collectionProject)
    {
        Write(true, "project/Shared Tools", "collection.mcs.yml", collectionProject);
        WriteCollection(true, "project/Zeta Tools", "cree9_zeta");

        var names = ComponentCollectionFolders.ReadSchemaNames(Collections(true, "project/Shared Tools", "project/Zeta Tools"));

        Assert.Equal(new[] { new ComponentCollectionFolderProblem(ComponentCollectionFolderProblemKind.SchemaNameMissing, "project/Shared Tools", null, null) }, names.Problems);
        Assert.Equal(new[] { "cree9_zeta" }, names.Folders.Keys);
    }

    [Fact]
    public void ReadSchemaNames_RefusesAFolderWithoutAProject()
    {
        Write(true, "project/Empty Tools", "topics/Lookup.mcs.yml", "kind: AdaptiveDialog\n");

        var names = ComponentCollectionFolders.ReadSchemaNames(Collections(true, "project/Empty Tools"));

        Assert.Equal(new[] { new ComponentCollectionFolderProblem(ComponentCollectionFolderProblemKind.SchemaNameMissing, "project/Empty Tools", null, null) }, names.Problems);
        Assert.Empty(names.Folders);
    }

    [Fact]
    public void ReadSchemaNames_RefusesEveryLaterFolderRepeatingASchemaNameIgnoringCaseAndNamesTheFirst()
    {
        WriteCollection(true, "project/Alpha Tools", "cree9_tools");
        WriteCollection(true, "project/Beta Tools", "CREE9_TOOLS");
        WriteCollection(true, "project/Gamma Tools", "Cree9_Tools");

        var names = ComponentCollectionFolders.ReadSchemaNames(Collections(true, "project/Alpha Tools", "project/Beta Tools", "project/Gamma Tools"));

        Assert.Equal(new[] { new ComponentCollectionFolderProblem(ComponentCollectionFolderProblemKind.SchemaNameRepeated, "project/Beta Tools", "CREE9_TOOLS", "project/Alpha Tools"), new ComponentCollectionFolderProblem(ComponentCollectionFolderProblemKind.SchemaNameRepeated, "project/Gamma Tools", "Cree9_Tools", "project/Alpha Tools") }, names.Problems);
        Assert.Equal(new[] { new KeyValuePair<string, string>("cree9_tools", "project/Alpha Tools") }, names.Folders);
    }

    [Fact]
    public void ReadSchemaNames_RefusesAFolderWhoseProjectCannotBeRead()
    {
        var unreadable = new Mock<IFileAccessor>();
        unreadable.Setup(accessor => accessor.Exists(It.IsAny<AgentFilePath>())).Returns(true);
        unreadable.Setup(accessor => accessor.OpenRead(It.IsAny<AgentFilePath>())).Throws(new IOException("The file is locked."));

        Assert.Equal(ComponentCollectionFolderProblemKind.SchemaNameMissing, Assert.Single(ComponentCollectionFolders.ReadSchemaNames(new[] { new KeyValuePair<string, IFileAccessor>("Locked Tools", unreadable.Object) }).Problems).Kind);
    }

    [Fact]
    public void ReadSchemaNames_OfNoFolder_MapsNothing()
    {
        var names = ComponentCollectionFolders.ReadSchemaNames(Array.Empty<KeyValuePair<string, IFileAccessor>>());

        Assert.Empty(names.Folders);
        Assert.Empty(names.Problems);
    }

    [Fact]
    public void ReadSchemaNames_WithoutFoldersOrAWorkspace_Throws()
    {
        Assert.Equal("collectionFolders", Assert.Throws<ArgumentNullException>(() => ComponentCollectionFolders.ReadSchemaNames((IEnumerable<KeyValuePair<string, IFileAccessor>>)null!)).ParamName);
        Assert.Equal("collectionFolders", Assert.Throws<ArgumentException>(() => ComponentCollectionFolders.ReadSchemaNames(new[] { new KeyValuePair<string, IFileAccessor>("Tools", null!) })).ParamName);
    }

    [Fact]
    public void ReadSchemaNames_OfDirectoriesOnDisk_MapsEachSchemaNameToItsDirectoryAsGivenInReadOrder()
    {
        WriteCollection(inMemory: false, "project/Zeta Tools", "cree9_zeta");
        WriteCollection(inMemory: false, "project/Alpha Tools", "cree9_alpha");
        var zeta = Path.Combine(_sandbox, "project", "Zeta Tools");
        var alpha = Path.Combine(_sandbox, "project", "Alpha Tools") + Path.DirectorySeparatorChar;

        var names = ComponentCollectionFolders.ReadSchemaNames(new[] { zeta, alpha });

        Assert.Equal(new[] { new KeyValuePair<string, string>("cree9_zeta", zeta), new KeyValuePair<string, string>("cree9_alpha", alpha) }, names.Folders);
        Assert.Empty(names.Problems);
    }

    [Fact]
    public void ReadSchemaNames_OfDirectoriesOnDisk_RefusesOneWithoutACollectionFileOrASchemaName()
    {
        Directory.CreateDirectory(Path.Combine(_sandbox, "project", "Empty Tools"));
        Write(inMemory: false, "project/Plain Tools", "collection.mcs.yml", "displayName: Plain Tools\n");
        WriteCollection(inMemory: false, "project/Zeta Tools", "cree9_zeta");
        var absent = Path.Combine(_sandbox, "project", "Absent Tools");
        var empty = Path.Combine(_sandbox, "project", "Empty Tools");
        var plain = Path.Combine(_sandbox, "project", "Plain Tools");
        var zeta = Path.Combine(_sandbox, "project", "Zeta Tools");

        var names = ComponentCollectionFolders.ReadSchemaNames(new[] { absent, empty, plain, zeta });

        Assert.Equal(new[] { new ComponentCollectionFolderProblem(ComponentCollectionFolderProblemKind.SchemaNameMissing, absent, null, null), new ComponentCollectionFolderProblem(ComponentCollectionFolderProblemKind.SchemaNameMissing, empty, null, null), new ComponentCollectionFolderProblem(ComponentCollectionFolderProblemKind.SchemaNameMissing, plain, null, null) }, names.Problems);
        Assert.Equal(new[] { new KeyValuePair<string, string>("cree9_zeta", zeta) }, names.Folders);
    }

    [Fact]
    public void ReadSchemaNames_OfDirectoriesOnDisk_RefusesOneWhoseCollectionFileCannotBeRead()
    {
        WriteCollection(inMemory: false, "project/Locked Tools", "cree9_locked");
        var locked = Path.Combine(_sandbox, "project", "Locked Tools");
        using var hold = new FileStream(Path.Combine(locked, "collection.mcs.yml"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var names = ComponentCollectionFolders.ReadSchemaNames(new[] { locked });

        Assert.Equal(new ComponentCollectionFolderProblem(ComponentCollectionFolderProblemKind.SchemaNameMissing, locked, null, null), Assert.Single(names.Problems));
        Assert.Empty(names.Folders);
    }

    [Fact]
    public void ReadSchemaNames_OfDirectoriesOnDisk_RefusesALaterDirectoryRepeatingASchemaNameIgnoringCaseAndNamesTheFirst()
    {
        WriteCollection(inMemory: false, "project/Alpha Tools", "cree9_tools");
        WriteCollection(inMemory: false, "project/Beta Tools", "CREE9_TOOLS");
        var alpha = Path.Combine(_sandbox, "project", "Alpha Tools");
        var beta = Path.Combine(_sandbox, "project", "Beta Tools");

        var names = ComponentCollectionFolders.ReadSchemaNames(new[] { alpha, beta });

        Assert.Equal(new[] { new ComponentCollectionFolderProblem(ComponentCollectionFolderProblemKind.SchemaNameRepeated, beta, "CREE9_TOOLS", alpha) }, names.Problems);
        Assert.Equal(new[] { new KeyValuePair<string, string>("cree9_tools", alpha) }, names.Folders);
    }

    [Fact]
    public void ReadSchemaNames_WithoutDirectoriesOrWithANullDirectory_Throws()
    {
        Assert.Equal("collectionDirectories", Assert.Throws<ArgumentNullException>(() => ComponentCollectionFolders.ReadSchemaNames((IEnumerable<string>)null!)).ParamName);
        Assert.Equal("collectionDirectories", Assert.Throws<ArgumentException>(() => ComponentCollectionFolders.ReadSchemaNames(new string[] { null! })).ParamName);
    }

    private IEnumerable<KeyValuePair<string, IFileAccessor>> Collections(bool inMemory, params string[] folders) => folders.Select(folder => new KeyValuePair<string, IFileAccessor>(folder, inMemory ? _memory.Create(Folder(true, folder)) : new FileAccessorFactory().Create(Folder(false, folder)))).ToArray();

    private void WriteAgent(bool inMemory, params string[] declaredDirectories)
    {
        Write(inMemory, Agent, "settings.mcs.yml", "schemaName: con_agent\n");

        if (declaredDirectories.Length > 0)
        {
            Write(inMemory, Agent, "references.mcs.yml", "componentCollections:\n" + string.Concat(declaredDirectories.Select(directory => $"  - schemaName:\n    directory: {directory}\n")));
        }
    }

    private void WriteCollection(bool inMemory, string folder, string schemaName = "con_tools") => Write(inMemory, folder, "collection.mcs.yml", $"schemaName: {schemaName}\n");

    private void Write(bool inMemory, string folder, string fileName, string content)
    {
        if (inMemory)
        {
            using var stream = _memory.Create(MemorySandbox.GetChildDirectoryPath(folder + "/")).OpenWrite(new AgentFilePath(fileName));
            stream.Write(Encoding.UTF8.GetBytes(content));
            return;
        }

        Directory.CreateDirectory(Path.Combine(_sandbox, folder));
        File.WriteAllText(Path.Combine(_sandbox, folder, fileName), content);
    }

    private ComponentCollectionFolderResolution Resolve(bool inMemory) => inMemory
        ? ComponentCollectionFolders.Resolve(_memory, MemorySandbox.GetChildDirectoryPath("project/"), Folder(true, Agent))
        : ComponentCollectionFolders.Resolve(Path.Combine(_sandbox, "project"), Path.Combine(_sandbox, Agent), path => path);

    private DirectoryPath Folder(bool inMemory, string folder) => inMemory ? MemorySandbox.GetChildDirectoryPath(folder + "/") : new DirectoryPath(Path.Combine(_sandbox, folder).Replace('\\', '/') + "/");
}
