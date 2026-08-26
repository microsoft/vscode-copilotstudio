// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.McsCore;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

public static class FileAccessorFactoryExtensions
{
    /// <summary>
    /// Takes a hold on an existing workspace root. Disposing the returned lease releases the workspace.
    /// </summary>
    /// <param name="factory">The factory that owns the workspace.</param>
    /// <param name="root">The workspace root to hold.</param>
    public static IWorkspaceLease LeaseWorkspace(this IFileAccessorFactory factory, DirectoryPath root)
    {
        if (factory == null)
        {
            throw new ArgumentNullException(nameof(factory));
        }

        return new WorkspaceLease(factory, root, temporaryDirectory: null);
    }

    /// <summary>
    /// Takes a hold on a new scratch workspace.
    /// </summary>
    /// <param name="factory">The factory to create the workspace in.</param>
    /// <param name="prefix">A short prefix identifying the operation that owns the workspace.</param>
    public static IWorkspaceLease LeaseTemporaryWorkspace(this IFileAccessorFactory factory, string prefix)
    {
        if (factory == null)
        {
            throw new ArgumentNullException(nameof(factory));
        }

        var name = prefix + Guid.NewGuid().ToString("N");
        if (factory.IsMemoryBacked)
        {
            return new WorkspaceLease(factory, new DirectoryPath("/" + name), temporaryDirectory: null);
        }

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(temporaryDirectory);
        return new WorkspaceLease(factory, new DirectoryPath(temporaryDirectory.Replace('\\', '/')), temporaryDirectory);
    }

    private sealed class WorkspaceLease : IWorkspaceLease
    {
        private readonly IFileAccessorFactory factory;
        private int released;

        public WorkspaceLease(IFileAccessorFactory factory, DirectoryPath root, string? temporaryDirectory)
        {
            this.factory = factory;
            this.Root = root;
            this.Accessor = WorkspaceHoldRegistry.Acquire(factory, root, temporaryDirectory);
        }

        public DirectoryPath Root { get; }

        public IFileAccessor Accessor { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.released, 1) != 0)
            {
                return;
            }

            WorkspaceHoldRegistry.Release(this.factory, this.Root);
        }
    }
}

internal static class WorkspaceHoldRegistry
{
    private static readonly ConditionalWeakTable<IFileAccessorFactory, WorkspaceHoldTable> Tables =
        new ConditionalWeakTable<IFileAccessorFactory, WorkspaceHoldTable>();

    internal static IFileAccessor Acquire(IFileAccessorFactory factory, DirectoryPath root, string? temporaryDirectory) =>
        Tables.GetOrCreateValue(factory).Acquire(factory, root, temporaryDirectory);

    internal static void Release(IFileAccessorFactory factory, DirectoryPath root) =>
        Tables.GetOrCreateValue(factory).Release(factory, root);

    internal static bool IsHeld(IFileAccessorFactory factory, string root) =>
        Tables.GetOrCreateValue(factory).IsHeld(root);

    private sealed class WorkspaceHoldTable
    {
        private readonly Dictionary<string, Hold> holds = new Dictionary<string, Hold>(StringComparer.OrdinalIgnoreCase);

        public IFileAccessor Acquire(IFileAccessorFactory factory, DirectoryPath root, string? temporaryDirectory)
        {
            lock (this.holds)
            {
                var key = root.ToString();
                if (this.holds.TryGetValue(key, out var existing))
                {
                    existing.Count++;
                    existing.TemporaryDirectory ??= temporaryDirectory;
                    return existing.Accessor;
                }

                var accessor = factory.Create(root);
                this.holds[key] = new Hold(accessor, temporaryDirectory);
                return accessor;
            }
        }

        public void Release(IFileAccessorFactory factory, DirectoryPath root)
        {
            string? temporaryDirectory;
            lock (this.holds)
            {
                var key = root.ToString();
                if (!this.holds.TryGetValue(key, out var hold))
                {
                    return;
                }

                hold.Count--;
                if (hold.Count > 0)
                {
                    return;
                }

                this.holds.Remove(key);
                temporaryDirectory = hold.TemporaryDirectory;
                factory.Release(root);
            }

            WorkspaceHoldTable.DeleteTemporaryDirectory(temporaryDirectory);
        }

        public bool IsHeld(string root)
        {
            lock (this.holds)
            {
                return this.holds.ContainsKey(root);
            }
        }

        private static void DeleteTemporaryDirectory(string? temporaryDirectory)
        {
            if (temporaryDirectory == null)
            {
                return;
            }

            try
            {
                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
            }
        }

        private sealed class Hold
        {
            public Hold(IFileAccessor accessor, string? temporaryDirectory)
            {
                this.Accessor = accessor;
                this.TemporaryDirectory = temporaryDirectory;
                this.Count = 1;
            }

            public IFileAccessor Accessor { get; }

            public int Count { get; set; }

            public string? TemporaryDirectory { get; set; }
        }
    }
}
