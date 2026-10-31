// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class ComponentFolderNameTests
{
    [Theory]
    [InlineData("Prompt1-2fcc84f9-b852-496a-8b53-f571ccbf74c0")]
    [InlineData("shared_contoso-2FCC84F9-B852-496A-8B53-F571CCBF74C0")]
    [InlineData("2fcc84f9-b852-496a-8b53-f571ccbf74c0")]
    public void ExtractTrailingGuid_GuidSuffix_Parses(string folderName)
    {
        Assert.Equal(Guid.Parse("2fcc84f9-b852-496a-8b53-f571ccbf74c0"), ComponentFolderName.ExtractTrailingGuid(folderName));
    }

    [Theory]
    [InlineData("Prompt1")]
    [InlineData("")]
    [InlineData("2fcc84f9-b852-496a-8b53-f571ccbf74c0-Prompt1")]
    [InlineData("Prompt1-2fcc84f9-b852-496a-8b53-f571ccbf74c")]
    public void ExtractTrailingGuid_NoGuidSuffix_ReturnsNull(string folderName)
    {
        Assert.Null(ComponentFolderName.ExtractTrailingGuid(folderName));
    }
}
