// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.UnitTests.DataverseCollectionPagerTests;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public sealed class AgentChangeProbeDataverseClientTests
{
    private const string Organization = "https://contoso.crm.dynamics.com";

    private const string Api = Organization + "/api/data/v9.2/";

    private const string SchemaName = "cr_helpdesk";

    private const string ConnectorPath = "/providers/Microsoft.PowerApps/apis/";

    private const string ComponentExpansions = "&$expand=botcomponent_connectionreference($select=connectionreferenceid,connectorid,modifiedon),botcomponent_environmentvariabledefinition($select=environmentvariabledefinitionid,modifiedon)";

    private static readonly Guid BotId = new("6a0f6f3e-2b1c-4d8e-9a7b-1c2d3e4f5a6b");

    private static readonly Guid CollectionId = new("0b7e2d5c-8f1a-4c3b-9e6d-2a4b6c8d0e1f");

    private static readonly Guid PromptId = new("c2d4e6f8-1a3b-4c5d-8e7f-9a0b1c2d3e4f");

    private static readonly Guid TestCasePromptId = new("f1e2d3c4-b5a6-4978-8a9b-0c1d2e3f4a5b");

    private static readonly Guid CustomReferenceId = new("1d3f5b7a-9c2e-4a6b-8d0f-2e4a6c8b0d1f");

    private static readonly Guid StandardReferenceId = new("2e4a6c8b-0d1f-4b7a-9c3e-5f7b9d1a3c5e");

    private static readonly Guid VariableId = new("3f5b7d9a-1c3e-4d6f-8a0b-6c8e0a2c4e6a");

    private static readonly Guid SecondVariableId = new("4a6c8e0b-2d4f-4e8a-9b1c-7d9f1b3d5f7b");

    private readonly List<string> _requests = new();

    private readonly JsonObject _bot = new() { ["botid"] = BotId, ["modifiedon"] = At(1, 9), ["bot_botcomponentcollection"] = new JsonArray(new JsonObject { ["botcomponentcollectionid"] = CollectionId, ["modifiedon"] = At(1, 8) }) };

    private readonly JsonArray _components = new(
        Component(0, At(2, 10), "kind: AdaptiveDialog", new[] { Reference(CustomReferenceId, ConnectorPath + "shared_cr123-5fcontoso-5f1234567890abcdef", At(5, 10)) }, new[] { Variable(VariableId, At(7, 10)), Variable(SecondVariableId, At(7, 9)) }),
        Component(0, At(2, 9), $"kind: TaskDialog\naction:\n  kind: InvokeAIPromptTaskAction\n  aIModelId: {PromptId}", new[] { Reference(CustomReferenceId, ConnectorPath + "shared_cr123-5fcontoso-5f1234567890abcdef", At(5, 10)), Reference(StandardReferenceId, ConnectorPath + "shared_office365", At(5, 9)) }, new[] { Variable(VariableId, At(7, 10)) }),
        Component(0, At(2, 7), "kind: AdaptiveDialog"),
        Component(19, At(2, 8), $"aIModelId: {TestCasePromptId}"));

    private readonly JsonArray _flows = new(new JsonObject { ["workflowid"] = Guid.NewGuid(), ["modifiedon"] = At(3, 10) }, new JsonObject { ["workflowid"] = Guid.NewGuid(), ["modifiedon"] = At(3, 9) });

    private readonly JsonArray _prompts = new(new JsonObject { ["msdyn_aimodelid"] = PromptId, ["modifiedon"] = At(4, 10), ["msdyn_aimodel_msdyn_aiconfiguration"] = new JsonArray(new JsonObject { ["msdyn_aiconfigurationid"] = Guid.NewGuid(), ["modifiedon"] = At(4, 11) }, new JsonObject { ["msdyn_aiconfigurationid"] = Guid.NewGuid(), ["modifiedon"] = At(4, 9) }) });

    private readonly JsonArray _connectors = new(new JsonObject { ["connectorid"] = Guid.NewGuid(), ["modifiedon"] = At(6, 10) });

    private readonly JsonArray _values = new(new JsonObject { ["environmentvariablevalueid"] = Guid.NewGuid(), ["modifiedon"] = At(8, 10) }, new JsonObject { ["environmentvariablevalueid"] = Guid.NewGuid(), ["modifiedon"] = At(8, 9) });

    private readonly SyncDataverseClient _client;

    private bool _botExists = true;

    private Func<int, string>? _botPage;

    private int _rowsPerPage = 100;

    private bool _chunked;

    public AgentChangeProbeDataverseClientTests()
    {
        var accessor = new Mock<IDataverseHttpClientAccessor>();
        accessor.Setup(httpClientAccessor => httpClientAccessor.CreateClient()).Returns(() => new HttpClient(new ConnectionReferenceChunkingTests.StubHandler((request, _) => Task.FromResult(Respond(request)))));
        _client = new SyncDataverseClient(accessor.Object, "CopilotStudio.Sync.UnitTests", new DataverseRetryPolicy { Delay = (_, _) => Task.CompletedTask });
        _client.SetDataverseUrl(Organization);
    }

    [Fact]
    public async Task ProbeAgentAsync_ReadsTheBotAndCollectionsThenComponentsWithTheirReferencesAndVariablesThenFlowsPromptsConnectorsAndValues_InOneRequestEach()
    {
        var probe = await _client.ProbeAgentAsync(BotId, SchemaName, CancellationToken.None);

        Assert.Equal(new AgentChangeProbe(new(1, At(1, 9)), new(1, At(1, 8)), new(4, At(2, 10)), new(2, At(3, 10)), new(1, At(4, 10)), new(2, At(4, 11)), new(2, At(5, 10)), new(1, At(6, 10)), new(2, At(7, 10)), new(2, At(8, 10))), probe);
        Assert.Equal(new[]
        {
            $"{Api}bots?$select=botid,modifiedon&$filter=botid eq {BotId}&$expand=bot_botcomponentcollection($select=botcomponentcollectionid,modifiedon)",
            $"{Api}botcomponents?$select=botcomponentid,componenttype,data,modifiedon&$filter=(_parentbotid_value eq {BotId} or _parentbotcomponentcollectionid_value eq {CollectionId}){ComponentExpansions}",
            $"{Api}workflows?$select=workflowid,modifiedon&$filter=botcomponent_workflow/any(component:(component/_parentbotid_value eq {BotId} or component/_parentbotcomponentcollectionid_value eq {CollectionId}) and component/componenttype ne 19)",
            $"{Api}msdyn_aimodels?$select=msdyn_aimodelid,modifiedon&$filter=(msdyn_aimodelid eq {PromptId})&$expand=msdyn_aimodel_msdyn_aiconfiguration($select=msdyn_aiconfigurationid,modifiedon)",
            $"{Api}connectors?$select=connectorid,modifiedon&$filter=(connectorinternalid eq 'shared_cr123-5fcontoso-5f1234567890abcdef' or connectorinternalid eq 'shared_office365') and ismanaged eq false",
            $"{Api}environmentvariablevalues?$select=environmentvariablevalueid,modifiedon&$filter=(_environmentvariabledefinitionid_value eq {VariableId} or _environmentvariabledefinitionid_value eq {SecondVariableId})",
        }, _requests);
    }

    [Theory]
    [InlineData(nameof(AgentChangeProbe.Bot))]
    [InlineData(nameof(AgentChangeProbe.ComponentCollections))]
    [InlineData(nameof(AgentChangeProbe.Components))]
    [InlineData(nameof(AgentChangeProbe.Flows))]
    [InlineData(nameof(AgentChangeProbe.Prompts))]
    [InlineData(nameof(AgentChangeProbe.PromptConfigurations))]
    [InlineData(nameof(AgentChangeProbe.ConnectionReferences))]
    [InlineData(nameof(AgentChangeProbe.CustomConnectors))]
    [InlineData(nameof(AgentChangeProbe.EnvironmentVariables))]
    [InlineData(nameof(AgentChangeProbe.EnvironmentVariableValues))]
    public async Task ProbeAgentAsync_AfterARowOfOneTableIsModified_ChangesOnlyThatTablesValues(string table)
    {
        var before = await _client.ProbeAgentAsync(BotId, SchemaName, CancellationToken.None);
        var row = table switch
        {
            nameof(AgentChangeProbe.Bot) => _bot,
            nameof(AgentChangeProbe.ComponentCollections) => _bot["bot_botcomponentcollection"]![0]!,
            nameof(AgentChangeProbe.Components) => _components[2]!,
            nameof(AgentChangeProbe.Flows) => _flows[1]!,
            nameof(AgentChangeProbe.Prompts) => _prompts[0]!,
            nameof(AgentChangeProbe.ConnectionReferences) => _components[1]!["botcomponent_connectionreference"]![1]!,
            nameof(AgentChangeProbe.CustomConnectors) => _connectors[0]!,
            nameof(AgentChangeProbe.EnvironmentVariables) => _components[0]!["botcomponent_environmentvariabledefinition"]![1]!,
            nameof(AgentChangeProbe.EnvironmentVariableValues) => _values[1]!,
            _ => _prompts[0]!["msdyn_aimodel_msdyn_aiconfiguration"]![1]!,
        };
        row["modifiedon"] = At(30, 12);

        var after = await _client.ProbeAgentAsync(BotId, SchemaName, CancellationToken.None);

        Assert.Equal(new[] { table }, ChangedTables(before!, after!));
    }

    [Theory]
    [InlineData(nameof(AgentChangeProbe.ConnectionReferences), true)]
    [InlineData(nameof(AgentChangeProbe.ConnectionReferences), false)]
    [InlineData(nameof(AgentChangeProbe.CustomConnectors), true)]
    [InlineData(nameof(AgentChangeProbe.CustomConnectors), false)]
    [InlineData(nameof(AgentChangeProbe.EnvironmentVariables), true)]
    [InlineData(nameof(AgentChangeProbe.EnvironmentVariables), false)]
    [InlineData(nameof(AgentChangeProbe.EnvironmentVariableValues), true)]
    [InlineData(nameof(AgentChangeProbe.EnvironmentVariableValues), false)]
    public async Task ProbeAgentAsync_AfterARowOfOneTableIsAddedOrRemoved_ChangesOnlyThatTablesValuesThoughNoRowWasModified(string table, bool added)
    {
        var before = await _client.ProbeAgentAsync(BotId, SchemaName, CancellationToken.None);
        var (rows, keyColumn) = table switch
        {
            nameof(AgentChangeProbe.ConnectionReferences) => (_components[1]!["botcomponent_connectionreference"]!.AsArray(), "connectionreferenceid"),
            nameof(AgentChangeProbe.CustomConnectors) => (_connectors, "connectorid"),
            nameof(AgentChangeProbe.EnvironmentVariables) => (_components[0]!["botcomponent_environmentvariabledefinition"]!.AsArray(), "environmentvariabledefinitionid"),
            _ => (_values, "environmentvariablevalueid"),
        };

        if (added)
        {
            rows.Add(new JsonObject { [keyColumn] = Guid.NewGuid(), ["modifiedon"] = At(1, 1) });
        }
        else
        {
            rows.RemoveAt(rows.Count - 1);
        }

        Assert.Equal(new[] { table }, ChangedTables(before!, (await _client.ProbeAgentAsync(BotId, SchemaName, CancellationToken.None))!));
    }

    [Fact]
    public async Task ProbeAgentAsync_ReadsTheCustomConnectorsWithTheFilterTheCloneReadsTheirVersionsWith()
    {
        var connectorIds = new[] { ConnectorPath + "shared_cr123-5fcontoso", ConnectorPath + "SHARED_CR123-5FCONTOSO ", ConnectorPath + "shared_o'neil", ConnectorPath, "shared_bare" };
        _components.Clear();
        _components.Add(Component(0, At(2, 10), "kind: AdaptiveDialog", connectorIds.Select(connectorId => Reference(Guid.NewGuid(), connectorId, At(5, 1))).ToArray()));
        await _client.ProbeAgentAsync(BotId, SchemaName, CancellationToken.None);
        var probed = _requests.Single(request => TableOf(request) == "connectors");
        _requests.Clear();

        await _client.GetConnectorVersionsByInternalIdsAsync(connectorIds.Select(SyncDataverseClient.ExtractConnectorInternalId).OfType<string>(), isManaged: false, CancellationToken.None);

        Assert.Equal(FilterOf(Assert.Single(_requests)), FilterOf(probed));
        Assert.Equal("(connectorinternalid eq 'shared_cr123-5fcontoso' or connectorinternalid eq 'shared_o''neil' or connectorinternalid eq 'shared_bare') and ismanaged eq false", FilterOf(probed));
    }

    [Fact]
    public async Task ProbeAgentAsync_OfReferencesNamingMoreConnectorsThanOneFilterHolds_SplitsTheConnectorsReadAndNamesEachConnectorOnce()
    {
        _components[2]!["botcomponent_connectionreference"] = new JsonArray(Enumerable.Range(0, 60).Select(index => (JsonNode?)Reference(Guid.NewGuid(), $"{ConnectorPath}shared_connector{index}", At(5, 1))).ToArray());

        Assert.Equal(62, (await _client.ProbeAgentAsync(BotId, SchemaName, CancellationToken.None))!.ConnectionReferences.RowCount);
        Assert.Equal(new[] { 50, 12 }, _requests.Where(request => TableOf(request) == "connectors").Select(request => request.Split("connectorinternalid eq ").Length - 1));
    }

    [Fact]
    public async Task ProbeAgentAsync_OfComponentsUsingMoreVariablesThanOneFilterHolds_SplitsTheValuesReadAndNamesEachVariableOnce()
    {
        _components[2]!["botcomponent_environmentvariabledefinition"] = new JsonArray(Enumerable.Range(0, 60).Select(_ => (JsonNode?)Variable(Guid.NewGuid(), At(7, 1))).ToArray());

        Assert.Equal(62, (await _client.ProbeAgentAsync(BotId, SchemaName, CancellationToken.None))!.EnvironmentVariables.RowCount);
        Assert.Equal(new[] { 50, 12 }, _requests.Where(request => TableOf(request) == "environmentvariablevalues").Select(request => request.Split("_environmentvariabledefinitionid_value eq ").Length - 1));
    }

    [Fact]
    public async Task ProbeAgentAsync_WhenNoComponentUsesAReferenceOrAVariable_ReadsNeitherConnectorsNorVariableValues()
    {
        foreach (var component in _components)
        {
            component!.AsObject().Remove("botcomponent_connectionreference");
            component.AsObject().Remove("botcomponent_environmentvariabledefinition");
        }

        var probe = await _client.ProbeAgentAsync(BotId, SchemaName, CancellationToken.None);

        Assert.Equal(new[] { "bots", "botcomponents", "workflows", "msdyn_aimodels" }, _requests.Select(TableOf));
        Assert.Equal(Enumerable.Repeat(new AgentTableProbe(0, null), 4), new[] { probe!.ConnectionReferences, probe.CustomConnectors, probe.EnvironmentVariables, probe.EnvironmentVariableValues });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(ConnectorPath)]
    public async Task ProbeAgentAsync_WhenNoReferenceNamesAConnector_CountsTheReferencesAndReadsNoConnectors(string? connectorId)
    {
        foreach (var component in _components.Take(2))
        {
            foreach (var reference in component!["botcomponent_connectionreference"]!.AsArray())
            {
                reference!["connectorid"] = connectorId;
            }
        }

        var probe = await _client.ProbeAgentAsync(BotId, SchemaName, CancellationToken.None);

        Assert.Equal(new[] { "bots", "botcomponents", "workflows", "msdyn_aimodels", "environmentvariablevalues" }, _requests.Select(TableOf));
        Assert.Equal((new AgentTableProbe(2, At(5, 10)), new AgentTableProbe(0, null)), (probe!.ConnectionReferences, probe.CustomConnectors));
    }

    [Fact]
    public async Task ProbeAgentAsync_AfterAComponentIsDeleted_ChangesTheComponentsValuesThoughNoRowWasModified()
    {
        var before = await _client.ProbeAgentAsync(BotId, SchemaName, CancellationToken.None);
        _components.RemoveAt(2);

        var after = await _client.ProbeAgentAsync(BotId, SchemaName, CancellationToken.None);

        Assert.Equal(new[] { nameof(AgentChangeProbe.Components) }, ChangedTables(before!, after!));
        Assert.Equal(new AgentTableProbe(3, At(2, 10)), after!.Components);
    }

    [Fact]
    public async Task ProbeAgentAsync_OfABotTheEnvironmentDoesNotHold_ReturnsNullAfterReadingOnlyTheBot()
    {
        _botExists = false;

        Assert.Null(await _client.ProbeAgentAsync(BotId, SchemaName, CancellationToken.None));
        Assert.Equal(new[] { "bots" }, _requests.Select(TableOf));
    }

    [Fact]
    public async Task ProbeAgentAsync_WithoutAnIdentifier_FindsTheBotByItsSchemaName()
    {
        await _client.ProbeAgentAsync(null, "cr_o'helpdesk", CancellationToken.None);

        Assert.Equal($"{Api}bots?$select=botid,modifiedon&$filter=schemaname eq 'cr_o''helpdesk'&$expand=bot_botcomponentcollection($select=botcomponentcollectionid,modifiedon)", _requests[0]);
        Assert.Contains($"_parentbotid_value eq {BotId}", _requests[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeAgentAsync_WhenOnlyATestCaseNamesAPrompt_ReadsNoPrompts()
    {
        _components.RemoveAt(1);

        var probe = await _client.ProbeAgentAsync(BotId, SchemaName, CancellationToken.None);

        Assert.Equal((new AgentTableProbe(0, null), new AgentTableProbe(0, null)), (probe!.Prompts, probe.PromptConfigurations));
        Assert.Equal(new[] { "bots", "botcomponents", "workflows", "connectors", "environmentvariablevalues" }, _requests.Select(TableOf));
    }

    [Fact]
    public async Task ProbeAgentAsync_OfABotLinkedToMoreCollectionsThanOneFilterHolds_SplitsTheOwnersAndCountsEachRowOnce()
    {
        _bot["bot_botcomponentcollection"] = new JsonArray(Enumerable.Range(0, 60).Select(_ => (JsonNode?)new JsonObject { ["botcomponentcollectionid"] = Guid.NewGuid(), ["modifiedon"] = At(1, 8) }).ToArray());

        var probe = await _client.ProbeAgentAsync(BotId, SchemaName, CancellationToken.None);

        Assert.Equal(new[] { "bots", "botcomponents", "botcomponents", "workflows", "workflows", "msdyn_aimodels", "connectors", "environmentvariablevalues" }, _requests.Select(TableOf));
        Assert.Equal(new[] { 50, 11 }, _requests.Where(request => TableOf(request) == "botcomponents").Select(request => request.Split("_value eq ").Length - 1));
        Assert.Equal((60, 4, 2, 2, 2), (probe!.ComponentCollections.RowCount, probe.Components.RowCount, probe.Flows.RowCount, probe.ConnectionReferences.RowCount, probe.EnvironmentVariables.RowCount));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("{}")]
    public async Task ProbeAgentAsync_WhenTheFirstBotRowIsNoObjectOrNamesNoBot_ReturnsNullAfterReadingOnlyTheBot(string row)
    {
        _botPage = _ => $"{{\"value\":[{row}]}}";

        Assert.Null(await _client.ProbeAgentAsync(BotId, SchemaName, CancellationToken.None));
        Assert.Equal(new[] { "bots" }, _requests.Select(TableOf));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task ProbeAgentAsync_OfABotWithoutALinkedCollectionArray_ReadsTheComponentsOfTheBotAlone(string? linkedCollections)
    {
        if (linkedCollections is null)
        {
            _bot.Remove("bot_botcomponentcollection");
        }
        else
        {
            _bot["bot_botcomponentcollection"] = JsonNode.Parse(linkedCollections);
        }

        var probe = await _client.ProbeAgentAsync(BotId, SchemaName, CancellationToken.None);

        Assert.Equal(new AgentTableProbe(0, null), probe!.ComponentCollections);
        Assert.Equal($"{Api}botcomponents?$select=botcomponentid,componenttype,data,modifiedon&$filter=(_parentbotid_value eq {BotId}){ComponentExpansions}", _requests[1]);
    }

    [Fact]
    public async Task ProbeAgentAsync_OfComponentsWithoutData_CountsThemAndReadsNoPrompts()
    {
        foreach (var component in _components)
        {
            component!.AsObject().Remove("data");
        }

        var probe = await _client.ProbeAgentAsync(BotId, SchemaName, CancellationToken.None);

        Assert.Equal((new AgentTableProbe(4, At(2, 10)), new AgentTableProbe(0, null)), (probe!.Components, probe.Prompts));
        Assert.Equal(new[] { "bots", "botcomponents", "workflows", "connectors", "environmentvariablevalues" }, _requests.Select(TableOf));
    }

    [Fact]
    public async Task ProbeAgentAsync_WhenEveryTableArrivesOneRowPerPageInChunks_ProbesWhatOnePagePerTableProbes()
    {
        var whole = await _client.ProbeAgentAsync(BotId, SchemaName, CancellationToken.None);
        _requests.Clear();
        (_rowsPerPage, _chunked) = (1, true);

        Assert.Equal(whole, await _client.ProbeAgentAsync(BotId, SchemaName, CancellationToken.None));
        Assert.Equal(new[] { "bots", "botcomponents", "botcomponents", "botcomponents", "botcomponents", "workflows", "workflows", "msdyn_aimodels", "connectors", "environmentvariablevalues", "environmentvariablevalues" }, _requests.Select(TableOf));
    }

    [Theory]
    [InlineData("TooManyPages", 1000, "The service returned more than 1000 pages, so the collection was not read to its end.")]
    [InlineData("NoValueArray", 2, "The service did not return a collection page carrying a 'value' array, so the read failed rather than returning only the rows before it.")]
    [InlineData("EmptyBody", 1, "The service did not return a collection page carrying a 'value' array, so the read failed rather than returning only the rows before it.")]
    [InlineData("NextLinkNotAUrl", 1, "The service supplied a pagination link that is not a URL, so the collection cannot be read to its end.")]
    [InlineData("NextLinkToAnotherOrganization", 1, "The service asked for a page outside the environment being used. The request was not sent, because sending it would disclose this environment's access token.")]
    [InlineData("RepeatedNextLink", 2, "The service returned a pagination link that had already been followed.")]
    public async Task ProbeAgentAsync_WhenTheBotsCannotBeReadToTheEndOfTheirCollection_FailsAsABadGatewaySayingWhy(string problem, int requests, string message)
    {
        _botPage = problem switch
        {
            "TooManyPages" => request => Page(Link(request.ToString(CultureInfo.InvariantCulture))),
            "NoValueArray" => request => request == 0 ? Page(Link("a")) : "{\"unexpected\":true}",
            "EmptyBody" => _ => string.Empty,
            "NextLinkNotAUrl" => _ => "{\"value\":[],\"@odata.nextLink\":5}",
            "NextLinkToAnotherOrganization" => _ => Page("https://fabrikam.crm.dynamics.com/api/data/v9.2/bots?$skiptoken=a"),
            _ => _ => Page(Link("a")),
        };

        var failure = await Assert.ThrowsAsync<DataverseRequestException>(() => _client.ProbeAgentAsync(BotId, SchemaName, CancellationToken.None));

        Assert.Equal((HttpStatusCode.BadGateway, message), (failure.StatusCode, failure.ResponseBody));
        Assert.Equal(requests, _requests.Count);
    }

    [Fact]
    public async Task ProbeAgentAsync_WhenTheOrganizationIsNotAUrl_FailsAsABadGatewayBeforeAnyRequest()
    {
        _client.SetDataverseUrl("contoso");

        var failure = await Assert.ThrowsAsync<DataverseRequestException>(() => _client.ProbeAgentAsync(BotId, SchemaName, CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadGateway, failure.StatusCode);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task DownloadAllAIPromptsForAgentAsync_LeavesOutTestCasesAndReadsThePromptsTheProbeReads()
    {
        _components.RemoveAt(3);
        var probe = await _client.ProbeAgentAsync(BotId, SchemaName, CancellationToken.None);
        _requests.Clear();

        var prompts = await _client.DownloadAllAIPromptsForAgentAsync(new AgentSyncInfo { AgentId = BotId }, CancellationToken.None);

        Assert.Equal($"{Api}botcomponents?$select=botcomponentid,data&$filter=_parentbotid_value eq {BotId} and componenttype ne 19", _requests[0]);
        Assert.Equal(new[] { PromptId }, prompts.Select(prompt => prompt.AIModelId));
        Assert.Equal(probe!.Prompts.RowCount, prompts.Length);
    }

    private static DateTimeOffset At(int day, int hour) => new(2026, 11, day, hour, 0, 0, TimeSpan.Zero);

    private static JsonObject Component(int componentType, DateTimeOffset modifiedOn, string data, JsonObject[]? references = null, JsonObject[]? variables = null)
    {
        var component = new JsonObject { ["botcomponentid"] = Guid.NewGuid(), ["componenttype"] = componentType, ["data"] = data, ["modifiedon"] = modifiedOn };

        if (references is not null)
        {
            component["botcomponent_connectionreference"] = new JsonArray(references.Select(reference => (JsonNode?)reference).ToArray());
        }

        if (variables is not null)
        {
            component["botcomponent_environmentvariabledefinition"] = new JsonArray(variables.Select(variable => (JsonNode?)variable).ToArray());
        }

        return component;
    }

    private static JsonObject Reference(Guid referenceId, string? connectorId, DateTimeOffset modifiedOn) => new() { ["connectionreferenceid"] = referenceId, ["connectorid"] = connectorId, ["modifiedon"] = modifiedOn };

    private static JsonObject Variable(Guid definitionId, DateTimeOffset modifiedOn) => new() { ["environmentvariabledefinitionid"] = definitionId, ["modifiedon"] = modifiedOn };

    private static string TableOf(string request) => request[Api.Length..request.IndexOf('?', StringComparison.Ordinal)];

    private static string FilterOf(string request) => request[(request.IndexOf("$filter=", StringComparison.Ordinal) + "$filter=".Length)..];

    private static string[] ChangedTables(AgentChangeProbe before, AgentChangeProbe after) => new (string Table, AgentTableProbe Before, AgentTableProbe After)[]
    {
        (nameof(AgentChangeProbe.Bot), before.Bot, after.Bot),
        (nameof(AgentChangeProbe.ComponentCollections), before.ComponentCollections, after.ComponentCollections),
        (nameof(AgentChangeProbe.Components), before.Components, after.Components),
        (nameof(AgentChangeProbe.Flows), before.Flows, after.Flows),
        (nameof(AgentChangeProbe.Prompts), before.Prompts, after.Prompts),
        (nameof(AgentChangeProbe.PromptConfigurations), before.PromptConfigurations, after.PromptConfigurations),
        (nameof(AgentChangeProbe.ConnectionReferences), before.ConnectionReferences, after.ConnectionReferences),
        (nameof(AgentChangeProbe.CustomConnectors), before.CustomConnectors, after.CustomConnectors),
        (nameof(AgentChangeProbe.EnvironmentVariables), before.EnvironmentVariables, after.EnvironmentVariables),
        (nameof(AgentChangeProbe.EnvironmentVariableValues), before.EnvironmentVariableValues, after.EnvironmentVariableValues),
    }.Where(table => table.Before != table.After).Select(table => table.Table).ToArray();

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var url = Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri);
        _requests.Add(url);

        if (_botPage is not null && TableOf(url) == "bots")
        {
            return Answer(_botPage(_requests.Count - 1));
        }

        var rows = TableOf(url) switch
        {
            "bots" => _botExists ? new JsonArray(_bot.DeepClone()) : new JsonArray(),
            "botcomponents" => _components,
            "workflows" => _flows,
            "msdyn_aimodels" => _prompts,
            "connectors" => _connectors,
            "environmentvariablevalues" => _values,
            var table => throw new InvalidOperationException($"No read of {table} was expected."),
        };
        var first = url.Contains("$skiptoken=", StringComparison.Ordinal) ? int.Parse(url.Split("$skiptoken=")[1], CultureInfo.InvariantCulture) : 0;
        var page = new JsonObject { ["value"] = new JsonArray(rows.Skip(first).Take(_rowsPerPage).Select(row => row!.DeepClone()).ToArray()) };

        if (first + _rowsPerPage < rows.Count)
        {
            page["@odata.nextLink"] = $"{Api}{TableOf(url)}?$skiptoken={first + _rowsPerPage}";
        }

        return Answer(page.ToJsonString());
    }

    private HttpResponseMessage Answer(string body) => new(HttpStatusCode.OK) { Content = _chunked ? new StreamContent(new KnowledgeFileStreamingTransferTests.ChunkedStream(Encoding.UTF8.GetBytes(body), 7, null)) : new StringContent(body, Encoding.UTF8, "application/json") };
}
