// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Contracts.FileLayout/Projectors/LspComponentPathResolver.cs

using System.Diagnostics;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.FileProjection;

using Microsoft.CopilotStudio.McsCore;
namespace Microsoft.CopilotStudio.Sync;

/// <summary>
/// Component path resolver that delegates to <see cref="LspProjectorService"/>.
/// </summary>
internal sealed class LspComponentPathResolver : IComponentPathResolver
{
    private static readonly LspProjectorService ProjectorService = LspProjectorService.Instance;

    /// <inheritdoc />
    public string GetComponentPath(BotComponentBase component, DefinitionBase? definition = null)
    {
        var botDefinition = definition as BotDefinition ?? component.ParentOfType<BotDefinition>();
        var collectionDefinition = definition as BotComponentCollectionDefinition ?? component.ParentOfType<BotComponentCollectionDefinition>();

        var botName = botDefinition?.Entity?.SchemaName.Value ?? collectionDefinition?.GetRootSchemaName();
        var subAgentFolder = GetSubAgentFolder(component, botDefinition);
        string? pathWithoutExtension = null;
        if (TryGetRelativePathWithoutExtension(component, definition, out var relativePath))
        {
            pathWithoutExtension = relativePath;
        }

        var context = new ProjectionContext(botName, subAgentFolder, botDefinition);
        var path = ProjectorService.GetFilePath(component, context, pathWithoutExtension);
        if (path == null)
        {
            throw new InvalidOperationException($"Failed to get file path for component type: {component.GetType().Name}.");
        }

        return path;
    }

    private static string? GetSubAgentFolder(BotComponentBase component, BotDefinition? botDefinition)
    {
        if (botDefinition == null || !component.ParentBotComponentId.HasValue)
        {
            return null;
        }

        if (botDefinition.TryGetBotComponentById(component.ParentBotComponentId.Value, out var parent)
            && parent is DialogComponent dialogComponent
            && dialogComponent.RootElement is AgentDialog)
        {
            var agentName = ExtractAgentName(dialogComponent.SchemaNameString ?? string.Empty);
            return $"agents/{agentName}/";
        }

        return null;
    }

    private static bool TryGetRelativePathWithoutExtension(
        BotComponentBase component,
        DefinitionBase? definition,
        out string pathWithoutExtension)
    {
        pathWithoutExtension = string.Empty;

        var sourceUri = component.RootElement?.Syntax?.SourceUri;
        if (sourceUri == null)
        {
            return false;
        }

        if (!TryGetWorkspaceRoot(definition, out var root))
        {
            return false;
        }

        var sourceFile = UriExtensions.ToFilePath(sourceUri);
        if (!root.Contains(sourceFile))
        {
            return false;
        }

        var relative = sourceFile.GetRelativeTo(root);
        var agentFile = new AgentFilePath(relative);
        pathWithoutExtension = agentFile.RemoveExtension().ToString();
        return true;
    }

    private static bool TryGetWorkspaceRoot(DefinitionBase? definition, out DirectoryPath root)
    {
        root = default;

        var definitionUri = definition?.Syntax?.SourceUri;
        if (definitionUri == null)
        {
            return false;
        }

        var definitionFile = UriExtensions.ToFilePath(definitionUri);
        if (definition is BotDefinition)
        {
            var mcsDir = definitionFile.ParentDirectoryPath;
            root = mcsDir.GetParentDirectoryPath();
            return true;
        }

        root = definitionFile.ParentDirectoryPath;
        return true;
    }

    private static string ExtractAgentName(string schemaName)
    {
        var infix = ".agent.";
        var infixIndex = schemaName.IndexOf(infix, StringComparison.OrdinalIgnoreCase);
        if (infixIndex >= 0)
        {
            return schemaName[(infixIndex + infix.Length)..];
        }

        return string.IsNullOrWhiteSpace(schemaName) ? "Unknown" : schemaName;
    }
}

/// <summary>
/// Uri-to-path extension methods (ported from Contracts.Internal.Models.UriExtensions).
/// </summary>
internal static class UriExtensions
{
    private static string ToNormalizedPath(Uri uri)
    {
        // Linux/macOS specific path normalization
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            && uri.LocalPath.StartsWith('/') && uri.Segments.Length > 1)
        {
            return uri.LocalPath;
        }

        return uri.GetComponents(UriComponents.Path, UriFormat.Unescaped);
    }

    public static DirectoryPath ToDirectoryPath(Uri uri)
    {
        var normalizedPath = ToNormalizedPath(uri);
        Debug.Assert(normalizedPath.Length == 0 || System.IO.Path.IsPathRooted(normalizedPath), "LSP methods should provide absolute URI.");
        return new DirectoryPath(normalizedPath);
    }

    public static FilePath ToFilePath(Uri uri)
    {
        var normalizedPath = ToNormalizedPath(uri);
        Debug.Assert(System.IO.Path.IsPathRooted(normalizedPath), "LSP methods should provide absolute URI.");
        return new FilePath(normalizedPath);
    }
}
