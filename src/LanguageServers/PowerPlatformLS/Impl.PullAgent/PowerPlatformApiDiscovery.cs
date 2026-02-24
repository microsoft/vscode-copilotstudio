namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using System;

    // Lightweight version of CoreFramework's PowerPlatformApiDiscovery.
    // todo: get a lightweight package from Core Services
    internal static class PowerPlatformApiDiscovery
    {
        /// <inheritdoc/>
        public static string GetTokenAudience(CoreServicesClusterCategory clusterCategory) => $"https://{GetEndpointSuffix(clusterCategory)}";

        public static string GetEnvironmentEndpoint(CoreServicesClusterCategory clusterCategory, string environmentId)
        {
            var normalizedResourceId = environmentId.ToLower().Replace("-", "");
            var idSuffixLength = GetIdSuffixLength(clusterCategory);
            var hexPrefix = normalizedResourceId.Substring(0, normalizedResourceId.Length - idSuffixLength);
            var hexSuffix = normalizedResourceId.Substring(normalizedResourceId.Length - idSuffixLength, idSuffixLength);
            return $"{hexPrefix}.{hexSuffix}.environment.{GetEndpointSuffix(clusterCategory)}";
        }

        private static string GetEndpointSuffix(CoreServicesClusterCategory category) => category switch
        {
            CoreServicesClusterCategory.Local => "api.powerplatform.localhost",
            CoreServicesClusterCategory.Exp => "api.exp.powerplatform.com",
            CoreServicesClusterCategory.Dev => "api.dev.powerplatform.com",
            CoreServicesClusterCategory.Prv => "api.prv.powerplatform.com",
            CoreServicesClusterCategory.Test => "api.test.powerplatform.com",
            CoreServicesClusterCategory.Preprod => "api.preprod.powerplatform.com",
            CoreServicesClusterCategory.FirstRelease => "api.powerplatform.com",
            CoreServicesClusterCategory.Prod => "api.powerplatform.com",
            CoreServicesClusterCategory.GovFR => "api.gov.powerplatform.microsoft.us",
            CoreServicesClusterCategory.Gov => "api.gov.powerplatform.microsoft.us",
            CoreServicesClusterCategory.High => "api.high.powerplatform.microsoft.us",
            CoreServicesClusterCategory.DoD => "api.appsplatform.us",
            CoreServicesClusterCategory.Mooncake => "api.powerplatform.partner.microsoftonline.cn",
            CoreServicesClusterCategory.Ex => "api.powerplatform.eaglex.ic.gov",
            CoreServicesClusterCategory.Rx => "api.powerplatform.microsoft.scloud",
            _ => throw new ArgumentException($"Invalid cluster category value: {category}", nameof(category)),
        };

        private static int GetIdSuffixLength(CoreServicesClusterCategory category) => category switch
        {
            CoreServicesClusterCategory.FirstRelease or CoreServicesClusterCategory.Prod => 2,
            _ => 1,
        };
    }
}
