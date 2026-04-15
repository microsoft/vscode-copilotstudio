namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Abstractions;
    using Microsoft.Agents.ObjectModel.FileProjection;
    using Microsoft.Agents.ObjectModel.PowerFx;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Exceptions;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Utilities;
    using System;
    using System.Collections.Generic;

    internal class McsWorkspaceCompiler : IWorkspaceCompiler<DefinitionBase>
    {
        private readonly PowerFxExpressionChecker _expressionChecker;
        private readonly IClientInformation _clientInfo;
        private readonly IMcsFileParser _fileParser;
        private readonly IAgentFilesAnalyzer _fileReader;
        private readonly IFeatureConfiguration _featureConfiguration;
        private readonly ILspLogger _logger;
        private readonly IReferenceResolver _referenceResolver;

        public McsWorkspaceCompiler(ILspLogger logger, IFeatureConfiguration featureConfiguration, IClientInformation clientInfo, IMcsFileParser fileParser, IAgentFilesAnalyzer fileReader, IReferenceResolver referenceResolver)
        {
            _logger = logger;
            _featureConfiguration = featureConfiguration;
            _expressionChecker = new(featureConfiguration);
            _clientInfo = clientInfo;
            _fileParser = fileParser;
            _fileReader = fileReader;
            _referenceResolver = referenceResolver;
        }

        public Compilation<DefinitionBase> Compile(IReadOnlyDictionary<FilePath, LspDocument> documents, DirectoryPath workspacePath, bool isFull = false)
        {
            var errors = new Dictionary<LspDocument, IEnumerable<Exception>>();
            var (bot, validateAcrossComponents) = CompileDefinition(documents, workspacePath, errors);
            bot = bot.ValidateFromMcsWorkspace(validateAcrossComponents, _expressionChecker, _featureConfiguration);
            return new(bot, errors);
        }

        // workspacePath is needed to resolve relative references. 
        private (DefinitionBase, bool) CompileDefinition(IReadOnlyDictionary<FilePath, LspDocument> documents, DirectoryPath workspacePath, Dictionary<LspDocument, IEnumerable<Exception>> errors)
        {
            var components = new List<BotComponentBase>();
            var environmentVariables = new List<EnvironmentVariableDefinition>();

            var baseDefinition = FirstOrDefaultDocumentOfType<DefinitionBase>();
            var settings = FirstOrDefaultDocumentOfType<BotEntity>();
            var componentCollection = FirstOrDefaultDocumentOfType<BotComponentCollection>();

            (var referencesDoc, var references) = FirstOrDefaultLspDocumentOfType<ReferencesSourceFile>();
            (var connectionReferencesDoc, var connectionReferencesFile) = FirstOrDefaultLspDocumentOfType<ConnectionReferencesSourceFile>();

            // BotEntitySchemaName? schemaPrefix;
            ProjectionContext projectionContext;
            if (settings != null)
            {
                projectionContext = new ProjectionContext(BotName: settings.SchemaName.Value);
            }
            else if (componentCollection != null)
            {
                projectionContext = new ProjectionContext(BotName: componentCollection.SchemaName.Value);
            } else
            {
                projectionContext = new ProjectionContext();
            }

            bool hasAgentFile = componentCollection != null;
            string? iconBase64 = null;

            foreach (var document in documents.Values)  
            {
                var mcsDocument = document.As<McsLspDocument>();

                if (mcsDocument.IsIcon)
                {
                    iconBase64 = mcsDocument.Text;
                    continue;
                }

                if (mcsDocument.FileModel is EnvironmentVariableDefinition environmentVariableDefinition)
                {
                    environmentVariables.Add(environmentVariableDefinition);
                    continue;
                }

                var (component, error) = _fileParser.CompileFile(mcsDocument, projectionContext);

                if (component != null)
                {
                    components.Add(component);
                }

                if (error != null)
                {
                    AddErrorForDocument(errors, document, error);
                }

                if (mcsDocument.FileModel is GptComponentMetadata)
                {
                    hasAgentFile = true;
                }
            }

            if (!hasAgentFile && documents.Any())
            {
                AddErrorForDocument(errors, documents.First().Value, new AgentFileMissingException(_clientInfo));
            }

            bool validateAcrossComponents = false;
            DefinitionBase result;
            if (componentCollection != null)
            {
                validateAcrossComponents = true;
                var baseCollectionDefinition = baseDefinition as BotComponentCollectionDefinition ?? new BotComponentCollectionDefinition();
                // merge collection.mcs.yml content with the additional properties in the snapshot while preserving syntax
                var collection = baseCollectionDefinition.ComponentCollection?.ApplyYamlFileProperties(componentCollection) ?? componentCollection;
                result = baseCollectionDefinition.WithComponentCollection(collection);
            }
            else
            {
                validateAcrossComponents = settings != null || hasAgentFile;
                var baseAgentDefinition = baseDefinition as BotDefinition ?? new BotDefinition();
                // merge settings.mcs.yml content with the additional properties in the snapshot while preserving syntax
                var entity = baseAgentDefinition.Entity?.ApplySettingsYamlProperties(settings) ?? settings;

                if (iconBase64 != null && entity != null)
                {
                    var entityWithIcon = entity.WithIconBase64(iconBase64);
                    entity = BotElementRewriterWithSyntaxDataPreservation.WithOriginalSyntaxData(entity, entityWithIcon) as BotEntity;
                }

                result = baseAgentDefinition.WithEntity(entity);

                if (references != null)
                {
                    var componentCollections = new List<BotComponentCollection>();

                    foreach (var item in references.ComponentCollections)
                    {
                        try
                        {
                            var cc = _referenceResolver.ResolveComponentCollectionOrThrow(workspacePath, item);
                            {
                                if (cc.ComponentCollection != null)
                                {
                                    componentCollections.Add(cc.ComponentCollection);

                                    // CCs are compile-time concepts. we must copy the components out.
                                    components.AddRange(cc.Components);
                                }
                            }
                        }
                        catch (McsException ex)
                        {
                            // Missing reference. This could happen if we didn't sync it.
                            // Or it the user just edited the file directly.
                            if (referencesDoc != null)
                            {
                                AddErrorForDocument(errors, referencesDoc, ex);
                            }
                        }
                    }

                    baseAgentDefinition.WithComponentCollections(componentCollections);
                }                
            }

            var workspaceEnvironmentVariables = result.EnvironmentVariables
                                                    .Concat(environmentVariables)
                                                    .Where(ev => ev.SchemaName.Value != null)
                                                    .GroupBy(ev => ev.SchemaName.Value, StringComparer.OrdinalIgnoreCase)
                                                    .Select(g => g.Last());

            result = result.WithEnvironmentVariables(workspaceEnvironmentVariables);

            var additionalComponentsNotInWorkspace = result.Components.Where(IsReusableOrNonCustomizableComponent);
            var workspaceComponents = additionalComponentsNotInWorkspace.Concat(components);
            result = result.WithComponents(workspaceComponents);
            
            // Merge connection references from connectionreferences.mcs.yml file
            if (connectionReferencesFile != null && connectionReferencesFile.ConnectionReferences.Any())
            {
                var existingRefs = result.ConnectionReferences.ToList();
                var fileRefs = connectionReferencesFile.ConnectionReferences;
                
                // Merge file references with existing ones, preferring file content
                var mergedRefs = fileRefs.Concat(existingRefs.Where(er => 
                    !fileRefs.Any(fr => fr.ConnectionReferenceLogicalName.Equals(er.ConnectionReferenceLogicalName))
                ));
                
                result = result.WithConnectionReferences(mergedRefs);
            }
            
            return (result, validateAcrossComponents);

            T? FirstOrDefaultDocumentOfType<T>() where T : BotElement
            {
                return documents.Values.Select(document => document.As<McsLspDocument>().FileModel).OfType<T>().FirstOrDefault();
            }

            (McsLspDocument?, T?) FirstOrDefaultLspDocumentOfType<T>() where T : BotElement
            {
                foreach(var document in documents.Values.OfType<McsLspDocument>())
                {
                    if (document.FileModel is T fileModel)
                    {
                        return (document, fileModel);
                    }
                }
                return (null, null);
            }
        }

        private static void AddErrorForDocument(Dictionary<LspDocument, IEnumerable<Exception>> errors, LspDocument document, Exception error)
        {
            if (!errors.ContainsKey(document))
            {
                errors[document] = new List<Exception>();
            }
            ((List<Exception>)errors[document]).Add(error);
        }

        private static bool IsReusableOrNonCustomizableComponent(BotComponentBase component)
        {
            bool isReusable = component.ShareContext?.ReusePolicy is BotComponentReusePolicyWrapper reusePolicy
                && !reusePolicy.IsUnknown() && reusePolicy.Value is BotComponentReusePolicy.Private or BotComponentReusePolicy.Public;
            if (isReusable)
            {
                return true;
            }

            if (component.ManagedProperties is ManagedProperties mp && mp.IsManaged && !mp.IsCustomizable)
            {
                return true;
            }

            return false;
        }
    }
}
