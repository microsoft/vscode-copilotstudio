using Microsoft.CopilotStudio.Sync.Dataverse;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AgentNameValidatorTests
{
    [Theory]
    [InlineData("Contoso Helpdesk")]
    [InlineData("Agent 1 & 2: \"quoted\"")]
    public void FindProblem_AValidName_HasNone(string displayName)
    {
        Assert.Null(AgentNameValidator.FindProblem(displayName));
    }

    [Fact]
    public void FindProblem_ANameOfTheMaximumLengthAfterTrimming_HasNone()
    {
        Assert.Null(AgentNameValidator.FindProblem("  " + new string('a', AgentNameValidator.MaximumLength) + "  "));
    }

    [Fact]
    public void FindProblem_ALongerName_NamesItsLengthAndTheLimit()
    {
        Assert.Equal("is 43 characters long; Copilot Studio allows at most 42", AgentNameValidator.FindProblem(new string('a', 43)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FindProblem_ABlankName_IsRequired(string? displayName)
    {
        Assert.Equal("is required", AgentNameValidator.FindProblem(displayName));
    }

    [Theory]
    [InlineData("Agent <b>")]
    [InlineData("Agent > 1")]
    public void FindProblem_AnAngleBracket_IsRefused(string displayName)
    {
        Assert.Equal("contains < or >, which Copilot Studio does not allow", AgentNameValidator.FindProblem(displayName));
    }

    [Fact]
    public void FindProblem_ALongNameWithAnAngleBracket_ReportsTheLength()
    {
        Assert.StartsWith("is 50 characters long", AgentNameValidator.FindProblem("<" + new string('a', 49)), StringComparison.Ordinal);
    }
}
