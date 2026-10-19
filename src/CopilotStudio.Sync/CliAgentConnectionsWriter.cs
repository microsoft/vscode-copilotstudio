// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

internal readonly struct LayeredConnectionFile
{
    public LayeredConnectionFile(AgentFilePath path, string leaf)
    {
        Path = path;
        Leaf = leaf;
    }

    public AgentFilePath Path { get; }

    public string Leaf { get; }

    public string LogicalName => Leaf.Substring(0, Leaf.Length - CliAgentConnectionsWriter.FileExtension.Length);
}

internal static class CliAgentConnectionsWriter
{
    internal const string InfrastructureConnectionsFolder = "infrastructure/connections";

    internal const string FileExtension = ".sync.yaml";

    private const string FolderPrefix = InfrastructureConnectionsFolder + "/";

    public static IEnumerable<LayeredConnectionFile> Enumerate(IFileAccessor fileAccessor)
    {
        if (fileAccessor is null)
        {
            yield break;
        }

        foreach (var existing in fileAccessor.ListFiles(InfrastructureConnectionsFolder, "*" + FileExtension))
        {
            var path = existing.ToString();

            if (!path.StartsWith(FolderPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var leaf = path.Substring(FolderPrefix.Length);

            if (leaf.IndexOf('/') >= 0 || leaf.IndexOf('\\') >= 0 || !leaf.EndsWith(FileExtension, StringComparison.Ordinal) || leaf.Length == FileExtension.Length)
            {
                continue;
            }

            yield return new LayeredConnectionFile(existing, leaf);
        }
    }

    public static bool WriteAll(IFileAccessor fileAccessor, IEnumerable<ConnectionReference>? connectionReferences, Action<string>? reportWarning, CancellationToken cancellationToken)
    {
        var candidates = new List<(ConnectionReference Reference, AgentFilePath Path, string Leaf)>();

        foreach (var reference in ConnectionReferenceStore.Deduplicate(connectionReferences))
        {
            if (TryProjectCandidate(reference, out var path, out var leaf, reportWarning))
            {
                candidates.Add((reference, path, leaf));
            }
        }

        var expectedLeafs = new HashSet<string>(candidates.Select(candidate => candidate.Leaf), StringComparer.Ordinal);
        var changed = false;

        foreach (var existing in Enumerate(fileAccessor).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!expectedLeafs.Contains(existing.Leaf))
            {
                fileAccessor.Delete(existing.Path);
                changed = true;
            }
        }

        foreach (var (reference, path, _) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            changed |= ConnectionReferenceStore.WriteLayeredFile(fileAccessor, reference, path);
        }

        return changed;
    }

    public static AgentFilePath GetConnectionFilePath(ConnectionReference connectionReference) =>
        new AgentFilePath($"{InfrastructureConnectionsFolder}/{ComputeConnectionFileName(connectionReference.ConnectionReferenceLogicalName.Value)}");

    internal static string ComputeConnectionFileName(string logicalName)
    {
        if (string.IsNullOrEmpty(logicalName))
        {
            throw new ArgumentException("Connection reference must have a non-empty logical name.", nameof(logicalName));
        }

        if (logicalName.IndexOf('/') >= 0 || logicalName.IndexOf('\\') >= 0)
        {
            throw new ArgumentException($"Connection reference logical name '{logicalName}' contains a path separator.", nameof(logicalName));
        }

        return logicalName + FileExtension;
    }

    private static bool TryProjectCandidate(ConnectionReference connectionReference, out AgentFilePath path, out string leaf, Action<string>? reportWarning)
    {
        path = default;
        leaf = string.Empty;

        var logicalName = connectionReference?.ConnectionReferenceLogicalName.Value;

        if (string.IsNullOrEmpty(logicalName))
        {
            reportWarning?.Invoke("CLI connection reference with empty logical name; skipping.");

            return false;
        }

        try
        {
            leaf = ComputeConnectionFileName(logicalName);
            path = new AgentFilePath($"{InfrastructureConnectionsFolder}/{leaf}");

            return true;
        }
        catch (ArgumentException failure)
        {
            reportWarning?.Invoke($"CLI connection reference '{logicalName}' has invalid filename: {failure.Message}. Skipping.");

            return false;
        }
    }
}
