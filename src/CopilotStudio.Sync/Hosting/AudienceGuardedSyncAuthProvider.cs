// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync.Hosting;

internal sealed class AudienceGuardedSyncAuthProvider : ISyncAuthProvider
{
    private readonly ISyncAuthProvider _authentication;

    public AudienceGuardedSyncAuthProvider(ISyncAuthProvider authentication, Uri organization)
    {
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        Organization = organization ?? throw new ArgumentNullException(nameof(organization));
    }

    public Uri Organization { get; }

    public Task<string> AcquireTokenAsync(Uri audience, CancellationToken cancellationToken = default) => Covers(audience) ? _authentication.AcquireTokenAsync(audience, cancellationToken) : throw Refuse("a token for", audience);

    public bool Covers(Uri? candidate) => OriginGuardedDataverseHttpClientAccessor.IsSameOrigin(candidate, Organization);

    public OriginGuardedDataverseHttpClientAccessor CreateDataverseAccessor(IHttpMessageHandlerFactory handlerFactory, string handlerName) => handlerFactory is null ? throw new ArgumentNullException(nameof(handlerFactory)) : new(this, () => Organization, () => handlerFactory.CreateHandler(handlerName), (requestUri, _) => Refuse("a request to", requestUri));

    public AgentWorkspaceHostException Refuse(string what, Uri? candidate) => new(AgentWorkspaceHostErrorCodes.UnexpectedAudience, $"The operation asked for {what} {(candidate is { IsAbsoluteUri: true } ? candidate.GetLeftPart(UriPartial.Authority) : "an unknown destination")}, but it may only use {Organization.GetLeftPart(UriPartial.Authority)}.", null);
}
