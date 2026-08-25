// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.McsCore;

using System;
using System.IO;

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
        private readonly string? temporaryDirectory;
        private bool released;

        public WorkspaceLease(IFileAccessorFactory factory, DirectoryPath root, string? temporaryDirectory)
        {
            this.factory = factory;
            this.temporaryDirectory = temporaryDirectory;
            this.Root = root;
            this.Accessor = factory.Create(root);
        }

        public DirectoryPath Root { get; }

        public IFileAccessor Accessor { get; }

        public void Dispose()
        {
            if (this.released)
            {
                return;
            }

            this.released = true;
            this.factory.Release(this.Root);

            if (this.temporaryDirectory == null)
            {
                return;
            }

            try
            {
                if (Directory.Exists(this.temporaryDirectory))
                {
                    Directory.Delete(this.temporaryDirectory, recursive: true);
                }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
