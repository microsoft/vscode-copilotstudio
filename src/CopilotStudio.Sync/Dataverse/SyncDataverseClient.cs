// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Impl.PullAgent/Dataverse/DataverseClient.cs
// Key auth change: IHttpClientFactory → DataverseHttpClientAccessor (AuthenticatedHttpClientHandler handles auth)

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.CopilotStudio.McsCore;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace Microsoft.CopilotStudio.Sync.Dataverse;

public class SyncDataverseClient : ISyncDataverseClient
{
    private readonly IDataverseHttpClientAccessor _httpClientAccessor;
    private readonly AsyncLocal<string> _dataverseUrl = new();
    private string DataverseUrl => _dataverseUrl.Value
        ?? throw new InvalidOperationException("Dataverse URL is not set. Call SetDataverseUrl before making API calls.");

    private const int BatchSize = 50;

    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly string _userAgent;

    private static volatile string? _connectionReferenceCustomConnectorNavName;

    public SyncDataverseClient(IDataverseHttpClientAccessor httpClientAccessor, string userAgent = "CopilotStudio.Sync")
    {
        _httpClientAccessor = httpClientAccessor ?? throw new ArgumentNullException(nameof(httpClientAccessor));
        _userAgent = userAgent;
    }

    /// <inheritdoc />
    public void SetDataverseUrl(string dataverseUrl)
    {
        _dataverseUrl.Value = dataverseUrl ?? throw new ArgumentNullException(nameof(dataverseUrl));
    }

    public virtual async Task<AgentInfo> CreateNewAgentAsync(string displayName, string schemaName, AuthoringShape authoringShape, CancellationToken cancellationToken)
    {
        var requestBody = new Dictionary<string, object?>
        {
            ["name"] = displayName ?? throw new ArgumentNullException(nameof(displayName)),
            ["template"] = authoringShape == AuthoringShape.CliCopilot ? "cliagent-1.0.0" : "empty-1.0.0",
            ["schemaname"] = string.IsNullOrWhiteSpace(schemaName) ? null : schemaName
        };
        var requestUri = $"{DataverseUrl}/api/data/v9.2/bots";
        var response = await SendAsync<AgentSyncInfoDetail>(HttpMethod.Post, requestUri, requestBody, expectReturn: true, cancellationToken).ConfigureAwait(false);

        if (response == null || response.AgentId == Guid.Empty)
        {
            throw new InvalidOperationException("Dataverse API returned an invalid agent creation response.");
        }

        return response.ToAgentInfo();
    }

    public virtual async Task<Guid> GetAgentIdBySchemaNameAsync(string schemaName, CancellationToken cancellationToken)
    {
        var requestUri = $"{DataverseUrl}/api/data/v9.2/bots?$select=botid&$filter=schemaname eq '{schemaName?.Replace("'", "''")}'";
        var result = await SendAsync<AgentInfoDetail>(HttpMethod.Get, requestUri, null, false, cancellationToken).ConfigureAwait(false);
        return result?.Value?.FirstOrDefault()?.AgentId ?? Guid.Empty;
    }

    public virtual async Task<WorkflowMetadata[]> DownloadAllWorkflowsForAgentAsync(AgentSyncInfo syncInfo, CancellationToken cancellationToken)
    {
        var botComponentIdentifiers = await GetAllBotComponentIdsAsync(syncInfo, cancellationToken).ConfigureAwait(false);
        if (botComponentIdentifiers.Count > 0)
        {
            return await GetAllWorkflowsByBotComponentsAsync(botComponentIdentifiers, cancellationToken).ConfigureAwait(false);
        }

        return Array.Empty<WorkflowMetadata>();
    }

    public virtual async Task<WorkflowResponse> UpdateWorkflowAsync(Guid? agentId, WorkflowMetadata? workflowMetadata, CancellationToken cancellationToken)
    {
        if (workflowMetadata is null)
        {
            throw new ArgumentNullException(nameof(workflowMetadata));
        }

        var errorMessage = string.Empty;
        try
        {
            var existsInCloud = await WorkflowExistsAsync(workflowMetadata.WorkflowId, cancellationToken).ConfigureAwait(false);
            if (!existsInCloud && agentId.HasValue)
            {
                return await InsertWorkflowAsync(agentId, workflowMetadata, cancellationToken).ConfigureAwait(false);
            }

            if (existsInCloud)
            {
                var requestBody = CreateWorkflowRequestBody(workflowMetadata);
                var updateUrl = $"{DataverseUrl}/api/data/v9.2/workflows({workflowMetadata.WorkflowId})";
                await SendAsync<object>(HttpMethodHelper.Patch, updateUrl, requestBody, false, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errorMessage = $"Failed to update workflow: {ex.Message}";
        }

        return new WorkflowResponse
        {
            WorkflowName = workflowMetadata.Name ?? workflowMetadata.WorkflowId.ToString(),
            IsDisabled = errorMessage.Contains("this is not a valid connection", StringComparison.OrdinalIgnoreCase) || !(workflowMetadata.StateCode == 1 && workflowMetadata.StatusCode == 2),
            ErrorMessage = errorMessage
        };
    }

    public virtual async Task<WorkflowResponse> InsertWorkflowAsync(Guid? agentId, WorkflowMetadata? workflowMetadata, CancellationToken cancellationToken)
    {
        if (workflowMetadata is null)
        {
            throw new ArgumentNullException(nameof(workflowMetadata));
        }

        if (!agentId.HasValue || agentId == Guid.Empty)
        {
            throw new ArgumentNullException(nameof(agentId));
        }

        var errorMessage = string.Empty;
        try
        {
            workflowMetadata.StateCode = null;
            workflowMetadata.StatusCode = null;
            var requestBody = CreateWorkflowRequestBody(workflowMetadata);
            requestBody["workflowid"] = workflowMetadata.WorkflowId;

            var createResponse = await SendAsync<JsonElement>(
                HttpMethod.Post,
                $"{DataverseUrl}/api/data/v9.2/workflows",
                requestBody,
                expectReturn: true,
                cancellationToken
            ).ConfigureAwait(false);

            await ActivateWorkflowAsync(workflowMetadata.WorkflowId, cancellationToken).ConfigureAwait(false);
            workflowMetadata.StateCode = 1;
            workflowMetadata.StatusCode = 2;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errorMessage = $"Failed to create workflow: {ex.Message}";
        }

        return new WorkflowResponse
        {
            WorkflowName = workflowMetadata.Name ?? workflowMetadata.WorkflowId.ToString(),
            IsDisabled = !(workflowMetadata.StateCode == 1 && workflowMetadata.StatusCode == 2),
            ErrorMessage = errorMessage
        };
    }

    public virtual async Task<ConnectionReferenceInfo[]> GetConnectionReferencesByLogicalNamesAsync(IEnumerable<string> logicalNames, CancellationToken cancellationToken)
    {
        var names = logicalNames?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Replace("'", "''")).Distinct().ToList();
        if (names == null || names.Count == 0)
        {
            return Array.Empty<ConnectionReferenceInfo>();
        }

        var filter = string.Join(" or ", names.Select(n => $"connectionreferencelogicalname eq '{n}'"));
        var requestUri = $"{DataverseUrl}/api/data/v9.2/connectionreferences?$select=connectionreferenceid,connectionreferencelogicalname,connectorid,connectionid&$filter={Uri.EscapeDataString(filter)}";

        var response = await SendAsync<ConnectionReferenceQueryResponse>(HttpMethod.Get, requestUri, null, false, cancellationToken).ConfigureAwait(false);
        return response?.Value ?? Array.Empty<ConnectionReferenceInfo>();
    }

    public static string? ExtractConnectorInternalId(string? connectorIdPath)
    {
        if (string.IsNullOrWhiteSpace(connectorIdPath))
        {
            return null;
        }

        var slash = connectorIdPath!.LastIndexOf('/');
        var segment = slash >= 0 ? connectorIdPath.Substring(slash + 1) : connectorIdPath;
        return string.IsNullOrWhiteSpace(segment) ? null : segment;
    }

    public virtual async Task<CustomConnectorMetadata[]> DownloadConnectorsByInternalIdsAsync(IEnumerable<string> connectorInternalIds, bool isManaged, CancellationToken cancellationToken)
    {
        if (connectorInternalIds == null)
        {
            return Array.Empty<CustomConnectorMetadata>();
        }

        var ids = connectorInternalIds.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (ids.Count == 0)
        {
            return Array.Empty<CustomConnectorMetadata>();
        }

        const string select =
            "connectorid,name,displayname,description,connectorinternalid," +
            "openapidefinition,connectionparameters,connectionparametersets," +
            "policytemplateinstances," +
            "iconbrandcolor,iconblob,connectortype,statecode,statuscode," +
            "ismanaged,componentstate,createdon,modifiedon,versionnumber";

        var results = new List<CustomConnectorMetadata>();
        var seenConnectorIds = new HashSet<Guid>();

        foreach (var batch in ids.Chunk(BatchSize))
        {
            var idClauses = string.Join(" or ", batch.Select(id => $"connectorinternalid eq '{id.Replace("'", "''")}'"));
            var filter = isManaged ? $"({idClauses})" : $"({idClauses}) and ismanaged eq false";
            string? next = $"{DataverseUrl}/api/data/v9.2/connectors" + $"?$select={select}" + $"&$filter={Uri.EscapeDataString(filter)}";

            while (!string.IsNullOrEmpty(next))
            {
                var page = await SendAsync<JsonElement>(HttpMethod.Get, next!, null, false, cancellationToken).ConfigureAwait(false);

                if (page.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        var meta = JsonSerializer.Deserialize<CustomConnectorMetadata>(el.GetRawText(), JsonSerializerOptions);
                        if (meta != null && meta.ConnectorId != Guid.Empty && seenConnectorIds.Add(meta.ConnectorId))
                        {
                            results.Add(meta);
                        }
                    }
                }

                next = page.TryGetProperty("@odata.nextLink", out var nextLinkProp) ? nextLinkProp.GetString() : null;
            }
        }

        return results.ToArray();
    }

    public virtual async Task<CustomConnectorMetadata[]> GetConnectorsByInternalIdPrefixAsync(string connectorInternalIdPrefix, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectorInternalIdPrefix))
        {
            return Array.Empty<CustomConnectorMetadata>();
        }

        const string select = "connectorid,name,displayname,connectorinternalid,connectortype,ismanaged,modifiedon";
        var filter = $"startswith(connectorinternalid,'{connectorInternalIdPrefix.Replace("'", "''")}')";
        string? next = $"{DataverseUrl}/api/data/v9.2/connectors?$select={select}&$filter={Uri.EscapeDataString(filter)}";

        var results = new List<CustomConnectorMetadata>();
        var seenConnectorIds = new HashSet<Guid>();

        while (!string.IsNullOrEmpty(next))
        {
            var page = await SendAsync<JsonElement>(HttpMethod.Get, next!, null, false, cancellationToken).ConfigureAwait(false);

            if (page.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var meta = JsonSerializer.Deserialize<CustomConnectorMetadata>(el.GetRawText(), JsonSerializerOptions);
                    if (meta != null && meta.ConnectorId != Guid.Empty && seenConnectorIds.Add(meta.ConnectorId))
                    {
                        results.Add(meta);
                    }
                }
            }

            next = page.TryGetProperty("@odata.nextLink", out var nextLinkProp) ? nextLinkProp.GetString() : null;
        }

        return results.ToArray();
    }

    public virtual async Task<bool> UpsertConnectorAsync(CustomConnectorMetadata connector, CancellationToken cancellationToken)
    {
        if (connector == null)
        {
            throw new ArgumentNullException(nameof(connector));
        }

        if (connector.ConnectorId == Guid.Empty)
        {
            throw new ArgumentException("Connector must have a non-empty ConnectorId.", nameof(connector));
        }

        var exists = await ConnectorExistsAsync(connector.ConnectorId, cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            await UpdateConnectorAsync(connector, cancellationToken).ConfigureAwait(false);
            return false;
        }
        else
        {
            await CreateConnectorAsync(connector, cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    private async Task<bool> ConnectorExistsAsync(Guid connectorId, CancellationToken cancellationToken)
    {
        var url = $"{DataverseUrl}/api/data/v9.2/connectors({connectorId})?$select=connectorid";
        try
        {
            await SendAsync<object>(HttpMethod.Get, url, null, false, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("(404)"))
        {
            return false;
        }
    }

    private async Task UpdateConnectorAsync(CustomConnectorMetadata connector, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = connector.Name,
            ["displayname"] = connector.DisplayName,
            ["description"] = connector.Description,
            ["openapidefinition"] = connector.OpenApiDefinition,
            ["connectionparameters"] = connector.ConnectionParameters,
            ["connectionparametersets"] = connector.ConnectionParameterSets,
            ["policytemplateinstances"] = connector.PolicyTemplateInstances,
            ["iconbrandcolor"] = connector.IconBrandColor,
            ["iconblob"] = connector.IconBlobBase64,
        }
        .Where(kv => kv.Value != null)
        .ToDictionary(kv => kv.Key, kv => kv.Value);

        var url = $"{DataverseUrl}/api/data/v9.2/connectors({connector.ConnectorId})";
        await SendAsync<object>(HttpMethodHelper.Patch, url, body, false, cancellationToken).ConfigureAwait(false);
    }

    private async Task CreateConnectorAsync(CustomConnectorMetadata connector, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["connectorid"] = connector.ConnectorId,
            ["name"] = connector.Name,
            ["displayname"] = connector.DisplayName,
            ["description"] = connector.Description,
            ["connectorinternalid"] = connector.ConnectorInternalId,
            ["openapidefinition"] = connector.OpenApiDefinition,
            ["connectionparameters"] = connector.ConnectionParameters,
            ["connectionparametersets"] = connector.ConnectionParameterSets,
            ["policytemplateinstances"] = connector.PolicyTemplateInstances,
            ["iconbrandcolor"] = connector.IconBrandColor,
            ["iconblob"] = connector.IconBlobBase64,
            ["connectortype"] = connector.ConnectorType,
        }
        .Where(kv => kv.Value != null)
        .ToDictionary(kv => kv.Key, kv => kv.Value);

        var url = $"{DataverseUrl}/api/data/v9.2/connectors";
        await SendAsync<object>(HttpMethod.Post, url, body, false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> WorkflowExistsAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        var checkUrl = $"{DataverseUrl}/api/data/v9.2/workflows({workflowId})?$select=workflowid";
        try
        {
            await SendAsync<object>(HttpMethod.Get, checkUrl, null, false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("(404)", StringComparison.Ordinal))
        {
            return false;
        }
        return true;
    }

    private Dictionary<string, object?> CreateWorkflowRequestBody(WorkflowMetadata m) =>
        new Dictionary<string, object?>(23)
        {
            ["name"] = m.Name,
            ["type"] = m.Type,
            ["description"] = m.Description,
            ["subprocess"] = m.Subprocess,
            ["category"] = m.Category,
            ["mode"] = m.Mode,
            ["scope"] = m.Scope,
            ["ondemand"] = m.OnDemand,
            ["triggeroncreate"] = m.TriggerOnCreate,
            ["triggerondelete"] = m.TriggerOnDelete,
            ["asyncautodelete"] = m.AsyncAutodelete,
            ["syncworkflowlogonfailure"] = m.SyncWorkflowLogOnFailure,
            ["runas"] = m.RunAs,
            ["istransacted"] = m.IsTransacted,
            ["introducedversion"] = m.IntroducedVersion,
            ["iscustomizable"] = m.IsCustomizable,
            ["businessprocesstype"] = m.BusinessProcessType,
            ["iscustomprocessingstepallowedforotherpublishers"] = m.IsCustomProcessingStepAllowedForOtherPublishers,
            ["modernflowtype"] = m.ModernFlowType,
            ["primaryentity"] = m.PrimaryEntity,
            ["clientdata"] = m.ClientData,
            ["statecode"] = m.StateCode,
            ["statuscode"] = m.StatusCode,
        }
        .Where(kv => kv.Value is not null)
        .ToDictionary(kv => kv.Key, kv => kv.Value);

    private async Task ActivateWorkflowAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        var activateUrl = $"{DataverseUrl}/api/data/v9.2/workflows({workflowId})";
        var activateBody = new Dictionary<string, object?>
        {
            ["statecode"] = 1,
            ["statuscode"] = 2
        };

        await SendAsync<object>(HttpMethodHelper.Patch, activateUrl, activateBody, false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<Guid>> GetAllBotComponentIdsAsync(AgentSyncInfo syncInfo, CancellationToken cancellationToken)
    {
        string url = string.Empty;
        if (syncInfo.AgentId.HasValue && syncInfo.AgentId != Guid.Empty)
        {
            url = $"{DataverseUrl}/api/data/v9.2/botcomponents?$select=botcomponentid&$filter=_parentbotid_value eq {syncInfo.AgentId} and componenttype ne 19";
        }
        else if (syncInfo.ComponentCollectionId.HasValue && syncInfo.ComponentCollectionId != Guid.Empty)
        {
            url = $"{DataverseUrl}/api/data/v9.2/botcomponents?$select=botcomponentid&$filter=_parentbotcomponentcollectionid_value eq {syncInfo.ComponentCollectionId} and componenttype ne 19";
        }

        if (string.IsNullOrEmpty(url))
        {
            return new List<Guid>();
        }

        var result = await SendAsync<BotComponentListResponse>(HttpMethod.Get, url, null, false, cancellationToken).ConfigureAwait(false);

        return result?.Value?.Select(component => component.BotComponentId).ToList() ?? new List<Guid>();
    }

    private async Task<WorkflowMetadata[]> GetAllWorkflowsByBotComponentsAsync(List<Guid> botComponentIds, CancellationToken cancellationToken)
    {
        if (botComponentIds == null || botComponentIds.Count == 0)
        {
            return Array.Empty<WorkflowMetadata>();
        }

        var workflowIdToBotComponentMap = new Dictionary<Guid, BotComponentWorkflowMetadata>();

        foreach (var batch in botComponentIds.Chunk(BatchSize))
        {
            string? nextBotComponentWorkflowUrl = $"{DataverseUrl}/api/data/v9.2/botcomponent_workflowset?$select=workflowid,botcomponentid";

            var filterQuery = string.Join(" or ", batch.Select(id => $"botcomponentid eq {id}"));
            nextBotComponentWorkflowUrl += $"&$filter={Uri.EscapeDataString(filterQuery)}";

            while (!string.IsNullOrEmpty(nextBotComponentWorkflowUrl))
            {
                // ns2.0 BCL's IsNullOrEmpty lacks NotNullWhen annotation; ! is compile-time only.
                var response = await SendAsync<ODataResponse<BotComponentWorkflowMetadata>>(HttpMethod.Get, nextBotComponentWorkflowUrl!, null, false, cancellationToken).ConfigureAwait(false);

                if (response?.Value != null)
                {
                    foreach (var link in response.Value)
                    {
                        if (link.WorkflowId != Guid.Empty && !workflowIdToBotComponentMap.ContainsKey(link.WorkflowId))
                        {
                            workflowIdToBotComponentMap[link.WorkflowId] = link;
                        }
                    }
                }

                nextBotComponentWorkflowUrl = response?.NextLink;
            }
        }

        if (workflowIdToBotComponentMap.Count == 0)
        {
            return Array.Empty<WorkflowMetadata>();
        }

        var workflows = new List<WorkflowMetadata>();

        foreach (var batch in workflowIdToBotComponentMap.Keys.Chunk(BatchSize))
        {
            string? nextWorkflowUrl = $"{DataverseUrl}/api/data/v9.2/workflows?" +
                                      "$select=workflowid,name,description,type,subprocess,category,mode,scope,ondemand," +
                                      "triggeroncreate,triggerondelete,asyncautodelete,syncworkflowlogonfailure,statecode,statuscode,runas," +
                                      "istransacted,introducedversion,iscustomizable,businessprocesstype," +
                                      "iscustomprocessingstepallowedforotherpublishers,modernflowtype,primaryentity," +
                                      "createdon,modifiedon,clientdata";

            var workflowFilterQuery = string.Join(" or ", batch.Select(id => $"workflowid eq {id}"));
            nextWorkflowUrl += $"&$filter={Uri.EscapeDataString(workflowFilterQuery)}";

            while (!string.IsNullOrEmpty(nextWorkflowUrl))
            {
                // ns2.0 BCL's IsNullOrEmpty lacks NotNullWhen annotation; ! is compile-time only.
                var response = await SendAsync<ODataResponse<WorkflowMetadata>>(HttpMethod.Get, nextWorkflowUrl!, null, false, cancellationToken).ConfigureAwait(false);
                if (response?.Value != null)
                {
                    workflows.AddRange(response.Value);
                }

                nextWorkflowUrl = response?.NextLink;
            }
        }

        return workflows.ToArray();
    }

    private async Task<T?> SendAsync<T>(HttpMethod httpMethod, string requestUrl, object? requestBody, bool expectReturn, CancellationToken cancellationToken)
    {
        // Auth is handled by the DataverseHttpClientAccessor's AuthenticatedHttpClientHandler.
        // The accessor owns HttpClient lifetime — callers must not dispose.
        var httpClient = _httpClientAccessor.CreateClient();
        using var requestMessage = new HttpRequestMessage(httpMethod, requestUrl);
        requestMessage.Headers.Add("OData-MaxVersion", "4.0");
        requestMessage.Headers.Add("OData-Version", "4.0");
        requestMessage.Headers.UserAgent.ParseAdd(_userAgent);

        if (expectReturn)
        {
            requestMessage.Headers.Add("Prefer", "return=representation");
        }

        if (requestBody != null)
        {
            var bodyStream = new MemoryStream();
            await JsonSerializer.SerializeAsync(bodyStream, requestBody, JsonSerializerOptions, cancellationToken).ConfigureAwait(false);
            bodyStream.Position = 0;
            var bodyContent = new StreamContent(bodyStream);
            bodyContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            requestMessage.Content = bodyContent;
        }

        using var responseMessage = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (!responseMessage.IsSuccessStatusCode)
        {
            var responseText = await responseMessage.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Dataverse request failed ({(int)responseMessage.StatusCode}): {responseText}");
        }

        if (typeof(T) == typeof(object)
            || responseMessage.StatusCode == System.Net.HttpStatusCode.NoContent
            || responseMessage.Content.Headers.ContentLength == 0)
        {
            return default;
        }

        using var responseStream = await responseMessage.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        if (responseStream.CanSeek)
        {
            if (responseStream.Length == 0)
            {
                return default;
            }

            return await JsonSerializer.DeserializeAsync<T>(responseStream, JsonSerializerOptions, cancellationToken).ConfigureAwait(false);
        }

        using var bufferStream = new MemoryStream();
        await responseStream.CopyToAsync(bufferStream, 81920, cancellationToken).ConfigureAwait(false);
        if (bufferStream.Length == 0)
        {
            return default;
        }

        bufferStream.Position = 0;
        return await JsonSerializer.DeserializeAsync<T>(bufferStream, JsonSerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    public virtual async Task<bool> ConnectionReferenceExistsAsync(
        string connectionReferenceLogicalName,
        CancellationToken cancellationToken)
    {
        if (connectionReferenceLogicalName is null) throw new ArgumentNullException(nameof(connectionReferenceLogicalName));
        var literal = connectionReferenceLogicalName.Replace("'", "''");
        var filterExpr = $"connectionreferencelogicalname eq '{literal}'";
        var baseUri = new Uri(new Uri(DataverseUrl), "/api/data/v9.2/connectionreferences");
        var requestUri = new Uri($"{baseUri}?$select=connectionreferenceid&$top=1&$filter={Uri.EscapeDataString(filterExpr)}");

        var queryResponse = await SendAsync<ConnectionReferenceQueryResponse>(
            HttpMethod.Get,
            requestUri.ToString(),
            null,
            false,
            cancellationToken
        ).ConfigureAwait(false);

        return queryResponse?.Value != null && queryResponse.Value.Length > 0;
    }

    public virtual async Task CreateConnectionReferenceAsync(
        string connectionReferenceLogicalName,
        string connectorId,
        CancellationToken cancellationToken,
        Guid? customConnectorRowId = null)
    {
        var requestUri = new Uri(new Uri(DataverseUrl), "/api/data/v9.2/connectionreferences");

        var body = new Dictionary<string, object>
        {
            ["connectionreferencelogicalname"] = connectionReferenceLogicalName,
            ["connectorid"] = connectorId
        };

        if (customConnectorRowId.HasValue)
        {
            var navName = await GetCustomConnectorNavigationPropertyNameAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(navName))
            {
                body[$"{navName}@odata.bind"] = $"/connectors({customConnectorRowId.Value})";
            }
        }

        await SendAsync<object>(
            HttpMethod.Post,
            requestUri.ToString(),
            body,
            false,
            cancellationToken
        ).ConfigureAwait(false);
    }

    private async Task<string?> GetCustomConnectorNavigationPropertyNameAsync(CancellationToken cancellationToken)
    {
        var cached = _connectionReferenceCustomConnectorNavName;
        if (cached != null)
        {
            return cached;
        }

        try
        {
            var url = $"{DataverseUrl}/api/data/v9.2/EntityDefinitions(LogicalName='connectionreference')/ManyToOneRelationships" +
                      "?$select=ReferencingAttribute,ReferencingEntityNavigationPropertyName" +
                      "&$filter=ReferencingAttribute eq 'customconnectorid'";
            var resp = await SendAsync<JsonElement>(HttpMethod.Get, url, null, false, cancellationToken).ConfigureAwait(false);
            if (resp.ValueKind == JsonValueKind.Object &&
                resp.TryGetProperty("value", out var arr) &&
                arr.ValueKind == JsonValueKind.Array &&
                arr.GetArrayLength() > 0 &&
                arr[0].TryGetProperty("ReferencingEntityNavigationPropertyName", out var navProp))
            {
                var name = navProp.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _connectionReferenceCustomConnectorNavName = name;
                    return name;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Metadata lookup failed; return null.
        }

        return null;
    }

    public virtual async Task<SolutionInfo> GetSolutionVersionsAsync(CancellationToken cancellationToken)
    {
        var solutionNames = new[] { "PowerVirtualAgents", "msdyn_RelevanceSearch", "msft_AIPlatformExtensionsComponents" };
        var filterQuery = string.Join(" or ", solutionNames.Select(s => $"uniquename eq '{s}'"));
        var requestUri = $"{DataverseUrl}/api/data/v9.2/solutions?$select=uniquename,version&$filter={filterQuery}";

        var response = await SendAsync<SolutionQueryResponse>(HttpMethod.Get, requestUri, null, false, cancellationToken).ConfigureAwait(false);

        var solutionInfo = new SolutionInfo();
        foreach (var solution in response?.Value ?? Array.Empty<SolutionData>())
        {
            if (!Version.TryParse(solution.Version, out var version))
            {
                continue;
            }

            if (string.Equals(solution.UniqueName, "PowerVirtualAgents", StringComparison.OrdinalIgnoreCase))
            {
                solutionInfo.CopilotStudioSolutionVersion = version;
            }
            else
            {
                solutionInfo.SolutionVersions[solution.UniqueName] = version;
            }
        }

        solutionInfo.CopilotStudioSolutionVersion ??= new Version(1, 0, 0);

        return solutionInfo;
    }

    public virtual async Task<AgentInfo> GetAgentInfoAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var requestUri = $"{DataverseUrl}/api/data/v9.2/bots({agentId})" +
                         "?$select=botid,name,iconbase64" +
                         "&$expand=bot_botcomponentcollection($select=schemaname,botcomponentcollectionid,name)";

        var response = await SendAsync<AgentDetailWithCollections>(HttpMethod.Get, requestUri, null, false, cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException($"Agent {agentId} not found in Dataverse.");

        return new AgentInfo
        {
            AgentId = response.AgentId,
            DisplayName = response.Name,
            IconBase64 = response.IconBase64,
            ComponentCollections = (response.ComponentCollections ?? Array.Empty<ComponentCollectionDetail>())
                .Select(cc => new ComponentCollectionInfo
                {
                    Id = cc.BotComponentCollectionId,
                    SchemaName = cc.SchemaName,
                    DisplayName = cc.Name
                })
                .ToList()
        };
    }

    public virtual async Task EnsureConnectionReferenceExistsAsync(
        string connectionReferenceLogicalName,
        string connectorId,
        CancellationToken cancellationToken,
        Guid? customConnectorRowId = null)
    {
        var existing = await GetConnectionReferenceByLogicalNameAsync(connectionReferenceLogicalName, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await CreateConnectionReferenceAsync(connectionReferenceLogicalName, connectorId, cancellationToken, customConnectorRowId).ConfigureAwait(false);
            return;
        }

        var desiredInternalId = ExtractConnectorInternalId(connectorId);
        var existingInternalId = ExtractConnectorInternalId(existing.ConnectorId);
        if (!string.IsNullOrWhiteSpace(desiredInternalId) &&
            !string.IsNullOrWhiteSpace(existingInternalId) &&
            !string.Equals(existingInternalId, desiredInternalId, StringComparison.OrdinalIgnoreCase))
        {
            await UpdateConnectionReferenceConnectorAsync(existing.ConnectionReferenceId, connectorId, customConnectorRowId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ConnectionReferenceInfo?> GetConnectionReferenceByLogicalNameAsync(string connectionReferenceLogicalName, CancellationToken cancellationToken)
    {
        if (connectionReferenceLogicalName is null)
        {
            throw new ArgumentNullException(nameof(connectionReferenceLogicalName));
        }

        var literal = connectionReferenceLogicalName.Replace("'", "''");
        var filterExpr = $"connectionreferencelogicalname eq '{literal}'";
        var baseUri = new Uri(new Uri(DataverseUrl), "/api/data/v9.2/connectionreferences");
        var requestUri = new Uri($"{baseUri}?$select=connectionreferenceid,connectionreferencelogicalname,connectorid,connectionid&$top=1&$filter={Uri.EscapeDataString(filterExpr)}");

        var queryResponse = await SendAsync<ConnectionReferenceQueryResponse>(
            HttpMethod.Get,
            requestUri.ToString(),
            null,
            false,
            cancellationToken
        ).ConfigureAwait(false);

        var existing = queryResponse?.Value;
        return existing != null && existing.Length > 0 ? existing[0] : null;
    }

    private async Task UpdateConnectionReferenceConnectorAsync(Guid connectionReferenceId, string connectorId, Guid? customConnectorRowId, CancellationToken cancellationToken)
    {
        var patchUri = new Uri(new Uri(DataverseUrl), $"/api/data/v9.2/connectionreferences({connectionReferenceId})");

        var body = new Dictionary<string, object>
        {
            ["connectorid"] = connectorId
        };

        if (customConnectorRowId.HasValue)
        {
            var navName = await GetCustomConnectorNavigationPropertyNameAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(navName))
            {
                body[$"{navName}@odata.bind"] = $"/connectors({customConnectorRowId.Value})";
            }
        }

        await SendAsync<object>(HttpMethodHelper.Patch, patchUri.ToString(), body, false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Binds a connection reference to a connection by setting its connectionid.
    /// </summary>
    /// <param name="connectionReferenceLogicalName">Logical name of the connection reference to bind.</param>
    /// <param name="connectionLogicalName">The bound connection's logical name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="connectionReferenceDisplayName">Optional display name to set on the reference.</param>
    public virtual async Task BindConnectionReferenceAsync(string connectionReferenceLogicalName, string connectionLogicalName, CancellationToken cancellationToken, string? connectionReferenceDisplayName = null)
    {
        if (connectionReferenceLogicalName is null)
        {
            throw new ArgumentNullException(nameof(connectionReferenceLogicalName));
        }

        if (string.IsNullOrWhiteSpace(connectionLogicalName))
        {
            throw new ArgumentException("Connection logical name is required.", nameof(connectionLogicalName));
        }

        var literal = connectionReferenceLogicalName.Replace("'", "''");
        var filterExpr = $"connectionreferencelogicalname eq '{literal}'";
        var baseUri = new Uri(new Uri(DataverseUrl), "/api/data/v9.2/connectionreferences");
        var queryUri = new Uri($"{baseUri}?$select=connectionreferenceid&$top=1&$filter={Uri.EscapeDataString(filterExpr)}");

        var queryResponse = await SendAsync<ConnectionReferenceQueryResponse>(HttpMethod.Get, queryUri.ToString(), null, false, cancellationToken).ConfigureAwait(false);

        var existing = queryResponse?.Value;
        if (existing == null || existing.Length == 0)
        {
            throw new InvalidOperationException($"Connection reference '{connectionReferenceLogicalName}' was not found in Dataverse.");
        }

        var connectionReferenceId = existing[0].ConnectionReferenceId;
        var patchUri = new Uri(new Uri(DataverseUrl), $"/api/data/v9.2/connectionreferences({connectionReferenceId})");

        var body = new Dictionary<string, object>
        {
            ["connectionid"] = connectionLogicalName
        };

        if (!string.IsNullOrWhiteSpace(connectionReferenceDisplayName))
        {
            body["connectionreferencedisplayname"] = connectionReferenceDisplayName!;
        }

        await SendAsync<object>(HttpMethodHelper.Patch, patchUri.ToString(), body, false, cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadKnowledgeFileAsync(string knowledgeFileFolder, BotComponentId botComponentId, string fileName, CancellationToken cancellationToken = default)
    {
        var requestUri = new Uri(new Uri(DataverseUrl), $"/api/data/v9.2/botcomponents({botComponentId})/filedata/$value");

        var localPath = GetKnowledgeFileLocalPath(knowledgeFileFolder, fileName);
        var dir = Path.GetDirectoryName(localPath);

        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var httpClient = _httpClientAccessor.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("OData-Version", "4.0");
        request.Headers.UserAgent.ParseAdd(_userAgent);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Dataverse request failed ({(int)response.StatusCode}): {text}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        // No #if: passing 81920 explicitly is identical to net10's default buffer size
        // on Stream.CopyToAsync(Stream, CT), and the 3-arg form is what netstandard2.0
        // exposes -- so the same expression compiles and behaves the same on both TFMs.
        await stream.CopyToAsync(fileStream, 81920, cancellationToken).ConfigureAwait(false);
    }

    public async Task UploadKnowledgeFileAsync(string knowledgeFileFolder, Guid botComponentId, string fileName, CancellationToken cancellationToken = default)
    {
        var requestUri = new Uri(new Uri(DataverseUrl), $"/api/data/v9.2/botcomponents({botComponentId})/filedata/");
        var httpClient = _httpClientAccessor.CreateClient();
        using var fileStream = new FileStream(GetKnowledgeFileLocalPath(knowledgeFileFolder, fileName), FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);

        using var request = new HttpRequestMessage(HttpMethodHelper.Patch, requestUri)
        {
            Content = new StreamContent(fileStream, bufferSize: 81920)
        };

        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add("x-ms-file-name", fileName);
        request.Headers.Add("Accept", "application/json");
        request.Headers.UserAgent.ParseAdd(_userAgent);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Dataverse upload failed ({(int)response.StatusCode}): {responseText}");
        }
    }

    private static string GetKnowledgeFileLocalPath(string knowledgeFileFolder, string fileName)
    {
        return Path.Combine(knowledgeFileFolder, fileName);
    }

    public virtual async Task<AIPromptMetadata[]> DownloadAllAIPromptsForAgentAsync(AgentSyncInfo syncInfo, CancellationToken cancellationToken)
    {
        string? scopeFilter = null;
        if (syncInfo.AgentId.HasValue && syncInfo.AgentId != Guid.Empty)
        {
            scopeFilter = $"_parentbotid_value eq {syncInfo.AgentId}";
        }
        else if (syncInfo.ComponentCollectionId.HasValue && syncInfo.ComponentCollectionId != Guid.Empty)
        {
            scopeFilter = $"_parentbotcomponentcollectionid_value eq {syncInfo.ComponentCollectionId}";
        }

        if (scopeFilter == null)
        {
            return Array.Empty<AIPromptMetadata>();
        }

        var aiModelIds = new HashSet<Guid>();
        var filter = $"componenttype eq 9 and {scopeFilter}";
        string? nextPageUrl = $"{DataverseUrl}/api/data/v9.2/botcomponents?$select=botcomponentid,data&$filter={Uri.EscapeDataString(filter)}";

        while (!string.IsNullOrEmpty(nextPageUrl))
        {
            var page = await SendAsync<JsonElement>(HttpMethod.Get, nextPageUrl!, null, false, cancellationToken).ConfigureAwait(false);

            if (page.TryGetProperty("value", out var valueArray) && valueArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var botComponent in valueArray.EnumerateArray())
                {
                    if (!botComponent.TryGetProperty("data", out var dataProperty) || dataProperty.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var componentYaml = dataProperty.GetString();
                    if (string.IsNullOrEmpty(componentYaml))
                    {
                        continue;
                    }

                    var aiModelIdMatch = Regex.Match(componentYaml!, @"aIModelId\s*:\s*([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
                    if (aiModelIdMatch.Success && Guid.TryParse(aiModelIdMatch.Groups[1].Value, out var modelId))
                    {
                        aiModelIds.Add(modelId);
                    }
                }
            }

            nextPageUrl = page.TryGetProperty("@odata.nextLink", out var nextLinkProperty) ? nextLinkProperty.GetString() : null;
        }

        if (aiModelIds.Count == 0)
        {
            return Array.Empty<AIPromptMetadata>();
        }

        return await FetchAIPromptsByModelIdsAsync(aiModelIds, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AIPromptMetadata[]> FetchAIPromptsByModelIdsAsync(IEnumerable<Guid> aiModelIds, CancellationToken cancellationToken)
    {
        var results = new List<AIPromptMetadata>();
        foreach (var batch in aiModelIds.Chunk(BatchSize))
        {
            var idFilter = string.Join(" or ", batch.Select(id => $"msdyn_aimodelid eq {id}"));
            const string expand = "msdyn_aimodel_msdyn_aiconfiguration($select=msdyn_aiconfigurationid,msdyn_type,msdyn_customconfiguration,msdyn_name,statecode,statuscode,msdyn_majoriterationnumber,msdyn_minoriterationnumber,msdyn_templateversion;$orderby=msdyn_majoriterationnumber desc,msdyn_minoriterationnumber desc)";
            string? nextPageUrl = $"{DataverseUrl}/api/data/v9.2/msdyn_aimodels?" +
                                   $"$select=msdyn_aimodelid,msdyn_name,_msdyn_templateid_value,statecode,statuscode" +
                                   $"&$expand={expand}" +
                                   $"&$filter={Uri.EscapeDataString(idFilter)}";

            while (!string.IsNullOrEmpty(nextPageUrl))
            {
                var page = await SendAsync<JsonElement>(HttpMethod.Get, nextPageUrl!, null, false, cancellationToken).ConfigureAwait(false);
                if (page.TryGetProperty("value", out var valueArray) && valueArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var aiModelElement in valueArray.EnumerateArray())
                    {
                        var promptMetadata = ParseAIPromptMetadata(aiModelElement);
                        if (promptMetadata != null)
                        {
                            results.Add(promptMetadata);
                        }
                    }
                }

                nextPageUrl = page.TryGetProperty("@odata.nextLink", out var nextLinkProperty) ? nextLinkProperty.GetString() : null;
            }
        }

        return results.ToArray();
    }

    private static AIPromptMetadata? ParseAIPromptMetadata(JsonElement aiModelElement)
    {
        if (!aiModelElement.TryGetProperty("msdyn_aimodelid", out var aiModelIdElement) || !Guid.TryParse(aiModelIdElement.GetString(), out var modelId))
        {
            return null;
        }

        var modelName = aiModelElement.TryGetProperty("msdyn_name", out var modelNameElement) ? modelNameElement.GetString() : null;

        Guid? templateId = null;
        if (aiModelElement.TryGetProperty("_msdyn_templateid_value", out var templateIdElement) && templateIdElement.ValueKind == JsonValueKind.String && Guid.TryParse(templateIdElement.GetString(), out var templateIdGuid))
        {
            templateId = templateIdGuid;
        }

        string? customConfiguration = null;
        if (aiModelElement.TryGetProperty("msdyn_aimodel_msdyn_aiconfiguration", out var configurationsArray) && configurationsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var configuration in configurationsArray.EnumerateArray())
            {
                if (configuration.TryGetProperty("msdyn_customconfiguration", out var customConfigurationElement) && customConfigurationElement.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(customConfigurationElement.GetString()))
                {
                    customConfiguration = customConfigurationElement.GetString();
                    break;
                }
            }
        }

        return new AIPromptMetadata
        {
            AIModelId = modelId,
            Name = modelName,
            TemplateId = templateId,
            CustomConfiguration = customConfiguration
        };
    }

    public virtual async Task<AIPromptResponse> UpsertAIPromptAsync(Guid? agentId, AIPromptMetadata? promptMetadata, CancellationToken cancellationToken)
    {
        if (promptMetadata is null)
        {
            throw new ArgumentNullException(nameof(promptMetadata));
        }

        if (promptMetadata.AIModelId == Guid.Empty)
        {
            throw new ArgumentException("AIPromptMetadata must have a non-empty AIModelId.", nameof(promptMetadata));
        }

        var promptName = promptMetadata.Name ?? promptMetadata.AIModelId.ToString();
        var errorMessage = string.Empty;

        try
        {
            if (string.IsNullOrEmpty(promptMetadata.CustomConfiguration))
            {
                return new AIPromptResponse { PromptName = promptName, ErrorMessage = string.Empty };
            }

            var templateId = await GetTemplateIdAsync(promptMetadata.AIModelId, cancellationToken).ConfigureAwait(false);
            if (templateId == Guid.Empty)
            {
                templateId = promptMetadata.TemplateId ?? Guid.Empty;
            }

            await PublishAIModelAsync(promptMetadata, templateId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }

        return new AIPromptResponse
        {
            PromptName = promptName,
            ErrorMessage = errorMessage
        };
    }

    private async Task PublishAIModelAsync(AIPromptMetadata prompt, Guid templateId, CancellationToken cancellationToken)
    {
        if (prompt.AIModelId == Guid.Empty)
        {
            return;
        }

        var requestBody = new Dictionary<string, object?>
        {
            ["CustomConfiguration"] = prompt.CustomConfiguration ?? string.Empty,
            ["ModelId"] = prompt.AIModelId.ToString(),
            ["ModelName"] = prompt.Name ?? string.Empty,
            ["RunConfigurationId"] = Guid.NewGuid().ToString(),
            ["TemplateId"] = templateId == Guid.Empty ? string.Empty : templateId.ToString(),
            ["RunConfiguration"] = string.Empty,
            ["Source"] = "{ \"consumptionSource\": \"Api\", \"partnerSource\": \"PVA\", \"consumptionSourceVersion\": \"GptApiClient\"}"
        };

        var publishUrl = $"{DataverseUrl}/api/data/v9.0/AIModelPublish";
        await SendAsync<object>(HttpMethod.Post, publishUrl, requestBody, expectReturn: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Guid> GetTemplateIdAsync(Guid aiModelId, CancellationToken cancellationToken)
    {
        var requestUrl = $"{DataverseUrl}/api/data/v9.2/msdyn_aimodels({aiModelId})?$select=_msdyn_templateid_value";
        try
        {
            var response = await SendAsync<JsonElement>(HttpMethod.Get, requestUrl, null, false, cancellationToken).ConfigureAwait(false);
            if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("_msdyn_templateid_value", out var templateIdElement) && templateIdElement.ValueKind == JsonValueKind.String && Guid.TryParse(templateIdElement.GetString(), out var templateId))
            {
                return templateId;
            }
        }
        catch (InvalidOperationException)
        {
        }

        return Guid.Empty;
    }

    #region DTO Types

    internal sealed class ODataResponse<T>
    {
        [JsonPropertyName("value")]
        public List<T>? Value { get; set; }

        [JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; set; }
    }

    internal class ConnectionReferenceQueryResponse
    {
        [JsonPropertyName("value")]
        public ConnectionReferenceInfo[] Value { get; set; } = Array.Empty<ConnectionReferenceInfo>();
    }

#pragma warning disable CA1034 // Nested types should not be visible - DTO used via 'using static'
    public class ConnectionReferenceInfo
    {
        [JsonPropertyName("connectionreferenceid")]
        public Guid ConnectionReferenceId { get; set; }

        [JsonPropertyName("connectionreferencelogicalname")]
        public string ConnectionReferenceLogicalName { get; set; } = string.Empty;

        [JsonPropertyName("connectorid")]
        public string? ConnectorId { get; set; } = string.Empty;

        [JsonPropertyName("connectionid")]
        public string? ConnectionId { get; set; } = string.Empty;
    }

    internal class AgentSyncInfoDetail
    {
        [JsonPropertyName("botid")]
        public Guid AgentId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("iconbase64")]
        public string IconBase64 { get; set; } = string.Empty;

        [JsonPropertyName("schemaname")]
        public string SchemaName { get; set; } = string.Empty;

        public AgentInfo ToAgentInfo()
        {
            return new AgentInfo
            {
                AgentId = AgentId,
                DisplayName = Name,
                IconBase64 = IconBase64,
                SchemaName = SchemaName
            };
        }
    }

    internal class AgentInfoDetail
    {
        [JsonPropertyName("value")]
        public AgentIdInfo[] Value { get; set; } = Array.Empty<AgentIdInfo>();
    }

    internal class AgentIdInfo
    {
        [JsonPropertyName("botid")]
        public Guid AgentId { get; set; }
    }

    public class WorkflowMetadata
    {
        [JsonPropertyName("jsonfilename")]
        public string? JsonFileName { get; set; }

        [JsonPropertyName("workflowid")]
        public Guid WorkflowId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("type")]
        public int? Type { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("subprocess")]
        public bool? Subprocess { get; set; }

        [JsonPropertyName("category")]
        public int? Category { get; set; }

        [JsonPropertyName("mode")]
        public int? Mode { get; set; }

        [JsonPropertyName("scope")]
        public int? Scope { get; set; }

        [JsonPropertyName("ondemand")]
        public bool? OnDemand { get; set; }

        [JsonPropertyName("triggeroncreate")]
        public bool? TriggerOnCreate { get; set; }

        [JsonPropertyName("triggerondelete")]
        public bool? TriggerOnDelete { get; set; }

        [JsonPropertyName("asyncautodelete")]
        public bool? AsyncAutodelete { get; set; }

        [JsonPropertyName("syncworkflowlogonfailure")]
        public bool? SyncWorkflowLogOnFailure { get; set; }

        [JsonPropertyName("statecode")]
        public int? StateCode { get; set; }

        [JsonPropertyName("statuscode")]
        public int? StatusCode { get; set; }

        [JsonPropertyName("runas")]
        public int? RunAs { get; set; }

        [JsonPropertyName("istransacted")]
        public bool? IsTransacted { get; set; }

        [JsonPropertyName("introducedversion")]
        public string? IntroducedVersion { get; set; }

        [JsonPropertyName("iscustomizable")]
        public ManagedProperty? IsCustomizable { get; set; }

        [JsonPropertyName("businessprocesstype")]
        public int? BusinessProcessType { get; set; }

        [JsonPropertyName("iscustomprocessingstepallowedforotherpublishers")]
        public ManagedProperty? IsCustomProcessingStepAllowedForOtherPublishers { get; set; }

        [JsonPropertyName("modernflowtype")]
        public int? ModernFlowType { get; set; }

        [JsonPropertyName("primaryentity")]
        public string? PrimaryEntity { get; set; }

        [YamlIgnore]
        [JsonPropertyName("clientdata")]
        public string? ClientData { get; set; }
    }

    public class ManagedProperty
    {
        [JsonPropertyName("Value")]
        public bool Value { get; set; }

        [JsonPropertyName("CanBeChanged")]
        public bool CanBeChanged { get; set; }

        [JsonPropertyName("ManagedPropertyLogicalName")]
        public string? ManagedPropertyLogicalName { get; set; }
    }

    public class AIPromptMetadata
    {
        [JsonPropertyName("aimodelid")]
        public Guid AIModelId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("templateid")]
        public Guid? TemplateId { get; set; }

        [YamlIgnore]
        [JsonPropertyName("customconfiguration")]
        public string? CustomConfiguration { get; set; }
    }

    public class AIPromptResponse
    {
        public string? PromptName { get; set; }

        public string? ErrorMessage { get; set; }
    }

        internal class SolutionQueryResponse
        {
            [JsonPropertyName("value")]
            public SolutionData[] Value { get; set; } = Array.Empty<SolutionData>();
        }

        internal class SolutionData
        {
            [JsonPropertyName("uniquename")]
            public string UniqueName { get; set; } = string.Empty;

            [JsonPropertyName("version")]
            public string Version { get; set; } = string.Empty;
        }

        internal class AgentDetailWithCollections
        {
            [JsonPropertyName("botid")]
            public Guid AgentId { get; set; }

            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("iconbase64")]
            public string? IconBase64 { get; set; }

            [JsonPropertyName("bot_botcomponentcollection")]
            public ComponentCollectionDetail[]? ComponentCollections { get; set; }
        }

        internal class ComponentCollectionDetail
        {
            [JsonPropertyName("botcomponentcollectionid")]
            public Guid BotComponentCollectionId { get; set; }

            [JsonPropertyName("schemaname")]
            public string SchemaName { get; set; } = string.Empty;

            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;
        }

#pragma warning restore CA1034

    internal class BotComponentListResponse
    {
        [JsonPropertyName("value")]
        public BotComponentItem[] Value { get; set; } = Array.Empty<BotComponentItem>();
    }

    internal class BotComponentItem
    {
        [JsonPropertyName("botcomponentid")]
        public Guid BotComponentId { get; set; }
    }

    internal class BotComponentWorkflowMetadata
    {
        [JsonPropertyName("workflowid")]
        public Guid WorkflowId { get; set; }

        [JsonPropertyName("botcomponentid")]
        public Guid BotComponentId { get; set; }
    }

    #endregion
}
