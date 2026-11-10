// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Concurrent;
using System.Net;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public sealed class OriginGuardedDataverseHttpClientAccessorTests
{
    private static readonly Uri Contoso = new("https://contoso.crm.dynamics.com/");

    private static readonly Uri Fabrikam = new("https://fabrikam.crm.dynamics.com/");

    private readonly RecordingAuthProvider _authentication = new();

    private readonly RecordingTransport _transport = new();

    private int _transportsCreated;

    [Theory]
    [InlineData("https://contoso.crm.dynamics.com/api/data/v9.2/bots")]
    [InlineData("https://CONTOSO.crm.dynamics.com/api/data/v9.2/bots?$skiptoken=a")]
    [InlineData("https://contoso.crm.dynamics.com:443/")]
    public void IsSameOrigin_ForAnHttpsUriAtTheOrganizationsHostAndPort_IsTrue(string requestUri)
    {
        Assert.True(OriginGuardedDataverseHttpClientAccessor.IsSameOrigin(new Uri(requestUri), Contoso));
    }

    [Theory]
    [InlineData("http://contoso.crm.dynamics.com/api/data/v9.2/bots", "https://contoso.crm.dynamics.com/")]
    [InlineData("https://contoso.crm.dynamics.com:8443/api/data/v9.2/bots", "https://contoso.crm.dynamics.com/")]
    [InlineData("https://admin@contoso.crm.dynamics.com/api/data/v9.2/bots", "https://contoso.crm.dynamics.com/")]
    [InlineData("https://fabrikam.crm.dynamics.com/api/data/v9.2/bots", "https://contoso.crm.dynamics.com/")]
    [InlineData("https://contoso.crm.dynamics.com.fabrikam.com/api/data/v9.2/bots", "https://contoso.crm.dynamics.com/")]
    [InlineData("/api/data/v9.2/bots", "https://contoso.crm.dynamics.com/")]
    [InlineData(null, "https://contoso.crm.dynamics.com/")]
    [InlineData("http://contoso.crm.dynamics.com/api/data/v9.2/bots", "http://contoso.crm.dynamics.com/")]
    public void IsSameOrigin_ForAnyOtherUriOrAnOrganizationWithoutHttps_IsFalse(string? requestUri, string organization)
    {
        Assert.False(OriginGuardedDataverseHttpClientAccessor.IsSameOrigin(requestUri is null ? null : new Uri(requestUri, UriKind.RelativeOrAbsolute), new Uri(organization)));
    }

    [Fact]
    public void IsSameOrigin_WithoutAnOrganization_Throws()
    {
        Assert.Equal("organization", Assert.Throws<ArgumentNullException>(() => OriginGuardedDataverseHttpClientAccessor.IsSameOrigin(Contoso, null!)).ParamName);
    }

    [Fact]
    public void Constructor_WithoutARequiredArgument_Throws()
    {
        Func<HttpMessageHandler> createTransport = () => _transport;
        Func<Uri?, Uri, Exception> refuse = (_, _) => new InvalidOperationException("refused");

        Assert.Equal("authentication", Assert.Throws<ArgumentNullException>(() => new OriginGuardedDataverseHttpClientAccessor(null!, () => Contoso, createTransport, refuse)).ParamName);
        Assert.Equal("organization", Assert.Throws<ArgumentNullException>(() => new OriginGuardedDataverseHttpClientAccessor(_authentication, null!, createTransport, refuse)).ParamName);
        Assert.Equal("createTransport", Assert.Throws<ArgumentNullException>(() => new OriginGuardedDataverseHttpClientAccessor(_authentication, () => Contoso, null!, refuse)).ParamName);
        Assert.Equal("refuse", Assert.Throws<ArgumentNullException>(() => new OriginGuardedDataverseHttpClientAccessor(_authentication, () => Contoso, createTransport, null!)).ParamName);
    }

    [Fact]
    public async Task CreateClient_ForARequestOutsideTheOrganization_RaisesTheCallersRefusalBeforeAskingForATokenOrSending()
    {
        var refusals = new List<(Uri? RequestUri, Uri Organization)>();
        using var accessor = Accessor(() => Contoso, (requestUri, organization) =>
        {
            refusals.Add((requestUri, organization));
            return new InvalidOperationException("refused");
        });
        using var client = accessor.CreateClient();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync(new Uri(Fabrikam, "api/data/v9.2/bots")));

        var refusal = Assert.Single(refusals);
        Assert.Equal("refused", failure.Message);
        Assert.Equal(new Uri(Fabrikam, "api/data/v9.2/bots"), refusal.RequestUri);
        Assert.Equal(Contoso, refusal.Organization);
        Assert.Empty(_authentication.Audiences);
        Assert.Empty(_transport.Requests);
    }

    [Fact]
    public async Task CreateClient_TargetsTheOrganizationNamedWhenEachClientIsCreated()
    {
        var organization = Contoso;
        using var accessor = Accessor(() => organization);
        using var contoso = accessor.CreateClient();
        organization = Fabrikam;
        using var fabrikam = accessor.CreateClient();

        (await contoso.GetAsync(new Uri(Contoso, "api/data/v9.2/bots"))).Dispose();
        (await fabrikam.GetAsync(new Uri(Fabrikam, "api/data/v9.2/bots"))).Dispose();
        await Assert.ThrowsAsync<InvalidOperationException>(() => contoso.GetAsync(new Uri(Fabrikam, "api/data/v9.2/bots")));

        Assert.Equal(new[] { "token-for-contoso.crm.dynamics.com", "token-for-fabrikam.crm.dynamics.com" }, _transport.Requests.Select(request => request.Headers.Authorization!.Parameter));
        Assert.Equal(new[] { Contoso, Fabrikam }, _authentication.Audiences);
    }

    [Fact]
    public void CreateClient_WhileNoOrganizationIsSet_ThrowsBeforeCreatingTheTransport()
    {
        using var accessor = Accessor(() => null);

        Assert.Throws<InvalidOperationException>(() => accessor.CreateClient());
        Assert.Equal(0, _transportsCreated);
    }

    [Fact]
    public async Task CreateClient_SendsEveryClientThroughOneTransportThatOnlyTheAccessorDisposes()
    {
        var accessor = Accessor(() => Contoso);

        using (var first = accessor.CreateClient())
        {
            (await first.GetAsync(new Uri(Contoso, "api/data/v9.2/bots"))).Dispose();
        }

        using var second = accessor.CreateClient();
        (await second.GetAsync(new Uri(Contoso, "api/data/v9.2/workflows"))).Dispose();
        var disposedBeforeTheAccessor = _transport.Disposed;
        accessor.Dispose();

        Assert.Equal(1, _transportsCreated);
        Assert.Equal(2, _transport.Requests.Count);
        Assert.False(disposedBeforeTheAccessor);
        Assert.True(_transport.Disposed);
        Assert.Equal(OriginGuardedDataverseHttpClientAccessor.RequestTimeout, second.Timeout);
    }

    private OriginGuardedDataverseHttpClientAccessor Accessor(Func<Uri?> organization, Func<Uri?, Uri, Exception>? refuse = null) => new(_authentication, organization, () =>
    {
        _transportsCreated++;
        return _transport;
    }, refuse ?? ((_, _) => new InvalidOperationException("The request leaves the organization.")));

    private sealed class RecordingTransport : HttpMessageHandler
    {
        public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
