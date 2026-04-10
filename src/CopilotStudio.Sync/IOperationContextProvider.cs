// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Impl.PullAgent/IOperationContextProvider.cs

using Microsoft.Agents.Platform.Content;
using System.Collections.Immutable;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>
/// Get an operation context that is needed to pass to a <see cref="IContentAuthoringService"/>.
/// </summary>
public interface IOperationContextProvider
{
    Task<AuthoringOperationContextBase> GetAsync(AgentSyncInfo agentInfo);

    Task<ImmutableArray<AuthoringOperationContextBase>> GetAllAsync(AgentSyncInfo agentInfo, AssetsToClone assetsToClone);
}

internal class OperationContextProvider : IOperationContextProvider
{
    public async Task<ImmutableArray<AuthoringOperationContextBase>> GetAllAsync(AgentSyncInfo agentInfo, AssetsToClone assetsToClone)
    {
        var accountInfo = agentInfo.AccountInfo ?? throw new InvalidOperationException($"Missing AccountInfo");

        var organizationInfo = GetOrganizationInfo(agentInfo);
        var result = ImmutableArray.CreateBuilder<AuthoringOperationContextBase>();
        if (assetsToClone.CloneAgent)
        {
            result.Add(await GetAsync(agentInfo).ConfigureAwait(false));
        }

        foreach (var item in assetsToClone.ComponentCollectionIds)
        {
            var reference = new BotComponentCollectionReference(agentInfo.EnvironmentId, item);
            result.Add(new BotComponentCollectionAuthoringOperationContext(new PrincipalObjectReference(accountInfo.TenantId, Guid.Empty), organizationInfo, reference, null));
        }

        return result.ToImmutable();
    }

    public Task<AuthoringOperationContextBase> GetAsync(AgentSyncInfo agentInfo)
    {
        var accountInfo = agentInfo.AccountInfo ?? throw new InvalidOperationException($"Missing AccountInfo");

        var principal = new PrincipalObjectReference(accountInfo.TenantId, Guid.Empty);
        var organizationInfo = GetOrganizationInfo(agentInfo);

        AuthoringOperationContextBase botOperationContext;
        if (agentInfo.AgentId != null)
        {
            botOperationContext = new AuthoringOperationContext(
                principal,
                organizationInfo,
                agentInfo.BotReference,
                null,
                false);
        }
        else if (agentInfo.ComponentCollectionId != null)
        {
            var reference = new BotComponentCollectionReference(agentInfo.EnvironmentId, agentInfo.ComponentCollectionId.Value);

            botOperationContext = new BotComponentCollectionAuthoringOperationContext(
                principal,
                organizationInfo,
                reference,
                null);
        }
        else
        {
            throw new InvalidOperationException($"Sync info is missing Id");
        }

        return Task.FromResult(botOperationContext);
    }

    private static CdsOrganizationInfo GetOrganizationInfo(AgentSyncInfo agentInfo)
    {
        var accountInfo = agentInfo.AccountInfo ?? throw new InvalidOperationException($"Missing AccountInfo");
        var solutionVersions = agentInfo.SolutionVersions ?? throw new InvalidOperationException($"Missing SolutionVersions");

        return new CdsOrganizationInfo(
            accountInfo.TenantId,
            agentInfo.DataverseEndpoint,
            pvaSolutionVersion: solutionVersions.CopilotStudioSolutionVersion,
            dvTableSearchGlossaryAndSynonymsSolutionVersion: solutionVersions.GetRelevanceSearchSolutionVersion(),
            dvTableSearchSolutionVersion: solutionVersions.GetDataverseTableSearchSolutionUniqueName());
    }
}
