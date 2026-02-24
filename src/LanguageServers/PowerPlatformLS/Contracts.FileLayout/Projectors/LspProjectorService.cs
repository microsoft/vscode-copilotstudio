namespace Microsoft.PowerPlatformLS.Contracts.FileLayout
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.FileProjection;
    using System;

    /// <summary>
    /// Language Server projection service that delegates to <see cref="LspProjection"/> for all projection rules.
    /// </summary>
    /// <remarks>
    /// <para>This service provides runtime access to projection logic. All projection rules, including
    /// element normalization, type resolution, and schema/path derivation, are defined in
    /// <see cref="LspProjection"/>. This class wraps ObjectModel projectors and coordinates
    /// registry lookups.</para>
    /// <para><b>To modify projection behavior, edit <see cref="LspProjection"/> only.</b></para>
    /// </remarks>
    public sealed class LspProjectorService
    {
        public static readonly LspProjectorService Instance = new();

        private readonly IProjectorRegistry _registry = DefaultProjectorRegistry.Instance;

        /// <summary>
        /// Gets schema name for a file, applying legacy special cases via <see cref="LspProjection"/>.
        /// </summary>
        public string? GetSchemaName(string pathWithoutExtension, string? botName, Type elementType)
        {
            // Delegate to declarative LspProjection
            var result = GetSchemaNameResult(pathWithoutExtension, botName, elementType);
            if (result.SchemaName != null)
            {
                return result.SchemaName;
            }

            // Fallback: use projector's default behavior for unknown types
            var projector = GetProjector(elementType, pathWithoutExtension);
            return projector?.GetSchemaName(pathWithoutExtension, botName, elementType);
        }

        /// <summary>
        /// Gets schema name for a file along with preserve-qualified-schema metadata.
        /// </summary>
        internal LspProjection.SchemaNameResult GetSchemaNameResult(string pathWithoutExtension, string? botName, Type elementType)
        {
            var result = LspProjection.GetSchemaNameResult(pathWithoutExtension, botName, elementType);
            if (result.SchemaName != null)
            {
                return result;
            }

            // Fallback: use projector's default behavior for unknown types
            var projector = GetProjector(elementType, pathWithoutExtension);
            var schemaName = projector?.GetSchemaName(pathWithoutExtension, botName, elementType);
            return new LspProjection.SchemaNameResult(schemaName, PreserveQualifiedSchemaName: false);
        }

        /// <summary>
        /// Gets the component projector for an element type, with legacy routing.
        /// </summary>
        public IComponentProjector? GetProjector(Type elementType, string? path = null)
        {
            var targetType = ResolveTargetType(elementType, path);
            
            // If we resolved to a target type, use GetForType
            if (targetType != null)
            {
                return _registry.GetForType(targetType) as IComponentProjector;
            }

            // If the element type is itself a component type, use GetForType
            if (typeof(BotComponentBase).IsAssignableFrom(elementType))
            {
                return _registry.GetForType(elementType) as IComponentProjector;
            }

            // DialogBase: wrap with DialogProjectorWrapper for legacy ID/parent handling
            if (typeof(DialogBase).IsAssignableFrom(elementType))
            {
                var dialogProjector = _registry.GetForElementType(elementType, path);
                if (dialogProjector != null)
                {
                    return new DialogProjectorWrapper(dialogProjector);
                }
            }

            return _registry.GetForElementType(elementType, path);
        }

        /// <summary>
        /// Normalizes an element before passing to projector.CreateComponent().
        /// </summary>
        /// <remarks>
        /// <para>Delegates to <see cref="LspProjection.NormalizeElement"/>.</para>
        /// </remarks>
        public BotElement NormalizeElement(BotElement element) => LspProjection.NormalizeElement(element);

        /// <summary>
        /// Gets file path for a component, applying legacy special cases via <see cref="LspProjection"/>.
        /// </summary>
        public string? GetFilePath(BotComponentBase component, ProjectionContext context)
            => GetFilePath(component, context, pathWithoutExtension: null);

        /// <summary>
        /// Gets file path for a component, applying legacy special cases via <see cref="LspProjection"/>.
        /// </summary>
        public string? GetFilePath(BotComponentBase component, ProjectionContext context, string? pathWithoutExtension)
        {
            // DialogComponent: use dialog's element type for polymorphic routing
            if (component is DialogComponent dialogComponent)
            {
                var elementType = dialogComponent.Dialog?.GetType() ?? typeof(AdaptiveDialog);
                var schemaName = dialogComponent.SchemaNameString ?? string.Empty;

                // Translations: route AdaptiveDialog schema names with locale suffix to translations/
                if (typeof(AdaptiveDialog).IsAssignableFrom(elementType) && LspProjection.IsTranslationSchemaName(schemaName))
                {
                    var translationPath = LspProjection.GetFilePath(typeof(TranslationsComponent), schemaName, context.BotName, context.SubAgentFolder, pathWithoutExtension);
                    if (translationPath != null) return translationPath;
                }

                var path = LspProjection.GetFilePath(elementType, schemaName, context.BotName, context.SubAgentFolder, pathWithoutExtension);
                if (path != null) return path;
            }

            // TranslationsComponent: uses .topic. infix, translations/ folder
            if (component is TranslationsComponent)
            {
                return LspProjection.GetFilePath(typeof(TranslationsComponent), component.SchemaNameString ?? string.Empty, context.BotName, context.SubAgentFolder, pathWithoutExtension);
            }

            // GptComponent
            if (component is GptComponent)
            {
                return LspProjection.GetFilePath(typeof(GptComponent), component.SchemaNameString ?? string.Empty, context.BotName, context.SubAgentFolder, pathWithoutExtension);
            }

            // Lookup by component type
            var filePath = LspProjection.GetFilePath(component.GetType(), component.SchemaNameString ?? string.Empty, context.BotName, context.SubAgentFolder, pathWithoutExtension);
            if (filePath != null) return filePath;

            // Fallback: delegate to projector
            var projector = GetProjectorForType(component.GetType());
            return projector?.GetFilePath(component, context);
        }

        /// <summary>
        /// Gets a projector by component type.
        /// </summary>
        public IComponentProjector? GetProjectorForType(Type componentType)
        {
            return _registry.GetForType(componentType) as IComponentProjector;
        }

        /// <summary>
        /// Gets all projectors (for layout derivation).
        /// </summary>
        public System.Collections.Generic.IEnumerable<IProjector> GetAll() => _registry.GetAll();

        #region Legacy Routing

        /// <summary>
        /// Resolves the target component type for an element type.
        /// </summary>
        /// <remarks>
        /// <para>Delegates to <see cref="LspProjection.ResolveTargetComponentType"/>.</para>
        /// </remarks>
        private static Type? ResolveTargetType(Type elementType, string? path) =>
            LspProjection.ResolveTargetComponentType(elementType, path);

        #endregion

        #region Dialog Projector Wrapper

        /// <summary>
        /// Wrapper for DialogComponent projector that applies legacy ID/parent handling.
        /// </summary>
        private sealed class DialogProjectorWrapper : IComponentProjector
        {
            private readonly IComponentProjector _inner;

            public DialogProjectorWrapper(IComponentProjector inner)
            {
                _inner = inner;
            }

            public Type TargetType => _inner.TargetType;
            public Type ElementType => _inner.ElementType;
            public string Infix => _inner.Infix;
            public string Folder => _inner.Folder;
            public bool IsPolymorphic => _inner.IsPolymorphic;

            public string GetFilePath(BotComponentBase component, ProjectionContext context)
                => _inner.GetFilePath(component, context);

            public string GetSchemaName(string filePathWithoutExtension, string? botName, Type knownElementType)
                => _inner.GetSchemaName(filePathWithoutExtension, botName, knownElementType);

            public BotComponentBase CreateComponent(
                BotElement element,
                string schemaName,
                BotComponentId? parentId,
                string? displayName,
                string? description,
                ProjectionContext? context = null)
            {
                var dialog = (DialogBase)element;
                var (id, parent) = LspProjection.GetComponentIds(dialog, parentId);

                return new DialogComponent(
                    schemaName: schemaName,
                    displayName: displayName ?? string.Empty,
                    description: description ?? string.Empty,
                    id: id,
                    parentBotComponentId: parent,
                    dialog: dialog);
            }
        }

        #endregion
    }
}
