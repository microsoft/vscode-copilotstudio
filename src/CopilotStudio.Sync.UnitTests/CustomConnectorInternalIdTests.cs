using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class CustomConnectorInternalIdTests
{
    [Fact]
    public void StablePrefixOf_ReturnsTheIdentifierUpToItsEnvironmentSuffix()
    {
        Assert.Equal("shared_cr1-5fweather-20plus-5f", CustomConnectorInternalId.StablePrefixOf("shared_cr1-5fweather-20plus-5f566c5f3d9705a6dd"));
    }

    [Fact]
    public void EnvironmentSuffixOf_ToleratesAnUppercaseSeparatorAndReturnsTheSuffixInLowercase()
    {
        Assert.Equal("566c5f3d9705a6dd", CustomConnectorInternalId.EnvironmentSuffixOf("shared_cr1-5fweather-20plus-5F566C5F3D9705A6DD"));
        Assert.Equal("shared_cr1-5fweather-20plus-5F", CustomConnectorInternalId.StablePrefixOf("shared_cr1-5fweather-20plus-5F566C5F3D9705A6DD"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("shared_teams")]
    [InlineData("shared_cr1-5fweather-5f566c5f3d97")]
    [InlineData("shared_cr1-5fweather-5f566c5f3d9705a6dd00")]
    [InlineData("shared_cr1-5fweather-5f566c5f3d9705a6dz")]
    [InlineData("-5f566c5f3d9705a6dd")]
    public void ParsingAnIdentifierWithoutAnEnvironmentSuffix_ReturnsNull(string? internalId)
    {
        Assert.Null(CustomConnectorInternalId.StablePrefixOf(internalId));
        Assert.Null(CustomConnectorInternalId.EnvironmentSuffixOf(internalId));
    }

    [Theory]
    [InlineData("cr1_5Fweather-20plus", "Weather Plus", "566c5f3d9705a6dd", "shared_cr1-5fweather-20plus-5f566c5f3d9705a6dd")]
    [InlineData("crf9a_agentd1weather", "Agent D1 Weather 202", "c57fa9bfea132312", "shared_crf9a-5fagent-20d1-20weather-20202-5fc57fa9bfea132312")]
    [InlineData("crf9a_agentd1weather", "Agent D1 Weather 202", "d6c23f2bd6bf7828", "shared_crf9a-5fagent-20d1-20weather-20202-5fd6c23f2bd6bf7828")]
    [InlineData("cr1_x", "Wetter (ä)", "566c5f3d9705a6dd", "shared_cr1-5fwetter-20-28-e4-29-5f566c5f3d9705a6dd")]
    public void Derive_EncodesThePrefixAndDisplayNameBeforeTheEnvironmentSuffix(string name, string displayName, string environmentSuffix, string expected)
    {
        Assert.Equal(expected, CustomConnectorInternalId.Derive(name, displayName, environmentSuffix));
    }

    [Theory]
    [InlineData("weather")]
    [InlineData("_weather")]
    [InlineData("9x_weather")]
    public void Derive_ANameWithoutAPublisherPrefix_Throws(string name)
    {
        Assert.Throws<ArgumentException>(() => CustomConnectorInternalId.Derive(name, "Weather", "566c5f3d9705a6dd"));
    }

    [Theory]
    [InlineData("shared_cr1-5fweather-20plus-5f566c5f3d9705a6dd")]
    [InlineData("SHARED_CR1-5FWEATHER-20PLUS-5F566C5F3D9705A6DD")]
    public void ConformingSuffixOf_AnIdentifierTheEnvironmentDerived_ReturnsItsSuffix(string internalId)
    {
        Assert.Equal("566c5f3d9705a6dd", CustomConnectorInternalId.ConformingSuffixOf("cr1_5Fweather-20plus", "Weather Plus", internalId));
    }

    [Theory]
    [InlineData("cr1_5Fweather-20plus", "Weather Plus", "shared_weatherplus-5f566c5f3d9705a6dd")]
    [InlineData("cr1_5Fweather-20plus", "Weather Minus", "shared_cr1-5fweather-20plus-5f566c5f3d9705a6dd")]
    [InlineData("weatherplus", "Weather Plus", "shared_cr1-5fweather-20plus-5f566c5f3d9705a6dd")]
    [InlineData("9x_weatherplus", "Weather Plus", "shared_9x-5fweather-20plus-5f566c5f3d9705a6dd")]
    [InlineData("cr1_5Fweather-20plus", "Weather Plus", "shared_cr1-5fweather-20plus")]
    public void ConformingSuffixOf_AnIdentifierTheEnvironmentDidNotDerive_ReturnsNull(string name, string displayName, string internalId)
    {
        Assert.Null(CustomConnectorInternalId.ConformingSuffixOf(name, displayName, internalId));
    }
}
