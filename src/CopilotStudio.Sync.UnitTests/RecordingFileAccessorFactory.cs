// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.CopilotStudio.McsCore;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

internal sealed class RecordingFileAccessorFactory : IFileAccessorFactory
{
    private static readonly AgentFilePath CloudCachePath = new(".mcs/botdefinition.json");

    private static readonly AgentFilePath ChangeTokenPath = new(".mcs/changetoken.txt");

    private readonly ConcurrentDictionary<string, Microsoft.CopilotStudio.McsCore.InMemoryFileAccessor> _live = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, string> _caches = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, (string Path, string Content)[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public bool IsMemoryBacked => true;

    public ConcurrentQueue<string> Opened { get; } = new();

    public ConcurrentQueue<string> Released { get; } = new();

    public int LiveCount => _live.Count;

    public IFileAccessor Create(DirectoryPath root)
    {
        if (!WorkspaceHoldRegistry.HasActiveSession())
        {
            throw new InvalidOperationException($"'{root}' was opened outside a workspace session.");
        }

        var accessor = _live.GetOrAdd(root.ToString(), _ => new Microsoft.CopilotStudio.McsCore.InMemoryFileAccessor());
        Opened.Enqueue(root.ToString());
        WorkspaceHoldRegistry.TrackOpenedRoot(this, root, accessor);

        return accessor;
    }

    public void Release(DirectoryPath root)
    {
        var key = root.ToString();

        foreach (var candidate in _live.Keys.Where(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(key.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            if (_live.TryRemove(candidate, out var accessor))
            {
                if (accessor.Exists(CloudCachePath))
                {
                    _caches[candidate] = Canonical(JsonNode.Parse(Read(accessor, CloudCachePath)))!.ToJsonString() + "\n" + (accessor.Exists(ChangeTokenPath) ? Read(accessor, ChangeTokenPath) : string.Empty);
                }

                _files[candidate] = accessor.ListFiles().Select(path => (path.ToString(), Convert.ToBase64String(accessor.ReadBytes(path).ToArray()))).OrderBy(file => file.Item1, StringComparer.Ordinal).ToArray();
                accessor.Clear();
                Released.Enqueue(candidate);
            }
        }
    }

    public string TakeCache(string folder)
    {
        Assert.True(_caches.TryRemove(Assert.Single(_caches.Keys, candidate => candidate.EndsWith("/" + folder + "/", StringComparison.OrdinalIgnoreCase)), out var cache));

        return cache!;
    }

    public (string Path, string Content)[] TakeFiles(string folder)
    {
        Assert.True(_files.TryRemove(Assert.Single(_files.Keys, candidate => candidate.EndsWith("/" + folder + "/", StringComparison.OrdinalIgnoreCase)), out var files));

        return files!;
    }

    private static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject properties => new JsonObject(properties.OrderBy(property => property.Key, StringComparer.Ordinal).Select(property => new KeyValuePair<string, JsonNode?>(property.Key, Canonical(property.Value)))),
        JsonArray items => new JsonArray(items.Select(Canonical).OrderBy(item => item?.ToJsonString(), StringComparer.Ordinal).ToArray()),
        _ => node?.DeepClone(),
    };

    private static string Read(IFileAccessor accessor, AgentFilePath path)
    {
        using var reader = new StreamReader(accessor.OpenRead(path));

        return reader.ReadToEnd();
    }
}
