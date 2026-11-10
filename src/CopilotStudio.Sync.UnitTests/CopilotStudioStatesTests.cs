// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync.Dataverse;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public sealed class CopilotStudioStatesTests
{
    [Theory]
    [InlineData(0, "draft")]
    [InlineData(1, "activated")]
    [InlineData(2, "suspended")]
    [InlineData(3, "unknown")]
    [InlineData(-1, "unknown")]
    [InlineData(null, "unknown")]
    public void WorkflowStateName_NamesTheStateOfEveryStateCode(int? stateCode, string expectedState)
    {
        Assert.Equal(expectedState, CopilotStudioStates.WorkflowStateName(stateCode));
    }
}
