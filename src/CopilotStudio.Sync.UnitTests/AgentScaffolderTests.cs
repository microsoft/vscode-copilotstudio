// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;
using ProductionFileAccessor = Microsoft.CopilotStudio.McsCore.InMemoryFileAccessor;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public sealed class AgentScaffolderTests : IDisposable
{
    internal const string EscapedDisplayName = "Contoso \"Help\": Desk #1 \\ Ops";

    internal const string EscapedSchemaName = "con_a{x}!";

    internal const string EscapedInstructions = "Say \"hello\"\nthen stop: always, #1 priority\r\n- back\\slash";

    private const string GoldenResourcePrefix = "Microsoft.CopilotStudio.Sync.UnitTests.TestData.AgentScaffold.";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mcs-agent-scaffold-" + Guid.NewGuid().ToString("N"));

    private readonly ProductionFileAccessor _workspace = new();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    public static TheoryData<string, string, string, string, string> GoldenScaffolds() => new()
    {
        { "ClassicPlain", "classic", "Contoso Helpdesk", "con_contosohelpdesk", "Be helpful." },
        { "ClassicEscaped", "classic", EscapedDisplayName, EscapedSchemaName, EscapedInstructions },
        { "CliCopilotPlain", "cli-copilot", "Contoso Helpdesk", "con_contosohelpdesk", "Be helpful." },
        { "CliCopilotEscaped", "cli-copilot", EscapedDisplayName, EscapedSchemaName, EscapedInstructions },
    };

    private IReadOnlyList<string> Scaffold(string templateFolder = "cli-copilot", string displayName = "Contoso Helpdesk", string schemaName = "con_contosohelpdesk", string instructions = "Be helpful.") =>
        AgentScaffolder.Scaffold(_workspace, templateFolder, displayName, schemaName, instructions);

    private string Read(string relativePath) => _workspace.ReadStringAsync(new AgentFilePath(relativePath), CancellationToken.None).GetAwaiter().GetResult();

    private BotEntity Settings() => CliAgentBotEntityReader.Read(_workspace);

    private string? Instructions() => Assert.IsType<StaticSegment>(Assert.Single(Settings().Configuration!.AgentSettings!.Instructions!.Segments)).Value;

    private static byte[] UpToLineEndings(string path, byte[] content) => path.EndsWith(".png", StringComparison.Ordinal) ? content : Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(content).Replace("\r\n", "\n", StringComparison.Ordinal));

    internal static byte[] Golden(string goldenFolder, string relativePath)
    {
        using var stream = typeof(AgentScaffolderTests).Assembly.GetManifestResourceStream($"{GoldenResourcePrefix}{goldenFolder}.{relativePath.Replace('/', '.')}") ?? throw new FileNotFoundException($"No golden file for {goldenFolder}/{relativePath}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return buffer.ToArray();
    }

    internal static int GoldenFileCount(string goldenFolder) =>
        typeof(AgentScaffolderTests).Assembly.GetManifestResourceNames().Count(name => name.StartsWith($"{GoldenResourcePrefix}{goldenFolder}.", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(GoldenScaffolds))]
    public void Scaffold_InMemory_MatchesTheGoldenScaffoldUpToLineEndings(string goldenFolder, string templateFolder, string displayName, string schemaName, string instructions)
    {
        var created = AgentScaffolder.Scaffold(_workspace, templateFolder, displayName, schemaName, instructions);

        Assert.Equal(GoldenFileCount(goldenFolder), created.Count);
        Assert.Equal(created.Count, _workspace.Count);
        Assert.All(created, path => Assert.Equal(Golden(goldenFolder, path), UpToLineEndings(path, _workspace.ReadBytesAsync(new AgentFilePath(path), CancellationToken.None).GetAwaiter().GetResult())));
    }

    [Theory]
    [MemberData(nameof(GoldenScaffolds))]
    public void Scaffold_OnDisk_MatchesTheGoldenScaffoldUpToLineEndings(string goldenFolder, string templateFolder, string displayName, string schemaName, string instructions)
    {
        var created = AgentScaffolder.Scaffold(_directory, templateFolder, displayName, schemaName, instructions);

        Assert.Equal(GoldenFileCount(goldenFolder), created.Count);
        Assert.Equal(created.Count, Directory.GetFiles(_directory, "*", SearchOption.AllDirectories).Length);
        Assert.All(created, path => Assert.Equal(Golden(goldenFolder, path), UpToLineEndings(path, File.ReadAllBytes(Path.Combine(_directory, path.Replace('/', Path.DirectorySeparatorChar))))));
    }

    [Theory]
    [InlineData("classic", "Contoso Helpdesk", "con_contosohelpdesk")]
    [InlineData("classic", EscapedDisplayName, EscapedSchemaName)]
    [InlineData("cli-copilot", "Contoso Helpdesk", "con_contosohelpdesk")]
    [InlineData("cli-copilot", EscapedDisplayName, EscapedSchemaName)]
    public void Scaffold_WritesSettingsThatReadBackAsTheIdentityItWasGiven(string templateFolder, string displayName, string schemaName)
    {
        Scaffold(templateFolder, displayName, schemaName);

        Assert.Equal((displayName, schemaName), (Settings().DisplayName, Settings().SchemaName.Value));
    }

    [Theory]
    [InlineData("cli-copilot")]
    [InlineData("CLI-Copilot")]
    public void Scaffold_CliCopilot_WritesExactlyTheTwoFilesPacDoes(string templateFolder)
    {
        Assert.Equal(["agent.sync.yaml", "settings.mcs.yml"], Scaffold(templateFolder));
    }

    [Fact]
    public void Scaffold_CliCopilot_WritesNoAgentMcsYml()
    {
        Scaffold();

        Assert.False(_workspace.Exists(new AgentFilePath("agent.mcs.yml")));
    }

    [Theory]
    [InlineData("authoringModel: CliCopilot")]
    [InlineData("kind: CLICopilotRecognizer")]
    [InlineData("series: Sonnet46")]
    [InlineData("accessControlPolicy: GroupMembership")]
    [InlineData("authenticationTrigger: Always")]
    [InlineData("template: cliagent-1.0.0")]
    [InlineData("language: 1033")]
    public void Scaffold_CliCopilot_DeclaresTheShape(string expected)
    {
        Scaffold();

        Assert.Contains(expected, Read("settings.mcs.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public void Scaffold_CliCopilot_WritesInstructionsAsAStaticSegment()
    {
        Scaffold();

        Assert.Equal("Be helpful.", Instructions());
    }

    [Fact]
    public void Scaffold_ResolvesTheLayoutVersionFromTheSdk()
    {
        Scaffold();

        Assert.Equal(AgentClassifier.BuildWorkspaceLayoutMarker(), Read("agent.sync.yaml"));
    }

    [Theory]
    [InlineData("Say \"hello\"\nthen stop.")]
    [InlineData("Escalate: always, #1 priority")]
    [InlineData("- leading dash")]
    [InlineData("back\\slash")]
    [InlineData("windows\r\nline")]
    public void Scaffold_CliCopilot_KeepsInstructionsThatWouldBreakTheDocument(string instructions)
    {
        Scaffold(instructions: instructions);

        Assert.Equal(instructions.Replace("\r\n", "\n", StringComparison.Ordinal), Instructions());
    }

    [Fact]
    public void Scaffold_WithoutInstructions_WritesAStaticSegmentWithoutText()
    {
        Scaffold(instructions: string.Empty);

        Assert.Null(Instructions());
    }

    [Theory]
    [InlineData("Say \"hello\"\nthen stop.")]
    [InlineData("Escalate: always, #1 priority")]
    [InlineData("- leading dash")]
    [InlineData("back\\slash")]
    public void Scaffold_Classic_EscapesInputThatWouldBreakTheDocument(string instructions)
    {
        Scaffold("classic", instructions: instructions);

        var line = Read("agent.mcs.yml").Split('\n').Single(candidate => candidate.StartsWith("instructions:", StringComparison.Ordinal)).TrimEnd('\r');

        Assert.StartsWith("instructions: \"", line, StringComparison.Ordinal);
        Assert.EndsWith("\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', line);
    }

    [Theory]
    [InlineData("Contoso: Helpdesk")]
    [InlineData("Contoso \"Helpdesk\"")]
    [InlineData("Contoso #1")]
    public void Scaffold_Classic_KeepsADisplayNameThatWouldBreakTheDocument(string displayName)
    {
        Scaffold("classic", displayName: displayName);

        Assert.Equal(displayName, Settings().DisplayName);
    }

    [Fact]
    public void Scaffold_Classic_WithoutInstructions_StillWritesAValidScalar()
    {
        Scaffold("classic", instructions: string.Empty);

        Assert.Contains("instructions: \"\"", Read("agent.mcs.yml"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("con_agent")]
    [InlineData("!agent")]
    [InlineData("{agent}")]
    [InlineData("con_a{x}!")]
    [InlineData("-agent")]
    public void Scaffold_Classic_DeclaresTheGptComponentOfAnySchemaName(string schemaName)
    {
        Scaffold("classic", schemaName: schemaName);

        Assert.Equal($"{schemaName}.gpt.default", Settings().Configuration!.GPTSettings!.DefaultSchemaName);
    }

    [Theory]
    [InlineData("!agent")]
    [InlineData("{agent}")]
    public void Scaffold_Classic_QuotesTopicDialogReferences(string schemaName)
    {
        Scaffold("classic", schemaName: schemaName);

        Assert.EndsWith($"dialog: \"{schemaName}.topic.Escalate\"", Read("topics/Fallback.mcs.yml").Split('\n').Single(candidate => candidate.TrimStart().StartsWith("dialog:", StringComparison.Ordinal)).TrimEnd('\r'), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cli-copilot")]
    [InlineData("classic")]
    public void Scaffold_LeavesNoUnsubstitutedTokens(string templateFolder)
    {
        foreach (var path in Scaffold(templateFolder).Where(path => !path.EndsWith(".png", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain('$', Read(path));
        }
    }

    [Fact]
    public void Scaffold_Classic_WritesTheFullTemplateWithNestedTopics()
    {
        var created = Scaffold("classic");

        Assert.Equal(16, created.Count);
        Assert.Contains("icon.png", created);
        Assert.Contains("agent.mcs.yml", created);
        Assert.Contains("settings.mcs.yml", created);
        Assert.Equal(13, created.Count(path => path.StartsWith("topics/", StringComparison.Ordinal)));
        Assert.Equal(created.OrderBy(path => path, StringComparer.Ordinal), created);
    }

    [Fact]
    public void Scaffold_Classic_StripsTheSubstitutionPrefixFromNestedFiles()
    {
        var created = Scaffold("classic");

        Assert.DoesNotContain(created, path => path.Contains("template_", StringComparison.Ordinal));
        Assert.Contains("topics/Fallback.mcs.yml", created);
        Assert.Contains("topics/Greeting.mcs.yml", created);
        Assert.True(_workspace.Exists(new AgentFilePath("topics/Fallback.mcs.yml")));
    }

    [Fact]
    public void Scaffold_Classic_SubstitutesNestedTopicTokens()
    {
        Scaffold("classic");

        Assert.Contains("con_contosohelpdesk", Read("topics/Fallback.mcs.yml"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("kind: GptComponentMetadata")]
    [InlineData("webBrowsing: true")]
    public void Scaffold_Classic_KeepsTheGptComponent(string expected)
    {
        Scaffold("classic");

        Assert.Contains(expected, Read("agent.mcs.yml"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("kind: GenerativeAIRecognizer")]
    [InlineData("template: empty-1.0.0")]
    [InlineData("GenerativeActionsEnabled: true")]
    public void Scaffold_Classic_DeclaresTheClassicSettings(string expected)
    {
        Scaffold("classic");

        Assert.Contains(expected, Read("settings.mcs.yml"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AgentAuthoringModes.Classic)]
    [InlineData(AgentAuthoringModes.CliCopilot)]
    public async Task Scaffold_DeclaresTheTemplateItsBotIsCreatedWith(string templateFolder)
    {
        string? sentTemplate = null;
        var accessor = new Mock<IDataverseHttpClientAccessor>();
        accessor.Setup(clientAccessor => clientAccessor.CreateClient()).Returns(() => new HttpClient(new ConnectionReferenceChunkingTests.StubHandler(async (request, cancellationToken) =>
        {
            sentTemplate = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!["template"]!.GetValue<string>();

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"botid\":\"{Guid.NewGuid()}\"}}", Encoding.UTF8, "application/json") };
        })));
        var client = new SyncDataverseClient(accessor.Object);
        client.SetDataverseUrl("https://test.crm.dynamics.com");
        Scaffold(templateFolder);

        await client.CreateNewAgentAsync("Contoso Helpdesk", "con_contosohelpdesk", AgentClassifier.DetectAuthoringShapeFromFolder(_workspace), CancellationToken.None);

        Assert.Equal(Settings().Template, sentTemplate);
    }

    [Fact]
    public void Scaffold_Classic_CopiesTheIconVerbatim()
    {
        Scaffold("classic");

        var icon = _workspace.ReadBytesAsync(new AgentFilePath("icon.png"), CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(14577, icon.Length);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], icon[..4]);
    }

    [Fact]
    public void Scaffold_CliCopilot_ClassifiesAsACliAuthoredWorkspace()
    {
        Scaffold();

        Assert.Equal(WorkspaceLayout.CliLayered, AgentClassifier.DetectWorkspaceLayout(_workspace));
        Assert.Equal(AuthoringShape.CliCopilot, AgentClassifier.DetectAuthoringShapeFromFolder(_workspace));
    }

    [Fact]
    public void Scaffold_Classic_ClassifiesAsAClassicWorkspace()
    {
        Scaffold("classic");

        Assert.Equal(AuthoringShape.Classic, AgentClassifier.DetectAuthoringShapeFromFolder(_workspace));
    }

    [Fact]
    public void Scaffold_OnDisk_ClassifiesTheWorkspaceItWrote()
    {
        AgentScaffolder.Scaffold(_directory, "cli-copilot", "Contoso Helpdesk", "con_contosohelpdesk", "Be helpful.");

        Assert.Equal(WorkspaceLayout.CliLayered, AgentClassifier.DetectWorkspaceLayout(_directory));
        Assert.Equal(AuthoringShape.CliCopilot, AgentClassifier.DetectAuthoringShapeFromFolder(_directory));
    }

    [Theory]
    [InlineData("does-not-exist")]
    [InlineData("")]
    [InlineData("mcs-workflow")]
    [InlineData("../mcs-workflow")]
    public void Scaffold_WithAnUnknownTemplate_IsRejectedAndWritesNothing(string templateFolder)
    {
        var inMemory = Assert.Throws<AgentScaffoldException>(() => Scaffold(templateFolder));
        var onDisk = Assert.Throws<AgentScaffoldException>(() => AgentScaffolder.Scaffold(_directory, templateFolder, "Contoso", "con_contoso", string.Empty));

        Assert.Equal(AgentScaffoldFailure.TemplateNotFound, inMemory.Failure);
        Assert.Equal(AgentScaffoldFailure.TemplateNotFound, onDisk.Failure);
        Assert.Equal(templateFolder, inMemory.Subject);
        Assert.Equal(0, _workspace.Count);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void Scaffold_OnDisk_WithADisplayNameLongerThanTheLimit_IsRejectedBeforeCreatingTheDirectory()
    {
        var displayName = new string('a', AgentWorkspaceIdentity.MaximumDisplayNameLength + 1);

        var failure = Assert.Throws<AgentScaffoldException>(() => AgentScaffolder.Scaffold(_directory, "classic", displayName, "con_contoso", "Be helpful."));

        Assert.Equal((AgentScaffoldFailure.DisplayNameTooLong, displayName), (failure.Failure, failure.Subject));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void Scaffold_RefusesMissingArguments()
    {
        Assert.Throws<ArgumentNullException>(() => AgentScaffolder.Scaffold((IFileAccessor)null!, "classic", "a", "b", "c"));
        Assert.Throws<ArgumentNullException>(() => AgentScaffolder.Scaffold(_workspace, "classic", null!, "b", "c"));
        Assert.Throws<ArgumentNullException>(() => AgentScaffolder.Scaffold(_workspace, "classic", "a", null!, "c"));
        Assert.Throws<ArgumentNullException>(() => AgentScaffolder.Scaffold(_workspace, "classic", "a", "b", null!));
        Assert.Throws<ArgumentException>(() => AgentScaffolder.Scaffold(" ", "classic", "a", "b", "c"));
        Assert.Equal(0, _workspace.Count);
    }
}
