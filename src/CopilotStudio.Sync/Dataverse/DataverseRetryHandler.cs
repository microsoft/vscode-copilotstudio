// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net.Http;

namespace Microsoft.CopilotStudio.Sync.Dataverse;

public sealed class DataverseRetryHandler : DelegatingHandler
{
    private readonly DataverseRetryPolicy _policy;

    public DataverseRetryHandler() : this(DataverseRetryPolicy.Default)
    {
    }

    internal DataverseRetryHandler(DataverseRetryPolicy policy) => _policy = policy;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (DataverseRetryPolicy.IsSentUnderPolicy(request))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var content = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var response = await _policy.SendAsync(() => CopyOf(request, content), (attempt, attemptCancellation) => base.SendAsync(attempt, attemptCancellation), cancellationToken).ConfigureAwait(false);
        response.RequestMessage = request;

        return response;
    }

    private static HttpRequestMessage CopyOf(HttpRequestMessage request, byte[]? content)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };

        foreach (var header in request.Headers)
        {
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

#if NETSTANDARD2_0
        foreach (var property in request.Properties)
        {
            copy.Properties[property.Key] = property.Value;
        }
#else
        copy.VersionPolicy = request.VersionPolicy;

        foreach (var option in request.Options)
        {
            ((IDictionary<string, object?>)copy.Options)[option.Key] = option.Value;
        }
#endif

        if (content is not null)
        {
            copy.Content = new ByteArrayContent(content);

            var originalContent = request.Content
                ?? throw new InvalidOperationException("Buffered request content has no original content.");
            foreach (var header in originalContent.Headers)
            {
                copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return copy;
    }
}
