// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Thrown when a pull stops because the environment deleted or replaced knowledge files that were edited locally.</summary>
public sealed class DeletedKnowledgeFileEditsException : Exception
{
    /// <summary>Creates the failure naming the locally edited files and whether a pull can keep them all.</summary>
    public DeletedKnowledgeFileEditsException(IReadOnlyList<string> filePaths, bool canBeKept = true) : base(Describe(filePaths, canBeKept, static filePath => filePath))
    {
        FilePaths = filePaths;
        CanBeKept = canBeKept;
    }

    /// <summary>Workspace-relative paths of the locally edited files.</summary>
    public IReadOnlyList<string> FilePaths { get; }

    /// <summary>Whether a pull can keep every listed file as a new local file, rather than only delete them.</summary>
    public bool CanBeKept { get; }

    /// <summary>Builds the summary used for <see cref="Exception.Message"/>, formatting each path with <paramref name="formatFilePath"/>.</summary>
    public static string Describe(IReadOnlyList<string> filePaths, bool canBeKept, Func<string, string> formatFilePath) => $"{(filePaths.Count == 1 ? "A knowledge file you changed locally was" : $"{filePaths.Count} knowledge files you changed locally were")} {(canBeKept ? "deleted in the environment" : $"deleted or replaced in the environment and can't {(filePaths.Count == 1 ? string.Empty : "all ")}be kept")}: {string.Join(", ", filePaths.Select(formatFilePath))}";
}
