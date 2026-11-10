// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

/// <summary>The token audience and the agent-management gateway domain of the Copilot Studio control plane that serves each cluster.</summary>
public static class CopilotStudioControlPlane
{
    private static readonly ControlPlane TestControlPlane = new(new Uri("api://a522f059-bb65-47c0-8934-7db6e5286414"), "island.powerapps.com");

    private static readonly ControlPlane ProductionControlPlane = new(new Uri("api://96ff4394-9197-43aa-b393-6a41652e21f8"), "island.powerapps.com");

    private static readonly ControlPlane GovControlPlane = new(new Uri("api://9315aedd-209b-43b3-b149-2abff6a95d59"), "island.powerapps.us");

    private static readonly ControlPlane HighControlPlane = new(new Uri("api://69c6e40c-465f-4154-987d-da5cba10734e"), "island.powerapps.us");

    private static readonly ControlPlane DodControlPlane = new(new Uri("api://bd4a9f18-e349-4c74-a6b7-65dd465ea9ab"), "island.appsplatform.us");

    private static readonly ControlPlane ChinaControlPlane = new(new Uri("api://96ff4394-9197-43aa-b393-6a41652e21f8"), "island.powerapps.cn");

    /// <summary>The token audience of the control plane serving a cluster: the test application for Exp, Dev, Test and Preprod, the production application for FirstRelease, Prod and Mooncake, and the sovereign applications for Gov, High and DoD.</summary>
    /// <param name="clusterCategory">The cluster the environment belongs to.</param>
    public static Uri AudienceFor(CoreServicesClusterCategory clusterCategory) => Require(clusterCategory).Audience;

    /// <summary>The domain the agent-management gateways of the control plane serving a cluster live on, such as island.powerapps.com.</summary>
    /// <param name="clusterCategory">The cluster the environment belongs to.</param>
    public static string GatewayDomainFor(CoreServicesClusterCategory clusterCategory) => Require(clusterCategory).GatewayDomain;

    /// <summary>Whether an endpoint is an agent-management gateway of the control plane serving a cluster: absolute HTTPS on the default port, without user information, on the cluster's gateway domain or a subdomain of it.</summary>
    /// <param name="endpoint">The endpoint an environment advertises.</param>
    /// <param name="clusterCategory">The cluster the environment belongs to.</param>
    public static bool IsTrustedGateway(Uri? endpoint, CoreServicesClusterCategory clusterCategory) => Of(clusterCategory) is { } controlPlane && endpoint is not null && endpoint.IsAbsoluteUri && endpoint.IsDefaultPort && string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(endpoint.UserInfo) && controlPlane.Serves(endpoint.Host);

    private static ControlPlane? Of(CoreServicesClusterCategory clusterCategory) => clusterCategory switch
    {
        CoreServicesClusterCategory.Exp or CoreServicesClusterCategory.Dev or CoreServicesClusterCategory.Test or CoreServicesClusterCategory.Preprod => TestControlPlane,
        CoreServicesClusterCategory.FirstRelease or CoreServicesClusterCategory.Prod => ProductionControlPlane,
        CoreServicesClusterCategory.Gov => GovControlPlane,
        CoreServicesClusterCategory.High => HighControlPlane,
        CoreServicesClusterCategory.DoD => DodControlPlane,
        CoreServicesClusterCategory.Mooncake => ChinaControlPlane,
        _ => null,
    };

    private static ControlPlane Require(CoreServicesClusterCategory clusterCategory) => Of(clusterCategory) ?? throw new InvalidOperationException($"Unsupported cluster category: {clusterCategory}");

    private sealed record ControlPlane(Uri Audience, string GatewayDomain)
    {
        public bool Serves(string host)
        {
            var candidate = host.TrimEnd('.');

            return string.Equals(candidate, GatewayDomain, StringComparison.OrdinalIgnoreCase) || candidate.EndsWith("." + GatewayDomain, StringComparison.OrdinalIgnoreCase);
        }
    }
}
