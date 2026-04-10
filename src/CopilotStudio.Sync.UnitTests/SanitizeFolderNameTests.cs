// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

/// <summary>
/// Tests for SanitizeFolderName, ported from CloneAgentHandler in the extension.
/// Behavior: keep alphanumeric, underscore, hyphen, space, and Unicode > 128.
/// Percent-encode other ASCII characters.
/// </summary>
public class SanitizeFolderNameTests
{
    [Fact]
    public void SimpleDisplayName_Preserved()
    {
        Assert.Equal("MyAgent", WorkspaceSynchronizer.SanitizeFolderName("MyAgent"));
    }

    [Fact]
    public void DisplayName_WithSpaces_Preserved()
    {
        Assert.Equal("My Test Agent", WorkspaceSynchronizer.SanitizeFolderName("My Test Agent"));
    }

    [Fact]
    public void DisplayName_WithAngleBrackets_PercentEncoded()
    {
        Assert.Equal("My%3cAgent%3e", WorkspaceSynchronizer.SanitizeFolderName("My<Agent>"));
    }

    [Fact]
    public void DisplayName_WithSlashes_PercentEncoded()
    {
        Assert.Equal("My%2fAgent%5c", WorkspaceSynchronizer.SanitizeFolderName("My/Agent\\"));
    }

    [Fact]
    public void DisplayName_WithColons_PercentEncoded()
    {
        Assert.Equal("My%3aAgent", WorkspaceSynchronizer.SanitizeFolderName("My:Agent"));
    }

    [Fact]
    public void DisplayName_AllNonAlphanumericASCII_ReturnsEmpty()
    {
        Assert.Equal("", WorkspaceSynchronizer.SanitizeFolderName("<>:\"/\\|?*"));
    }

    [Fact]
    public void DisplayName_WithLeadingTrailingSpaces_Trimmed()
    {
        Assert.Equal("Agent", WorkspaceSynchronizer.SanitizeFolderName("  Agent  "));
    }

    [Fact]
    public void DisplayName_UnicodeCharacters_Preserved()
    {
        Assert.Equal("My Agent 日本語", WorkspaceSynchronizer.SanitizeFolderName("My Agent 日本語"));
    }

    [Fact]
    public void DisplayName_UnderscoresAndHyphens_Preserved()
    {
        Assert.Equal("my-agent_v2", WorkspaceSynchronizer.SanitizeFolderName("my-agent_v2"));
    }

    [Fact]
    public void DisplayName_Dots_PercentEncoded()
    {
        // Dots are ASCII but not alphanumeric/underscore/hyphen/space
        Assert.Equal("agent%2ev1", WorkspaceSynchronizer.SanitizeFolderName("agent.v1"));
    }
}
