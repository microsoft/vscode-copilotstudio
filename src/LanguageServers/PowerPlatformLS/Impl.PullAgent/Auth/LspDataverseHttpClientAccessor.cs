// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.CopilotStudio.Sync;
using System.Net.Http.Headers;

namespace Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;

/// <summary>
/// IDataverseHttpClientAccessor for the shared sync library that creates HttpClients
/// with bearer token auth via ISyncAuthProvider. The Dataverse URL is set per-request
/// via AsyncLocal because the PullAgent LSP handles concurrent requests, each
/// potentially targeting a different Dataverse org.
/// </summary>
internal sealed class LspDataverseHttpClientAccessor : IDataverseHttpClientAccessor
{
    private readonly ISyncAuthProvider _authProvider;
    private readonly AsyncLocal<Uri?> _dataverseUri = new();

    public LspDataverseHttpClientAccessor(ISyncAuthProvider authProvider)
    {
        _authProvider = authProvider;
    }

    /// <summary>
    /// Sets the Dataverse org URL for the current async flow.
    /// Must be called before CreateClient() on each request path.
    /// </summary>
    public void SetDataverseUrl(Uri dataverseUri)
    {
        _dataverseUri.Value = dataverseUri;
    }

    public HttpClient CreateClient()
    {
        var uri = _dataverseUri.Value
            ?? throw new InvalidOperationException(
                "Dataverse URL not set. Call SetDataverseUrl() before CreateClient().");

#pragma warning disable CA2000 // handler is disposed by HttpClient (disposeHandler: true)
        var handler = new BearerTokenHandler(_authProvider, uri);
#pragma warning restore CA2000
#pragma warning disable CA5399 // HttpClient created with custom handler for bearer token auth
        return new HttpClient(handler, disposeHandler: true);
#pragma warning restore CA5399
    }

    private sealed class BearerTokenHandler : DelegatingHandler
    {
        private readonly ISyncAuthProvider _authProvider;
        private readonly Uri _audience;

        public BearerTokenHandler(ISyncAuthProvider authProvider, Uri audience)
            : base(new HttpClientHandler())
        {
            _authProvider = authProvider;
            _audience = audience;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var token = await _authProvider.AcquireTokenAsync(_audience, cancellationToken)
                .ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
