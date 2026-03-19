namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Exceptions;
    using Microsoft.Agents.ObjectModel.Yaml;
    using Microsoft.Agents.Platform.Content;
    using System.Collections.Immutable;
    using System.Net.Http.Json;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    internal class IslandControlPlaneService : IIslandControlPlaneService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IContentAuthoringService _contentAuthoringService;
        private static readonly JsonSerializerOptions Options = ElementSerializer.CreateOptions();
        private readonly AsyncLocal<string> _baseEndpoint = new AsyncLocal<string>();


        public IslandControlPlaneService(IHttpClientFactory httpClientFactory, IContentAuthoringService  contentAuthoringService)
        {
            _httpClientFactory = httpClientFactory;
            _contentAuthoringService = contentAuthoringService;
        }


        public async Task<PvaComponentChangeSet> SaveChangesAsync(AuthoringOperationContextBase operationContext, PvaComponentChangeSet pushChangeset, CancellationToken cancellationToken)
        {
            using var yamlContext = YamlSerializationContext.UseYamlPassThroughSerializationContext();
            var tag = new BotEntityTag { BypassSynchronization = false, Source = "api" };
            await _contentAuthoringService.SaveChangesAsync(operationContext, pushChangeset, tag, false, false, true, cancellationToken);
            return await GetComponentsAsync(operationContext, pushChangeset.ChangeToken, cancellationToken);
        }

        public async Task<PvaComponentChangeSet> GetComponentsAsync(
            AuthoringOperationContextBase operationContext,
            string? changeToken,
            CancellationToken cancellationToken)
        {
            for (var i = 0; i < 3; i++)
            {
                var islandChangeSet = await RetrieveComponentsFromIslandAsync(operationContext, changeToken, cancellationToken);


                // workaround until the island returns syntax
                PvaComponentChangeSet dataverseChangeSet;
                using (var yamlContext = YamlSerializationContext.UseYamlPassThroughSerializationContext())
                {
                    dataverseChangeSet = await _contentAuthoringService.GetComponentsAsync(operationContext, changeToken, true, false, cancellationToken);
                }
                    
                if (!AreBotComponentChangesEqual(islandChangeSet, dataverseChangeSet))
                {
                    continue;
                }

                if (islandChangeSet.Bot?.Version != dataverseChangeSet.Bot?.Version)
                {
                    continue;
                }
                
                // return merged changeset
                return islandChangeSet.WithBotComponentChanges(dataverseChangeSet.BotComponentChanges).WithBot(dataverseChangeSet.Bot);

            }

            throw new ObjectModelException("Unable to retrieve components", "Unable to retrieve components");
        }


        public void SetIslandBaseEndpoint(string baseEndpoint)
        {
            // Normalize to having a trailing /. 
            if (!baseEndpoint.EndsWith('/'))
            {
                baseEndpoint = baseEndpoint + "/";
            }

            _baseEndpoint.Value = baseEndpoint;
        }

        public string GetBaseEndpoint()
        {
            return _baseEndpoint.Value ?? throw new InvalidOperationException("Base endpoint is not set.");
        }


        private async Task<PvaComponentChangeSet> RetrieveComponentsFromIslandAsync(AuthoringOperationContextBase operationContext, string? changeToken, CancellationToken cancellationToken)
        {
            if (operationContext is AuthoringOperationContext botContext)
            {
                return await CallBotEndpointAsync(new PvaComponentChangeSet(null, null, changeToken), botContext, cancellationToken, changes: false);
            }
            else if (operationContext is BotComponentCollectionAuthoringOperationContext collectionContext)
            {
                return await CallCollectionEndpointAsync(new PvaComponentChangeSet(null, null, changeToken), collectionContext, cancellationToken, changes: false);
            }
            else
            {
                throw new NotImplementedException();
            }
        }


        private async Task<PvaComponentChangeSet> CallCollectionEndpointAsync(PvaComponentChangeSet changeToken, BotComponentCollectionAuthoringOperationContext collectionContext, CancellationToken cancellationToken, bool changes)
        {
            using var client = _httpClientFactory.CreateClient(HttpClientNames.BotManagement);
            // https://powervamg.us-il201.gateway.test.island.powerapps.com/chatbotmanagement/tenants/72f988bf-86f1-41af-91ab-2d7cd011db47/environments/4d4a8e81-17a4-4a92-9bfe-8d12e607fb7f/componentcollections/api/50a51fc9-692f-f011-8c4d-000d3a3bb11b/get-content
            var endpoint = $"{GetBaseEndpoint()}chatbotmanagement/tenants/{collectionContext.OrganizationInfo.TenantId}/environments/{collectionContext.EnvironmentId}/componentcollections/api/{collectionContext.BotComponentCollectionReference.CdsId}";
            using var request = new HttpRequestMessage(
                changes ? HttpMethod.Put : HttpMethod.Post,
                endpoint + (changes ? "/save-content" : "/get-content"));

            SetContent(request, changeToken, collectionContext, changes);

            var result = await client.SendAsync(request, cancellationToken);
            result.EnsureSuccessStatusCode();
            var parsedContent = await result.Content.ReadFromJsonAsync<PvaComponentChangeSet>(Options, cancellationToken);

            return parsedContent ?? new PvaComponentChangeSet(null, null, null);
        }

        private static void SetContent(
            HttpRequestMessage request,
            PvaComponentChangeSet changeToken,
            AuthoringOperationContextBase authoringContext,
            bool changes)
        {
            request.Content = new StringContent(
    changes ? JsonSerializer.Serialize(changeToken, Options) : JsonSerializer.Serialize(new { ComponentDeltaToken = changeToken.ChangeToken }, Options),
    Encoding.UTF8,
    "application/json");

            request.Headers.Add("x-ms-client-tenant-id", authoringContext.OrganizationInfo.TenantId.ToString());
            request.Headers.Add("x-cci-tenantid", authoringContext.OrganizationInfo.TenantId.ToString());
            request.Headers.Add("x-cci-bapenvironmentid", authoringContext.EnvironmentId);

            string id = authoringContext switch
            {
                AuthoringOperationContext botContext => botContext.BotReference.CdsBotId.ToString(),
                BotComponentCollectionAuthoringOperationContext collectionContext =>
                collectionContext.BotComponentCollectionReference.CdsId.ToString(),
                _ => throw new NotImplementedException()
           };

            request.Headers.Add("x-cci-cdsbotid", id);
        }

        private async Task<PvaComponentChangeSet> CallBotEndpointAsync(PvaComponentChangeSet changeToken, AuthoringOperationContext botContext, CancellationToken cancellationToken, bool changes)
        {
            using var client = _httpClientFactory.CreateClient(HttpClientNames.BotManagement);
            using var request = new HttpRequestMessage(
                changes ? HttpMethod.Put : HttpMethod.Post,
                $"{GetBaseEndpoint()}api/botmanagement/v1/environments/{botContext.EnvironmentId}/bots/{botContext.BotReference.CdsBotId}/content/botcomponents");

            SetContent(request, changeToken, botContext, changes);

            var result = await client.SendAsync(request, cancellationToken);
            result.EnsureSuccessStatusCode();
            var parsedContent = await result.Content.ReadFromJsonAsync<PvaComponentChangeSet>(Options, cancellationToken);

            return parsedContent ?? new PvaComponentChangeSet(null, null, null);
        }
    
        private bool AreBotComponentChangesEqual(PvaComponentChangeSet islandChangeSet, PvaComponentChangeSet dataverseChangeSet)
        {
            if (islandChangeSet.BotComponentChanges.Length != dataverseChangeSet.BotComponentChanges.Length)
            {
                return false;
            }

            var sortedIslandChanges = OrderChanges(islandChangeSet.BotComponentChanges);
            var sortedDvChanges = OrderChanges(dataverseChangeSet.BotComponentChanges);

            for (var j = 0; j < sortedIslandChanges.Length; j++)
            {
                if (!sortedIslandChanges[j].Accept(ChangeComparerVisitor.Instance)(sortedDvChanges[j]))
                {
                    // Found a mismatch
                    return false;
                }
            }

            return true;
        }

        private ImmutableArray<BotComponentChange> OrderChanges(ImmutableArray<BotComponentChange> botComponentChanges)
        {
            return botComponentChanges.Sort(static (x, y) => string.Compare(x.Accept(ComponentIdVisitor.Instance), y.Accept(ComponentIdVisitor.Instance), StringComparison.Ordinal));
        }

        private class ComponentIdVisitor : BotComponentChangeVisitor<string>
        {
            public static ComponentIdVisitor Instance { get; } = new ComponentIdVisitor();
            public override string Visit(UnknownBotComponentChange item) => "";

            public override string Visit(BotComponentInsert item) => item.Component?.Id.ToString() ?? string.Empty;

            public override string Visit(BotComponentUpdate item) => item.Component?.Id.ToString() ?? string.Empty;

            public override string Visit(BotComponentDelete item) => item.BotComponentId.ToString();
        }

        private class ChangeComparerVisitor : BotComponentChangeVisitor<Func<BotComponentChange, bool>>
        {
            public static ChangeComparerVisitor Instance { get; } = new ChangeComparerVisitor();
            public override Func<BotComponentChange, bool> Visit(UnknownBotComponentChange item) =>
                (BotComponentChange c) => c is UnknownBotComponentChange unknown && NodeComparer.Structural.Equals(item.RawData, item.RawData);

            public override Func<BotComponentChange, bool> Visit(BotComponentInsert item) =>
                (BotComponentChange c) => c is BotComponentInsert insert && insert.Component?.Id == item.Component?.Id && insert.Component?.Version == item.Component?.Version;

            public override Func<BotComponentChange, bool> Visit(BotComponentUpdate item) =>
                (BotComponentChange c) => c is BotComponentUpdate update && update.Component?.Id == item.Component?.Id && update.Component?.Version == item.Component?.Version;

            public override Func<BotComponentChange, bool> Visit(BotComponentDelete item) =>
                (BotComponentChange c) => c is BotComponentDelete delete && delete.BotComponentId == item.BotComponentId && delete.VersionNumber == item.VersionNumber;
        }

    }
}