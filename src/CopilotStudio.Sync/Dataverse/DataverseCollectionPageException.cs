// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync.Dataverse;

/// <summary>Why a Dataverse collection could not be read to its end within its organization.</summary>
public enum DataverseCollectionPageProblem
{
    /// <summary>The collection kept paging past <see cref="DataverseCollectionPager.MaximumPages"/> pages.</summary>
    TooManyPages = 0,

    /// <summary>A page was missing, or was not a JSON object holding a <c>value</c> array.</summary>
    NotACollection = 1,

    /// <summary>A page carried a next link that is not a non-empty string naming a URL.</summary>
    NextLinkNotAUrl = 2,

    /// <summary>A page carried a next link to a page outside the organization.</summary>
    NextLinkLeavesOrganization = 3,

    /// <summary>A page carried a next link to a page the read had already followed.</summary>
    NextLinkRepeated = 4,
}

/// <summary>Raised when a Dataverse collection cannot be read to its end within its organization, naming the page or next link at fault.</summary>
public sealed class DataverseCollectionPageException : Exception
{
    internal const string PageOutsideOrganizationMessage = "The service asked for a page outside the environment being used. The request was not sent, because sending it would disclose this environment's access token.";

    internal DataverseCollectionPageException(DataverseCollectionPageProblem problem, string url, string? linkOrigin = null, string? organizationOrigin = null)
        : base(MessageOf(problem))
    {
        Problem = problem;
        Url = url;
        LinkOrigin = linkOrigin;
        OrganizationOrigin = organizationOrigin;
    }

    /// <summary>Why the collection could not be read to its end.</summary>
    public DataverseCollectionPageProblem Problem { get; }

    /// <summary>The URL at fault: the first page when there are too many pages, the page read when it is not a collection, and otherwise the next link as the page carries it, as JSON text when it is not a URL.</summary>
    public string Url { get; }

    /// <summary>The origin of a next link that leaves the organization, or <see langword="null"/> for any other problem.</summary>
    public string? LinkOrigin { get; }

    /// <summary>The organization's origin when a next link leaves it, or <see langword="null"/> for any other problem.</summary>
    public string? OrganizationOrigin { get; }

    private static string MessageOf(DataverseCollectionPageProblem problem) => problem switch
    {
        DataverseCollectionPageProblem.TooManyPages => $"The service returned more than {DataverseCollectionPager.MaximumPages} pages, so the collection was not read to its end.",
        DataverseCollectionPageProblem.NotACollection => "The service did not return a collection page carrying a 'value' array, so the read failed rather than returning only the rows before it.",
        DataverseCollectionPageProblem.NextLinkNotAUrl => "The service supplied a pagination link that is not a URL, so the collection cannot be read to its end.",
        DataverseCollectionPageProblem.NextLinkLeavesOrganization => PageOutsideOrganizationMessage,
        _ => "The service returned a pagination link that had already been followed.",
    };
}
