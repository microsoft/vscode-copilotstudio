// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Impl.PullAgent/Dataverse/DataverseClient.cs
// Key auth change: IHttpClientFactory → DataverseHttpClientAccessor (AuthenticatedHttpClientHandler handles auth)

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content.Abstractions;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using YamlDotNet.Serialization;

namespace Microsoft.CopilotStudio.Sync.Dataverse;

public class SyncDataverseClient : ISyncDataverseClient
{
    private readonly IDataverseHttpClientAccessor _httpClientAccessor;
    private readonly AsyncLocal<string> _dataverseUrl = new();
    private string DataverseUrl => _dataverseUrl.Value
        ?? throw new InvalidOperationException("Dataverse URL is not set. Call SetDataverseUrl before making API calls.");

    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public SyncDataverseClient(IDataverseHttpClientAccessor httpClientAccessor)
    {
        _httpClientAccessor = httpClientAccessor ?? throw new ArgumentNullException(nameof(httpClientAccessor));
    }

    /// <inheritdoc />
    public void SetDataverseUrl(string dataverseUrl)
    {
        _dataverseUrl.Value = dataverseUrl ?? throw new ArgumentNullException(nameof(dataverseUrl));
    }

    public virtual async Task<AgentInfo> CreateNewAgentAsync(string displayName, string schemaName, CancellationToken cancellationToken)
    {
        var requestBody = new Dictionary<string, object?>
        {
            ["name"] = displayName ?? throw new ArgumentNullException(nameof(displayName)),
            ["template"] = "empty-1.0.0",
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
        var requestUri = $"{DataverseUrl}/api/data/v9.2/bots?$select=botid&$filter=schemaname eq '{schemaName}'";
        var result = await SendAsync<AgentInfoDetail>(HttpMethod.Get, requestUri, null, false, cancellationToken).ConfigureAwait(false);
        return result?.Value?.FirstOrDefault()?.AgentId ?? Guid.Empty;
    }

    public virtual async Task<WorkflowMetadata[]> DownloadAllWorkflowsForAgentAsync(Guid? agentId, CancellationToken cancellationToken)
    {
        if (agentId.HasValue && agentId != Guid.Empty)
        {
            var botComponentIdentifiers = await GetAllBotComponentIdsAsync(agentId.Value, cancellationToken).ConfigureAwait(false);
            if (botComponentIdentifiers.Count > 0)
            {
                return await GetAllWorkflowsByBotComponentsAsync(botComponentIdentifiers, cancellationToken).ConfigureAwait(false);
            }
        }

        return Array.Empty<WorkflowMetadata>();
    }

    public virtual async Task<WorkflowResponse> UpdateWorkflowAsync(Guid? agentId, WorkflowMetadata? workflowMetadata, CancellationToken cancellationToken)
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
            var existsInCloud = await WorkflowExistsAsync(workflowMetadata.WorkflowId, cancellationToken).ConfigureAwait(false);
            if (!existsInCloud)
            {
                return await InsertWorkflowAsync(agentId, workflowMetadata, cancellationToken).ConfigureAwait(false);
            }

            var requestBody = CreateWorkflowRequestBody(workflowMetadata);
            var updateUrl = $"{DataverseUrl}/api/data/v9.2/workflows({workflowMetadata.WorkflowId})";
            await SendAsync<object>(HttpMethod.Patch, updateUrl, requestBody, false, cancellationToken).ConfigureAwait(false);
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
        var requestUri = $"{DataverseUrl}/api/data/v9.2/connectionreferences" +
                         $"?$select=connectionreferenceid,connectionreferencelogicalname,connectorid" +
                         $"&$filter={Uri.EscapeDataString(filter)}";

        var response = await SendAsync<ConnectionReferenceQueryResponse>(HttpMethod.Get, requestUri, null, false, cancellationToken).ConfigureAwait(false);
        return response?.Value ?? Array.Empty<ConnectionReferenceInfo>();
    }

    private async Task<bool> WorkflowExistsAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        var checkUrl = $"{DataverseUrl}/api/data/v9.2/workflows({workflowId})?$select=workflowid";
        try
        {
            await SendAsync<object>(HttpMethod.Get, checkUrl, null, false, cancellationToken).ConfigureAwait(false);
        }
        catch
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

        await SendAsync<object>(HttpMethod.Patch, activateUrl, activateBody, false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<Guid>> GetAllBotComponentIdsAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var url = $"{DataverseUrl}/api/data/v9.2/botcomponents?$select=botcomponentid&$filter=_parentbotid_value eq {agentId}";
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
        var nextBotComponentWorkflowUrl = $"{DataverseUrl}/api/data/v9.2/botcomponent_workflowset?$select=workflowid,botcomponentid";

        var filterQuery = string.Join(" or ", botComponentIds.Select(id => $"botcomponentid eq {id}"));
        nextBotComponentWorkflowUrl += $"&$filter={filterQuery}";

        while (!string.IsNullOrEmpty(nextBotComponentWorkflowUrl))
        {
            var response = await SendAsync<JsonElement>(HttpMethod.Get, nextBotComponentWorkflowUrl, null, false, cancellationToken).ConfigureAwait(false);

            if (response.TryGetProperty("value", out var valueArray) && valueArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in valueArray.EnumerateArray())
                {
                    if (element.TryGetProperty("workflowid", out var workflowIdElement) && workflowIdElement.ValueKind == JsonValueKind.String && Guid.TryParse(workflowIdElement.GetString(), out var workflowIdentifier) && workflowIdentifier != Guid.Empty)
                    {
                        var botComponentIdentifier = element.GetProperty("botcomponentid").GetGuid();
                        if (!workflowIdToBotComponentMap.ContainsKey(workflowIdentifier))
                        {
                            workflowIdToBotComponentMap[workflowIdentifier] = new BotComponentWorkflowMetadata
                            {
                                WorkflowId = workflowIdentifier,
                                BotComponentId = botComponentIdentifier
                            };
                        }
                    }
                }
            }

            nextBotComponentWorkflowUrl = response.TryGetProperty("@odata.nextLink", out var nextLinkProp) ? nextLinkProp.GetString() : null;
        }

        if (workflowIdToBotComponentMap.Count == 0)
        {
            return Array.Empty<WorkflowMetadata>();
        }

        var workflows = new List<WorkflowMetadata>();
        var nextWorkflowUrl = $"{DataverseUrl}/api/data/v9.2/workflows?" +
                                  "$select=workflowid,name,description,type,subprocess,category,mode,scope,ondemand," +
                                  "triggeroncreate,triggerondelete,asyncautodelete,syncworkflowlogonfailure,statecode,statuscode,runas," +
                                  "istransacted,introducedversion,iscustomizable,businessprocesstype," +
                                  "iscustomprocessingstepallowedforotherpublishers,modernflowtype,primaryentity," +
                                  "createdon,modifiedon,clientdata";

        var workflowFilterQuery = string.Join(" or ", workflowIdToBotComponentMap.Keys.Select(id => $"workflowid eq {id}"));
        nextWorkflowUrl += $"&$filter={workflowFilterQuery}";

        while (!string.IsNullOrEmpty(nextWorkflowUrl))
        {
            var response = await SendAsync<JsonElement>(HttpMethod.Get, nextWorkflowUrl, null, false, cancellationToken).ConfigureAwait(false);
            if (response.TryGetProperty("value", out var workflowArray) && workflowArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in workflowArray.EnumerateArray())
                {
                    var workflow = JsonSerializer.Deserialize<WorkflowMetadata>(element.GetRawText(), JsonSerializerOptions);
                    if (workflow != null)
                    {
                        workflows.Add(workflow);
                    }
                }
            }

            nextWorkflowUrl = response.TryGetProperty("@odata.nextLink", out var nextLinkProp) ? nextLinkProp.GetString() : null;
        }

        return workflows.ToArray();
    }

    private async Task<T?> SendAsync<T>(HttpMethod httpMethod, string requestUrl, object? requestBody, bool expectReturn, CancellationToken cancellationToken)
    {
        // Auth is handled by the DataverseHttpClientAccessor's AuthenticatedHttpClientHandler
        using var httpClient = _httpClientAccessor.CreateClient();
        using var requestMessage = new HttpRequestMessage(httpMethod, requestUrl);
        requestMessage.Headers.Add("OData-MaxVersion", "4.0");
        requestMessage.Headers.Add("OData-Version", "4.0");

        if (expectReturn)
        {
            requestMessage.Headers.Add("Prefer", "return=representation");
        }

        if (requestBody != null)
        {
            requestMessage.Content = new StringContent(JsonSerializer.Serialize(requestBody, JsonSerializerOptions), Encoding.UTF8, "application/json");
        }

        using var responseMessage = await httpClient.SendAsync(requestMessage, cancellationToken).ConfigureAwait(false);
        var responseText = await responseMessage.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!responseMessage.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Dataverse request failed ({(int)responseMessage.StatusCode}): {responseText}");
        }

        if (typeof(T) == typeof(object) || string.IsNullOrWhiteSpace(responseText))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(responseText, JsonSerializerOptions);
    }

    public virtual async Task<bool> ConnectionReferenceExistsAsync(
        string connectionReferenceLogicalName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connectionReferenceLogicalName);
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
        CancellationToken cancellationToken)
    {
        var requestUri = new Uri(new Uri(DataverseUrl), "/api/data/v9.2/connectionreferences");

        var body = new Dictionary<string, object>
        {
            ["connectionreferencelogicalname"] = connectionReferenceLogicalName,
            ["connectorid"] = connectorId
        };

        await SendAsync<object>(
            HttpMethod.Post,
            requestUri.ToString(),
            body,
            false,
            cancellationToken
        ).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        var exists = await ConnectionReferenceExistsAsync(connectionReferenceLogicalName, cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            await CreateConnectionReferenceAsync(connectionReferenceLogicalName, connectorId, cancellationToken).ConfigureAwait(false);
        }
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

        using var httpClient = _httpClientAccessor.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("OData-Version", "4.0");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Dataverse request failed ({(int)response.StatusCode}): {text}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        await stream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
    }

    public async Task UploadKnowledgeFileAsync(string knowledgeFileFolder, Guid botComponentId, string fileName, CancellationToken cancellationToken = default)
    {
        var requestUri = new Uri(new Uri(DataverseUrl), $"/api/data/v9.2/botcomponents({botComponentId})/filedata/");
        using var httpClient = _httpClientAccessor.CreateClient();
        using var fileStream = new FileStream(GetKnowledgeFileLocalPath(knowledgeFileFolder, fileName), FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);

        using var request = new HttpRequestMessage(HttpMethod.Patch, requestUri)
        {
            Content = new StreamContent(fileStream, bufferSize: 81920)
        };

        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add("x-ms-file-name", fileName);
        request.Headers.Add("Accept", "application/json");

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

    #region DTO Types

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
