namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.FileProjection;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Exceptions;
    using System;
    using System.Security.Cryptography;
    using System.Text;

    internal class McsFileParser : IMcsFileParser
    {
        private readonly LspProjectorService _projectorService = LspProjectorService.Instance;

        /// <summary>
        /// This is not a real path and is used when compiling model from a virtual source.
        /// </summary>
        private static readonly AgentFilePath VirtualPath = new AgentFilePath(".mcs/virtual_content_model");

        // If this is in a /agent/ folder, then we need a component Id
        // to associate ita s a child-agent component.
        private static Guid GetSubAgentComponentId(AgentFilePath filePath)
        {
            if (filePath.TryGetSubAgentName(out var agentName, out _))
            {
                return HashStringToGuid(agentName);
            }

            return Guid.Empty;
        }

        // Only the cloud has real componentIds.
        // Fabricate a guid from the schema name.
        // We just need this locally when creating compilations. 
        private static Guid HashStringToGuid(string input)
        {
            // Used only a single time within this class.
            using MD5 md5 = MD5.Create(); // CodeQL [SM02196] False Positive: Not used for security purposes
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            Guid result = new Guid(hash);

            return result;
        }

        private (BotComponentBase? component, Exception? error) InternalCompileFile(
           AgentFilePath relativePath,
           string schemaName,
           BotElement? fileModel)
        {
            var subAgentId = GetSubAgentComponentId(relativePath);
            BotComponentBase component;

            if (fileModel == null)
            {
                // NOTE: InvalidDataException would be more precise for null models, but we keep
                // UnsupportedBotElementException to preserve legacy, user-visible behavior during refactor.
                return (null, new UnsupportedBotElementException("Unhandled data type", fileModel));
            }

            // Handle elements that are not components or agent files
            if (fileModel is BotEntity or DefinitionBase or BotComponentCollection or 
                ReferencesSourceFile or ConnectionReferencesSourceFile)
            {
                return (null, null);
            }

            // Look up projector for component creation
            var projector = _projectorService.GetProjector(fileModel.GetType(), relativePath.ToString());

            if (projector != null)
            {
                // Normalize element (e.g., wrap KnowledgeSource in KnowledgeSourceConfiguration)
                var normalizedModel = _projectorService.NormalizeElement(fileModel);

                var (displayName, description) = GetDisplayNameAndDescription(fileModel, schemaName);
                component = projector.CreateComponent(
                    normalizedModel,
                    schemaName,
                    subAgentId,
                    displayName,
                    description);
            }
            else
            {
                return (null, new UnsupportedBotElementException("Unhandled data type", fileModel));
            }

            // Validate.
            if (component.SchemaNameString != schemaName)
            {
                throw new InvalidOperationException($"SchemaName not set property: {schemaName}");
            }

            return (component, null);
        }

        public (BotComponentBase? component, Exception? error) CompileFileModel(string schemaName, BotElement? model)
        {
            return InternalCompileFile(VirtualPath, schemaName, model);
        }

        public (BotComponentBase? component, Exception? error) CompileFile(
            LspDocument<BotElement> document,
            ProjectionContext context)
        {
            BotElement? fileModel = document.FileModel;
            var relativePath = document.As<McsLspDocument>().RelativePath;

            if (fileModel == null)
            {
                return (null, new InvalidDataException($"File model is null for {relativePath}"));
            }

            // Derive schema name using projector or element's SchemaName property
            var schemaName = DeriveSchemaName(fileModel, relativePath, context);

            if (schemaName == null)
            {
                return (null, new UnsupportedBotElementException($"Can't get schema", fileModel));
            }

            try
            {
                return InternalCompileFile(relativePath, schemaName, fileModel);
            }
            catch (Exception ex)
            {
                return (null, ex);
            }
        }

        private static (string displayName, string description) GetDisplayNameAndDescription(BotElement? fileModel, string schemaName)
        {
            var fallbackName = !string.IsNullOrWhiteSpace(schemaName) ? schemaName.Split('.').Last() : string.Empty;
            if (fileModel == null)
            {
                return (fallbackName, fallbackName);
            }

            CodeSerializer.ParseYamlHeader(fileModel, out var displayName, out var description);
            displayName = string.IsNullOrWhiteSpace(displayName) ? fallbackName : displayName;
            description = string.IsNullOrWhiteSpace(description) ? fallbackName : description;

            return (displayName, description);
        }

        /// <summary>
        /// Derives schema name from file model using projector or element's SchemaName property.
        /// </summary>
        private string? DeriveSchemaName(BotElement fileModel, AgentFilePath relativePath, ProjectionContext context)
        {
            // Handle elements that have their own SchemaName property
            if (fileModel is BotEntity entity)
            {
                return entity.SchemaName.ToString();
            }

            if (fileModel is BotComponentCollection collection)
            {
                return collection.SchemaName.ToString();
            }

            // Handle agent files and non-component elements that return empty schema name
            if (fileModel is BotDefinition or BotComponentCollectionDefinition or UnknownBotElement
                or ReferencesSourceFile or ConnectionReferencesSourceFile)
            {
                return string.Empty;
            }

            // Use the projector service for schema name derivation (includes legacy special cases)
            var pathWithoutExtension = relativePath.RemoveExtension().ToString();
            return _projectorService.GetSchemaName(pathWithoutExtension, context.BotName, fileModel.GetType());
        }
    }
}
