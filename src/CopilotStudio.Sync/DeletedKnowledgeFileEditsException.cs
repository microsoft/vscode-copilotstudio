// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Thrown when a pull stops because the environment deleted knowledge files that were edited locally.</summary>
public sealed class DeletedKnowledgeFileEditsException : Exception
{
    /// <summary>Creates the failure naming the locally edited files.</summary>
    public DeletedKnowledgeFileEditsException(IReadOnlyList<string> filePaths) : base(Describe(filePaths, static filePath => filePath))
    {
        FilePaths = filePaths;
    }

    /// <summary>Workspace-relative paths of the locally edited files.</summary>
    public IReadOnlyList<string> FilePaths { get; }

    /// <summary>Builds the summary used for <see cref="Exception.Message"/>, formatting each path with <paramref name="formatFilePath"/>.</summary>
    public static string Describe(IReadOnlyList<string> filePaths, Func<string, string> formatFilePath) => $"{(filePaths.Count == 1 ? "A knowledge file you changed locally was" : $"{filePaths.Count} knowledge files you changed locally were")} deleted in the environment: {string.Join(", ", filePaths.Select(formatFilePath))}";
}
