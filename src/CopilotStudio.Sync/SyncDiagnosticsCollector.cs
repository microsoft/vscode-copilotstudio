// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Collects workspace problems found while reading a workspace.</summary>
internal sealed class SyncDiagnosticsCollector
{
    private readonly List<WorkspaceDiagnostic> _diagnostics = new();

    private readonly List<Change> _unreadableFiles = new();

    /// <summary>Problems found so far, in discovery order.</summary>
    internal ImmutableArray<WorkspaceDiagnostic> Diagnostics => WorkspaceDiagnostic.GetDistinct(_diagnostics);

    /// <summary>Changes that restore every file whose authored content could not be parsed.</summary>
    internal ImmutableArray<Change> UnreadableFiles => _unreadableFiles.ToImmutableArray();

    /// <summary>True when at least one problem was found.</summary>
    internal bool HasDiagnostics => _diagnostics.Count > 0;

    internal void Add(WorkspaceDiagnostic diagnostic) => _diagnostics.Add(diagnostic);

    internal void AddRange(IEnumerable<WorkspaceDiagnostic> diagnostics) => _diagnostics.AddRange(diagnostics);

    internal void AddUnreadableFile(Change change) => _unreadableFiles.Add(change);
}
