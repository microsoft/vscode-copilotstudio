// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>A component collection a push sequence pushed, with what pushing it produced.</summary>
/// <param name="Folder">The folder holding the collection project.</param>
/// <param name="Pushed">What pushing the collection produced.</param>
public sealed record PushedComponentCollection<TPushed>(DirectoryPath Folder, TPushed Pushed);

/// <summary>What pushing an agent after the component collections it declares produced.</summary>
public sealed class ComponentCollectionPushResult<TPushed>
{
    /// <summary>Creates the result of pushing an agent after its component collections.</summary>
    /// <param name="agent">What pushing the agent produced.</param>
    public ComponentCollectionPushResult(TPushed agent) => Agent = agent;

    /// <summary>The collections that were pushed, in the order they were pushed.</summary>
    public ImmutableArray<PushedComponentCollection<TPushed>> Collections { get; init; } = ImmutableArray<PushedComponentCollection<TPushed>>.Empty;

    /// <summary>The declared directories, as written, that resolve to no collection project inside the project.</summary>
    public ImmutableArray<string> Missing { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>What pushing the agent produced.</summary>
    public TPushed Agent { get; }
}

/// <summary>Pushes the component collections an agent declares before the agent, so the links the agent's push writes find every collection already in the target.</summary>
public static class ComponentCollectionPushSequence
{
    /// <summary>Pushes each resolved collection the caller lets through, in declaration order, then the agent, stopping at the first push that fails.</summary>
    /// <param name="collections">The collections the agent declares.</param>
    /// <param name="agentWorkspaceFolder">The folder holding the agent's workspace.</param>
    /// <param name="pushWorkspaceAsync">Pushes one workspace folder.</param>
    /// <param name="shouldPushCollectionAsync">Decides whether a collection is pushed, for example only when it is connected to the target; <see langword="null"/> pushes every collection.</param>
    /// <param name="cancellationToken">Cancels the sequence.</param>
    public static async Task<ComponentCollectionPushResult<TPushed>> PushAsync<TPushed>(ComponentCollectionFolderResolution collections, DirectoryPath agentWorkspaceFolder, Func<DirectoryPath, CancellationToken, Task<TPushed>> pushWorkspaceAsync, Func<DirectoryPath, CancellationToken, Task<bool>>? shouldPushCollectionAsync, CancellationToken cancellationToken)
    {
        if (collections is null)
        {
            throw new ArgumentNullException(nameof(collections));
        }

        if (pushWorkspaceAsync is null)
        {
            throw new ArgumentNullException(nameof(pushWorkspaceAsync));
        }

        var pushed = ImmutableArray.CreateBuilder<PushedComponentCollection<TPushed>>();

        foreach (var collection in collections.Found)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (shouldPushCollectionAsync is not null && !await shouldPushCollectionAsync(collection, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            pushed.Add(new PushedComponentCollection<TPushed>(collection, await pushWorkspaceAsync(collection, cancellationToken).ConfigureAwait(false)));
        }

        return new ComponentCollectionPushResult<TPushed>(await pushWorkspaceAsync(agentWorkspaceFolder, cancellationToken).ConfigureAwait(false))
        {
            Collections = pushed.ToImmutable(),
            Missing = collections.Missing,
        };
    }
}
