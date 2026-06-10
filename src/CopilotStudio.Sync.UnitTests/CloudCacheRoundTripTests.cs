// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

/// <summary>
/// Round-trip verification for <see cref="WorkspaceSynchronizer.WriteCloudCache"/> /
/// <see cref="WorkspaceSynchronizer.ReadCloudCacheSnapshot"/> against pinned CLI-agent and
/// classic-agent baselines.
///
/// Drives branch point B1 of the CliAgentSyncSupport plan: if any pac-written field is
/// dropped or mutated by the shared library's serializer, this test fails and the operator
/// must decide whether to extend OM or patch the shared library.
/// </summary>
public class CloudCacheRoundTripTests
{
    private static readonly AgentFilePath CachePath = new AgentFilePath(".mcs/botdefinition.json");

    private static readonly string TestDataResourcePrefix =
        typeof(CloudCacheRoundTripTests).Assembly.GetName().Name + ".TestData.CliAgentFixtures.";

    private readonly ITestOutputHelper _output;

    public CloudCacheRoundTripTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData("FoodLogger", "cli")]       // Primary CLI fixture (6/8 type-surface targets); B1 gate
    [InlineData("BrandSpecialist", "cli")]  // CLI smoke fixture; B1 gate
    [InlineData("HRAgent", "classic")]      // Classic regression baseline; must not drift
    public void CloudCache_RoundTrip_PreservesAllFields(string fixtureName, string fixtureKind)
    {
        // Step 1: Load original JSON bytes from the embedded fixture resource.
        byte[] originalBytes = LoadFixtureBytes(fixtureName);

        // Step 2: Seed an accessor with the original bytes at the cloud-cache path; read via
        // the shared library's actual read path (the same code clone/pull would invoke).
        var accessor0 = NewAccessor();
        WriteBytes(accessor0, CachePath, originalBytes);
        DefinitionBase originalDef = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor0)
            ?? throw new InvalidOperationException(
                $"ReadCloudCacheSnapshot returned null for fixture {fixtureName}.");

        // Step 3: Write to a fresh accessor and capture first-write bytes.
        var accessor1 = NewAccessor();
        WorkspaceSynchronizer.WriteCloudCache(accessor1, originalDef);
        byte[] firstWriteBytes = accessor1.Files[CachePath.ToString()];

        // Step 4: Round-trip again to verify idempotency (write -> read -> write reaches a fixed point).
        DefinitionBase roundTrippedDef = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor1)
            ?? throw new InvalidOperationException(
                $"ReadCloudCacheSnapshot returned null after re-write for fixture {fixtureName}.");

        var accessor2 = NewAccessor();
        WorkspaceSynchronizer.WriteCloudCache(accessor2, roundTrippedDef);
        byte[] secondWriteBytes = accessor2.Files[CachePath.ToString()];

        // Step 5: Idempotency assertion. If this fails, the serializer is not at a fixed point.
        Assert.True(secondWriteBytes.SequenceEqual(firstWriteBytes),
            $"Fixture {fixtureName} ({fixtureKind}): WriteCloudCache is not idempotent " +
            $"(write1 != write2). Serializer does not reach a fixed point after one round-trip.");

        // Step 6: Compute drop-set (original vs first-write) using path-based structural diff.
        var diff = JsonStructuralDiff.Compute(originalBytes, firstWriteBytes);

        _output.WriteLine(
            $"Fixture {fixtureName} (kind={fixtureKind}): " +
            $"{diff.MissingCount} missing, {diff.MismatchCount} mismatch, {diff.AddedCount} added");
        if (diff.Entries.Count > 0)
        {
            const int maxPrint = 200;
            foreach (var e in diff.Entries.Take(maxPrint))
            {
                _output.WriteLine(
                    $"  [{e.Kind,-8}] {e.Path}   was={e.OriginalValue}   now={e.NewValue}");
            }
            if (diff.Entries.Count > maxPrint)
            {
                _output.WriteLine($"  ... ({diff.Entries.Count - maxPrint} more entries omitted)");
            }
        }

        // Step 7: B1 gate. Missing or Mismatch entries mean pac wrote something the shared
        // library dropped or mutated. Additions (Added) are reported but do not fail this test
        // - default-value emission is a different category of drift and is not B1-blocking.
        int losses = diff.MissingCount + diff.MismatchCount;
        Assert.True(losses == 0,
            $"Fixture {fixtureName} (kind={fixtureKind}): {losses} field(s) lost or mutated by " +
            $"round-trip (Missing={diff.MissingCount}, Mismatch={diff.MismatchCount}). " +
            $"See test output for paths. This is the branch-point B1 signal: any Missing/Mismatch " +
            $"entry means the shared library is dropping fields that pac wrote.");
    }

    private static byte[] LoadFixtureBytes(string fixtureName)
    {
        var resourceName = TestDataResourcePrefix + fixtureName + ".botdefinition.json";
        using var stream = typeof(CloudCacheRoundTripTests).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource not found: {resourceName}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static InMemoryFileAccessor NewAccessor() =>
        new InMemoryFileAccessor(new DirectoryPath("c:/test/round-trip/"));

    private static void WriteBytes(IFileAccessor accessor, AgentFilePath path, byte[] bytes)
    {
        using var s = accessor.OpenWrite(path);
        s.Write(bytes, 0, bytes.Length);
    }
}

/// <summary>
/// Path-based structural diff between two JSON documents. Arrays are matched by stable
/// identity key (<c>schemaName</c>, <c>connectionReferenceLogicalName</c>, <c>id</c>, or
/// <c>name</c>) when every element in both arrays carries a unique value for that key;
/// otherwise compared positionally.
///
/// Three categories of diff entries:
/// <list type="bullet">
///   <item><c>Missing</c>  - path present in original, absent in new (pac wrote, shared lib dropped)</item>
///   <item><c>Mismatch</c> - path present in both, values differ</item>
///   <item><c>Added</c>    - path absent in original, present in new (shared lib emits something pac didn't)</item>
/// </list>
/// </summary>
internal static class JsonStructuralDiff
{
    public sealed record DiffEntry(string Kind, string Path, string OriginalValue, string NewValue);

    public sealed class DiffResult
    {
        public List<DiffEntry> Entries { get; } = new List<DiffEntry>();
        public int MissingCount => Entries.Count(e => e.Kind == "Missing");
        public int MismatchCount => Entries.Count(e => e.Kind == "Mismatch");
        public int AddedCount => Entries.Count(e => e.Kind == "Added");
    }

    // Property names tried (in order) as stable array identity keys.
    private static readonly string[] KeyCandidates =
    {
        "schemaName",
        "connectionReferenceLogicalName",
        "id",
        "name",
    };

    public static DiffResult Compute(byte[] originalBytes, byte[] newBytes)
    {
        var result = new DiffResult();
        using var originalDoc = JsonDocument.Parse(originalBytes);
        using var newDoc = JsonDocument.Parse(newBytes);
        Walk("$", originalDoc.RootElement, newDoc.RootElement, result);
        return result;
    }

    private static void Walk(string path, JsonElement original, JsonElement next, DiffResult result)
    {
        if (original.ValueKind != next.ValueKind)
        {
            result.Entries.Add(new DiffEntry(
                "Mismatch", path, original.ValueKind.ToString(), next.ValueKind.ToString()));
            return;
        }

        switch (original.ValueKind)
        {
            case JsonValueKind.Object:
                WalkObject(path, original, next, result);
                break;
            case JsonValueKind.Array:
                WalkArray(path, original, next, result);
                break;
            case JsonValueKind.String:
                var os = original.GetString() ?? string.Empty;
                var ns = next.GetString() ?? string.Empty;
                if (!string.Equals(os, ns, StringComparison.Ordinal))
                {
                    result.Entries.Add(new DiffEntry("Mismatch", path, Trunc(os), Trunc(ns)));
                }
                break;
            case JsonValueKind.Number:
                var on = original.GetRawText();
                var nn = next.GetRawText();
                if (!string.Equals(on, nn, StringComparison.Ordinal))
                {
                    result.Entries.Add(new DiffEntry("Mismatch", path, on, nn));
                }
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                // ValueKind already matched; no scalar to compare.
                break;
        }
    }

    private static void WalkObject(string path, JsonElement original, JsonElement next, DiffResult result)
    {
        var nextProps = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in next.EnumerateObject())
        {
            nextProps[p.Name] = p.Value;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in original.EnumerateObject())
        {
            seen.Add(p.Name);
            var childPath = JoinPath(path, p.Name);
            if (!nextProps.TryGetValue(p.Name, out var newVal))
            {
                result.Entries.Add(new DiffEntry("Missing", childPath, RawSnippet(p.Value), string.Empty));
                continue;
            }
            Walk(childPath, p.Value, newVal, result);
        }
        foreach (var kvp in nextProps)
        {
            if (seen.Contains(kvp.Key)) { continue; }
            result.Entries.Add(new DiffEntry(
                "Added", JoinPath(path, kvp.Key), string.Empty, RawSnippet(kvp.Value)));
        }
    }

    private static void WalkArray(string path, JsonElement original, JsonElement next, DiffResult result)
    {
        var keyName = FindStableKey(original, next);
        if (keyName != null)
        {
            var originalByKey = IndexByKey(original, keyName);
            var nextByKey = IndexByKey(next, keyName);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kvp in originalByKey)
            {
                seen.Add(kvp.Key);
                var childPath = $"{path}[{keyName}={kvp.Key}]";
                if (!nextByKey.TryGetValue(kvp.Key, out var newVal))
                {
                    result.Entries.Add(new DiffEntry("Missing", childPath, RawSnippet(kvp.Value), string.Empty));
                    continue;
                }
                Walk(childPath, kvp.Value, newVal, result);
            }
            foreach (var kvp in nextByKey)
            {
                if (seen.Contains(kvp.Key)) { continue; }
                result.Entries.Add(new DiffEntry(
                    "Added", $"{path}[{keyName}={kvp.Key}]", string.Empty, RawSnippet(kvp.Value)));
            }
            return;
        }

        // Positional fallback for arrays without a stable identity key.
        var origCount = original.GetArrayLength();
        var nextCount = next.GetArrayLength();
        var max = Math.Max(origCount, nextCount);
        for (int i = 0; i < max; i++)
        {
            var childPath = $"{path}[{i}]";
            if (i >= nextCount)
            {
                result.Entries.Add(new DiffEntry("Missing", childPath, RawSnippet(original[i]), string.Empty));
            }
            else if (i >= origCount)
            {
                result.Entries.Add(new DiffEntry("Added", childPath, string.Empty, RawSnippet(next[i])));
            }
            else
            {
                Walk(childPath, original[i], next[i], result);
            }
        }
    }

    private static string? FindStableKey(JsonElement original, JsonElement next)
    {
        foreach (var candidate in KeyCandidates)
        {
            if (HasUniqueStringKey(original, candidate) && HasUniqueStringKey(next, candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static bool HasUniqueStringKey(JsonElement array, string keyName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var any = false;
        foreach (var item in array.EnumerateArray())
        {
            any = true;
            if (item.ValueKind != JsonValueKind.Object) { return false; }
            if (!item.TryGetProperty(keyName, out var keyVal)) { return false; }
            if (keyVal.ValueKind != JsonValueKind.String) { return false; }
            var key = keyVal.GetString() ?? string.Empty;
            if (!seen.Add(key)) { return false; }
        }
        return any;
    }

    private static Dictionary<string, JsonElement> IndexByKey(JsonElement array, string keyName)
    {
        var d = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            var key = item.GetProperty(keyName).GetString() ?? string.Empty;
            d[key] = item;
        }
        return d;
    }

    private static string JoinPath(string parent, string name) =>
        parent == "$" ? "$." + name : parent + "." + name;

    private static string RawSnippet(JsonElement e) => Trunc(e.GetRawText());

    private static string Trunc(string s)
    {
        const int max = 200;
        if (s.Length <= max) { return s; }
        return s.Substring(0, max) + "...";
    }
}
