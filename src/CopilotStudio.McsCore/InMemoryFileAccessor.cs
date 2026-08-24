// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.McsCore;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public sealed class InMemoryFileAccessorFactory : IFileAccessorFactory
{
    private readonly ConcurrentDictionary<string, InMemoryFileAccessor> accessors = new ConcurrentDictionary<string, InMemoryFileAccessor>(StringComparer.OrdinalIgnoreCase);

    public IFileAccessor Create(DirectoryPath root) => this.accessors.GetOrAdd(root.ToString(), _ => new InMemoryFileAccessor());

    /// <summary>
    /// Drops the in-memory store for a workspace root.
    /// </summary>
    public bool Release(DirectoryPath root) => this.accessors.TryRemove(root.ToString(), out _);

    /// <summary>
    /// Drops every in-memory workspace store held by this factory.
    /// </summary>
    public void ReleaseAll() => this.accessors.Clear();
}

public sealed class InMemoryFileAccessor : IFileAccessor
{
    private readonly ConcurrentDictionary<string, byte[]> files = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, byte[]> Files => this.files;

    public bool Exists(AgentFilePath path) => this.files.ContainsKey(Normalize(path));

    public Stream OpenRead(AgentFilePath path)
    {
        if (this.files.TryGetValue(Normalize(path), out var data))
        {
            return new MemoryStream(data, writable: false);
        }

        throw new FileNotFoundException($"File not found: {path}");
    }

    public Stream OpenWrite(AgentFilePath path) => new WriteCapturingStream(Normalize(path), this.files);

    public void Delete(AgentFilePath path) => this.files.TryRemove(Normalize(path), out _);

    public void DeleteDirectory(AgentFilePath path)
    {
        var prefix = Normalize(path).TrimEnd('/') + "/";
        foreach (var key in this.files.Keys.Where(candidate => candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            this.files.TryRemove(key, out _);
        }
    }

    public void CreateHiddenDirectory(AgentFilePath path)
    {
    }

    public void Replace(AgentFilePath sourcePath, AgentFilePath targetPath)
    {
        if (!this.files.TryRemove(Normalize(sourcePath), out var data))
        {
            throw new FileNotFoundException($"File not found: {sourcePath}");
        }

        this.files[Normalize(targetPath)] = data;
    }

    public IEnumerable<AgentFilePath> ListFiles(string? relativeFolder = null, string filePattern = "*.*")
    {
        var folderPrefix = string.IsNullOrEmpty(relativeFolder) ? null : relativeFolder!.Replace('\\', '/').TrimEnd('/') + "/";
        foreach (var key in this.files.Keys)
        {
            if (folderPrefix != null && !key.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!MatchesPattern(key, filePattern))
            {
                continue;
            }

            yield return new AgentFilePath(key);
        }
    }

    private static string Normalize(AgentFilePath path) => path.ToString().Replace('\\', '/');

    private static bool MatchesPattern(string key, string filePattern)
    {
        if (filePattern == "*.*" || filePattern == "*")
        {
            return true;
        }

        var fileName = key.Substring(key.LastIndexOf('/') + 1);
        if (filePattern.StartsWith("*", StringComparison.Ordinal))
        {
            return fileName.EndsWith(filePattern.Substring(1), StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(fileName, filePattern, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class WriteCapturingStream : MemoryStream
    {
        private readonly string key;
        private readonly ConcurrentDictionary<string, byte[]> store;

        public WriteCapturingStream(string key, ConcurrentDictionary<string, byte[]> store)
        {
            this.key = key;
            this.store = store;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.store[this.key] = this.ToArray();
            }

            base.Dispose(disposing);
        }
    }
}
