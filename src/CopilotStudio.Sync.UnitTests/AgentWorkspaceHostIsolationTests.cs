// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net;
using System.Text;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Microsoft.CopilotStudio.Sync.UnitTests.AgentWorkspaceHostPushTests;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AgentWorkspaceHostIsolationTests
{
    private const string UserAgent = "Contoso.Deployments/1.0";

    private static readonly Uri FabrikamOrganization = new("https://fabrikam.crm.dynamics.com/");

    private readonly RecordingAuthProvider _authentication = new();

    [Fact]
    public void AddCopilotStudioAgentWorkspaceHost_RegistersOnlyOneStatelessHost()
    {
        var services = new ServiceCollection();

        services.AddCopilotStudioAgentWorkspaceHost(UserAgent);
        using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<IAgentWorkspaceHost>(), provider.GetRequiredService<IAgentWorkspaceHost>());
        Assert.Equal(new[] { typeof(IAgentWorkspaceHost) }, services.Select(descriptor => descriptor.ServiceType).Where(type => type.Namespace!.StartsWith("Microsoft.CopilotStudio", StringComparison.Ordinal)));
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IAgentWorkspaceHost)).Lifetime);
    }

    [Fact]
    public void AddCopilotStudioAgentWorkspaceHost_WithoutAContainerOrAUserAgent_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AgentWorkspaceHostServiceCollectionExtensions.AddCopilotStudioAgentWorkspaceHost(null!, UserAgent));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddCopilotStudioAgentWorkspaceHost(" "));
    }

    [Fact]
    public void AddCopilotStudioAgentWorkspaceHost_PoolsAPrimaryHandlerThatNeitherRedirectsNorKeepsCookies()
    {
        using var provider = new ServiceCollection().AddCopilotStudioAgentWorkspaceHost(UserAgent).BuildServiceProvider();

        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(AgentWorkspaceCallFactory.HttpClientName);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        var primary = Assert.IsType<HttpClientHandler>(handler);
        Assert.False(primary.AllowAutoRedirect);
        Assert.False(primary.UseCookies);
    }

    [Fact]
    public async Task CloneAsync_ThroughTheRegisteredHost_SendsOnlyToTheOrganizationWithTheCallersTokenOverOnePooledHandler()
    {
        var handlers = new StubHandlerFactory(_ => Json("{\"value\":[]}"));
        using var provider = Registered(handlers);
        var host = provider.GetRequiredService<IAgentWorkspaceHost>();

        var first = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => host.CloneAsync(CloneRequest(HostedCloud.Organization), _authentication, CancellationToken.None));
        var second = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => host.CloneAsync(CloneRequest(HostedCloud.Organization), _authentication, CancellationToken.None));

        Assert.Equal(AgentWorkspaceHostErrorCodes.BotNotFound, first.Code);
        Assert.Equal(AgentWorkspaceHostErrorCodes.BotNotFound, second.Code);
        Assert.Equal(new[] { AgentWorkspaceCallFactory.HttpClientName, AgentWorkspaceCallFactory.HttpClientName }, handlers.CreatedNames);
        Assert.Equal(2, handlers.Requests.Count);
        Assert.All(handlers.Requests, request =>
        {
            Assert.Equal(HostedCloud.Organization.Authority, request.RequestUri!.Authority);
            Assert.Equal(("Bearer", "token-for-contoso.crm.dynamics.com"), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
            Assert.Contains(UserAgent, request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        });
        Assert.All(_authentication.Audiences, audience => Assert.Equal(HostedCloud.Organization, audience));
    }

    [Fact]
    public async Task CloneAsync_InTwoOrganizationsAtOnce_KeepsEachCallsRequestsAndTokensToItsOwnOrganization()
    {
        using var overlap = new Barrier(2);
        var handlers = new StubHandlerFactory(_ => overlap.SignalAndWait(TimeSpan.FromSeconds(30)) ? Json("{\"value\":[]}") : new HttpResponseMessage(HttpStatusCode.GatewayTimeout));
        using var provider = Registered(handlers);
        var host = provider.GetRequiredService<IAgentWorkspaceHost>();
        var fabrikam = new RecordingAuthProvider();

        Assert.All(await Task.WhenAll(Task.Run(() => Assert.ThrowsAsync<AgentWorkspaceHostException>(() => host.CloneAsync(CloneRequest(HostedCloud.Organization), _authentication, CancellationToken.None))), Task.Run(() => Assert.ThrowsAsync<AgentWorkspaceHostException>(() => host.CloneAsync(CloneRequest(FabrikamOrganization), fabrikam, CancellationToken.None)))), failure => Assert.Equal(AgentWorkspaceHostErrorCodes.BotNotFound, failure.Code));
        Assert.Equal(new[] { HostedCloud.Organization }, _authentication.Audiences);
        Assert.Equal(new[] { FabrikamOrganization }, fabrikam.Audiences);
        Assert.Equal(new (string, string?)[] { ("contoso.crm.dynamics.com", "token-for-contoso.crm.dynamics.com"), ("fabrikam.crm.dynamics.com", "token-for-fabrikam.crm.dynamics.com") }, handlers.Requests.Select(request => (request.RequestUri!.Host, request.Headers.Authorization!.Parameter)).OrderBy(request => request.Host, StringComparer.Ordinal));
    }

    [Fact]
    public async Task FindAgentAsync_ThroughTheRegisteredHost_SendsOnlyOneReadOfTheSchemaNameToTheOrganizationWithTheCallersToken()
    {
        var botId = Guid.NewGuid();
        var handlers = new StubHandlerFactory(_ => Json($"{{\"value\":[{{\"botid\":\"{botId}\"}}]}}"));
        using var provider = Registered(handlers);

        var found = await provider.GetRequiredService<IAgentWorkspaceHost>().FindAgentAsync(HostedCloud.Target(SchemaName), _authentication, CancellationToken.None);

        var request = Assert.Single(handlers.Requests);
        Assert.Equal(botId.ToString(), found);
        Assert.Equal((HttpMethod.Get, "contoso.crm.dynamics.com", "/api/data/v9.2/bots"), (request.Method, request.RequestUri!.Host, request.RequestUri.AbsolutePath));
        Assert.Contains($"schemaname eq '{SchemaName}'", Uri.UnescapeDataString(request.RequestUri.Query), StringComparison.Ordinal);
        Assert.Equal(("Bearer", "token-for-contoso.crm.dynamics.com"), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
        Assert.Equal(new[] { HostedCloud.Organization }, _authentication.Audiences);
    }

    [Theory]
    [InlineData("https://admin@contoso.crm.dynamics.com/")]
    [InlineData("http://contoso.crm.dynamics.com/")]
    public async Task FindAgentAsync_InAnOrganizationAddressedWithUserInfoOrWithoutHttps_RefusesBeforeSendingOrAskingForAToken(string organization)
    {
        var handlers = new StubHandlerFactory(_ => Json("{\"value\":[]}"));
        using var provider = Registered(handlers);

        Assert.Equal(AgentWorkspaceHostErrorCodes.UnexpectedAudience, (await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => provider.GetRequiredService<IAgentWorkspaceHost>().FindAgentAsync(HostedCloud.Target(SchemaName) with { DataverseEndpoint = new Uri(organization) }, _authentication, CancellationToken.None))).Code);
        Assert.Empty(handlers.Requests);
        Assert.Empty(_authentication.Audiences);
    }

    [Fact]
    public async Task FindWorkflowAsync_ThroughTheRegisteredHost_SendsOnlyOneReadOfTheWorkflowToTheOrganizationWithTheCallersToken()
    {
        var workflowId = Guid.NewGuid();
        var handlers = new StubHandlerFactory(_ => Json($"{{\"value\":[{{\"workflowid\":\"{workflowId}\",\"name\":\"Weather\"}}]}}"));
        using var provider = Registered(handlers);

        var found = await provider.GetRequiredService<IAgentWorkspaceHost>().FindWorkflowAsync(WorkflowTarget(HostedCloud.Organization, workflowId), _authentication, CancellationToken.None);

        var request = Assert.Single(handlers.Requests);
        Assert.Equal(workflowId.ToString(), found);
        Assert.Equal((HttpMethod.Get, "contoso.crm.dynamics.com", "/api/data/v9.2/workflows"), (request.Method, request.RequestUri!.Host, request.RequestUri.AbsolutePath));
        Assert.Contains($"workflowid eq {workflowId}", Uri.UnescapeDataString(request.RequestUri.Query), StringComparison.Ordinal);
        Assert.Equal(("Bearer", "token-for-contoso.crm.dynamics.com"), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
        Assert.Equal(new[] { HostedCloud.Organization }, _authentication.Audiences);
    }

    [Theory]
    [InlineData("https://admin@contoso.crm.dynamics.com/")]
    [InlineData("http://contoso.crm.dynamics.com/")]
    public async Task FindWorkflowAsync_InAnOrganizationAddressedWithUserInfoOrWithoutHttps_RefusesBeforeSendingOrAskingForAToken(string organization)
    {
        var handlers = new StubHandlerFactory(_ => Json("{\"value\":[]}"));
        using var provider = Registered(handlers);

        Assert.Equal(AgentWorkspaceHostErrorCodes.UnexpectedAudience, (await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => provider.GetRequiredService<IAgentWorkspaceHost>().FindWorkflowAsync(WorkflowTarget(new Uri(organization), Guid.NewGuid()), _authentication, CancellationToken.None))).Code);
        Assert.Empty(handlers.Requests);
        Assert.Empty(_authentication.Audiences);
    }

    [Fact]
    public async Task PushAsync_OfTwoAgentsAtOnce_KeepsEachSessionToItselfAndReleasesBoth()
    {
        var cloud = new HostedCloud();
        var files = new RecordingFileAccessorFactory();
        var host = cloud.CreateHost(files);
        using var overlap = new Barrier(2);
        cloud.RefusesWorkflow = _ => overlap.SignalAndWait(TimeSpan.FromSeconds(30)) ? null : "The pushes did not overlap.";
        var scaffold = Scaffolds.Agent("Agent", "Contoso Helpdesk", SchemaName, AgentAuthoringModes.Classic);

        Assert.All(await Task.WhenAll(new[] { "First", "Second" }.Select(name => Task.Run(() => host.PushAsync(WithWorkflow(With(scaffold, $"Agent/topics/{name}.mcs.yml", Topic(name)), name, Guid.NewGuid(), "{}"), HostedCloud.Target($"con_{name.ToLowerInvariant()}"), HostedCloud.Options(), _authentication, CancellationToken.None)))), result => Assert.Empty(result.Diagnostics));
        Assert.Contains(cloud.Island.ComponentsOf(cloud.AgentIdOf("con_first")), component => component.SchemaNameString == "con_first.topic.First");
        Assert.DoesNotContain(cloud.Island.ComponentsOf(cloud.AgentIdOf("con_first")), component => component.SchemaNameString.EndsWith(".topic.Second", StringComparison.Ordinal));
        Assert.Contains(cloud.Island.ComponentsOf(cloud.AgentIdOf("con_second")), component => component.SchemaNameString == "con_second.topic.Second");
        Assert.DoesNotContain(cloud.Island.ComponentsOf(cloud.AgentIdOf("con_second")), component => component.SchemaNameString.EndsWith(".topic.First", StringComparison.Ordinal));
        Assert.Equal(new[] { "First", "Second" }, cloud.Cloud.Workflows.Values.Select(workflow => workflow.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(2, files.Opened.Where(root => root.Contains("agent-host-", StringComparison.Ordinal)).Select(SessionOf).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(0, files.LiveCount);
    }

    [Fact]
    public async Task CloneAsync_ForTwoCustomersWithStructurallyEqualTopics_ReturnsOnlyEachCustomersOwnText()
    {
        var first = new HostedCloud();
        var second = new HostedCloud();
        var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var triggerPhrase = $"help {Guid.NewGuid():N}";
        first.SeedAgent("con_contoso", "Contoso Helpdesk", CommentedTopic("con_contoso", "# Contoso internal note", triggerPhrase));
        second.SeedAgent("con_fabrikam", "Fabrikam Helpdesk", CommentedTopic("con_fabrikam", "# Fabrikam internal note", triggerPhrase));
        first.Island.Serves = AsReadFromDataverse;
        second.Island.Serves = AsReadFromDataverse;
        first.Island.Pauses = _ =>
        {
            firstRead.TrySetResult();
            return secondRead.Task.WaitAsync(TimeSpan.FromSeconds(30));
        };
        second.Island.Pauses = _ =>
        {
            secondRead.TrySetResult();
            return Task.CompletedTask;
        };

        var firstClone = first.CreateHost().CloneAsync(new AgentCloneRequest(HostedCloud.Target("con_contoso"), "Agent", new Dictionary<string, string>(), Array.Empty<string>()), _authentication, CancellationToken.None);
        await Task.WhenAny(firstRead.Task, firstClone);
        var results = await Task.WhenAll(firstClone, second.CreateHost().CloneAsync(new AgentCloneRequest(HostedCloud.Target("con_fabrikam"), "Agent", new Dictionary<string, string>(), Array.Empty<string>()), new RecordingAuthProvider(), CancellationToken.None));

        Assert.Contains("# Contoso internal note", Text(results[0].Snapshot, "Agent/topics/Help.mcs.yml"), StringComparison.Ordinal);
        Assert.DoesNotContain("Fabrikam", Text(results[0].Snapshot, "Agent/topics/Help.mcs.yml"), StringComparison.Ordinal);
        Assert.Contains("# Fabrikam internal note", Text(results[1].Snapshot, "Agent/topics/Help.mcs.yml"), StringComparison.Ordinal);
        Assert.DoesNotContain("Contoso", Text(results[1].Snapshot, "Agent/topics/Help.mcs.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryCall_BuildsItsOwnContainerWithoutAnHttpClientFactoryOrItsOwnHandlers()
    {
        var cloud = new HostedCloud();
        var containers = new List<IServiceCollection>();
        var host = cloud.CreateHost(configure: containers.Add);

        await host.CloneAsync(new AgentCloneRequest(HostedCloud.Target(SchemaName, (await host.PushAsync(Scaffolds.Agent("Agent", "Contoso Helpdesk", SchemaName, AgentAuthoringModes.CliCopilot), HostedCloud.Target(SchemaName), HostedCloud.Options(), _authentication, CancellationToken.None)).BotId), "Agent", new Dictionary<string, string>(), Array.Empty<string>()), _authentication, CancellationToken.None);

        Assert.Equal(2, containers.Distinct().Count());
        Assert.All(containers, services => Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHttpClientFactory) || descriptor.ServiceType == typeof(IHttpMessageHandlerFactory)));
    }

    [Theory]
    [InlineData("https://fabrikam.crm.dynamics.com/")]
    [InlineData("http://contoso.crm.dynamics.com/")]
    [InlineData("https://contoso.crm.dynamics.com:8443/")]
    [InlineData("https://admin@contoso.crm.dynamics.com/")]
    [InlineData("https://contoso.crm.dynamics.com.fabrikam.com/")]
    [InlineData("https://api.powerplatform.com/.default")]
    public async Task AcquireTokenAsync_ForAnyAudienceButTheOrganization_RefusesBeforeAskingTheCaller(string audience)
    {
        var guard = new AudienceGuardedSyncAuthProvider(_authentication, HostedCloud.Organization);

        Assert.Equal(AgentWorkspaceHostErrorCodes.UnexpectedAudience, (await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => guard.AcquireTokenAsync(new Uri(audience)))).Code);
        Assert.Empty(_authentication.Audiences);
    }

    [Theory]
    [InlineData("https://contoso.crm.dynamics.com/")]
    [InlineData("https://CONTOSO.crm.dynamics.com/.default")]
    [InlineData("https://contoso.crm.dynamics.com:443/api/data/v9.2/")]
    public async Task AcquireTokenAsync_ForTheOrganizationUnderAnyPathOrCase_AsksTheCaller(string audience)
    {
        Assert.Equal("token-for-contoso.crm.dynamics.com", await new AudienceGuardedSyncAuthProvider(_authentication, HostedCloud.Organization).AcquireTokenAsync(new Uri(audience)));
        Assert.Equal(new[] { new Uri(audience) }, _authentication.Audiences);
    }

    [Fact]
    public async Task CreateClient_ForARequestOutsideTheOrganization_RefusesBeforeSendingOrAskingForAToken()
    {
        var handlers = new StubHandlerFactory(_ => Json("{}"));
        using var accessor = new AudienceGuardedSyncAuthProvider(_authentication, HostedCloud.Organization).CreateDataverseAccessor(handlers, AgentWorkspaceCallFactory.HttpClientName);
        using var client = accessor.CreateClient();

        Assert.Equal(AgentWorkspaceHostErrorCodes.UnexpectedAudience, (await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => client.GetAsync(new Uri(FabrikamOrganization, "api/data/v9.2/bots")))).Code);
        Assert.Empty(handlers.Requests);
        Assert.Empty(_authentication.Audiences);
    }

    [Fact]
    public async Task CreateClient_SharesOneHandlerAcrossTheClientsOfACall()
    {
        var handlers = new StubHandlerFactory(_ => Json("{}"));
        using var accessor = new AudienceGuardedSyncAuthProvider(_authentication, HostedCloud.Organization).CreateDataverseAccessor(handlers, AgentWorkspaceCallFactory.HttpClientName);

        foreach (var path in new[] { "api/data/v9.2/bots", "api/data/v9.2/workflows" })
        {
            using var client = accessor.CreateClient();
            (await client.GetAsync(new Uri(HostedCloud.Organization, path))).Dispose();
        }

        Assert.Equal(new[] { AgentWorkspaceCallFactory.HttpClientName }, handlers.CreatedNames);
        Assert.Equal(new[] { HostedCloud.Organization, HostedCloud.Organization }, _authentication.Audiences);
        Assert.All(handlers.Requests, request => Assert.Equal("token-for-contoso.crm.dynamics.com", request.Headers.Authorization!.Parameter));
    }

    internal static ServiceProvider Registered(IHttpMessageHandlerFactory handlers) => new ServiceCollection().AddCopilotStudioAgentWorkspaceHost(UserAgent).AddSingleton(handlers).BuildServiceProvider();

    private static BotComponentBase CommentedTopic(string schemaName, string comment, string triggerPhrase) => PushComponentCollectionExclusionTests.Topic($"{schemaName}.topic.Help", "Help").WithDialog(CodeSerializer.Deserialize<AdaptiveDialog>($"{comment}\nkind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  id: main\n  intent:\n    displayName: Help\n    triggerQueries:\n      - {triggerPhrase}\n\n  actions:\n    - kind: SendActivity\n      id: sendMessage_Help\n      activity: How can I help?\n")!);

    private static BotComponentBase AsReadFromDataverse(BotComponentBase component)
    {
        var dataverse = new InMemoryFileAccessor(new DirectoryPath("c:/dataverse/"));
        WorkspaceSynchronizer.WriteCloudCache(dataverse, new BotDefinition().WithComponents(new[] { component }));

        return WorkspaceSynchronizer.ReadCloudCacheSnapshot(dataverse)!.Components.Single();
    }

    private static WorkflowSyncTarget WorkflowTarget(Uri organization, Guid workflowId) => new(organization, HostedCloud.EnvironmentId, HostedCloud.TenantId, workflowId.ToString());

    private static AgentCloneRequest CloneRequest(Uri organization) => new(new AgentSyncTarget(organization, HostedCloud.EnvironmentId, HostedCloud.TenantId, null, SchemaName, "Contoso Helpdesk", string.Empty), "Agent", new Dictionary<string, string>(), Array.Empty<string>());

    internal static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private static string SessionOf(string root) => root.Substring(0, root.IndexOf("/", root.IndexOf("agent-host-", StringComparison.Ordinal), StringComparison.Ordinal));
}
