// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class WorkingCopyMergeTests
{
    private const string SkillPath = "TA1/behaviors/skill-1/SKILL.md";

    private const string Base = "a\nb\nc\nd\ne\n";

    private const string LocalEdit = "a\nB-local\nc\nd\ne\n";

    private const string RevisionEdit = "a\nb\nc\nd\nE-revision\n";

    private const string RevisionSameLineEdit = "a\nB-revision\nc\nd\nE-revision\n";

    [Fact]
    public void KeepsTheLocalFileWhenTheRevisionLeftItUnchanged()
    {
        var result = MergeText(Base, LocalEdit, Base);

        AssertEntry(result, WorkingCopyMergeOutcome.Unchanged, null);
        Assert.Equal(LocalEdit, Read(result.Files[SkillPath]));
    }

    [Theory]
    [InlineData(Base, Base, RevisionEdit, WorkingCopyMergeOutcome.Updated)]
    [InlineData(Base, Base, "\uFEFF" + Base, WorkingCopyMergeOutcome.Updated)]
    [InlineData(null, null, RevisionEdit, WorkingCopyMergeOutcome.Added)]
    [InlineData(Base, Base, null, WorkingCopyMergeOutcome.Deleted)]
    public void TakesTheRevisionWhereTheLocalFileIsStillTheBase(string? baseText, string? localText, string? incomingText, WorkingCopyMergeOutcome outcome)
    {
        var result = MergeText(baseText, localText, incomingText);

        AssertEntry(result, outcome, incomingText);
        Assert.Equal(incomingText, result.Files.TryGetValue(SkillPath, out var content) ? Read(content) : null);
    }

    [Theory]
    [InlineData(Base, RevisionEdit, RevisionEdit)]
    [InlineData(null, RevisionEdit, RevisionEdit)]
    [InlineData(Base, null, null)]
    public void KeepsTheLocalFileThatAlreadyMatchesTheRevision(string? baseText, string? localText, string? incomingText)
    {
        AssertEntry(MergeText(baseText, localText, incomingText), WorkingCopyMergeOutcome.Unchanged, null);
    }

    [Fact]
    public void MergesChangesOnOtherLines()
    {
        var result = MergeText(Base, LocalEdit, RevisionEdit);

        AssertEntry(result, WorkingCopyMergeOutcome.Merged, "a\nB-local\nc\nd\nE-revision\n");
        Assert.Equal("a\nB-local\nc\nd\nE-revision\n", Read(result.Files[SkillPath]));
    }

    [Theory]
    [InlineData(new byte[] { 0x61, 0x00, 0x0A })]
    [InlineData(new byte[] { 0x61, 0xC3, 0x0A })]
    public void TreatsContentThatIsNotUtf8TextAsBinary(byte[] incomingContent)
    {
        AssertEntry(MergeContent(Encoding.UTF8.GetBytes(Base), Encoding.UTF8.GetBytes(LocalEdit), incomingContent), WorkingCopyMergeOutcome.Conflicted, null);
    }

    [Fact]
    public void MergesNonAsciiUtf8AsText()
    {
        AssertEntry(MergeText("é\nb\nc\nd\ne\n", "é\nB-local\nc\nd\ne\n", "é\nb\nc\nd\nE-révision\n"), WorkingCopyMergeOutcome.Merged, "é\nB-local\nc\nd\nE-révision\n");
    }

    [Fact]
    public void KeepsTheByteOrderMarkTheLocalFileStartsWithInTheMergedText()
    {
        AssertEntry(MergeText(Base, "\uFEFF" + LocalEdit, RevisionEdit), WorkingCopyMergeOutcome.Merged, "\uFEFFa\nB-local\nc\nd\nE-revision\n");
    }

    [Theory]
    [InlineData("a\r\nb\r\nc\r\nd\r\ne")]
    [InlineData("a\rb\rc\rd\re\r")]
    [InlineData("a\nb\nc\nd\ne")]
    public void TreatsARevisionThatChangedOnlyLineEndingsAsUnchanged(string incomingText)
    {
        AssertEntry(MergeText(Base, LocalEdit, incomingText), WorkingCopyMergeOutcome.Unchanged, null);
    }

    [Fact]
    public void TreatsALocalFileThatDiffersOnlyInLineEndingsAsTheBase()
    {
        AssertEntry(MergeText(Base, "a\r\nb\r\nc\r\nd\r\ne", RevisionEdit), WorkingCopyMergeOutcome.Updated, RevisionEdit);
    }

    [Fact]
    public void TreatsALocalFileThatDiffersOnlyInLineEndingsFromTheRevisionAsTheRevision()
    {
        AssertEntry(MergeText(Base, "a\r\nb\r\nc\r\nd\r\nE-revision\r\n", RevisionEdit), WorkingCopyMergeOutcome.Unchanged, null);
    }

    [Fact]
    public void WritesTheMergedTextWithTheLineEndingsOfTheLocalFile()
    {
        AssertEntry(MergeText(Base, "a\r\nB-local\r\nc\r\nd\r\ne", RevisionEdit), WorkingCopyMergeOutcome.Merged, "a\r\nB-local\r\nc\r\nd\r\nE-revision");
    }

    [Fact]
    public void KeepsTheLocalFileWhenItAlreadyHoldsTheRevisionsChanges()
    {
        AssertEntry(MergeText(Base, "a\nB-local\nc\nd\nE-revision\n", RevisionEdit), WorkingCopyMergeOutcome.Unchanged, null);
    }

    [Fact]
    public void MergesEveryPathAnyMapHoldsInOrdinalOrder()
    {
        var baseFiles = Map(("TA1/agent.mcs.yml", "agent\n"), ("TA1/topics/old.mcs.yml", "old\n"));
        var localFiles = Map(("TA1/agent.mcs.yml", "agent\n"), ("TA1/topics/old.mcs.yml", "old\n"), ("TA1/notes/local.md", "mine\n"));
        var incomingFiles = Map(("TA1/agent.mcs.yml", "agent\n"), ("TA1/behaviors/skill-1/SKILL.md", "skill\n"), ("Collection/a.mcs.yml", "collection\n"));

        var result = WorkingCopyMerge.Merge(baseFiles, localFiles, incomingFiles, WorkingCopyConflictMode.Report);

        Assert.Equal(
            new[]
            {
                ("Collection/a.mcs.yml", WorkingCopyMergeOutcome.Added),
                ("TA1/agent.mcs.yml", WorkingCopyMergeOutcome.Unchanged),
                ("TA1/behaviors/skill-1/SKILL.md", WorkingCopyMergeOutcome.Added),
                ("TA1/notes/local.md", WorkingCopyMergeOutcome.Unchanged),
                ("TA1/topics/old.mcs.yml", WorkingCopyMergeOutcome.Deleted),
            },
            result.Entries.Select(entry => (entry.Path, entry.Outcome)));
        Assert.Equal(
            new[] { "Collection/a.mcs.yml", "TA1/agent.mcs.yml", "TA1/behaviors/skill-1/SKILL.md", "TA1/notes/local.md" },
            result.Files.Keys.OrderBy(path => path, StringComparer.Ordinal));
        Assert.Equal("mine\n", Read(result.Files["TA1/notes/local.md"]));
    }

    [Theory]
    [InlineData(Base, LocalEdit, RevisionSameLineEdit)]
    [InlineData(Base, LocalEdit, null)]
    [InlineData(Base, null, RevisionEdit)]
    [InlineData(null, "local\n", "revision\n")]
    public void ReportModeNamesEachCollisionWithoutProducingItsContent(string? baseText, string? localText, string? incomingText)
    {
        var result = MergeText(baseText, localText, incomingText, WorkingCopyConflictMode.Report);

        AssertEntry(result, WorkingCopyMergeOutcome.Conflicted, null);
        Assert.Equal(localText, result.Files.TryGetValue(SkillPath, out var content) ? Read(content) : null);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReportModeNamesABinaryFileBothSidesChanged(bool inBase)
    {
        var result = MergeContent(inBase ? new byte[] { 0x00, 0x01 } : null, new byte[] { 0x00, 0x02 }, new byte[] { 0x00, 0x03 }, WorkingCopyConflictMode.Report);

        AssertEntry(result, WorkingCopyMergeOutcome.Conflicted, null);
        Assert.Equal(new byte[] { 0x00, 0x02 }, result.Files[SkillPath].ToArray());
    }

    [Fact]
    public void ReportModeMergesChangesOnOtherLines()
    {
        AssertEntry(MergeText(Base, LocalEdit, RevisionEdit, WorkingCopyConflictMode.Report), WorkingCopyMergeOutcome.Merged, "a\nB-local\nc\nd\nE-revision\n");
    }

    [Fact]
    public void OursModeKeepsTheLocalLinesOfAConflictAndMergesTheOtherChanges()
    {
        AssertEntry(MergeText(Base, LocalEdit, RevisionSameLineEdit, WorkingCopyConflictMode.Ours), WorkingCopyMergeOutcome.Merged, "a\nB-local\nc\nd\nE-revision\n");
    }

    [Theory]
    [InlineData(Base, LocalEdit, null)]
    [InlineData(Base, null, RevisionEdit)]
    [InlineData(null, "local\n", "revision\n")]
    public void OursModeKeepsTheLocalSideOfEveryOtherCollision(string? baseText, string? localText, string? incomingText)
    {
        var result = MergeText(baseText, localText, incomingText, WorkingCopyConflictMode.Ours);

        AssertEntry(result, WorkingCopyMergeOutcome.Unchanged, null);
        Assert.Equal(localText, result.Files.TryGetValue(SkillPath, out var content) ? Read(content) : null);
    }

    [Fact]
    public void OursModeKeepsTheLocalBinaryFileBothSidesChanged()
    {
        AssertEntry(MergeContent(new byte[] { 0x00, 0x01 }, new byte[] { 0x00, 0x02 }, new byte[] { 0x00, 0x03 }, WorkingCopyConflictMode.Ours), WorkingCopyMergeOutcome.Unchanged, null);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void MarkersModeWritesTheConflictMarkersOfSyncsPullInTheLineEndingsOfTheLocalFile(string lineEnd)
    {
        var (sync, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var localText = LocalEdit.Replace("\n", lineEnd);
        var markedText = sync.MergeStrings(Base, localText, RevisionSameLineEdit);

        var result = MergeText(Base, localText, RevisionSameLineEdit, WorkingCopyConflictMode.Markers);

        Assert.Contains("<<<<<<<", markedText);
        Assert.DoesNotContain("\n", markedText.Replace(lineEnd, string.Empty));
        AssertEntry(result, WorkingCopyMergeOutcome.Conflicted, markedText);
        Assert.Equal(markedText, Read(result.Files[SkillPath]));
    }

    [Fact]
    public void MarkersModeMergesChangesOnOtherLines()
    {
        AssertEntry(MergeText(Base, LocalEdit, RevisionEdit, WorkingCopyConflictMode.Markers), WorkingCopyMergeOutcome.Merged, "a\nB-local\nc\nd\nE-revision\n");
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x01 }, new byte[] { 0x00, 0x02 }, new byte[] { 0x00, 0x03 })]
    [InlineData(null, new byte[] { 0x00, 0x02 }, new byte[] { 0x00, 0x03 })]
    [InlineData(new byte[] { 0x00, 0x01 }, new byte[] { 0x61, 0x0A }, new byte[] { 0x62, 0x0A })]
    [InlineData(new byte[] { 0x61, 0x0A }, new byte[] { 0x62, 0x0A }, new byte[] { 0x61, 0xC3, 0x0A })]
    public void MarkersModeReplacesAFileTheTextMergeCannotSettleWithTheIncomingFile(byte[]? baseContent, byte[] localContent, byte[] incomingContent)
    {
        var result = MergeContent(baseContent, localContent, incomingContent, WorkingCopyConflictMode.Markers);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(WorkingCopyMergeOutcome.Replaced, entry.Outcome);
        Assert.Equal(incomingContent, entry.Content?.ToArray());
        Assert.Equal(incomingContent, result.Files[SkillPath].ToArray());
    }

    [Theory]
    [InlineData(null, RevisionEdit, RevisionEdit)]
    [InlineData(LocalEdit, null, null)]
    public void MarkersModeKeepsTheChangedSideOfAFileTheOtherSideDeleted(string? localText, string? incomingText, string? writtenText)
    {
        var result = MergeText(Base, localText, incomingText, WorkingCopyConflictMode.Markers);

        AssertEntry(result, WorkingCopyMergeOutcome.Conflicted, writtenText);
        Assert.Equal(localText ?? incomingText, Read(result.Files[SkillPath]));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    public void RefusesAnUnknownConflictMode(int mode)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MergeText(Base, LocalEdit, RevisionEdit, (WorkingCopyConflictMode)mode));
    }

    private static WorkingCopyMergeResult MergeText(string? baseText, string? localText, string? incomingText, WorkingCopyConflictMode mode = WorkingCopyConflictMode.Report)
        => MergeContent(Encode(baseText), Encode(localText), Encode(incomingText), mode);

    private static WorkingCopyMergeResult MergeContent(byte[]? baseContent, byte[]? localContent, byte[]? incomingContent, WorkingCopyConflictMode mode = WorkingCopyConflictMode.Report)
        => WorkingCopyMerge.Merge(Single(baseContent), Single(localContent), Single(incomingContent), mode);

    private static Dictionary<string, ReadOnlyMemory<byte>> Single(byte[]? content)
        => content is null ? new Dictionary<string, ReadOnlyMemory<byte>>() : new Dictionary<string, ReadOnlyMemory<byte>> { [SkillPath] = content };

    private static Dictionary<string, ReadOnlyMemory<byte>> Map(params (string Path, string Text)[] files)
        => files.ToDictionary(file => file.Path, file => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(file.Text), StringComparer.Ordinal);

    private static byte[]? Encode(string? text) => text is null ? null : Encoding.UTF8.GetBytes(text);

    private static string Read(ReadOnlyMemory<byte> content) => Encoding.UTF8.GetString(content.ToArray());

    private static void AssertEntry(WorkingCopyMergeResult result, WorkingCopyMergeOutcome outcome, string? content)
    {
        var entry = Assert.Single(result.Entries);
        Assert.Equal(SkillPath, entry.Path);
        Assert.Equal(outcome, entry.Outcome);
        Assert.Equal(content, entry.Content is { } written ? Read(written) : null);
    }
}