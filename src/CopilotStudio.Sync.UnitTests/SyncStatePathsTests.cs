// Copyright (C) Microsoft Corporation. All rights reserved.

using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class SyncStatePathsTests
{
    [Theory]
    [InlineData(".mcs")]
    [InlineData(".mcs/conn.json")]
    [InlineData("Agent/.mcs/botdefinition.json")]
    [InlineData("Agent/topics/deep/.mcs")]
    [InlineData(".MCS/conn.json")]
    [InlineData("Agent/.Mcs/changetoken.txt")]
    [InlineData("Agent\\sub/.mcs/conn.json")]
    public void IsSyncStatePath_OfAPathWithASyncStateSegmentAtAnyDepthInAnyCase_IsTrue(string path) => Assert.True(SyncStatePaths.IsSyncStatePath(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("x.mcs")]
    [InlineData(".mcsx")]
    [InlineData("Agent/x.mcs/conn.json")]
    [InlineData("Agent/.mcsx/conn.json")]
    [InlineData("Agent/settings.mcs.yml")]
    [InlineData("Agent/mcs/conn.json")]
    [InlineData("Agent/ .mcs/conn.json")]
    public void IsSyncStatePath_OfAPathWithoutASyncStateSegment_IsFalse(string? path) => Assert.False(SyncStatePaths.IsSyncStatePath(path));

    [Theory]
    [InlineData("Agent\\.mcs\\conn.json")]
    [InlineData(".mcs\\conn.json")]
    [InlineData("Agent\\.mcs")]
    public void IsSyncStatePath_SplitsOnlyOnForwardSlashes_SoABackslashPathIsFalse(string path) => Assert.False(SyncStatePaths.IsSyncStatePath(path));
}