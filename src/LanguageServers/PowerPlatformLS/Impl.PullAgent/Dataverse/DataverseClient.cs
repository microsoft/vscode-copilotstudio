namespace Microsoft.PowerPlatformLS.Impl.PullAgent.Dataverse
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using YamlDotNet.Serialization;

    internal class DataverseClient : IDataverseClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _dataverseUrl;
        private readonly string _accessToken;
        private readonly string _userAgent;
        private const int BatchSize = 50;

        private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        public DataverseClient(HttpClient httpClient, string dataverseUrl, string accessToken, string userAgent)
        {
            _httpClient = httpClient;
            _dataverseUrl = dataverseUrl ?? throw new ArgumentNullException(nameof(dataverseUrl));
            _accessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));
            _userAgent = userAgent ?? throw new ArgumentNullException(nameof(userAgent));
        }

        /// <summary>
        /// Create new agent by agent name and schema name.
        /// </summary>
        /// <param name="displayName">Display name of the new agent.</param>
        /// <param name="schemaName">Schema name for the new agent.</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
        /// <returns>The created agent information.</returns>
        public virtual async Task<AgentInfo> CreateNewAgentAsync(string displayName, string schemaName, CancellationToken cancellationToken)
        {
            var requestBody = new Dictionary<string, object?>
            {
                ["name"] = displayName ?? throw new ArgumentNullException(nameof(displayName)),
                ["template"] = "empty-1.0.0",
                ["schemaname"] = string.IsNullOrWhiteSpace(schemaName) ? null : schemaName
            };
            var requestUri = $"{_dataverseUrl}/api/data/v9.2/bots";
            var response = await SendAsync<AgentSyncInfoDetail>(HttpMethod.Post, requestUri, requestBody, expectReturn: true, cancellationToken).ConfigureAwait(false);

            if (response == null || response.AgentId == Guid.Empty)
            {
                throw new InvalidOperationException("Dataverse API returned an invalid agent creation response.");
            }

            return response.ToAgentInfo();
        }

        /// <summary>
        /// Get an agent with the given schemaName.
        /// </summary>
        /// <param name="schemaName">Schema name to search for.</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
        /// <returns>Agent ID if found, otherwise Guid.Empty.</returns>
        public virtual async Task<Guid> GetAgentIdBySchemaNameAsync(string schemaName, CancellationToken cancellationToken)
        {
            var requestUri = $"{_dataverseUrl}/api/data/v9.2/bots?$select=botid&$filter=schemaname eq '{schemaName}'";
            var result = await SendAsync<AgentInfoDetail>(HttpMethod.Get, requestUri, null, false, cancellationToken).ConfigureAwait(false);
            return result?.Value?.FirstOrDefault()?.AgentId ?? Guid.Empty;
        }

        /// <summary>
        /// Download all workflows for the specified agent.
        /// </summary>
        /// <param name="agentId"></param>
        /// <param name="cancellationToken"></param>
        /// <returns>The workflow metadata.</returns>
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

        /// <summary>
        /// Updates an existing workflow for the specified agent if exist, otherwise insert new workflow.
        /// </summary>
        /// <param name="agentId">The ID of the agent.</param>
        /// <param name="workflowMetadata">The workflow metadata to update.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <return>The response of workflow update.</return>
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

            string errorMessage = string.Empty;
            try
            {
                bool existsInCloud = await WorkflowExistsAsync(workflowMetadata.WorkflowId, cancellationToken);
                if (!existsInCloud)
                {
                    return await InsertWorkflowAsync(agentId, workflowMetadata, cancellationToken);
                }

                var requestBody = CreateWorkflowRequestBody(workflowMetadata);
                var updateUrl = $"{_dataverseUrl}/api/data/v9.2/workflows({workflowMetadata.WorkflowId})";
                await SendAsync<object>(HttpMethod.Patch, updateUrl, requestBody, false, cancellationToken);
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

        /// <summary>
        /// Inserts a new workflow for the specified agent.
        /// <param name="agentId">The ID of the agent.</param>
        /// <param name="workflowMetadata">The workflow metadata to update.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <return>The response of workflow creation.</return>
        /// </summary>
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

            string errorMessage = string.Empty;
            try
            {
                workflowMetadata.StateCode = null;
                workflowMetadata.StatusCode = null;
                var requestBody = CreateWorkflowRequestBody(workflowMetadata);
                requestBody["workflowid"] = workflowMetadata.WorkflowId;

                var createResponse = await SendAsync<JsonElement>(
                    HttpMethod.Post,
                    $"{_dataverseUrl}/api/data/v9.2/workflows",
                    requestBody,
                    expectReturn: true,
                    cancellationToken
                );

                await ActivateWorkflowAsync(workflowMetadata.WorkflowId, cancellationToken);
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
            var requestUri = $"{_dataverseUrl}/api/data/v9.2/connectionreferences" +
                             $"?$select=connectionreferenceid,connectionreferencelogicalname,connectorid" +
                             $"&$filter={Uri.EscapeDataString(filter)}";

            var response = await SendAsync<ConnectionReferenceQueryResponse>(HttpMethod.Get, requestUri, null, false, cancellationToken);
            return response?.Value ?? Array.Empty<ConnectionReferenceInfo>();
        }

        private async Task<bool> WorkflowExistsAsync(Guid workflowId, CancellationToken cancellationToken)
        {
            var checkUrl = $"{_dataverseUrl}/api/data/v9.2/workflows({workflowId})?$select=workflowid";
            try
            {
                await SendAsync<object>(HttpMethod.Get, checkUrl, null, false, cancellationToken);
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
            var activateUrl = $"{_dataverseUrl}/api/data/v9.2/workflows({workflowId})";
            var activateBody = new Dictionary<string, object?>
            {
                ["statecode"] = 1,
                ["statuscode"] = 2
            };

            await SendAsync<object>(HttpMethod.Patch, activateUrl, activateBody, false, cancellationToken);
        }

        private async Task<List<Guid>> GetAllBotComponentIdsAsync(Guid agentId, CancellationToken cancellationToken)
        {
            var url = $"{_dataverseUrl}/api/data/v9.2/botcomponents?$select=botcomponentid&$filter=_parentbotid_value eq {agentId} and componenttype ne 19";
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
                string? nextBotComponentWorkflowUrl = $"{_dataverseUrl}/api/data/v9.2/botcomponent_workflowset?$select=workflowid,botcomponentid";

                var filterQuery = string.Join(" or ", batch.Select(id => $"botcomponentid eq {id}"));
                nextBotComponentWorkflowUrl += $"&$filter={Uri.EscapeDataString(filterQuery)}";

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
            }

            if (workflowIdToBotComponentMap.Count == 0)
            {
                return Array.Empty<WorkflowMetadata>();
            }

            var workflows = new List<WorkflowMetadata>();

            foreach (var batch in workflowIdToBotComponentMap.Keys.Chunk(BatchSize))
            {
                string? nextWorkflowUrl = $"{_dataverseUrl}/api/data/v9.2/workflows?" +
                    "$select=workflowid,name,description,type,subprocess,category,mode,scope,ondemand," +
                    "triggeroncreate,triggerondelete,asyncautodelete,syncworkflowlogonfailure,statecode,statuscode,runas," +
                    "istransacted,introducedversion,iscustomizable,businessprocesstype," +
                    "iscustomprocessingstepallowedforotherpublishers,modernflowtype,primaryentity," +
                    "createdon,modifiedon,clientdata";

                var workflowFilterQuery = string.Join(" or ", batch.Select(id => $"workflowid eq {id}"));
                nextWorkflowUrl += $"&$filter={Uri.EscapeDataString(workflowFilterQuery)}";

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
            }

            return workflows.ToArray();
        }

        private async Task<T?> SendAsync<T>(HttpMethod httpMethod, string requestUrl, object? requestBody, bool expectReturn, CancellationToken cancellationToken)
        {
            using var requestMessage = new HttpRequestMessage(httpMethod, requestUrl);
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            requestMessage.Headers.Add("OData-MaxVersion", "4.0");
            requestMessage.Headers.Add("OData-Version", "4.0");
            requestMessage.Headers.UserAgent.ParseAdd(_userAgent);

            if (expectReturn)
            {
                requestMessage.Headers.Add("Prefer", "return=representation");
            }

            if (requestBody != null)
            {
                requestMessage.Content = new StringContent(JsonSerializer.Serialize(requestBody, JsonSerializerOptions), Encoding.UTF8, "application/json");
            }

            using var responseMessage = await _httpClient.SendAsync(requestMessage, cancellationToken).ConfigureAwait(false);
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

        /// <summary>
        /// Checks if a connection reference exists.
        /// GET /api/data/v9.2/connectionreferences?$filter=connectionreferencelogicalname eq '{name}'
        /// </summary>
        public virtual async Task<bool> ConnectionReferenceExistsAsync(
            string connectionReferenceLogicalName,
            CancellationToken cancellationToken)
        {  
            var literal = connectionReferenceLogicalName.Replace("'", "''");
            var filterExpr = $"connectionreferencelogicalname eq '{literal}'";
            var baseUri = new Uri(new Uri(_dataverseUrl), "/api/data/v9.2/connectionreferences");
            var requestUri = new Uri($"{baseUri}?$select=connectionreferenceid&$top=1&$filter={Uri.EscapeDataString(filterExpr)}");

            var queryResponse = await SendAsync<ConnectionReferenceQueryResponse>(
                HttpMethod.Get,
                requestUri.ToString(),
                null,
                false,
                cancellationToken
            );

            return queryResponse?.Value != null && queryResponse.Value.Length > 0;
        }

        /// <summary>
        /// Creates an unbound connection reference.
        /// POST /api/data/v9.2/connectionreferences
        /// Body: { "connectionreferencelogicalname": "...", "connectorid": "..." }
        /// </summary>
        public virtual async Task CreateConnectionReferenceAsync(
            string connectionReferenceLogicalName,
            string connectorId,
            CancellationToken cancellationToken)
        {
            var requestUri = new Uri(new Uri(_dataverseUrl), "/api/data/v9.2/connectionreferences");

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
            );
        }

        /// <summary>
        /// Ensures connection reference exists (creates if missing).
        /// </summary>
        public virtual async Task EnsureConnectionReferenceExistsAsync(
            string connectionReferenceLogicalName,
            string connectorId,
            CancellationToken cancellationToken)
        {
            var exists = await ConnectionReferenceExistsAsync(connectionReferenceLogicalName, cancellationToken);
            if (!exists)
            {
                await CreateConnectionReferenceAsync(connectionReferenceLogicalName, connectorId, cancellationToken);
            }
        }

        internal class ConnectionReferenceQueryResponse
        {
            [JsonPropertyName("value")]
            public ConnectionReferenceInfo[] Value { get; set; } = Array.Empty<ConnectionReferenceInfo>();
        }

        internal class ConnectionReferenceInfo
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

        internal class WorkflowMetadata
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

        internal class ManagedProperty
        {
            [JsonPropertyName("Value")]
            public bool Value { get; set; }

            [JsonPropertyName("CanBeChanged")]
            public bool CanBeChanged { get; set; }

            [JsonPropertyName("ManagedPropertyLogicalName")]
            public string? ManagedPropertyLogicalName { get; set; }
        }

        internal class BotComponentListResponse
        {
            [JsonPropertyName("value")]
            public BotComponent[] Value { get; set; } = Array.Empty<BotComponent>();
        }

        internal class BotComponent
        {
            [JsonPropertyName("botcomponentid")]
            public Guid BotComponentId { get; set; }
        }

        internal class BotComponentWorkflowListResponse
        {
            [JsonPropertyName("value")]
            public BotComponentWorkflowMetadata[] Value { get; set; } = Array.Empty<BotComponentWorkflowMetadata>();
        }

        internal class BotComponentWorkflowMetadata
        {
            [JsonPropertyName("workflowid")]
            public Guid WorkflowId { get; set; }

            [JsonPropertyName("botcomponentid")]
            public Guid BotComponentId { get; set; }
        }

        internal class WorkflowListResponse
        {
            [JsonPropertyName("value")]
            public WorkflowMetadata[] Value { get; set; } = Array.Empty<WorkflowMetadata>();
        }
    }
}
