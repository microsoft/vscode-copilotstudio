// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>The on-disk projection a workspace uses for its connection references.</summary>
public enum ConnectionReferenceLayout
{
    /// <summary>A single file at the workspace root holding every reference.</summary>
    Flat = 0,

    /// <summary>One file per reference beneath the infrastructure folder.</summary>
    Layered = 1,
}

/// <summary>Reads and writes a workspace's connection references in either on-disk layout.</summary>
public static class ConnectionReferenceStore
{
    /// <summary>The file the flat layout declares every reference in.</summary>
    public const string FlatFileName = "connectionreferences.mcs.yml";

    /// <summary>The folder the layered layout writes one file per reference into.</summary>
    public const string LayeredFolderName = CliAgentConnectionsWriter.InfrastructureConnectionsFolder;

    /// <summary>The extension the layered layout gives each reference file.</summary>
    public const string LayeredFileExtension = CliAgentConnectionsWriter.FileExtension;

    internal static readonly AgentFilePath FlatPath = new AgentFilePath(FlatFileName);

    private static readonly Encoding FlatEncoding = Encoding.UTF8;

    private static readonly Encoding LayeredEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Drops references without a logical name and keeps the last of each duplicated name.</summary>
    /// <param name="connectionReferences">The references to reduce, or null for none.</param>
    /// <returns>One reference per logical name, compared without case.</returns>
    public static IReadOnlyList<ConnectionReference> Deduplicate(IEnumerable<ConnectionReference>? connectionReferences) =>
        (connectionReferences ?? Array.Empty<ConnectionReference>())
            .Where(reference => reference is not null && !string.IsNullOrEmpty(reference.ConnectionReferenceLogicalName.Value))
            .GroupBy(reference => reference.ConnectionReferenceLogicalName.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();

    /// <summary>The layout a workspace already declares on disk, or <paramref name="fallback"/> when it declares none.</summary>
    /// <param name="fileAccessor">The workspace to inspect.</param>
    /// <param name="fallback">The layout to assume when the workspace holds no reference files.</param>
    /// <returns>The layout later writes should use.</returns>
    public static ConnectionReferenceLayout Detect(IFileAccessor fileAccessor, ConnectionReferenceLayout fallback)
    {
        if (fileAccessor is null)
        {
            throw new ArgumentNullException(nameof(fileAccessor));
        }

        if (CliAgentConnectionsReader.IsLayeredShapeActive(fileAccessor))
        {
            return ConnectionReferenceLayout.Layered;
        }

        return fileAccessor.Exists(FlatPath) ? ConnectionReferenceLayout.Flat : fallback;
    }

    /// <summary>Every connection reference a workspace declares, in either layout.</summary>
    /// <param name="fileAccessor">The workspace to read.</param>
    /// <param name="reportWarning">Receives a message for each file that could not be read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One reference per logical name, with the layered shape winning any overlap.</returns>
    public static IReadOnlyList<ConnectionReference> Read(IFileAccessor fileAccessor, Action<string>? reportWarning, CancellationToken cancellationToken)
    {
        if (fileAccessor is null)
        {
            throw new ArgumentNullException(nameof(fileAccessor));
        }

        var declared = new List<ConnectionReference>(ReadFlat(fileAccessor, reportWarning));
        declared.AddRange(CliAgentConnectionsReader.Overlay(fileAccessor, Array.Empty<ConnectionReference>(), reportWarning, cancellationToken));

        return Deduplicate(declared);
    }

    /// <summary>Writes a workspace's connection references in one layout and removes whatever the other layout left behind.</summary>
    /// <param name="fileAccessor">The workspace to write.</param>
    /// <param name="connectionReferences">The references to declare, or null for none.</param>
    /// <param name="layout">The layout to write.</param>
    /// <param name="reportWarning">Receives a message for each reference that could not be projected.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when anything on disk changed.</returns>
    public static bool Write(
        IFileAccessor fileAccessor,
        IEnumerable<ConnectionReference>? connectionReferences,
        ConnectionReferenceLayout layout,
        Action<string>? reportWarning,
        CancellationToken cancellationToken)
    {
        if (fileAccessor is null)
        {
            throw new ArgumentNullException(nameof(fileAccessor));
        }

        var unique = Deduplicate(connectionReferences);

        return layout == ConnectionReferenceLayout.Layered
            ? WriteLayered(fileAccessor, unique, reportWarning, cancellationToken)
            : WriteFlat(fileAccessor, unique, cancellationToken);
    }

    /// <summary>Rewrites whatever a workspace declares into the requested layout and removes the other.</summary>
    /// <param name="fileAccessor">The workspace to convert.</param>
    /// <param name="layout">The layout to convert to.</param>
    /// <param name="reportWarning">Receives a message for each file that could not be converted.</param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    /// <returns><see langword="true"/> when anything on disk changed.</returns>
    public static bool Migrate(IFileAccessor fileAccessor, ConnectionReferenceLayout layout, Action<string>? reportWarning, CancellationToken cancellationToken) =>
        Write(fileAccessor, Read(fileAccessor, reportWarning, cancellationToken), layout, reportWarning, cancellationToken);

    internal static string Serialize(IReadOnlyList<ConnectionReference> connectionReferences)
    {
        using var writer = new StringWriter();

        using (YamlSerializationContext.UseStandardSerializationContextIfNotDefined(throwOnInvalidYaml: false))
        {
            CodeSerializer.SerializeConnectionReferences(writer, connectionReferences);
        }

        return writer.ToString();
    }

    internal static bool WriteIfChanged(IFileAccessor fileAccessor, AgentFilePath path, string content, Encoding encoding)
    {
        if (fileAccessor.Exists(path) && string.Equals(ReadTextOrNull(fileAccessor, path), content, StringComparison.Ordinal))
        {
            return false;
        }

        using var stream = fileAccessor.OpenWrite(path);
        using var writer = new StreamWriter(stream, encoding);

        writer.Write(content);

        return true;
    }

    internal static bool WriteLayeredFile(IFileAccessor fileAccessor, ConnectionReference connectionReference, AgentFilePath path) =>
        WriteIfChanged(fileAccessor, path, Serialize(new[] { connectionReference }), LayeredEncoding);

    private static bool WriteLayered(IFileAccessor fileAccessor, IReadOnlyList<ConnectionReference> unique, Action<string>? reportWarning, CancellationToken cancellationToken)
    {
        var changed = CliAgentConnectionsWriter.WriteAll(fileAccessor, unique, reportWarning, cancellationToken);

        return RemoveFlat(fileAccessor) || changed;
    }

    private static bool WriteFlat(IFileAccessor fileAccessor, IReadOnlyList<ConnectionReference> unique, CancellationToken cancellationToken)
    {
        var changed = unique.Count == 0
            ? RemoveFlat(fileAccessor)
            : WriteIfChanged(fileAccessor, FlatPath, Serialize(unique), FlatEncoding);

        return RemoveLayered(fileAccessor, cancellationToken) || changed;
    }

    private static bool RemoveFlat(IFileAccessor fileAccessor)
    {
        if (!fileAccessor.Exists(FlatPath))
        {
            return false;
        }

        fileAccessor.Delete(FlatPath);

        return true;
    }

    private static bool RemoveLayered(IFileAccessor fileAccessor, CancellationToken cancellationToken) =>
        CliAgentConnectionsReader.IsLayeredShapeActive(fileAccessor)
        && CliAgentConnectionsWriter.WriteAll(fileAccessor, Array.Empty<ConnectionReference>(), null, cancellationToken);

    private static IReadOnlyList<ConnectionReference> ReadFlat(IFileAccessor fileAccessor, Action<string>? reportWarning)
    {
        if (!fileAccessor.Exists(FlatPath))
        {
            return Array.Empty<ConnectionReference>();
        }

        try
        {
            using var context = YamlSerializationContext.UseStandardSerializationContextIfNotDefined(throwOnInvalidYaml: false);

            var parsed = CodeSerializer.Deserialize<ConnectionReferencesSourceFile>(ReadTextOrNull(fileAccessor, FlatPath) ?? string.Empty, sourceUri: null);

            return parsed is null || parsed.ConnectionReferences.IsDefaultOrEmpty ? Array.Empty<ConnectionReference>() : parsed.ConnectionReferences.ToList();
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            reportWarning?.Invoke($"Connection reference file '{FlatFileName}' could not be read: {failure.Message}. Keeping it as it is.");

            return Array.Empty<ConnectionReference>();
        }
    }

    internal static string? ReadTextOrNull(IFileAccessor fileAccessor, AgentFilePath path)
    {
        if (!fileAccessor.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = fileAccessor.OpenRead(path);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
    }
}
