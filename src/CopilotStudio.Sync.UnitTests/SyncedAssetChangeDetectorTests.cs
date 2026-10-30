// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class SyncedAssetChangeDetectorTests
{
    private const string Folder = "connectors/Contoso-2fcc84f9-b852-496a-8b53-f571ccbf74c0";

    private static readonly Guid AssetId = Guid.Parse("2fcc84f9-b852-496a-8b53-f571ccbf74c0");

    private static readonly IReadOnlyDictionary<string, string> CloudCopy = new Dictionary<string, string>
    {
        [CustomConnectorLayout.MetadataFileName] = "{\n  \"description\": \"cloud\"\n}",
        [CustomConnectorLayout.OpenApiDefinitionFileName] = "{\n  \"swagger\": \"2.0\"\n}",
    };

    private static readonly IReadOnlyDictionary<string, string> NoCloudCopy = new Dictionary<string, string>();

    private static SyncedAssetSnapshot Snapshot(string contentHash = "hash", string? baselineHash = "hash", string? baseVersion = "5", string? baseVersionContentHash = "hash") =>
        new(SyncedAssetKind.CustomConnector, AssetId, "Contoso", Folder, contentHash, baselineHash, baseVersion, baseVersionContentHash);

    private static Dictionary<string, string> CloudCopyWith(string fileName, string? content)
    {
        var files = new Dictionary<string, string>(CloudCopy);
        if (content == null)
        {
            files.Remove(fileName);
        }
        else
        {
            files[fileName] = content;
        }

        return files;
    }

    private static List<Change> DetectRemote(SyncedAssetSnapshot asset, string? remoteVersion, IReadOnlyDictionary<string, string>? remoteFiles, IReadOnlyDictionary<string, string>? cloudCopy = null, string? remoteContentHash = null)
    {
        var remoteStates = remoteVersion == null ? new Dictionary<Guid, RemoteAssetState>() : new Dictionary<Guid, RemoteAssetState> { [AssetId] = new RemoteAssetState(remoteVersion, remoteFiles, remoteContentHash) };
        return SyncedAssetChangeDetector.DetectRemoteChanges(new[] { asset }, remoteStates, _ => cloudCopy ?? CloudCopy).ToList();
    }

    [Fact]
    public void DetectLocalChanges_ContentMatchesBaseline_ReturnsNothing()
    {
        Assert.Empty(SyncedAssetChangeDetector.DetectLocalChanges(Snapshot(), CloudCopy, CloudCopyWith(CustomConnectorLayout.OpenApiDefinitionFileName, "{}")));
    }

    [Fact]
    public void DetectLocalChanges_OneFileEdited_ReturnsAnUpdateForThatFileOnly()
    {
        var change = Assert.Single(SyncedAssetChangeDetector.DetectLocalChanges(Snapshot(contentHash: "edited"), CloudCopy, CloudCopyWith(CustomConnectorLayout.OpenApiDefinitionFileName, "{\n  \"swagger\": \"3.0\"\n}")));

        Assert.Equal(ChangeType.Update, change.ChangeType);
        Assert.Equal("Contoso", change.Name);
        Assert.Equal($"{Folder}/openapidefinition.json", change.Uri);
        Assert.Equal("CustomConnector", change.ChangeKind);
        Assert.Equal($"Mcs.Connector.{AssetId}.openapidefinition", change.SchemaName);
        Assert.Null(change.RemoteContent);
    }

    [Fact]
    public void DetectLocalChanges_FileDiffersOnlyInLineEndings_IsNotReported()
    {
        var localFiles = CloudCopyWith(CustomConnectorLayout.OpenApiDefinitionFileName, "{\r\n  \"swagger\": \"2.0\"\r\n}");
        localFiles[CustomConnectorLayout.MetadataFileName] = "{\n  \"description\": \"local\"\n}";

        var change = Assert.Single(SyncedAssetChangeDetector.DetectLocalChanges(Snapshot(contentHash: "edited"), CloudCopy, localFiles));

        Assert.Equal($"{Folder}/metadata.yml", change.Uri);
        Assert.Equal($"Mcs.Connector.{AssetId}", change.SchemaName);
    }

    [Fact]
    public void DetectLocalChanges_EveryFileDiffersOnlyInLineEndings_ReturnsNothing()
    {
        Assert.Empty(SyncedAssetChangeDetector.DetectLocalChanges(Snapshot(contentHash: "edited"), CloudCopy, CloudCopy.ToDictionary(file => file.Key, file => file.Value.Replace("\n", "\r\n"))));
    }

    [Fact]
    public void DetectLocalChanges_FileAddedAndFileRemoved_ReturnsACreateAndADelete()
    {
        var localFiles = CloudCopyWith(CustomConnectorLayout.OpenApiDefinitionFileName, null);
        localFiles[CustomConnectorLayout.IconFileName] = "aWNvbg==";

        var changes = SyncedAssetChangeDetector.DetectLocalChanges(Snapshot(contentHash: "edited"), CloudCopy, localFiles).ToList();

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, change => change.ChangeType == ChangeType.Delete && change.Uri == $"{Folder}/openapidefinition.json");
        Assert.Contains(changes, change => change.ChangeType == ChangeType.Create && change.SchemaName == $"Mcs.Connector.{AssetId}.iconblob");
    }

    [Fact]
    public void DetectLocalChanges_NoCloudCopy_ReturnsOneUpdateOnTheMetadataFile()
    {
        var change = Assert.Single(SyncedAssetChangeDetector.DetectLocalChanges(Snapshot(contentHash: "edited"), NoCloudCopy, CloudCopyWith(CustomConnectorLayout.OpenApiDefinitionFileName, "{}")));

        Assert.Equal(ChangeType.Update, change.ChangeType);
        Assert.Equal($"{Folder}/metadata.yml", change.Uri);
        Assert.Equal($"Mcs.Connector.{AssetId}", change.SchemaName);
    }

    [Fact]
    public void DetectLocalChanges_NoBaseline_ReturnsACreateForEveryLocalFile()
    {
        var changes = SyncedAssetChangeDetector.DetectLocalChanges(Snapshot(baselineHash: null), NoCloudCopy, CloudCopy).ToList();

        Assert.All(changes, change => Assert.Equal(ChangeType.Create, change.ChangeType));
        Assert.Equal(new[] { $"{Folder}/metadata.yml", $"{Folder}/openapidefinition.json" }, changes.Select(change => change.Uri));
    }

    [Fact]
    public void DetectLocalChanges_PromptKind_NamesThePromptFileAfterThePromptAndTheMetadataFileWithASuffix()
    {
        var cloudCopy = new Dictionary<string, string> { [AiPromptLayout.PromptFileName] = "{ \"instruction\": \"cloud\" }", [AiPromptLayout.MetadataFileName] = "name: cloud\n" };
        var localFiles = new Dictionary<string, string> { [AiPromptLayout.PromptFileName] = "{ \"instruction\": \"local\" }", [AiPromptLayout.MetadataFileName] = "name: local\n" };

        var changes = SyncedAssetChangeDetector.DetectLocalChanges(Snapshot(contentHash: "edited") with { Kind = SyncedAssetKind.AiPrompt }, cloudCopy, localFiles).ToList();

        Assert.All(changes, change => Assert.Equal("AIPrompt", change.ChangeKind));
        Assert.Equal(new[] { $"Mcs.AIPrompt.{AssetId}", $"Mcs.AIPrompt.{AssetId}.metadata" }, changes.Select(change => change.SchemaName));
    }

    [Fact]
    public void DetectRemoteChanges_VersionMatchesBase_ReturnsNothingWithoutReadingTheCloudCopy()
    {
        Assert.Empty(SyncedAssetChangeDetector.DetectRemoteChanges(new[] { Snapshot() }, new Dictionary<Guid, RemoteAssetState> { [AssetId] = new RemoteAssetState("5", CloudCopy, null) }, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void DetectRemoteChanges_OnlyTheVersionMoved_ReturnsNothing()
    {
        Assert.Empty(DetectRemote(Snapshot(), "6", CloudCopyWith(CustomConnectorLayout.MetadataFileName, "{\n  \"versionnumber\": 6\n}"), remoteContentHash: "hash"));
    }

    [Fact]
    public void DetectRemoteChanges_VersionMovedAndTheBaselineDoesNotDescribeTheBaseVersion_ReturnsAnUpdate()
    {
        Assert.Equal(ChangeType.Update, Assert.Single(DetectRemote(Snapshot(baseVersionContentHash: null), "6", CloudCopy, remoteContentHash: "hash")).ChangeType);
    }

    [Fact]
    public void DetectRemoteChanges_NoBaseAndNotInCloud_ReturnsNothing()
    {
        Assert.Empty(DetectRemote(Snapshot(baseVersion: null), remoteVersion: null, remoteFiles: null));
    }

    [Fact]
    public void DetectRemoteChanges_OneFileChangedInTheCloud_ReturnsAnUpdateForThatFileWithItsRemoteContent()
    {
        var change = Assert.Single(DetectRemote(Snapshot(), "6", CloudCopyWith(CustomConnectorLayout.MetadataFileName, "{\n  \"description\": \"portal\"\n}")));

        Assert.Equal(ChangeType.Update, change.ChangeType);
        Assert.Equal($"{Folder}/metadata.yml", change.Uri);
        Assert.Equal($"Mcs.Connector.{AssetId}", change.SchemaName);
        Assert.Equal("{\n  \"description\": \"portal\"\n}", change.RemoteContent);
    }

    [Fact]
    public void DetectRemoteChanges_NoCloudCopy_ReturnsOneUpdateWithTheRemoteMetadata()
    {
        var change = Assert.Single(DetectRemote(Snapshot(), "6", CloudCopyWith(CustomConnectorLayout.MetadataFileName, "portal metadata"), NoCloudCopy));

        Assert.Equal($"Mcs.Connector.{AssetId}", change.SchemaName);
        Assert.Equal("portal metadata", change.RemoteContent);
    }

    [Fact]
    public void DetectRemoteChanges_RemoteFilesNotDownloaded_ReturnsOneUpdateWithoutContent()
    {
        var change = Assert.Single(DetectRemote(Snapshot(), "6", remoteFiles: null));

        Assert.Equal(ChangeType.Update, change.ChangeType);
        Assert.Null(change.RemoteContent);
    }

    [Fact]
    public void DetectRemoteChanges_BaseButNotInCloud_ReturnsADeleteForEveryCachedFile()
    {
        var changes = DetectRemote(Snapshot(), remoteVersion: null, remoteFiles: null);

        Assert.Equal(new[] { $"Mcs.Connector.{AssetId}", $"Mcs.Connector.{AssetId}.openapidefinition" }, changes.Select(change => change.SchemaName));
        Assert.All(changes, change => Assert.Equal(ChangeType.Delete, change.ChangeType));
        Assert.All(changes, change => Assert.Null(change.RemoteContent));
    }

    [Fact]
    public void DetectRemoteChanges_NoBaseButInCloud_ReturnsACreateForEveryRemoteFileWithItsContent()
    {
        var changes = DetectRemote(Snapshot(baseVersion: null), "1", CloudCopy);

        Assert.All(changes, change => Assert.Equal(ChangeType.Create, change.ChangeType));
        Assert.Equal(new[] { CloudCopy[CustomConnectorLayout.MetadataFileName], CloudCopy[CustomConnectorLayout.OpenApiDefinitionFileName] }, changes.Select(change => change.RemoteContent));
    }
}
