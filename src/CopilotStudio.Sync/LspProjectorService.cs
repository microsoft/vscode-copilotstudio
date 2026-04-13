// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Contracts.FileLayout/Projectors/LspProjectorService.cs

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.FileProjection;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>
/// Language Server projection service that delegates to <see cref="LspProjection"/> for all projection rules.
/// </summary>
internal sealed class LspProjectorService
{
    public static readonly LspProjectorService Instance = new();

    private readonly IProjectorRegistry _registry = DefaultProjectorRegistry.Instance;

    public string? GetSchemaName(string pathWithoutExtension, string? botName, Type elementType)
    {
        var result = GetSchemaNameResult(pathWithoutExtension, botName, elementType);
        if (result.SchemaName != null)
        {
            return result.SchemaName;
        }

        var projector = GetProjector(elementType, pathWithoutExtension);
        return projector?.GetSchemaName(pathWithoutExtension, botName, elementType);
    }

    internal LspProjection.SchemaNameResult GetSchemaNameResult(string pathWithoutExtension, string? botName, Type elementType)
    {
        var result = LspProjection.GetSchemaNameResult(pathWithoutExtension, botName, elementType);
        if (result.SchemaName != null)
        {
            return result;
        }

        var projector = GetProjector(elementType, pathWithoutExtension);
        var schemaName = projector?.GetSchemaName(pathWithoutExtension, botName, elementType);
        return new LspProjection.SchemaNameResult(schemaName, PreserveQualifiedSchemaName: false);
    }

    public IComponentProjector? GetProjector(Type elementType, string? path = null)
    {
        var targetType = ResolveTargetType(elementType, path);

        if (targetType != null)
        {
            return _registry.GetForType(targetType) as IComponentProjector;
        }

        if (typeof(BotComponentBase).IsAssignableFrom(elementType))
        {
            return _registry.GetForType(elementType) as IComponentProjector;
        }

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

    public BotElement NormalizeElement(BotElement element) => LspProjection.NormalizeElement(element);

    public string? GetFilePath(BotComponentBase component, ProjectionContext context)
        => GetFilePath(component, context, pathWithoutExtension: null);

    public string? GetFilePath(BotComponentBase component, ProjectionContext context, string? pathWithoutExtension)
    {
        if (component is DialogComponent dialogComponent)
        {
            var elementType = dialogComponent.Dialog?.GetType() ?? typeof(AdaptiveDialog);
            var schemaName = dialogComponent.SchemaNameString ?? string.Empty;

            if (typeof(AdaptiveDialog).IsAssignableFrom(elementType) && LspProjection.IsTranslationSchemaName(schemaName))
            {
                var translationPath = LspProjection.GetFilePath(typeof(TranslationsComponent), schemaName, context.BotName, context.SubAgentFolder, pathWithoutExtension);
                if (translationPath != null) return translationPath;
            }

            var path = LspProjection.GetFilePath(elementType, schemaName, context.BotName, context.SubAgentFolder, pathWithoutExtension);
            if (path != null) return path;
        }

        if (component is TranslationsComponent)
        {
            return LspProjection.GetFilePath(typeof(TranslationsComponent), component.SchemaNameString ?? string.Empty, context.BotName, context.SubAgentFolder, pathWithoutExtension);
        }

        if (component is GptComponent)
        {
            return LspProjection.GetFilePath(typeof(GptComponent), component.SchemaNameString ?? string.Empty, context.BotName, context.SubAgentFolder, pathWithoutExtension);
        }

        var filePath = LspProjection.GetFilePath(component.GetType(), component.SchemaNameString ?? string.Empty, context.BotName, context.SubAgentFolder, pathWithoutExtension);
        if (filePath != null) return filePath;

        var projector = GetProjectorForType(component.GetType());
        return projector?.GetFilePath(component, context);
    }

    public IComponentProjector? GetProjectorForType(Type componentType)
    {
        return _registry.GetForType(componentType) as IComponentProjector;
    }

    public System.Collections.Generic.IEnumerable<IProjector> GetAll() => _registry.GetAll();

    #region Legacy Routing

    private static Type? ResolveTargetType(Type elementType, string? path) =>
        LspProjection.ResolveTargetComponentType(elementType, path);

    #endregion

    #region Dialog Projector Wrapper

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
