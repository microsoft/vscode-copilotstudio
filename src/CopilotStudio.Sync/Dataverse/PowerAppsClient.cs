// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Microsoft.CopilotStudio.Sync.Dataverse;

public class PowerAppsClient : IConnectionCatalogClient
{
    private const string ConnectionsApiVersion = "2016-11-01";
    private const int MaxCatalogPages = 50;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _httpClient;
    private readonly string _userAgent;
    private readonly TimeSpan _requestTimeout;

    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public PowerAppsClient(HttpClient httpClient, string userAgent = "CopilotStudio.Sync", TimeSpan? requestTimeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _userAgent = userAgent;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
    }

    public async Task<IReadOnlyList<ConnectionInstance>> ListConnectionsAsync(PowerAppsContext context, string connectorName, CancellationToken cancellationToken)
    {
        if (context == null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var normalizedConnector = LastSegment(connectorName);
        if (string.IsNullOrWhiteSpace(normalizedConnector) || string.IsNullOrWhiteSpace(context.EnvironmentId))
        {
            return Array.Empty<ConnectionInstance>();
        }

        var requestUri = BuildApisRequestUri(context, $"/{normalizedConnector}/connections");
        var result = new List<ConnectionInstance>();
        var pageCount = 0;
        while (!string.IsNullOrEmpty(requestUri) && pageCount < MaxCatalogPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = await SendRequestAsync<ConnectionListResponse>(requestUri!, context.AccessToken, "Connections", cancellationToken).ConfigureAwait(false);
            if (parsed?.Value != null)
            {
                foreach (var item in parsed.Value)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.Name))
                    {
                        continue;
                    }

                    var props = item.Properties;
                    var name = item.Name!;
                    result.Add(new ConnectionInstance
                    {
                        Name = name,
                        DisplayName = string.IsNullOrWhiteSpace(props?.DisplayName) ? name : props!.DisplayName!,
                        Status = ExtractStatus(props),
                        Owner = ExtractOwner(props),
                    });
                }
            }

            requestUri = parsed?.NextLink;
            pageCount++;
        }

        return result;
    }

    public async Task<IReadOnlyList<ConnectorInfo>> ListConnectorsAsync(PowerAppsContext context, CancellationToken cancellationToken)
    {
        if (context == null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        if (string.IsNullOrWhiteSpace(context.EnvironmentId))
        {
            return Array.Empty<ConnectorInfo>();
        }

        var requestUri = BuildApisRequestUri(context, string.Empty);
        var result = new List<ConnectorInfo>();
        var pageCount = 0;
        while (!string.IsNullOrEmpty(requestUri) && pageCount < MaxCatalogPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = await SendRequestAsync<ConnectorListResponse>(requestUri!, context.AccessToken, "Connectors", cancellationToken).ConfigureAwait(false);
            if (parsed?.Value != null)
            {
                foreach (var item in parsed.Value)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.Name))
                    {
                        continue;
                    }

                    var props = item.Properties;
                    var name = item.Name!;
                    result.Add(new ConnectorInfo
                    {
                        InternalId = name,
                        DisplayName = string.IsNullOrWhiteSpace(props?.DisplayName) ? name : props!.DisplayName!,
                        Publisher = props?.Publisher ?? string.Empty,
                        Tier = props?.Tier ?? string.Empty,
                        IconUri = props?.IconUri ?? string.Empty,
                    });
                }
            }

            requestUri = parsed?.NextLink;
            pageCount++;
        }

        return result;
    }

    private string BuildApisRequestUri(PowerAppsContext context, string pathSuffix)
    {
        var host = ResolveHost(context.ClusterCategory);
        var filter = Uri.EscapeDataString($"environment eq '{context.EnvironmentId.Replace("'", "''")}'");
        return $"https://{host}/providers/Microsoft.PowerApps/apis{pathSuffix}?api-version={ConnectionsApiVersion}&$filter={filter}";
    }

    private async Task<T?> SendRequestAsync<T>(string requestUri, string accessToken, string operationLabel, CancellationToken cancellationToken)
    {
        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, requestUri);
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        requestMessage.Headers.UserAgent.ParseAdd(_userAgent);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_requestTimeout);

        try
        {
            using var responseMessage = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            if (!responseMessage.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"{operationLabel} request failed ({(int)responseMessage.StatusCode}).");
            }

            using var responseStream = await responseMessage.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(responseStream, JsonSerializerOptions, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{operationLabel} request timed out after {_requestTimeout.TotalSeconds:0}s.");
        }
    }

    private static string ExtractStatus(ConnectionProperties? props)
    {
        if (props?.Statuses == null || props.Statuses.Length == 0)
        {
            return string.Empty;
        }

        return props.Statuses[0]?.Status ?? string.Empty;
    }

    private static string ExtractOwner(ConnectionProperties? props)
    {
        var createdBy = props?.CreatedBy;
        if (createdBy == null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(createdBy.Email))
        {
            return createdBy.Email!;
        }

        return createdBy.DisplayName ?? string.Empty;
    }

    private static string LastSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value!.Trim().TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
    }

    private static string ResolveHost(CoreServicesClusterCategory clusterCategory) => clusterCategory switch
    {
        CoreServicesClusterCategory.Gov => "gov.api.powerapps.us",
        CoreServicesClusterCategory.GovFR => "gov.api.powerapps.us",
        CoreServicesClusterCategory.High => "high.api.powerapps.us",
        CoreServicesClusterCategory.DoD => "api.apps.appsplatform.us",
        CoreServicesClusterCategory.Mooncake => "api.powerapps.cn",
        CoreServicesClusterCategory.Ex => "api.powerapps.eaglex.ic.gov",
        CoreServicesClusterCategory.Rx => "api.powerapps.microsoft.scloud",
        _ => "api.powerapps.com",
    };

    private sealed class ConnectionListResponse
    {
        [JsonPropertyName("value")]
        public ConnectionListItem[]? Value { get; set; }

        [JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; set; }
    }

    private sealed class ConnectionListItem
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("properties")]
        public ConnectionProperties? Properties { get; set; }
    }

    private sealed class ConnectionProperties
    {
        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("statuses")]
        public ConnectionStatus[]? Statuses { get; set; }

        [JsonPropertyName("createdBy")]
        public ConnectionPrincipal? CreatedBy { get; set; }
    }

    private sealed class ConnectionStatus
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }
    }

    private sealed class ConnectionPrincipal
    {
        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }
    }

    private sealed class ConnectorListResponse
    {
        [JsonPropertyName("value")]
        public ConnectorListItem[]? Value { get; set; }

        [JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; set; }
    }

    private sealed class ConnectorListItem
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("properties")]
        public ConnectorProperties? Properties { get; set; }
    }

    private sealed class ConnectorProperties
    {
        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("publisher")]
        public string? Publisher { get; set; }

        [JsonPropertyName("tier")]
        public string? Tier { get; set; }

        [JsonPropertyName("iconUri")]
        public string? IconUri { get; set; }
    }
}

public sealed class PowerAppsContext
{
    public string AccessToken { get; init; } = string.Empty;

    public string EnvironmentId { get; init; } = string.Empty;

    public CoreServicesClusterCategory ClusterCategory { get; init; }
}
