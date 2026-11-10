// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.CopilotStudio.Sync.Hosting;

/// <summary>Registers the in-memory agent workspace host.</summary>
public static class AgentWorkspaceHostServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IAgentWorkspaceHost"/> as a stateless singleton that gives every operation its own in-memory session and child service provider over pooled Dataverse handlers, and turns off ObjectModel's process-wide node cache so concurrent operations never share each other's original YAML text.</summary>
    /// <param name="services">The container to register into.</param>
    /// <param name="userAgent">The user agent reported on Dataverse requests.</param>
    /// <returns>The same container, for chaining.</returns>
    public static IServiceCollection AddCopilotStudioAgentWorkspaceHost(this IServiceCollection services, string userAgent)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        if (string.IsNullOrWhiteSpace(userAgent))
        {
            throw new ArgumentException("A user agent is required.", nameof(userAgent));
        }

        services.AddUnredirectedHttpClient(AgentWorkspaceCallFactory.HttpClientName);
        services.AddSingleton<IAgentWorkspaceHost>(provider => new AgentWorkspaceHost(new AgentWorkspaceCallFactory(userAgent, provider.GetRequiredService<IHttpMessageHandlerFactory>())));

        return services;
    }
}
