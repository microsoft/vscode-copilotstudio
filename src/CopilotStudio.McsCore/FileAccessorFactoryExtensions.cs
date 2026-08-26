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

    private static readonly AsyncLocal<OperationScope?> CurrentOperation = new AsyncLocal<OperationScope?>();

    internal static IFileAccessor Acquire(IFileAccessorFactory factory, DirectoryPath root, string? temporaryDirectory)
    {
        var operation = WorkspaceHoldRegistry.EnterOperation();
        try
        {
            return Tables.GetOrCreateValue(factory).AcquireLease(factory, root, operation, temporaryDirectory);
        }
        catch
        {
            WorkspaceHoldRegistry.LeaveOperation();
            throw;
        }
    }

    internal static void Release(IFileAccessorFactory factory, DirectoryPath root)
    {
        Tables.GetOrCreateValue(factory).ReleaseLease(root);
        WorkspaceHoldRegistry.LeaveOperation();
    }

    internal static void TrackOpenedRoot(IFileAccessorFactory factory, DirectoryPath root, IFileAccessor accessor)
    {
        var operation = WorkspaceHoldRegistry.CurrentOperation.Value;
        if (operation == null || operation.Depth <= 0)
        {
            return;
        }

        Tables.GetOrCreateValue(factory).TrackRoot(factory, root, operation, accessor);
    }

    internal static bool IsHeld(IFileAccessorFactory factory, string root) =>
        Tables.GetOrCreateValue(factory).IsHeld(root);

    internal static bool HasActiveSession() => WorkspaceHoldRegistry.CurrentOperation.Value?.Depth > 0;

    private static OperationScope EnterOperation()
    {
        var operation = WorkspaceHoldRegistry.CurrentOperation.Value;
        if (operation == null || operation.Depth <= 0)
        {
            operation = new OperationScope();
            WorkspaceHoldRegistry.CurrentOperation.Value = operation;
        }

        operation.Enter();
        return operation;
    }

    private static void LeaveOperation()
    {
        var operation = WorkspaceHoldRegistry.CurrentOperation.Value;
        if (operation == null)
        {
            return;
        }

        if (operation.Leave() > 0)
        {
            return;
        }

        WorkspaceHoldRegistry.CurrentOperation.Value = null;
        foreach (var (factory, table) in operation.TakeParticipants())
        {
            table.ReleaseOperation(factory, operation);
        }
    }

    internal sealed class OperationScope
    {
        private readonly List<(IFileAccessorFactory Factory, WorkspaceHoldTable Table)> participants =
            new List<(IFileAccessorFactory, WorkspaceHoldTable)>();

        private int depth;

        public int Depth => Volatile.Read(ref this.depth);

        public void Enter() => Interlocked.Increment(ref this.depth);

        public int Leave() => Interlocked.Decrement(ref this.depth);

        public void TrackParticipant(IFileAccessorFactory factory, WorkspaceHoldTable table)
        {
            lock (this.participants)
            {
                foreach (var participant in this.participants)
                {
                    if (ReferenceEquals(participant.Table, table))
                    {
                        return;
                    }
                }

                this.participants.Add((factory, table));
            }
        }

        public List<(IFileAccessorFactory Factory, WorkspaceHoldTable Table)> TakeParticipants()
        {
            lock (this.participants)
            {
                var copy = new List<(IFileAccessorFactory, WorkspaceHoldTable)>(this.participants);
                this.participants.Clear();
                return copy;
            }
        }
    }

    internal sealed class WorkspaceHoldTable
    {
        private readonly Dictionary<string, RootRecord> roots = new Dictionary<string, RootRecord>(StringComparer.OrdinalIgnoreCase);

        public IFileAccessor AcquireLease(IFileAccessorFactory factory, DirectoryPath root, OperationScope operation, string? temporaryDirectory)
        {
            lock (this.roots)
            {
                var key = root.ToString();
                if (this.roots.TryGetValue(key, out var existing))
                {
                    WorkspaceHoldTable.AssertSameOperation(existing.Operation, operation);
                    existing.LeaseCount++;
                    existing.TemporaryDirectory ??= temporaryDirectory;
                    return existing.Accessor;
                }

                this.AssertNoOverlappingOperation(key, operation);

                var accessor = factory.Create(root);
                var record = new RootRecord(accessor, operation, temporaryDirectory);
                record.LeaseCount = 1;
                this.roots[key] = record;
                operation.TrackParticipant(factory, this);
                return accessor;
            }
        }

        public void TrackRoot(IFileAccessorFactory factory, DirectoryPath root, OperationScope operation, IFileAccessor accessor)
        {
            lock (this.roots)
            {
                var key = root.ToString();
                if (this.roots.TryGetValue(key, out var existing))
                {
                    WorkspaceHoldTable.AssertSameOperation(existing.Operation, operation);
                    return;
                }

                this.AssertNoOverlappingOperation(key, operation);
                this.roots[key] = new RootRecord(accessor, operation, temporaryDirectory: null);
                operation.TrackParticipant(factory, this);
            }
        }

        public void ReleaseLease(DirectoryPath root)
        {
            lock (this.roots)
            {
                if (this.roots.TryGetValue(root.ToString(), out var record) && record.LeaseCount > 0)
                {
                    record.LeaseCount--;
                }
            }
        }

        public void ReleaseOperation(IFileAccessorFactory factory, OperationScope operation)
        {
            var temporaryDirectories = new List<string>();
            lock (this.roots)
            {
                var owned = new List<string>();
                foreach (var pair in this.roots)
                {
                    if (ReferenceEquals(pair.Value.Operation, operation))
                    {
                        owned.Add(pair.Key);
                    }
                }

                foreach (var key in owned)
                {
                    var record = this.roots[key];
                    this.roots.Remove(key);
                    if (record.TemporaryDirectory != null)
                    {
                        temporaryDirectories.Add(record.TemporaryDirectory);
                    }

                    factory.Release(new DirectoryPath(key));
                }
            }

            foreach (var temporaryDirectory in temporaryDirectories)
            {
                WorkspaceHoldTable.DeleteTemporaryDirectory(temporaryDirectory);
            }
        }

        public bool IsHeld(string root)
        {
            lock (this.roots)
            {
                return this.roots.ContainsKey(root);
            }
        }

        private static void AssertSameOperation(OperationScope owner, OperationScope operation)
        {
            if (!ReferenceEquals(owner, operation))
            {
                throw new InvalidOperationException("This workspace is already in use by another operation. Give each concurrent operation its own workspace root so their file changes cannot interleave.");
            }
        }

        private static bool Overlaps(string first, string second)
        {
            if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return first.StartsWith(second.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase) || second.StartsWith(first.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
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

        private void AssertNoOverlappingOperation(string key, OperationScope operation)
        {
            foreach (var pair in this.roots)
            {
                if (!ReferenceEquals(pair.Value.Operation, operation) && WorkspaceHoldTable.Overlaps(key, pair.Key))
                {
                    throw new InvalidOperationException("This workspace overlaps a workspace already in use by another operation. Give each concurrent operation its own workspace root so their file changes cannot interleave.");
                }
            }
        }

        private sealed class RootRecord
        {
            public RootRecord(IFileAccessor accessor, OperationScope operation, string? temporaryDirectory)
            {
                this.Accessor = accessor;
                this.Operation = operation;
                this.TemporaryDirectory = temporaryDirectory;
            }

            public IFileAccessor Accessor { get; }

            public OperationScope Operation { get; }

            public int LeaseCount { get; set; }

            public string? TemporaryDirectory { get; set; }
        }
    }
}
