// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.CopilotStudio.McsCore;
using Xunit;
using ProductionFileAccessor = Microsoft.CopilotStudio.McsCore.InMemoryFileAccessor;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public sealed class DeclaredConnectionReferencesFuzzTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mcs-declared-fuzz-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private IFileAccessor Workspace(bool onDisk)
    {
        if (!onDisk)
        {
            return new ProductionFileAccessor();
        }

        Directory.CreateDirectory(_directory);

        return WorkflowWorkspace.OpenWorkspace(_directory);
    }

    private static string Write(IFileAccessor workspace, string content, string fileName = "probe.sync.yaml")
    {
        var path = $"infrastructure/connections/{fileName}";
        using var stream = workspace.OpenWrite(new AgentFilePath(path));
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);

        return path;
    }

    private static string Nested(int depth) =>
        "connectionReferences:" + string.Concat(Enumerable.Range(0, depth).Select(level => $"\n{new string(' ', level * 2)}- a:"));

    private static string AliasChain(int depth, int width)
    {
        var lines = new List<string> { "seed: &a0 [x]" };

        for (var level = 1; level <= depth; level++)
        {
            lines.Add($"level{level}: &a{level} [{string.Join(", ", Enumerable.Repeat($"*a{level - 1}", width))}]");
        }

        lines.Add($"connectionReferences: *a{depth}");

        return string.Join("\n", lines);
    }

    private static readonly string[] Corpus =
    [
        string.Empty,
        "   ",
        "\0",
        "\uFFFF\uFFFE",
        "\ufeffconnectionReferences:\n  - connectionReferenceLogicalName: cr_bom",
        "not yaml at all: [unclosed",
        "connectionReferences: [",
        "connectionReferences:\n\t- connectionReferenceLogicalName: cr_tab",
        "connectionReferences: 5",
        "connectionReferences: \"text\"",
        "connectionReferences: {}",
        "connectionReferences:\n  - 5",
        "connectionReferences:\n  - null",
        "connectionReferences:\n  - connectionReferenceLogicalName:",
        "connectionReferences:\n  - connectionReferenceLogicalName: \"\"",
        "connectionReferences:\n  - connectionReferenceLogicalName: \"   \"",
        "connectionReferences:\n  - connectorId: /providers/Microsoft.PowerApps/apis/shared_teams",
        "connectionReferences:\n  - connectionReferenceLogicalName: cr_a\n  - connectionReferenceLogicalName: cr_a",
        "connectionReferences:\n  - connectionReferenceLogicalName: cr_a\n    connectionReferenceLogicalName: cr_b",
        "connectionReferences:\n  - connectionReferenceLogicalName: " + new string('n', 200_000),
        "connectionReferences:\n  - connectionReferenceLogicalName: \"cr_\\uD800\"",
        "connectionReferences:\n  - connectionReferenceLogicalName: \"cr_\\u0000\"",
        "connectionReferences:\n  - connectionReferenceLogicalName: cr_cr\r    connectorId: x",
        "connectionReferences:\n  - connectionReferenceLogicalName: !!python/object/apply:os.system [echo]",
        "--- \n--- \nconnectionReferences:\n  - connectionReferenceLogicalName: cr_two_docs",
        "connectionReferences:\n  - connectionReferenceLogicalName: cr_a\nconnectionReferences:\n  - connectionReferenceLogicalName: cr_b",
        Nested(256),
        AliasChain(depth: 6, width: 3),
    ];

    public static TheoryData<int, bool> MalformedDeclarations()
    {
        var data = new TheoryData<int, bool>();

        for (var index = 0; index < Corpus.Length; index++)
        {
            data.Add(index, false);
            data.Add(index, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(MalformedDeclarations))]
    public void ADeclarationFileIsEitherReadOrTypedAsUnreadable(int corpusIndex, bool onDisk)
    {
        var workspace = Workspace(onDisk);
        var path = Write(workspace, Corpus[corpusIndex]);

        try
        {
            foreach (var reference in DeclaredConnectionReferences.Read(workspace))
            {
                Assert.False(string.IsNullOrWhiteSpace(reference.ConnectionReferenceLogicalName.Value));
            }
        }
        catch (DeclaredConnectionReferenceException failure)
        {
            Assert.Equal(DeclaredConnectionReferenceFailure.Unreadable, failure.Failure);
            Assert.Equal(path, failure.Path);
            Assert.Contains(path, failure.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(MalformedDeclarations))]
    public void ADeclarationFileNeverEchoesItsWholeContent(int corpusIndex, bool onDisk)
    {
        var workspace = Workspace(onDisk);
        Write(workspace, Corpus[corpusIndex]);

        try
        {
            DeclaredConnectionReferences.Read(workspace);
        }
        catch (DeclaredConnectionReferenceException failure)
        {
            Assert.DoesNotContain(new string('n', 1_000), failure.Message, StringComparison.Ordinal);
            Assert.True(failure.Message.Length < 4_000);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFileNameTheWorkspaceChoseIsNeverTreatedAsAPattern(bool onDisk)
    {
        var workspace = Workspace(onDisk);
        Write(workspace, "connectionReferences:\n  - connectionReferenceLogicalName: cr_odd", "a b.[1]{2}(3)+#%.sync.yaml");

        Assert.Equal(["cr_odd"], DeclaredConnectionReferences.Read(workspace).Select(reference => reference.ConnectionReferenceLogicalName.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFileThatIsNotADeclarationIsNotRead(bool onDisk)
    {
        var workspace = Workspace(onDisk);
        Write(workspace, "not yaml at all: [unclosed", "notes.txt");
        Write(workspace, "not yaml at all: [unclosed", "nested.sync.yaml.bak");

        Assert.Empty(DeclaredConnectionReferences.Read(workspace));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneUnreadableFileIsNamedRatherThanItsReadableNeighbours(bool onDisk)
    {
        var workspace = Workspace(onDisk);
        Write(workspace, "connectionReferences:\n  - connectionReferenceLogicalName: cr_fine", "a.sync.yaml");
        var broken = Write(workspace, "connectionReferences:\n  - connectionReferenceLogicalName:", "b.sync.yaml");

        var failure = Assert.Throws<DeclaredConnectionReferenceException>(() => DeclaredConnectionReferences.Read(workspace));

        Assert.Equal(broken, failure.Path);
        Assert.Contains(broken, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("cr_fine", failure.Message, StringComparison.Ordinal);
    }
}
