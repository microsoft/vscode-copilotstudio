// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class IslandLicenseCheckSignInTests
{
    private const string ConditionalAccessDescription = "Device is not in required device state: compliant. Conditional Access policy requires a compliant device, and the device is not compliant. Trace ID: 00000000-0000-0000-0000-000000000001 Correlation ID: 00000000-0000-0000-0000-000000000002 Timestamp: 2026-01-01 00:00:00Z";

    [Fact]
    public async Task GetComponentsAsync_LicenseCheckBlockedByConditionalAccess_ReportsTheEntraError()
    {
        var response = IslandError(HttpStatusCode.BadRequest, ErrorBody($"MSAL.NetCore.4.83.1.0.MsalClaimsChallengeException: ErrorCode: invalid_grant Microsoft.Identity.Client.MsalClaimsChallengeException: AADSTS53000: {ConditionalAccessDescription} The returned error contains a claims challenge. For additional info, see https://aka.ms/msal-conditional-access-claims.\r\n   at Microsoft.Identity.Client.Internal.Requests.RequestBase.HandleTokenRefreshErrorAsync()"));

        var failure = await Assert.ThrowsAsync<LicenseCheckSignInException>(() => GetComponentsAsync(response));

        Assert.True(failure.RequiresConditionalAccess);
        Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode);
        Assert.Equal("AADSTS53000", failure.ErrorCode);
        Assert.Equal(ConditionalAccessDescription, failure.ErrorDescription);
    }

    [Fact]
    public async Task GetComponentsAsync_ClaimsChallengeWithoutAnEntraError_ReportsConditionalAccess()
    {
        var response = IslandError(HttpStatusCode.BadRequest, string.Empty);
        response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Bearer", "error=\"insufficient_claims\", claims=\"eyJhY2Nlc3NfdG9rZW4iOnsiY2Fwb2xpZHMiOnsiZXNzZW50aWFsIjp0cnVlfX19\""));

        var failure = await Assert.ThrowsAsync<LicenseCheckSignInException>(() => GetComponentsAsync(response));

        Assert.True(failure.RequiresConditionalAccess);
        Assert.Null(failure.ErrorCode);
        Assert.Null(failure.ErrorDescription);
    }

    [Fact]
    public async Task GetComponentsAsync_EntraErrorWithoutConditionalAccess_ReportsASignInFailure()
    {
        var failure = await Assert.ThrowsAsync<LicenseCheckSignInException>(() => GetComponentsAsync(IslandError(HttpStatusCode.BadRequest, ErrorBody("AADSTS65001: The user or administrator has not consented to use the application."))));

        Assert.False(failure.RequiresConditionalAccess);
        Assert.Equal("AADSTS65001", failure.ErrorCode);
        Assert.Equal("The user or administrator has not consented to use the application.", failure.ErrorDescription);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "The delta token is invalid.")]
    [InlineData(HttpStatusCode.InternalServerError, "AADSTS7000215: Invalid client secret provided.")]
    public async Task GetComponentsAsync_ErrorThatIsNotARefusedUserSignIn_StillFailsAsAnHttpError(HttpStatusCode statusCode, string message)
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => GetComponentsAsync(IslandError(statusCode, ErrorBody(message))));
    }

    private static string ErrorBody(string message) => JsonSerializer.Serialize(new { error = new { code = "BadRequest", message } });

    private static HttpResponseMessage IslandError(HttpStatusCode statusCode, string body) => new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Task<PvaComponentChangeSet> GetComponentsAsync(HttpResponseMessage islandResponse)
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(factory => factory.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new IslandStub(islandResponse), disposeHandler: false));
        var service = new IslandControlPlaneService(Mock.Of<ISyncAuthProvider>(), Mock.Of<IContentAuthoringService>(), isIslandPreauthorized: true, httpClientFactory.Object);
        service.SetConnectionContext("https://island.example.test/", CoreServicesClusterCategory.Prod);
        return service.GetComponentsAsync(new AuthoringOperationContext(null, new CdsOrganizationInfo(), new BotReference("environment-id", Guid.NewGuid()), null, false), null, CancellationToken.None);
    }

    private sealed class IslandStub(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
    }
}
