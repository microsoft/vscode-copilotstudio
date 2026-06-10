// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Read-side of the CLI agent entity, converged onto the OM serializer (TDD D22).
// The CLI agent identity (displayName/schemaName/language) and BotConfiguration
// (recognizer + agentSettings) live in the language-recognized settings.mcs.yml,
// the same file classic agents use. This reader overlays the on-disk settings onto
// the cloud-cache BotEntity using the OM-native BotEntity.ApplySettingsYamlProperties
// (which preserves cloud-only metadata and lets OM handle $kind discrimination),
// replacing the previous hand-coded agent.yaml YAML/$kind rewrite machinery.
//
// Preserved guarantees from the prior implementation:
//  - HARD-FAIL on malformed settings.mcs.yml. The entity file is the workspace
//    identity manifest; corruption must abort the read rather than silently
//    proceed with cloud-cache identity (which would mask edits).
//  - HARD-FAIL if the on-disk schemaName differs from (or is missing relative to)
//    the cloud schemaName. Renaming an agent re-roots every projected component
//    path, so it is a cloud-side operation that requires a fresh clone.

using System;
using System.IO;
using System.Text;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>
/// Overlays the on-disk <c>settings.mcs.yml</c> identity + configuration subtree
/// onto a cloud-cache <see cref="BotEntity"/> via the OM serializer.
/// </summary>
internal static class CliAgentBotEntityReader
{
    private static readonly AgentFilePath SettingsPath = new AgentFilePath("settings.mcs.yml");
    private static readonly AgentFilePath LayoutMarkerPath = new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName);

    /// <summary>
    /// Reads the <c>layoutVersion</c> from the <c>agent.sync.yaml</c> marker, if present
    /// (TDD D29). Returns <c>null</c> when the marker is absent or malformed.
    /// </summary>
    public static int? TryReadLayoutVersion(IFileAccessor fileAccessor)
    {
        if (fileAccessor == null || !fileAccessor.Exists(LayoutMarkerPath))
        {
            return null;
        }

        try
        {
            using var stream = fileAccessor.OpenRead(LayoutMarkerPath);
            using var sr = new StreamReader(stream, Encoding.UTF8);
            return AgentClassifier.TryParseLayoutVersion(sr.ReadToEnd());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True iff the workspace declares an UNKNOWN-higher <c>layoutVersion</c> than this
    /// tooling supports (TDD D29 evolution contract). Callers on the write/pack path MUST
    /// fail closed: the workspace uses a newer layout the current tooling cannot safely
    /// project/pack.
    /// </summary>
    public static bool HasUnsupportedHigherLayoutVersion(IFileAccessor fileAccessor, out int version)
    {
        version = 0;
        var declared = TryReadLayoutVersion(fileAccessor);
        if (declared.HasValue && declared.Value > AgentClassifier.CurrentLayoutVersion)
        {
            version = declared.Value;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true iff the workspace has the language-recognized
    /// <c>settings.mcs.yml</c> entity on disk. With the CLI agent identity now
    /// stored there (D22), this is the stable "CLI layout adopted" anchor that
    /// gates destructive delete detection - independent of any single component
    /// file, so deleting the only file in a route does not disable the signal.
    /// </summary>
    public static bool IsCliLayoutAdopted(IFileAccessor fileAccessor)
    {
        if (fileAccessor == null)
        {
            return false;
        }
        return fileAccessor.Exists(SettingsPath);
    }

    /// <summary>
    /// Overlay on-disk identity + configuration onto the cloud-cache entity.
    /// Throws <see cref="InvalidOperationException"/> if the file is malformed or
    /// contains a schemaName that is missing or differs from cloud.
    /// </summary>
    public static BotEntity Overlay(IFileAccessor fileAccessor, BotEntity cloudEntity)
    {
        if (fileAccessor == null)
        {
            throw new ArgumentNullException(nameof(fileAccessor));
        }
        if (cloudEntity == null)
        {
            throw new ArgumentNullException(nameof(cloudEntity));
        }

        string yamlText;
        try
        {
            using var stream = fileAccessor.OpenRead(SettingsPath);
            using var sr = new StreamReader(stream, Encoding.UTF8);
            yamlText = sr.ReadToEnd();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"CLI settings.mcs.yml could not be read: {ex.Message}. Aborting read to avoid masking identity changes with cloud-cache values.",
                ex);
        }

        BotEntity? diskEntity;
        try
        {
            diskEntity = CodeSerializer.Deserialize<BotEntity>(yamlText);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"CLI settings.mcs.yml is malformed: {ex.Message}. Aborting read; fix or remove the file to re-establish a clean workspace state.",
                ex);
        }

        if (diskEntity == null)
        {
            throw new InvalidOperationException(
                "CLI settings.mcs.yml did not deserialize to a BotEntity. Aborting read; fix or re-clone the agent.");
        }

        // Hard-fail on missing/empty schemaName: settings.mcs.yml is the identity
        // manifest, and silently falling back to cloud's schemaName would mask a
        // user-introduced corruption (e.g., the user deleted the line).
        var diskSchemaName = diskEntity.SchemaName.Value;
        if (string.IsNullOrEmpty(diskSchemaName))
        {
            throw new InvalidOperationException(
                "CLI settings.mcs.yml is missing a 'schemaName' value. settings.mcs.yml is the workspace identity manifest; restore the schemaName or re-clone the agent.");
        }

        var cloudSchemaName = cloudEntity.SchemaName.Value;
        if (!string.Equals(diskSchemaName, cloudSchemaName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"CLI settings.mcs.yml schemaName '{diskSchemaName}' does not match cloud schemaName '{cloudSchemaName}'. " +
                "Renaming an agent is a cloud-side operation; restore the schemaName in settings.mcs.yml or re-clone the agent.");
        }

        // OM-native overlay: start from the on-disk settings (identity + recognizer
        // + agentSettings) and layer the cloud-only metadata back on.
        return cloudEntity.ApplySettingsYamlProperties(diskEntity);
    }
}
