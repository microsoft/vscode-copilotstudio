// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Globalization;
using System.Text.Json;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public sealed class DataverseCollectionPagerTests
{
    private const string FirstPage = "https://contoso.crm.dynamics.com/api/data/v9.2/bots?$select=botid";

    private const string OrganizationOrigin = "https://contoso.crm.dynamics.com";

    private static readonly Uri Organization = new("https://contoso.crm.dynamics.com/");

    private readonly List<string> _requests = new();

    private readonly List<CancellationToken> _cancellationTokens = new();

    private readonly List<JsonDocument> _pages = new();

    private Func<string?> _respond = () => throw new InvalidOperationException("No page was expected.");

    public static TheoryData<string?> MalformedPages => new() { null, "null", "[]", "5", "\"page\"", "{\"unexpected\":true}", "{\"value\":null}", "{\"value\":{}}", "{\"value\":\"rows\"}" };

    public static TheoryData<string> UnfollowableLinks => new() { "5", "\"\"", "null", "{}", "[]", "true", "\"https://\"", "\"https://contoso.crm.dynamics.com:99999/api/data/v9.2/bots?$skiptoken=p2\"" };

    [Fact]
    public async Task ReadAsync_AnEmptyCollection_ReturnsNothingAfterOneRequest()
    {
        Respond(Page(null));

        Assert.Empty(await ReadAsync());
        Assert.Equal(new[] { FirstPage }, _requests);
    }

    [Fact]
    public async Task ReadAsync_FollowsANextLinkOnTheSameOrigin()
    {
        Respond(Page(Link("p2"), "b1"), Page(null, "b2"));

        Assert.Equal(new[] { "b1", "b2" }, await ReadAsync());
        Assert.Equal(new[] { FirstPage, Link("p2") }, _requests);
    }

    [Fact]
    public async Task ReadAsync_FollowsDistinctNextLinksToTheEnd()
    {
        Respond(Page(Link("p2"), "b1", "b2"), Page(Link("p3"), "b3"), Page(null, "b4"));

        Assert.Equal(new[] { "b1", "b2", "b3", "b4" }, await ReadAsync());
        Assert.Equal(new[] { FirstPage, Link("p2"), Link("p3") }, _requests);
    }

    [Fact]
    public async Task ReadAsync_PassesEveryPageToTheReaderAsTheCallerOrTheServiceWroteIt()
    {
        Respond(Page("/api/data/v9.2/bots?$skiptoken=p2", "b1"), Page("bots?$skiptoken=p3", "b2"), Page(Link("p4"), "b3"), Page(null, "b4"));

        Assert.Equal(new[] { "b1", "b2", "b3", "b4" }, await ReadAsync(firstPage: "api/data/v9.2/bots?$select=botid"));
        Assert.Equal(new[] { "api/data/v9.2/bots?$select=botid", "/api/data/v9.2/bots?$skiptoken=p2", "bots?$skiptoken=p3", Link("p4") }, _requests);
    }

    [Theory]
    [InlineData("https://evil.example.com/api/data/v9.2/bots?$skiptoken=x", "https://evil.example.com")]
    [InlineData("http://contoso.crm.dynamics.com/api/data/v9.2/bots?$skiptoken=x", "http://contoso.crm.dynamics.com")]
    [InlineData("https://user:pass@contoso.crm.dynamics.com/api/data/v9.2/bots?$skiptoken=x", "https://user:pass@contoso.crm.dynamics.com")]
    [InlineData("https://contoso.crm.dynamics.com:8443/api/data/v9.2/bots?$skiptoken=x", "https://contoso.crm.dynamics.com:8443")]
    [InlineData("https://contoso.crm.dynamics.com.fabrikam.com/api/data/v9.2/bots?$skiptoken=x", "https://contoso.crm.dynamics.com.fabrikam.com")]
    [InlineData("//evil.example.com/api/data/v9.2/bots?$skiptoken=x", "https://evil.example.com")]
    public async Task ReadAsync_RefusesANextLinkThatLeavesTheOrganizationNamingBothOriginsWithoutFollowingIt(string nextLink, string linkOrigin)
    {
        Respond(Page(nextLink, "b1"));

        var failure = await Assert.ThrowsAsync<DataverseCollectionPageException>(() => ReadAsync());

        Assert.Equal(DataverseCollectionPageProblem.NextLinkLeavesOrganization, failure.Problem);
        Assert.Equal(nextLink, failure.Url);
        Assert.Equal(linkOrigin, failure.LinkOrigin);
        Assert.Equal(OrganizationOrigin, failure.OrganizationOrigin);
        Assert.Equal("The service asked for a page outside the environment being used. The request was not sent, because sending it would disclose this environment's access token.", failure.Message);
        Assert.Equal(new[] { FirstPage }, _requests);
    }

    [Theory]
    [InlineData("a,a")]
    [InlineData("a,b,a")]
    [InlineData("a,b,c,a")]
    public async Task ReadAsync_RefusesANextLinkItAlreadyFollowedWithoutReadingThePageAgain(string tokens)
    {
        var chain = tokens.Split(',');
        Respond(chain.Select(token => Page(Link(token), "b1")).ToArray());

        var failure = await Assert.ThrowsAsync<DataverseCollectionPageException>(() => ReadAsync());

        Assert.Equal(DataverseCollectionPageProblem.NextLinkRepeated, failure.Problem);
        Assert.Equal(Link(chain[chain.Length - 1]), failure.Url);
        Assert.Null(failure.LinkOrigin);
        Assert.Null(failure.OrganizationOrigin);
        Assert.Equal("The service returned a pagination link that had already been followed.", failure.Message);
        Assert.Equal(new[] { FirstPage }.Concat(chain.Take(chain.Length - 1).Select(Link)), _requests);
    }

    [Theory]
    [InlineData("https://CONTOSO.CRM.DYNAMICS.COM/api/data/v9.2/bots?$skiptoken=p2")]
    [InlineData("HTTPS://contoso.crm.dynamics.com/api/data/v9.2/bots?$skiptoken=p2")]
    [InlineData("https://contoso.crm.dynamics.com:443/api/data/v9.2/bots?$skiptoken=p2")]
    [InlineData("/api/data/v9.2/bots?$skiptoken=p2")]
    public async Task ReadAsync_RefusesAFollowedNextLinkSpelledAnotherWayAsACycle(string repeated)
    {
        Respond(Page(Link("p2"), "b1"), Page(repeated, "b2"), Page(null, "b3"));

        var failure = await Assert.ThrowsAsync<DataverseCollectionPageException>(() => ReadAsync());

        Assert.Equal(DataverseCollectionPageProblem.NextLinkRepeated, failure.Problem);
        Assert.Equal(repeated, failure.Url);
        Assert.Equal(new[] { FirstPage, Link("p2") }, _requests);
    }

    [Fact]
    public async Task ReadAsync_FollowsANextLinkThatDiffersFromAFollowedOneOnlyInTheCaseOfItsQuery()
    {
        Respond(Page(Link("AbC"), "b1"), Page(Link("aBc"), "b2"), Page(null, "b3"));

        Assert.Equal(new[] { "b1", "b2", "b3" }, await ReadAsync());
        Assert.Equal(new[] { FirstPage, Link("AbC"), Link("aBc") }, _requests);
    }

    [Fact]
    public async Task ReadAsync_FollowsANextLinkBackToTheFirstPageOnceOnly()
    {
        Respond(Page(FirstPage, "b1"), Page(FirstPage, "b2"));

        var failure = await Assert.ThrowsAsync<DataverseCollectionPageException>(() => ReadAsync());

        Assert.Equal(DataverseCollectionPageProblem.NextLinkRepeated, failure.Problem);
        Assert.Equal(new[] { FirstPage, FirstPage }, _requests);
    }

    [Fact]
    public async Task ReadAsync_WhenTheServiceNeverStopsPaging_FailsAfterTheMaximumPagesNamingTheFirstPage()
    {
        _respond = () => Page(Link(_requests.Count.ToString(CultureInfo.InvariantCulture)), "b1");

        var failure = await Assert.ThrowsAsync<DataverseCollectionPageException>(() => ReadAsync());

        Assert.Equal(DataverseCollectionPageProblem.TooManyPages, failure.Problem);
        Assert.Equal(FirstPage, failure.Url);
        Assert.Equal("The service returned more than 1000 pages, so the collection was not read to its end.", failure.Message);
        Assert.Equal(1000, DataverseCollectionPager.MaximumPages);
        Assert.Equal(DataverseCollectionPager.MaximumPages, _requests.Count);
    }

    [Fact]
    public async Task ReadAsync_ReadsACollectionOfExactlyTheMaximumPages()
    {
        _respond = () => Page(_requests.Count < DataverseCollectionPager.MaximumPages ? Link(_requests.Count.ToString(CultureInfo.InvariantCulture)) : null, "b1");

        Assert.Equal(DataverseCollectionPager.MaximumPages, (await ReadAsync()).Count);
        Assert.Equal(DataverseCollectionPager.MaximumPages, _requests.Count);
    }

    [Theory]
    [MemberData(nameof(MalformedPages))]
    public async Task ReadAsync_WhenAPageIsMissingOrHoldsNoValueArray_FailsNamingThatPageRatherThanReturningTheRowsBeforeIt(string? page)
    {
        Respond(Page(Link("p2"), "b1"), page);

        var failure = await Assert.ThrowsAsync<DataverseCollectionPageException>(() => ReadAsync());

        Assert.Equal(DataverseCollectionPageProblem.NotACollection, failure.Problem);
        Assert.Equal(Link("p2"), failure.Url);
        Assert.Equal("The service did not return a collection page carrying a 'value' array, so the read failed rather than returning only the rows before it.", failure.Message);
        Assert.Equal(new[] { FirstPage, Link("p2") }, _requests);
    }

    [Theory]
    [MemberData(nameof(UnfollowableLinks))]
    public async Task ReadAsync_WhenANextLinkIsNotAUrl_FailsNamingItRatherThanStoppingEarly(string nextLink)
    {
        Respond($"{{\"value\":[{{\"botid\":\"b1\"}}],\"@odata.nextLink\":{nextLink}}}");

        var failure = await Assert.ThrowsAsync<DataverseCollectionPageException>(() => ReadAsync());

        Assert.Equal(DataverseCollectionPageProblem.NextLinkNotAUrl, failure.Problem);
        Assert.Equal(nextLink, failure.Url);
        Assert.Equal("The service supplied a pagination link that is not a URL, so the collection cannot be read to its end.", failure.Message);
        Assert.Equal(new[] { FirstPage }, _requests);
    }

    [Fact]
    public async Task ReadAsync_WithAPositiveMaximumCount_ReturnsExactlyThatManyRowsWithoutFollowingFurther()
    {
        Respond(Page(Link("p2"), "b1", "b2"), Page(Link("p3"), "b3", "b4"), Page(null, "b5"));

        Assert.Equal(new[] { "b1", "b2", "b3" }, await ReadAsync(maximumCount: 3));
        Assert.Equal(new[] { FirstPage, Link("p2") }, _requests);
    }

    [Fact]
    public async Task ReadAsync_WhenAPageFillsTheMaximumCount_DoesNotFollowItsNextLink()
    {
        Respond(Page(Link("p2"), "b1", "b2"));

        Assert.Equal(new[] { "b1", "b2" }, await ReadAsync(maximumCount: 2));
        Assert.Equal(new[] { FirstPage }, _requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    public async Task ReadAsync_WhenTheMaximumCountIsNotPositiveOrNotReached_ReadsEveryRow(int? maximumCount)
    {
        Respond(Page(Link("p2"), "b1", "b2"), Page(null, "b3"));

        Assert.Equal(new[] { "b1", "b2", "b3" }, await ReadAsync(maximumCount));
        Assert.Equal(new[] { FirstPage, Link("p2") }, _requests);
    }

    [Fact]
    public async Task ReadAsync_DisposesEveryPageItReads()
    {
        Respond(Page(Link("p2"), "b1"), Page(Link("p3"), "b2"), Page(null, "b3"));

        await ReadAsync();

        Assert.Equal(3, _pages.Count);
        Assert.All(_pages, AssertDisposed);
    }

    [Fact]
    public async Task ReadAsync_WhenAPageFails_DisposesItAndThePagesBeforeIt()
    {
        Respond(Page(Link("p2"), "b1"), Page("https://evil.example.com/api/data/v9.2/bots", "b2"));

        await Assert.ThrowsAsync<DataverseCollectionPageException>(() => ReadAsync());

        Assert.Equal(2, _pages.Count);
        Assert.All(_pages, AssertDisposed);
    }

    [Fact]
    public async Task ReadAsync_PassesItsCancellationTokenToEveryPageRead()
    {
        using var cancellation = new CancellationTokenSource();
        Respond(Page(Link("p2"), "b1"), Page(null, "b2"));

        await ReadAsync(cancellationToken: cancellation.Token);

        Assert.Equal(new[] { cancellation.Token, cancellation.Token }, _cancellationTokens);
    }

    [Fact]
    public async Task ReadAsync_WhenThePageReaderIsCancelled_StopsWithItsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DataverseCollectionPager.ReadAsync(Organization, FirstPage, async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }, row => row.GetRawText(), null, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    [Fact]
    public async Task ReadAsync_WithAPageReaderThatCompletesLater_ReadsEveryPage()
    {
        var pages = new Queue<string>(new[] { Page(Link("p2"), "b1"), Page(null, "b2") });

        var rows = await DataverseCollectionPager.ReadAsync(Organization, FirstPage, async (_, _) =>
        {
            await Task.Yield();
            return JsonDocument.Parse(pages.Dequeue());
        }, row => row.GetProperty("botid").GetString(), null, CancellationToken.None);

        Assert.Equal(new[] { "b1", "b2" }, rows);
    }

    [Fact]
    public async Task ReadAsync_WithoutARequiredArgument_Throws()
    {
        Func<string, CancellationToken, Task<JsonDocument?>> readPage = (_, _) => Task.FromResult<JsonDocument?>(null);
        Func<JsonElement, string> readRow = row => row.GetRawText();

        Assert.Equal("organization", (await Assert.ThrowsAsync<ArgumentNullException>(() => DataverseCollectionPager.ReadAsync(null!, FirstPage, readPage, readRow, null, CancellationToken.None))).ParamName);
        Assert.Equal("firstPage", (await Assert.ThrowsAsync<ArgumentNullException>(() => DataverseCollectionPager.ReadAsync(Organization, null!, readPage, readRow, null, CancellationToken.None))).ParamName);
        Assert.Equal("readPage", (await Assert.ThrowsAsync<ArgumentNullException>(() => DataverseCollectionPager.ReadAsync(Organization, FirstPage, null!, readRow, null, CancellationToken.None))).ParamName);
        Assert.Equal("readRow", (await Assert.ThrowsAsync<ArgumentNullException>(() => DataverseCollectionPager.ReadAsync<string>(Organization, FirstPage, readPage, null!, null, CancellationToken.None))).ParamName);
    }

    internal static string Link(string token) => $"https://contoso.crm.dynamics.com/api/data/v9.2/bots?$skiptoken={token}";

    internal static string Page(string? nextLink, params string[] botIds) => $"{{\"value\":[{string.Join(",", botIds.Select(botId => $"{{\"botid\":\"{botId}\"}}"))}]{(nextLink is null ? string.Empty : $",\"@odata.nextLink\":{JsonSerializer.Serialize(nextLink)}")}}}";

    private static void AssertDisposed(JsonDocument page) => Assert.Throws<ObjectDisposedException>(() => page.RootElement.ValueKind);

    private void Respond(params string?[] pages) => _respond = new Queue<string?>(pages).Dequeue;

    private Task<IReadOnlyList<string?>> ReadAsync(int? maximumCount = null, string firstPage = FirstPage, CancellationToken cancellationToken = default) => DataverseCollectionPager.ReadAsync(Organization, firstPage, ReadPageAsync, row => row.GetProperty("botid").GetString(), maximumCount, cancellationToken);

    private Task<JsonDocument?> ReadPageAsync(string url, CancellationToken cancellationToken)
    {
        _requests.Add(url);
        _cancellationTokens.Add(cancellationToken);

        if (_respond() is not { } body)
        {
            return Task.FromResult<JsonDocument?>(null);
        }

        var page = JsonDocument.Parse(body);
        _pages.Add(page);

        return Task.FromResult<JsonDocument?>(page);
    }
}
