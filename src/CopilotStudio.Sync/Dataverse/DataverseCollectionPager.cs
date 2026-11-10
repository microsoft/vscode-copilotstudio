// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text.Json;

namespace Microsoft.CopilotStudio.Sync.Dataverse;

/// <summary>Reads a Dataverse collection to its end, following only next links within the organization and refusing any page or link that would cut the read short.</summary>
public static class DataverseCollectionPager
{
    /// <summary>The most pages one collection read requests.</summary>
    public const int MaximumPages = 1000;

    /// <summary>Reads the rows of a Dataverse collection page by page, following each page's next link while it stays within the organization.</summary>
    /// <typeparam name="T">The projected row type.</typeparam>
    /// <param name="organization">The Dataverse organization every next link has to stay within.</param>
    /// <param name="firstPage">The first page's URL, passed to the page reader as given.</param>
    /// <param name="readPage">Reads the page at a URL as the caller or the service wrote it, or returns <see langword="null"/> when there is none, leaving the page for the pager to dispose.</param>
    /// <param name="readRow">Projects one row, whose element is valid only until its page is disposed.</param>
    /// <param name="maximumCount">Stops once this many rows have been read when it is positive, and reads every row otherwise.</param>
    /// <param name="cancellationToken">Cancels the read, passed to every page read.</param>
    /// <returns>The projected rows in the order the pages hold them, exactly the maximum count when it is positive and the collection holds that many.</returns>
    /// <exception cref="DataverseCollectionPageException">A page or next link keeps the collection from being read to its end within the organization.</exception>
    public static async Task<IReadOnlyList<T>> ReadAsync<T>(Uri organization, string firstPage, Func<string, CancellationToken, Task<JsonDocument?>> readPage, Func<JsonElement, T> readRow, int? maximumCount, CancellationToken cancellationToken)
    {
        if (organization is null)
        {
            throw new ArgumentNullException(nameof(organization));
        }

        if (firstPage is null)
        {
            throw new ArgumentNullException(nameof(firstPage));
        }

        if (readPage is null)
        {
            throw new ArgumentNullException(nameof(readPage));
        }

        if (readRow is null)
        {
            throw new ArgumentNullException(nameof(readRow));
        }

        var rows = new List<T>();
        var followedPages = new HashSet<string>(StringComparer.Ordinal);
        string? pageUrl = firstPage;

        for (var pageCount = 0; pageUrl is not null; pageCount++)
        {
            if (pageCount >= MaximumPages)
            {
                throw new DataverseCollectionPageException(DataverseCollectionPageProblem.TooManyPages, firstPage);
            }

            using var page = await readPage(pageUrl, cancellationToken).ConfigureAwait(false);

            if (page is null || page.RootElement.ValueKind != JsonValueKind.Object || !page.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
            {
                throw new DataverseCollectionPageException(DataverseCollectionPageProblem.NotACollection, pageUrl);
            }

            rows.AddRange(values.EnumerateArray().Select(readRow));

            if (maximumCount > 0 && rows.Count >= maximumCount)
            {
                rows.RemoveRange(maximumCount.Value, rows.Count - maximumCount.Value);
                return rows;
            }

            pageUrl = NextPageUrl(organization, page.RootElement, followedPages);
        }

        return rows;
    }

    private static string? NextPageUrl(Uri organization, JsonElement page, HashSet<string> followedPages)
    {
        if (!page.TryGetProperty("@odata.nextLink", out var link))
        {
            return null;
        }

        if (link.ValueKind != JsonValueKind.String || link.GetString() is not { Length: > 0 } nextPageUrl || !Uri.TryCreate(organization, nextPageUrl, out var nextPage))
        {
            throw new DataverseCollectionPageException(DataverseCollectionPageProblem.NextLinkNotAUrl, link.GetRawText());
        }

        if (!OriginGuardedDataverseHttpClientAccessor.IsSameOrigin(nextPage, organization))
        {
            throw new DataverseCollectionPageException(DataverseCollectionPageProblem.NextLinkLeavesOrganization, nextPageUrl, nextPage.GetLeftPart(UriPartial.Authority), organization.GetLeftPart(UriPartial.Authority));
        }

        return followedPages.Add(nextPage.PathAndQuery) ? nextPageUrl : throw new DataverseCollectionPageException(DataverseCollectionPageProblem.NextLinkRepeated, nextPageUrl);
    }
}
