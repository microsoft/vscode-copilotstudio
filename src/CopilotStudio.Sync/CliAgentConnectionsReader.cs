// Copyright (C) Microsoft Corporation. All rights reserved.
//
// CliAgentSyncSupport / Node E — read-side inverse of D5's per-reference
// projection. Overlays on-disk fields from
// infrastructure/connections/{logicalName}.sync.yaml onto the cloud-cache
// ConnectionReference collection, preserving fields the on-disk shape
// does not carry (Id, ConnectionId, CustomConnectorId, DisplayName,
// SharedConnectionParameters — see om/src/ObjectModel/SolutionComponents.xml
// lines 208-214).
//
// Migration-safe activation gate (rubber-duck blocking #1):
// The layered shape is "active" iff infrastructure/connections/ has at
// least one direct *.sync.yaml child. When inactive, the caller keeps the
// cloud-cache verbatim. This handles three pre-existing states without
// silent data loss:
//   (a) Clones written by a pre-D5 build (no infrastructure/ folder).
//   (b) Zero-reference CLI agents like BrandSpecialist (D5 writer
//       intentionally creates no directory).
//   (c) A destructive "user deleted all connection files" state — that
//       intent belongs to push-side detection (Node F), not read-side
//       overlay; we keep cache to avoid silent propagation.
//
// Per-file decoding:
//   - Strict-parent filter: skip files in subdirectories. Matches D5's
//     defensive scope (D5 doesn't create subdirectories under
//     infrastructure/connections/).
//   - Typed deserialize: CodeSerializer.Deserialize<ConnectionReferencesSourceFile>
//     (the 1-item-collection inverse of CodeSerializer.SerializeConnectionReferences
//     used by D5).
//   - Enforce exactly one ConnectionReference per file. The D5 writer
//     emits a 1-item slice per file; multi-item content is malformed.
//   - Enforce filename leaf == body.ConnectionReferenceLogicalName
//     (Ordinal — matches D5's leaf-comparison semantics). Mismatch is a
//     filesystem-level inconsistency; warn and skip.
//   - Skip-and-warn on per-file parse failure rather than throwing
//     (rubber-duck non-blocking #6 — less destructive than the omit-on-
//     missing-cloud-entry path).
//
// Field overlay (rubber-duck blocking #2):
//   - Cloud-cache match: ToBuilder().With(ConnectionReferenceLogicalName,
//     ConnectorId) from disk; keep everything else from cloud cache.
//   - No cloud-cache match: build a fresh ConnectionReference from the
//     two disk fields only (a "new-on-disk" reference; the push-side
//     would surface this as a new ConnectionReferenceInsert change).
//   - Cloud-only (no disk file): preserved verbatim in the overlay
//     output. Combined with the activation gate, this means a partial
//     layered shape never silently drops cloud-cache entries.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>
/// Read-side inverse of <see cref="CliAgentConnectionsWriter"/>.
/// Loads per-reference YAML files under
/// <c>infrastructure/connections/</c> and overlays the two emitted
/// fields onto the cloud-cache <see cref="ConnectionReference"/>
/// collection. Preserves all other cloud-cache fields.
/// </summary>
internal static class CliAgentConnectionsReader
{
    /// <summary>
    /// Returns true iff <c>infrastructure/connections/</c> has at least
    /// one direct <c>*.yaml</c> child. This is the "layered shape is
    /// active" signal that gates the overlay step — see file header for
    /// rationale.
    /// </summary>
    public static bool IsLayeredShapeActive(IFileAccessor fileAccessor)
    {
        if (fileAccessor == null)
        {
            return false;
        }

        var prefix = CliAgentConnectionsWriter.InfrastructureConnectionsFolder + "/";

        foreach (var existing in fileAccessor.ListFiles(
                     CliAgentConnectionsWriter.InfrastructureConnectionsFolder,
                     "*" + CliAgentConnectionsWriter.FileExtension))
        {
            var pathStr = existing.ToString();
            if (!pathStr.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relativeLeaf = pathStr.Substring(prefix.Length);
            if (relativeLeaf.IndexOf('/') >= 0 || relativeLeaf.IndexOf('\\') >= 0)
            {
                // Nested file (subdirectory) — D5 doesn't claim ownership
                // here; doesn't count for the active signal.
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// CliAgentSyncSupport / Node F: enumerate the connection-reference
    /// logical names that exist as direct <c>*.yaml</c> children of
    /// <c>infrastructure/connections/</c>. Returns the filename leaves
    /// (without the <c>.yaml</c> extension) under the strict-parent
    /// filter that <see cref="Overlay"/> applies.
    ///
    /// Why this exists: the push-side connection-reference delete detector
    /// cannot use a "local minus cloud" diff on the in-memory definition,
    /// because <see cref="Overlay"/> preserves cloud-only refs verbatim
    /// (so they remain in <c>localDefinition.ConnectionReferences</c>
    /// after read). The destructive delete intent — "user removed
    /// <c>infrastructure/connections/foo.yaml</c>" — only becomes visible
    /// by enumerating the disk directly, then comparing the actual disk
    /// set to the cloud-cache set.
    /// </summary>
    /// <returns>Set of disk logical names (OrdinalIgnoreCase). Returns
    /// empty when the directory is missing.</returns>
    public static HashSet<string> ListDiskLogicalNames(IFileAccessor fileAccessor)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (fileAccessor == null)
        {
            return names;
        }

        var prefix = CliAgentConnectionsWriter.InfrastructureConnectionsFolder + "/";

        foreach (var existing in fileAccessor.ListFiles(
                     CliAgentConnectionsWriter.InfrastructureConnectionsFolder,
                     "*" + CliAgentConnectionsWriter.FileExtension))
        {
            var pathStr = existing.ToString();
            if (!pathStr.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relativeLeaf = pathStr.Substring(prefix.Length);
            if (relativeLeaf.IndexOf('/') >= 0 || relativeLeaf.IndexOf('\\') >= 0)
            {
                continue;
            }

            if (!relativeLeaf.EndsWith(CliAgentConnectionsWriter.FileExtension, StringComparison.Ordinal))
            {
                continue;
            }

            var leaf = relativeLeaf.Substring(
                0, relativeLeaf.Length - CliAgentConnectionsWriter.FileExtension.Length);

            if (string.IsNullOrEmpty(leaf))
            {
                continue;
            }

            names.Add(leaf);
        }

        return names;
    }

    /// <summary>
    /// Overlay on-disk connection-reference fields onto the cloud-cache
    /// collection. Cloud-cache entries without a corresponding disk file
    /// are preserved verbatim; on-disk entries without a cloud match are
    /// added with only the two emitted fields populated.
    /// </summary>
    /// <param name="fileAccessor">File access abstraction.</param>
    /// <param name="cloudCacheRefs">
    /// Connection references from the cloud-cache snapshot.
    /// Null is tolerated as empty.
    /// </param>
    /// <param name="reportWarning">Optional warning sink for skip-and-warn cases.</param>
    /// <param name="cancellationToken">Cancellation token; checked between files.</param>
    /// <returns>The overlaid collection.</returns>
    public static IReadOnlyList<ConnectionReference> Overlay(
        IFileAccessor fileAccessor,
        IReadOnlyList<ConnectionReference>? cloudCacheRefs,
        Action<string>? reportWarning,
        CancellationToken cancellationToken)
    {
        if (fileAccessor == null)
        {
            throw new ArgumentNullException(nameof(fileAccessor));
        }

        cloudCacheRefs ??= Array.Empty<ConnectionReference>();

        // Index cloud-cache by logical name (OrdinalIgnoreCase — matches
        // WriteConnectionReferencesAsync's dedup comparer).
        var cloudByName = new Dictionary<string, ConnectionReference>(StringComparer.OrdinalIgnoreCase);
        foreach (var cr in cloudCacheRefs)
        {
            var name = cr?.ConnectionReferenceLogicalName.Value;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }
            cloudByName[name!] = cr!;
        }

        var prefix = CliAgentConnectionsWriter.InfrastructureConnectionsFolder + "/";

        var diskOverlays = new Dictionary<string, ConnectionReference>(StringComparer.OrdinalIgnoreCase);

        foreach (var existing in fileAccessor.ListFiles(
                     CliAgentConnectionsWriter.InfrastructureConnectionsFolder,
                     "*" + CliAgentConnectionsWriter.FileExtension))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pathStr = existing.ToString();
            if (!pathStr.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relativeLeaf = pathStr.Substring(prefix.Length);
            if (relativeLeaf.IndexOf('/') >= 0 || relativeLeaf.IndexOf('\\') >= 0)
            {
                // Strict-parent filter (matches writer scope).
                continue;
            }

            // Filename leaf without the extension. We compare against the
            // body's logical name in Ordinal (matches D5's leaf-comparison
            // semantics for stale-prune correctness on Linux).
            if (!relativeLeaf.EndsWith(CliAgentConnectionsWriter.FileExtension, StringComparison.Ordinal))
            {
                continue;
            }
            var leafNameWithoutExt = relativeLeaf.Substring(
                0, relativeLeaf.Length - CliAgentConnectionsWriter.FileExtension.Length);

            string yaml;
            try
            {
                using var stream = fileAccessor.OpenRead(existing);
                using var sr = new System.IO.StreamReader(stream, Encoding.UTF8);
                yaml = sr.ReadToEnd();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                reportWarning?.Invoke(
                    $"CLI connection reference file '{pathStr}' could not be read: {ex.Message}. Keeping cloud-cache version.");
                continue;
            }

            ConnectionReferencesSourceFile? parsed;
            try
            {
                using var ctx = YamlSerializationContext.UseStandardSerializationContextIfNotDefined(throwOnInvalidYaml: false);
                parsed = CodeSerializer.Deserialize<ConnectionReferencesSourceFile>(yaml, sourceUri: null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                reportWarning?.Invoke(
                    $"CLI connection reference file '{pathStr}' could not be parsed: {ex.Message}. Keeping cloud-cache version.");
                continue;
            }

            if (parsed?.ConnectionReferences == null)
            {
                reportWarning?.Invoke(
                    $"CLI connection reference file '{pathStr}' has no connectionReferences node. Skipping.");
                continue;
            }

            var items = parsed.ConnectionReferences.ToList();
            if (items.Count != 1)
            {
                reportWarning?.Invoke(
                    $"CLI connection reference file '{pathStr}' must contain exactly one connection reference; found {items.Count}. Skipping.");
                continue;
            }

            var diskRef = items[0];
            var diskLogicalName = diskRef.ConnectionReferenceLogicalName.Value;
            if (string.IsNullOrEmpty(diskLogicalName))
            {
                reportWarning?.Invoke(
                    $"CLI connection reference file '{pathStr}' has empty connectionReferenceLogicalName. Skipping.");
                continue;
            }

            if (!string.Equals(diskLogicalName, leafNameWithoutExt, StringComparison.Ordinal))
            {
                reportWarning?.Invoke(
                    $"CLI connection reference file '{pathStr}' connectionReferenceLogicalName '{diskLogicalName}' does not match filename leaf '{leafNameWithoutExt}'. Skipping.");
                continue;
            }

            ConnectionReference overlaid;
            if (cloudByName.TryGetValue(diskLogicalName, out var cloudMatch))
            {
                // Cloud-cache match: ToBuilder preserves all other fields
                // (Id, ConnectionId, CustomConnectorId, DisplayName,
                // SharedConnectionParameters); disk supplies the two
                // emitted ones.
                var builder = cloudMatch.ToBuilder();
                builder.ConnectionReferenceLogicalName = diskRef.ConnectionReferenceLogicalName;
                builder.ConnectorId = diskRef.ConnectorId;
                overlaid = builder.Build();
            }
            else
            {
                // New-on-disk: build from the two disk fields only. Push-
                // side change detection (Node F) will surface this as a
                // ConnectionReferenceInsert.
                overlaid = new ConnectionReference.Builder
                {
                    ConnectionReferenceLogicalName = diskRef.ConnectionReferenceLogicalName,
                    ConnectorId = diskRef.ConnectorId,
                }.Build();
            }

            diskOverlays[diskLogicalName] = overlaid;
        }

        // Combine: overlay wins for matched names; cloud-only entries
        // (no disk file) are preserved verbatim. Iteration order:
        // cloud-cache order first (preserves snapshot order), then
        // any disk-only entries appended in disk-enumeration order.
        var result = new List<ConnectionReference>(
            capacity: cloudCacheRefs.Count + diskOverlays.Count);
        var emittedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cr in cloudCacheRefs)
        {
            var name = cr?.ConnectionReferenceLogicalName.Value;
            if (string.IsNullOrEmpty(name))
            {
                // Preserve cloud-cache entries with empty logical names
                // verbatim (they're already weird, but not our problem here).
                result.Add(cr!);
                continue;
            }

            if (diskOverlays.TryGetValue(name!, out var overlaid))
            {
                result.Add(overlaid);
                emittedNames.Add(name!);
            }
            else
            {
                result.Add(cr!);
                emittedNames.Add(name!);
            }
        }

        foreach (var kvp in diskOverlays
                     .Where(kv => !emittedNames.Contains(kv.Key))
                     .OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            // Disk-only refs (no cloud-cache match) are appended in
            // Ordinal logical-name order for determinism (rubber-duck
            // suggestion #6). Disk-enumeration order is FS-implementation
            // dependent (InMemoryFileAccessor preserves insertion order
            // but real FS does not), so sort here to keep the read result
            // stable across hosts.
            result.Add(kvp.Value);
        }

        return result;
    }
}
