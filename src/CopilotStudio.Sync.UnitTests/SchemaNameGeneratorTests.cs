using Microsoft.CopilotStudio.Sync.Dataverse;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class SchemaNameGeneratorTests
{
    [Theory]
    [InlineData("cr", true)]
    [InlineData("crf9a", true)]
    [InlineData("Abcdefg8", true)]
    [InlineData("abcdefghi", false)]
    [InlineData("c", false)]
    [InlineData("9cr", false)]
    [InlineData("c_r", false)]
    [InlineData("cré", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidPublisherPrefix_AcceptsTwoToEightAsciiLettersOrDigitsStartingWithALetter(string? publisherPrefix, bool expected)
    {
        Assert.Equal(expected, SchemaNameGenerator.IsValidPublisherPrefix(publisherPrefix));
    }

    [Theory]
    [InlineData("crf9a_helpdesk", "crf9a")]
    [InlineData("cr1_cli_copy", "cr1")]
    [InlineData("helpdesk", null)]
    [InlineData("_helpdesk", null)]
    [InlineData("9cr_helpdesk", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void PublisherPrefixOf_ReturnsTheValidPrefixBeforeTheFirstUnderscore(string? schemaName, string? expected)
    {
        Assert.Equal(expected, SchemaNameGenerator.PublisherPrefixOf(schemaName));
    }

    [Theory]
    [InlineData("CR", "Contoso Help-Desk 2!", "cr_contosohelpdesk2")]
    [InlineData("cr", "Café Ünïcode", "cr_cafncode")]
    public void GenerateAgentSchemaName_KeepsTheAsciiLettersAndDigitsInLowercase(string publisherPrefix, string displayName, string expected)
    {
        Assert.Equal(expected, SchemaNameGenerator.GenerateAgentSchemaName(publisherPrefix, displayName));
    }

    [Fact]
    public void GenerateAgentSchemaName_TruncatesTheNameToTheMaximumLength()
    {
        Assert.Equal("cr_abcde", SchemaNameGenerator.GenerateAgentSchemaName("cr", "abcdefgh", 8));
    }

    [Fact]
    public void GenerateAgentSchemaName_WithoutAMaximumLength_FitsTheDataverseLimit()
    {
        Assert.Equal(SchemaNameValidator.MaximumLength, SchemaNameGenerator.GenerateAgentSchemaName("cr", new string('a', 200))?.Length);
    }

    [Theory]
    [InlineData("¡¿ !", 100)]
    [InlineData("Üñï", 100)]
    [InlineData("abc", 3)]
    [InlineData("abc", -5)]
    public void GenerateAgentSchemaName_WhenNoCharacterFits_ReturnsNull(string displayName, int maximumLength)
    {
        Assert.Null(SchemaNameGenerator.GenerateAgentSchemaName("cr", displayName, maximumLength));
    }

    [Theory]
    [InlineData("9x")]
    [InlineData("toolongprefix")]
    public void GenerateAgentSchemaName_WithAnInvalidPublisherPrefix_Throws(string publisherPrefix)
    {
        Assert.Throws<ArgumentException>(() => SchemaNameGenerator.GenerateAgentSchemaName(publisherPrefix, "Agent"));
    }

    [Fact]
    public void RandomString_DrawsOnlyFromTheAlphabet()
    {
        Assert.Matches("^[ab]{40}$", SchemaNameGenerator.RandomString(40, "ab"));
    }

    [Fact]
    public void GenerateSchemaNameForBotComponent_ACollision_AddsAThreeCharacterAlphanumericSuffix()
    {
        Assert.Matches(@"^cr1_agent\.file\.notes\.txt_[A-Za-z0-9]{3}$", SchemaNameGenerator.GenerateSchemaNameForBotComponent("cr1_agent", "file", "notes.txt", new[] { "CR1_AGENT.FILE.NOTES.TXT" }));
    }

    [Fact]
    public void GenerateSchemaNameForBotComponent_ADisplayNameWithoutValidCharacters_UsesThreeRandomAlphanumericCharacters()
    {
        Assert.Matches(@"^cr1_agent\.topic\.[A-Za-z0-9]{3}$", SchemaNameGenerator.GenerateSchemaNameForBotComponent("cr1_agent", "topic", "???", Array.Empty<string>()));
    }

    [Fact]
    public void GenerateSchemaNameForBotComponent_ALongDisplayName_IsCutToTheDataverseLimit()
    {
        Assert.Equal(SchemaNameValidator.MaximumLength, SchemaNameGenerator.GenerateSchemaNameForBotComponent("cr1_agent", "topic", new string('a', 150), Array.Empty<string>()).Length);
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void SchemaNameValidator_AcceptsNamesUpToTheDataverseLimit(int length, bool expected)
    {
        Assert.Equal(expected, SchemaNameValidator.IsValid("cr_" + new string('a', length - 3)));
    }
}
