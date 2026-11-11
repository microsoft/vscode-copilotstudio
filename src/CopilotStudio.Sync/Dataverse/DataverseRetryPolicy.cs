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

    private const string SqlDeadlockMarker = "Sql Number: 1205";

    private const string ConcurrentDeleteErrorCode = "0x80048105";

    private static readonly HashSet<string> RepeatableMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "PUT", "PATCH", "DELETE" };

    private static readonly HashSet<HttpStatusCode> TransientStatusCodes = new() { HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout };

    private static readonly HashSet<string> TransientErrorCodes = new(StringComparer.OrdinalIgnoreCase) { "0x80040251", "0x80044151", "0x8004026C", "0x8004ED47", "0x80048D3D", "0x80098019" };

    private static readonly (string Code, string Message) UnknownError = (string.Empty, string.Empty);

    private static readonly Random JitterSource = new();

    public static DataverseRetryPolicy Default { get; } = new();

    public int MaxAttempts { get; init; } = 3;

    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaximumDelay { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ConcurrentDeleteWait { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ConcurrentDeleteBudget { get; init; } = TimeSpan.FromMinutes(3);

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
        var attempt = 1;
        var remainingConcurrentDeleteBudget = ConcurrentDeleteBudget;
        var rowMayAlreadyBeDeleted = false;

        while (true)
        {
            HttpMethod method;
            HttpResponseMessage response;

            try
            {
                using var request = requestFactory();
                Prepare(request, clientRequestId);
                method = request.Method;
                response = await send(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (attempt < MaxAttempts && FailedToConnect(exception))
            {
                await Delay(Backoff(attempt++), cancellationToken).ConfigureAwait(false);

                continue;
            }

            TimeSpan delay;

            try
            {
                switch (await DecideAsync(method, response, attempt < MaxAttempts, remainingConcurrentDeleteBudget >= ConcurrentDeleteWait, cancellationToken).ConfigureAwait(false))
                {
                    case RetryDecision.Backoff:
                        delay = DelayBefore(attempt++, response);
                        break;
                    case RetryDecision.WaitForConcurrentDelete:
                        delay = ConcurrentDeleteWait;
                        remainingConcurrentDeleteBudget -= ConcurrentDeleteWait;
                        break;
                    default:
                        return rowMayAlreadyBeDeleted && response.StatusCode == HttpStatusCode.NotFound ? RowGone(response) : response;
                }
            }
            catch
            {
                response.Dispose();

                throw;
            }

            rowMayAlreadyBeDeleted |= method == HttpMethod.Delete && response.StatusCode != TooManyRequests;
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

    private static async Task<RetryDecision> DecideAsync(HttpMethod method, HttpResponseMessage response, bool canBackOff, bool canWaitForConcurrentDelete, CancellationToken cancellationToken)
    {
        var repeatable = RepeatableMethods.Contains(method.Method);

        if (canBackOff && (response.StatusCode == TooManyRequests || (repeatable && TransientStatusCodes.Contains(response.StatusCode))))
        {
            return RetryDecision.Backoff;
        }

        if (canBackOff && repeatable && response.StatusCode == HttpStatusCode.InternalServerError)
        {
            var (code, message) = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);

            return TransientErrorCodes.Contains(code) || message.Contains(SqlDeadlockMarker, StringComparison.Ordinal) ? RetryDecision.Backoff : RetryDecision.None;
        }

        return canWaitForConcurrentDelete && method == HttpMethod.Delete && response.StatusCode == HttpStatusCode.BadRequest && string.Equals((await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false)).Code, ConcurrentDeleteErrorCode, StringComparison.OrdinalIgnoreCase) ? RetryDecision.WaitForConcurrentDelete : RetryDecision.None;
    }

    private static async Task<(string Code, string Message)> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            return UnknownError;
        }

        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object ? (StringPropertyOf(error, "code"), StringPropertyOf(error, "message")) : UnknownError;
        }
        catch (JsonException)
        {
            return UnknownError;
        }
    }

    private static string StringPropertyOf(JsonElement element, string propertyName) => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : string.Empty;

    private static bool FailedToConnect(HttpRequestException exception)
    {
#if NETSTANDARD2_0
        return false;
#else
        return exception.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError;
#endif
    }

    private static HttpResponseMessage RowGone(HttpResponseMessage notFound)
    {
        var noContent = new HttpResponseMessage(HttpStatusCode.NoContent) { RequestMessage = notFound.RequestMessage, Version = notFound.Version };
        notFound.Dispose();

        return noContent;
    }

    private TimeSpan DelayBefore(int retry, HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } retryAfter && (retryAfter.Delta ?? (retryAfter.Date - DateTimeOffset.UtcNow)) is { } requested)
        {
            return requested < TimeSpan.Zero ? TimeSpan.Zero : requested > MaximumDelay ? MaximumDelay : requested;
        }

        return Backoff(retry);
    }

    private TimeSpan Backoff(int retry) => TimeSpan.FromTicks((long)(Math.Min(MaximumDelay.Ticks, BaseDelay.Ticks * Math.Pow(2, retry - 1)) * (0.5 + (NextJitter() / 2))));

    private static double NextJitter()
    {
        lock (JitterSource)
        {
            return JitterSource.NextDouble();
        }
    }

    private enum RetryDecision
    {
        None,
        Backoff,
        WaitForConcurrentDelete,
    }
}
