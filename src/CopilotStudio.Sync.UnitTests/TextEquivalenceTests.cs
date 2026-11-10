// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.CopilotStudio.McsCore;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class TextEquivalenceTests
{
    private const string Text = "one\ntwo\n";

    private static readonly AgentFilePath LocalPath = new("local.txt");

    private static readonly AgentFilePath CloudPath = new("cloud.txt");

    public static TheoryData<byte[], byte[], bool, bool, bool> LocalFilesAgainstTheCloud => new()
    {
        { Utf8(Text), Utf8(Text), true, true, true },
        { Utf8("one\r\ntwo\r\n"), Utf8(Text), true, true, true },
        { Utf8(Text), Utf8("one\r\ntwo\r\n"), true, true, true },
        { Utf8("\uFEFF" + Text), Utf8(Text), true, true, false },
        { Utf8(Text), Utf8("\uFEFF" + Text), false, true, false },
        { Utf8("\uFEFFone\r\ntwo\r\n"), Utf8(Text), true, true, false },
        { Utf8("one\rtwo\r"), Utf8(Text), false, false, true },
        { Utf8(Text), Utf8("one\rtwo\r"), false, false, true },
        { Utf8("one\r\r\ntwo\n"), Utf8(Text), false, false, false },
        { Utf8("one\ntwo"), Utf8(Text), false, false, true },
        { Utf8(Text), Utf8("one\ntwo"), false, false, true },
        { Utf8("one\r\ntwo"), Utf8("one\r\ntwo\r\n"), false, false, true },
        { Utf8("one\r\ntwo\r\n"), Utf8("one\ntwo"), false, false, true },
        { Utf8("one\ntwo\r"), Utf8(Text), false, false, true },
        { Utf8("one\ntwo\n\n"), Utf8(Text), false, false, false },
        { Utf8("one\r\ntwo\n"), Utf8(Text), true, true, true },
        { Utf8("one\r\ntwo\n"), Utf8("one\r\ntwo\r\n"), true, true, true },
        { Utf8("one\r\ntwo\rthree\n"), Utf8("one\ntwo\nthree\n"), false, false, true },
        { Utf8("one\0\r\ntwo\r\n"), Utf8("one\0\ntwo\n"), false, false, false },
        { Utf8("one\0\ntwo\n"), Utf8("one\0\ntwo\n"), false, true, true },
        { Invalid("\r\n"), Invalid("\n"), false, false, false },
        { Invalid("\n"), Invalid("\n"), false, true, true },
        { Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Text)).ToArray(), Utf8(Text), false, false, false },
        { Utf8(string.Empty), Utf8(string.Empty), true, true, true },
        { Utf8("\uFEFF"), Utf8(string.Empty), true, true, false },
        { Utf8(string.Empty), Utf8("\uFEFF"), false, true, false },
        { Utf8(string.Empty), Utf8("\n"), false, false, false },
        { Utf8("one\n\uFEFFtwo\n"), Utf8(Text), false, false, false },
    };

    [Theory]
    [MemberData(nameof(LocalFilesAgainstTheCloud))]
    public void ThePullComparesByTheCheckoutRulesAndTheCaptureMergeByItsOwn(byte[] local, byte[] cloud, bool holdsText, bool holdsContent, bool mergeSame)
    {
        var files = new InMemoryFileAccessor(new DirectoryPath("c:/table/"));
        files.Write(LocalPath, local);
        files.Write(CloudPath, cloud);

        Assert.Equal(holdsText, CheckoutContent.HoldsText(files, LocalPath, Encoding.UTF8.GetString(cloud)));
        Assert.Equal(holdsContent, CheckoutContent.HoldsContentOf(files, LocalPath, CloudPath));
        Assert.Equal(mergeSame ? WorkingCopyMergeOutcome.Unchanged : WorkingCopyMergeOutcome.Updated, Assert.Single(WorkingCopyMerge.Merge(Single(cloud), Single(cloud), Single(local), WorkingCopyConflictMode.Report).Entries).Outcome);
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Invalid(string lineEnding) => Utf8("one").Append((byte)0xFF).Concat(Utf8(lineEnding)).ToArray();

    private static Dictionary<string, ReadOnlyMemory<byte>> Single(byte[] content) => new() { [LocalPath.ToString()] = content };
}
