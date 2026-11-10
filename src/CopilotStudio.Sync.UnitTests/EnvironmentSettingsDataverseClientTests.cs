// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class EnvironmentSettingsDataverseClientTests
{
    private const string Organization = "https://test.crm.dynamics.com";

    private const string ReportedSynchronizationStatus = """{"$kind":"BotSynchronizationDetails","contentVersion":2,"lastFinishedPublishOperation":{"$kind":"PublishResultDetails","userId":"7541accf-ad35-46a7-b2c1-72148c66c078","operationStart":"2026-10-25T19:46:02.4946475Z","operationEnd":"2026-10-25T19:46:12.2437292Z","status":"Succeeded"},"currentSynchronizationState":{"$kind":"SynchronizationState","botRegistration":{"$kind":"BotRegistration"},"provisioningStatus":"Provisioned","state":"Synchronized"}}""";

    private readonly List<(string Method, string Uri, string Body)> _requests = new();

    private Func<string, string> _respond = _ => "{\"value\":[]}";

    public static TheoryData<string> RowsOfBotsWithNoFinishedPublish => new()
    {
        "{}",
        Bot(null),
        Bot("""{"$kind":"BotSynchronizationDetails","contentVersion":2,"currentSynchronizationState":{"provisioningStatus":"Provisioned","state":"Synchronized"}}"""),
        Bot("""{"lastFinishedPublishOperation":null}"""),
    };

    public static TheoryData<string> RowsWithAnUnreadableSynchronizationStatus => Rows(RowsWhoseStatusIsNoJsonObject.Concat(new[]
    {
        Bot("""{"lastFinishedPublishOperation":"Succeeded"}"""),
        Bot("""{"lastFinishedPublishOperation":{}}"""),
        Bot("""{"lastFinishedPublishOperation":{"status":""}}"""),
        Bot("""{"lastFinishedPublishOperation":{"status":1}}"""),
        Bot("""{"lastFinishedPublishOperation":{"status":"Succeeded","operationStart":"yesterday"}}"""),
        Bot("""{"lastFinishedPublishOperation":{"status":"Succeeded","operationEnd":5}}"""),
    }));

    public static TheoryData<string> RowsWithNoJsonObjectAsTheirStatus => Rows(RowsWhoseStatusIsNoJsonObject);

    public static TheoryData<string> RowsOfBotsWithNoProvisioningStatus => new()
    {
        "{}",
        Bot(null),
        Bot("""{"lastFinishedPublishOperation":{"status":"Succeeded"}}"""),
        Bot("""{"lastFinishedPublishOperation":"Succeeded","currentSynchronizationState":null}"""),
        Bot("""{"currentSynchronizationState":"Provisioning"}"""),
        Bot("""{"currentSynchronizationState":{"state":"Synchronizing"}}"""),
        Bot("""{"currentSynchronizationState":{"provisioningStatus":5}}"""),
    };

    private static string[] RowsWhoseStatusIsNoJsonObject => new[] { "[]", """{"synchronizationstatus":5}""", Bot(string.Empty), Bot("{"), Bot("null"), Bot("[]") };

    [Fact]
    public async Task GetComponentCollectionsBySchemaNamesAsync_ReadsEveryNamedCollectionManyPerRequest()
    {
        var identifiers = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        _respond = query => "{\"value\":[" + string.Join(",", Regex.Matches(query, "schemaname eq '([^']*)'").Select(match => match.Groups[1].Value).Select(name => $"{{\"botcomponentcollectionid\":\"{identifiers[name] = Guid.NewGuid()}\",\"schemaname\":\"{name}\",\"name\":\"Tools {name}\"}}")) + "]}";

        var collections = await Client().GetComponentCollectionsBySchemaNamesAsync(Enumerable.Range(0, 60).Select(index => $"cr_tools{index:D2}").Append("CR_TOOLS07").Append(" "), CancellationToken.None);

        Assert.Equal(2, _requests.Count);
        Assert.All(_requests, request => Assert.Contains("$select=botcomponentcollectionid,schemaname,name", request.Uri, StringComparison.Ordinal));
        Assert.Equal(60, collections.Count);
        Assert.Equal(("cr_tools07", "Tools cr_tools07", identifiers["cr_tools07"]), (collections["CR_TOOLS07"].SchemaName, collections["CR_TOOLS07"].DisplayName, collections["CR_TOOLS07"].Id));
    }

    [Fact]
    public async Task GetAgentInfoAsync_ReadsTheSchemaNameOfTheBot()
    {
        var agentId = Guid.NewGuid();
        _respond = _ => $"{{\"botid\":\"{agentId}\",\"name\":\"Contoso\",\"schemaname\":\"cr_contoso\",\"bot_botcomponentcollection\":[]}}";

        var agent = await Client().GetAgentInfoAsync(agentId, CancellationToken.None);

        Assert.Contains($"bots({agentId})?$select=botid,name,iconbase64,schemaname&", Assert.Single(_requests).Uri, StringComparison.Ordinal);
        Assert.Equal((agentId, "Contoso", "cr_contoso"), (agent.AgentId, agent.DisplayName, agent.SchemaName));
    }

    [Fact]
    public async Task PublishAgentAsync_PostsThePublishActionOfTheBot()
    {
        var agentId = Guid.NewGuid();

        await Client().PublishAgentAsync(agentId, CancellationToken.None);

        var request = Assert.Single(_requests);
        Assert.Equal(("POST", $"{Organization}/api/data/v9.2/bots({agentId})/Microsoft.Dynamics.CRM.PvaPublish"), (request.Method, request.Uri));
        Assert.Empty(JsonNode.Parse(request.Body)!.AsObject());
    }

    [Theory]
    [InlineData(ReportedSynchronizationStatus, "Succeeded", "2026-10-25T19:46:02.4946475Z", "2026-10-25T19:46:12.2437292Z")]
    [InlineData("""{"lastFinishedPublishOperation":{"status":"Failed"}}""", "Failed", null, null)]
    [InlineData("""{"lastFinishedPublishOperation":{"status":"Failed","operationStart":null,"operationEnd":null}}""", "Failed", null, null)]
    public async Task GetLastPublishOperationAsync_ReadsTheLastFinishedPublishFromTheBotsSynchronizationStatus(string synchronizationStatus, string status, string? operationStart, string? operationEnd)
    {
        var agentId = Guid.NewGuid();
        _respond = _ => Bot(synchronizationStatus);

        var operation = await Client().GetLastPublishOperationAsync(agentId, CancellationToken.None);

        Assert.Equal(("GET", $"{Organization}/api/data/v9.2/bots({agentId})?$select=synchronizationstatus"), (Assert.Single(_requests).Method, _requests[0].Uri));
        Assert.Equal(new AgentPublishOperation(status, Time(operationStart), Time(operationEnd)), operation);
    }

    [Theory]
    [MemberData(nameof(RowsOfBotsWithNoFinishedPublish))]
    public async Task GetLastPublishOperationAsync_ForABotWithNoFinishedPublish_ReturnsNull(string row)
    {
        _respond = _ => row;

        Assert.Null(await Client().GetLastPublishOperationAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(RowsWithAnUnreadableSynchronizationStatus))]
    public async Task GetLastPublishOperationAsync_WhenTheSynchronizationStatusCannotBeRead_FailsAtOnce(string row)
    {
        _respond = _ => row;

        await Assert.ThrowsAsync<FormatException>(() => Client().GetLastPublishOperationAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Single(_requests);
    }

    [Theory]
    [InlineData(ReportedSynchronizationStatus, "Provisioned")]
    [InlineData("""{"$kind":"BotSynchronizationDetails","contentVersion":2,"currentSynchronizationState":{"$kind":"SynchronizationState","provisioningStatus":"Provisioning","state":"Synchronizing"}}""", "Provisioning")]
    public async Task GetProvisioningStatusAsync_ReadsTheCurrentProvisioningStatusAndTheTemplateFromTheBotInOneRequest(string synchronizationStatus, string expected)
    {
        var agentId = Guid.NewGuid();
        _respond = _ => new JsonObject { ["synchronizationstatus"] = synchronizationStatus, ["template"] = "cliagent-1.0.0" }.ToJsonString();

        var status = await Client().GetProvisioningStatusAsync(agentId, CancellationToken.None);

        Assert.Equal(("GET", $"{Organization}/api/data/v9.2/bots({agentId})?$select=synchronizationstatus,template"), (Assert.Single(_requests).Method, _requests[0].Uri));
        Assert.Equal(new AgentProvisioningStatus(expected, "cliagent-1.0.0"), status);
    }

    [Theory]
    [MemberData(nameof(RowsOfBotsWithNoProvisioningStatus))]
    public async Task GetProvisioningStatusAsync_ForABotThatReportsNone_ReturnsNoStatusAndNoTemplate(string row)
    {
        _respond = _ => row;

        Assert.Equal(new AgentProvisioningStatus(null, null), await Client().GetProvisioningStatusAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(RowsWithNoJsonObjectAsTheirStatus))]
    public async Task GetProvisioningStatusAsync_WhenTheSynchronizationStatusIsNoJsonObject_FailsAtOnceNamingTheProvisioningStatus(string row)
    {
        _respond = _ => row;

        Assert.StartsWith("The bot's synchronization status cannot be read as Copilot Studio's provisioning status.", (await Assert.ThrowsAsync<FormatException>(() => Client().GetProvisioningStatusAsync(Guid.NewGuid(), CancellationToken.None))).Message, StringComparison.Ordinal);
        Assert.Single(_requests);
    }

    private static string Bot(string? synchronizationStatus) => new JsonObject { ["synchronizationstatus"] = synchronizationStatus }.ToJsonString();

    private static TheoryData<string> Rows(IEnumerable<string> rows)
    {
        var data = new TheoryData<string>();

        foreach (var row in rows)
        {
            data.Add(row);
        }

        return data;
    }

    private static DateTimeOffset? Time(string? text) => text is null ? null : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);

    private SyncDataverseClient Client()
    {
        var accessor = new Mock<IDataverseHttpClientAccessor>();
        accessor.Setup(httpClientAccessor => httpClientAccessor.CreateClient()).Returns(() => new HttpClient(new ConnectionReferenceChunkingTests.StubHandler(RespondAsync)));
        var client = new SyncDataverseClient(accessor.Object);
        client.SetDataverseUrl(Organization);

        return client;
    }

    private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());
        _requests.Add((request.Method.Method, uri, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));

        return request.Method == HttpMethod.Get ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_respond(uri), Encoding.UTF8, "application/json") } : new HttpResponseMessage(HttpStatusCode.NoContent);
    }
}
