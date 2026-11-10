// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync.Hosting;

internal sealed class AgentClonePipeline
{
    private readonly AgentWorkspaceCall _call;

    public AgentClonePipeline(AgentWorkspaceCall call) => _call = call;

    public async Task<AgentCloneResult> RunAsync(AgentCloneRequest request, Guid? botId, CancellationToken cancellationToken)
    {
        var changeProbe = request.ReadChangeProbe ? await _call.ProbeOrNullAsync(botId, request.Source.SchemaName, cancellationToken).ConfigureAwait(false) : null;
        var clone = await CloneAsync(_call, request, botId, failure => AgentWorkspaceHostException.BotNotFound(request.Source, failure), cancellationToken).ConfigureAwait(false);

        return new AgentCloneResult(clone.Read(_call.Open), clone.AgentId.ToString()) { ChangeProbe = changeProbe };
    }

    public static async Task<AgentClone> CloneAsync(IAgentCloneSpace space, AgentCloneRequest request, Guid? botId, Func<DataverseRequestException?, Exception> notFound, CancellationToken cancellationToken)
    {
        var source = request.Source;
        var agentId = await WorkspaceCloneSequence.FindAgentAsync(space.Dataverse, botId, source.SchemaName, notFound, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var agent = await WorkspaceCloneSequence.ReadAgentAsync(space.Dataverse, agentId, notFound, cancellationToken).ConfigureAwait(false);
        var syncInfo = await WorkspaceCloneSequence.ForAgentAsync(space.Dataverse, agentId, source.DataverseEndpoint, source.EnvironmentId, source.TenantId, cancellationToken).ConfigureAwait(false);
        var claims = new FolderClaims(request);
        var collections = new List<(Guid Id, string Folder)>();

        foreach (var collection in agent.ComponentCollections.Where(collection => collection.Id != Guid.Empty))
        {
            collections.Add((collection.Id, await claims.ClaimAsync(collection, cancellationToken).ConfigureAwait(false)));
        }

        var contexts = await space.OperationContexts.GetAllAsync(syncInfo, new AssetsToClone { CloneAgent = true, ComponentCollectionIds = collections.Select(collection => collection.Id).ToImmutableArray() }).ConfigureAwait(false);
        var folders = collections.ToDictionary(collection => collection.Id, collection => collection.Folder);
        cancellationToken.ThrowIfCancellationRequested();
        await WorkspaceCloneSequence.CloneAsync(space.Synchronizer, contexts.Select(context => new KeyValuePair<AuthoringOperationContextBase, DirectoryPath>(context, space.FolderOf(context is BotComponentCollectionAuthoringOperationContext collection ? folders[collection.BotComponentCollectionReference.CdsId] : request.Folder))).ToArray(), syncInfo, space.Dataverse, cancellationToken).ConfigureAwait(false);

        return new AgentClone(request.Folder, agentId, syncInfo, collections);
    }

    private sealed class FolderClaims
    {
        private readonly AgentCloneRequest _request;

        private readonly Dictionary<string, string> _known;

        private readonly HashSet<string> _taken;

        public FolderClaims(AgentCloneRequest request)
        {
            _request = request;
            _known = (request.CollectionFolders ?? new Dictionary<string, string>()).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
            _taken = new HashSet<string>((request.OccupiedFolders ?? Array.Empty<string>()).Concat(_known.Values).Append(request.Folder), StringComparer.OrdinalIgnoreCase);
        }

        public async Task<string> ClaimAsync(ComponentCollectionInfo collection, CancellationToken cancellationToken)
        {
            if (collection.SchemaName is { } schemaName && _known.TryGetValue(schemaName, out var known))
            {
                return known;
            }

            var claimed = await FreeFolderAsync(WorkspaceFolderNames.ForProject(NameOf(collection)), cancellationToken).ConfigureAwait(false);
            _taken.Add(claimed);

            return claimed;
        }

        private static string NameOf(ComponentCollectionInfo collection) => !string.IsNullOrWhiteSpace(collection.DisplayName) ? collection.DisplayName : !string.IsNullOrWhiteSpace(collection.SchemaName) ? collection.SchemaName : collection.Id.ToString();

        private async Task<string> FreeFolderAsync(string folder, CancellationToken cancellationToken)
        {
            var candidate = folder;

            for (var suffix = 2; await IsTakenAsync(candidate, cancellationToken).ConfigureAwait(false); suffix++)
            {
                candidate = $"{folder}-{suffix}";
            }

            return candidate;
        }

        private async Task<bool> IsTakenAsync(string candidate, CancellationToken cancellationToken)
        {
            if (_taken.Contains(candidate))
            {
                return true;
            }

            if (!await IsReportedTakenAsync(candidate, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            _taken.Add(candidate);

            return true;
        }

        private async Task<bool> IsReportedTakenAsync(string candidate, CancellationToken cancellationToken) => _request.IsFolderTaken is { } isFolderTaken && await isFolderTaken(candidate, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record AgentClone(string Folder, Guid AgentId, AgentSyncInfo SyncInfo, IReadOnlyList<(Guid Id, string Folder)> Collections)
{
    public IEnumerable<string> Folders => Collections.Select(collection => collection.Folder).Prepend(Folder);

    public AgentWorkspaceSnapshot Read(Func<string, IFileAccessor> open) => WorkspaceSnapshotFiles.Read(Folder, Folders.Select(folder => new KeyValuePair<string, IFileAccessor>(folder, open(folder))));
}
