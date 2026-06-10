// Copyright (C) Microsoft Corporation. All rights reserved.
//
// CliAgentSyncSupport / Node E — tests for the CLI agent connection-
// references reader (infrastructure/connections/{name}.sync.yaml inverse of
// the D5 writer). Validates:
//   - Activation-gate rule: layered shape active iff ≥1 direct *.sync.yaml child.
//   - Field-provenance overlay: cloud-cache snapshot preserves Id /
//     ConnectionId / DisplayName etc.; disk supplies the two emitted
//     fields (ConnectionReferenceLogicalName, ConnectorId).
//   - Skip-and-warn behaviors for per-file errors (malformed, multi-item,
//     filename/body mismatch, IO errors).
//   - Conservative merge: cloud-only entries (no disk file) preserved
//     verbatim; new-on-disk entries (no cloud match) emitted with only
//     the two disk fields.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class CliAgentConnectionsReaderTests
{
    private const string FoodLoggerDataverseLogicalName =
        "new_sharedcommondataserviceforapps_6480c125";
    private const string FoodLoggerSharePointLogicalName =
        "Default_draft_rxzs_q.cr.shared_workiqsharepoint.020c62e5181149bc8c90e45269f66dce";

    private static readonly AgentFilePath CachePath = new AgentFilePath(".mcs/botdefinition.json");

    private static readonly string TestDataResourcePrefix =
        typeof(CliAgentConnectionsReaderTests).Assembly.GetName().Name + ".TestData.CliAgentFixtures.";

    private readonly ITestOutputHelper _output;

    public CliAgentConnectionsReaderTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // --- IsLayeredShapeActive --------------------------------------------------------

    [Fact]
    public void IsLayeredShapeActive_NoDirectory_ReturnsFalse()
    {
        var accessor = NewAccessor();
        Assert.False(CliAgentConnectionsReader.IsLayeredShapeActive(accessor));
    }

    [Fact]
    public void IsLayeredShapeActive_OnlyNestedChildren_ReturnsFalse()
    {
        // Strict-parent rule: a file in a subdirectory under
        // infrastructure/connections/ does not count toward "active".
        // (Matches the writer's strict-parent prune scope.)
        var accessor = NewAccessor();
        WriteBytes(accessor, new AgentFilePath("infrastructure/connections/sub/nested.sync.yaml"),
            Encoding.UTF8.GetBytes("connectionReferences:\n  - connectionReferenceLogicalName: nested\n    connectorId: x\n"));

        Assert.False(CliAgentConnectionsReader.IsLayeredShapeActive(accessor));
    }

    [Fact]
    public void IsLayeredShapeActive_OneDirectChild_ReturnsTrue()
    {
        var accessor = NewAccessor();
        WriteBytes(accessor, new AgentFilePath("infrastructure/connections/foo.sync.yaml"),
            Encoding.UTF8.GetBytes("connectionReferences:\n  - connectionReferenceLogicalName: foo\n    connectorId: x\n"));

        Assert.True(CliAgentConnectionsReader.IsLayeredShapeActive(accessor));
    }

    [Fact]
    public void IsLayeredShapeActive_FoodLoggerSeed_TrueAfterWriterPopulates()
    {
        // Round-trip the activation signal: write FoodLogger refs via the
        // writer; the reader's activation gate must observe them.
        var (_, definition) = LoadFixtureBotAndDefinition("FoodLogger");
        Assert.Equal(2, definition.ConnectionReferences.Length);

        var accessor = NewAccessor();
        CliAgentConnectionsWriter.WriteAll(
            accessor, definition.ConnectionReferences, null, CancellationToken.None);

        Assert.True(CliAgentConnectionsReader.IsLayeredShapeActive(accessor));
    }

    // --- Overlay: positive paths -----------------------------------------------------

    [Fact]
    public void Overlay_FoodLoggerBothRefs_FieldsPreserved_BytePerfectViaWriter()
    {
        // End-to-end: writer projects cloud-cache refs to disk; reader
        // overlays them back; the reconstructed collection must equal
        // the input (no field loss).
        var (_, definition) = LoadFixtureBotAndDefinition("FoodLogger");
        var inputRefs = definition.ConnectionReferences;

        var accessor = NewAccessor();
        CliAgentConnectionsWriter.WriteAll(
            accessor, inputRefs, null, CancellationToken.None);

        var overlaid = CliAgentConnectionsReader.Overlay(
            accessor, inputRefs, null, CancellationToken.None);

        Assert.Equal(inputRefs.Length, overlaid.Count);

        foreach (var original in inputRefs)
        {
            var match = overlaid.Single(o => o.ConnectionReferenceLogicalName.Value
                                             == original.ConnectionReferenceLogicalName.Value);
            Assert.Equal(original.ConnectorId.Value, match.ConnectorId.Value);
            Assert.Equal(original.Id.Value, match.Id.Value);
            Assert.Equal(original.ConnectionId, match.ConnectionId);
            Assert.Equal(original.CustomConnectorId, match.CustomConnectorId);
            Assert.Equal(original.DisplayName, match.DisplayName);
            Assert.Equal(original.SharedConnectionParameters, match.SharedConnectionParameters);
        }
    }

    [Fact]
    public void Overlay_DiskHasOnlyOneOfTwo_OtherPreservedFromCloudCache()
    {
        // Conservative merge rule: cloud-cache entry without a corresponding
        // disk file is preserved verbatim in the overlay result. (This is
        // the property that lets the activation gate tolerate partial
        // states without silently dropping fields.)
        var (_, definition) = LoadFixtureBotAndDefinition("FoodLogger");
        var inputRefs = definition.ConnectionReferences;
        Assert.Equal(2, inputRefs.Length);

        var dataverseOnly = inputRefs
            .Where(cr => cr.ConnectionReferenceLogicalName.Value == FoodLoggerDataverseLogicalName)
            .ToList();
        Assert.Single(dataverseOnly);

        var accessor = NewAccessor();
        CliAgentConnectionsWriter.WriteAll(
            accessor, dataverseOnly, null, CancellationToken.None);

        var overlaid = CliAgentConnectionsReader.Overlay(
            accessor, inputRefs, null, CancellationToken.None);

        // Both refs returned: dataverse from disk overlay, SharePoint
        // preserved verbatim from cloud cache (had no file).
        Assert.Equal(2, overlaid.Count);
        Assert.Contains(overlaid,
            cr => cr.ConnectionReferenceLogicalName.Value == FoodLoggerDataverseLogicalName);
        Assert.Contains(overlaid,
            cr => cr.ConnectionReferenceLogicalName.Value == FoodLoggerSharePointLogicalName);
    }

    [Fact]
    public void Overlay_DiskHasNewRefNotInCloud_AddedWithDiskFieldsOnly()
    {
        // New-on-disk: present in infrastructure/connections/ but absent
        // from cloud-cache. Emit with the two disk fields only; push-
        // side (Node F) will surface this as a new reference.
        var newCr = MakeSyntheticConnectionReference(
            logicalName: "fresh_disk_only",
            connectorId: "/providers/Microsoft.PowerApps/apis/shared_fresh");

        var accessor = NewAccessor();
        CliAgentConnectionsWriter.WriteAll(
            accessor, new[] { newCr }, null, CancellationToken.None);

        var overlaid = CliAgentConnectionsReader.Overlay(
            accessor,
            cloudCacheRefs: Array.Empty<ConnectionReference>(),
            reportWarning: null,
            cancellationToken: CancellationToken.None);

        var match = Assert.Single(overlaid);
        Assert.Equal("fresh_disk_only", match.ConnectionReferenceLogicalName.Value);
        Assert.Equal("/providers/Microsoft.PowerApps/apis/shared_fresh", match.ConnectorId.Value);
    }

    [Fact]
    public void Overlay_NoDiskFiles_EmptyDirectory_CloudCachePreservedVerbatim()
    {
        var (_, definition) = LoadFixtureBotAndDefinition("FoodLogger");
        var inputRefs = definition.ConnectionReferences;

        // No writer call → no disk files.
        var accessor = NewAccessor();

        var overlaid = CliAgentConnectionsReader.Overlay(
            accessor, inputRefs, null, CancellationToken.None);

        Assert.Equal(inputRefs.Length, overlaid.Count);
        foreach (var original in inputRefs)
        {
            Assert.Contains(overlaid,
                cr => cr.ConnectionReferenceLogicalName.Value == original.ConnectionReferenceLogicalName.Value);
        }
    }

    [Fact]
    public void Overlay_NullCloudCache_OnlyDiskRefsReturned()
    {
        var newCr = MakeSyntheticConnectionReference(
            logicalName: "disk_only",
            connectorId: "/providers/Microsoft.PowerApps/apis/shared_x");

        var accessor = NewAccessor();
        CliAgentConnectionsWriter.WriteAll(
            accessor, new[] { newCr }, null, CancellationToken.None);

        var overlaid = CliAgentConnectionsReader.Overlay(
            accessor, cloudCacheRefs: null, reportWarning: null, cancellationToken: CancellationToken.None);

        var match = Assert.Single(overlaid);
        Assert.Equal("disk_only", match.ConnectionReferenceLogicalName.Value);
    }

    // --- Overlay: skip-and-warn behaviors --------------------------------------------

    [Fact]
    public void Overlay_MalformedYamlFile_WarnAndKeepCloudCache()
    {
        var (_, definition) = LoadFixtureBotAndDefinition("FoodLogger");
        var inputRefs = definition.ConnectionReferences;

        var accessor = NewAccessor();
        // Pre-seed a malformed file matching one of the cloud-cache refs.
        WriteBytes(accessor,
            new AgentFilePath($"infrastructure/connections/{FoodLoggerDataverseLogicalName}.sync.yaml"),
            Encoding.UTF8.GetBytes("this is not valid yaml: : :\n   bad indent\n"));

        var warnings = new List<string>();
        var overlaid = CliAgentConnectionsReader.Overlay(
            accessor, inputRefs, warnings.Add, CancellationToken.None);

        // Cloud-cache entry preserved verbatim.
        Assert.Equal(inputRefs.Length, overlaid.Count);
        var preserved = overlaid.Single(o => o.ConnectionReferenceLogicalName.Value == FoodLoggerDataverseLogicalName);
        var original = inputRefs.Single(o => o.ConnectionReferenceLogicalName.Value == FoodLoggerDataverseLogicalName);
        Assert.Equal(original.ConnectorId.Value, preserved.ConnectorId.Value);

        Assert.Contains(warnings,
            w => w.Contains(FoodLoggerDataverseLogicalName, StringComparison.Ordinal)
                 && (w.Contains("parsed", StringComparison.OrdinalIgnoreCase)
                     || w.Contains("Skipping", StringComparison.OrdinalIgnoreCase)
                     || w.Contains("Keeping cloud-cache version", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Overlay_FileWithMultipleItems_WarnAndSkip()
    {
        var accessor = NewAccessor();
        // Two items in one file — D5 writer emits 1-item slices; multi-
        // item is malformed.
        var content = "connectionReferences:\n"
                      + "  - connectionReferenceLogicalName: alpha\n"
                      + "    connectorId: /providers/x/apis/a\n"
                      + "  - connectionReferenceLogicalName: alpha\n"
                      + "    connectorId: /providers/x/apis/b\n";
        WriteBytes(accessor,
            new AgentFilePath("infrastructure/connections/alpha.sync.yaml"),
            Encoding.UTF8.GetBytes(content));

        var warnings = new List<string>();
        var overlaid = CliAgentConnectionsReader.Overlay(
            accessor,
            cloudCacheRefs: Array.Empty<ConnectionReference>(),
            reportWarning: warnings.Add,
            cancellationToken: CancellationToken.None);

        Assert.Empty(overlaid);
        Assert.Contains(warnings,
            w => w.Contains("exactly one", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Overlay_FilenameBodyMismatch_WarnAndSkip()
    {
        var accessor = NewAccessor();
        // Filename says "foo", body says "bar".
        var content = "connectionReferences:\n"
                      + "  - connectionReferenceLogicalName: bar\n"
                      + "    connectorId: /providers/x/apis/y\n";
        WriteBytes(accessor,
            new AgentFilePath("infrastructure/connections/foo.sync.yaml"),
            Encoding.UTF8.GetBytes(content));

        var warnings = new List<string>();
        var overlaid = CliAgentConnectionsReader.Overlay(
            accessor,
            cloudCacheRefs: Array.Empty<ConnectionReference>(),
            reportWarning: warnings.Add,
            cancellationToken: CancellationToken.None);

        Assert.Empty(overlaid);
        Assert.Contains(warnings,
            w => w.Contains("does not match filename leaf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Overlay_FileInSubdirectory_IgnoredSilently()
    {
        // Strict-parent filter: a file in a subdirectory must not be
        // overlaid (mirrors writer scope).
        var accessor = NewAccessor();
        var content = "connectionReferences:\n"
                      + "  - connectionReferenceLogicalName: nested\n"
                      + "    connectorId: /providers/x/apis/y\n";
        WriteBytes(accessor,
            new AgentFilePath("infrastructure/connections/sub/nested.sync.yaml"),
            Encoding.UTF8.GetBytes(content));

        var warnings = new List<string>();
        var overlaid = CliAgentConnectionsReader.Overlay(
            accessor,
            cloudCacheRefs: Array.Empty<ConnectionReference>(),
            reportWarning: warnings.Add,
            cancellationToken: CancellationToken.None);

        Assert.Empty(overlaid);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Overlay_OneRefValid_OnePeerBad_ValidIsPreservedAndBadSkipped()
    {
        // Resilience: a single corrupt file must not abort overlay for
        // its peers.
        var (_, definition) = LoadFixtureBotAndDefinition("FoodLogger");
        var inputRefs = definition.ConnectionReferences;

        var accessor = NewAccessor();
        // Valid Dataverse file (one of the 2 cloud-cache refs).
        var dataverseRef = inputRefs.Single(
            cr => cr.ConnectionReferenceLogicalName.Value == FoodLoggerDataverseLogicalName);
        CliAgentConnectionsWriter.WriteAll(
            accessor, new[] { dataverseRef }, null, CancellationToken.None);

        // Pre-seed a malformed peer file matching the OTHER cloud-cache ref.
        WriteBytes(accessor,
            new AgentFilePath($"infrastructure/connections/{FoodLoggerSharePointLogicalName}.sync.yaml"),
            Encoding.UTF8.GetBytes("totally: not: valid: yaml: nesting\n"));

        var warnings = new List<string>();
        var overlaid = CliAgentConnectionsReader.Overlay(
            accessor, inputRefs, warnings.Add, CancellationToken.None);

        // Both refs in result; SharePoint preserved verbatim from cloud cache.
        Assert.Equal(2, overlaid.Count);
        var sharePoint = overlaid.Single(o => o.ConnectionReferenceLogicalName.Value == FoodLoggerSharePointLogicalName);
        var sharePointCloud = inputRefs.Single(o => o.ConnectionReferenceLogicalName.Value == FoodLoggerSharePointLogicalName);
        Assert.Equal(sharePointCloud.Id.Value, sharePoint.Id.Value);
        Assert.Equal(sharePointCloud.ConnectorId.Value, sharePoint.ConnectorId.Value);
        Assert.NotEmpty(warnings);
    }

    // --- Helpers ---------------------------------------------------------------------

    private static (BotEntity entity, BotDefinition definition) LoadFixtureBotAndDefinition(string fixtureName)
    {
        var bytes = LoadFixtureBytes(fixtureName);
        var accessor = NewAccessor();
        using (var s = accessor.OpenWrite(CachePath))
        {
            s.Write(bytes, 0, bytes.Length);
        }
        var def = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)
                  ?? throw new InvalidOperationException($"ReadCloudCacheSnapshot returned null for {fixtureName}.");
        var botDef = (BotDefinition)def;
        return (botDef.Entity!, botDef);
    }

    private static byte[] LoadFixtureBytes(string fixtureName)
    {
        var resourceName = TestDataResourcePrefix + fixtureName + ".botdefinition.json";
        using var stream = typeof(CliAgentConnectionsReaderTests).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource not found: {resourceName}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static InMemoryFileAccessor NewAccessor() =>
        new InMemoryFileAccessor(new DirectoryPath($"c:/test/connections-reader-{Guid.NewGuid():N}/"));

    private static void WriteBytes(IFileAccessor accessor, AgentFilePath path, byte[] bytes)
    {
        using var stream = accessor.OpenWrite(path);
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// Build a minimal ConnectionReference via the JSON round-trip path,
    /// so the typed object goes through the same construction route as a
    /// real cloud-cache read.
    /// </summary>
    private static ConnectionReference MakeSyntheticConnectionReference(
        string logicalName,
        string connectorId)
    {
        var json = $$"""
        {
          "$kind": "BotDefinition",
          "connectionReferences": [
            {
              "$kind": "ConnectionReference",
              "connectionReferenceLogicalName": {{JsonSerializer.Serialize(logicalName)}},
              "connectorId": {{JsonSerializer.Serialize(connectorId)}}
            }
          ]
        }
        """;

        var bytes = Encoding.UTF8.GetBytes(json);
        var accessor = NewAccessor();
        using (var s = accessor.OpenWrite(CachePath))
        {
            s.Write(bytes, 0, bytes.Length);
        }
        var def = (BotDefinition)WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;
        return def.ConnectionReferences.Single();
    }
}
