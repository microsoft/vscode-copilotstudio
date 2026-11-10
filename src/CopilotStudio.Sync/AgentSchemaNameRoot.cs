// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

internal static class AgentSchemaNameRoot
{
    private static readonly Regex DeclaredSchemaNames = new("^(?<declaration>[ \\t]*(?:-[ \\t]+)?(?:schemaName|defaultSchemaName|bundle|manifestSchemaName)[ \\t]*:[ \\t]*(?<quote>[\"']?))(?<schemaName>[^\"'\\s#]+)(?=\\k<quote>[ \\t]*(?:#[^\\r\\n]*)?\\r?$)", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly string[] LinkFileNames = { ChildAgentLink.LinkFileName, SkillLink.LinkFileName };

    internal static string RootOf(string schemaName) => schemaName.Substring(0, schemaName.IndexOf('.') is var separator && separator > 0 ? separator : schemaName.Length);

    internal static string Rebase(string schemaName, string sourceRoot, string targetRoot) => string.Equals(RootOf(schemaName), sourceRoot, StringComparison.OrdinalIgnoreCase) ? targetRoot + schemaName.Substring(sourceRoot.Length) : schemaName;

    internal static void Rebase(IFileAccessor agentWorkspace, string? sourceRoot, string targetRoot)
    {
        if (sourceRoot is null || string.IsNullOrWhiteSpace(sourceRoot) || string.Equals(sourceRoot, targetRoot, StringComparison.Ordinal))
        {
            return;
        }

        var edits = agentWorkspace.ListFiles().Where(IsDeclaringFile).Select(path => Edit(agentWorkspace, path, sourceRoot, targetRoot)).Where(edit => edit.Content is not null || IsRename(edit)).ToArray();
        var collisions = edits.Where(edit => IsRename(edit) && agentWorkspace.Exists(edit.Target)).Select(edit => new WorkspaceDiagnostic(edit.Source.ToString(), $"Taking the agent's schema name '{targetRoot}' renames this file to '{edit.Target}', which already exists.", 0, 0)).ToImmutableArray();

        if (!collisions.IsEmpty)
        {
            throw new WorkspaceValidationException(collisions);
        }

        foreach (var edit in edits)
        {
            if (edit.Content is not null)
            {
                agentWorkspace.Write(edit.Source, edit.Content);
            }

            if (IsRename(edit))
            {
                agentWorkspace.Replace(edit.Source, edit.Target);
            }
        }
    }

    private static bool IsDeclaringFile(AgentFilePath path) => !SyncStatePaths.IsSyncStatePath(path.ToString()) && (IsLinkFile(path) || (path.FileName.EndsWith(SkillLayout.SidecarExtension, StringComparison.OrdinalIgnoreCase) && !string.Equals(path.ToString(), WorkspaceSynchronizer.ReferencesCollectionPath.ToString(), StringComparison.OrdinalIgnoreCase)));

    private static bool IsLinkFile(AgentFilePath path) => LinkFileNames.Contains(path.FileName, StringComparer.OrdinalIgnoreCase);

    private static bool IsRename(FileEdit edit) => !string.Equals(edit.Source.ToString(), edit.Target.ToString(), StringComparison.OrdinalIgnoreCase);

    private static FileEdit Edit(IFileAccessor agentWorkspace, AgentFilePath path, string sourceRoot, string targetRoot) => IsLinkFile(path) ? new FileEdit(path, path, RebasedLink(SchemaLink.TryRead(agentWorkspace, path), sourceRoot, targetRoot)) : new FileEdit(path, RenamedPath(path, sourceRoot, targetRoot), RebasedDeclarations(agentWorkspace.ReadBytes(path), sourceRoot, targetRoot));

    private static byte[]? RebasedLink(SchemaLinkData? link, string sourceRoot, string targetRoot)
    {
        if (link is null)
        {
            return null;
        }

        var rebased = Rebase(link.SchemaName, sourceRoot, targetRoot);

        return string.Equals(rebased, link.SchemaName, StringComparison.Ordinal) ? null : Encoding.UTF8.GetBytes(SchemaLink.Serialize(new SchemaLinkData { SchemaName = rebased, FolderName = link.FolderName }));
    }

    private static byte[]? RebasedDeclarations(ReadOnlyMemory<byte> content, string sourceRoot, string targetRoot) => FileAccessorExtensions.EditText(content, text => DeclaredSchemaNames.Replace(text, match => match.Groups["declaration"].Value + Rebase(match.Groups["schemaName"].Value, sourceRoot, targetRoot)));

    private static AgentFilePath RenamedPath(AgentFilePath path, string sourceRoot, string targetRoot)
    {
        var stem = path.FileName.Substring(0, path.FileName.Length - SkillLayout.SidecarExtension.Length);
        var rebased = stem.IndexOf('.') > 0 ? Rebase(stem, sourceRoot, targetRoot) : stem;

        return string.Equals(rebased, stem, StringComparison.Ordinal) ? path : new AgentFilePath(path.ToString().Substring(0, path.ToString().Length - path.FileName.Length) + rebased + path.FileName.Substring(stem.Length));
    }

    private readonly record struct FileEdit(AgentFilePath Source, AgentFilePath Target, byte[]? Content);
}
