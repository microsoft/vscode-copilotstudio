// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Concurrent;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Moq;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

internal sealed class FakeIsland
{
    private readonly object _gate = new();

    private readonly Dictionary<Guid, IslandStore> _stores = new();

    public FakeIsland()
    {
        Service.Setup(service => service.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((AuthoringOperationContextBase context, string? changeToken, CancellationToken _) => RespondAsync(context, changeToken));
        Service.Setup(service => service.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns((AuthoringOperationContextBase context, PvaComponentChangeSet changes, CancellationToken _) => SaveFailures.TryDequeue(out var failure) ? Task.FromException<PvaComponentChangeSet>(failure) : Task.FromResult(Save(context, changes)));
    }

    public Mock<IIslandControlPlaneService> Service { get; } = new();

    public ConcurrentQueue<Exception> SaveFailures { get; } = new();

    public List<AuthoringOperationContextBase> Saves { get; } = new();

    public List<PvaComponentChangeSet> SavedChanges { get; } = new();

    public ConcurrentQueue<string> Inserted { get; } = new();

    public ConcurrentQueue<string> EnvironmentVariablesInserted { get; } = new();

    public BotEntity Entity { get; } = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cree9_agent")!;

    public Func<BotComponentBase, BotComponentBase>? Normalizes { get; set; }

    public Func<BotEntity, BotEntity>? NormalizesBot { get; set; }

    public Func<BotComponentBase, BotComponentBase>? Serves { get; set; }

    public Func<AuthoringOperationContextBase, Task>? Pauses { get; set; }

    public Func<BotComponentBase, bool>? Rejects { get; set; }

    public Func<Guid, IEnumerable<BotComponentCollection>>? LinkedCollections { get; set; }

    public Func<Guid, BotComponentCollection?>? CollectionOf { get; set; }

    public static Guid IdentityOf(AuthoringOperationContextBase context) => context switch
    {
        AuthoringOperationContext agent => agent.BotReference.CdsBotId,
        BotComponentCollectionAuthoringOperationContext collection => collection.BotComponentCollectionReference.CdsId,
        _ => throw new ArgumentException($"Unexpected context {context.GetType().Name}.", nameof(context)),
    };

    public void SetEntity(Guid identity, BotEntity entity)
    {
        lock (_gate)
        {
            StoreOf(identity).Entity = entity;
        }
    }

    public void Seed(Guid identity, params BotComponentBase[] components)
    {
        lock (_gate)
        {
            var store = StoreOf(identity);

            foreach (var component in components)
            {
                store.Put(component.Id.Value == Guid.Empty ? component.WithId(Guid.NewGuid()) : component);
            }

            store.Token++;
        }
    }

    public IReadOnlyList<BotComponentBase> ComponentsOf(Guid identity)
    {
        lock (_gate)
        {
            return _stores.TryGetValue(identity, out var store) ? store.Components.ToArray() : Array.Empty<BotComponentBase>();
        }
    }

    public BotEntity? EntityOf(Guid identity)
    {
        lock (_gate)
        {
            return _stores.TryGetValue(identity, out var store) ? store.Entity : null;
        }
    }

    public int SaveCountOf(Guid identity)
    {
        lock (_gate)
        {
            return Saves.Count(context => IdentityOf(context) == identity);
        }
    }

    private async Task<PvaComponentChangeSet> RespondAsync(AuthoringOperationContextBase context, string? changeToken)
    {
        var response = Read(context, changeToken);

        if (Pauses is { } pause)
        {
            await pause(context).ConfigureAwait(false);
        }

        return response;
    }

    private PvaComponentChangeSet Read(AuthoringOperationContextBase context, string? changeToken)
    {
        lock (_gate)
        {
            var identity = IdentityOf(context);
            var store = StoreOf(identity);
            var since = changeToken is { } token && token.StartsWith("token-", StringComparison.Ordinal) && int.TryParse(token.Substring("token-".Length), out var tokenNumber) ? tokenNumber : (int?)null;
            var components = store.Components.Where(component => since is not { } sinceToken || store.Stamps[component.SchemaNameString].Changed >= sinceToken).Select(component => since is { } sinceToken && store.Stamps[component.SchemaNameString].Created < sinceToken ? (BotComponentChange)new BotComponentUpdate(Serves?.Invoke(component) ?? component) : new BotComponentInsert(Serves?.Invoke(component) ?? component)).ToArray();
            var entity = context is BotComponentCollectionAuthoringOperationContext ? null : store.Entity ?? Entity;
            var collections = (context is AuthoringOperationContext ? LinkedCollections?.Invoke(identity).ToArray() : CollectionOf?.Invoke(identity) is { } self ? new[] { self } : null) is { Length: > 0 } linked ? linked.Select(collection => (BotComponentCollectionChange)new BotComponentCollectionInsert(collection)).ToArray() : null;

            return collections is null
                ? new PvaComponentChangeSet(components, entity, $"token-{store.Token}")
                : new PvaComponentChangeSet(botComponentChanges: components, connectorDefinitionChanges: null, environmentVariableChanges: null, connectionReferenceChanges: null, aIPluginOperationChanges: null, componentCollectionChanges: collections, dataverseTableSearchChanges: null, dataverseTableSearchEntityConfigurationChanges: null, connectedAgentDefinitionChanges: null, bot: entity, changeToken: $"token-{store.Token}");
        }
    }

    private PvaComponentChangeSet Save(AuthoringOperationContextBase context, PvaComponentChangeSet changes)
    {
        lock (_gate)
        {
            Saves.Add(context);
            SavedChanges.Add(changes);
            var store = StoreOf(IdentityOf(context));
            var saved = new List<BotComponentChange>();

            foreach (var change in changes.BotComponentChanges)
            {
                switch (change)
                {
                    case BotComponentInsert { Component: { } inserted } when Rejects?.Invoke(inserted) != true:
                        var stored = Normalize(inserted.WithId(Guid.NewGuid()));
                        store.Put(stored);
                        Inserted.Enqueue(stored.SchemaNameString);
                        saved.Add(new BotComponentInsert(stored));
                        break;
                    case BotComponentUpdate { Component: { } updated } when Rejects?.Invoke(updated) != true:
                        var previous = store.Find(updated.SchemaNameString);
                        var replaced = Normalize(updated.WithId(previous?.Id.Value ?? updated.Id.Value).WithVersion((previous?.Version ?? updated.Version) + 1));
                        store.Put(replaced);
                        saved.Add(new BotComponentUpdate(replaced));
                        break;
                    case BotComponentDelete deleted when store.Components.FirstOrDefault(component => component.Id.Value == deleted.BotComponentId.Value) is { } removed:
                        store.Components.Remove(removed);
                        saved.Add(new BotComponentDelete(removed.Id, removed.Version));
                        break;
                }
            }

            if (changes.Bot is { } bot)
            {
                store.Entity = NormalizesBot?.Invoke(bot) ?? bot;
            }

            foreach (var variable in changes.EnvironmentVariableChanges.IsDefault ? Enumerable.Empty<EnvironmentVariableDefinition>() : changes.EnvironmentVariableChanges.OfType<EnvironmentVariableInsert>().Select(insert => insert.EnvironmentVariable).OfType<EnvironmentVariableDefinition>())
            {
                EnvironmentVariablesInserted.Enqueue(variable.SchemaName.Value);
            }

            store.Token++;

            return new PvaComponentChangeSet(saved, changes.Bot is null ? null : store.Entity, $"token-{store.Token}");
        }
    }

    private BotComponentBase Normalize(BotComponentBase component) => Normalizes?.Invoke(component) ?? component;

    private IslandStore StoreOf(Guid identity)
    {
        if (!_stores.TryGetValue(identity, out var store))
        {
            store = new IslandStore();
            _stores[identity] = store;
        }

        return store;
    }

    private sealed class IslandStore
    {
        public List<BotComponentBase> Components { get; } = new();

        public BotEntity? Entity { get; set; }

        public int Token { get; set; } = 1;

        public Dictionary<string, (int Created, int Changed)> Stamps { get; } = new(StringComparer.OrdinalIgnoreCase);

        public BotComponentBase? Find(string schemaName) => Components.FirstOrDefault(component => string.Equals(component.SchemaNameString, schemaName, StringComparison.OrdinalIgnoreCase));

        public void Put(BotComponentBase component)
        {
            var index = Components.FindIndex(existing => string.Equals(existing.SchemaNameString, component.SchemaNameString, StringComparison.OrdinalIgnoreCase));
            Stamps[component.SchemaNameString] = (index >= 0 && Stamps.TryGetValue(component.SchemaNameString, out var stamp) ? stamp.Created : Token, Token);

            if (index < 0)
            {
                Components.Add(component);
            }
            else
            {
                Components[index] = component;
            }
        }
    }
}
