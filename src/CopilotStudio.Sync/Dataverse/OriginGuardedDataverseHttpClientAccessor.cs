// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net.Http.Headers;
using Microsoft.Agents.Platform.Content.Abstractions;

namespace Microsoft.CopilotStudio.Sync.Dataverse;

/// <summary>Creates the clients a Dataverse client sends through, each attaching the bearer token for its Dataverse organization and refusing, before it asks for a token or sends anything, a request that leaves that organization.</summary>
public sealed class OriginGuardedDataverseHttpClientAccessor : IDataverseHttpClientAccessor, IDisposable
{
    /// <summary>How long one Dataverse request may take before its client gives up.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    private readonly ISyncAuthProvider _authentication;

    private readonly Func<Uri?> _organization;

    private readonly Func<Uri?, Uri, Exception> _refuse;

    private readonly Lazy<HttpMessageHandler> _transport;

    /// <summary>Creates an accessor whose clients each target the organization named when the client is created and send through one transport created on first use and disposed with the accessor.</summary>
    /// <param name="authentication">Supplies the bearer token for the organization.</param>
    /// <param name="organization">Names the Dataverse organization the next client targets, or returns <see langword="null"/> while none is set.</param>
    /// <param name="createTransport">Creates the transport every client sends through.</param>
    /// <param name="refuse">Creates the exception a request outside the organization raises, from the request's URI and the organization.</param>
    public OriginGuardedDataverseHttpClientAccessor(ISyncAuthProvider authentication, Func<Uri?> organization, Func<HttpMessageHandler> createTransport, Func<Uri?, Uri, Exception> refuse)
    {
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _organization = organization ?? throw new ArgumentNullException(nameof(organization));
        _refuse = refuse ?? throw new ArgumentNullException(nameof(refuse));

        if (createTransport is null)
        {
            throw new ArgumentNullException(nameof(createTransport));
        }

        _transport = new Lazy<HttpMessageHandler>(() => new DataverseRetryHandler { InnerHandler = createTransport() });
    }

    internal static bool IsSameOrigin(Uri? requestUri, Uri organization) => organization is null ? throw new ArgumentNullException(nameof(organization)) : requestUri is { IsAbsoluteUri: true } && requestUri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(requestUri.UserInfo) && Uri.Compare(requestUri, organization, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>Creates a client for the organization the accessor names now, with the <see cref="RequestTimeout"/> timeout, or throws <see cref="InvalidOperationException"/> while it names none.</summary>
    /// <returns>A client whose requests carry the organization's bearer token.</returns>
    public HttpClient CreateClient() => new(new OriginGuardHandler(_authentication, _organization() ?? throw new InvalidOperationException("The Dataverse organization is not set."), _refuse, _transport.Value), disposeHandler: false) { Timeout = RequestTimeout };

    /// <summary>Disposes the transport every client of the accessor sent through.</summary>
    public void Dispose()
    {
        if (_transport.IsValueCreated)
        {
            _transport.Value.Dispose();
        }
    }

    private sealed class OriginGuardHandler : DelegatingHandler
    {
        private readonly ISyncAuthProvider _authentication;

        private readonly Uri _organization;

        private readonly Func<Uri?, Uri, Exception> _refuse;

        public OriginGuardHandler(ISyncAuthProvider authentication, Uri organization, Func<Uri?, Uri, Exception> refuse, HttpMessageHandler innerHandler)
            : base(innerHandler)
        {
            _authentication = authentication;
            _organization = organization;
            _refuse = refuse;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!IsSameOrigin(request.RequestUri, _organization))
            {
                throw _refuse(request.RequestUri, _organization);
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _authentication.AcquireTokenAsync(_organization, cancellationToken).ConfigureAwait(false));

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
