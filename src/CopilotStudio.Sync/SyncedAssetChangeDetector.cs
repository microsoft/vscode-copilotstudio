// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

internal sealed record SyncedAssetSnapshot(SyncedAssetKind Kind, Guid Id, string Name, string Folder, string ContentHash, string? BaselineHash, string? BaseVersion)
{
    internal string SchemaName => $"{Kind.SchemaNamePrefix}{Id}";

    internal bool IsChangedLocally => !string.Equals(ContentHash, BaselineHash, StringComparison.Ordinal);

    internal Change ToChange(ChangeType changeType, string? fileName = null, string? remoteContent = null) => new() { Name = Name, Uri = $"{Folder}/{fileName ?? Kind.PrimaryFileName}", ChangeType = changeType, ChangeKind = Kind.ChangeKind, SchemaName = Kind.GetFileSchemaName(Id, fileName ?? Kind.PrimaryFileName), RemoteContent = remoteContent };
}

internal sealed record RemoteAssetState(string? Version, IReadOnlyDictionary<string, string>? Files);

internal static class SyncedAssetChangeDetector
{
    private static readonly IReadOnlyDictionary<string, string> NoFiles = new Dictionary<string, string>();

    internal static IEnumerable<Change> DetectLocalChanges(SyncedAssetSnapshot asset, IReadOnlyDictionary<string, string> cloudCopy, IReadOnlyDictionary<string, string> localFiles) => asset.IsChangedLocally ? DetectFileChanges(asset, asset.BaselineHash == null ? ChangeType.Create : ChangeType.Update, cloudCopy, localFiles, includeContent: false) : Enumerable.Empty<Change>();

    internal static IEnumerable<Change> DetectRemoteChanges(IEnumerable<SyncedAssetSnapshot> assets, IReadOnlyDictionary<Guid, RemoteAssetState> remoteStates, Func<SyncedAssetSnapshot, IReadOnlyDictionary<string, string>> readCloudCopy)
    {
        foreach (var asset in assets)
        {
            var remoteState = remoteStates.TryGetValue(asset.Id, out var state) ? state : null;
            if (GetRemoteChangeType(asset, remoteState) is ChangeType changeType)
            {
                foreach (var change in DetectFileChanges(asset, changeType, readCloudCopy(asset), remoteState?.Files, includeContent: true))
                {
                    yield return change;
                }
            }
        }
    }

    internal static ChangeType? GetRemoteChangeType(SyncedAssetSnapshot asset, RemoteAssetState? remoteState)
    {
        if (remoteState == null)
        {
            return asset.BaseVersion == null ? null : ChangeType.Delete;
        }

        if (asset.BaseVersion == null)
        {
            return ChangeType.Create;
        }

        return string.Equals(asset.BaseVersion, remoteState.Version, StringComparison.Ordinal) ? null : ChangeType.Update;
    }

    private static List<Change> DetectFileChanges(SyncedAssetSnapshot asset, ChangeType changeType, IReadOnlyDictionary<string, string> cloudCopy, IReadOnlyDictionary<string, string>? currentFiles, bool includeContent)
    {
        var originalFiles = changeType == ChangeType.Create ? NoFiles : cloudCopy;
        var changedFiles = changeType == ChangeType.Delete ? NoFiles : currentFiles;
        var fileChanges = changedFiles == null || (originalFiles.Count == 0 && changeType != ChangeType.Create) ? new List<Change>() : asset.Kind.FileNames.Select(fileName => GetFileChangeType(GetContent(originalFiles, fileName), GetContent(changedFiles, fileName)) is ChangeType fileChangeType ? asset.ToChange(fileChangeType, fileName, includeContent ? GetContent(changedFiles, fileName) : null) : null).OfType<Change>().ToList();
        if (fileChanges.Count == 0)
        {
            fileChanges.Add(asset.ToChange(changeType, remoteContent: includeContent ? GetContent(changedFiles, asset.Kind.PrimaryFileName) : null));
        }

        return fileChanges;
    }

    private static ChangeType? GetFileChangeType(string? originalContent, string? currentContent) => originalContent == null ? currentContent == null ? null : ChangeType.Create : currentContent == null ? ChangeType.Delete : string.Equals(NormalizeLineEndings(originalContent), NormalizeLineEndings(currentContent), StringComparison.Ordinal) ? null : ChangeType.Update;

    private static string? GetContent(IReadOnlyDictionary<string, string>? files, string fileName) => files != null && files.TryGetValue(fileName, out var content) ? content : null;

    private static string NormalizeLineEndings(string content) => content.Replace("\r\n", "\n");
}
