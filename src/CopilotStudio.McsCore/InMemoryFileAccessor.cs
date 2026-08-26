// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.McsCore;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public sealed class InMemoryFileAccessorFactory : IFileAccessorFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, InMemoryFileAccessor> accessors = new ConcurrentDictionary<string, InMemoryFileAccessor>(StringComparer.OrdinalIgnoreCase);

    private readonly bool requireSession;

    public InMemoryFileAccessorFactory()
        : this(requireSession: false)
    {
    }

    public InMemoryFileAccessorFactory(bool requireSession)
    {
        this.requireSession = requireSession;
    }

    /// <inheritdoc/>
    public bool IsMemoryBacked => true;

    /// <inheritdoc/>
    public IFileAccessor Create(DirectoryPath root)
    {
        if (this.requireSession && !WorkspaceHoldRegistry.HasActiveSession())
        {
            throw new InvalidOperationException(
                $"An in-memory workspace was opened outside a workspace session. Wrap the operation in {nameof(FileAccessorFactoryExtensions.LeaseTemporaryWorkspace)} or {nameof(FileAccessorFactoryExtensions.LeaseWorkspace)} and dispose the lease when the operation ends, so the workspace is not retained for the life of the process.");
        }

        var accessor = this.accessors.GetOrAdd(root.ToString(), _ => new InMemoryFileAccessor());
        WorkspaceHoldRegistry.TrackOpenedRoot(this, root, accessor);
        return accessor;
    }

    /// <inheritdoc/>
    public void Release(DirectoryPath root)
    {
        var key = root.ToString();
        this.ReleaseExact(key);

        var nestedPrefix = key.TrimEnd('/') + "/";
        foreach (var candidate in this.accessors.Keys.ToList())
        {
            if (candidate.StartsWith(nestedPrefix, StringComparison.OrdinalIgnoreCase)
                && !WorkspaceHoldRegistry.IsHeld(this, candidate))
            {
                this.ReleaseExact(candidate);
            }
        }
    }

    private void ReleaseExact(string key)
    {
        if (this.accessors.TryRemove(key, out var accessor))
        {
            accessor.Clear();
        }
    }

    /// <summary>
    /// Drops every workspace held by this factory and discards the content each one holds.
    /// </summary>
    public void ReleaseAll()
    {
        foreach (var key in this.accessors.Keys.ToList())
        {
            if (this.accessors.TryRemove(key, out var accessor))
            {
                accessor.Clear();
            }
        }
    }

    /// <summary>
    /// Releases every workspace held by this factory. A dependency injection container that owns the
    /// factory calls this when its scope ends.
    /// </summary>
    public void Dispose() => this.ReleaseAll();
}

/// <summary>
/// An agent workspace held in process memory. Content is committed when the stream returned by
/// <see cref="OpenWrite"/> is disposed.
/// </summary>
public sealed class InMemoryFileAccessor : IFileAccessor
{
    private readonly ConcurrentDictionary<string, WorkspaceFile> files = new ConcurrentDictionary<string, WorkspaceFile>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the number of files held for this workspace.
    /// </summary>
    public int Count => this.files.Count;

    /// <summary>
    /// Gets the path of every file held for this workspace.
    /// </summary>
    public IReadOnlyCollection<string> FilePaths => this.files.Keys.ToList();

    /// <summary>
    /// Discards every file held for this workspace.
    /// </summary>
    public void Clear() => this.files.Clear();

    /// <inheritdoc/>
    public bool Exists(AgentFilePath path) => this.files.ContainsKey(Normalize(path));

    public Stream OpenRead(AgentFilePath path)
    {
        if (this.files.TryGetValue(Normalize(path), out var file))
        {
            return new MemoryStream(file.Buffer, 0, file.Length, writable: false);
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
        if (!this.files.TryRemove(Normalize(sourcePath), out var file))
        {
            throw new FileNotFoundException($"File not found: {sourcePath}");
        }

        this.files[Normalize(targetPath)] = file;
    }

    public IEnumerable<AgentFilePath> ListFiles(string? relativeFolder = null, string filePattern = "*.*")
    {
        var folderPrefix = string.IsNullOrEmpty(relativeFolder) ? null : relativeFolder!.TrimEnd('/') + "/";
        foreach (var key in this.files.Keys)
        {
            if (folderPrefix != null && !key.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (MatchesPattern(key, filePattern))
            {
                yield return new AgentFilePath(key);
            }
        }
    }

    private static string Normalize(AgentFilePath path) => path.ToString();

    private static bool MatchesPattern(string key, string filePattern)
    {
        if (filePattern == "*.*" || filePattern == "*")
        {
            return true;
        }

        var fileName = key.Substring(key.LastIndexOf('/') + 1);
        return filePattern.StartsWith("*", StringComparison.Ordinal) ? fileName.EndsWith(filePattern.Substring(1), StringComparison.OrdinalIgnoreCase) : string.Equals(fileName, filePattern, StringComparison.OrdinalIgnoreCase);
    }

    private readonly struct WorkspaceFile
    {
        public WorkspaceFile(byte[] buffer, int length)
        {
            this.Buffer = buffer;
            this.Length = length;
        }

        public byte[] Buffer { get; }

        public int Length { get; }
    }

    private sealed class WriteCapturingStream : MemoryStream
    {
        private readonly string key;
        private readonly ConcurrentDictionary<string, WorkspaceFile> store;
        private bool committed;

        public WriteCapturingStream(string key, ConcurrentDictionary<string, WorkspaceFile> store)
        {
            this.key = key;
            this.store = store;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !this.committed)
            {
                this.committed = true;
                this.store[this.key] = new WorkspaceFile(this.GetBuffer(), (int)this.Length);
            }

            base.Dispose(disposing);
        }
    }
}
