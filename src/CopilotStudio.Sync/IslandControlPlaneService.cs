// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from pac/src/cli/bolt.module.copilot/sync/IslandControlPlaneService.cs
// Auth change: IAuthenticatedClientFactory + AuthProfile → ISyncAuthProvider

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Exceptions;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.Agents.Platform.Content;
using System.Collections.Immutable;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Microsoft.CopilotStudio.Sync;

internal class IslandControlPlaneService : IIslandControlPlaneService
{
    private readonly ISyncAuthProvider _authProvider;
    private readonly IContentAuthoringService _contentAuthoringService;
    private static readonly JsonSerializerOptions Options = ElementSerializer.CreateOptions();
    private readonly AsyncLocal<string> _baseEndpoint = new AsyncLocal<string>();
    private readonly AsyncLocal<CoreServicesClusterCategory?> _clusterCategory = new();

    public IslandControlPlaneService(ISyncAuthProvider authProvider, IContentAuthoringService contentAuthoringService)
    {
        _authProvider = authProvider;
        _contentAuthoringService = contentAuthoringService;
    }

    public async Task<PvaComponentChangeSet> SaveChangesAsync(AuthoringOperationContextBase operationContext, PvaComponentChangeSet pushChangeset, CancellationToken cancellationToken)
    {
        using var yamlContext = YamlSerializationContext.UseYamlPassThroughSerializationContext();
        var tag = new BotEntityTag { BypassSynchronization = false, Source = "api" };
        await _contentAuthoringService.SaveChangesAsync(operationContext, pushChangeset, tag, false, false, true, cancellationToken).ConfigureAwait(false);
        return await GetComponentsAsync(operationContext, pushChangeset.ChangeToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PvaComponentChangeSet> GetComponentsAsync(
        AuthoringOperationContextBase operationContext,
        string? changeToken,
        CancellationToken cancellationToken)
    {
        // TEMPORARY: PAC CLI app (9cee029c-6210-4654-90bb-17e6e9d36617) is not yet preauthorized
        // for the Copilot Studio Island resource. Once preauthorization is granted, remove this
        // flag and the bypass branch to re-enable Island cross-validation.
        const bool isIslandPreauthorized = false;

        if (!isIslandPreauthorized)
        {
            using var yamlContext = YamlSerializationContext.UseYamlPassThroughSerializationContext();
            return await _contentAuthoringService.GetComponentsAsync(operationContext, changeToken, true, false, cancellationToken).ConfigureAwait(false);
        }

#pragma warning disable CS0162 // Unreachable code detected — Island cross-validation kept for future preauthorization
        for (var i = 0; i < 3; i++)
        {
            var islandChangeSet = await RetrieveComponentsFromIslandAsync(operationContext, changeToken, cancellationToken).ConfigureAwait(false);

            PvaComponentChangeSet dataverseChangeSet;
            using (var yamlContext = YamlSerializationContext.UseYamlPassThroughSerializationContext())
            {
                dataverseChangeSet = await _contentAuthoringService.GetComponentsAsync(operationContext, changeToken, true, false, cancellationToken).ConfigureAwait(false);
            }

            if (!AreBotComponentChangesEqual(islandChangeSet, dataverseChangeSet))
            {
                continue;
            }

            if (islandChangeSet.Bot?.Version != dataverseChangeSet.Bot?.Version)
            {
                continue;
            }

            return islandChangeSet.WithBotComponentChanges(dataverseChangeSet.BotComponentChanges).WithBot(dataverseChangeSet.Bot);
        }

        throw new ObjectModelException("Unable to retrieve components", "Unable to retrieve components");
#pragma warning restore CS0162
    }

    public void SetIslandBaseEndpoint(string baseEndpoint)
    {
        if (!baseEndpoint.EndsWith('/'))
        {
            baseEndpoint = baseEndpoint + "/";
        }

        _baseEndpoint.Value = baseEndpoint;
    }

    public void SetConnectionContext(string baseEndpoint, CoreServicesClusterCategory clusterCategory)
    {
        SetIslandBaseEndpoint(baseEndpoint);
        _clusterCategory.Value = clusterCategory;
    }

    public string GetBaseEndpoint()
    {
        return _baseEndpoint.Value ?? throw new InvalidOperationException("Base endpoint is not set.");
    }

    private HttpClient CreateAuthenticatedClient()
    {
        var audienceUri = new Uri(GetTokenAudience(
            _clusterCategory.Value ?? throw new InvalidOperationException("Cluster category is not set. Call SetConnectionContext before making API calls.")));
#pragma warning disable CA2000 // handler is disposed by HttpClient (disposeHandler: true)
        var handler = new BearerTokenHandler(_authProvider, audienceUri);
#pragma warning restore CA2000
#pragma warning disable CA5399 // HttpClient created with custom handler for bearer token auth
        return new HttpClient(handler, disposeHandler: true);
#pragma warning restore CA5399
    }

    /// <summary>
    /// Maps cluster category to the Azure AD application ID used as the token audience
    /// for Copilot Studio Island Control Plane.
    /// Ported from vscode-extensions account.ts getTokenScopeHostName().
    /// </summary>
    private static string GetTokenAudience(CoreServicesClusterCategory clusterCategory) => clusterCategory switch
    {
        CoreServicesClusterCategory.Exp or
        CoreServicesClusterCategory.Dev or
        CoreServicesClusterCategory.Test or
        CoreServicesClusterCategory.Preprod => "api://a522f059-bb65-47c0-8934-7db6e5286414",
        CoreServicesClusterCategory.FirstRelease or
        CoreServicesClusterCategory.Prod => "api://96ff4394-9197-43aa-b393-6a41652e21f8",
        CoreServicesClusterCategory.Gov => "api://9315aedd-209b-43b3-b149-2abff6a95d59",
        CoreServicesClusterCategory.High => "api://69c6e40c-465f-4154-987d-da5cba10734e",
        CoreServicesClusterCategory.DoD => "api://bd4a9f18-e349-4c74-a6b7-65dd465ea9ab",
        _ => throw new InvalidOperationException($"Unsupported cluster category: {clusterCategory}")
    };

    private async Task<PvaComponentChangeSet> RetrieveComponentsFromIslandAsync(AuthoringOperationContextBase operationContext, string? changeToken, CancellationToken cancellationToken)
    {
        if (operationContext is AuthoringOperationContext botContext)
        {
            return await CallBotEndpointAsync(new PvaComponentChangeSet(null, null, changeToken), botContext, cancellationToken, changes: false).ConfigureAwait(false);
        }
        else if (operationContext is BotComponentCollectionAuthoringOperationContext collectionContext)
        {
            return await CallCollectionEndpointAsync(new PvaComponentChangeSet(null, null, changeToken), collectionContext, cancellationToken, changes: false).ConfigureAwait(false);
        }
        else
        {
            throw new NotImplementedException();
        }
    }

    private async Task<PvaComponentChangeSet> CallCollectionEndpointAsync(PvaComponentChangeSet changeToken, BotComponentCollectionAuthoringOperationContext collectionContext, CancellationToken cancellationToken, bool changes)
    {
        using var client = CreateAuthenticatedClient();
        var endpoint = $"{GetBaseEndpoint()}chatbotmanagement/tenants/{collectionContext.OrganizationInfo.TenantId}/environments/{collectionContext.EnvironmentId}/componentcollections/api/{collectionContext.BotComponentCollectionReference.CdsId}";
        using var request = new HttpRequestMessage(
            changes ? HttpMethod.Put : HttpMethod.Post,
            endpoint + (changes ? "/save-content" : "/get-content"));

        SetContent(request, changeToken, collectionContext, changes);

        var result = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        result.EnsureSuccessStatusCode();
        var json = await result.Content.ReadAsStringAsync().ConfigureAwait(false);
        var parsedContent = JsonSerializer.Deserialize<PvaComponentChangeSet>(json, Options);

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

        var id = authoringContext switch
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
        using var client = CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(
            changes ? HttpMethod.Put : HttpMethod.Post,
            $"{GetBaseEndpoint()}api/botmanagement/v1/environments/{botContext.EnvironmentId}/bots/{botContext.BotReference.CdsBotId}/content/botcomponents");

        SetContent(request, changeToken, botContext, changes);

        var result = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        result.EnsureSuccessStatusCode();
        var json = await result.Content.ReadAsStringAsync().ConfigureAwait(false);
        var parsedContent = JsonSerializer.Deserialize<PvaComponentChangeSet>(json, Options);

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

    /// <summary>
    /// DelegatingHandler that acquires bearer tokens via ISyncAuthProvider for each request.
    /// Replaces the pac-specific IAuthenticatedClientFactory + AuthProfile + IslandAuthenticatedHandler pattern.
    /// </summary>
    private sealed class BearerTokenHandler : DelegatingHandler
    {
        private readonly ISyncAuthProvider _authProvider;
        private readonly Uri _audience;

        public BearerTokenHandler(ISyncAuthProvider authProvider, Uri audience)
            : base(new HttpClientHandler())
        {
            _authProvider = authProvider;
            _audience = audience;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = await _authProvider.AcquireTokenAsync(_audience, cancellationToken).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
