// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Microsoft.CopilotStudio.Sync.Dataverse;

internal sealed class DataverseRetryPolicy
{
    private const HttpStatusCode TooManyRequests = (HttpStatusCode)429;

    private const string ClientRequestIdHeader = "x-ms-client-request-id";

    private const string SentUnderPolicyOption = "Microsoft.CopilotStudio.Sync.Dataverse.SentUnderRetryPolicy";

    private static readonly HashSet<string> RepeatableMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "PUT", "PATCH", "DELETE" };

    private static readonly HashSet<HttpStatusCode> TransientStatusCodes = new() { HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout };

    private static readonly HashSet<string> TransientErrorCodes = new(StringComparer.OrdinalIgnoreCase) { "0x80040251", "0x80044151", "0x8004026C", "0x8004ED47", "0x80048D3D", "0x80098019" };

    private static readonly Random JitterSource = new();

    public static DataverseRetryPolicy Default { get; } = new();

    public int MaxAttempts { get; init; } = 3;

    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaximumDelay { get; init; } = TimeSpan.FromSeconds(30);

    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public static bool IsSentUnderPolicy(HttpRequestMessage request)
    {
#if NETSTANDARD2_0
        return request.Properties.ContainsKey(SentUnderPolicyOption);
#else
        return request.Options.TryGetValue(new HttpRequestOptionsKey<bool>(SentUnderPolicyOption), out var sentUnderPolicy) && sentUnderPolicy;
#endif
    }

    public Task<HttpResponseMessage> SendAsync(HttpClient httpClient, Func<HttpRequestMessage> requestFactory, HttpCompletionOption completionOption, CancellationToken cancellationToken) => SendAsync(requestFactory, (request, attemptCancellation) => httpClient.SendAsync(request, completionOption, attemptCancellation), cancellationToken);

    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> requestFactory, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        var clientRequestId = Guid.NewGuid().ToString();

        for (var attempt = 1; ; attempt++)
        {
            HttpMethod method;
            HttpResponseMessage response;

            using (var request = requestFactory())
            {
                Prepare(request, clientRequestId);
                method = request.Method;
                response = await send(request, cancellationToken).ConfigureAwait(false);
            }

            TimeSpan delay;

            try
            {
                if (attempt >= MaxAttempts || !await IsWorthRepeatingAsync(method, response, cancellationToken).ConfigureAwait(false))
                {
                    return response;
                }

                delay = DelayBefore(attempt, response);
            }
            catch
            {
                response.Dispose();

                throw;
            }

            response.Dispose();

            await Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Prepare(HttpRequestMessage request, string clientRequestId)
    {
        if (!request.Headers.Contains(ClientRequestIdHeader))
        {
            request.Headers.Add(ClientRequestIdHeader, clientRequestId);
        }

#if NETSTANDARD2_0
        request.Properties[SentUnderPolicyOption] = true;
#else
        request.Options.Set(new HttpRequestOptionsKey<bool>(SentUnderPolicyOption), true);
#endif
    }

    private static async Task<bool> IsWorthRepeatingAsync(HttpMethod method, HttpResponseMessage response, CancellationToken cancellationToken) => response.StatusCode == TooManyRequests || (RepeatableMethods.Contains(method.Method) && await IsTransientAsync(response, cancellationToken).ConfigureAwait(false));

    private static async Task<bool> IsTransientAsync(HttpResponseMessage response, CancellationToken cancellationToken) => TransientStatusCodes.Contains(response.StatusCode) || (response.StatusCode == HttpStatusCode.InternalServerError && response.Content is not null && ErrorCodeOf(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)) is { } errorCode && TransientErrorCodes.Contains(errorCode));

    private static string? ErrorCodeOf(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);

            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private TimeSpan DelayBefore(int retry, HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } retryAfter && (retryAfter.Delta ?? (retryAfter.Date - DateTimeOffset.UtcNow)) is { } requested)
        {
            return requested < TimeSpan.Zero ? TimeSpan.Zero : requested > MaximumDelay ? MaximumDelay : requested;
        }

        var backoff = Math.Min(MaximumDelay.Ticks, BaseDelay.Ticks * Math.Pow(2, retry - 1));

        return TimeSpan.FromTicks((long)(backoff * (0.5 + (NextJitter() / 2))));
    }

    private static double NextJitter()
    {
        lock (JitterSource)
        {
            return JitterSource.NextDouble();
        }
    }
}
