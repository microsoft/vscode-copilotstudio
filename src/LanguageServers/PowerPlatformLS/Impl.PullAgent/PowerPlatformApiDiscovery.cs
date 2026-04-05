namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CopilotStudio.Sync;
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

        private static string GetEndpointSuffix(CoreServicesClusterCategory category)
        {
            return PowerPlatformEndpointsConfig.Instance.GetEndpointSuffix(category);
        }

        private static int GetIdSuffixLength(CoreServicesClusterCategory category) => category switch
        {
            CoreServicesClusterCategory.FirstRelease or CoreServicesClusterCategory.Prod => 2,
            _ => 1,
        };
    }
}
