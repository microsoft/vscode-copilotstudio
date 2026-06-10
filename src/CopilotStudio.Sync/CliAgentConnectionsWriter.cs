// Copyright (C) Microsoft Corporation. All rights reserved.
//
// CliAgentSyncSupport / Node D5 — typed-OM-collection projection for CLI
// agents. Projects the ConnectionReference collection (carried on
// DefinitionBase.ConnectionReferences per om/.../BotDefinition.xml line 69)
// from the existing flat connectionreferences.mcs.yml at the workspace
// root to per-reference files at infrastructure/connections/{name}.yaml.
//
// Unlike D2/D3/D4, ConnectionReference is NOT a BotComponentBase — it
// lives on DefinitionBase, not in the Components collection walked by
// ComponentWriter. The dispatch chokepoint for D5 is the existing
// WorkspaceSynchronizer.WriteConnectionReferencesAsync entry point,
// which is called once per UpdateWorkspaceDirectoryAsync invocation.
//
// Per-file shape (TDD decision D5: option I):
// Each file contains the byte-equivalent output of
// CodeSerializer.SerializeConnectionReferences(sw, new[] { cr }) — i.e.
// a 1-item version of the flat-file shape:
//
//     connectionReferences:
//       - connectionReferenceLogicalName: X
//         connectorId: Y
//
// This preserves byte-equivalence to the existing OM serializer for a
// 1-item slice. Option (II) (flat per-file record without the wrapper)
// and option (III) (full-fidelity typed ConnectionReference YAML) were
// considered and deferred — see TDD decision row.
//
// Stale-cleanup: D5 takes a different approach to deletions than D2/D3/D4
// because the existing flat-file shape implicitly handled deletions
// through full overwrite, while a per-file shape requires explicit orphan
// removal. WriteAll pre-prunes the infrastructure/connections/ directory
// against the expected filename set before writing, so any file from a
// prior write that is no longer in the deduped collection is deleted.
// The IFileAccessor.ListFiles disk implementation recurses
// (SearchOption.AllDirectories), so the prune step filters to direct
// children of infrastructure/connections/ to avoid wiping nested files
// (defensive — D5 does not create subdirectories under this folder).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>
/// Writer for CLI agent <see cref="ConnectionReference"/> collections.
///
/// Projects each unique <see cref="ConnectionReference"/> in a
/// <see cref="DefinitionBase.ConnectionReferences"/> collection to its own
/// file at <c>infrastructure/connections/{logicalName}.sync.yaml</c>, using the
/// OM serializer to produce 1-item-collection output per file (byte-
/// equivalent to the existing flat <c>connectionreferences.mcs.yml</c>
/// serializer for a 1-item slice).
/// </summary>
internal static class CliAgentConnectionsWriter
{
    internal const string InfrastructureConnectionsFolder = "infrastructure/connections";

    /// <summary>
    /// Sync-overlay extension (D28): connection references are a Sync-managed overlay,
    /// not a language-recognized MCS component, so they use the <c>.sync.yaml</c>
    /// extension (routes to generic YAML, never MCS-parsed) and are excluded from the
    /// D30 component allowlist scan by living outside the component folders.
    /// </summary>
    internal const string FileExtension = ".sync.yaml";

    /// <summary>
    /// Write all connection references to <c>infrastructure/connections/</c>
    /// as per-reference YAML files, pre-pruning any orphan files from a
    /// prior write that no longer correspond to a current reference.
    /// </summary>
    /// <param name="fileAccessor">File access abstraction.</param>
    /// <param name="connectionReferences">
    /// The connection references to project. Null is tolerated and treated
    /// as an empty collection — in that case, only the prune step runs
    /// (which still removes any stale orphan files).
    /// </param>
    /// <param name="reportWarning">Optional warning sink for skip-and-warn cases.</param>
    /// <param name="cancellationToken">Cancellation token; checked before each delete and write.</param>
    public static void WriteAll(
        IFileAccessor fileAccessor,
        IEnumerable<ConnectionReference>? connectionReferences,
        Action<string>? reportWarning,
        CancellationToken cancellationToken)
    {
        // Null-tolerant per rubber-duck D5 blocking #2: a null collection
        // should not throw — it represents the "0 refs, prune any stale"
        // case and is exercised by the 0-reference stale-cleanup test.
        connectionReferences ??= Enumerable.Empty<ConnectionReference>();

        // Dedup by logical name — mirrors WorkspaceSynchronizer.WriteConnectionReferencesAsync
        // exactly. OrdinalIgnoreCase matches the existing flat-writer behavior
        // for "what is a duplicate?" while leaf-name comparison during prune
        // uses Ordinal (case-sensitive) to be correct on Linux filesystems.
        var unique = connectionReferences
            .Where(cr => !string.IsNullOrEmpty(cr.ConnectionReferenceLogicalName.Value))
            .GroupBy(cr => cr.ConnectionReferenceLogicalName.Value, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();

        // Validate-and-project candidates up front (rubber-duck D5 blocking #1):
        // a single ref with a separator-bearing or otherwise invalid logical
        // name must NOT throw out of WriteAll — that would abort pruning
        // and writes for valid references. Skip-and-warn here so the rest
        // of the collection still gets processed.
        var candidates = new List<(ConnectionReference Cr, AgentFilePath Path, string Leaf)>();
        foreach (var cr in unique)
        {
            if (TryProjectCandidate(cr, out var path, out var leaf, reportWarning))
            {
                candidates.Add((cr, path, leaf));
            }
        }

        // Build expected-filename set (Ordinal — case-sensitive — to match
        // real filesystem semantics on Linux per rubber-duck non-blocking #5).
        var expectedLeafs = new HashSet<string>(
            candidates.Select(c => c.Leaf),
            StringComparer.Ordinal);

        // Prune orphans. ListFiles is recursive on disk (SearchOption.AllDirectories
        // — per FileWriter.ListFiles), so filter strictly to direct children
        // of infrastructure/connections/ to avoid wiping nested files
        // (rubber-duck non-blocking #6).
        var prefix = InfrastructureConnectionsFolder + "/";
        foreach (var existing in fileAccessor.ListFiles(InfrastructureConnectionsFolder, "*" + FileExtension))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pathStr = existing.ToString();
            if (!pathStr.StartsWith(prefix, StringComparison.Ordinal))
            {
                // AgentFilePath uses forward slashes; if the underlying
                // accessor returns a path that doesn't start with our
                // expected prefix, it's not a direct child of our folder.
                continue;
            }

            var relativeLeaf = pathStr.Substring(prefix.Length);
            if (relativeLeaf.IndexOf('/') >= 0 || relativeLeaf.IndexOf('\\') >= 0)
            {
                // Nested file (subdirectory) — D5 doesn't create these and
                // doesn't claim ownership over them. Skip.
                continue;
            }

            if (!expectedLeafs.Contains(relativeLeaf))
            {
                fileAccessor.Delete(existing);
            }
        }

        // Write each candidate. Path/leaf already validated.
        foreach (var (cr, path, _) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteOne(fileAccessor, cr, path);
        }
    }

    /// <summary>
    /// Compute the absolute path under <c>infrastructure/connections/</c>
    /// where a connection reference's per-file YAML would land. Throws on
    /// invalid logical names. Callers that need a non-throwing variant
    /// should use <see cref="TryProjectCandidate"/>.
    /// </summary>
    public static AgentFilePath GetConnectionFilePath(ConnectionReference cr)
    {
        var name = ComputeConnectionFileName(cr.ConnectionReferenceLogicalName.Value);
        return new AgentFilePath($"{InfrastructureConnectionsFolder}/{name}");
    }

    /// <summary>
    /// Compute the leaf filename for a connection reference. Defensive
    /// scope per rubber-duck non-blocking #4: we guard ONLY path separators
    /// (which would create an unintended subdirectory), NOT full Windows-
    /// invalid filename characters or reserved device names. Logical names
    /// are Dataverse-constrained; full filesystem validation is out of
    /// scope. Dots in the name (FoodLogger's WorkIQ ref has 4 dots) are
    /// valid and preserved verbatim.
    /// </summary>
    internal static string ComputeConnectionFileName(string logicalName)
    {
        if (string.IsNullOrEmpty(logicalName))
        {
            throw new ArgumentException(
                "Connection reference must have a non-empty logical name.",
                nameof(logicalName));
        }

        if (logicalName.IndexOf('/') >= 0 || logicalName.IndexOf('\\') >= 0)
        {
            throw new ArgumentException(
                $"Connection reference logical name '{logicalName}' contains a path separator.",
                nameof(logicalName));
        }

        return logicalName + FileExtension;
    }

    /// <summary>
    /// Validate-and-project: produce a (path, leaf) pair for a single
    /// connection reference, or skip-and-warn if the logical name is
    /// invalid. Never throws.
    /// </summary>
    private static bool TryProjectCandidate(
        ConnectionReference cr,
        out AgentFilePath path,
        out string leaf,
        Action<string>? reportWarning)
    {
        path = default;
        leaf = string.Empty;

        var logicalName = cr?.ConnectionReferenceLogicalName.Value;
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
        catch (ArgumentException ex)
        {
            reportWarning?.Invoke(
                $"CLI connection reference '{logicalName}' has invalid filename: {ex.Message}. Skipping.");
            return false;
        }
    }

    private static void WriteOne(IFileAccessor fileAccessor, ConnectionReference cr, AgentFilePath path)
    {
        using var stream = fileAccessor.OpenWrite(path);
        using var sw = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        using var ctx = YamlSerializationContext.UseStandardSerializationContextIfNotDefined(throwOnInvalidYaml: false);

        // 1-item-collection invocation: byte-equivalent per-file slice of
        // the existing flat-file output. The OM serializer's sort step is
        // a no-op for a single-element list, so per-file content is
        // trivially deterministic.
        CodeSerializer.SerializeConnectionReferences(sw, new[] { cr });
    }
}
