namespace Microsoft.PowerPlatformLS.Contracts.FileLayout
{
    using System;
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.FileProjection;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;

    /// <summary>
    /// Component path resolver that delegates to <see cref="LspProjectorService"/>.
    /// </summary>
    public sealed class LspComponentPathResolver : IComponentPathResolver
    {
        private static readonly LspProjectorService ProjectorService = LspProjectorService.Instance;

        /// <inheritdoc />
        public string GetComponentPath(BotComponentBase component, DefinitionBase? definition = null)
        {
            var botDefinition = definition as BotDefinition ?? component.ParentOfType<BotDefinition>();
            var collectionDefinition = definition as BotComponentCollectionDefinition ?? component.ParentOfType<BotComponentCollectionDefinition>();

            string? botName = botDefinition?.Entity?.SchemaName.Value ?? collectionDefinition?.GetRootSchemaName();
            string? subAgentFolder = GetSubAgentFolder(component, botDefinition);
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

            var sourceFile = sourceUri.ToFilePath();
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

            var definitionFile = definitionUri.ToFilePath();
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
}
