// Copyright (C) Microsoft Corporation. All rights reserved.

using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class CopilotStudioControlPlaneTests
{
    private const string Gateway = "https://powervamg.us-il301.gateway.prod.island.powerapps.com/";

    [Theory]
    [InlineData(CoreServicesClusterCategory.Exp, "api://a522f059-bb65-47c0-8934-7db6e5286414")]
    [InlineData(CoreServicesClusterCategory.Dev, "api://a522f059-bb65-47c0-8934-7db6e5286414")]
    [InlineData(CoreServicesClusterCategory.Test, "api://a522f059-bb65-47c0-8934-7db6e5286414")]
    [InlineData(CoreServicesClusterCategory.Preprod, "api://a522f059-bb65-47c0-8934-7db6e5286414")]
    [InlineData(CoreServicesClusterCategory.FirstRelease, "api://96ff4394-9197-43aa-b393-6a41652e21f8")]
    [InlineData(CoreServicesClusterCategory.Prod, "api://96ff4394-9197-43aa-b393-6a41652e21f8")]
    [InlineData(CoreServicesClusterCategory.Gov, "api://9315aedd-209b-43b3-b149-2abff6a95d59")]
    [InlineData(CoreServicesClusterCategory.High, "api://69c6e40c-465f-4154-987d-da5cba10734e")]
    [InlineData(CoreServicesClusterCategory.DoD, "api://bd4a9f18-e349-4c74-a6b7-65dd465ea9ab")]
    [InlineData(CoreServicesClusterCategory.Mooncake, "api://96ff4394-9197-43aa-b393-6a41652e21f8")]
    public void AudienceFor_NamesTheApplicationOfTheControlPlaneServingTheCluster(CoreServicesClusterCategory cluster, string audience) => Assert.Equal(new Uri(audience), CopilotStudioControlPlane.AudienceFor(cluster));

    [Theory]
    [InlineData(CoreServicesClusterCategory.Exp, "island.powerapps.com")]
    [InlineData(CoreServicesClusterCategory.Preprod, "island.powerapps.com")]
    [InlineData(CoreServicesClusterCategory.Prod, "island.powerapps.com")]
    [InlineData(CoreServicesClusterCategory.Gov, "island.powerapps.us")]
    [InlineData(CoreServicesClusterCategory.High, "island.powerapps.us")]
    [InlineData(CoreServicesClusterCategory.DoD, "island.appsplatform.us")]
    [InlineData(CoreServicesClusterCategory.Mooncake, "island.powerapps.cn")]
    public void GatewayDomainFor_NamesTheDomainOfTheControlPlaneServingTheCluster(CoreServicesClusterCategory cluster, string domain) => Assert.Equal(domain, CopilotStudioControlPlane.GatewayDomainFor(cluster));

    [Theory]
    [InlineData(CoreServicesClusterCategory.Ex)]
    [InlineData(CoreServicesClusterCategory.Rx)]
    [InlineData(CoreServicesClusterCategory.Prv)]
    [InlineData(CoreServicesClusterCategory.Local)]
    [InlineData(CoreServicesClusterCategory.GovFR)]
    public void AClusterNoKnownControlPlaneServesHasNoAudienceNoGatewayDomainAndNoTrustedGateway(CoreServicesClusterCategory cluster)
    {
        Assert.Equal($"Unsupported cluster category: {cluster}", Assert.Throws<InvalidOperationException>(() => CopilotStudioControlPlane.AudienceFor(cluster)).Message);
        Assert.Equal($"Unsupported cluster category: {cluster}", Assert.Throws<InvalidOperationException>(() => CopilotStudioControlPlane.GatewayDomainFor(cluster)).Message);
        Assert.False(CopilotStudioControlPlane.IsTrustedGateway(new Uri(Gateway), cluster));
    }

    [Theory]
    [InlineData(Gateway, CoreServicesClusterCategory.Prod, true)]
    [InlineData(Gateway, CoreServicesClusterCategory.Test, true)]
    [InlineData("https://POWERVAMG.us-il301.gateway.prod.ISLAND.POWERAPPS.COM:443/", CoreServicesClusterCategory.FirstRelease, true)]
    [InlineData("https://powervamg.us-il301.gateway.prod.island.powerapps.com./", CoreServicesClusterCategory.Prod, true)]
    [InlineData("https://island.powerapps.com/", CoreServicesClusterCategory.Dev, true)]
    [InlineData("https://powervamg.us-il101.gateway.gov.island.powerapps.us/", CoreServicesClusterCategory.Gov, true)]
    [InlineData("https://powervamg.us-il101.gateway.high.island.powerapps.us/", CoreServicesClusterCategory.High, true)]
    [InlineData("https://powervamg.us-il101.gateway.dod.island.appsplatform.us/", CoreServicesClusterCategory.DoD, true)]
    [InlineData("https://powervamg.cn-il101.gateway.prod.island.powerapps.cn/", CoreServicesClusterCategory.Mooncake, true)]
    [InlineData("https://island.powerapps.cn/", CoreServicesClusterCategory.Mooncake, true)]
    [InlineData(Gateway, CoreServicesClusterCategory.Mooncake, false)]
    [InlineData("https://powervamg.cn-il101.gateway.prod.island.powerapps.cn/", CoreServicesClusterCategory.Prod, false)]
    [InlineData("https://powervamg.cn-il101.gateway.prod.island.powerapps.cn.attacker.example/", CoreServicesClusterCategory.Mooncake, false)]
    [InlineData("https://powervamg.us-il101.gateway.gov.island.powerapps.us/", CoreServicesClusterCategory.Prod, false)]
    [InlineData(Gateway, CoreServicesClusterCategory.Gov, false)]
    [InlineData("https://powervamg.us-il101.gateway.dod.island.appsplatform.us/", CoreServicesClusterCategory.High, false)]
    [InlineData("https://powervamg.us-il301.gateway.prod.island.powerapps.com.attacker.example/", CoreServicesClusterCategory.Prod, false)]
    [InlineData("https://notisland.powerapps.com/", CoreServicesClusterCategory.Prod, false)]
    [InlineData("http://powervamg.us-il301.gateway.prod.island.powerapps.com/", CoreServicesClusterCategory.Prod, false)]
    [InlineData("https://powervamg.us-il301.gateway.prod.island.powerapps.com:8443/", CoreServicesClusterCategory.Prod, false)]
    [InlineData("https://admin@powervamg.us-il301.gateway.prod.island.powerapps.com/", CoreServicesClusterCategory.Prod, false)]
    public void IsTrustedGateway_TrustsOnlyAnHttpsGatewayOnTheDomainOfTheClustersControlPlane(string endpoint, CoreServicesClusterCategory cluster, bool trusted) => Assert.Equal(trusted, CopilotStudioControlPlane.IsTrustedGateway(new Uri(endpoint), cluster));

    [Fact]
    public void IsTrustedGateway_RefusesNoEndpointAndARelativeOne()
    {
        Assert.False(CopilotStudioControlPlane.IsTrustedGateway(null, CoreServicesClusterCategory.Prod));
        Assert.False(CopilotStudioControlPlane.IsTrustedGateway(new Uri("api/botmanagement/", UriKind.Relative), CoreServicesClusterCategory.Prod));
    }
}
