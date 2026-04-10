namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent
{
    using System.Collections.Generic;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Xunit;

    public class PowerPlatformApiDiscoveryTests
    {
        private static readonly string DefaultEnvironmentId = "E81E6CF4-34DA-48B8-AB34-C42317E9B434";

        [Fact]
        public void GetEnvironmentEndpoint_InvalidClusterCategory_ThrowsKeyNotFoundException()
        {
            Assert.Throws<KeyNotFoundException>(() =>
                PowerPlatformApiDiscovery.GetEnvironmentEndpoint((CoreServicesClusterCategory)999, "foo"));
        }

        [Fact]
        public void GetTokenAudience_InvalidClusterCategory_ThrowsKeyNotFoundException()
        {
            Assert.Throws<KeyNotFoundException>(() =>
                PowerPlatformApiDiscovery.GetTokenAudience((CoreServicesClusterCategory)999));
        }

        [Theory]
        [InlineData((int)CoreServicesClusterCategory.Local,        "e81e6cf434da48b8ab34c42317e9b43.4.environment.api.powerplatform.localhost")]
        [InlineData((int)CoreServicesClusterCategory.Exp,          "e81e6cf434da48b8ab34c42317e9b43.4.environment.api.exp.powerplatform.com")]
        [InlineData((int)CoreServicesClusterCategory.Dev,          "e81e6cf434da48b8ab34c42317e9b43.4.environment.api.dev.powerplatform.com")]
        [InlineData((int)CoreServicesClusterCategory.Prv,          "e81e6cf434da48b8ab34c42317e9b43.4.environment.api.prv.powerplatform.com")]
        [InlineData((int)CoreServicesClusterCategory.Test,         "e81e6cf434da48b8ab34c42317e9b43.4.environment.api.test.powerplatform.com")]
        [InlineData((int)CoreServicesClusterCategory.Preprod,      "e81e6cf434da48b8ab34c42317e9b43.4.environment.api.preprod.powerplatform.com")]
        [InlineData((int)CoreServicesClusterCategory.FirstRelease, "e81e6cf434da48b8ab34c42317e9b4.34.environment.api.powerplatform.com")]
        [InlineData((int)CoreServicesClusterCategory.Prod,         "e81e6cf434da48b8ab34c42317e9b4.34.environment.api.powerplatform.com")]
        [InlineData((int)CoreServicesClusterCategory.GovFR,        "e81e6cf434da48b8ab34c42317e9b43.4.environment.api.gov.powerplatform.microsoft.us")]
        [InlineData((int)CoreServicesClusterCategory.Gov,          "e81e6cf434da48b8ab34c42317e9b43.4.environment.api.gov.powerplatform.microsoft.us")]
        [InlineData((int)CoreServicesClusterCategory.High,         "e81e6cf434da48b8ab34c42317e9b43.4.environment.api.high.powerplatform.microsoft.us")]
        [InlineData((int)CoreServicesClusterCategory.DoD,          "e81e6cf434da48b8ab34c42317e9b43.4.environment.api.appsplatform.us")]
        [InlineData((int)CoreServicesClusterCategory.Mooncake,     "e81e6cf434da48b8ab34c42317e9b43.4.environment.api.powerplatform.partner.microsoftonline.cn")]
        [InlineData((int)CoreServicesClusterCategory.Ex,           "e81e6cf434da48b8ab34c42317e9b43.4.environment.api.powerplatform.eaglex.ic.gov")]
        [InlineData((int)CoreServicesClusterCategory.Rx,           "e81e6cf434da48b8ab34c42317e9b43.4.environment.api.powerplatform.microsoft.scloud")]
        public void GetEnvironmentEndpoint_ReturnsExpectedEndpoint(int clusterCategoryValue, string expectedEndpoint)
        {
            var clusterCategory = (CoreServicesClusterCategory)clusterCategoryValue;
            var result = PowerPlatformApiDiscovery.GetEnvironmentEndpoint(clusterCategory, DefaultEnvironmentId);

            Assert.Equal(expectedEndpoint, result);
        }

        [Theory]
        [InlineData((int)CoreServicesClusterCategory.Local,        "https://api.powerplatform.localhost")]
        [InlineData((int)CoreServicesClusterCategory.Exp,          "https://api.exp.powerplatform.com")]
        [InlineData((int)CoreServicesClusterCategory.Dev,          "https://api.dev.powerplatform.com")]
        [InlineData((int)CoreServicesClusterCategory.Prv,          "https://api.prv.powerplatform.com")]
        [InlineData((int)CoreServicesClusterCategory.Test,         "https://api.test.powerplatform.com")]
        [InlineData((int)CoreServicesClusterCategory.Preprod,      "https://api.preprod.powerplatform.com")]
        [InlineData((int)CoreServicesClusterCategory.FirstRelease, "https://api.powerplatform.com")]
        [InlineData((int)CoreServicesClusterCategory.Prod,         "https://api.powerplatform.com")]
        [InlineData((int)CoreServicesClusterCategory.GovFR,        "https://api.gov.powerplatform.microsoft.us")]
        [InlineData((int)CoreServicesClusterCategory.Gov,          "https://api.gov.powerplatform.microsoft.us")]
        [InlineData((int)CoreServicesClusterCategory.High,         "https://api.high.powerplatform.microsoft.us")]
        [InlineData((int)CoreServicesClusterCategory.DoD,          "https://api.appsplatform.us")]
        [InlineData((int)CoreServicesClusterCategory.Mooncake,     "https://api.powerplatform.partner.microsoftonline.cn")]
        [InlineData((int)CoreServicesClusterCategory.Ex,           "https://api.powerplatform.eaglex.ic.gov")]
        [InlineData((int)CoreServicesClusterCategory.Rx,           "https://api.powerplatform.microsoft.scloud")]
        public void GetTokenAudience_ReturnsExpectedAudience(int clusterCategoryValue, string expectedAudience)
        {
            var clusterCategory = (CoreServicesClusterCategory)clusterCategoryValue;
            var result = PowerPlatformApiDiscovery.GetTokenAudience(clusterCategory);

            Assert.Equal(expectedAudience, result);
        }
    }
}
