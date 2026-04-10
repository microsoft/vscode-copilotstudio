namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Xunit;

    public class PowerPlatformEndpointsConfigTests
    {
        [Fact]
        public void Instance_ReturnsNonNull()
        {
            var instance = PowerPlatformEndpointsConfig.Instance;

            Assert.NotNull(instance);
        }

        [Fact]
        public void Instance_ReturnsSameInstance()
        {
            var instance1 = PowerPlatformEndpointsConfig.Instance;
            var instance2 = PowerPlatformEndpointsConfig.Instance;

            Assert.Same(instance1, instance2);
        }

        [Fact]
        public void Instance_ThreadSafe_ReturnsSameInstance()
        {
            var instances = new PowerPlatformEndpointsConfig[10];

            Parallel.For(0, 10, i =>
            {
                instances[i] = PowerPlatformEndpointsConfig.Instance;
            });

            var firstInstance = instances[0];
            Assert.NotNull(firstInstance);
            Assert.All(instances, instance => Assert.Same(firstInstance, instance));
        }

        [Theory]
        [InlineData((int)CoreServicesClusterCategory.Local, "api.powerplatform.localhost")]
        [InlineData((int)CoreServicesClusterCategory.Exp, "api.exp.powerplatform.com")]
        [InlineData((int)CoreServicesClusterCategory.Dev, "api.dev.powerplatform.com")]
        [InlineData((int)CoreServicesClusterCategory.Prv, "api.prv.powerplatform.com")]
        [InlineData((int)CoreServicesClusterCategory.Test, "api.test.powerplatform.com")]
        [InlineData((int)CoreServicesClusterCategory.Preprod, "api.preprod.powerplatform.com")]
        [InlineData((int)CoreServicesClusterCategory.FirstRelease, "api.powerplatform.com")]
        [InlineData((int)CoreServicesClusterCategory.Prod, "api.powerplatform.com")]
        [InlineData((int)CoreServicesClusterCategory.GovFR, "api.gov.powerplatform.microsoft.us")]
        [InlineData((int)CoreServicesClusterCategory.Gov, "api.gov.powerplatform.microsoft.us")]
        [InlineData((int)CoreServicesClusterCategory.High, "api.high.powerplatform.microsoft.us")]
        [InlineData((int)CoreServicesClusterCategory.DoD, "api.appsplatform.us")]
        [InlineData((int)CoreServicesClusterCategory.Mooncake, "api.powerplatform.partner.microsoftonline.cn")]
        [InlineData((int)CoreServicesClusterCategory.Ex, "api.powerplatform.eaglex.ic.gov")]
        [InlineData((int)CoreServicesClusterCategory.Rx, "api.powerplatform.microsoft.scloud")]
        public void GetEndpointSuffix_ValidCategory_ReturnsCorrectSuffix(int categoryValue, string expectedSuffix)
        {
            var category = (CoreServicesClusterCategory)categoryValue;
            var config = PowerPlatformEndpointsConfig.Instance;

            var suffix = config.GetEndpointSuffix(category);

            Assert.Equal(expectedSuffix, suffix);
        }

        [Fact]
        public void GetEndpointSuffix_InvalidCategory_ThrowsKeyNotFoundException()
        {
            var config = PowerPlatformEndpointsConfig.Instance;
            var invalidCategory = (CoreServicesClusterCategory)999;

            var exception = Assert.Throws<KeyNotFoundException>(() => config.GetEndpointSuffix(invalidCategory));
            Assert.Contains("Configuration mapping missing for cluster category", exception.Message);
        }

        [Fact]
        public void GetEndpointSuffix_AllValidCategories_ReturnValues()
        {
            var config = PowerPlatformEndpointsConfig.Instance;
            var allCategories = Enum.GetValues(typeof(CoreServicesClusterCategory)).Cast<CoreServicesClusterCategory>();

            foreach (var category in allCategories)
            {
                var result = config.GetEndpointSuffix(category);
                Assert.NotNull(result);
                Assert.NotEmpty(result);
            }
        }

        [Fact]
        public void LoadConfigurationFromStream_WithValidJson_ReturnsConfig()
        {
            var json = @"{
                ""EndpointSuffixes"": {
                    ""Prod"": ""api.powerplatform.com"",
                    ""Test"": ""api.test.powerplatform.com""
                }
            }";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

            var config = PowerPlatformEndpointsConfig.LoadConfigurationFromStream(stream);

            Assert.NotNull(config);
            Assert.Equal("api.powerplatform.com", config.GetEndpointSuffix(CoreServicesClusterCategory.Prod));
        }

        [Fact]
        public void LoadConfigurationFromStream_WithEmptyEndpointSuffixes_ThrowsInvalidOperationException()
        {
            var json = @"{
                ""EndpointSuffixes"": {}
            }";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PowerPlatformEndpointsConfig.LoadConfigurationFromStream(stream));
            Assert.Contains("invalid or empty", exception.Message);
        }

        [Fact]
        public void LoadConfigurationFromStream_WithNullEndpointSuffixes_ThrowsInvalidOperationException()
        {
            var json = @"{}";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PowerPlatformEndpointsConfig.LoadConfigurationFromStream(stream));
            Assert.Contains("invalid or empty", exception.Message);
        }

        [Fact]
        public void LoadConfigurationFromResource_WithInvalidResourceName_ThrowsInvalidOperationException()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var invalidResourceName = "NonExistent.Resource.json";

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PowerPlatformEndpointsConfig.LoadConfigurationFromResource(assembly, invalidResourceName));
            Assert.Contains("Failed to load embedded resource", exception.Message);
            Assert.Contains(invalidResourceName, exception.Message);
        }
    }
}
