// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class IslandLicenseCheckSignInTests
{
    private const int InteractionRequiredErrorCode = 9101;
    private const string ConditionalAccessDescription = "Device is not in required device state: compliant. Conditional Access policy requires a compliant device, and the device is not compliant. The user must enroll their device with an approved MDM provider like Intune. Trace ID: 00000000-0000-0000-0000-000000000001 Correlation ID: 00000000-0000-0000-0000-000000000002 Timestamp: 2026-01-01 00:00:00Z";
    private const string ConditionalAccessClaims = "{\"access_token\":{\"capolids\":{\"essential\":true,\"values\":[\"00000000-0000-0000-0000-000000000003\"]}}}";

    [Fact]
    public async Task GetComponentsAsync_LicenseCheckBlockedByConditionalAccess_ReportsTheEntraError()
    {
        var body = IslandErrorBody(InteractionRequiredErrorCode, $"AADSTS53000: {ConditionalAccessDescription} The returned error contains a claims challenge. For additional info on how to handle claims related to multifactor authentication, Conditional Access, and incremental consent, see https://aka.ms/msal-conditional-access-claims. If you are using the On-Behalf-Of flow, see https://aka.ms/msal-conditional-access-claims-obo for details.", ConditionalAccessClaims);

        var failure = await Assert.ThrowsAsync<LicenseCheckSignInException>(() => GetComponentsAsync(IslandError(HttpStatusCode.BadRequest, body)));

        Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode);
        Assert.Equal("AADSTS53000", failure.ErrorCode);
        Assert.Equal(ConditionalAccessDescription, failure.ErrorDescription);
    }

    [Fact]
    public async Task GetComponentsAsync_LicenseCheckBlockedWithoutAnEntraErrorInTheMessage_StillReportsTheBlockedLicenseCheck()
    {
        var failure = await Assert.ThrowsAsync<LicenseCheckSignInException>(() => GetComponentsAsync(IslandError((HttpStatusCode)430, IslandErrorBody(InteractionRequiredErrorCode, "Missing claims.", ConditionalAccessClaims))));

        Assert.Equal((HttpStatusCode)430, failure.StatusCode);
        Assert.Null(failure.ErrorCode);
        Assert.Null(failure.ErrorDescription);
    }

    [Theory]
    [InlineData(@"{""ErrorCode"":400,""ErrorMessage"":""AADSTS7000215: Invalid client secret provided."",""DiagnosticsMessage"":null,""ErrorInfo"":null}")]
    [InlineData("AADSTS53000: Device is not in required device state: compliant.")]
    public async Task GetComponentsAsync_ErrorThatIslandDoesNotReportAsABlockedSignIn_StillFailsAsAnHttpError(string body)
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => GetComponentsAsync(IslandError(HttpStatusCode.BadRequest, body)));
    }

    private static string IslandErrorBody(int errorCode, string errorMessage, string? errorInfo) => JsonSerializer.Serialize(new { ErrorCode = errorCode, ErrorMessage = errorMessage, DiagnosticsMessage = (string?)null, ErrorInfo = errorInfo });

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
